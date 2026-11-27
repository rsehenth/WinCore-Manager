using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public partial class UpdatesViewModel
{
    private readonly ISettingsService? _settings;
    private readonly WinGetInterruptionStore? _interruptionStore;
    private UpdateOperation? _operation;
    private DispatcherTimer? _operationTimer;
    private TaskCompletionSource? _closeCompletion;
    private bool _unpersistedInterruption;
    internal Func<WinGetPackage, CancellationToken, Task<string?>>? ChooseUpdateLocation { get; set; }
    internal Func<string, string, bool>? ConfirmUnknownBattleNetUpdate { get; set; }
    internal Func<WinGetPackage, string, string?>? ChooseBattleNetUpdateLocation { get; set; }

    internal bool PackageChangesBlockedNow => BlocksPackageChanges || _interruptionStore?.HasReceipt == true;
    internal bool TryReservePackageChange(out PackageChangeReservation? reservation) =>
        TryReservePackageChange(PackageChangeKind.External, "External", out reservation);

    internal bool TryReservePackageChange(PackageChangeKind kind, string target, out PackageChangeReservation? reservation)
    {
        reservation = null;
        if (PackageChangesBlockedNow)
        {
            RefreshInterruptionState();
            return false;
        }
        if (_interruptionStore is null) return true;
        try
        {
            var receipt = _interruptionStore.BeginExternal(kind, target);
            reservation = new PackageChangeReservation(receipt, RefreshInterruptionState);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RefreshInterruptionState();
            AppendLog(LocalizationService.Source("UpdateOperation.ReceiptError", ex.Message));
        }
        return false;
    }

    internal void RefreshInterruptionState()
    {
        if (IsUpdating || IsCheckingInterruption || UninstallRunning || MaintenanceRunning) return;
        var hasReceipt = _unpersistedInterruption || _interruptionStore?.HasReceipt == true;
        if (hasReceipt && !HasInterruptedUpdate)
            SetInterruptionNote("UpdateOperation.RecoveryReview");
        if (!hasReceipt && HasInterruptedUpdate)
        {
            _presentedReview = null;
            ObservedVersionForReview = null;
            InterruptionTarget = InterruptionState = InterruptionDetails = string.Empty;
        }
        HasInterruptedUpdate = hasReceipt;
    }

    internal UpdatesViewModel(IWinGetService winGet, IHistoryService history, ISettingsService settings, WinGetInterruptionStore store)
        : this(winGet, history)
    {
        _settings = settings;
        LoadListPreferences(settings.Settings);
        if (winGet is not IControlledWinGetService) return;
        _interruptionStore = store;
        HasInterruptedUpdate = store.HasReceipt;
        if (HasInterruptedUpdate) SetInterruptionNote("UpdateOperation.RecoveryReview");
        RestoreSuspendedQueue();
    }

    [ObservableProperty] private string _operationTitle = string.Empty;
    [ObservableProperty] private string _operationStatus = string.Empty;
    [ObservableProperty] private string _operationElapsed = string.Empty;
    [ObservableProperty] private string _operationLastMessage = string.Empty;
    [ObservableProperty] private string _operationResult = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdateControls))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateDetails))]
    private bool _hasOperation;
    [ObservableProperty] private bool _operationStalled;
    [ObservableProperty] private bool _operationInterrupted;
    [ObservableProperty] private bool _showForce;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnmeasuredProgress))]
    private bool _showDownloadProgress;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUnmeasuredProgress))]
    private bool _showActiveProgress;
    public bool ShowUnmeasuredProgress => ShowActiveProgress && !ShowDownloadProgress;
    [ObservableProperty] private bool _downloadIndeterminate = true;
    [ObservableProperty] private double _downloadPercent;
    [ObservableProperty] private string _downloadPercentage = string.Empty;
    [ObservableProperty] private string _downloadSize = string.Empty;
    [ObservableProperty] private string _downloadRate = string.Empty;
    [ObservableProperty] private string _downloadEta = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageChangeBlockReason))]
    [NotifyPropertyChangedFor(nameof(PendingPackageChangeHint))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RecoveryActionStatus))]
    private string _interruptionNote = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    private string? _observedVersionForReview;

    public bool ShowInterruptionReview => ObservedVersionForReview is not null;
    partial void OnObservedVersionForReviewChanged(string? value) => OnPropertyChanged(nameof(ShowInterruptionReview));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlocksPackageChanges))]
    [NotifyPropertyChangedFor(nameof(PackageChangeBlockReason))]
    [NotifyPropertyChangedFor(nameof(PendingPackageChangeHint))]
    [NotifyPropertyChangedFor(nameof(RecoveryActionDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RecoveryCompletion))]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    [NotifyPropertyChangedFor(nameof(RefreshDisabledReason))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DismissWarningCommand))]
    private bool _hasInterruptedUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlocksPackageChanges))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RecoveryActionDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RecoveryCompletion))]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(DismissWarningCommand))]
    private bool _isCheckingInterruption;

    public bool BlocksPackageChanges => IsUpdating || HasInterruptedUpdate || IsCheckingInterruption;
    internal bool CanCloseWithoutWaiting => _operation?.Control.State == WinGetControlState.NeedsDecision && _interruptionStore is not null;
    internal bool CanCancelQueueForClose => IsUpdating && CanCancelUpdate();
    internal bool CanForceForClose => CanForceUpdate();

    private void PrepareBatchPanel(int total)
    {
        RemainingUpdates = total;
        BatchSummary = ResumeReview = string.Empty;
        ResumeDifferences = [];
        OperationTitle = OperationResult = OperationLastMessage = string.Empty;
        OperationElapsed = "00:00:00";
        OperationStalled = OperationInterrupted = false;
        OperationStatus = LocalizationService.Source("UpdateOperation.Waiting");
        SetDownloadProgress(false, null);
        HasOperation = true;
    }

    private UpdateOperation StartOperation(UpdateItemRow row, int position, int total)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var operation = new UpdateOperation(new WinGetProcessControl
        {
            ChooseInstallLocation = ChooseUpdateLocation ?? ((package, token) =>
                dispatcher.InvokeAsync(() => SelectInstallLocation(package), DispatcherPriority.Normal, token).Task),
            ConfirmBattleNetLocation = (package, installation, token) =>
                dispatcher.InvokeAsync(() => ConfirmBattleNetLocation(package, installation, token),
                    DispatcherPriority.Normal, token).Task,
        });
        _operation = operation;
        RemainingUpdates = total - position;
        CurrentOperation = OperationTitle = LocalizationService.Source("UpdateOperation.QueuePosition", row.Name, position, total);
        OperationStatus = LocalizationService.Source("UpdateOperation.Waiting");
        OperationElapsed = "00:00:00";
        OperationLastMessage = string.Empty;
        OperationResult = string.Empty;
        OperationStalled = OperationInterrupted = false;
        SetDownloadProgress(false, null);
        HasOperation = true;
        RefreshOperationActions();
        _operationTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background,
            (_, _) => UpdateOperationPanel(), Dispatcher.CurrentDispatcher);
        _operationTimer.Start();
        return operation;
    }

    private void UpdateOperationPanel()
    {
        if (_operation is not { } operation) return;
        var (lines, message, activity, phase, sample) = operation.Output.Drain();
        foreach (var line in lines) AppendLogOnUi(line);
        if (message is not null) OperationLastMessage = message;

        var elapsed = operation.Clock.Elapsed;
        OperationElapsed = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        var state = operation.Control.State;
        ShowActiveProgress = IsUpdating && state != WinGetControlState.Finished;
        var downloading = !IsCancelRequested && ShowActiveProgress && phase == "UpdateOperation.Downloading";
        SetDownloadProgress(downloading, downloading ? sample : null);
        if (IsCancelRequested)
        {
            OperationStatus = LocalizationService.Source("UpdateOperation.QueueStopping");
        }
        else if (state != WinGetControlState.Finished)
        {
            OperationStatus = LocalizationService.Source(downloading && sample is null ? "UpdateOperation.DownloadIndeterminate" : phase);
        }
        var inactivitySeconds = Math.Clamp(_settings?.Settings.WinGetInactivitySeconds ?? 120, 1, 600);
        OperationStalled = state is WinGetControlState.Waiting or WinGetControlState.Running &&
            Stopwatch.GetElapsedTime(activity) >= TimeSpan.FromSeconds(inactivitySeconds);
        RefreshOperationActions();
    }

    private void SetDownloadProgress(bool visible, WinGetDownloadSample? sample)
    {
        DownloadIndeterminate = sample is null;
        DownloadPercent = sample?.Percent ?? 0;
        DownloadPercentage = sample is null ? string.Empty : LocalizationService.Source("UpdateOperation.DownloadPercentage", Math.Floor(DownloadPercent));
        const double megabyte = 1 << 20;
        const double gigabyte = 1 << 30;
        var scale = sample?.Total >= gigabyte ? gigabyte : megabyte;
        var unit = scale == gigabyte ? "GB" : "MB";
        DownloadSize = sample is { } progress
            ? LocalizationService.Source("UpdateOperation.DownloadAmount", progress.Downloaded / scale, unit, progress.Total / scale, unit)
            : string.Empty;
        DownloadRate = sample?.BytesPerSecond is { } rate && rate >= megabyte / 100
            ? LocalizationService.Source("UpdateOperation.DownloadRate", rate / megabyte) : string.Empty;
        DownloadEta = DownloadRate.Length > 0 && sample?.RemainingSeconds is { } seconds && seconds is > 0 and <= 86_400
            ? LocalizationService.Source("UpdateOperation.DownloadEta", Math.Ceiling(seconds)) : string.Empty;
        ShowDownloadProgress = visible;
    }

    private async Task<(ProcessRunResult Result, string? InstalledVersion, WinGetAttemptDecision Decision, bool HistoryHandled)> RunControlledItemAsync(
        IControlledWinGetService controlled, UpdateItemRow row, UpdateOperation operation,
        IReadOnlyList<WinGetPackage> pending, CancellationToken token)
    {
        var package = row.Package;
        WinGetInterruptionStore.Receipt? receipt = null;
        var launchRequested = false;
        var historyHandled = false;
        ProcessRunResult result;
        string? installed = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var refusal = await controlled.PrepareUpgradeAsync(package, operation.Control, token);
            token.ThrowIfCancellationRequested();
            if (refusal is not null)
                return (refusal, null, WinGetAttemptDecision.Evaluate(refusal, null, package.AvailableVersion, verifyInventory: true), false);
            if (_interruptionStore is not null)
            {
                receipt = await Task.Run(() => _interruptionStore.BeginPrepared(package));
                operation.ReceiptId = receipt.OperationId;
                receipt.Bind(operation.Control);
                try { await Task.Run(() => _interruptionStore.SaveSuspendedQueue(receipt, pending.ToArray())); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { AppendLog(L("UpdateOperation.SuspendedQueueNotSaved")); }
            }
            token.ThrowIfCancellationRequested();
            launchRequested = true;
            result = await controlled.UpgradeControlledAsync(package, operation.Output.Write, operation.Control, token);
            operation.Control.State = WinGetControlState.Finished;
            if (!result.StartFailed && !(result.Interrupted && operation.Control.BatchStopRequested) && !operation.Control.DetachRequested)
            {
                SetDownloadProgress(false, null);
                if (!IsCancelRequested) OperationStatus = LocalizationService.Source("UpdateOperation.Confirming");
                RefreshOperationActions();
                installed = await ReadVersionAsync(controlled, package);
            }
        }
        catch (OperationCanceledException) when (!launchRequested && token.IsCancellationRequested)
        {
            result = new() { StartFailed = true, Cancelled = true, HasExitCode = false };
        }
        catch (Exception ex)
        {
            result = new() { StartFailed = !launchRequested, Interrupted = launchRequested, HasExitCode = false, StartError = ex.Message };
        }

        operation.Control.State = WinGetControlState.Finished;
        var decision = WinGetAttemptDecision.Evaluate(result, installed, package.AvailableVersion, verifyInventory: true,
            identityPersisted: _interruptionStore is null || operation.Control.IdentityPersisted,
            cancelledWhileVerifying: !result.StartFailed && token.IsCancellationRequested && result.Observation.IsConclusive);
        var noLaunch = WinGetAttemptDecision.ProvesNoLaunch(result, operation.Control);
        if (result.StartFailed && !noLaunch) decision = decision.WithUnresolvedReceipt();
        var identityHealthy = operation.Control.IdentityPersisted && operation.Control.IdentityPersistenceError is null;
        if (operation.Control.IdentityPersistenceError is { } identityError)
        {
            AppendLog(LocalizationService.Source("UpdateOperation.ReceiptError", identityError));
            decision = decision.WithUnresolvedReceipt();
        }

        if (receipt is not null)
        {
            using var lease = receipt;
            try
            {
                if (noLaunch) await Task.Run(receipt.MarkNotStarted);
                else await Task.Run(() => receipt.RecordOutcome(result, decision.PendingReason,
                    certifyCompletion: identityHealthy && !token.IsCancellationRequested));

                if (!decision.KeepReceipt && decision.Status == WinGetAttemptStatus.Succeeded)
                {
                    historyHandled = true;
                    if (!await RecordHistoryAsync(row, result, UpdateRowStatus.Success,
                            LocalizationService.Source("UpdateOperation.VerifiedVersion", installed), installed))
                    {
                        decision = decision.WithUnresolvedReceipt(WinGetPendingReason.HistoryUnavailable);
                        await Task.Run(() => receipt.RecordOutcome(result, decision.PendingReason,
                            certifyCompletion: identityHealthy && !token.IsCancellationRequested));
                    }
                }
                if (operation.ReceiptId is { } id && (!decision.KeepReceipt || operation.Control.BatchStopRequested))
                {
                    try
                    {
                        if (operation.Control.BatchStopRequested) await Task.Run(() => _interruptionStore!.DiscardQueueCheckpoint(id));
                        else await Task.Run(() => _interruptionStore!.MarkSuspendedQueueReviewed(receipt));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { AppendLog(L("UpdateOperation.SuspendedQueueUnavailable")); }
                }
                if (!decision.KeepReceipt) await Task.Run(receipt.ClearOwned);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppendLog(LocalizationService.Source("UpdateOperation.ReceiptError", ex.Message));
                decision = decision.WithUnresolvedReceipt();
            }
        }

        var persisted = _interruptionStore?.HasReceipt == true;
        if (persisted) decision = decision.WithUnresolvedReceipt();
        _unpersistedInterruption = decision.KeepReceipt && !persisted;
        HasInterruptedUpdate = decision.KeepReceipt;
        if (HasInterruptedUpdate)
        {
            InterruptionTarget = receipt is null ? string.Empty : RecoveryTarget(package.Name, package.Id);
            InterruptionDetails = string.Empty;
            SetInterruptionNote(WinGetAttemptDecision.RecoveryNote(decision.PendingReason));
        }
        return (result, installed, decision, historyHandled);
    }

    private async Task<string?> ReadVersionAsync(IControlledWinGetService service, WinGetPackage package, bool forReview = false)
    {
        try { return forReview ? await service.ReadReviewVersionAsync(package) : await service.ReadInstalledVersionAsync(package); }
        catch (Exception ex)
        {
            AppendLog(LocalizationService.Source("UpdateOperation.InventoryError", ex.Message));
            return null;
        }
    }

    private void FinishOperation(UpdateRowStatus status, string message)
    {
        if (_operation is { } operation) operation.Control.State = WinGetControlState.Finished;
        UpdateOperationPanel();
        OperationInterrupted = status == UpdateRowStatus.Interrupted || status == UpdateRowStatus.Cancelled && HasInterruptedUpdate;
        if (!IsCancelRequested) OperationStatus = LocalizationService.Source(HasInterruptedUpdate &&
            InterruptionNote == L("UpdateOperation.RecoveryInstallerRestart") ? "UpdateOperation.RestartPending" : status switch
        {
            UpdateRowStatus.Success => "UpdateOperation.Completed",
            UpdateRowStatus.SuccessRebootRequired => "Message.SuccessRestartPending",
            UpdateRowStatus.Cancelled => OperationInterrupted ? "UpdateOperation.Interrupted" : "UpdateOperation.Cancelled",
            UpdateRowStatus.Interrupted => "UpdateOperation.Interrupted",
            _ => "UpdateOperation.Failed",
        });
        OperationResult = message;
        EndOperationMonitoring();
    }

    private void EndOperationMonitoring()
    {
        _operationTimer?.Stop();
        _operation = null;
        OperationStalled = false;
        ShowActiveProgress = false;
        RefreshOperationActions();
    }

    private void RefreshOperationActions()
    {
        var showForce = _operation?.Control.State == WinGetControlState.NeedsDecision;
        if (ShowForce != showForce)
        {
            ShowForce = showForce;
            ForceUpdateCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanForceUpdate() => _operation?.Control.State == WinGetControlState.NeedsDecision;

    [RelayCommand(CanExecute = nameof(CanForceUpdate))]
    private void ForceUpdate() => ConfirmForce(Application.Current?.MainWindow);

    internal bool ConfirmForce(Window? owner)
    {
        var operation = _operation;
        return operation?.Control.State == WinGetControlState.NeedsDecision &&
            LocalizedDialog.Confirm(owner, LocalizationService.Source("UpdateOperation.Force"),
                LocalizationService.Source("UpdateOperation.ForceWarning"), "UpdateOperation.Force") &&
            ReferenceEquals(operation, _operation) && operation.Control.RequestForce();
    }

    internal Task? DetachForClose()
    {
        if (!CanCloseWithoutWaiting || _operation?.Control.RequestDetach() != true) return null;
        var completion = _closeCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancelUpdate();
        return completion.Task;
    }

    private static string DiagnosticExcerpt(ProcessRunResult result, UpdateRowStatus status)
    {
        if (status is UpdateRowStatus.Success or UpdateRowStatus.SuccessRebootRequired) return string.Empty;
        var excerpt = string.Join(Environment.NewLine, result.Output.Concat(result.Errors).Append(result.StartError ?? string.Empty)
            .Select(WinGetDownloadProgress.StripControls)
            .Where(line => !IsProgressNoise(line) && !WinGetDownloadProgress.IsByteProgress(line) &&
                line != LocalizationService.Source("UpdateOperation.ExternalMayContinue")).TakeLast(6)
            .Select(line => line[..Math.Min(line.Length, 300)]));
        return excerpt.Length == 0 ? string.Empty : Environment.NewLine + excerpt;
    }

    private static string PackageLog(ProcessRunResult result)
    {
        var lines = result.Output.Concat(result.Errors).Append(result.StartError ?? string.Empty)
            .Select(WinGetDownloadProgress.StripControls)
            .Where(line => !IsProgressNoise(line) && !WinGetDownloadProgress.IsByteProgress(line))
            .Select(line => line[..Math.Min(line.Length, 1024)])
            .TakeLast(120).ToArray();
        return lines.Length == 0 ? L("UpdateOperation.NoPackageOutput") : string.Join(Environment.NewLine, lines);
    }

    private string? ConfirmBattleNetLocation(WinGetPackage package, BattleNetInstallation installation, CancellationToken token)
    {
        var title = LocalizationService.Source("UpdateOperation.BattleNetUnknownTitle");
        var message = LocalizationService.Source("UpdateOperation.BattleNetUnknownMessage",
            installation.LauncherVersion, package.AvailableVersion, installation.Location);
        var confirmed = ConfirmUnknownBattleNetUpdate?.Invoke(title, message) ??
            LocalizedDialog.Confirm(Application.Current?.MainWindow, title, message, "UpdateOperation.BattleNetChooseFolder");
        token.ThrowIfCancellationRequested();
        if (!confirmed) return null;

        return ChooseBattleNetUpdateLocation is { } choose
            ? choose(package, installation.Location)
            : SelectInstallLocation(package, installation.Location);
    }

    private static string? SelectInstallLocation(WinGetPackage package, string? suggested = null)
    {
        var key = WinGetInstallerRequirements.IsBattleNet(package)
            ? "UpdateOperation.SelectBattleNetFolder" : "UpdateOperation.SelectInstallFolder";
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = LocalizationService.Current.Format(key, package.Name), Multiselect = false,
        };
        if (suggested is not null) dialog.InitialDirectory = suggested;
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FolderName : null;
    }

    private sealed class UpdateOperation(WinGetProcessControl control)
    {
        internal Stopwatch Clock { get; } = Stopwatch.StartNew();
        internal WinGetProcessControl Control { get; } = control;
        internal Guid? ReceiptId { get; set; }
        internal UpdateOutput Output { get; } = new();
    }

    private sealed class UpdateOutput
    {
        private readonly Lock _lock = new();
        private readonly Queue<string> _lines = new();
        private long _activity = Stopwatch.GetTimestamp();
        private string? _latest;
        private string _phase = "UpdateOperation.Waiting";
        private readonly WinGetDownloadProgress _download = new();

        internal void Write(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lock)
            {
                line = WinGetDownloadProgress.StripControls(line);
                _activity = Stopwatch.GetTimestamp();
                if (_phase == "UpdateOperation.Downloading" && _download.Observe(line, _activity)) return;
                if (WinGetDownloadProgress.IsByteProgress(line)) return;
                if (IsProgressNoise(line)) return;
                _latest = line;
                if (_lines.Count == 80) _lines.Dequeue();
                _lines.Enqueue(line);
                if (WinGetService.FindProgressPhase(line) is { } phase && phase != _phase)
                {
                    _phase = phase;
                    _download.Reset();
                }
            }
        }

        internal (string[] Lines, string? Latest, long Activity, string Phase, WinGetDownloadSample? Sample) Drain()
        {
            lock (_lock)
            {
                var snapshot = (_lines.ToArray(), _latest, _activity, _phase, _download.Read(Stopwatch.GetTimestamp()));
                _lines.Clear();
                return snapshot;
            }
        }

    }
}

