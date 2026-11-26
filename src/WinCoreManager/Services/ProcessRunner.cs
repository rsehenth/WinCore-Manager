using WinCoreManager.Localization;
using System.Diagnostics;
using System.IO;

namespace WinCoreManager.Services;

public sealed class ProcessRunResult
{
    public int ExitCode { get; init; }

    public bool Cancelled { get; init; }
    public bool Interrupted { get; init; }
    public bool HasExitCode { get; init; } = true;
    internal ExternalProcessObservation Observation { get; init; }

    /// <summary>True quando o processo nem chegou a iniciar (ex.: UAC recusado, exe ausente).</summary>
    public bool StartFailed { get; init; }

    public string? StartError { get; init; }

    /// <summary>Código nativo do Win32Exception quando o processo não pôde ser iniciado (ex.: 5 = acesso negado).</summary>
    public int? StartErrorCode { get; init; }

    public List<string> Output { get; init; } = new();

    public List<string> Errors { get; init; } = new();

    public long DurationMs { get; init; }

    public string FullOutput => string.Join(Environment.NewLine, Output.Concat(Errors));
}

/// <summary>
/// Executa processos externos de forma controlada: captura de saída,
/// observação com prazo e encerramento opcional do processo iniciado diretamente.
/// Nenhum argumento de usuário é montado aqui — os chamadores usam
/// definições fixas (allowlist) desta aplicação.
/// </summary>
public static partial class ProcessRunner
{
    public static Task<ProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        Action<string>? onOutputLine = null,
        Action<string>? onErrorLine = null,
        CancellationToken cancellationToken = default,
        bool terminateOnCancellation = true,
        Action? onStarted = null,
        ProcessObservationPolicy? observation = null) =>
        RunObservedAsync(startInfo, onOutputLine, onErrorLine, cancellationToken, terminateOnCancellation, onStarted, observation);

    internal static async Task<ProcessRunResult> RunObservedAsync(
        ProcessStartInfo startInfo, Action<string>? onOutputLine, Action<string>? onErrorLine,
        CancellationToken cancellationToken, bool terminateOnCancellation, Action? onStarted = null,
        ProcessObservationPolicy? observation = null, WinGetProcessControl? control = null)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new ProcessRunResult { Cancelled = true, StartFailed = true, StartError = LocalizationService.Source("Message.OperationCanceledBeforeItStarted") };
        }
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        var output = new List<string>();
        var errors = new List<string>();
        var locker = new object();

        void Append(List<string> target, string? line, Action<string>? callback)
        {
            if (line is null)
            {
                return;
            }

            // Ferramentas como DISM usam \r para progresso na mesma "linha".
            foreach (var part in line.Replace("\r", "\n").Split('\n'))
            {
                var trimmed = part.TrimEnd();
                lock (locker)
                {
                    target.Add(trimmed);
                }

                callback?.Invoke(trimmed);
            }
        }

        var stopwatch = Stopwatch.StartNew();

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                return new ProcessRunResult { StartFailed = true, StartError = LocalizationService.Source("Message.TheProcessDidNotStart") };
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            stopwatch.Stop();
            return new ProcessRunResult
            {
                StartFailed = true,
                StartError = ex.Message,
                StartErrorCode = ex.NativeErrorCode,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            stopwatch.Stop();
            return new ProcessRunResult
            {
                StartFailed = true,
                StartError = ex.Message,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        control?.CaptureRoot(process);
        onStarted?.Invoke();
        using var drain = new CancellationTokenSource();
        async Task ReadUntilEnd(StreamReader stream, List<string> target, Action<string>? callback)
        {
            while (await stream.ReadLineAsync(drain.Token).ConfigureAwait(false) is { } line)
            {
                Append(target, line, callback);
            }
        }
        var reads = Task.WhenAll(ReadUntilEnd(process.StandardOutput, output, onOutputLine),
            ReadUntilEnd(process.StandardError, errors, onErrorLine));

        var observed = await ObserveExitAsync(process, observation ?? ProcessObservationPolicy.Direct,
            cancellationToken, terminateOnCancellation, control).ConfigureAwait(false);

        drain.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await reads.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (drain.IsCancellationRequested)
        {
            errors.Add(LocalizationService.Source("Message.ProcessOutputDrainTimeout"));
        }
        catch (IOException ex)
        {
            errors.Add(ex.Message);
        }
        if (!observed.Exited)
            errors.Add(LocalizationService.Source("Message.ExternalProcessObservationEnded"));

        stopwatch.Stop();

        return new ProcessRunResult
        {
            ExitCode = observed.Exited ? process.ExitCode : -1,
            HasExitCode = observed.Exited,
            Interrupted = !observed.Exited,
            Observation = new(observed.Exited ? ControlledProcessState.Exited : ControlledProcessState.Active,
                observed.DelegateActive ? DelegatedProcessState.KnownActive : DelegatedProcessState.Possible),
            Cancelled = observed.Exited && observed.TerminationRequested,
            Output = output,
            Errors = errors,
            DurationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    internal static async Task<(bool Exited, bool TerminationRequested, bool DelegateActive)> ObserveExitAsync(Process process,
        ProcessObservationPolicy policy, CancellationToken cancellationToken, bool terminateOwnedProcess = false,
        WinGetProcessControl? control = null)
    {
        using var observationLifetime = new CancellationTokenSource();
        var observation = control is null ? Task.CompletedTask : ObserveDescendantsAsync(control, observationLifetime.Token);
        (bool Exited, bool TerminationRequested) exit;
        try { exit = await WaitForExitAsync(process, policy, cancellationToken, terminateOwnedProcess).ConfigureAwait(false); }
        finally
        {
            await observationLifetime.CancelAsync().ConfigureAwait(false);
            await observation.ConfigureAwait(false);
        }
        return (exit.Exited, exit.TerminationRequested, control?.ObserveDelegates() == true);
    }

    private static async Task ObserveDescendantsAsync(WinGetProcessControl control, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                control.ObserveDelegates();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task<(bool Exited, bool TerminationRequested)> WaitForExitAsync(Process process,
        ProcessObservationPolicy policy, CancellationToken cancellationToken, bool terminateOwnedProcess)
    {
        var clock = Stopwatch.StartNew();
        using var waitLifetime = new CancellationTokenSource();
        try
        {
            var exit = process.WaitForExitAsync(waitLifetime.Token);
            try
            {
                await exit.WaitAsync(policy.MaximumWait, cancellationToken).ConfigureAwait(false);
                return (true, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (exit.IsCompletedSuccessfully) return (true, false);
                var terminated = terminateOwnedProcess && RequestOwnedRootTermination(process);
                var remaining = policy.MaximumWait - clock.Elapsed;
                var grace = remaining < policy.AfterCancellation ? remaining : policy.AfterCancellation;
                if (grace <= TimeSpan.Zero) return (false, terminated);
                await exit.WaitAsync(grace).ConfigureAwait(false);
                return (true, terminated);
            }
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return (false, false); }
        finally { waitLifetime.Cancel(); }
    }

    private static bool RequestOwnedRootTermination(Process process)
    {
        try
        {
            if (process.HasExited) return false;
            process.Kill(entireProcessTree: false);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }
}

public sealed record ProcessObservationPolicy(TimeSpan MaximumWait, TimeSpan AfterCancellation)
{
    internal static readonly ProcessObservationPolicy Direct = new(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(3));
    internal static readonly ProcessObservationPolicy Maintenance = new(TimeSpan.FromMinutes(90), TimeSpan.FromSeconds(30));
    internal static readonly ProcessObservationPolicy Uninstall = new(TimeSpan.FromMinutes(45), TimeSpan.FromSeconds(20));
}
