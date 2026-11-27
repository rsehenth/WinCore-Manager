namespace WinCoreManager.Services;

internal enum WinGetAttemptStatus { Succeeded, Failed, Cancelled, Inconclusive }
internal enum WinGetPendingReason
{
    Unspecified, RestartRequired, Active, Interrupted, TerminationUnverified, ReceiptIncomplete, HistoryUnavailable,
    MsiLogUnverified, MsiRegistrationUnverified, DelegationUnverified, InventoryUnavailable, InventoryMismatch,
}

internal readonly record struct WinGetAttemptDecision(WinGetAttemptStatus Status, bool KeepReceipt, bool RestartRequired,
    WinGetPendingReason PendingReason = WinGetPendingReason.Unspecified)
{
    internal bool CanAdvance => !KeepReceipt;

    internal static bool ProvesNoLaunch(ProcessRunResult run, WinGetProcessControl control) =>
        run.StartFailed && !run.Interrupted && control.RootIdentity is null &&
        run.Observation.ControlledProcesses != ControlledProcessState.Active &&
        run.Observation.Delegation != DelegatedProcessState.KnownActive;

    internal static WinGetAttemptDecision Evaluate(ProcessRunResult run, string? observedVersion, string targetVersion,
        bool verifyInventory, bool identityPersisted = false, bool cancelledWhileVerifying = false)
    {
        var windowsCancelled = WingetExitCodes.ReportsWindowsCancellation(run);
        var cancelled = run.Cancelled || windowsCancelled ||
            (!run.StartFailed && run.HasExitCode && WingetExitCodes.IsCancelled(run.ExitCode));
        var restart = RequiresRestart(run);
        var observed = !string.IsNullOrWhiteSpace(observedVersion);
        var matches = observed && string.Equals(observedVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        var reason = restart ? WinGetPendingReason.RestartRequired
            : run.Observation.ControlledProcesses == ControlledProcessState.Active ||
              run.Observation.Delegation == DelegatedProcessState.KnownActive ||
              !run.StartFailed && run.HasExitCode && WingetExitCodes.InstallationInProgress(run.ExitCode) ? WinGetPendingReason.Active
            : run.Interrupted || cancelledWhileVerifying && !cancelled ||
              verifyInventory && !run.StartFailed && cancelled && !run.Observation.IsConclusive
                ? WinGetPendingReason.Interrupted
            : run.StartFailed ? WinGetPendingReason.Unspecified
            : !run.HasExitCode ? WinGetPendingReason.TerminationUnverified
            : !verifyInventory ? WinGetPendingReason.Unspecified
            : !identityPersisted ? WinGetPendingReason.ReceiptIncomplete
            : run.Observation.ControlledProcesses != ControlledProcessState.Exited ? WinGetPendingReason.TerminationUnverified
            // All observed activity ended. WinGet's failure code is the result; no inventory is needed to release it.
            : run.ExitCode != 0 && !cancelled ? WinGetPendingReason.Unspecified
            : !observed && !(windowsCancelled && run.Observation.IsConclusive)
                ? WinGetPendingReason.InventoryUnavailable
            : run.ExitCode == 0 && !cancelled && !matches ? WinGetPendingReason.InventoryMismatch
            : WinGetPendingReason.Unspecified;

        var status = cancelled ? WinGetAttemptStatus.Cancelled
            : run.StartFailed ? WinGetAttemptStatus.Failed
            : run.Interrupted || !run.HasExitCode || restart && !WingetExitCodes.FailedBeforeRestart(run.ExitCode)
                ? WinGetAttemptStatus.Inconclusive
            : run.ExitCode != 0 ? WinGetAttemptStatus.Failed : WinGetAttemptStatus.Succeeded;
        return new(status, reason != WinGetPendingReason.Unspecified, restart, reason);
    }

    internal static bool RequiresRestart(ProcessRunResult run) => !run.StartFailed &&
        (run.RestartRequestedByInstaller || run.HasExitCode && WingetExitCodes.RequiresReboot(run.ExitCode));

    // Keep the specific reason already found (e.g. Active) instead of hiding it behind "record incomplete".
    internal WinGetAttemptDecision WithUnresolvedReceipt(WinGetPendingReason reason = WinGetPendingReason.ReceiptIncomplete) =>
        new(Status, true, RestartRequired, RestartRequired ? WinGetPendingReason.RestartRequired
            : KeepReceipt && PendingReason != WinGetPendingReason.Unspecified ? PendingReason : reason);

    internal static string RecoveryNote(WinGetPendingReason reason) => reason switch
    {
        WinGetPendingReason.RestartRequired => "UpdateOperation.RecoveryInstallerRestart",
        WinGetPendingReason.Active => "UpdateOperation.RecoveryInstallerActive",
        WinGetPendingReason.Interrupted => "UpdateOperation.RecoveryInterruptedAttempt",
        WinGetPendingReason.TerminationUnverified => "UpdateOperation.RecoveryTerminationUnverified",
        WinGetPendingReason.ReceiptIncomplete => "UpdateOperation.RecoveryEvidenceIncomplete",
        WinGetPendingReason.HistoryUnavailable => "UpdateOperation.RecoveryHistoryUnavailable",
        WinGetPendingReason.MsiLogUnverified => "UpdateOperation.RecoveryMsiLogUnverified",
        WinGetPendingReason.MsiRegistrationUnverified => "UpdateOperation.RecoveryMsiRegistrationUnverified",
        WinGetPendingReason.DelegationUnverified => "UpdateOperation.RecoveryDelegationUnknown",
        WinGetPendingReason.InventoryUnavailable => "UpdateOperation.RecoveryFreshInventoryMissing",
        WinGetPendingReason.InventoryMismatch => "UpdateOperation.RecoveryFreshInventoryMismatch",
        _ => "UpdateOperation.RecoveryReview",
    };
}
