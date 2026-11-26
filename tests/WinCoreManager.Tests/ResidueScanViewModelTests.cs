using System.ComponentModel;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

/// <summary>Análise falsa: devolve a lista configurada (nenhum acesso a disco/Registro).</summary>
public sealed class FakeResidueAnalysisService : IResidueAnalysisService
{
    public ResidueScanResult Result { get; set; } = new()
    {
        Program = new InstalledProgram { Name = "X", RegistryKeyName = "X", RegistryPath = "HKLM\\...\\X" },
    };

    public int DetectCalls { get; private set; }

    public Task<ResidueScanReport> AnalyzeAsync(InstalledProgram program, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<ResidueScanResult> DetectAsync(InstalledProgram program, IReadOnlyList<InstalledProgram> allPrograms, CancellationToken cancellationToken = default)
    {
        DetectCalls++;
        return Task.FromResult(new ResidueScanResult
        {
            Program = program,
            Items = Result.Items,
            ScanFailed = Result.ScanFailed,
            Error = Result.Error,
        });
    }
}

/// <summary>Quarentena falsa: movimentações simuladas em memória.</summary>
public sealed class FakeQuarantineService : IResidueQuarantineService
{
    public List<QuarantineEntry> Entries { get; } = [];

    public bool FailNextMove { get; set; }

    public bool FailNextExport { get; set; }

    public QuarantineEntry MoveToQuarantine(string originalPath, ResidueCategory category, string programName, CancellationToken ct, string? expectedFingerprint = null)
    {
        if (FailNextMove)
        {
            FailNextMove = false;
            throw new InvalidOperationException("falha simulada de movimentação");
        }

        var entry = new QuarantineEntry
        {
            Id = Guid.NewGuid(),
            OriginalPath = originalPath,
            QuarantinedPath = $@"C:\quarentena\{Guid.NewGuid():N}",
            Category = category,
            ProgramName = programName,
            Timestamp = DateTimeOffset.Now,
        };
        Entries.Add(entry);
        return entry;
    }

    public QuarantineEntry ExportRegistryKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, string fullKeyPath, string programName, CancellationToken ct, string? expectedFingerprint = null)
    {
        if (FailNextExport)
        {
            FailNextExport = false;
            throw new InvalidOperationException("falha simulada de exportação");
        }

        var entry = new QuarantineEntry
        {
            Id = Guid.NewGuid(),
            OriginalPath = fullKeyPath,
            QuarantinedPath = $@"C:\quarentena\{Guid.NewGuid():N}_snapshot.json",
            Category = ResidueCategory.RegistryKey,
            ProgramName = programName,
            Timestamp = DateTimeOffset.Now,
            RegistryBackupFile = $@"C:\quarentena\{Guid.NewGuid():N}.reg",
            Is64BitView = is64BitView,
            Fingerprint = expectedFingerprint,
        };
        Entries.Add(entry);
        return entry;
    }

    public IReadOnlyList<QuarantineEntry> GetEntries() => Entries;

    public QuarantineRestoreResult Restore(Guid entryId, bool overwriteConfirmed, CancellationToken ct)
    {
        var entry = Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.EntryNotFound, "não encontrada", null);
        }

        if (!overwriteConfirmed)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.ConflictDestinationExists, "destino ocupado", entry);
        }

        Entries.Remove(entry);
        return new QuarantineRestoreResult(QuarantineRestoreStatus.Restored, "recuperado", entry);
    }
}

/// <summary>Contexto imediato: callbacks executam na mesma linha (determinismo nos testes).</summary>
public sealed class ImmediateSynchronizationContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => d(state);

    public override void Send(SendOrPostCallback d, object? state) => d(state);
}

public class ResidueScanViewModelTests : IDisposable
{
    private readonly ResidueTestEnvironment _env = new("vm");
    private readonly FakeResidueRegistryReader _reader = new();
    private readonly FakeResidueRegistryWriter _writer = new();
    private readonly FakeHistoryService _history = new();
    private readonly FakeQuarantineService _quarantine = new();
    private readonly ResidueCleanupPlanner _planner;
    private readonly ResidueCleanupExecutor _executor;

