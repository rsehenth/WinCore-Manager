using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class MaintenanceServiceTests
{
    private static MaintenanceService CreateService(out HistoryService history, TestSettings? settings = null)
    {
        settings ??= new TestSettings();
        history = new HistoryService(settings);
        return new MaintenanceService(history);
    }

    [Fact]
    public async Task Segunda_execução_concorrente_é_bloqueada()
    {
        var service = CreateService(out _);
        var tool = MaintenanceService.Tools.First(t => t.Key == "dism_checkhealth");

        var firstEntered = new TaskCompletionSource();
        var calls = 0;

        service.ExecutorOverride = (_, _, _) =>
        {
            Interlocked.Increment(ref calls);
            firstEntered.TrySetResult();
            return Task.Delay(300).ContinueWith(_ => new ProcessRunResult { ExitCode = 0 });
        };

        var first = service.RunAsync(tool, null, null, CancellationToken.None);
        await firstEntered.Task;

        var second = await service.RunAsync(tool, null, null, CancellationToken.None);

        Assert.Equal(MaintenanceStatus.Failed, second.Status);
        Assert.Contains("em execução", second.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, calls); // a segunda chamada não chegou a executar

        var firstResult = await first;
        Assert.Equal(MaintenanceStatus.Success, firstResult.Status);
    }

    [Fact]
    public async Task Reparo_completo_executa_dism_antes_de_sfc_e_registra_historico()
    {
        var service = CreateService(out var history);
        var order = new List<string>();

        service.ExecutorOverride = (exe, _, _) =>
        {
            lock (order)
            {
                order.Add(Path.GetFileName(exe));
            }

            return Task.FromResult(new ProcessRunResult { ExitCode = 0 });
        };

        var outcomes = await service.RunFullRepairAsync(null, CancellationToken.None);

        Assert.Equal(2, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(MaintenanceStatus.Success, o.Status));

        lock (order)
        {
            Assert.Equal("Dism.exe", order[0]);
            Assert.Equal("sfc.exe", order[1]);
        }

        var records = await history.QueryAsync(new HistoryFilter { Type = HistoryType.Maintenance });
        Assert.Equal(2, records.Count);
        Assert.Contains(records, r => r.Target == "DISM RestoreHealth");
        Assert.Contains(records, r => r.Target == "SFC Scannow");
    }

    [Fact]
    public async Task Chkdsk_com_unidade_invalida_não_executa_e_registra_falha()
    {
        var service = CreateService(out var history);
        var executed = false;

        service.ExecutorOverride = (_, _, _) =>
        {
            executed = true;
            return Task.FromResult(new ProcessRunResult { ExitCode = 0 });
        };

        var tool = MaintenanceService.Tools.First(t => t.Key == "chkdsk_scan");
        var outcome = await service.RunAsync(tool, "ZZ", null, CancellationToken.None);

        Assert.Equal(MaintenanceStatus.Failed, outcome.Status);
        Assert.Contains("Unidade inválida", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(executed);
        var record = Assert.Single(await history.RecentAsync(1));
        Assert.Equal(HistoryStatus.Failure, record.Status);
    }

    [Fact]
    public async Task Definição_fora_do_catálogo_não_chega_ao_executor()
    {
        var service = CreateService(out _);
        var definition = new MaintenanceToolDef("sfc_verifyonly", "Teste", "teste.exe", "teste", false, "", "", "");
        service.ExecutorOverride = (_, _, _) => Task.FromResult(new ProcessRunResult { ExitCode = 0 });

        var outcome = await service.RunAsync(definition, null, null, CancellationToken.None);

        Assert.Equal(MaintenanceStatus.Failed, outcome.Status);
    }

    [Fact]
    public async Task Reparo_completo_mantém_bloqueio_entre_etapas()
    {
        var service = CreateService(out _);
        var tool = MaintenanceService.Tools.First(t => t.Key == "sfc_verifyonly");
        Task<MaintenanceOutcome>? competing = null;
        var calls = 0;
        service.ExecutorOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessRunResult { ExitCode = 0 });
        };
        service.StatusChanged += (status, name) =>
        {
            if (status == MaintenanceStatus.Success && name == "DISM RestoreHealth")
            {
                competing = service.RunAsync(tool, null, null, CancellationToken.None);
            }
        };

        await service.RunFullRepairAsync(null, CancellationToken.None);

        Assert.NotNull(competing);
        Assert.Equal(MaintenanceStatus.Failed, (await competing).Status);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cancelamento_entre_etapas_preserva_resultado_dism_e_não_inicia_sfc()
    {
        var service = CreateService(out var history);
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        service.ExecutorOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessRunResult { ExitCode = 0 });
        };
        service.StatusChanged += (status, name) =>
        {
            if (status == MaintenanceStatus.Success && name == "DISM RestoreHealth")
            {
                cancellation.Cancel();
            }
        };

        var outcomes = await service.RunFullRepairAsync(null, cancellation.Token);

        Assert.Equal(2, outcomes.Count);
        Assert.Equal(MaintenanceStatus.Success, outcomes[0].Status);
        Assert.Equal(MaintenanceStatus.Cancelled, outcomes[1].Status);
        Assert.Equal(1, calls);
        Assert.Contains(await history.RecentAsync(5), record => record.Status == HistoryStatus.Cancelled);
    }

    [Fact]
    public async Task Chkdsk_com_unidade_valida_monta_argumento_correto()
    {
        var service = CreateService(out _);
        var fixedDrives = MaintenanceService.GetFixedDriveLetters();
        var drive = fixedDrives[0];

        string? receivedArgs = null;
        service.ExecutorOverride = (_, args, _) =>
        {
            receivedArgs = args;
            return Task.FromResult(new ProcessRunResult { ExitCode = 0 });
        };

        var tool = MaintenanceService.Tools.First(t => t.Key == "chkdsk_scan");
        var outcome = await service.RunAsync(tool, drive, null, CancellationToken.None);

        Assert.Equal(MaintenanceStatus.Success, outcome.Status);
        Assert.Equal($"{drive.ToUpperInvariant()}: /scan", receivedArgs);
    }

    [Fact]
    public async Task Cancelamento_é_mapeado_como_status_cancelado()
    {
        var service = CreateService(out _);
        var tool = MaintenanceService.Tools.First(t => t.Key == "dism_scanhealth");

        service.ExecutorOverride = (_, _, _) => Task.FromResult(
            new ProcessRunResult { ExitCode = -1, Cancelled = true });

        var outcome = await service.RunAsync(tool, null, null, CancellationToken.None);

        Assert.Equal(MaintenanceStatus.Cancelled, outcome.Status);
    }

    [Fact]
    public async Task Recusa_do_UAC_registra_cancelamento_e_impede_segunda_etapa()
    {
        var service = CreateService(out var history);
        var calls = 0;
        service.ExecutorOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessRunResult
            {
                StartFailed = true,
                Cancelled = true,
                StartError = "Elevação cancelada pelo usuário.",
            });
        };

        var outcome = Assert.Single(await service.RunFullRepairAsync(null, CancellationToken.None));
        Assert.Equal(MaintenanceStatus.Cancelled, outcome.Status);
        Assert.Equal(1, calls);
        Assert.False(service.IsBusy);
        var record = Assert.Single(await history.RecentAsync(5));
        Assert.Equal(HistoryStatus.Cancelled, record.Status);
        Assert.Null(record.ExitCode);
    }

    [Fact]
    public async Task Código_de_saída_diferente_de_zero_é_falha_com_detalhe()
    {
        var service = CreateService(out _);
        var tool = MaintenanceService.Tools.First(t => t.Key == "sfc_verifyonly");

        service.ExecutorOverride = (_, _, _) => Task.FromResult(
            new ProcessRunResult { ExitCode = 3 });

        var outcome = await service.RunAsync(tool, null, null, CancellationToken.None);

        Assert.Equal(MaintenanceStatus.Failed, outcome.Status);
        Assert.Equal(3, outcome.ExitCode);
        Assert.Contains("3", outcome.Message);
    }

    [Fact]
    public async Task Falha_do_dism_impede_etapa_sfc()
    {
        var service = CreateService(out var history);
        var calls = 0;
        service.ExecutorOverride = (_, _, _) =>
        {
            calls++;
            return Task.FromResult(new ProcessRunResult { ExitCode = 87 });
        };

        var outcome = Assert.Single(await service.RunFullRepairAsync(null, CancellationToken.None));
        Assert.Equal(MaintenanceStatus.Failed, outcome.Status);
        Assert.Equal(1, calls);
        Assert.Equal(87, Assert.Single(await history.RecentAsync(5)).ExitCode);
    }

    [Fact]
    public async Task Chkdsk_codigo_um_registra_erros_corrigidos_como_sucesso()
    {
        var service = CreateService(out var history);
        var drive = MaintenanceService.GetFixedDriveLetters()[0];
        var tool = MaintenanceService.Tools.First(t => t.Key == "chkdsk_scan");
        service.ExecutorOverride = (_, _, _) => Task.FromResult(new ProcessRunResult { ExitCode = 1 });

        var outcome = await service.RunAsync(tool, drive, null, CancellationToken.None);
        Assert.Equal(MaintenanceStatus.Success, outcome.Status);
        Assert.Contains("corrigiu erros", outcome.Message);
        Assert.Equal(1, Assert.Single(await history.RecentAsync(5)).ExitCode);
    }
}
