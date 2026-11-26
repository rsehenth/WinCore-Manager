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
    private CancellationTokenSource? _itemCts;
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
        if (PackageChangesBlockedNow) return false;
        if (_interruptionStore is null) return true;
        try
        {
            var receipt = _interruptionStore.BeginExternal(kind, target);
            reservation = new PackageChangeReservation(receipt, RefreshInterruptionState);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog(LocalizationService.Source("UpdateOperation.ReceiptError", ex.Message));
        }
        return false;
    }

    internal void RefreshInterruptionState()
    {
        if (IsUpdating || IsCheckingInterruption) return;
        var hasReceipt = _unpersistedInterruption || _interruptionStore?.HasReceipt == true;
        if (hasReceipt && !HasInterruptedUpdate)
            SetInterruptionNote("UpdateOperation.RecoveryReview");
        if (!hasReceipt && HasInterruptedUpdate)
        {
            _presentedReview = null;
            ObservedVersionForReview = null;
            InterruptionTarget = InterruptionDetails = string.Empty;
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
    [NotifyPropertyChangedFor(nameof(PackageChangeBlockReason))]
    private string _interruptionNote = string.Empty;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    private string? _observedVersionForReview;

    public bool ShowInterruptionReview => ObservedVersionForReview is not null;
    partial void OnObservedVersionForReviewChanged(string? value) => OnPropertyChanged(nameof(ShowInterruptionReview));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlocksPackageChanges))]
    [NotifyPropertyChangedFor(nameof(HasUpdateControls))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateDetails))]
    [NotifyPropertyChangedFor(nameof(PackageChangeBlockReason))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    private bool _hasInterruptedUpdate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResolveInterruptionCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReviewInterruptionCommand))]
    private bool _isCheckingInterruption;

    public bool BlocksPackageChanges => IsUpdating || HasInterruptedUpdate;
    internal bool CanCloseWithoutWaiting => _operation?.Control.State == WinGetControlState.NeedsDecision && _interruptionStore is not null;
    internal bool CanCancelQueueForClose => IsUpdating && CanCancelUpdate();
    internal bool CanForceForClose => CanForceUpdate();

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
        CurrentOperation = OperationTitle = LocalizationService.Source("UpdateOperation.QueuePosition", row.Name, position, total);
        OperationStatus = LocalizationService.Source("UpdateOperation.Waiting");
        OperationElapsed = "00:00:00";
        OperationLastMessage = string.Empty;
        OperationResult = string.Empty;
        OperationStalled = OperationInterrupted = false;
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
        var (lines, message, activity, phase) = operation.Output.Drain();
        foreach (var line in lines) AppendLogOnUi(line);
        if (message is not null) OperationLastMessage = message;

        var elapsed = operation.Clock.Elapsed;
        OperationElapsed = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        var state = operation.Control.State;
        if (state != WinGetControlState.Finished)
        {
            OperationStatus = LocalizationService.Source(state is WinGetControlState.Cancelling or WinGetControlState.NeedsDecision ||
                _itemCts?.IsCancellationRequested == true ? "UpdateOperation.Cancelling" : phase);
        }
        var inactivitySeconds = Math.Clamp(_settings?.Settings.WinGetInactivitySeconds ?? 120, 1, 600);
        OperationStalled = state is WinGetControlState.Waiting or WinGetControlState.Running &&
            Stopwatch.GetElapsedTime(activity) >= TimeSpan.FromSeconds(inactivitySeconds);
        RefreshOperationActions();
    }

    private async Task<(ProcessRunResult Result, string? InstalledVersion, WinGetAttemptDecision Decision)> RunControlledItemAsync(
        IControlledWinGetService controlled, WinGetPackage package, UpdateOperation operation, CancellationToken token)
    {
        WinGetInterruptionStore.Receipt? receipt = null;
        var launchRequested = false;
        ProcessRunResult result;
        string? installed = null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (await controlled.PrepareUpgradeAsync(package, operation.Control, token) is { } refusal)
                return (refusal, null, WinGetAttemptDecision.Evaluate(refusal, null, package.AvailableVersion, verifyInventory: true));
            if (_interruptionStore is not null)
            {
                receipt = await Task.Run(() => _interruptionStore.BeginPrepared(package));
                receipt.Bind(operation.Control);
            }
            token.ThrowIfCancellationRequested();
            launchRequested = true;
            result = await controlled.UpgradeControlledAsync(package, operation.Output.Write, operation.Control, token);
            operation.Control.State = WinGetControlState.Finished;
            if (!result.StartFailed && !operation.Control.DetachRequested)
            {
                OperationStatus = LocalizationService.Source("UpdateOperation.Verifying");
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
            identityPersisted: _interruptionStore is null || operation.Control.IdentityPersisted);
        var noLaunch = WinGetAttemptDecision.ProvesNoLaunch(result, operation.Control);
        if (result.StartFailed && !noLaunch) decision = decision.WithUnresolvedReceipt();
        if (operation.Control.IdentityPersistenceError is { } identityError)
        {
            AppendLog(LocalizationService.Source("UpdateOperation.ReceiptError", identityError));
            decision = decision.WithUnresolvedReceipt();
        }

        using (receipt)
        {
            try
            {
                if (receipt is not null && noLaunch) await Task.Run(receipt.MarkNotStarted);
                if (receipt is not null && !decision.KeepReceipt) await Task.Run(receipt.Clear);
                else if (receipt is not null && !noLaunch) await Task.Run(() => receipt.RecordOutcome(result));
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
            SetInterruptionNote(decision.RestartRequired ? "UpdateOperation.RecoveryRestart" : "UpdateOperation.RecoveryReview");
        }
        return (result, installed, decision);
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
        OperationStatus = LocalizationService.Source(status switch
        {
            UpdateRowStatus.Success or UpdateRowStatus.SuccessRebootRequired => "UpdateOperation.Completed",
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
        CancelCurrentUpdateCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelCurrentUpdate() => _itemCts is { IsCancellationRequested: false } &&
        _operation?.Control.State != WinGetControlState.Finished;

    [RelayCommand(CanExecute = nameof(CanCancelCurrentUpdate))]
    private void CancelCurrentUpdate()
    {
        if (!CanCancelCurrentUpdate()) return;
        CancelUpdate();
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
            .Where(line => !IsProgressNoise(line) && line != LocalizationService.Source("UpdateOperation.ExternalMayContinue")).TakeLast(6)
            .Select(line => line[..Math.Min(line.Length, 300)]));
        return excerpt.Length == 0 ? string.Empty : Environment.NewLine + excerpt;
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
        internal UpdateOutput Output { get; } = new();
    }

    private sealed class UpdateOutput
    {
        private readonly Lock _lock = new();
        private readonly Queue<string> _lines = new();
        private long _activity = Stopwatch.GetTimestamp();
        private string? _latest;
        private string _phase = "UpdateOperation.Waiting";

        internal void Write(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            lock (_lock)
            {
                _activity = Stopwatch.GetTimestamp();
                if (IsProgressNoise(line)) return;
                _latest = line;
                if (_lines.Count == 80) _lines.Dequeue();
                _lines.Enqueue(line);
                _phase = WinGetService.FindProgressPhase(line) ?? _phase;
            }
        }

        internal (string[] Lines, string? Latest, long Activity, string Phase) Drain()
        {
            lock (_lock)
            {
                var snapshot = (_lines.ToArray(), _latest, _activity, _phase);
                _lines.Clear();
                return snapshot;
            }
        }

    }
}

