using WinCoreManager.Localization;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

public partial class UpdatesViewModel : ViewModelBase
{
    private const int MaxLogLines = 600;

    private readonly IWinGetService _winGet;
    private readonly IHistoryService _history;

    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _updateCts;
    private bool _initialized;
    private bool _batchSelecting;
    private bool _scanIncomplete;
    private ICollectionView? _filteredView;

    public UpdatesViewModel(IWinGetService winGet, IHistoryService history)
    {
        Title = LocalizationService.Source("Message.Updates");
        _winGet = winGet;
        _history = history;

        Rows.CollectionChanged += (_, _) => NotifySummaryChanged();
    }

    public ObservableCollection<UpdateItemRow> Rows { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DismissWarningCommand))]
    [NotifyPropertyChangedFor(nameof(RecoveryActionDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RefreshDisabledReason))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanSelectVisible))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowLog))]
    [NotifyPropertyChangedFor(nameof(HasUpdateControls))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateDetails))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeUpdatesCommand))]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    [NotifyCanExecuteChangedFor(nameof(ResolveInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DismissWarningCommand))]
    [NotifyPropertyChangedFor(nameof(RecoveryActionDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RefreshDisabledReason))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(BlocksPackageChanges))]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    [NotifyPropertyChangedFor(nameof(CanSelectVisible))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowLog))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateDetails))]
    private bool _isUpdating;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(AllSelected))]
    [NotifyPropertyChangedFor(nameof(VisibleSelection))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    private int _selectedCount;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = LocalizationService.Source("Message.Loading");

    [ObservableProperty]
    private string _currentOperation = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateTitle))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    private bool _scanFailed;

    /// <summary>Resumo para o dashboard.</summary>
    public int AvailableCount { get; private set; }

    public DateTimeOffset? LastScanTime { get; private set; }

    public bool IsBusy => IsScanning || IsUpdating;

    public bool HasRows => Rows.Count > 0;

    public bool ShowEmptyState => !IsScanning && !Rows.Any(FilterRow);

    public bool ShowLog => IsUpdating || LogLines.Count > 0 || ShowAttemptHistory;
    public string EmptyStateTitle => LocalizationService.Source(ScanFailed ? "Message.ScanUnavailable"
        : Rows.Count == 0 ? "Message.UpdatesList.Empty" : Rows.All(IsHidden) ? "Message.UpdatesList.AllHidden" : "Message.UpdatesList.NoMatches");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    private bool _maintenanceRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    private bool _uninstallRunning;

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
    }

    public void AttachFilter(ICollectionView view)
    {
        _filteredView = view;
        _filteredView.Filter = FilterRow;
        ApplyFilter();
    }

    public void ApplyFilter()
    {
        _filteredView?.Refresh();
        foreach (var row in Rows.Where(row => row.IsSelected && !IsVisible(row)))
        {
            row.IsSelected = false;
        }

        RecountSelected();
        NotifySummaryChanged();
    }

    private bool FilterRow(object item)
    {
        if (item is not UpdateItemRow row) return true;
        if (IsHidden(row)) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;

        var term = SearchText.Trim();
        return row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.Id.Contains(term, StringComparison.OrdinalIgnoreCase)
               || row.Source.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(UpdateListSummary));
        OnPropertyChanged(nameof(AvailableCount));
        OnPropertyChanged(nameof(LastScanTime));
    }

    private void RecountSelected()
    {
        SelectedCount = VisibleSelectableRows.Count(row => row.IsSelected);
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(VisibleSelection));
        OnPropertyChanged(nameof(CanSelectVisible));
    }

    private bool IsVisible(UpdateItemRow row) => _filteredView?.Filter?.Invoke(row) ?? FilterRow(row);

    private IEnumerable<UpdateItemRow> VisibleSelectableRows => Rows.Where(row => row.CanSelect && IsVisible(row));

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await DetectAndScanAsync().ConfigureAwait(true);
        if (HasInterruptedUpdate) await RecheckInterruptionAsync();
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        await DetectAndScanAsync(preserveSelection: true).ConfigureAwait(true);
    }

    private bool CanRefresh() => RefreshDisabledReason is null;

    public string? RefreshDisabledReason => IsBusy || IsCheckingInterruption || IsPreparingResume
        ? L("Message.PackageAction.Busy") : null;

    public bool? VisibleSelection
    {
        get => SelectedCount == 0 ? false : AllSelected ? true : null;
        set { if (CanSelectVisible) AllSelected = value == true; }
    }
    public bool CanSelectVisible => !IsBusy && VisibleSelectableRows.Any();

    public bool AllSelected
    {
        get => SelectedCount > 0 && VisibleSelectableRows.All(row => row.IsSelected);
        set
        {
            _batchSelecting = true;
            try
            {
                foreach (var row in VisibleSelectableRows)
                {
                    row.IsSelected = value;
                }
            }
            finally
            {
                _batchSelecting = false;
            }

            RecountSelected();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateSelected))]
    private async Task UpdateSelectedAsync()
    {
        RefreshInterruptionState();
        if (!CanUpdateSelected())
        {
            return;
        }

        var selected = VisibleSelectableRows.Where(row => row.IsSelected).ToArray();
        if (!ConfirmBatch(selected)) return;
        RefreshInterruptionState();
        if (!CanUpdateSelected() ||
            !VisibleSelectableRows.Where(row => row.IsSelected).SequenceEqual(selected))
            return;

        if (HasSuspendedQueue && !TryConsumeSuspendedQueue())
        {
            RefreshInterruptionState();
            StatusText = L("UpdateOperation.SuspendedQueueUnavailable");
            return;
        }
        await RunBatchAsync(selected);
    }

    private async Task RunBatchAsync(IReadOnlyList<UpdateItemRow> selected)
    {
        if (selected.Count == 0)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _updateCts = cancellation;
        var ct = cancellation.Token;

        SetSuspendedQueue([]);
        IsCancelRequested = false;
        PrepareBatchPanel(selected.Count);
        Results.Clear();
        PendingBatch = selected.Select(row => row.Package).ToArray();

        AppendLog(LocalizationService.Source("Message.UpdateStartedSelectedApps0", selected.Count));

        var successes = 0;
        var failures = 0;
        var cancelled = 0;
        var inconclusive = 0;
        var processed = 0;
        Guid? suspendedReceiptId = null;
        Guid? handoffReceiptId = null;
        try
        {
            StatusText = L("Message.Updating");
            IsUpdating = true;
            for (var index = 0; index < selected.Count; index++)
            {
                if (ct.IsCancellationRequested) break;

                var row = selected[index];
                PendingBatch = selected.Skip(index + 1).Select(item => item.Package).ToArray();
                var previousStatus = row.Status;
                row.Status = UpdateRowStatus.Running;
                using var itemCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var operation = StartOperation(row, index + 1, selected.Count);

                ProcessRunResult result;
                string? installedVersion = null;
                WinGetAttemptDecision decision;
                var historyHandled = false;
                if (ct.IsCancellationRequested)
                {
                    row.Status = previousStatus;
                    break;
                }
                if (_winGet is IControlledWinGetService controlled)
                {
                    (result, installedVersion, decision, historyHandled) =
                        await RunControlledItemAsync(controlled, row, operation, PendingBatch, itemCancellation.Token);
                    handoffReceiptId = operation.ReceiptId ?? handoffReceiptId;
                }
                else
                {
                    try { result = await _winGet.UpgradePackageAsync(row.Package, operation.Output.Write, itemCancellation.Token); }
                    catch (OperationCanceledException) when (itemCancellation.IsCancellationRequested)
                    { result = new ProcessRunResult { Cancelled = true, StartFailed = true }; }
                    catch (Exception ex) { result = new ProcessRunResult { StartFailed = true, StartError = ex.Message }; }
                    decision = WinGetAttemptDecision.Evaluate(result, null, row.AvailableVersion, verifyInventory: false);
                }

                if (itemCancellation.IsCancellationRequested && result.Cancelled && !decision.KeepReceipt &&
                    WinGetAttemptDecision.ProvesNoLaunch(result, operation.Control))
                {
                    row.Status = previousStatus;
                    break;
                }

                var outcome = MapOutcome(row, result, installedVersion, decision);
                Results.Add(new(row.Name, row.Id, decision.RestartRequired ? L("UpdateOperation.RestartPending") : row.StatusText,
                    outcome.message, PackageLog(result), row.InstalledVersion, row.AvailableVersion,
                    result.StartFailed || !result.HasExitCode ? null : result.ExitCode, DateTimeOffset.Now,
                    LogTruncated: result.Output.Count + result.Errors.Count > 120 ||
                        result.Output.Concat(result.Errors).Append(result.StartError ?? "").Any(line => line.Length > 1024)));
                processed++;
                RemainingUpdates = selected.Count - processed;
                FinishOperation(outcome.status, outcome.message);
                switch (outcome.status)
                {
                    case UpdateRowStatus.Success:
                    case UpdateRowStatus.SuccessRebootRequired:
                        successes++;
                        break;
                    case UpdateRowStatus.Cancelled:
                        cancelled++;
                        break;
                    case UpdateRowStatus.Interrupted:
                        inconclusive++;
                        break;
                    default:
                        failures++;
                        break;
                }

                if (!row.CanSelect)
                {
                    AvailableCount--;
                    row.IsSelected = false;
                    RecountSelected();
                    NotifySummaryChanged();
                }

                if (!historyHandled)
                    await RecordHistoryAsync(row, result, outcome.status, outcome.message, installedVersion).ConfigureAwait(true);

                if (!decision.CanAdvance || (decision.Status == WinGetAttemptStatus.Cancelled &&
                    !itemCancellation.IsCancellationRequested && !WingetExitCodes.ReportsWindowsCancellation(result)))
                {
                    suspendedReceiptId = operation.ReceiptId;
                    break;
                }
            }
        }
        finally
        {
            _updateCts = null;
            EndOperationMonitoring();
            CurrentOperation = string.Empty;
            var remaining = selected.Count - processed;
            PendingBatch = selected.Skip(processed).Select(row => row.Package).ToArray();
            var needsReview = OperationInterrupted || HasInterruptedUpdate;
            if ((ct.IsCancellationRequested || needsReview) && OperationStatus != L("UpdateOperation.RestartPending"))
                OperationStatus = LocalizationService.Source(needsReview ? "UpdateOperation.QueueNeedsReview" : "UpdateOperation.QueueStopped");
            if (!ct.IsCancellationRequested && HasInterruptedUpdate)
                SaveSuspendedQueue(suspendedReceiptId, selected.Skip(processed).Select(row => row.Package).ToArray());
            else if (handoffReceiptId is { } handoff)
            {
                // The batch ended in this session without suspending: drop the crash checkpoint it handed over.
                try { _interruptionStore?.ConsumeSuspendedQueue(handoff); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                { AppendLog(L("UpdateOperation.SuspendedQueueUnavailable")); }
            }
            RemainingUpdates = remaining;
            var summary = LocalizationService.Source("UpdateOperation.BatchTotals", successes, failures, cancelled, inconclusive, remaining);
            BatchSummary = summary;
            AppendLog(summary);
            StatusText = summary;
            IsUpdating = false;
            RefreshInterruptionState();
            _closeCompletion?.TrySetResult();
            _closeCompletion = null;
        }
    }

    private bool CanUpdateSelected() => UpdateSelectedDisabledReason is null;

    public string? UpdateSelectedDisabledReason
    {
        get
        {
            if (PendingPackageChangeHint is { } pending) return pending;
            if (MaintenanceRunning) return L("Message.PackageAction.Maintenance");
            if (IsBusy || IsCheckingInterruption || IsPreparingResume || UninstallRunning) return L("Message.PackageAction.Busy");
            if (ScanFailed) return L("Message.PackageAction.InventoryUnavailable");
            return SelectedCount == 0 ? L("Message.PackageAction.NoSelection") : null;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelUpdateCommand))]
    private bool _isCancelRequested;

    [RelayCommand(CanExecute = nameof(CanCancelUpdate))]
    private void CancelUpdate()
    {
        if (!CanCancelUpdate()) return;
        var cancellation = _updateCts ?? _scanCts!;
        _operation?.Control.RequestBatchStop();
        cancellation.Cancel();
        IsCancelRequested = true;
        if (_updateCts is not null)
        {
            OperationStatus = LocalizationService.Source("UpdateOperation.QueueStopping");
            ShowDownloadProgress = false;
        }
        UpdateOperationPanel();
    }

    private bool CanCancelUpdate() => !IsCancelRequested &&
        (_updateCts is { IsCancellationRequested: false } && IsUpdating || _scanCts is { IsCancellationRequested: false } && IsScanning);

    private (UpdateRowStatus status, string message) MapOutcome(UpdateItemRow row, ProcessRunResult result,
        string? installedVersion, WinGetAttemptDecision decision)
    {
        var status = decision.Status switch
        {
            WinGetAttemptStatus.Succeeded when decision.KeepReceipt => UpdateRowStatus.Interrupted,
            WinGetAttemptStatus.Succeeded => UpdateRowStatus.Success,
            WinGetAttemptStatus.Cancelled when decision.KeepReceipt && IsCancelRequested => UpdateRowStatus.Interrupted,
            WinGetAttemptStatus.Cancelled => UpdateRowStatus.Cancelled,
            WinGetAttemptStatus.Inconclusive => UpdateRowStatus.Interrupted,
            _ => UpdateRowStatus.Failed,
        };
        var details = new List<string>
        {
            decision.Status switch
            {
                WinGetAttemptStatus.Succeeded when decision.KeepReceipt => LocalizationService.Source("UpdateOperation.AttemptOnly"),
                WinGetAttemptStatus.Succeeded when installedVersion is not null => LocalizationService.Source("UpdateOperation.VerifiedVersion", installedVersion),
                WinGetAttemptStatus.Succeeded => LocalizationService.Source("Message.UpdatedSuccessfully"),
                WinGetAttemptStatus.Cancelled when decision.KeepReceipt && IsCancelRequested => LocalizationService.Source("UpdateOperation.ExternalMayContinue"),
                WinGetAttemptStatus.Cancelled => LocalizationService.Source("Message.CanceledByTheUser"),
                WinGetAttemptStatus.Inconclusive when result.Interrupted => LocalizationService.Source("UpdateOperation.ExternalMayContinue"),
                WinGetAttemptStatus.Inconclusive => LocalizationService.Source("UpdateOperation.VersionUnconfirmed", row.AvailableVersion, installedVersion ?? "\u2014"),
                _ when result.StartFailed => result.StartError ?? LocalizationService.Source("Message.CouldNotStartWinGet"),
                _ => LocalizationService.Source("Message.Failed"),
            }
        };
        if (result.StartFailed && !result.Interrupted && !decision.KeepReceipt)
            details.Add(LocalizationService.Source("UpdateOperation.StartRejected"));
        if (status != UpdateRowStatus.Success && installedVersion is not null)
            details.Add(LocalizationService.Source("UpdateOperation.InstalledVersion", row.Name, installedVersion));
        if (!result.StartFailed && result.HasExitCode && (result.ExitCode != 0 || decision.Status == WinGetAttemptStatus.Inconclusive))
            details.Add(WingetExitCodes.Describe(result.ExitCode));
        if (decision.Status == WinGetAttemptStatus.Cancelled && !WingetExitCodes.IsWindowsCancellation(result.ExitCode) &&
            WingetExitCodes.ReportsWindowsCancellation(result))
            details.Add(L("UpdateOperation.WindowsCancelled"));
        if (!result.StartFailed && result.StartError is { } diagnostic)
            details.Add(diagnostic);
        if (decision.Status == WinGetAttemptStatus.Failed && result.InstallerFailureDetail is { } installerDetail)
            details.Add(installerDetail);
        else if (decision.Status == WinGetAttemptStatus.Failed && WingetExitCodes.ReportsWrappedInstallerCode(result, 1603))
            details.Add(L("UpdateOperation.MsiFatalError"));
        if (!result.Interrupted && result.Observation.Delegation == DelegatedProcessState.KnownActive)
            details.Add(LocalizationService.Source("UpdateOperation.ExternalMayContinue"));
        if (decision.KeepReceipt)
            details.Add(LocalizationService.Source(WinGetAttemptDecision.RecoveryNote(decision.PendingReason)));

        row.Status = status;
        var message = string.Join(Environment.NewLine, details);
        AppendLog($"{row.Name} \u2014 {message}");
        return (status, message);
    }

    private async Task<bool> RecordHistoryAsync(UpdateItemRow row, ProcessRunResult result, UpdateRowStatus status, string message, string? installedVersion)
    {
        try
        {
            await _history.AddAsync(new HistoryRecord
            {
                Type = HistoryType.Update,
                Target = row.Name,
                Details = $"{row.Id} — origem: {(row.Source.Length > 0 ? row.Source : "—")}" +
                    $"{Environment.NewLine}TargetVersion: {row.AvailableVersion}",
                Status = status switch
                {
                    UpdateRowStatus.Success or UpdateRowStatus.SuccessRebootRequired => HistoryStatus.Success,
                    UpdateRowStatus.Cancelled => HistoryStatus.Cancelled,
                    UpdateRowStatus.Interrupted => HistoryStatus.Interrupted,
                    _ => HistoryStatus.Failure,
                },
                ExitCode = result.StartFailed || !result.HasExitCode ? null : result.ExitCode,
                Message = message + DiagnosticExcerpt(result, status),
                DurationMs = result.DurationMs,
                VersionFrom = row.InstalledVersion,
                VersionTo = installedVersion ?? (_winGet is IControlledWinGetService ? string.Empty : row.AvailableVersion),
            }).ConfigureAwait(true);
            return true;
        }
        catch
        {
            AppendLog(LocalizationService.Source("Message.ErrorCouldNotSaveTheResultFor0", row.Name));
            return false;
        }
    }

    private async Task<bool> DetectAndScanAsync(bool preserveSelection = false)
    {
        if (IsScanning || IsUpdating || (IsCheckingInterruption && !preserveSelection))
        {
            return false;
        }

        using var cancellation = new CancellationTokenSource();
        _scanCts = cancellation;
        var ct = cancellation.Token;
        IsCancelRequested = false;

        IsScanning = true;
        StatusText = LocalizationService.Source("Message.DetectingWinGet");
        CurrentOperation = StatusText;

        try
        {
            var availability = await _winGet.DetectAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();
            if (!availability.IsInstalled)
            {
                SetScanError(availability.Error ?? LocalizationService.Source("Message.WinGetIsUnavailable"));
                return false;
            }

            StatusText = LocalizationService.Source("Message.CheckingForAvailableUpdates");
            CurrentOperation = StatusText;

            var scan = await _winGet.ListUpdatesAsync(ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            if (scan.Failed)
            {
                SetScanError(scan.Error ?? LocalizationService.Source("Message.CouldNotCheckWinGet2"));
                AppendScanOutput(scan.RawOutput);
                return false;
            }

            var selected = preserveSelection
                ? VisibleSelectableRows.Where(row => row.IsSelected).Select(row => (row.Source, row.Id)).ToHashSet()
                : null;
            ScanFailed = false;
            _scanIncomplete = scan.Error is not null || scan.UnreadableRows > 0;
            ClearRows();
            foreach (var package in scan.Packages)
            {
                var row = new UpdateItemRow(package);
                if (selected?.Contains((row.Source, row.Id)) == true && row.CanSelect && IsVisible(row))
                    row.IsSelected = true;
                row.PropertyChanged += OnRowPropertyChanged;
                Rows.Add(row);
            }

            AvailableCount = scan.Packages.Count;
            LastScanTime = DateTimeOffset.Now;

            StatusText = LocalizationService.Source("Message.UpdatesList.CheckedAt", LastScanTime.Value);

            AppendScanOutput(scan.RawOutput);
            if (scan.Error is { } catalogWarning)
            {
                StatusText = L("UpdatesList.PartialScan");
                AppendLog(catalogWarning);
            }
            if (scan.UnreadableRows > 0)
            {
                StatusText += LocalizationService.Source("Message.RowsSkippedDueToIncompleteData0", scan.UnreadableRows);
            }

            AppendLog(LocalizationService.Source("Message.UpdatesList.ScanDetected", AvailableCount));
            return true;
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Source("Message.ScanCanceled");
            return false;
        }
        catch (Exception ex)
        {
            SetScanError(ex.Message);
            return false;
        }
        finally
        {
            _scanCts = null;
            RecountSelected();
            NotifySummaryChanged();
            IsScanning = false;
            CurrentOperation = string.Empty;
        }
    }

    private void SetScanError(string message)
    {
        ClearRows();
        LastScanTime = DateTimeOffset.Now;
        ScanFailed = true;
        _scanIncomplete = true;
        StatusText = message;
        AppendLog(LocalizationService.Source("Message.Error") + message);
    }

    private void ClearRows()
    {
        foreach (var row in Rows)
        {
            row.PropertyChanged -= OnRowPropertyChanged;
        }

        AvailableCount = 0;
        Rows.Clear();
    }

    private void AppendScanOutput(string output)
    {
        foreach (var line in output.Split(Environment.NewLine).Where(line => !IsProgressNoise(line)).TakeLast(MaxLogLines))
            AppendLogOnUi(line);
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_batchSelecting && e.PropertyName == nameof(UpdateItemRow.IsSelected))
        {
            RecountSelected();
        }
    }

    private void AppendLog(string line) => AppendLogOnUi(line);

    private void AppendLogOnUi(string line)
    {
        var first = LogLines.Count == 0;
        if (LogLines.Count == MaxLogLines) LogLines.RemoveAt(0);
        LogLines.Add(line);
        if (first)
        {
            OnPropertyChanged(nameof(ShowLog));
            OnPropertyChanged(nameof(ShowUpdateDetails));
        }
    }

    /// <summary>Filtra barras de progresso do winget ("  [████ 34%]"), mantendo o log legível.</summary>
    private static bool IsProgressNoise(string line)
    {
        if (line.Trim().Length == 0)
        {
            return true;
        }

        var compact = line.Replace(" ", string.Empty);
        return compact.Any(c => c is '█' or '░' or '▒') &&
               compact.All(c => char.IsAsciiDigit(c) || c is '█' or '░' or '▒' or '[' or ']' or '%' or '.' or '=' or '-');
    }
}
