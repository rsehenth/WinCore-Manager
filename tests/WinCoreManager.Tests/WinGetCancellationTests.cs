using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class WinGetCancellationTests
{
    internal static ProcessStartInfo Fixture(string mode) => new()
    {
        FileName = Path.Combine(AppContext.BaseDirectory, "WinGetFixture", "WinGetFixture.exe"),
        ArgumentList = { mode },
    };

    [Fact]
    public async Task Upgrade_without_a_control_session_returns_uncertain_without_killing()
    {
        using var cancellation = new CancellationTokenSource();
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ProcessRunner.RunWinGetAsync(Fixture("never"), line =>
        {
            if (line.StartsWith("ready ")) ready.TrySetResult(int.Parse(line[6..]));
        }, cancellation.Token);
        using var fixture = Process.GetProcessById(await ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        try
        {
            cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(result.Cancelled);
            Assert.True(result.Interrupted);
            Assert.False(result.HasExitCode);
            Assert.False(fixture.HasExited);
        }
        finally
        {
            if (!fixture.HasExited) fixture.Kill();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Natural_exit_collects_stdout_stderr_and_exit_code()
    {
        var result = await ProcessRunner.RunWinGetAsync(Fixture("success"), null, default, new()).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(result.Interrupted, result.FullOutput + result.StartError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Successfully installed", result.Output);
        Assert.Contains("fixture stderr", result.Errors);
    }

    [Fact]
    public async Task Hidden_process_gets_eof_without_an_input_or_cancel_action()
    {
        var result = await ProcessRunner.RunWinGetAsync(Fixture("stdin"), null, default, new(),
            externalInstallerPossible: false).WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Contains("waiting for input", result.Output);
        Assert.Contains("stdin closed", result.Output);
        Assert.True(result.HasExitCode && !result.Interrupted);
    }

    [Theory]
    [InlineData("silent")]
    [InlineData("never")]
    [InlineData("pipes-out")]
    [InlineData("pipes-err")]
    public async Task Cancel_waits_for_explicit_force_then_confirms_process_exit(string mode)
    {
        using var cts = new CancellationTokenSource();
        var control = new WinGetProcessControl();
        var run = ProcessRunner.RunWinGetAsync(Fixture(mode), null, cts.Token, control, externalInstallerPossible: false);
        await WaitUntilAsync(() => control.State == WinGetControlState.Running);
        cts.Cancel();
        cts.Cancel();
        await WaitUntilAsync(() => control.State == WinGetControlState.NeedsDecision);
        Assert.False(run.IsCompleted);
        Assert.True(control.RequestForce());
        _ = control.RequestForce();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
        AssertControlledForce(result);
        Assert.Equal(1223, result.ExitCode);
        Assert.Equal(WinGetControlState.Finished, control.State);
        Assert.False(control.RequestForce());
    }

    [Theory]
    [InlineData("child")]
    [InlineData("orphan")]
    public async Task Force_stops_job_members_including_an_orphan_and_leaves_external_process_alone(string mode)
    {
        using var cts = new CancellationTokenSource();
        using var external = Process.Start(Fixture("silent"))!;
        try
        {
            var control = new WinGetProcessControl();
            var childId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = ProcessRunner.RunWinGetAsync(Fixture(mode), line =>
            {
                if (line.StartsWith("child ")) childId.TrySetResult(int.Parse(line[6..]));
            }, cts.Token, control, externalInstallerPossible: false);
            using var child = Process.GetProcessById(await childId.Task.WaitAsync(TimeSpan.FromSeconds(8)));
            _ = child.SafeHandle;
            try
            {
                if (mode == "orphan")
                    await WaitUntilAsync(() => control.RootIdentity is { } root &&
                        !WinGetNative.OriginalProcessMayStillBeActive(root, out _));
                Assert.False(run.IsCompleted);
                Assert.False(control.RequestForce());
                cts.Cancel();
                await WaitUntilAsync(() => control.State == WinGetControlState.NeedsDecision);
                Assert.True(control.RequestForce());
                var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
                AssertControlledForce(result);
                Assert.Equal(mode == "orphan" ? 0 : 1223, result.ExitCode);
                Assert.True(child.HasExited);
                Assert.False(external.HasExited);
            }
            finally { if (!child.HasExited) child.Kill(); }
        }
        finally { if (!external.HasExited) external.Kill(); }
    }

    [Fact]
    public async Task Detach_reports_uncertainty_and_leaves_own_process_running()
    {
        using var cts = new CancellationTokenSource();
        var control = new WinGetProcessControl();
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ProcessRunner.RunWinGetAsync(Fixture("never"), line =>
        {
            if (line.StartsWith("ready ")) ready.TrySetResult(int.Parse(line[6..]));
        }, cts.Token, control);
        using var fixture = Process.GetProcessById(await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        try
        {
            Assert.False(control.RequestDetach());
            cts.Cancel();
            await WaitUntilAsync(() => control.State == WinGetControlState.NeedsDecision);
            Assert.True(control.RequestDetach());
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result.Interrupted);
            Assert.False(result.Cancelled || result.HasExitCode || fixture.HasExited);
        }
        finally { if (!fixture.HasExited) fixture.Kill(); }
    }

    [Fact]
    public async Task Escaped_child_inherits_pipe_but_is_not_terminated_or_reported_as_cancelled()
    {
        var childId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = ProcessRunner.RunWinGetAsync(Fixture("escaped"), line =>
        {
            if (line.StartsWith("escaped ")) childId.TrySetResult(int.Parse(line[8..]));
        }, default, new());
        await Task.WhenAny(run, childId.Task).WaitAsync(TimeSpan.FromSeconds(8));
        if (!childId.Task.IsCompleted)
        {
            var failed = await run;
            Assert.Fail(failed.FullOutput + failed.StartError);
        }
        using var child = Process.GetProcessById(await childId.Task);
        try
        {
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result.Interrupted, result.FullOutput + result.StartError);
            Assert.True(result.HasExitCode);
            Assert.False(result.Cancelled || child.HasExited);
        }
        finally { if (!child.HasExited) child.Kill(); }
    }

    [Fact]
    public void Native_termination_refuses_a_handle_without_terminate_access()
    {
        using var fixture = Process.Start(Fixture("silent"))!;
        try
        {
            using var handle = OpenProcess(0x1000, false, fixture.Id);
            Assert.False(handle.IsInvalid);
            Assert.Equal(5, Assert.Throws<System.ComponentModel.Win32Exception>(() => WinGetNative.TerminateRoot(handle)).NativeErrorCode);
            Assert.False(fixture.HasExited);
        }
        finally { if (!fixture.HasExited) fixture.Kill(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [Fact]
    public async Task Continuous_output_is_drained_with_a_bounded_tail()
    {
        var result = await ProcessRunner.RunWinGetAsync(Fixture("flood"), null, default, new()).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Interrupted);
        Assert.Equal(600, result.Output.Count);
        Assert.Contains("stdout 1999", result.Output);
        Assert.Equal(600, result.Errors.Count);
    }

    [Fact]
    public async Task Cancelled_before_start_does_not_resolve_or_start_the_executable()
    {
        var result = await ProcessRunner.RunWinGetAsync(new(), null, new CancellationToken(true), new());
        Assert.True(result.Cancelled && result.StartFailed);
        Assert.False(result.HasExitCode);
    }

    [Fact]
    public async Task Cancel_during_durable_launch_intent_prevents_process_creation()
    {
        using var cts = new CancellationTokenSource();
        var control = new WinGetProcessControl { PersistLaunchIntent = cts.Cancel };
        var result = await ProcessRunner.RunWinGetAsync(Fixture("success"), null, cts.Token, control);

        Assert.True(result.Cancelled && result.StartFailed);
        Assert.False(result.HasExitCode || result.Interrupted);
        Assert.Null(control.RootIdentity);
    }

    [Fact]
    public async Task Native_access_denied_is_preserved()
    {
        var directory = Directory.CreateTempSubdirectory("wcm-access-");
        try
        {
            var invalid = Directory.CreateDirectory(Path.Combine(directory.FullName, "denied.exe"));
            var result = await ProcessRunner.RunWinGetAsync(new() { FileName = invalid.FullName }, null, default, new());
            Assert.True(result.StartFailed);
            Assert.Equal(5, result.StartErrorCode);
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public async Task Natural_exit_racing_cancel_remains_a_natural_result_without_force()
    {
        for (var attempt = 0; attempt < 8; ++attempt)
        {
            using var cts = new CancellationTokenSource();
            var run = ProcessRunner.RunWinGetAsync(Fixture("brief"), line => { if (line == "ready") cts.Cancel(); }, cts.Token, new());
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result.HasExitCode, result.FullOutput + result.StartError);
            Assert.Equal(0, result.ExitCode);
            Assert.False(result.Cancelled || result.Interrupted);
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition() && deadline.Elapsed < TimeSpan.FromSeconds(8)) await Task.Delay(20);
        Assert.True(condition());
    }

    // A fixture process may still hold the file it is writing: a sharing violation means "not ready yet".
    internal static string ReadFixtureFile(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var expired = deadline.Elapsed > TimeSpan.FromSeconds(5);
            try
            {
                if (File.ReadAllText(path) is { Length: > 0 } text) return text;
                if (expired) throw new IOException($"Fixture file stayed empty: {path}");
            }
            catch (IOException) when (!expired) { }
            Thread.Sleep(20);
        }
    }

    [Fact]
    public async Task Fixture_file_read_waits_for_its_writer_to_close()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wincore-fixture-{Guid.NewGuid():N}");
        try
        {
            // Same sharing mode as File.WriteAllText while it is still writing.
            var writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
            var read = Task.Run(() => ReadFixtureFile(path));
            await Task.Delay(200);
            Assert.False(read.IsCompleted);
            writer.Write("4242");
            writer.Dispose();
            Assert.Equal("4242", await read);
        }
        finally { File.Delete(path); }
    }

    private static void AssertControlledForce(ProcessRunResult result)
    {
        Assert.True(result.HasExitCode, result.FullOutput + result.StartError);
        if (result.Observation.Delegation == DelegatedProcessState.KnownActive)
        {
            Assert.False(result.Observation.IsConclusive);
            Assert.True(result.Cancelled || result.Interrupted, result.FullOutput);
        }
        else
        {
            Assert.True(result.Cancelled && !result.Interrupted, result.FullOutput + result.StartError);
        }
    }
}
