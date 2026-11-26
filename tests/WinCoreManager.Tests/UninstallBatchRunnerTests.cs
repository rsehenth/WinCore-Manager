using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

/// <summary>Executor fake: registra a ordem de execução e permite falhas/cancelamentos programados.</summary>
public sealed class FakeUninstallExecutor(params UninstallOutcome[] outcomes) : IUninstallExecutor
{
    private int _index;

    public List<string> ExecutedPrograms { get; } = new();

    public Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        ExecutedPrograms.Add(plan.Program.Name);
        var outcome = _index < outcomes.Length ? outcomes[_index] : UninstallOutcome.Success;
        _index++;
        return Task.FromResult(new UninstallExecutionResult
        {
            Program = plan.Program,
            Outcome = outcome,
            ExitCode = 0,
            Message = "fake",
        });
    }
}

public sealed class FakeHistoryService : IHistoryService
{
    public List<HistoryRecord> Records { get; } = new();

    public Task AddAsync(HistoryRecord record)
    {
        Records.Add(record);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryFilter filter) => throw new NotSupportedException();

    public Task<IReadOnlyList<HistoryRecord>> RecentAsync(int count) => throw new NotSupportedException();

    public Task<int> ClearAsync() => throw new NotSupportedException();

    public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => throw new NotSupportedException();
}

public class UninstallBatchRunnerTests : IDisposable
{
    private readonly string _fakeExe;

    public UninstallBatchRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WinCoreManagerTests"));
        _fakeExe = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", $"fake_{Guid.NewGuid():N}.exe");
        File.WriteAllText(_fakeExe, "fake");
    }

    public void Dispose()
    {
        File.Delete(_fakeExe);
    }

    private InstalledProgram CreateProgram(string name) => new()
    {
        Name = name,
        RegistryKeyName = name,
        RegistryPath = $@"HKLM\...\{name}",
        Origin = "Registro (64 bits)",
        Kind = ProgramKind.Executable,
        UninstallString = $@"{_fakeExe} /mode:{name}",
    };

    private static UninstallBatchRunner CreateRunner(FakeUninstallExecutor executor, FakeHistoryService history)
        => new(new UninstallPlanner(), executor, history);

    [Fact]
    public async Task ExecucaoEstritamenteSequencialNaOrdemDaLista()
    {
        var programs = new[] { CreateProgram("A"), CreateProgram("B"), CreateProgram("C") };
        var executor = new FakeUninstallExecutor();
        var history = new FakeHistoryService();

        var report = await CreateRunner(executor, history).RunAsync(programs);

        Assert.Equal(new[] { "A", "B", "C" }, executor.ExecutedPrograms);
        Assert.Equal(3, report.SuccessCount);
        Assert.Equal(0, report.FailureCount);
    }

    [Fact]
    public async Task FalhaEmUmProgramaNaoInterrompeOsSeguintes()
    {
        var programs = new[] { CreateProgram("A"), CreateProgram("B"), CreateProgram("C") };
        var executor = new FakeUninstallExecutor(UninstallOutcome.Success, UninstallOutcome.Failure, UninstallOutcome.Success);
        var history = new FakeHistoryService();

        var report = await CreateRunner(executor, history).RunAsync(programs);

        Assert.Equal(new[] { "A", "B", "C" }, executor.ExecutedPrograms);
        Assert.Equal(2, report.SuccessCount);
        Assert.Equal(1, report.FailureCount);
        Assert.Equal(3, report.Results.Count);
    }

    [Fact]
    public async Task CancelamentoPreservaResultadosEMarcaRestantes()
    {
        var programs = new[] { CreateProgram("A"), CreateProgram("B"), CreateProgram("C") };
        var executor = new FakeUninstallExecutor(UninstallOutcome.Success, UninstallOutcome.Cancelled);
        var history = new FakeHistoryService();

        var report = await CreateRunner(executor, history).RunAsync(programs);

        Assert.Equal(new[] { "A", "B" }, executor.ExecutedPrograms); // C não foi executado
        Assert.True(report.WasCancelledByUser);
        Assert.Equal(1, report.SuccessCount);
        Assert.Equal(2, report.CancelledCount); // B (cancelado no executor) + C (não executado)
        Assert.Equal(3, report.Results.Count);
        Assert.Contains(report.Results, r => r.Program.Name == "C" && r.Message.Contains("canceladas pelo usuário", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PlanoNaoExecutavelERegistradoEContinua()
    {
        var executavel = CreateProgram("ComDesinstalador");
        var steam = new InstalledProgram
        {
            Name = "JogoSteam",
            RegistryKeyName = "Steam App 1",
            RegistryPath = @"HKLM\...\Steam App 1",
            Origin = "Registro (64 bits)",
            Kind = ProgramKind.Steam,
            UninstallString = @"C:\Program Files (x86)\Steam\steam.exe //uninstall/1",
        };
        var executor = new FakeUninstallExecutor();
        var history = new FakeHistoryService();

        var report = await CreateRunner(executor, history).RunAsync([executavel, steam]);

        Assert.Equal(executor.ExecutedPrograms, new[] { "ComDesinstalador" });
        Assert.Equal(1, report.SuccessCount);
        Assert.Equal(1, report.NotExecutableCount);
        Assert.Equal(UninstallOutcome.NotExecutable, report.Results[1].Outcome);
        Assert.False(string.IsNullOrWhiteSpace(report.Results[1].Message));
    }

    [Fact]
    public async Task HistoricoRegistraTipoDesinstalacaoComStatusCorreto()
    {
        var programs = new[] { CreateProgram("A"), CreateProgram("B") };
        var executor = new FakeUninstallExecutor(UninstallOutcome.Success, UninstallOutcome.Failure);
        var history = new FakeHistoryService();

        await CreateRunner(executor, history).RunAsync(programs);

        Assert.Equal(2, history.Records.Count);
        Assert.All(history.Records, r => Assert.Equal(HistoryType.Uninstall, r.Type));
        Assert.Equal(HistoryStatus.Success, history.Records[0].Status);
        Assert.Equal(HistoryStatus.Failure, history.Records[1].Status);
        Assert.Equal("A", history.Records[0].Target);
    }

    [Fact]
    public async Task ExecutorSimuladoNuncaIniciaProcesso()
    {
        var program = CreateProgram("A");
        var simulated = new SimulatedUninstallExecutor();
        var history = new FakeHistoryService();
        var runner = new UninstallBatchRunner(new UninstallPlanner(), simulated, history);

        var report = await runner.RunAsync([program]);

        Assert.Equal(1, report.SuccessCount);
        Assert.Contains("simulada", report.Results[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelamentoAntesDoInicioMarcaTudoComoCancelado()
    {
        var programs = new[] { CreateProgram("A"), CreateProgram("B") };
        var executor = new FakeUninstallExecutor();
        var history = new FakeHistoryService();

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var report = await CreateRunner(executor, history).RunAsync(programs, cancellationToken: cts.Token);

        Assert.Empty(executor.ExecutedPrograms);
        Assert.Equal(2, report.CancelledCount);
    }
}
