namespace WinCoreManager.Services;

internal enum WinGetAttemptStatus { Succeeded, Failed, Cancelled, Inconclusive }

internal readonly record struct WinGetAttemptDecision(WinGetAttemptStatus Status, bool KeepReceipt, bool RestartRequired)
{
    internal bool CanAdvance => !KeepReceipt;

    internal static bool ProvesNoLaunch(ProcessRunResult run, WinGetProcessControl control) =>
        run.StartFailed && !run.Interrupted && control.RootIdentity is null &&
        run.Observation.ControlledProcesses != ControlledProcessState.Active &&
        run.Observation.Delegation != DelegatedProcessState.KnownActive;

    internal static WinGetAttemptDecision Evaluate(ProcessRunResult run, string? observedVersion, string targetVersion,
        bool verifyInventory, bool identityPersisted = false)
    {
        var cancelled = run.Cancelled || (!run.StartFailed && run.HasExitCode && WingetExitCodes.IsCancelled(run.ExitCode));
        var restart = !run.StartFailed && run.HasExitCode && WingetExitCodes.RequiresReboot(run.ExitCode);
        var observed = !string.IsNullOrWhiteSpace(observedVersion);
        var matches = observed && string.Equals(observedVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        var unresolved = run.Interrupted || !run.StartFailed &&
            (!run.HasExitCode || restart || WingetExitCodes.InstallationInProgress(run.ExitCode) ||
             verifyInventory && (!identityPersisted || !run.Observation.IsConclusive) ||
             verifyInventory && (!observed || run.ExitCode == 0 && !cancelled && !matches));

        var status = cancelled ? WinGetAttemptStatus.Cancelled
            : run.StartFailed ? WinGetAttemptStatus.Failed
            : run.Interrupted || !run.HasExitCode || restart && !WingetExitCodes.FailedBeforeRestart(run.ExitCode)
                ? WinGetAttemptStatus.Inconclusive
            : run.ExitCode != 0 ? WinGetAttemptStatus.Failed : WinGetAttemptStatus.Succeeded;
        return new(status, unresolved, restart);
    }

    internal WinGetAttemptDecision WithUnresolvedReceipt() =>
        new(Status, true, RestartRequired);
}
