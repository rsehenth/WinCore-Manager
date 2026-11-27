using WinCoreManager.Models;

namespace WinCoreManager.Services;

// Verified: the recorded attempt meets the same contract as a live success (WinGet exit 0, observed
// processes ended, no MSI transaction, requested version installed). Otherwise the owner accepts residual risk.
internal sealed record PackageChangeReview(string Fingerprint, string Boot, string InventoryState, string? Version,
    bool Verified = false)
{
    internal const string NoInventory = "—";

    internal static PackageActivity ObserveActivity(WinGetInterruptionStore.Receipt receipt, string? currentBoot)
    {
        var known = receipt.Delegates.OfType<WinGetProcessIdentity>();
        var original = receipt.Identity;
        var rootState = original is null
            ? receipt.Outcome?.RootExited == true ? PackageProcessState.Exited : PackageProcessState.Unknown
            : Observe(original);
        // Also check for children born before a failed receipt update or abruptly stopped observer.
        if (original is { Pid: > 0, Created: > 0 } &&
            (string.Equals(currentBoot, receipt.Boot, StringComparison.Ordinal) ||
             !long.TryParse(currentBoot, out var bootTicks) ||
             bootTicks - new DateTime(1601, 1, 1).Ticks <= original.Created))
            known = known.Concat(WinGetNative.FindDescendants(original, known));

        var delegation = PackageProcessState.Unknown;
        uint? activeDelegatePid = null;
        foreach (var process in known.Distinct())
        {
            var observed = Observe(process);
            if (observed == PackageProcessState.Active)
            {
                delegation = observed;
                activeDelegatePid = process.Pid;
                break;
            }
            if (delegation == PackageProcessState.Unknown || observed == PackageProcessState.Unverified)
                delegation = observed;
        }
        // An MSI transaction in progress is concrete installer activity, whoever started it.
        if (delegation != PackageProcessState.Active && WinGetNative.WindowsInstallerBusy())
            delegation = PackageProcessState.Active;
        return new(rootState, delegation, activeDelegatePid);

        static PackageProcessState Observe(WinGetProcessIdentity identity) =>
            !WinGetNative.OriginalProcessMayStillBeActive(identity, out var error) ? PackageProcessState.Exited :
            error == 0 && identity is { Pid: > 0, Created: > 0 } ? PackageProcessState.Active : PackageProcessState.Unverified;
    }

    internal static bool HasRestartEvidence(WinGetInterruptionStore.Receipt receipt, string? currentBoot)
    {
        if (!TryReadBootTicks(currentBoot, out var current))
            return false;
        if (receipt.IsUnreadable) return receipt.FileTimestamp > 0 && current > receipt.FileTimestamp;
        var previous = long.TryParse(receipt.Boot, out var saved) && saved > 0 ? saved : 0;
        var request = receipt.RequestedUtcTicks ?? 0;
        var recorded = Math.Max(previous, request);
        if (receipt.Identity is { Created: > 0 } root)
        {
            var epoch = new DateTime(1601, 1, 1).Ticks;
            if (root.Created > DateTime.MaxValue.Ticks - epoch) return false;
            recorded = Math.Max(recorded, epoch + root.Created);
        }
        if (previous == 0 && request == 0) recorded = Math.Max(recorded, receipt.FileTimestamp);
        return recorded > 0 && current > recorded;
    }

    // Same-session review is possible whenever this boot still lets us verify the recorded root and every
    // known delegate as ended (plus orphans found by parentage). A restart is only needed when the root
    // identity was never recorded, cannot be verified, or the installer itself asked for one.
    // The owner still confirms: this proves the observed work ended, not that no invisible delegate exists.
    internal static bool CanReviewWithoutRestart(WinGetInterruptionStore.Receipt receipt, string? currentBoot,
        PackageActivity activity) =>
        receipt is { IsUnreadable: false, Identity: not null, Stage: WinGetReceiptStage.RootStarted or WinGetReceiptStage.Observed } &&
        (receipt.Kind != PackageChangeKind.WinGet || receipt.Package is { } package && receipt.Target == package.Id) &&
        IsUsableBoot(currentBoot) && receipt.Boot == currentBoot &&
        activity.Root == PackageProcessState.Exited &&
        (!activity.ActivityMayRemain || LingersAfterCompletedUninstall(receipt, activity)) &&
        !(receipt.Outcome?.Delegation == DelegatedProcessState.KnownActive && activity.Delegates == PackageProcessState.Unknown) &&
        receipt.Outcome is not { RestartRequired: true } &&
        !(receipt.Outcome?.ExitCode is int exit && WingetExitCodes.RequiresReboot(exit));

    // The uninstaller's root exited 0 and the program left the registry, so the uninstall is complete. A second-phase
    // copy that stays open (Inno _iu*.tmp, even with no window) must not hold every package change until a restart.
    // The owner still confirms the release; nothing is terminated.
    internal static bool LingersAfterCompletedUninstall(WinGetInterruptionStore.Receipt receipt, PackageActivity activity) =>
        receipt is { Kind: PackageChangeKind.Uninstall, IsUnreadable: false, Identity: not null, Target: { Length: > 0 } target,
            Outcome: { RootExited: true, ExitCode: 0, RestartRequired: false } } &&
        activity is { Root: PackageProcessState.Exited, Delegates: PackageProcessState.Active } &&
        !WinGetNative.WindowsInstallerBusy() && UninstallEntryListed(target) == false;

    internal static Func<string, bool?> UninstallEntryListed { get; set; } = name =>
    {
        try
        {
            foreach (var (hive, view) in new[] { (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64),
                         (Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry32),
                         (Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryView.Registry64) })
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (root is null) continue;
                foreach (var key in root.GetSubKeyNames())
                {
                    using var entry = root.OpenSubKey(key);
                    if (entry?.GetValue("DisplayName") as string == name) return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return null; }
    };

    internal static bool IsUsableBoot(string? currentBoot) => TryReadBootTicks(currentBoot, out _);

    private static bool TryReadBootTicks(string? currentBoot, out long ticks) =>
        long.TryParse(currentBoot, out ticks) && ticks > 0 && ticks <= DateTime.UtcNow.Ticks;

    internal static HistoryType HistoryTypeFor(PackageChangeKind kind) => kind switch
    {
        PackageChangeKind.Uninstall => HistoryType.Uninstall,
        PackageChangeKind.Maintenance or PackageChangeKind.External => HistoryType.Maintenance,
        _ => HistoryType.Update,
    };
}

internal enum PackageProcessState { Unknown, Active, Exited, Unverified }
internal readonly record struct PackageActivity(PackageProcessState Root, PackageProcessState Delegates, uint? ActiveDelegatePid)
{
    internal bool ActivityMayRemain =>
        Root is PackageProcessState.Active or PackageProcessState.Unverified ||
        Delegates is PackageProcessState.Active or PackageProcessState.Unverified;
}
