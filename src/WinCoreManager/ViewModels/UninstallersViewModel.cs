using WinCoreManager.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.ViewModels;

public partial class UninstallersViewModel : ViewModelBase
{
    private const int MaxLogLines = 600;

    private readonly IProgramInventoryService _inventory;
    private readonly IUninstallPlanner _planner;
    private readonly UninstallBatchRunner _batch;
    private readonly IResidueAnalysisService _residues;
    private readonly IWindowsTools? _windowsTools;
    private readonly SynchronizationContext _ui;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _uninstallCts;
    private bool _initialized;
    private bool _suspendCatalogRebuild;

    public Func<UninstallConfirmationRequest, Task<bool>>? ConfirmHandler { get; set; }

    /// <summary>Abre a janela de limpeza avançada de resíduos (definido pela página, com o dono correto).</summary>
    public Func<InstalledProgram, Task>? ShowResidueScanHandler { get; set; }

    public UninstallersViewModel(
        IProgramInventoryService inventory,
        IUninstallPlanner planner,
        UninstallBatchRunner batch,
        IResidueAnalysisService residues,
        IWindowsTools? windowsTools = null)
    {
        Title = LocalizationService.Source("Message.Uninstaller");
        _inventory = inventory;
        _planner = planner;
        _batch = batch;
        _residues = residues;
        _windowsTools = windowsTools;
        _ui = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);

