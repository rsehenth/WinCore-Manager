using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public partial class UpdatesViewModel
{
    private PackageChangeReview? _presentedReview;
    private string? _detailsCulture;
    internal Func<string, string, bool>? ConfirmInterruptionReview { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PackageChangeBlockReason))]
    [NotifyPropertyChangedFor(nameof(PendingPackageChangeHint))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    private string _interruptionTarget = string.Empty;
    [ObservableProperty] private string _interruptionState = string.Empty;
    [ObservableProperty] private string _interruptionDetails = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecoveryActionStatus))]
    [NotifyPropertyChangedFor(nameof(RecoveryCompletion))]
    [NotifyPropertyChangedFor(nameof(RecoveryActionDisabledReason))]
    private string _recoveryFeedback = string.Empty;

    public string RecoveryActionStatus => RecoveryFeedback.Length > 0 ? RecoveryFeedback : InterruptionNote;
    public string RecoveryCompletion => HasInterruptedUpdate || IsCheckingInterruption ? string.Empty : RecoveryFeedback;
    public string? PackageChangeBlockReason => PendingPackageChangeHint is { } pending ? Localize(pending) : null;

    internal string? PendingPackageChangeHint => HasInterruptedUpdate ||
        (!IsUpdating && !UninstallRunning && !MaintenanceRunning && _interruptionStore?.HasReceipt == true)
        ? string.Join("\n", new[] { L("Message.PackageAction.Pending"), InterruptionTarget, InterruptionNote }
            .Where(value => value.Length > 0))
        : null;

    private bool CanResolveInterruption() => HasInterruptedUpdate && !IsBusy && !IsCheckingInterruption && _interruptionStore is not null;
    public string? RecoveryActionDisabledReason => CanResolveInterruption() ? null
        : IsCheckingInterruption ? RecoveryActionStatus : L("Message.PackageAction.Busy");

    [RelayCommand(CanExecute = nameof(CanResolveInterruption))]
    private Task ResolveInterruptionAsync() => RecheckInterruptionAsync(userRequested: true);

    [RelayCommand(CanExecute = nameof(CanResolveInterruption))]
    private Task DismissWarningAsync() => RecheckInterruptionAsync(dismissWarning: true, userRequested: true);

    [RelayCommand]
    private async Task ViewInterruptionDetailsAsync()
    {
        if (string.IsNullOrEmpty(InterruptionDetails) || _detailsCulture != LocalizationService.Current.CultureName)
            await RecheckInterruptionAsync();
        if (!HasInterruptedUpdate) return;
        var message = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { Localize(InterruptionTarget), Localize(RecoveryActionStatus), T("UpdateOperation.RecoverySteps"), InterruptionDetails }
                .Where(value => value.Length > 0));
        LocalizedDialog.Inform(Application.Current?.MainWindow, T("UpdateOperation.RecoveryStepsDetails"), message);
    }

    private void SetInterruptionNote(string key, params object?[] arguments) =>
        InterruptionNote = LocalizationService.Source(key, arguments);

    private bool CanReviewInterruption() => CanResolveInterruption() && ObservedVersionForReview is not null;

    [RelayCommand(CanExecute = nameof(CanReviewInterruption))]
    private async Task ReviewInterruptionAsync()
    {
        if (!CanReviewInterruption() || ObservedVersionForReview is not { } version || _presentedReview is not { } presented) return;
        var title = T("UpdateOperation.ReviewVersion");
        var inventory = T(presented.InventoryState, version);
        var confirmation = presented.Verified ? "UpdateOperation.VerifiedRecoveryConfirm" : "UpdateOperation.ReviewVersionConfirm";
        var confirmLabel = presented.Verified ? "UpdateOperation.VerifiedRecoveryConfirmClosure" : "UpdateOperation.AcceptResidualRisk";
        var message = $"{Localize(InterruptionTarget)}{Environment.NewLine}{Environment.NewLine}{T(confirmation, inventory)}";
        var confirmed = ConfirmInterruptionReview?.Invoke(title, message) ??
            LocalizedDialog.Confirm(Application.Current?.MainWindow, title, message, confirmLabel);
        if (!confirmed)
            RecoveryFeedback = $"{L("UpdateOperation.RecoveryReviewDeclined")}\n{InterruptionNote}";
        else if (ReferenceEquals(presented, _presentedReview))
            await RecheckInterruptionAsync(version, userRequested: true);
        else
            RecoveryFeedback = L("UpdateOperation.RecoveryReceiptChanged");
    }

    internal async Task RecheckInterruptionAsync(string? reviewedVersion = null, bool dismissWarning = false,
        bool userRequested = false)
    {
        RefreshInterruptionState();
        if (!CanResolveInterruption() || _winGet is not IControlledWinGetService service)
        {
            if (userRequested && !IsCheckingInterruption)
                RecoveryFeedback = L(HasInterruptedUpdate ? "UpdateOperation.RecoveryIdentity" : "UpdateOperation.RecoveryAlreadyResolved");
            return;
        }
        IsCheckingInterruption = true;
        RecoveryFeedback = L(dismissWarning ? "UpdateOperation.RecoveryCheckingDismissal" : "UpdateOperation.RecoveryChecking");
        var presented = _presentedReview;
        _presentedReview = null;
        ObservedVersionForReview = null;
        var declined = false;
        try
        {
            if (!_interruptionStore!.HasReceipt)
            {
                SetInterruptionNote("UpdateOperation.RecoveryIdentity");
                return;
            }

            using var receipt = await Task.Run(_interruptionStore.OpenForReview);
            InterruptionTarget = RecoveryTarget(receipt.Package?.Name, receipt.Target);
            InterruptionState = L($"UpdateOperation.RecoveryStage{(receipt.IsUnreadable ? "Unreadable" : receipt.Stage)}");
            if (reviewedVersion is not null && presented is not null && presented.Fingerprint != receipt.Fingerprint)
            {
                SetInterruptionNote("UpdateOperation.RecoveryReceiptChanged");
                return;
            }
            if (receipt.ProvesNoLaunch)
            {
                if (dismissWarning)
                {
                    declined = !ConfirmWarningDismissal(T("UpdateOperation.DismissNoLaunch"));
                    if (declined || !await RecordRecoveryDecisionAsync(receipt, null, default, dismissed: true))
                        return;
                }
                await RefreshInventoryForRecoveryAsync();
                await ClearReviewedReceiptAsync(receipt, "UpdateOperation.RecoveryNotStarted");
                AppendLog(InterruptionNote);
                return;
            }

            var boot = await Task.Run(_interruptionStore.ReadBoot);
            var activity = await Task.Run(() => PackageChangeReview.ObserveActivity(receipt, boot));
            var restarted = PackageChangeReview.HasRestartEvidence(receipt, boot);
            var sameSessionReview = PackageChangeReview.CanReviewWithoutRestart(receipt, boot, activity);
            var inventory = new RecoveryInventory(null, "UpdateOperation.RecoveryNotChecked", false);
            UpdateRecoveryDetails(receipt, boot, restarted, activity, inventory);

            if (SetActivityBlockNote(receipt, activity))
                return;
            if (dismissWarning && receipt.IsUnreadable)
            {
                SetInterruptionNote("UpdateOperation.DismissUnreadableReceipt");
                return;
            }
            if (!PackageChangeReview.IsUsableBoot(boot))
            {
                SetInterruptionNote("UpdateOperation.RecoveryBootUnavailable");
                return;
            }
            if (!restarted && !sameSessionReview)
            {
                // Only two cases still need a restart: the installer asked for one, or this session
                // cannot verify the recorded processes (identity missing or unreadable receipt).
                SetInterruptionNote(receipt.Outcome is { RestartRequired: true } ||
                    receipt.Outcome?.ExitCode is int exit && WingetExitCodes.RequiresReboot(exit)
                        ? "UpdateOperation.RecoveryInstallerRestart" : "UpdateOperation.RecoveryRestart");
                return;
            }
            RecoveryFeedback = L("UpdateOperation.RecoveryCheckingInventory");
            inventory = await ReadRecoveryInventoryAsync(receipt, service);
            if (await RecheckActivityAsync()) return;
            if (!inventory.Ready)
            {
                SetInterruptionNote("UpdateOperation.RecoveryInventory", SafeReceiptLabel(receipt.Target));
                return;
            }
            if (!await CheckReceiptAsync(receipt)) return;

            // The live success contract applied retrospectively: WinGet exit 0, processes verified as ended
            // in this session (checked above) and the requested version installed.
            var verified = receipt is { Kind: PackageChangeKind.WinGet, Outcome: { ExitCode: 0, Interrupted: false, CancellationConfirmed: false, ControlledProcesses: ControlledProcessState.Exited } } &&
                inventory.Label == "UpdateOperation.RecoveryInventoryExpectedVersion";
            var review = new PackageChangeReview(receipt.Fingerprint, boot!, inventory.Label, inventory.Version, verified);
            var displayedVersion = SafeReceiptLabel(inventory.Version);
            if (dismissWarning)
            {
                declined = !ConfirmWarningDismissal(T("UpdateOperation.DismissWarningConfirm", T(inventory.Label, displayedVersion)));
                if (declined)
                {
                    PresentReview(review, displayedVersion);
                    return;
                }
                if (await RecheckActivityAsync())
                    return;
            }
            else if (presented != review || !string.Equals(displayedVersion, reviewedVersion, StringComparison.Ordinal))
            {
                PresentReview(review, displayedVersion);
                return;
            }

            if (!await RecordRecoveryDecisionAsync(receipt, boot, inventory, dismissWarning, review.Verified))
                return;

            await RefreshInventoryForRecoveryAsync();
            if (await RecheckActivityAsync())
                return;

            await ClearReviewedReceiptAsync(receipt, review.Verified ? "UpdateOperation.VerifiedRecoveryReleased" : dismissWarning
                ? "UpdateOperation.DismissWarningReleased" : "UpdateOperation.RiskReviewReleased");
            AppendLog(InterruptionNote);

            async Task<bool> RecheckActivityAsync()
            {
                activity = await Task.Run(() => PackageChangeReview.ObserveActivity(receipt, boot));
                UpdateRecoveryDetails(receipt, boot, restarted, activity, inventory);
                if (SetActivityBlockNote(receipt, activity)) return true;
                if (restarted || PackageChangeReview.CanReviewWithoutRestart(receipt, boot, activity)) return false;
                SetInterruptionNote("UpdateOperation.RecoveryEvidenceIncomplete");
                return true;
            }
        }
        catch (FileNotFoundException) when (_interruptionStore?.HasReceipt == false && !_unpersistedInterruption)
        {
            HasInterruptedUpdate = false;
            SetInterruptionNote("UpdateOperation.RecoveryAlreadyResolved");
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            InterruptionDetails = T("UpdateOperation.RecoveryLeaseBusy");
            _detailsCulture = LocalizationService.Current.CultureName;
            InterruptionTarget = InterruptionState = string.Empty;
            SetInterruptionNote("UpdateOperation.RecoveryLeaseBusy");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InterruptionDetails = T("UpdateOperation.RecoveryReceiptUnavailable");
            _detailsCulture = LocalizationService.Current.CultureName;
            SetInterruptionNote("UpdateOperation.RecoveryReceiptUnavailable");
        }
        finally
        {
            RecoveryFeedback = !HasInterruptedUpdate ? InterruptionNote
                : declined ? $"{L("UpdateOperation.RecoveryReviewDeclined")}\n{InterruptionNote}"
                : dismissWarning && _presentedReview is not { Verified: true } ? $"{L("UpdateOperation.RecoveryDismissBlocked")}\n{InterruptionNote}"
                : string.Empty;
            IsCheckingInterruption = false;
        }
    }

    private Task<bool> RefreshInventoryForRecoveryAsync()
    {
        RecoveryFeedback = L("UpdateOperation.RecoveryRefreshingInventory");
        return DetectAndScanAsync(preserveSelection: true);
    }

    private void PresentReview(PackageChangeReview review, string displayedVersion)
    {
        _presentedReview = review;
        ObservedVersionForReview = displayedVersion;
        var next = review.Verified ? "UpdateOperation.VerifiedRecoveryReviewNext" : "UpdateOperation.RecoveryReviewNext";
        InterruptionNote = $"{L(next)}\n{L(review.InventoryState, displayedVersion)}";
    }

    private bool ConfirmWarningDismissal(string message)
    {
        var title = T("UpdateOperation.DismissWarning");
        message = $"{Localize(InterruptionTarget)}{Environment.NewLine}{Environment.NewLine}{message}";
        return ConfirmInterruptionReview?.Invoke(title, message) ??
            LocalizedDialog.Confirm(Application.Current?.MainWindow, title, message, "UpdateOperation.AcceptResidualRisk");
    }

    private bool SetActivityBlockNote(WinGetInterruptionStore.Receipt receipt, PackageActivity activity)
    {
        if (PackageChangeReview.LingersAfterCompletedUninstall(receipt, activity)) return false;
        if (!activity.ActivityMayRemain &&
            !(receipt.Outcome?.Delegation == DelegatedProcessState.KnownActive && activity.Delegates == PackageProcessState.Unknown))
            return false;
        var reason = activity.Root switch
        {
            PackageProcessState.Active => "UpdateOperation.OriginalProcessPending",
            PackageProcessState.Unverified => "UpdateOperation.RecoveryRootUnverified",
            _ when activity.Delegates == PackageProcessState.Active => "UpdateOperation.RecoveryDelegateActive",
            _ => "UpdateOperation.RecoveryDelegateUnverified",
        };
        SetInterruptionNote(reason);
        return true;
    }

    private async Task<bool> RecordRecoveryDecisionAsync(WinGetInterruptionStore.Receipt receipt, string? boot,
        RecoveryInventory inventory, bool dismissed, bool verified = false)
    {
        if (!await CheckReceiptAsync(receipt)) return false;
        var displayedVersion = SafeReceiptLabel(inventory.Version);
        var message = verified
            ? L("UpdateOperation.VerifiedRecoveryRecord", boot, displayedVersion)
            : receipt.ProvesNoLaunch ? L("UpdateOperation.DismissNoLaunchRecord") :
            L(dismissed ? "UpdateOperation.DismissWarningRecord" : "UpdateOperation.RiskReviewRecord",
                boot, L(inventory.Label, displayedVersion));
        try
        {
            await _history.AddAsync(new HistoryRecord
            {
                Type = PackageChangeReview.HistoryTypeFor(receipt.Kind),
                Target = SafeReceiptLabel(receipt.Package?.Name ?? receipt.Target),
                Details = $"{receipt.Kind} {SafeReceiptLabel(receipt.Target)} [{receipt.OperationId ?? Guid.Empty}] " +
                    $"SHA-256 {receipt.Fingerprint}",
                Status = verified ? HistoryStatus.Success : HistoryStatus.Interrupted,
                VersionFrom = SafeReceiptLabel(receipt.Package?.InstalledVersion),
                VersionTo = inventory.Version is null ? string.Empty : displayedVersion,
                Message = message,
            });
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            SetInterruptionNote("UpdateOperation.RecoveryHistoryUnavailable");
            return false;
        }
    }

    private async Task<bool> CheckReceiptAsync(WinGetInterruptionStore.Receipt receipt, bool clear = false)
    {
        var queueWriteFailed = false;
        try
        {
            if (await Task.Run(() =>
            {
                if (!receipt.StillMatches()) return false;
                if (clear)
                {
                    try { _interruptionStore!.MarkSuspendedQueueReviewed(receipt); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        queueWriteFailed = true;
                        return false;
                    }
                    if (!receipt.StillMatches()) return false;
                    receipt.Clear();
                }
                return true;
            }))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // The lease excludes another app instance. Unexpected removal leaves no evidence for this session.
            _unpersistedInterruption = true;
        }
        SetInterruptionNote(queueWriteFailed ? "UpdateOperation.SuspendedQueueUnavailable" : "UpdateOperation.RecoveryReceiptChanged");
        return false;
    }

    private async Task ClearReviewedReceiptAsync(WinGetInterruptionStore.Receipt receipt, string message)
    {
        if (!await CheckReceiptAsync(receipt, clear: true)) return;
        _unpersistedInterruption = false;
        HasInterruptedUpdate = false;
        InterruptionTarget = InterruptionState = InterruptionDetails = string.Empty;
        SetInterruptionNote(message);
    }

    private async Task<RecoveryInventory> ReadRecoveryInventoryAsync(WinGetInterruptionStore.Receipt receipt,
        IControlledWinGetService service)
    {
        if (receipt.Package is { Id: { Length: > 0 } } package &&
            (!receipt.IsUnreadable || WinGetTrust.IsAllowedSource(package.Source) && WingetTableParser.IsActionableId(package.Id)))
        {
            var version = await ReadVersionAsync(service, package, forReview: true);
            // A completed listing without one row for the ID cannot confirm a version, but it is not a failed read.
            // Keeping it "unavailable" would hold the receipt forever; it allows only the supervised risk review.
            if (version is { Length: 0 })
                return new(null, "UpdateOperation.RecoveryInventoryUncorrelated", true);
            if (string.IsNullOrWhiteSpace(version))
                return new(null, "UpdateOperation.RecoveryInventoryUnavailable", false);
            var label = "UpdateOperation.RecoveryInventoryVersion";
            if (WingetTableParser.IsUnknownVersion(version))
                label = "UpdateOperation.RecoveryInventoryUnknownVersion";
            else if (!receipt.IsUnreadable && receipt.Kind == PackageChangeKind.WinGet &&
                !string.IsNullOrWhiteSpace(package.AvailableVersion) && !WingetTableParser.IsUnknownVersion(package.AvailableVersion))
                label = string.Equals(version, package.AvailableVersion, StringComparison.OrdinalIgnoreCase)
                    ? "UpdateOperation.RecoveryInventoryExpectedVersion" : "UpdateOperation.RecoveryInventoryDifferentVersion";
            return new(version, label, true);
        }
        if (receipt.IsExternal && !receipt.IsUnreadable)
            return new(null, "UpdateOperation.RecoveryInventoryNotApplicable", true);

        // A damaged or anonymous WinGet receipt cannot be matched to a package. Require a
        // healthy inventory read and disclose the missing package correlation in the audit.
        try
        {
            var inventory = await service.ListUpdatesAsync();
            if (!inventory.Failed)
                return new(null, inventory.Error is null ? "UpdateOperation.RecoveryInventoryUncorrelated"
                    : "UpdateOperation.RecoveryInventoryPartial", true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        return new(null, "UpdateOperation.RecoveryInventoryUnavailable", false);
    }

    private void UpdateRecoveryDetails(WinGetInterruptionStore.Receipt receipt, string? boot, bool restarted,
        PackageActivity activity, RecoveryInventory inventory)
    {
        var stamp = receipt.RequestedUtcTicks ?? receipt.FileTimestamp;
        var time = stamp > 0 && stamp <= DateTime.MaxValue.Ticks
            ? new DateTime(stamp, DateTimeKind.Utc).ToLocalTime().ToString("g", LocalizationService.Current.Culture)
            : PackageChangeReview.NoInventory;
        if (receipt.RequestedUtcTicks is null && stamp > 0)
            time = T("UpdateOperation.RecoveryReceiptTimestamp", time);
        var stage = receipt.IsUnreadable ? "Unreadable" : receipt.Stage.ToString();
        var root = T($"UpdateOperation.RecoveryProcess{activity.Root}");
        var sameSessionReview = PackageChangeReview.CanReviewWithoutRestart(receipt, boot, activity);
        var delegates = T(activity.Delegates == PackageProcessState.Unknown
            ? "UpdateOperation.RecoveryDelegatesUnknown" : $"UpdateOperation.RecoveryProcess{activity.Delegates}");
        if (activity.Root == PackageProcessState.Active && receipt.Identity is { } identity)
            root = T("UpdateOperation.RecoveryProcessWithPid", root, identity.Pid);
        if (activity.ActiveDelegatePid is { } delegatedPid)
            delegates = T("UpdateOperation.RecoveryProcessWithPid", delegates, delegatedPid);
        var restart = T(sameSessionReview ? "UpdateOperation.RecoverySameSession" : restarted ? "UpdateOperation.RecoveryBootConfirmed" : !PackageChangeReview.IsUsableBoot(boot)
            ? "UpdateOperation.RecoveryProcessUnknown" : "UpdateOperation.RecoveryBootMissing");
        InterruptionDetails = T("UpdateOperation.RecoveryDiagnostic", time, T($"UpdateOperation.RecoveryStage{stage}"),
            root, delegates, T(inventory.Label, SafeReceiptLabel(inventory.Version)), restart,
            receipt.Outcome?.ExitCode is int exitCode ? Localize(WingetExitCodes.Describe(exitCode)) : PackageChangeReview.NoInventory);
        _detailsCulture = LocalizationService.Current.CultureName;
    }

    private static string RecoveryTarget(string? name, string? id) =>
        name is null && id is null ? L("UpdateOperation.RecoveryUnknownTarget")
            : L("UpdateOperation.RecoveryTarget", SafeReceiptLabel(name ?? id), SafeReceiptLabel(id));

    private static string SafeReceiptLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.IndexOfAny(['\\', '/', ':']) >= 0)
            return PackageChangeReview.NoInventory;
        return new string(label.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .Take(140).ToArray());
    }

    private static string L(string key, params object?[] args) => LocalizationService.Source(key, args);
    private static string T(string key, params object?[] args) => LocalizationService.Current.Format(key, args);
    private static string Localize(string value) => PresentationText.Translate(value, LocalizationService.Current.Culture);
    private readonly record struct RecoveryInventory(string? Version, string Label, bool Ready);
}
