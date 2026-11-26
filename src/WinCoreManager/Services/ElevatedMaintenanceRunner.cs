using WinCoreManager.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Helpers;

namespace WinCoreManager.Services;

internal static class ElevatedMaintenanceRunner
{
    public static Task<ProcessRunResult> RunAsync(string executablePath, string arguments, Encoding encoding,
        Action<string>? onOutputLine, CancellationToken cancellationToken, WinGetProcessControl? control = null)
    {
        var tool = MaintenanceService.Tools.SingleOrDefault(candidate => candidate.RequiresElevation
            && string.Equals(candidate.SystemExecutable, executablePath, StringComparison.OrdinalIgnoreCase)
            && (candidate.Key == "chkdsk_scan"
                ? arguments.Length == 8 && char.IsAsciiLetter(arguments[0]) && arguments[1..] == ": /scan"
                  && MaintenanceService.IsValidDriveLetter(arguments[..1])
                : candidate.Arguments == arguments));
        if (tool is null)
        {
            return Task.FromResult(new ProcessRunResult
            {
                StartFailed = true,
                StartError = LocalizationService.Source("Message.TheProcessCouldNotBeStarted"),
            });
        }

        return Task.Run(() => RunCoreAsync(tool.SystemExecutable, arguments, cancellationToken, elevate: true,
            encoding, onOutputLine, control: control));
    }

