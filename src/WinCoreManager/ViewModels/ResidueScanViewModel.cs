using WinCoreManager.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.ViewModels;

/// <summary>Linha da lista de resíduos: empacota ResidueItem com estado de UI.</summary>
public partial class ResidueItemRow : ObservableObject
{
    public ResidueItemRow(ResidueItem item, string? unavailableReason = null)
    {
        Item = item;
        UnavailableReason = unavailableReason;
    }

    public ResidueItem Item { get; }
    public string? UnavailableReason { get; }

    public ResidueCategory Category => Item.Category;

    public string CategoryLabel => Item.CategoryLabel;

    public string Location => Item.Location;

    public string ConfidenceLabel => Item.ConfidenceLabel;

    public Brush ConfidenceBrush => Item.Confidence switch
    {
        ResidueConfidence.High => BrushExtensions.SolidSuccess,
        ResidueConfidence.Medium => BrushExtensions.SolidWarning,
        ResidueConfidence.Low => BrushExtensions.SolidSecondary,
        _ => BrushExtensions.SolidDanger,
    };

    /// <summary>Somente itens de alta confiança são selecionáveis.</summary>
    public bool CanSelect => Item.IsEligibleForCleanup && UnavailableReason is null;

    public string Justification => string.Join(" · ", Item.Justifications.Append(UnavailableReason).Where(reason => reason is not null));

    public string SizeText => Item.Category is ResidueCategory.RegistryKey || Item.SizeBytes == 0
        ? "—"
        : Services.Hardware.HardwareReading.FormatBytes((ulong)Item.SizeBytes);

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Linha da área de quarentena com ação de recuperação.</summary>
public sealed class QuarantineEntryRow
{
    public QuarantineEntryRow(QuarantineEntry entry, bool canRestoreRegistry = true)
    {
        Entry = entry;
        CanRestore = entry.Category != ResidueCategory.RegistryKey || canRestoreRegistry;
    }

    public QuarantineEntry Entry { get; }
    public bool CanRestore { get; }
    public string? RestoreUnavailableReason => CanRestore ? null
        : LocalizationService.Source("Message.Safety.RegistryMutationUnavailable");

    public string ProgramName => Entry.ProgramName;

    public string CategoryLabel => Entry.Category switch
    {
        ResidueCategory.File => LocalizationService.Source("Message.File"),
        ResidueCategory.Folder => LocalizationService.Source("Message.Folder"),
        _ => LocalizationService.Source("Message.Registry"),
    };

    public string OriginalPath => Entry.OriginalPath;

    public string TimestampText => Entry.Timestamp.LocalDateTime.ToString("g", LocalizationService.Current.Culture);

    public string RestoreLabel => Entry.Category == ResidueCategory.RegistryKey ? LocalizationService.Source("Message.RestoreSupervised") : LocalizationService.Source("Message.Restore");
}

/// <summary>Pedido de confirmação da limpeza (resumo completo antes de qualquer exclusão).</summary>
public sealed class ResidueCleanupConfirmationRequest
{
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ResidueItem> Items { get; init; }
    public required IReadOnlyList<string> BlockedReasons { get; init; }
    public long TotalBytes { get; init; }
}

public partial class ResidueScanViewModel : ViewModelBase
{
    private readonly InstalledProgram _program;
    private readonly IReadOnlyList<InstalledProgram> _allPrograms;
    private readonly IResidueAnalysisService _analysis;
    private readonly ResidueCleanupPlanner _planner;
    private readonly ResidueCleanupExecutor _executor;
    private readonly IResidueQuarantineService _quarantine;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _cts;
    private ICollectionView? _filteredView;
    private ResidueScanResult? _lastScan;

    public ResidueScanViewModel(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> allPrograms,
        IResidueAnalysisService analysis,
        ResidueCleanupPlanner planner,
        ResidueCleanupExecutor executor,
        IResidueQuarantineService quarantine,
        SynchronizationContext? uiContext = null)
    {
        Title = LocalizationService.Source("Message.AdvancedLeftoverCleanup");
        _program = program;
        _allPrograms = allPrograms;
        _analysis = analysis;
        _planner = planner;
        _executor = executor;
        _quarantine = quarantine;
        // No app: contexto do dispatcher da UI. Em testes: contexto imediato injetado.
        _ui = uiContext ?? new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher);

