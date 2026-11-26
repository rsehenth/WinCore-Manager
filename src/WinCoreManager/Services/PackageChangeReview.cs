using WinCoreManager.Models;

namespace WinCoreManager.Services;

internal sealed record PackageChangeReview(string Fingerprint, string Boot, string InventoryState, string? Version)
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
        foreach (var process in known.Distinct())
        {
            var observed = Observe(process);
            if (observed == PackageProcessState.Active)
            {
                delegation = observed;
                break;
            }
            if (delegation == PackageProcessState.Unknown || observed == PackageProcessState.Unverified)
                delegation = observed;
        }
        return new(rootState, delegation);

        static PackageProcessState Observe(WinGetProcessIdentity identity) =>
            !WinGetNative.OriginalProcessMayStillBeActive(identity, out var error) ? PackageProcessState.Exited :
            error == 0 && identity is { Pid: > 0, Created: > 0 } ? PackageProcessState.Active : PackageProcessState.Unverified;
    }

    internal static bool HasRestartEvidence(WinGetInterruptionStore.Receipt receipt, string? currentBoot)
    {
        if (!long.TryParse(currentBoot, out var current) || current <= 0 || current > DateTime.UtcNow.Ticks)
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

    internal static HistoryType HistoryTypeFor(PackageChangeKind kind) => kind switch
    {
        PackageChangeKind.Uninstall => HistoryType.Uninstall,
        PackageChangeKind.Maintenance or PackageChangeKind.External => HistoryType.Maintenance,
        _ => HistoryType.Update,
    };
}

internal enum PackageProcessState { Unknown, Active, Exited, Unverified }
internal readonly record struct PackageActivity(PackageProcessState Root, PackageProcessState Delegates)
{
    internal bool ActivityMayRemain =>
        Root is PackageProcessState.Active or PackageProcessState.Unverified ||
        Delegates is PackageProcessState.Active or PackageProcessState.Unverified;
}
