using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

public class ResidueCleanupTests : IDisposable
{
    private readonly ResidueTestEnvironment _env = new("clean");
    private readonly FakeResidueRegistryReader _reader = new();
    private readonly FakeResidueRegistryWriter _writer = new();
    private readonly string _quarantineRoot;
    private readonly ResidueQuarantineService _quarantine;
    private readonly ResidueAnalysisService _analysis;
    private readonly ResidueCleanupPlanner _planner;
    private readonly ResidueCleanupExecutor _executor;
    private readonly FakeHistoryService _history = new();
    private readonly FakeRegistryView _uninstallView = new();

    private const string UninstallKeyPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp";

    public ResidueCleanupTests()
    {
        _quarantineRoot = Path.Combine(_env.Root, "quarantine");
        _quarantine = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules);
        _analysis = new ResidueAnalysisService(_uninstallView, _reader, _env.Rules);
        _planner = new ResidueCleanupPlanner(_analysis);
        _executor = new ResidueCleanupExecutor(_quarantine, _reader, _writer, _history);
    }

    private static InstalledProgram Program(string name = "Exemplo App", string? installLocation = null) => new()
    {
        Name = name,
        RegistryKeyName = "ExemploApp",
        RegistryPath = UninstallKeyPath,
        Is64Bit = true,
        InstallLocation = installLocation ?? string.Empty,
    };

    // Item aprovado diretamente, com a identidade real: exercita o executor sem depender do nome.
    private static ResidueItem ApprovedItem(string path, ResidueCategory category) => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        Location = path,
        Confidence = ResidueConfidence.High,
        Fingerprint = ResidueFileGuard.Inspect(path, 1000, CancellationToken.None)!.Fingerprint,
    };

    private static ResidueCleanupPlan DirectPlan(params ResidueItem[] items) => new()
    {
        OperationId = Guid.NewGuid(),
        Program = Program(),
        Approved = items,
    };

    [Fact]
    public async Task PlanoAprovaItemAltoQueContinuaExistindo()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(installLocation: directory);

        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var selected = scan.Items.Where(i => i.IsEligibleForCleanup).ToList();
        Assert.NotEmpty(selected);

        var plan = await _planner.BuildPlanAsync(program, [], selected, CancellationToken.None);

        Assert.Empty(plan.Blocked);
        Assert.Equal(selected.Count, plan.Approved.Count);
    }

    [Fact]
    public async Task PlanoBloqueiaItemQueDesapareceuDesdeAPrevia()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(installLocation: directory);
        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var selected = scan.Items.Where(i => i.IsEligibleForCleanup).ToList();
        Assert.NotEmpty(selected);
        Directory.Delete(directory, recursive: true);

        var plan = await _planner.BuildPlanAsync(program, [], selected, CancellationToken.None);

        Assert.Empty(plan.Approved);
        Assert.All(plan.Blocked, b => Assert.Equal(ResidueCleanupItemOutcome.SkippedInvalid, b.Outcome));
    }

    [Fact]
    public async Task PlanoBloqueiaItemRebaixadoDesdeAPrevia()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(installLocation: directory);
        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var selected = scan.Items.Where(i => i.IsEligibleForCleanup).ToList();
        Assert.NotEmpty(selected);

        // Entre a prévia e o plano, um executável apareceu na pasta: confiança cai para Média.
        await File.WriteAllTextAsync(Path.Combine(directory, "app.exe"), "binário");

        var plan = await _planner.BuildPlanAsync(program, [], selected, CancellationToken.None);

        Assert.Empty(plan.Approved);
        Assert.Contains(plan.Blocked, b => b.Message.Contains("Média"));
    }

    [Fact]
    public async Task PlanoRecusaItensNaoElegiveisDaPrevias()
    {
        Directory.CreateDirectory(Path.Combine(_env.ProgramFiles, "Exemplo Aplicativo"));
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App");
        var scan = await _analysis.DetectAsync(Program(), [], CancellationToken.None);
        var selected = scan.Items.Where(i => !i.IsEligibleForCleanup).ToList();
        Assert.NotEmpty(selected);

        var plan = await _planner.BuildPlanAsync(Program(), [], selected, CancellationToken.None);

        Assert.Empty(plan.Approved);
        Assert.Equal(selected.Count, plan.Blocked.Count);
    }

    [Fact]
    public async Task PlanoVazioParaSelecaoVazia()
    {
        var plan = await _planner.BuildPlanAsync(Program(), [], [], CancellationToken.None);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public async Task ExecutorMoveArquivoParaQuarentenaERegistraHistorico()
    {
        var file = Path.Combine(_env.ProgramData, "Exemplo App.log");
        await File.WriteAllTextAsync(file, "conteúdo");

        var report = await _executor.ExecuteAsync(DirectPlan(ApprovedItem(file, ResidueCategory.File)));

        Assert.Equal(1, report.SuccessCount);
        Assert.False(File.Exists(file));
        Assert.True(Directory.GetFiles(_quarantineRoot).Length > 0);
        var record = Assert.Single(_history.Records);
        Assert.Equal(HistoryType.ResidueCleanup, record.Type);
        Assert.Equal(HistoryStatus.Success, record.Status);
        Assert.Contains("backups:", record.Details);
    }

    [Fact]
    public async Task ExecutorMovePastaParaQuarentena()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(installLocation: directory);

        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var plan = await _planner.BuildPlanAsync(program, [], scan.Items.Where(i => i.IsEligibleForCleanup).ToList(), CancellationToken.None);
        var report = await _executor.ExecuteAsync(plan);

        Assert.Equal(1, report.SuccessCount);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task ExecutorRecusaDiretorioProtegidoMesmoComPlanoEDigitalValidos()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_env.WindowsDir, "fixture-protegida")).FullName;
        var file = Path.Combine(directory, "keep.txt");
        await File.WriteAllTextAsync(file, "dados descartaveis");
        var item = ApprovedItem(directory, ResidueCategory.Folder);

        var report = await _executor.ExecuteAsync(DirectPlan(item));

        Assert.Equal(0, report.SuccessCount);
        Assert.Equal("dados descartaveis", await File.ReadAllTextAsync(file));
        Assert.Empty(_quarantine.GetEntries());
    }

    [Fact]
    public async Task ExecutorExportaERemoveChave()
    {
        var installDirectory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(installDirectory);
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Path"] = installDirectory,
        });
        var program = Program(installLocation: installDirectory);

        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var selected = scan.Items
            .Where(i => i.IsEligibleForCleanup && i.Category == ResidueCategory.RegistryKey)
            .ToList();
        Assert.NotEmpty(selected);

        var plan = await _planner.BuildPlanAsync(program, [], selected, CancellationToken.None);
        var report = await _executor.ExecuteAsync(plan);

        Assert.Equal(1, report.SuccessCount);
        var result = Assert.Single(report.Results);
        Assert.NotNull(result.RegistryBackupFile);
        Assert.True(File.Exists(result.RegistryBackupFile!));
        Assert.Contains(_writer.Operations, o => o.StartsWith("DELETE CurrentUser\\SOFTWARE"));
    }

    [Fact]
    public async Task FalhaDeBackupDeChaveImpedeARemocao()
    {
        var installDirectory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(installDirectory);
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Path"] = installDirectory,
        });
        var program = Program(installLocation: installDirectory);

        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var plan = await _planner.BuildPlanAsync(program, [], scan.Items
            .Where(i => i.IsEligibleForCleanup && i.Category == ResidueCategory.RegistryKey).ToList(), CancellationToken.None);
        Assert.NotEmpty(plan.Approved);

        // Quarentena falha: caminho UNC não é suportado para movimentação/exportação.
        var brokenQuarantine = new ResidueQuarantineService(@"\\servidor\compartilhado\quarentena", _reader, _writer, _env.Rules);
        var executor = new ResidueCleanupExecutor(brokenQuarantine, _reader, _writer, _history);

        var report = await executor.ExecuteAsync(plan);

        var result = Assert.Single(report.Results);
        Assert.Equal(ResidueCleanupItemOutcome.BackupFailed, result.Outcome);
        Assert.DoesNotContain(_writer.Operations, o => o.StartsWith("DELETE"));
        Assert.Contains("NÃO foi removida", result.Message);
        var record = Assert.Single(_history.Records);
        Assert.Equal(HistoryStatus.Failure, record.Status);
    }

    [Fact]
    public async Task FalhaDeExclusaoPreservaBackupReferenciado()
    {
        var installDirectory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(installDirectory);
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Path"] = installDirectory,
        });
        var program = Program(installLocation: installDirectory);

        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var plan = await _planner.BuildPlanAsync(program, [], scan.Items
            .Where(i => i.IsEligibleForCleanup && i.Category == ResidueCategory.RegistryKey).ToList(), CancellationToken.None);

        _writer.FailNextOperations = true;
        var report = await _executor.ExecuteAsync(plan);

        var result = Assert.Single(report.Results, r => r.Outcome == ResidueCleanupItemOutcome.Failed);
        Assert.NotNull(result.RegistryBackupFile);
        Assert.True(File.Exists(result.RegistryBackupFile!));
        Assert.Equal(HistoryStatus.Failure, Assert.Single(_history.Records).Status);
    }

    [Fact]
    public async Task CancelamentoImpedeOperacoesFuturasEPreservaConcluidos()
    {
        // Três pastas com identidade exata do programa, em raízes diferentes.
        var directories = new[]
        {
            Path.Combine(_env.ProgramData, "Exemplo App"),
            Path.Combine(_env.ProgramFilesX86, "Exemplo App"),
            Path.Combine(_env.Local, "Exemplo App"),
        };
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
        }

        var selected = directories.Select(directory => ApprovedItem(directory, ResidueCategory.Folder)).ToArray();
        var plan = DirectPlan(selected);

        var completed = 0;
        using var cts = new CancellationTokenSource();
        var report = await _executor.ExecuteAsync(
            plan,
            onItemCompleted: _ =>
            {
                completed++;
                if (completed >= 2)
                {
                    cts.Cancel();
                }
            },
            cancellationToken: cts.Token);

        Assert.True(report.SuccessCount >= 2);
        Assert.True(report.WasCancelledByUser);
        Assert.Contains(report.Results, r => r.Outcome == ResidueCleanupItemOutcome.SkippedCancelled);
        Assert.Equal(selected.Length, report.Results.Count);
        // Pastas já movidas permanecem na quarentena; pendentes continuam no original.
        Assert.Equal(report.SuccessCount, _quarantine.GetEntries().Count);
    }

    [Fact]
    public async Task PlanoComItensBloqueadosExecutaSomenteAprovados()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(installLocation: directory);
        var scan = await _analysis.DetectAsync(program, [], CancellationToken.None);
        var selected = scan.Items.Where(i => i.IsEligibleForCleanup).ToList();
        Assert.NotEmpty(selected);
        Directory.Delete(directory, recursive: true); // desaparece: será bloqueado na revalidação

        var plan = await _planner.BuildPlanAsync(program, [], selected, CancellationToken.None);
        var report = await _executor.ExecuteAsync(plan);

        Assert.Equal(plan.Blocked.Count, report.Results.Count(r => r.Outcome == ResidueCleanupItemOutcome.SkippedInvalid));
        Assert.Equal(0, report.SuccessCount);
    }

    public void Dispose() => _env.Dispose();
}