        Rows.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRows));
    }

    /// <summary>Confirmação explícita antes de qualquer exclusão; null ou false cancela.</summary>
    public Func<ResidueCleanupConfirmationRequest, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>Pergunta de sobrescrita na recuperação; null ou false não recupera.</summary>
    public Func<QuarantineEntry, Task<bool>>? AskOverwriteHandler { get; set; }

    public string ProgramDisplayName => _program.Name.Length > 0 ? _program.Name : LocalizationService.Source("Message.AppEnteredManually");

    public string ProgramDetails => string.Join("  ·  ", new[]
    {
        _program.DisplayVersion,
        _program.Publisher,
        _program.InstallLocation,
    }.Where(part => part.Length > 0));

    public ObservableCollection<ResidueItemRow> Rows { get; } = [];

    public ObservableCollection<string> LogLines { get; } = [];

    public ObservableCollection<QuarantineEntryRow> QuarantineEntries { get; } = [];

    public bool HasRows => Rows.Count > 0;

    public bool HasLog => LogLines.Count > 0;

    public IReadOnlyList<string> CategoryOptions { get; } = [LocalizationService.Source("Message.AllCategories"), LocalizationService.Source("Message.Files"), LocalizationService.Source("Message.Folders"), LocalizationService.Source("Message.Registry")];

    public IReadOnlyList<string> ConfidenceOptions { get; } =
        [LocalizationService.Source("Message.AllConfidenceLevels"), LocalizationService.Source("Message.High"),
            LocalizationService.Source("Message.Medium"), LocalizationService.Source("Message.Low"), LocalizationService.Source("Message.Protected")];

    [ObservableProperty]
    private string _selectedCategory = LocalizationService.Source("Message.AllCategories");

    [ObservableProperty]
    private string _selectedConfidence = LocalizationService.Source("Message.AllConfidenceLevels");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(HasItems))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanupSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCleaning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanupSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _selectedCount;

    [ObservableProperty]
    private string _statusText = LocalizationService.Source("Message.PreparingScan");

    [ObservableProperty]
    private string _currentOperation = string.Empty;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuarantineEntries))]
    private bool _isBusy;

    /// <summary>Somente itens elegíveis são marcados (nunca ambíguos, protegidos ou de baixa confiança).</summary>
    public bool SelectAllEligible
    {
        get => SelectedCount > 0 && VisibleEligibleRows.All(row => row.IsSelected);
        set => SelectEligible(value);
    }

    public bool HasItems => Rows.Count > 0;

    public bool HasQuarantineEntries => QuarantineEntries.Count > 0;

    public string SummaryText
    {
        get
        {
            if (IsScanning && Rows.Count == 0)
            {
                return LocalizationService.Source("Message.Scanning");
            }

            var high = Rows.Count(r => r.Item.Confidence == ResidueConfidence.High);
            var medium = Rows.Count(r => r.Item.Confidence == ResidueConfidence.Medium);
            var low = Rows.Count(r => r.Item.Confidence == ResidueConfidence.Low);
            var protectedCount = Rows.Count(r => r.Item.Confidence == ResidueConfidence.Protected);
            var bytes = Rows.Where(r => r.Item.Confidence != ResidueConfidence.Protected).Sum(r => r.Item.SizeBytes);
            var sizeLabel = bytes switch
            {
                >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
                >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
                >= 1024 => $"{bytes / 1024.0:F0} KB",
                > 0 => $"{bytes} B",
                _ => "0 B",
            };
            return LocalizationService.Source("Message.High0Medium1Low2Protected3", high, medium, low, protectedCount, sizeLabel);
        }
    }

    partial void OnSelectedCategoryChanged(string value) => RefreshFilter();

    partial void OnSelectedConfidenceChanged(string value) => RefreshFilter();

    public void AttachFilter(ICollectionView view)
    {
        _filteredView = view;
        _filteredView.Filter = FilterRow;
        RefreshFilter();
    }

    private void RefreshFilter()
    {
        _filteredView?.Refresh();
        foreach (var row in Rows.Where(row => row.IsSelected && !IsVisible(row)))
        {
            row.IsSelected = false;
        }

        RecountSelected();
    }

    private bool IsVisible(ResidueItemRow row) => _filteredView?.Filter?.Invoke(row) ?? FilterRow(row);

    private IEnumerable<ResidueItemRow> VisibleEligibleRows => Rows.Where(row => row.CanSelect && IsVisible(row));

    internal bool FilterRow(object item)
    {
        if (item is not ResidueItemRow row)
        {
            return true;
        }

        var categoryMatch = SelectedCategory switch
        {
            var label when label == CategoryOptions[1] => row.Category == ResidueCategory.File,
            var label when label == CategoryOptions[2] => row.Category == ResidueCategory.Folder,
            var label when label == CategoryOptions[3] => row.Category == ResidueCategory.RegistryKey,
            _ => true,
        };

        var confidenceMatch = SelectedConfidence switch
        {
            var label when label == LocalizationService.Source("Message.High") => row.Item.Confidence == ResidueConfidence.High,
            var label when label == LocalizationService.Source("Message.Medium") => row.Item.Confidence == ResidueConfidence.Medium,
            var label when label == LocalizationService.Source("Message.Low") => row.Item.Confidence == ResidueConfidence.Low,
            var label when label == LocalizationService.Source("Message.Protected") => row.Item.Confidence == ResidueConfidence.Protected,
            _ => true,
        };

        return categoryMatch && confidenceMatch;
    }

    /// <summary>Somente itens elegíveis entram na seleção (nunca ambíguos/protegidos).</summary>
    public void SelectEligible(bool select)
    {
        foreach (var row in VisibleEligibleRows)
        {
            row.IsSelected = select;
        }

        RecountSelected();
    }

    private void RecountSelected()
    {
        SelectedCount = VisibleEligibleRows.Count(row => row.IsSelected);
        OnPropertyChanged(nameof(SelectAllEligible));
    }

    public void HookRowEvents()
    {
        // Chamado após popular as linhas: reconstrói os handlers de seleção.
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
            row.PropertyChanged += OnRowPropertyChanged;
        }
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResidueItemRow.IsSelected))
        {
            RecountSelected();
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning || IsCleaning)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        var ct = cancellation.Token;

        IsScanning = true;
        IsBusy = true;
        StatusText = LocalizationService.Source("Message.ScanningForLeftoversReadOnly");
        CurrentOperation = string.Empty;
        Rows.Clear();
        LogLines.Clear();
        SelectAllEligible = false;
        RecountSelected();

        try
        {
            var result = await _analysis.DetectAsync(_program, _allPrograms, ct).ConfigureAwait(true);
            if (ReferenceEquals(_cts, cancellation))
            {
                _cts = null;
                cancellation.Dispose();
            }

            if (result.ScanFailed)
            {
                StatusText = LocalizationService.Source("Message.ScanFailed0", result.Error);
                return;
            }

            _lastScan = result;
            foreach (var item in result.Items)
            {
                    Rows.Add(new ResidueItemRow(item, _quarantine.DescribeUnsupportedItem(item)));
            }

            HookRowEvents();
            RecountSelected();
            StatusText = result.Items.Count == 0
                ? LocalizationService.Source("Message.NoLikelyLeftoversWereFoundForThisApp")
                : LocalizationService.Source("Message.PossibleLeftoversFound0ReviewTheClassificationBefore", result.Items.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Source("Message.ScanCanceled2");
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Source("Message.ScanFailed0", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cancellation))
            {
                _cts = null;
                cancellation.Dispose();
            }

            IsScanning = false;
            IsBusy = false;
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private bool CanCleanupSelected() => !IsScanning && !IsCleaning && SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanCleanupSelected))]
    private async Task CleanupSelectedAsync()
    {
        var selected = VisibleEligibleRows.Where(row => row.IsSelected).Select(row => row.Item).ToList();
        if (selected.Count == 0 || IsScanning || IsCleaning)
        {
            return;
        }

        // Resumo completo antes de qualquer exclusão + confirmação explícita.
        var request = new ResidueCleanupConfirmationRequest
        {
            Title = LocalizationService.Source("Message.CleanUp0LeftoverItemsFrom1", selected.Count, ProgramDisplayName),
            Description = DescribeQuarantineDestination(selected),
            Items = selected,
            BlockedReasons = [],
            TotalBytes = selected.Sum(i => i.SizeBytes),
        };

        if (ConfirmHandler is null || !await ConfirmHandler(request).ConfigureAwait(true))
        {
            AppendLog(LocalizationService.Source("Message.TheUserCanceledCleanupBeforeConfirming"));
            return;
        }

        var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        var ct = cancellation.Token;

        IsCleaning = true;
        IsBusy = true;
        Progress = 0;
        CurrentOperation = LocalizationService.Source("Message.ValidatingItemsAgain");
        AppendLog(LocalizationService.Source("Message.CleanupStartedItems0", selected.Count));

        try
        {
            var plan = await _planner.BuildPlanAsync(_program, _allPrograms, selected, ct).ConfigureAwait(true);
            foreach (var blocked in plan.Blocked)
            {
                Post(() =>
                {
                    AppendLog(LocalizationService.Source("Message.Blocked01", blocked.Item.Location, blocked.Message));
                    if (FindRow(blocked.Item) is { } row)
                    {
                        row.IsSelected = false;
                    }
                });
            }

            if (plan.IsEmpty)
            {
                AppendLog(LocalizationService.Source("Message.NoItemsWereEligibleAfterValidationCleanupRemoved"));
                StatusText = LocalizationService.Source("Message.NothingToRemoveValidationBlockedAllSelectedItems");
                return;
            }

            var completed = 0;
            // Fora da thread de UI: a janela continua responsiva e o botão Cancelar funciona.
            var report = await Task.Run(() => _executor.ExecuteAsync(
                plan,
                onItemCompleted: result => Post(() =>
                {
                    completed++;
                    Progress = completed * 100.0 / plan.Approved.Count;
                    CurrentOperation = $"{result.Item.CategoryLabel}: {result.Outcome switch
                    {
                        ResidueCleanupItemOutcome.Success => "removido",
                        ResidueCleanupItemOutcome.BackupFailed => LocalizationService.Source("Message.KeptBackupFailed"),
                        ResidueCleanupItemOutcome.Failed => LocalizationService.Source("Message.Failed2"),
                        ResidueCleanupItemOutcome.SkippedInvalid => LocalizationService.Source("Message.Blocked"),
                        _ => LocalizationService.Source("Message.Canceled2"),
                    }}";
                    if (FindRow(result.Item) is { } row && result.Outcome == ResidueCleanupItemOutcome.Success)
                    {
                        Rows.Remove(row);
                    }

                    RecountSelected();
                }),
                cancellationToken: ct), CancellationToken.None).ConfigureAwait(true);

            foreach (var failed in report.Results.Where(r => r.Outcome is ResidueCleanupItemOutcome.BackupFailed or ResidueCleanupItemOutcome.Failed))
            {
                AppendLog(LocalizationService.Source("Message.Failed01", failed.Item.Location, failed.Message));
            }

            AppendLog(LocalizationService.Source("Message.Complete0Removed1Failed2BlockedOr",
                report.SuccessCount, report.FailureCount, report.SkippedCount));
            StatusText = report.IsFullSuccess
                ? LocalizationService.Source("Message.CleanupFinishedBackedUpAndRemoved0", report.SuccessCount)
                : LocalizationService.Source("Message.CleanupPartiallyCompleted0Removed1Failed2", report.SuccessCount, report.FailureCount, report.SkippedCount);
        }
        catch (OperationCanceledException)
        {
            AppendLog(LocalizationService.Source("Message.CleanupCanceledByTheUserCompletedItemsWere"));
            StatusText = LocalizationService.Source("Message.CleanupCanceledCompletedItemsWerePreserved");
        }
        catch (Exception ex)
        {
            AppendLog(LocalizationService.Source("Message.UnexpectedCleanupError0", ex.Message));
            StatusText = LocalizationService.Source("Message.CleanupFailed0", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cancellation))
            {
                _cts = null;
                cancellation.Dispose();
            }

            IsCleaning = false;
            IsBusy = false;
            CurrentOperation = string.Empty;

            // A autorização vale para uma única execução: nova limpeza exige nova seleção.
            SelectAllEligible = false;
            await RefreshQuarantineAsync().ConfigureAwait(true);
            OnPropertyChanged(nameof(SummaryText));
        }
    }

    private string DescribeQuarantineDestination(IReadOnlyList<ResidueItem> selected)
    {
        var description = LocalizationService.Source("Message.FilesAndFoldersWillBeMovedToQuarantine");
        var alternate = selected.Select(_quarantine.AlternateStorage).FirstOrDefault(path => path is not null);
        return alternate is null ? description : description + Environment.NewLine
            + LocalizationService.Source("Message.Safety.SourceVolumeQuarantine0", alternate);
    }

    // A revalidação recria os itens (novos Ids): casa por identidade de categoria + localização.
    private ResidueItemRow? FindRow(ResidueItem item) =>
        Rows.FirstOrDefault(r => r.Item.Category == item.Category
                                 && r.Item.Is64BitView == item.Is64BitView
                                 && string.Equals(r.Item.Location, item.Location, StringComparison.OrdinalIgnoreCase));

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Operação já terminou; nada a cancelar.
        }
    }

    private bool CanCancel() => IsScanning || IsCleaning;

    public async Task RefreshQuarantineAsync()
    {
        var entries = await Task.Run(() => _quarantine.GetEntries()).ConfigureAwait(true);
        QuarantineEntries.Clear();
        foreach (var entry in entries.OrderByDescending(e => e.Timestamp))
        {
            QuarantineEntries.Add(new QuarantineEntryRow(entry, _quarantine.CanRestoreRegistry));
        }

        OnPropertyChanged(nameof(HasQuarantineEntries));
        if (_quarantine.ManifestProblem is { } problem && !LogLines.Contains(problem))
        {
            AppendLog(problem);
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(QuarantineEntryRow? row)
    {
        if (row is null || !row.CanRestore || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await RestoreEntryAsync(row).ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreEntryAsync(QuarantineEntryRow row)
    {
        var firstAttempt = await Task.Run(() => _quarantine.Restore(row.Entry.Id, overwriteConfirmed: false, CancellationToken.None))
            .ConfigureAwait(true);

        // Chave existente nunca é mesclada: a comparação com o backup é manual.
        if (firstAttempt.Status == QuarantineRestoreStatus.ConflictDestinationExists
            && row.Entry.Category != ResidueCategory.RegistryKey)
        {
            if (AskOverwriteHandler is null || !await AskOverwriteHandler(row.Entry).ConfigureAwait(true))
            {
                AppendLog(LocalizationService.Source("Message.RestoreWasNotConfirmed0", row.Entry.OriginalPath));
                return;
            }

            firstAttempt = await Task.Run(() => _quarantine.Restore(row.Entry.Id, overwriteConfirmed: true, CancellationToken.None))
                .ConfigureAwait(true);
        }

        AppendLog($"{row.Entry.OriginalPath}: {firstAttempt.Message}");
        StatusText = firstAttempt.Status == QuarantineRestoreStatus.Restored
            ? firstAttempt.Message
            : LocalizationService.Source("Message.RestoreDidNotComplete0", firstAttempt.Message);
        await RefreshQuarantineAsync().ConfigureAwait(true);
    }

    private void Post(Action action) => _ui.Post(_ => action(), null);

    private void AppendLog(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > 400)
        {
            LogLines.RemoveAt(0);
        }
    }
}
