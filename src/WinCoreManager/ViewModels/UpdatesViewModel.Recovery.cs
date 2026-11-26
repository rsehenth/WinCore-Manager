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
    private string _interruptionTarget = string.Empty;
    [ObservableProperty] private string _interruptionDetails = string.Empty;

    public string? PackageChangeBlockReason => HasInterruptedUpdate
        ? string.Join(Environment.NewLine, new[] { InterruptionTarget, InterruptionNote }
            .Where(value => value.Length > 0).Select(Localize))
        : null;

    private bool CanResolveInterruption() => HasInterruptedUpdate && !IsBusy && !IsCheckingInterruption && _interruptionStore is not null;

    [RelayCommand(CanExecute = nameof(CanResolveInterruption))]
    private Task ResolveInterruptionAsync() => RecheckInterruptionAsync();

    [RelayCommand]
    private async Task ViewInterruptionDetailsAsync()
    {
        if (string.IsNullOrEmpty(InterruptionDetails) || _detailsCulture != LocalizationService.Current.CultureName)
            await RecheckInterruptionAsync();
        if (!HasInterruptedUpdate) return;
        var message = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { Localize(InterruptionTarget), InterruptionDetails, Localize(InterruptionNote) }.Where(value => value.Length > 0));
        LocalizedDialog.Inform(Application.Current?.MainWindow, T("UpdateOperation.RecoveryDetails"), message);
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
        var message = $"{Localize(InterruptionTarget)}{Environment.NewLine}{Environment.NewLine}{T("UpdateOperation.ReviewVersionConfirm", inventory)}";
        var confirmed = ConfirmInterruptionReview?.Invoke(title, message) ??
            LocalizedDialog.Confirm(Application.Current?.MainWindow, title, message, "UpdateOperation.AcceptResidualRisk");
        if (confirmed && ReferenceEquals(presented, _presentedReview))
            await RecheckInterruptionAsync(version);
    }

    internal async Task RecheckInterruptionAsync(string? reviewedVersion = null)
    {
        RefreshInterruptionState();
        if (!CanResolveInterruption() || _winGet is not IControlledWinGetService service) return;
        IsCheckingInterruption = true;
        var presented = _presentedReview;
        _presentedReview = null;
        ObservedVersionForReview = null;
        try
        {
            if (!_interruptionStore!.HasReceipt)
            {
                SetInterruptionNote("UpdateOperation.RecoveryIdentity");
                return;
            }

            using var receipt = await Task.Run(_interruptionStore.OpenForReview);
            InterruptionTarget = RecoveryTarget(receipt.Package?.Name, receipt.Target);
            if (receipt.ProvesNoLaunch)
            {
                await ClearReviewedReceiptAsync(receipt, "UpdateOperation.RecoveryNotStarted");
                AppendLog(InterruptionNote);
                return;
            }

            var boot = await Task.Run(_interruptionStore.ReadBoot);
            var activity = await Task.Run(() => PackageChangeReview.ObserveActivity(receipt, boot));
            var restarted = PackageChangeReview.HasRestartEvidence(receipt, boot);
            var inventory = new RecoveryInventory(null, "UpdateOperation.RecoveryNotChecked", false);
            if (!activity.ActivityMayRemain)
            {
                inventory = await ReadRecoveryInventoryAsync(receipt, service);
                activity = await Task.Run(() => PackageChangeReview.ObserveActivity(receipt, boot));
            }
            InterruptionDetails = DescribeRecovery(receipt, boot, restarted, activity, inventory);
            _detailsCulture = LocalizationService.Current.CultureName;

            if (activity.ActivityMayRemain)
            {
                SetInterruptionNote("UpdateOperation.OriginalProcessPending");
                return;
            }
            if (string.IsNullOrWhiteSpace(boot))
            {
                SetInterruptionNote("UpdateOperation.RecoveryBootUnavailable");
                return;
            }
            if (!restarted)
            {
                SetInterruptionNote("UpdateOperation.RecoveryRestart");
                return;
            }
            if (!inventory.Ready)
            {
                SetInterruptionNote("UpdateOperation.RecoveryInventory", SafeReceiptLabel(receipt.Target));
                return;
            }

            var review = new PackageChangeReview(receipt.Fingerprint, boot, inventory.Label, inventory.Version);
            var displayedVersion = SafeReceiptLabel(inventory.Version);
            if (presented != review || !string.Equals(displayedVersion, reviewedVersion, StringComparison.Ordinal))
            {
                _presentedReview = review;
                ObservedVersionForReview = displayedVersion;
                InterruptionNote = T("UpdateOperation.RiskReviewAvailable", SafeReceiptLabel(receipt.Target), T(inventory.Label, displayedVersion));
                return;
            }

            if (!await Task.Run(receipt.StillMatches))
            {
                SetInterruptionNote("UpdateOperation.RecoveryReceiptChanged");
                return;
            }
            try
            {
                await _history.AddAsync(new HistoryRecord
                {
                    Type = PackageChangeReview.HistoryTypeFor(receipt.Kind),
                    Target = SafeReceiptLabel(receipt.Package?.Name ?? receipt.Target),
                    Details = $"{receipt.Kind} {SafeReceiptLabel(receipt.Target)} [{receipt.OperationId ?? Guid.Empty}] " +
                        $"SHA-256 {receipt.Fingerprint}",
                    Status = HistoryStatus.Interrupted,
                    VersionFrom = SafeReceiptLabel(receipt.Package?.InstalledVersion),
                    VersionTo = inventory.Version is null ? string.Empty : displayedVersion,
                    Message = L("UpdateOperation.RiskReviewRecord", boot, L(inventory.Label, displayedVersion)),
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                SetInterruptionNote("UpdateOperation.RecoveryHistoryUnavailable");
                return;
            }
            if ((await Task.Run(() => PackageChangeReview.ObserveActivity(receipt, boot))).ActivityMayRemain)
            {
                SetInterruptionNote("UpdateOperation.OriginalProcessPending");
                return;
            }

            await ClearReviewedReceiptAsync(receipt, "UpdateOperation.RiskReviewReleased");
            AppendLog(InterruptionNote);
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
            InterruptionTarget = string.Empty;
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
            IsCheckingInterruption = false;
            if (!HasInterruptedUpdate) await DetectAndScanAsync();
        }
    }

    private async Task ClearReviewedReceiptAsync(WinGetInterruptionStore.Receipt receipt, string message)
    {
        if (!await Task.Run(receipt.StillMatches))
        {
            SetInterruptionNote("UpdateOperation.RecoveryReceiptChanged");
            return;
        }
        await Task.Run(receipt.Clear);
        _unpersistedInterruption = false;
        HasInterruptedUpdate = false;
        InterruptionTarget = InterruptionDetails = string.Empty;
        SetInterruptionNote(message);
    }

    private async Task<RecoveryInventory> ReadRecoveryInventoryAsync(WinGetInterruptionStore.Receipt receipt,
        IControlledWinGetService service)
    {
        if (receipt.Package is { Id: { Length: > 0 } } package &&
            (!receipt.IsUnreadable || WinGetTrust.IsAllowedSource(package.Source) && WingetTableParser.IsActionableId(package.Id)))
        {
            var version = await ReadVersionAsync(service, package, forReview: true);
            if (string.IsNullOrWhiteSpace(version))
                return new(null, "UpdateOperation.RecoveryInventoryUnavailable", false);
            return new(version, WingetTableParser.IsUnknownVersion(version)
                ? "UpdateOperation.RecoveryInventoryUnknownVersion" : "UpdateOperation.RecoveryInventoryVersion", true);
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

    private static string DescribeRecovery(WinGetInterruptionStore.Receipt receipt, string? boot, bool restarted,
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
        var delegates = T(activity.Delegates == PackageProcessState.Unknown
            ? "UpdateOperation.RecoveryDelegatesUnknown" : $"UpdateOperation.RecoveryProcess{activity.Delegates}");
        var restart = T(restarted ? "UpdateOperation.RecoveryBootConfirmed" : string.IsNullOrWhiteSpace(boot)
            ? "UpdateOperation.RecoveryProcessUnknown" : "UpdateOperation.RecoveryBootMissing");
        return T("UpdateOperation.RecoveryDiagnostic", time, T($"UpdateOperation.RecoveryStage{stage}"),
            root, delegates, T(inventory.Label, SafeReceiptLabel(inventory.Version)), restart,
            receipt.Outcome?.ExitCode is int exitCode ? Localize(WingetExitCodes.Describe(exitCode)) : PackageChangeReview.NoInventory);
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