    public ResidueScanViewModelTests()
    {
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        _planner = new ResidueCleanupPlanner(analysis);
        _executor = new ResidueCleanupExecutor(_quarantine, _reader, _writer, _history);
    }

    private static InstalledProgram Program(string installLocation = "") => new()
    {
        Name = "Exemplo App",
        RegistryKeyName = "ExemploApp",
        RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp",
        Is64Bit = true,
        InstallLocation = installLocation,
    };

    private static ResidueItem Item(ResidueCategory category, string location, ResidueConfidence confidence) => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        Location = location,
        Confidence = confidence,
        Justifications = ["teste"],
        SizeBytes = 1024,
    };

    private ResidueScanViewModel CreateViewModel(IResidueAnalysisService analysis)
    {
        return new ResidueScanViewModel(
            Program(), [Program()], analysis, _planner, _executor, _quarantine,
            new ImmediateSynchronizationContext());
    }

    [Fact]
    public async Task MuitosResultadosSaoListadosEFiltrados()
    {
        var items = new List<ResidueItem>();
        for (var i = 0; i < 5000; i++)
        {
            items.Add(Item(ResidueCategory.Folder, $@"C:\Program Files\Exemplo {i}", ResidueConfidence.Medium));
        }

        items.Add(Item(ResidueCategory.Folder, @"C:\Program Files\Exemplo App", ResidueConfidence.High));
        items.Add(Item(ResidueCategory.RegistryKey, @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App", ResidueConfidence.High));
        items.Add(Item(ResidueCategory.File, @"C:\ProgramData\Exemplo App.log", ResidueConfidence.Protected));

        var viewModel = CreateViewModel(new FakeResidueAnalysisService
        {
            Result = new ResidueScanResult { Program = Program(), Items = items },
        });

        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);

        Assert.Equal(5003, viewModel.Rows.Count);
        Assert.Contains("Alta: 2", viewModel.SummaryText);

        // Filtro de categoria/confiança (função interna, sem depender de CollectionView).
        viewModel.SelectedCategory = "Registro";
        var registryRow = viewModel.Rows.Single(r => r.Category == ResidueCategory.RegistryKey);
        Assert.True(viewModel.FilterRow(registryRow));
        Assert.False(viewModel.FilterRow(viewModel.Rows[0]));

        viewModel.SelectedConfidence = "Protegido";
        Assert.False(viewModel.FilterRow(registryRow));
        viewModel.SelectedCategory = "Todas as categorias";
        var protectedRow = viewModel.Rows.Single(r => r.Item.Confidence == ResidueConfidence.Protected);
        Assert.True(viewModel.FilterRow(protectedRow));

        viewModel.SelectedConfidence = "Todas as confianças";
        Assert.True(viewModel.FilterRow(protectedRow));
    }

    [Fact]
    public async Task SelecionarElegiveisNuncaMarcaAmbiguosOuProtegidos()
    {
        var items = new List<ResidueItem>
        {
            Item(ResidueCategory.Folder, @"C:\Program Files\Exemplo App", ResidueConfidence.High),
            Item(ResidueCategory.Folder, @"C:\Program Files\Exemplo", ResidueConfidence.Medium),
            Item(ResidueCategory.File, @"C:\ProgramData\Exemplo App.log", ResidueConfidence.Low),
            Item(ResidueCategory.Folder, @"C:\Windows\Exemplo App", ResidueConfidence.Protected),
        };

        var viewModel = CreateViewModel(new FakeResidueAnalysisService
        {
            Result = new ResidueScanResult { Program = Program(), Items = items },
        });
        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);

        viewModel.SelectAllEligible = true;

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.Equal(1, viewModel.Rows.Count(r => r.IsSelected));
        Assert.Equal(ResidueConfidence.High, viewModel.Rows.Single(r => r.IsSelected).Item.Confidence);
    }

    [Fact]
    public async Task SelecionarTodosERestringidoAosResiduosVisiveis()
    {
        var viewModel = CreateViewModel(new FakeResidueAnalysisService
        {
            Result = new ResidueScanResult
            {
                Program = Program(),
                Items =
                [
                    Item(ResidueCategory.Folder, @"C:\Exemplo App", ResidueConfidence.High),
                    Item(ResidueCategory.RegistryKey, @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App", ResidueConfidence.High),
                ],
            },
        });
        await viewModel.ScanCommand.ExecuteAsync(null);
        viewModel.SelectAllEligible = true;

        viewModel.SelectedCategory = viewModel.CategoryOptions[3];
        viewModel.SelectAllEligible = false;
        viewModel.SelectAllEligible = true;

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.SelectAllEligible);
        Assert.Equal(ResidueCategory.RegistryKey, Assert.Single(viewModel.Rows, row => row.IsSelected).Category);
    }

    [Fact]
    public async Task LimpezaSemConfirmacaoNaoExecuta()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);

        var viewModel = new ResidueScanViewModel(
            Program(directory), [Program(directory)],
            new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules),
            _planner, _executor, _quarantine, new ImmediateSynchronizationContext())
        {
            ConfirmHandler = _ => Task.FromResult(false),
        };

        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);
        viewModel.SelectAllEligible = true;
        Assert.Equal(1, viewModel.SelectedCount);

        viewModel.CleanupSelectedCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCleaning);

        Assert.True(Directory.Exists(directory));
        Assert.Empty(_history.Records);
        Assert.Contains(viewModel.LogLines, l => l.Contains("cancelada"));
    }

    [Fact]
    public async Task LimpezaComConfirmacaoRemoveERegistra()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);

        var viewModel = new ResidueScanViewModel(
            Program(directory), [Program(directory)],
            new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules),
            _planner, _executor, _quarantine, new ImmediateSynchronizationContext())
        {
            ConfirmHandler = _ => Task.FromResult(true),
        };

        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);
        viewModel.SelectAllEligible = true;

        viewModel.CleanupSelectedCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCleaning);

        // A quarentena falsa registra a movimentação (a movimentação real é testada em ResidueCleanupTests).
        Assert.Single(_quarantine.Entries);
        Assert.Equal(directory, _quarantine.Entries[0].OriginalPath);
        Assert.Single(_history.Records);
        Assert.Equal(HistoryType.ResidueCleanup, _history.Records[0].Type);
        Assert.DoesNotContain(viewModel.Rows, r => r.Item.Location == directory);
        Assert.Contains(viewModel.LogLines, l => l.Contains("Concluído"));
    }

    [Fact]
    public async Task RevalidacaoBloqueiaItemAlterado()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);

        var viewModel = new ResidueScanViewModel(
            Program(directory), [Program(directory)],
            new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules),
            _planner, _executor, _quarantine, new ImmediateSynchronizationContext())
        {
            ConfirmHandler = _ => Task.FromResult(true),
        };

        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);
        viewModel.SelectAllEligible = true;

        Directory.Delete(directory, recursive: true); // muda entre prévia e limpeza

        viewModel.CleanupSelectedCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCleaning);

        Assert.Empty(_quarantine.Entries);
        Assert.Contains(viewModel.LogLines, l => l.Contains("Bloqueado"));
        Assert.Contains(viewModel.LogLines, l => l.Contains("nada foi removido"));
    }

    [Fact]
    public async Task RecuperacaoComConflitoExigeConfirmacao()
    {
        var entry = _quarantine.MoveToQuarantine(@"C:\fake\item", ResidueCategory.File, "X", CancellationToken.None);
        var viewModel = CreateViewModel(new FakeResidueAnalysisService());
        await viewModel.RefreshQuarantineAsync();
        Assert.Single(viewModel.QuarantineEntries);

        viewModel.AskOverwriteHandler = _ => Task.FromResult(false);
        await viewModel.RestoreCommand.ExecuteAsync(viewModel.QuarantineEntries[0]);
        Assert.Single(_quarantine.Entries);

        viewModel.AskOverwriteHandler = _ => Task.FromResult(true);
        await viewModel.RestoreCommand.ExecuteAsync(viewModel.QuarantineEntries[0]);
        Assert.Empty(_quarantine.Entries);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condição não atingida");
    }

    public void Dispose() => _env.Dispose();
}
