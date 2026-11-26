using System.IO;
using System.Threading;
using System.Diagnostics;
using System.ComponentModel;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

internal enum WinGetControlState { Waiting, Running, Cancelling, NeedsDecision, Finished }

internal enum ControlledProcessState { NotObserved, Active, Exited }
internal enum DelegatedProcessState { Possible, KnownActive, CompletionVerified }

internal readonly record struct ExternalProcessObservation(
    ControlledProcessState ControlledProcesses, DelegatedProcessState Delegation)
{
    internal bool IsConclusive => ControlledProcesses == ControlledProcessState.Exited &&
        Delegation == DelegatedProcessState.CompletionVerified;
}

// The caller can request actions. Only the worker holds process and job handles.
internal sealed class WinGetProcessControl
{
    private int _state;
    private int _force;
    private int _detach;
    private WinGetProcessIdentity? _rootIdentity;
    internal Func<WinGetPackage, CancellationToken, Task<string?>>? ChooseInstallLocation { get; init; }
    internal Func<WinGetPackage, BattleNetInstallation, CancellationToken, Task<string?>>? ConfirmBattleNetLocation { get; init; }
    internal WinGetPackage? PreparedPackage { get; set; }
    internal string? InstallLocation { get; set; }
    internal BattleNetUpgradeAuthorization? BattleNetAuthorization { get; set; }
    internal Action? PersistLaunchIntent { get; set; }
    internal Action<WinGetProcessIdentity>? PersistRoot { get; set; }
    internal Action<WinGetProcessIdentity[]>? PersistDelegates { get; set; }
    internal WinGetMsixUpgrade? MsixUpgrade { get; set; }
    internal string? IdentityPersistenceError { get; private set; }
    internal bool IdentityPersisted { get; private set; }
    private readonly HashSet<WinGetProcessIdentity> _delegates = [];

    internal void CaptureRoot(Process process)
    {
        // Reset proof for each launch under a multi-step reservation.
        IdentityPersisted = false;
        RootIdentity = null;
        try { RootIdentity = WinGetNative.CaptureIdentity(process.SafeHandle, (uint)process.Id); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { IdentityPersistenceError = ex.Message; }
    }

    internal bool ObserveDelegates()
    {
        if (RootIdentity is not { } root) return false;
        var added = false;
        foreach (var identity in WinGetNative.FindDescendants(root, _delegates))
            added |= _delegates.Add(identity);
        if (added) Persist(() => PersistDelegates?.Invoke([.. _delegates]));
        return _delegates.Any(identity => WinGetNative.OriginalProcessMayStillBeActive(identity, out _));
    }

    internal WinGetProcessIdentity? RootIdentity
    {
        get => Volatile.Read(ref _rootIdentity);
        set
        {
            Volatile.Write(ref _rootIdentity, value);
            if (value is null) return;
            Persist(() =>
            {
                PersistRoot?.Invoke(value);
                IdentityPersisted = PersistRoot is not null;
            });
        }
    }

    private void Persist(Action write)
    {
        try { write(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { IdentityPersistenceError = ex.Message; }
    }

    internal WinGetControlState State
    {
        get => (WinGetControlState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    internal bool RequestForce() => State == WinGetControlState.NeedsDecision &&
        Interlocked.CompareExchange(ref _force, 1, 0) == 0;

    internal bool RequestDetach() => State == WinGetControlState.NeedsDecision &&
        Interlocked.CompareExchange(ref _detach, 1, 0) == 0;

    internal bool TakeForceRequest() => Interlocked.Exchange(ref _force, 0) != 0;
    internal bool DetachRequested => Volatile.Read(ref _detach) != 0;
}

internal sealed record WinGetProcessIdentity(uint Pid, long Created);
