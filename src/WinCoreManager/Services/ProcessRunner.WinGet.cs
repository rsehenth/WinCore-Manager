using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Localization;

namespace WinCoreManager.Services;

public static partial class ProcessRunner
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    internal static Task<ProcessRunResult> RunWinGetAsync(ProcessStartInfo startInfo, Action<string>? output,
        CancellationToken cancellationToken, WinGetProcessControl? control = null, bool externalInstallerPossible = true,
        Func<IDisposable?>? reserveLaunch = null) =>
        Task.Run(() => RunWinGetCoreAsync(startInfo, output, cancellationToken, control, externalInstallerPossible, reserveLaunch));

    private static async Task<ProcessRunResult> RunWinGetCoreAsync(ProcessStartInfo startInfo, Action<string>? output,
        CancellationToken token, WinGetProcessControl? control, bool externalInstallerPossible, Func<IDisposable?>? reserveLaunch)
    {
        var clock = Stopwatch.StartNew();
        var started = false;
        var launchAmbiguous = false;
        var exited = false;
        var exitCode = -1;
        var controlledProcesses = ControlledProcessState.NotObserved;
        var delegatedActive = false;
        var diagnostics = new List<string>();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (token.IsCancellationRequested)
                return new() { StartFailed = true, Cancelled = true, HasExitCode = false };

            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.RedirectStandardInput = true;
            using (var reservation = reserveLaunch?.Invoke())
            {
                if (reserveLaunch is not null && reservation is null)
                    return new() { StartFailed = true, HasExitCode = false, StartError = LocalizationService.Source("UpdateOperation.UntrustedAppInstaller") };
                if (token.IsCancellationRequested)
                    return new() { StartFailed = true, Cancelled = true, HasExitCode = false };
                control?.PersistLaunchIntent?.Invoke();
                if (token.IsCancellationRequested)
                    return new() { StartFailed = true, Cancelled = true, HasExitCode = false };
                launchAmbiguous = true;
                try { started = process.Start(); }
                catch (Win32Exception)
                {
                    // A failed native creation is a start failure. Treat other exceptions
                    // during Process.Start as ambiguous in case stream setup failed after creation.
                    launchAmbiguous = false;
                    throw;
                }
                launchAmbiguous = false;
            }
            if (!started)
                return new() { StartFailed = true, HasExitCode = false, StartError = LocalizationService.Source("Message.TheProcessDidNotStart") };

            var handle = process.SafeHandle;
            var pid = process.Id;
            bool OwnsRoot() => !handle.IsInvalid && !handle.IsClosed && WinGetNative.GetProcessId(handle) == (uint)pid;
            if (control is not null && OwnsRoot()) control.RootIdentity = WinGetNative.CaptureIdentity(handle, (uint)pid);
            using var job = control is null || !OwnsRoot() ? null : WinGetNative.TryCreateJob(handle, diagnostics);
            // Close the only input writer. A hidden WinGet instance can never wait for console entry.
            process.StandardInput.Close();
            using var stdoutReader = process.StandardOutput;
            using var stderrReader = process.StandardError;
            var rebootWarning = false;
            void ObserveOutput(string line)
            {
                if (externalInstallerPossible && !rebootWarning) rebootWarning = WingetExitCodes.IsRebootWarning(line);
                output?.Invoke(line);
            }
            var stdout = new WinGetPipe(stdoutReader, startInfo.StandardOutputEncoding, ObserveOutput, control is null ? 16_384 : 600);
            var stderr = new WinGetPipe(stderrReader, startInfo.StandardErrorEncoding, ObserveOutput, 600);
            if (control is not null) control.State = WinGetControlState.Running;

            var cancelSent = false;
            var forced = false;
            var normalCloseSent = false;
            TimeSpan? graceUntil = null;
            TimeSpan? batchStopUntil = null;
            TimeSpan? drainUntil = null;
            TimeSpan? settleUntil = null;
            var settleReported = false;
            var nextSnapshot = TimeSpan.Zero;
            var rootExitObserved = false;
            while (true)
            {
                var outputRead = stdout.Pump() | stderr.Pump();
                if (!exited && process.HasExited)
                {
                    exited = true;
                    exitCode = process.ExitCode;
                }

                var activeMembers = job is null ? 0u : WinGetNative.ActiveProcesses(job);
                controlledProcesses = !exited || activeMembers > 0 ? ControlledProcessState.Active
                    : job is null ? ControlledProcessState.NotObserved : ControlledProcessState.Exited;
                if (control is not null && (clock.Elapsed >= nextSnapshot || exited && !rootExitObserved))
                {
                    delegatedActive = control.ObserveDelegates();
                    rootExitObserved = exited;
                    nextSnapshot = clock.Elapsed + TimeSpan.FromMilliseconds(250);
                }
                if (exited && activeMembers == 0)
                {
                    if (stdout.Closed && stderr.Closed)
                    {
                        if (control is not null && settleUntil is null) delegatedActive = control.ObserveDelegates();
                        if (!SettlingExternalWork())
                            return Result(cancelled: forced || (cancelSent && (normalCloseSent || WingetExitCodes.IsCancelled(exitCode))),
                                interrupted: control is not null && externalInstallerPossible && (forced || (cancelSent && normalCloseSent)));
                    }
                    else
                    {
                        drainUntil ??= clock.Elapsed + DrainGrace;
                        if (clock.Elapsed >= drainUntil)
                        {
                            AddDiagnostic("Message.ProcessOutputDrainTimeout");
                            return Result(cancelled: false, interrupted: true);
                        }
                    }
                }

                if (!cancelSent && token.IsCancellationRequested && drainUntil is null)
                {
                    cancelSent = true;
                    if (control is not null) control.State = WinGetControlState.Cancelling;
                    if (!exited && OwnsRoot())
                    {
                        try { normalCloseSent = process.CloseMainWindow(); }
                        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                        { AddDiagnostic("UpdateOperation.ControlError", ex.Message); }
                    }

                    if (control is not null && !normalCloseSent) AddDiagnostic("UpdateOperation.NoGracefulClose");
                    graceUntil = clock.Elapsed + (normalCloseSent ? CloseGrace : TimeSpan.Zero);
                }

                if (graceUntil is { } deadline && clock.Elapsed >= deadline)
                {
                    graceUntil = null;
                    if (control is not null) control.State = WinGetControlState.NeedsDecision;
                    else if (externalInstallerPossible) return Result(cancelled: false, interrupted: true);
                    if (control?.BatchStopRequested == true) batchStopUntil ??= clock.Elapsed + CloseGrace;
                }

                if (drainUntil is null && control?.DetachRequested == true)
                {
                    AddDiagnostic("UpdateOperation.ExternalMayContinue");
                    return Result(cancelled: false, interrupted: true);
                }

                if (drainUntil is null && (control?.TakeForceRequest() == true || (control is null && cancelSent && !forced && graceUntil is null)))
                {
                    if (control is not null) control.State = WinGetControlState.Cancelling;
                    try
                    {
                        if (job is not null && activeMembers > 0 && OwnsRoot() && WinGetNative.IsMember(handle, job))
                            WinGetNative.Terminate(job);
                        else if (!exited && OwnsRoot())
                            WinGetNative.TerminateRoot(handle);
                        else
                            throw new InvalidOperationException(LocalizationService.Source("UpdateOperation.OwnershipUnconfirmed"));
                        forced = true;
                        graceUntil = clock.Elapsed + CloseGrace;
                        if (batchStopUntil is not null) batchStopUntil = graceUntil;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        AddDiagnostic("UpdateOperation.ControlError", ex.Message);
                        if (control is null) return Result(cancelled: false, interrupted: true);
                        control.State = WinGetControlState.NeedsDecision;
                        continue;
                    }
                }

                if (drainUntil is null && batchStopUntil is { } batchDeadline && clock.Elapsed >= batchDeadline)
                {
                    if (forced) return BatchStopUnconfirmed();
                    delegatedActive = control!.ObserveDelegates();
                    if ((externalInstallerPossible && delegatedActive) || exited || !OwnsRoot()) return BatchStopUnconfirmed();

                    // An upgrade can delegate outside the job, even while WinGet is printing download
                    // progress. Automatically stop only the verified root in that case and keep the receipt.
                    try
                    {
                        if (!externalInstallerPossible && job is not null && activeMembers > 0 && WinGetNative.IsMember(handle, job))
                            WinGetNative.Terminate(job);
                        else
                            WinGetNative.TerminateRoot(handle);
                        forced = true;
                        control.State = WinGetControlState.Cancelling;
                        batchStopUntil = clock.Elapsed + CloseGrace;
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                    {
                        AddDiagnostic("UpdateOperation.ControlError", ex.Message);
                        return BatchStopUnconfirmed();
                    }
                }

                if (control is null && forced && !exited && graceUntil is null)
                    return Result(cancelled: false, interrupted: true);

                // Poll only while holding the original process and pipe handles. Neither a pipe
                // writer nor an installer can block the dispatcher or an outstanding pipe read.
                if (outputRead) await Task.Yield();
                else await Task.Delay(20).ConfigureAwait(false);
            }

            // WinGet returned, but a process it started outside the Job (an elevated or relaunched uninstaller,
            // for example) or a Windows Installer transaction is still working. Supervise it for a bounded
            // time so a finished attempt is judged on its real outcome. Stop and Detach still apply meanwhile.
            bool SettlingExternalWork()
            {
                if (control is not { DelegateSettleWindow.Ticks: > 0 } || !externalInstallerPossible || cancelSent ||
                    !delegatedActive && !WinGetNative.WindowsInstallerBusy()) return false;
                settleUntil ??= clock.Elapsed + control.DelegateSettleWindow;
                if (clock.Elapsed >= settleUntil) return false;
                if (!settleReported)
                {
                    settleReported = true;
                    AddDiagnostic("UpdateOperation.WaitingForExternalInstaller");
                }
                return true;
            }

            ProcessRunResult BatchStopUnconfirmed()
            {
                AddDiagnostic("UpdateOperation.ExternalMayContinue");
                return Result(cancelled: false, interrupted: true);
            }

            ProcessRunResult Result(bool cancelled, bool interrupted)
            {
                var restart = rebootWarning;
                var cleanExit = externalInstallerPossible && !cancelled && !interrupted && !cancelSent &&
                    !token.IsCancellationRequested && exited && exitCode == 0 && !restart &&
                    controlledProcesses == ControlledProcessState.Exited &&
                    control is { IdentityPersisted: true, IdentityPersistenceError: null };
                var msi = externalInstallerPossible && exited && !interrupted ? control?.MsiUpgrade?.Observe() : null;
                restart |= msi == WinGetMsiResult.RestartRequired;
                // The restricted MSIX command can finish with registration deferred.
                // Only the exact current-user registration change completes that contract.
                var contract = cleanExit && !delegatedActive && !restart
                    ? msi == WinGetMsiResult.Exited ? WinGetCompletionContract.MsiTransaction
                        : control!.MsixUpgrade?.VerifyRegistration() == true ? WinGetCompletionContract.MsixRegistration
                        : WinGetCompletionContract.None
                    : WinGetCompletionContract.None;
                if (contract != WinGetCompletionContract.None)
                {
                    delegatedActive = control!.ObserveDelegates();
                    if (delegatedActive || token.IsCancellationRequested || control.IdentityPersistenceError is not null)
                        contract = WinGetCompletionContract.None;
                }
                // A running MSI transaction is concrete installer activity, even when a broker started it outside the tree.
                if (externalInstallerPossible && control is not null && !delegatedActive && WinGetNative.WindowsInstallerBusy())
                {
                    delegatedActive = true;
                    contract = WinGetCompletionContract.None;
                    AddDiagnostic("UpdateOperation.WindowsInstallerBusy");
                }
                var delegation = delegatedActive ? DelegatedProcessState.KnownActive
                    : !externalInstallerPossible || contract != WinGetCompletionContract.None
                        ? DelegatedProcessState.CompletionVerified : DelegatedProcessState.Possible;

                var errors = stderr.Lines.ToList();
                errors.AddRange(diagnostics);
                return new()
                {
                    Cancelled = cancelled && !interrupted,
                    Interrupted = interrupted,
                    HasExitCode = exited,
                    ExitCode = exitCode,
                    Observation = new(controlledProcesses, delegation, contract),
                    MsiResult = msi,
                    RestartRequestedByInstaller = restart,
                    Output = stdout.Lines.ToList(),
                    Errors = errors,
                    DurationMs = clock.ElapsedMilliseconds,
                };
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new()
            {
                StartFailed = !started && !launchAmbiguous,
                Interrupted = started || launchAmbiguous,
                HasExitCode = exited,
                ExitCode = exitCode,
                Observation = new(controlledProcesses, delegatedActive ? DelegatedProcessState.KnownActive : DelegatedProcessState.Possible),
                StartError = ex.Message,
                StartErrorCode = (ex as Win32Exception)?.NativeErrorCode,
                Errors = diagnostics,
                DurationMs = clock.ElapsedMilliseconds,
            };
        }
        finally
        {
            if (control is not null) control.State = WinGetControlState.Finished;
        }

        void AddDiagnostic(string key, params object?[] args)
        {
            var message = LocalizationService.Source(key, args);
            if (diagnostics.Count == 20) diagnostics.RemoveAt(0);
            diagnostics.Add(message);
            output?.Invoke(message);
        }
    }

    private sealed class WinGetPipe(StreamReader reader, Encoding? encoding, Action<string>? output, int capacity)
    {
        private readonly SafeFileHandle _pipe = ((FileStream)reader.BaseStream).SafeFileHandle;
        private readonly Decoder _decoder = (encoding ?? Encoding.UTF8).GetDecoder();
        private readonly byte[] _bytes = new byte[8192];
        private readonly char[] _chars = new char[8193];
        private readonly StringBuilder _line = new(256);
        internal Queue<string> Lines { get; } = new();
        internal bool Closed { get; private set; }

        internal bool Pump()
        {
            if (Closed) return false;
            var count = WinGetNative.ReadAvailable(_pipe, _bytes);
            if (count < 0) Closed = true;
            if (count == 0) return false;

            var chars = _decoder.GetChars(_bytes.AsSpan(0, Math.Max(0, count)), _chars, flush: Closed);
            foreach (var character in _chars.AsSpan(0, chars))
            {
                if (character is '\n' or '\r') Publish();
                else if (_line.Length < 2048) _line.Append(character);
            }
            if (Closed) Publish();
            return !Closed;
        }

        private void Publish()
        {
            if (_line.Length == 0) return;
            var text = _line.ToString().TrimEnd();
            _line.Clear();
            if (string.IsNullOrEmpty(text)) return;
            if (Lines.Count == capacity) Lines.Dequeue();
            Lines.Enqueue(text);
            output?.Invoke(text);
        }
    }
}
