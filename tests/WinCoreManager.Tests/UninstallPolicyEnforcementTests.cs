using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

/// <summary>
/// Regressões de segurança da camada de execução: chamadas diretas ao serviço (sem passar pela
/// interface) não podem contornar a política de proteção.
/// </summary>
public class UninstallPolicyEnforcementTests : IDisposable
{
    private static readonly FakeHistoryService History = new();
    private readonly string _fakeExe;

    public UninstallPolicyEnforcementTests()
    {
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "WinCoreManagerTests"));
        _fakeExe = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", $"policy_{Guid.NewGuid():N}.exe");
        File.WriteAllText(_fakeExe, "fake");
    }

    public void Dispose()
    {
        File.Delete(_fakeExe);
    }

    private UninstallBatchRunner CreateRunner(FakeUninstallExecutor executor)
        => new(new UninstallPlanner(), executor, History);

    [Fact]
    public async Task AcaoDiretaNoServico_NaoContornaProtecaoDeComponenteInterno()
    {
        var programa = ClassifierProgramFactory.Create(
            name: "Componente Interno do Windows",
            publisher: "Microsoft Corporation",
            isSystemComponent: true);
        var executor = new FakeUninstallExecutor();

        var report = await CreateRunner(executor).RunAsync([programa]);

        Assert.Empty(executor.ExecutedPrograms);
        Assert.Equal(UninstallOutcome.NotExecutable, report.Results.Single().Outcome);
        Assert.Contains("Bloqueado", report.Results.Single().Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcaoDiretaNoServico_NaoContornaProtecaoNoRemove()
    {
        var programa = ClassifierProgramFactory.Create(
            name: "Aplicativo Protegido Pelo Fabricante",
            publisher: "Fabricante SA",
            isProtected: true);
        var executor = new FakeUninstallExecutor();

        var report = await CreateRunner(executor).RunAsync([programa]);

        Assert.Empty(executor.ExecutedPrograms);
        Assert.Equal(UninstallOutcome.NotExecutable, report.Results.Single().Outcome);
    }

    [Fact]
    public async Task DependenciaCompartilhada_NaoEDesinstaladaEmMassa()
    {
        var vc1 = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation");
        var vc2 = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2013 Redistributable (x86)",
            publisher: "Microsoft Corporation");
        var executor = new FakeUninstallExecutor();

        var report = await CreateRunner(executor).RunAsync([vc1, vc2]);

        Assert.Empty(executor.ExecutedPrograms);
        Assert.Equal(2, report.NotExecutableCount);
        Assert.All(report.Results, r => Assert.Contains("individualmente", r.Message, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DependenciaCompartilhada_IndividualEPossivelComAviso()
    {
        var vc = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation",
            uninstallString: $@"""{_fakeExe}"" /uninstall /quiet");
        var executor = new FakeUninstallExecutor();

        var runner = CreateRunner(executor);
        var unconfirmed = await runner.RunAsync([vc]);
        Assert.Empty(executor.ExecutedPrograms);
        Assert.Equal(1, unconfirmed.NotExecutableCount);

        var report = await runner.RunAsync([vc], reviewConfirmed: true);
        Assert.Single(executor.ExecutedPrograms);
        Assert.Equal(1, report.SuccessCount);
    }

    [Fact]
    public async Task ExecutorRealRecusaPlanoForaDaPoliticaAntesDeIniciarProcesso()
    {
        var program = ClassifierProgramFactory.Create(name: "Google Chrome", publisher: "Google LLC", isSystemComponent: true);
        var result = await new ProcessUninstallExecutor().ExecuteAsync(new UninstallPlan
        {
            Program = program,
            Method = UninstallMethod.Executable,
            ExecutablePath = _fakeExe,
        }, null, CancellationToken.None);

        Assert.Equal(UninstallOutcome.NotExecutable, result.Outcome);
    }

    [Fact]
    public async Task ExecutorBloqueiaElevacaoDeDesinstaladorDeMaquina()
    {
        var program = new InstalledProgram
        {
            Name = "Produto sintético",
            RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Fixture",
            Kind = ProgramKind.Executable,
        };
        var plan = new UninstallPlan { Program = program, Method = UninstallMethod.Executable, ExecutablePath = _fakeExe };
        Assert.True(ProcessUninstallExecutor.IsMachineExecutable(plan));
        var result = await new ProcessUninstallExecutor().ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Equal(UninstallOutcome.NotExecutable, result.Outcome);
    }

    [Fact]
    public void Planner_RecusaPlanoBloqueadoEMotiva()
    {
        var programa = ClassifierProgramFactory.Create(
            name: "Componente Interno do Windows",
            publisher: "Microsoft Corporation",
            isSystemComponent: true);

        var plan = new UninstallPlanner().CreatePlan(programa, silentPreferred: false);

        Assert.False(plan.IsExecutable);
        Assert.Equal(UninstallMethod.NotSupported, plan.Method);
        Assert.Contains("bloqueada", plan.NotExecutableReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Planner_AcrescentaAvisoDeRevisaoParaDependencia()
    {
        var programa = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation");

        var plan = new UninstallPlanner().CreatePlan(programa, silentPreferred: false);

        Assert.Contains(plan.Warnings, w => w.Contains("revisão", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClassificacaoAtualizadaNaoDescartaDadosDoInventario()
    {
        var programa = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation");

        var row = new UninstallerItemRow(programa);

        // Dados completos continuam disponíveis na linha, independentemente da categoria.
        Assert.Equal(programa.Name, row.Name);
        Assert.Equal(programa.Publisher, row.Publisher);
        Assert.Equal(programa.UninstallString, row.Program.UninstallString);
        Assert.Equal(programa.Kind, row.Program.Kind);
        Assert.False(string.IsNullOrWhiteSpace(row.CategoryLabel));
    }
}
