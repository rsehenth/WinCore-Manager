using System.IO;

namespace WinCoreManager.Services;

internal sealed class PackageChangeReservation : IDisposable
{
    private readonly WinGetInterruptionStore.Receipt _receipt;
    private readonly Action _released;

    internal PackageChangeReservation(WinGetInterruptionStore.Receipt receipt, Action released)
    {
        _receipt = receipt;
        _released = released;
        Control = new() { PersistRoot = receipt.RememberRoot, PersistDelegates = receipt.RememberDelegates };
    }

    internal WinGetProcessControl Control { get; }

    internal void MarkUninstallLaunchPending(string target) => _receipt.MarkUninstallLaunchPending(target);

    internal bool RecordUninstallOutcome(ProcessRunResult run)
    {
        try { _receipt.RecordOutcome(run); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // Same generic contract as WinGet updates: the observed tree ended, no known delegate or MSI
    // transaction remains, and the root identity was persisted. Uninstallers are no longer held
    // only because an invisible delegate is theoretically possible.
    internal bool CanAdvance(ProcessRunResult run) =>
        !run.Interrupted && (run.StartFailed && Control.RootIdentity is null ||
            run.HasExitCode && run.Observation.IsConclusive && !WinGetNative.WindowsInstallerBusy() &&
            !WingetExitCodes.RequiresReboot(run.ExitCode) && !WingetExitCodes.InstallationInProgress(run.ExitCode) &&
            Control.IdentityPersisted && Control.IdentityPersistenceError is null);

    internal Task CompleteAsync() => Task.Run(_receipt.Kind == PackageChangeKind.Uninstall ? _receipt.ClearOwned : _receipt.Clear);

    public void Dispose()
    {
        _receipt.Dispose();
        _released();
    }
}