        Rows.CollectionChanged += (_, _) => NotifySummaryChanged();
        Catalog.CollectionChanged += (_, _) => NotifySummaryChanged();
        LogLines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasLog));
    }

    public bool HasLog => LogLines.Count > 0;

    /// <summary>Inventário técnico completo (todas as entradas do Registro). Nunca é exibido
    /// diretamente: serve às proteções de desinstalação e à contagem técnica.</summary>
    public ObservableCollection<UninstallerItemRow> Rows { get; } = new();

    /// <summary>Catálogo de aplicativos exibido na lista: subconjunto de <see cref="Rows"/>
    /// filtrado pela categoria escolhida e pela busca. É a única fonte da grade.</summary>
    public ObservableCollection<UninstallerItemRow> Catalog { get; } = new();

    internal IReadOnlyList<SortDescription> CatalogSort { get; set; } =
        [new(nameof(UninstallerItemRow.InstallDate), ListSortDirection.Descending)];

    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUninstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanOpenManualResidue))]
    [NotifyCanExecuteChangedFor(nameof(OpenResidueScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeResiduesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWindowsInstalledAppsCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    [NotifyPropertyChangedFor(nameof(UninstallSelectedDisabledReason))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUninstallCommand))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanOpenManualResidue))]
    [NotifyCanExecuteChangedFor(nameof(OpenResidueScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeResiduesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWindowsInstalledAppsCommand))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(CancelButtonText))]
    [NotifyPropertyChangedFor(nameof(UninstallSelectedDisabledReason))]
    private bool _isUninstalling;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Índice do filtro de categoria: 0 Aplicativos, 1 Segundo plano, 2 Ferramentas de
    /// desenvolvimento, 3 Todos os aplicativos, 4 Componentes técnicos (avançado).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private int _selectedFilterIndex;

    partial void OnSelectedFilterIndexChanged(int value)
    {
        RebuildCatalog();
    }

    [ObservableProperty]
    private string _statusText = LocalizationService.Source("Message.Loading");

    [ObservableProperty]
    private string _currentOperation = string.Empty;

    [ObservableProperty]
    private double _uninstallProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(UninstallSelectedDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    private bool _scanFailed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(CanOpenManualResidue))]
    [NotifyPropertyChangedFor(nameof(UninstallSelectedDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(OpenResidueScanCommand))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeResiduesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWindowsInstalledAppsCommand))]
    private bool _applicationBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UninstallSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenWindowsInstalledAppsCommand))]
    [NotifyPropertyChangedFor(nameof(UninstallSelectedDisabledReason))]
    private bool _maintenanceRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeResiduesCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenResidueScanCommand))]
    [NotifyPropertyChangedFor(nameof(HasSelectedRow))]
    [NotifyPropertyChangedFor(nameof(UninstallSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(ShowWindowsUninstall))]
    [NotifyCanExecuteChangedFor(nameof(OpenWindowsInstalledAppsCommand))]
    private UninstallerItemRow? _selectedRow;

    public bool HasSelectedRow => SelectedRow is not null;
    public bool ShowWindowsUninstall => SelectedRow?.NeedsWindowsUninstall == true;

    private bool CanOpenWindowsInstalledApps() => ShowWindowsUninstall && !IsBusy && !ApplicationBusy && !MaintenanceRunning
        && PackageChangesBlocked?.Invoke() != true && PendingRecoveryNotice is null && PackageActivityReason?.Invoke() is null
        && _windowsTools?.IsAvailable(WindowsTool.InstalledApps) == true;

    [RelayCommand(CanExecute = nameof(CanOpenWindowsInstalledApps))]
    private void OpenWindowsInstalledApps()
    {
        if (CanOpenWindowsInstalledApps())
            StatusText = _windowsTools!.Open(WindowsTool.InstalledApps).Message;
    }

    [ObservableProperty]
    private string _residueReport = string.Empty;

    public bool IsRunning => IsScanning || IsUninstalling;

    public bool IsBusy => IsRunning;
    public bool CanOpenManualResidue => !IsBusy && !ApplicationBusy;

    public bool HasRows => Catalog.Count > 0;

    public bool ShowEmptyState => !IsScanning && Catalog.Count == 0;

    public string EmptyStateTitle => ScanFailed ? LocalizationService.Source("Message.ScanUnavailable") : LocalizationService.Source("Message.InstalledPrograms");

    public string CancelButtonText => IsUninstalling ? LocalizationService.Source("Message.StopAfterCurrent") : LocalizationService.Source("Message.CancelScan");

    partial void OnSearchTextChanged(string value)
    {
        RebuildCatalog();
    }

    /// <summary>Reconstrói o catálogo exibido a partir do inventário. Chamado após cada
    /// varredura e a cada mudança de filtro ou busca — a grade nunca lê o inventário bruto.</summary>
    private void RebuildCatalog()
    {
        if (_suspendCatalogRebuild)
        {
            return;
        }

        var visible = Rows.Where(FilterRow).ToList();
        var selectedRow = SelectedRow;
        foreach (var row in Rows.Where(row => row.IsSelected).Except(visible))
        {
            row.IsSelected = false;
        }

        Catalog.Clear();
        foreach (var row in visible)
        {
            Catalog.Add(row);
        }

        // A seleção de detalhes acompanha o catálogo: nada de painel apontando para item oculto.
        SelectedRow = selectedRow is not null && Catalog.Contains(selectedRow) ? selectedRow : null;

        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(InventoryText));
        RecountSelected();

        if (!IsScanning && !ScanFailed)
        {
            StatusText = DescribeCatalog();
        }
    }

    /// <summary>Resumo do que está na tela, sempre medido no catálogo — nunca no inventário.</summary>
    private string DescribeCatalog()
    {
        return SelectedFilterIndex == 4
            ? LocalizationService.Source("Message.TechnicalComponentsInAdvancedView01", Catalog.Count, Rows.Count)
            : LocalizationService.Source("Message.AppsListed01", Catalog.Count, Rows.Count);
    }

    private bool FilterRow(UninstallerItemRow row)
    {
        if (!MatchesFilter(row.Classification.Category, row.Classification.Policy, SelectedFilterIndex))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var term = SearchText.Trim();
        return row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.Publisher.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.DisplayVersion.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Filtro de categoria (puro, testável): busca sempre respeita o filtro escolhido.
    /// Entradas protegidas (Blocked) saem de todas as listas de aplicativos e ficam apenas na
    /// visão técnica; visibilidade nunca altera a política de remoção.</summary>
    internal static bool MatchesFilter(ProgramCategory category, RemovalPolicy policy, int filterIndex)
    {
        var protegido = policy is RemovalPolicy.Blocked;
        return filterIndex switch
        {
            1 => !protegido && category is ProgramCategory.BackgroundApplication,
            2 => !protegido && category is ProgramCategory.DeveloperTool,
            // "Todos os aplicativos": tudo com cara de aplicativo, inclusive não classificados.
            3 => !protegido && category is not (ProgramCategory.SystemComponent or ProgramCategory.SharedDependency),
            4 => protegido || category is ProgramCategory.SystemComponent or ProgramCategory.SharedDependency,
            // Padrão "Aplicativos": lista principal sem itens técnicos nem protegidos.
            _ => !protegido && category is ProgramCategory.UserApplication
                    or ProgramCategory.BackgroundApplication
                    or ProgramCategory.DeveloperTool,
        };
    }

    /// <summary>Linhas exibidas no catálogo atual (o inventário completo permanece em Rows).</summary>
    internal IReadOnlyList<UninstallerItemRow> VisibleRows => Catalog;

    /// <summary>Contador de exibição: aplicativos na lista e componentes técnicos que ficaram
    /// fora dela. Mede o catálogo (o que a grade mostra), nunca o inventário bruto.</summary>
    public string SummaryText
    {
        get
        {
            var visibleApps = Catalog.Count(r => !r.IsTechnical);
            var visibleTechnical = Catalog.Count(r => r.IsTechnical);
            var totalTechnical = Rows.Count(r => r.IsTechnical);

            if (SelectedFilterIndex == 4)
            {
                var totalApps = Rows.Count(r => !r.IsTechnical);
                return LocalizationService.Source("Message.0TechnicalComponentsShown1AppsHidden", visibleTechnical, totalApps - visibleApps);
            }

            return LocalizationService.Source("Message.0Apps1TechnicalComponentsHidden", visibleApps, totalTechnical - visibleTechnical);
        }
    }

    /// <summary>Contagem técnica do inventário completo (não é a lista de aplicativos).</summary>
    public string InventoryText
    {
        get
        {
            var technical = Rows.Count(r => r.IsTechnical);
            return LocalizationService.Source("Message.0TechnicalInventoryEntries1ComponentsOrDependencies", Rows.Count, technical);
        }
    }

    public int AppCount => Rows.Count(row => !row.IsTechnical);
    public int TechnicalCount => Rows.Count(row => row.IsTechnical);

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(AppCount));
        OnPropertyChanged(nameof(TechnicalCount));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(InventoryText));
    }

    private void RecountSelected()
    {
        SelectedCount = Catalog.Count(IsMarked);
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(CanSelectAll));
        OnPropertyChanged(nameof(UninstallSelectedDisabledReason));
        UninstallSelectedCommand.NotifyCanExecuteChanged();
    }

    private static bool IsMarked(UninstallerItemRow row) => row.IsSelected && row.CanSelect;

    partial void OnSelectedRowChanged(UninstallerItemRow? value)
    {
        ResidueReport = string.Empty;
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ScanAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await ScanAsync().ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsBusy;

    public bool AllSelected
    {
        get => CanSelectAll && Catalog.Where(row => row.IsBatchSelectable).All(IsMarked);
        set
        {
            // "Selecionar todos" atinge apenas as linhas visíveis e efetivamente elegíveis;
            // itens bloqueados e itens de revisão nunca entram na seleção em lote.
            foreach (var row in Catalog)
            {
                row.IsSelected = value && row.IsBatchSelectable;
            }

            RecountSelected();
        }
    }

    public bool CanSelectAll => Catalog.Any(row => row.IsBatchSelectable);

    private string? GetUnavailableReason(UninstallPlan plan) => plan.IsExecutable
        ? _batch.GetLaunchRestriction(plan)
        : plan.NotExecutableReason ?? LocalizationService.Source("Message.Unsupported");

    // Asked after each successful removal; the existing leftover window lists the items before anything moves.
    public Func<string, bool>? OfferResidueScanHandler { get; set; }

    [RelayCommand(CanExecute = nameof(CanUninstallSelected))]
    private async Task UninstallSelectedAsync()
    {
        // The package reservation is released before the leftover window opens.
        foreach (var program in await RunSelectedUninstallAsync())
            if (ShowResidueScanHandler is not null && OfferResidueScanHandler?.Invoke(program.Name) == true)
                await ShowResidueScanHandler(program).ConfigureAwait(true);
    }

    private async Task<IReadOnlyList<InstalledProgram>> RunSelectedUninstallAsync()
    {
        IReadOnlyList<InstalledProgram> removed = [];
        if (!CanUninstallSelected()) return [];

        var selected = Catalog.Where(IsMarked).ToList();
        if (selected.Count == 0) return [];

        var plans = selected.Select(row => _planner.CreatePlan(row.Program, silentPreferred: false)).ToList();
        var unavailable = await _batch.CheckConsentPlansAsync(plans).ConfigureAwait(true);
        if (unavailable.Count > 0)
        {
            foreach (var entry in unavailable)
                AppendLog($"{entry.Name}: {entry.Reason}");
            await ScanAsync();
            return [];
        }

        var warnings = plans.SelectMany(plan => plan.Warnings).Distinct(StringComparer.Ordinal).ToList();
        if (plans.Any(plan => MachineUninstallPolicy.IsMachine(plan.Program)))
            warnings.Add(LocalizationService.Source("Message.Uninstaller.UacWarning"));

        var request = new UninstallConfirmationRequest
        {
            Title = plans.Count == 1
                ? LocalizationService.Source("Message.Uninstall0", plans[0].Program.Name)
                : LocalizationService.Source("Message.Uninstall0Apps", plans.Count),
            Description = plans.Count == 1
                ? LocalizationService.Source("Message.TheAppSOfficialUninstallerWillOpenFollow")
                : LocalizationService.Source("Message.OfficialUninstallersWillRunOneAtATime"),
            CommandText = plans.Count == 1 ? plans[0].DescribeCommand() : null,
            Effects = plans.Count == 1
                ? LocalizationService.Source("Message.ThePublisherSUninstallerMayRemoveTheApp")
                : LocalizationService.Source("Message.OfficialUninstallersRunOneAtATimeApps"),
            Warnings = warnings,
            ItemCount = plans.Count,
            Programs = plans.Select(plan => plan.Program).ToList(),
        };

        if (ConfirmHandler is null || !await ConfirmHandler(request).ConfigureAwait(true))
        {
            AppendLog(LocalizationService.Source("Message.TheUserCanceledUninstallBeforeConfirming"));
            return [];
        }

        if (UninstallSelectedDisabledReason is { } disabledReason)
        {
            AppendLog(disabledReason);
            return [];
        }
        if (!selected.SequenceEqual(Catalog.Where(IsMarked)))
        {
            AppendLog(LocalizationService.Source("Message.Uninstaller.SelectionChanged"));
            return [];
        }
        foreach (var plan in plans) plan.ConsentGranted = true;
        PackageChangeReservation? reservation = null;
        if (TryReservePackageChange is not null && !TryReservePackageChange(PackageChangeKind.Uninstall,
            plans.Count == 1 ? plans[0].Program.Name : request.Title, out reservation))
        {
            NotifyPackageChangeState();
            AppendLog(UninstallSelectedDisabledReason ?? LocalizationService.Source("Message.Uninstaller.ReservationUnavailable"));
            return [];
        }
        using var packageLease = reservation;

        foreach (var row in selected)
            row.Status = UninstallRowStatus.Queued;

        var cancellation = new CancellationTokenSource();
        _uninstallCts = cancellation;
        var ct = cancellation.Token;

        IsUninstalling = true;
        UninstallProgress = 0;
        CurrentOperation = LocalizationService.Source("Message.Uninstalling0", selected[0].Name);
        AppendLog(LocalizationService.Source("Message.UninstallStartedApps0", selected.Count));

        try
        {
            var completed = 0;
            var report = await _batch.RunAsync(
                request.Programs,
                onLog: line => Post(() => AppendLog(line)),
                onItemCompleted: result => Post(() =>
                {
                    completed++;
                    var row = selected.FirstOrDefault(r => r.Program == result.Program);
                    if (row is not null)
                    {
                        row.Status = result.Outcome switch
                        {
                            UninstallOutcome.Success => UninstallRowStatus.Success,
                            UninstallOutcome.SuccessRebootRequired => UninstallRowStatus.SuccessRebootRequired,
                            UninstallOutcome.Failure => UninstallRowStatus.Failed,
                            UninstallOutcome.Cancelled => UninstallRowStatus.Cancelled,
                            UninstallOutcome.Interrupted => UninstallRowStatus.Interrupted,
                            _ => UninstallRowStatus.NotExecutable,
                        };
                    }

                    UninstallProgress = completed * 100.0 / selected.Count;
                    CurrentOperation = $"{result.Program.Name}: {result.OutcomeLabel}";
                    RecountSelected();
                }),
                cancellationToken: ct,
                reviewConfirmed: selected.Count == 1, reservation: reservation, confirmedPlans: plans).ConfigureAwait(true);

            if (report.Results.Any(result => result.Outcome == UninstallOutcome.Interrupted))
            {
                foreach (var row in selected.Where(row => row.Status == UninstallRowStatus.Queued))
                    row.Status = UninstallRowStatus.Idle;
                AppendLog(LocalizationService.Source("Message.ExternalProcessObservationEnded"));
            }
            else
            {
                if (reservation is not null) await reservation.CompleteAsync();
                AppendLog(LocalizationService.Source("Message.Complete0Succeeded1Failed2Canceled3",
                    report.SuccessCount, report.FailureCount, report.CancelledCount, report.NotExecutableCount));
                removed = report.Results.Where(result => result.Outcome is UninstallOutcome.Success
                    or UninstallOutcome.SuccessRebootRequired).Select(result => result.Program).ToArray();
            }
        }
        catch (Exception ex)
        {
            AppendLog(LocalizationService.Source("Message.UnexpectedUninstallError0", ex.Message));
            foreach (var row in selected.Where(r => r.Status is UninstallRowStatus.Queued or UninstallRowStatus.Running))
            {
                row.Status = UninstallRowStatus.Failed;
            }
        }
        finally
        {
            try
            {
                await ScanAsync(selected);
            }
            finally
            {
                IsUninstalling = false;
                if (ReferenceEquals(_uninstallCts, cancellation))
                {
                    _uninstallCts = null;
                    cancellation.Dispose();
                }
                UninstallProgress = 100;
                CurrentOperation = string.Empty;
                RecountSelected();
            }
        }
        return removed;
    }

    internal Func<bool>? PackageChangesBlocked { get; set; }
    internal Func<string?>? PendingPackageChangeHint { get; set; }
    internal Func<string?>? PackageActivityReason { get; set; }
    internal Action? NavigateToPackageRecovery { get; set; }
    internal TryReserveOperation? TryReservePackageChange { get; set; }

    public string? PendingRecoveryNotice => PendingPackageChangeHint?.Invoke();

    [RelayCommand]
    private void OpenPackageRecovery() => NavigateToPackageRecovery?.Invoke();

    internal void NotifyPackageChangeState()
    {
        OnPropertyChanged(nameof(PendingRecoveryNotice));
        OnPropertyChanged(nameof(UninstallSelectedDisabledReason));
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        OpenWindowsInstalledAppsCommand.NotifyCanExecuteChanged();
    }

    private bool CanUninstallSelected() => UninstallSelectedDisabledReason is null;

    public string? UninstallSelectedDisabledReason
    {
        get
        {
            if (PendingRecoveryNotice is { } pending)
                return pending + "\n" + LocalizationService.Source("Message.PackageAction.OpenUpdates");
            if (MaintenanceRunning) return LocalizationService.Source("Message.PackageAction.Maintenance");
            if (IsScanning) return LocalizationService.Source("Message.ScanningInstalledApps");
            if (IsUninstalling) return LocalizationService.Source("Message.Uninstaller.OperationRunning");
            if (PackageActivityReason?.Invoke() is { } activity) return activity;
            if (ApplicationBusy || PackageChangesBlocked?.Invoke() == true)
                return LocalizationService.Source("Message.PackageAction.Busy");
            if (ScanFailed) return LocalizationService.Source("Message.PackageAction.InventoryUnavailable");
            if (SelectedCount > 1 && Catalog.Any(row => IsMarked(row) && !row.IsBatchSelectable))
                return LocalizationService.Source("Message.Uninstaller.ReviewIndividually");
            if (SelectedCount > 0) return null;
            if (SelectedRow?.UnavailableReason is { } reason) return reason;
            return LocalizationService.Source(SelectedRow?.Classification.Policy == RemovalPolicy.Blocked
                ? "Message.PackageAction.Protected" : "Message.Uninstaller.NoSelection");
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelUninstall()
    {
        try
        {
            _scanCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A varredura já terminou; nada a cancelar.
        }

        try
        {
            _uninstallCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // A desinstalação já terminou; nada a cancelar.
        }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyzeResidues))]
    private async Task AnalyzeResiduesAsync()
    {
        if (SelectedRow is null)
        {
            return;
        }

        var program = SelectedRow.Program;
        ResidueReport = LocalizationService.Source("Message.ScanningForLeftoversReadOnly");
        try
        {
            var report = await _residues.AnalyzeAsync(program).ConfigureAwait(true);
            if (report.ScanFailed)
            {
                ResidueReport = LocalizationService.Source("Message.ScanFailed0", report.Error);
                return;
            }

            var lines = new List<string>
            {
                LocalizationService.Source("Message.PreviewOnlyThisStepRemovesNothing"),
            };

            if (report.LocationExists)
            {
                lines.Add(LocalizationService.Source("Message.InstallationFolderFound0FilesIn1Subfolders", report.FileCount, report.DirectoryCount, report.TotalBytesLabel));
            }
            else if (report.InstallLocation.Length > 0)
            {
                lines.Add(LocalizationService.Source("Message.TheSpecifiedInstallationFolderNoLongerExists"));
            }
            else
            {
                lines.Add(LocalizationService.Source("Message.ThisEntryHasNoInstallationFolder"));
            }

            lines.Add(report.RegistryKeyStillPresent
                ? LocalizationService.Source("Message.TheUninstallKeyIsStillInTheRegistry")
                : LocalizationService.Source("Message.TheUninstallKeyIsMissingFromTheRegistry"));

            ResidueReport = string.Join(Environment.NewLine, lines);
        }
        catch (Exception ex)
        {
            ResidueReport = LocalizationService.Source("Message.ScanFailed0", ex.Message);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAnalyzeResidues))]
    private async Task OpenResidueScanAsync()
    {
        if (SelectedRow is null || ShowResidueScanHandler is null)
        {
            return;
        }

        // Janela modal: impede limpeza e desinstalação simultâneas.
        await ShowResidueScanHandler(SelectedRow.Program).ConfigureAwait(true);
    }

    private bool CanAnalyzeResidues() => SelectedRow is not null && CanOpenManualResidue;

    private void Post(Action action)
    {
        _ui.Post(_ => action(), null);
    }

    private void AppendLog(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > MaxLogLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    private async Task ScanAsync(IReadOnlyList<UninstallerItemRow>? completedRows = null)
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        var cancellation = new CancellationTokenSource();
        _scanCts = cancellation;
        var ct = cancellation.Token;

        IsScanning = true;
        ScanFailed = false;
        StatusText = LocalizationService.Source("Message.ScanningInstalledApps");
        CurrentOperation = string.Empty;
        var concluida = false;

        try
        {
            var programs = await _inventory.GetInstalledProgramsAsync(ct).ConfigureAwait(true);
            var rows = await Task.Run(() => programs
                .OrderByDescending(program => program.Name, StringComparer.OrdinalIgnoreCase)
                .Select(CreateInventoryRow).ToList(), ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            _suspendCatalogRebuild = true;
            try
            {
                Rows.Clear();
                foreach (var row in rows)
                {
                    var completed = completedRows?.FirstOrDefault(item => SameInventoryEntry(item.Program, row.Program));
                    if (completed is not null)
                    {
                        row.Status = completed.Status;
                        if (row.Status is UninstallRowStatus.Success or UninstallRowStatus.SuccessRebootRequired)
                            AppendLog(LocalizationService.Source("Message.Uninstaller.StillListed", row.Name));
                    }
                    row.PropertyChanged += (_, args) =>
                    {
                        if (args.PropertyName is nameof(UninstallerItemRow.IsSelected) or nameof(UninstallerItemRow.CanSelect))
                        {
                            RecountSelected();
                        }
                    };
                    Rows.Add(row);
                }
            }
            finally
            {
                _suspendCatalogRebuild = false;
            }

            RebuildCatalog();
            concluida = true;
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Source("Message.ScanCanceled");
        }
        catch (Exception ex)
        {
            ScanFailed = true;
            StatusText = LocalizationService.Source("Message.CouldNotScanInstalledApps0", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cancellation))
            {
                _scanCts = null;
                cancellation.Dispose();
            }

            IsScanning = false;
            RecountSelected();

            // O cabeçalho só pode ser medido depois que a varredura conclui; cancelamento e
            // falha mantêm a mensagem própria.
            if (concluida)
            {
                StatusText = DescribeCatalog();
            }
        }
    }

    private UninstallerItemRow CreateInventoryRow(InstalledProgram program)
    {
        var row = new UninstallerItemRow(program);
        if (row.Classification.Policy != RemovalPolicy.Blocked)
        {
            var plan = _planner.CreatePlan(program, silentPreferred: false);
            row.UnavailableReason = GetUnavailableReason(plan);
            row.NeedsWindowsUninstall = row.UnavailableReason is not null && plan.IsExecutable
                && ProcessUninstallExecutor.IsMachineExecutable(plan);
        }
        return row;
    }

    private static bool SameInventoryEntry(InstalledProgram first, InstalledProgram second) =>
        ReferenceEquals(first, second) ||
        (first.RegistryPath.Length > 0 && first.Is64Bit == second.Is64Bit &&
         string.Equals(first.RegistryPath, second.RegistryPath, StringComparison.OrdinalIgnoreCase) &&
         string.Equals(first.DisplayVersion, second.DisplayVersion, StringComparison.OrdinalIgnoreCase));
}