    internal static async Task<ProcessRunResult> RunCoreAsync(
        string executablePath, string arguments, CancellationToken cancellationToken, bool elevate,
        Encoding? encoding = null, Action<string>? onOutputLine = null, ProcessObservationPolicy? observation = null,
        WinGetProcessControl? control = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The elevated process writes only to a local pipe. It never reads requests from the UI
        // or opens a log path controlled by the non-elevated user.
        // The non-elevated integration test can run under Windows app-container isolation.
        var pipeName = (elevate ? string.Empty : @"LOCAL\") + $"WinCoreManager-{Guid.NewGuid():N}";
        var completionMarker = $"WinCoreManager-completed-{Guid.NewGuid():N}";
        // Windows grants write access to the creator and administrators by default, including
        // when UAC is approved with a different administrator account. Verify the client PID as well.
        await using var pipe = new NamedPipeServerStream(
            pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = Environment.SystemDirectory,
            Verb = elevate ? "runas" : string.Empty,
            UseShellExecute = elevate,
            CreateNoWindow = !elevate,
            RedirectStandardError = !elevate,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(BuildCommand(pipeName, completionMarker, executablePath, arguments,
            (encoding ?? NativeMethods.OemEncoding).CodePage));

        var stopwatch = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        Process? started;
        try
        {
            if (!elevate) started = Process.Start(startInfo);
            else
            {
                var launch = Task.Run(() => Process.Start(startInfo));
                try { started = await launch.WaitAsync(TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    // The wrapper cannot start the tool without the pipe. Dispose any late launcher handle.
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    _ = launch.ContinueWith(completed =>
                    {
                        if (completed.IsCompletedSuccessfully) completed.Result?.Dispose();
                        else _ = completed.Exception;
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return new ProcessRunResult { Interrupted = true, HasExitCode = false, DurationMs = stopwatch.ElapsedMilliseconds };
                }
            }
        }
        catch (Win32Exception ex)
        {
            return new ProcessRunResult
            {
                Cancelled = ex.NativeErrorCode == 1223,
                StartFailed = true,
                StartError = ex.NativeErrorCode == 1223 ? LocalizationService.Source("Message.AdministratorApprovalWasCanceled") : ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        using var process = started;
        if (process is null)
        {
            return new ProcessRunResult { StartFailed = true, StartError = LocalizationService.Source("Message.CouldNotStartMaintenanceWithAdministratorPrivileges") };
        }

        control?.CaptureRoot(process);
        // Observe the delegated tool without terminating the launcher or its children.
        using var captureDeadline = new CancellationTokenSource();
        var output = new List<string>();
        var capture = ReadOutputAsync(pipe, process.Id, output, onOutputLine, completionMarker, captureDeadline.Token);
        var diagnostic = startInfo.RedirectStandardError ? process.StandardError.ReadToEndAsync(captureDeadline.Token) : null;
        var observed = await ProcessRunner.ObserveExitAsync(process, observation ?? ProcessObservationPolicy.Maintenance,
            cancellationToken, control: control).ConfigureAwait(false);
        captureDeadline.CancelAfter(TimeSpan.FromSeconds(3));
        var errors = new List<string>();
        var childCompleted = false;
        try
        {
            childCompleted = await capture.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            errors.Add(LocalizationService.Source("Message.CouldNotReadOutputFromTheAdministratorMaintenance"));
        }

        if (diagnostic is not null)
        {
            try
            {
                if (await diagnostic.ConfigureAwait(false) is { Length: > 0 } detail) errors.Add(detail);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                errors.Add(LocalizationService.Source("Message.CouldNotReadOutputFromTheAdministratorMaintenance"));
            }
        }

        if (!observed.Exited) errors.Add(LocalizationService.Source("Message.ExternalProcessObservationEnded"));
        else if (cancellationToken.IsCancellationRequested)
            errors.Add(LocalizationService.Source("Message.CancellationRequestedTheCurrentStepFinishedBeforeMaintenance"));

        return new ProcessRunResult
        {
            ExitCode = observed.Exited ? process.ExitCode : -1,
            HasExitCode = observed.Exited,
            Interrupted = !observed.Exited,
            Observation = new(!observed.Exited ? ControlledProcessState.Active :
                    childCompleted ? ControlledProcessState.Exited : ControlledProcessState.NotObserved,
                observed.DelegateActive ? DelegatedProcessState.KnownActive : DelegatedProcessState.Possible),
            Output = output,
            Errors = errors,
            DurationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static async Task<bool> ReadOutputAsync(NamedPipeServerStream pipe, int processId, List<string> lines,
        Action<string>? onOutputLine, string completionMarker, CancellationToken cancellationToken)
    {
        while (true)
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) && clientId == (uint)processId)
            {
                break;
            }

            pipe.Disconnect();
        }

        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var completed = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.Equals(line, completionMarker, StringComparison.Ordinal))
            {
                completed = true;
                continue;
            }
            if (!string.IsNullOrWhiteSpace(line))
            {
                var text = line.TrimEnd();
                lines.Add(text);
                onOutputLine?.Invoke(text);
            }
        }
        return completed;
    }

    private static string BuildCommand(string pipeName, string completionMarker, string executablePath, string arguments, int codePage)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $pipe = [IO.Pipes.NamedPipeClientStream]::new('.', {{Literal(pipeName)}}, [IO.Pipes.PipeDirection]::Out,
                [IO.Pipes.PipeOptions]::None, [Security.Principal.TokenImpersonationLevel]::Anonymous)
            try {
                $pipe.Connect(10000)
                $writer = [IO.StreamWriter]::new($pipe, [Text.Encoding]::UTF8)
                $writer.AutoFlush = $true
                try {
                    $info = [Diagnostics.ProcessStartInfo]::new()
                    $info.FileName = {{Literal(executablePath)}}
                    $info.Arguments = {{Literal(arguments)}}
                    $info.WorkingDirectory = [Environment]::SystemDirectory
                    $info.UseShellExecute = $false
                    $info.CreateNoWindow = $true
                    $info.RedirectStandardOutput = $true
                    $info.RedirectStandardError = $true
                    $info.StandardOutputEncoding = [Text.Encoding]::GetEncoding({{codePage}})
                    $info.StandardErrorEncoding = $info.StandardOutputEncoding
                    $child = [Diagnostics.Process]::Start($info)
                    try {
                        $stdout = $child.StandardOutput.ReadLineAsync()
                        $stderr = $child.StandardError.ReadLineAsync()
                        $drain = $null
                        while (($null -ne $stdout) -or ($null -ne $stderr)) {
                            [Threading.Tasks.Task[]]$pending = @($stdout, $stderr) -ne $null
                            [void][Threading.Tasks.Task]::WaitAny($pending, 100)
                            if (($null -ne $stdout) -and $stdout.IsCompleted) {
                                $line = $stdout.GetAwaiter().GetResult()
                                if ($null -eq $line) { $stdout = $null }
                                else { $writer.WriteLine($line); $stdout = $child.StandardOutput.ReadLineAsync() }
                            }
                            if (($null -ne $stderr) -and $stderr.IsCompleted) {
                                $line = $stderr.GetAwaiter().GetResult()
                                if ($null -eq $line) { $stderr = $null }
                                else { $writer.WriteLine($line); $stderr = $child.StandardError.ReadLineAsync() }
                            }
                            if ($child.HasExited) {
                                if ($null -eq $drain) { $drain = [Diagnostics.Stopwatch]::StartNew() }
                                if ($drain.ElapsedMilliseconds -ge 3000) { break }
                            }
                        }
                        $child.WaitForExit()
                        $writer.WriteLine({{Literal(completionMarker)}})
                        exit $child.ExitCode
                    } finally {
                        $child.Dispose()
                    }
                } catch {
                    $writer.WriteLine($_.Exception.Message)
                    exit 1
                } finally {
                    $writer.Dispose()
                }
            } finally {
                $pipe.Dispose()
            }
            """;
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
}
