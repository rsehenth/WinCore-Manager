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

    internal bool CanAdvance(ProcessRunResult run, bool synchronousTool = false) =>
        !run.Interrupted && (run.StartFailed && Control.RootIdentity is null ||
            synchronousTool && run.HasExitCode && run.Observation.ControlledProcesses == ControlledProcessState.Exited &&
            run.Observation.Delegation != DelegatedProcessState.KnownActive &&
            !WingetExitCodes.RequiresReboot(run.ExitCode) && !WingetExitCodes.InstallationInProgress(run.ExitCode) &&
            Control.IdentityPersisted && Control.IdentityPersistenceError is null);

    internal Task CompleteAsync() => Task.Run(_receipt.Clear);

    public void Dispose()
    {
        _receipt.Dispose();
        _released();
    }
}
