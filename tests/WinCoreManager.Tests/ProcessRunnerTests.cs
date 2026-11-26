using System.Diagnostics;
using System.Text;
using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class ProcessRunnerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit12H08_observation_is_bounded_and_keeps_the_delegated_process(bool cancel)
    {
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var token = new CancellationTokenSource();
        var start = WinGetCancellationTests.Fixture("never");
        var policy = new ProcessObservationPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200));
        var running = ProcessRunner.RunAsync(start, line =>
        {
            if (line.StartsWith("ready ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(6), out var pid))
                ready.TrySetResult(pid);
        }, cancellationToken: token.Token, terminateOnCancellation: false, observation: policy);
        using var child = Process.GetProcessById(await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        try
        {
            if (cancel) token.Cancel();
            var result = await running.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result.Interrupted);
            Assert.False(result.HasExitCode || result.Cancelled || result.StartFailed);
            Assert.False(child.HasExited);
        }
        finally
        {
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task Captura_saída_e_código_de_saída()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/d /c echo linha-teste & exit /b 5",
        };

        var result = await ProcessRunner.RunAsync(psi);

        Assert.False(result.StartFailed);
        Assert.Equal(5, result.ExitCode);
        Assert.Contains(result.Output, l => l.Contains("linha-teste"));
    }

    [Fact]
    public void SaidaSfcPreservaCaracteresSemNulos()
    {
        var tool = MaintenanceService.Tools.First(t => t.Key == "sfc_scannow");
        var bytes = Encoding.Unicode.GetBytes("Verificação concluída");
        var text = MaintenanceService.OutputEncoding(tool).GetString(bytes);
        Assert.Equal("Verificação concluída", text);
        Assert.DoesNotContain('\0', text);
    }

    [Fact]
    public async Task Canal_de_manutencao_captura_as_duas_saidas_sem_elevacao()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var result = await ElevatedMaintenanceRunner.RunCoreAsync(
            Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c echo saida & echo erro 1>&2 & exit /b 7",
            CancellationToken.None, elevate: false);

        Assert.False(result.StartFailed, result.StartError);
        Assert.True(result.ExitCode == 7, $"Código {result.ExitCode}. {result.FullOutput}");
        Assert.Contains("saida", result.Output);
        Assert.Contains("erro", result.Output);
    }

    [Fact]
    public async Task Cancelamento_encerra_o_processo_e_marca_resultado()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -Command \"[Console]::WriteLine('pronto'); [Threading.Thread]::Sleep(30000)\"",
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await ProcessRunner.RunAsync(psi, line =>
        {
            if (line == "pronto")
            {
                cts.Cancel();
            }
        }, cancellationToken: cts.Token);

        Assert.Contains("pronto", result.Output);
        Assert.True(result.Cancelled);
        Assert.True(result.DurationMs < 10_000);
    }

    [Fact]
    public async Task Token_ja_cancelado_não_tenta_iniciar_processo()
    {
        var result = await ProcessRunner.RunAsync(new ProcessStartInfo(),
            cancellationToken: new CancellationToken(true));

        Assert.True(result.Cancelled);
        Assert.True(result.StartFailed);
    }

    [Fact]
    public async Task Saída_com_CR_e_progresso_é_dividida_em_linhas()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/d /c <nul set /p =a& <nul set /p =b& echo c",
        };

        var result = await ProcessRunner.RunAsync(psi);

        Assert.False(result.StartFailed);
        Assert.Contains(result.Output, l => l.Contains('a'));
    }

    [Fact]
    public async Task Executável_inexistente_retorna_StartFailed()
    {
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Path.GetTempPath(), "este-arquivo-nao-existe-12345.exe"),
            Arguments = string.Empty,
        };

        var result = await ProcessRunner.RunAsync(psi);

        Assert.True(result.StartFailed);
        Assert.False(string.IsNullOrWhiteSpace(result.StartError));
    }
}
