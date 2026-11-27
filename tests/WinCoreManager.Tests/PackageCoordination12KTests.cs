using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public sealed class PackageCoordination12KTests
{
    [Fact]
    public Task QuietDelegateMustKeepReceiptAndStopSecondLaunch() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        var service = new WinGetOperationFixture(2);
        var history = new FakeHistoryService();
        var marker = Path.Combine(settings.DataDirectory, "quiet-child");
        var release = marker + ".release";
        ProcessRunResult? observed = null;
        service.Run = async (_, control, token) =>
        {
            control.DelegateSettleWindow = TimeSpan.FromMilliseconds(500); // delegate outlives the supervised wait
            return observed = await ProcessRunner.RunWinGetAsync(Fixture("quiet-delegate", marker, release), null, token, control);
        };
        var vm = new UpdatesViewModel(service, history, settings, store);
        try
        {
            await RunQueueAsync(vm);
            using var child = ReadChild(marker);
            Assert.False(child.HasExited);
            Assert.NotNull(observed);
            Assert.Equal(0, observed.ExitCode);
            Assert.Equal(ControlledProcessState.Exited, observed.Observation.ControlledProcesses);
            Assert.Equal(DelegatedProcessState.KnownActive, observed.Observation.Delegation);
            Assert.Single(service.Launched);
            Assert.Equal(UpdateRowStatus.Idle, vm.Rows[1].Status);
            Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
            Assert.True(store.HasReceipt);
            using var receipt = store.Open();
            Assert.Contains(receipt.Delegates, process => process.Pid == child.Id);
        }
        finally { File.WriteAllText(release, ""); }
    });

    [Theory]
    [InlineData("quiet-controlled", false)]
    [InlineData("quiet-delegate", true)]
    public async Task ProcessExitAndDelegationRemainSeparate(string mode, bool delegationPossible)
    {
        var settings = new TestSettings();
        var marker = Path.Combine(settings.DataDirectory, "child");
        Directory.CreateDirectory(settings.DataDirectory);
        var release = marker + ".release";
        var control = new WinGetProcessControl { DelegateSettleWindow = TimeSpan.FromMilliseconds(500) };
        var run = ProcessRunner.RunWinGetAsync(Fixture(mode, marker, release), null, default, control, delegationPossible);
        try
        {
            await WaitForFileAsync(marker);
            using var child = ReadChild(marker);
            Assert.False(child.HasExited);
            if (delegationPossible)
                Assert.Equal(DelegatedProcessState.KnownActive, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Observation.Delegation);
            else
                Assert.False(run.IsCompleted);
            File.WriteAllText(release, "");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!delegationPossible, result.Observation.IsConclusive);
            Assert.False(control.ObserveDelegates());
        }
        finally { File.WriteAllText(release, ""); }
    }

    [Fact]
    public Task ConclusiveExitWithoutCapturedIdentityMustNotEraseReceipt() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        var service = new WinGetOperationFixture(2);
        var history = new FakeHistoryService();
        await RunQueueAsync(new UpdatesViewModel(service, history, settings, store));
        Assert.Single(service.Launched);
        Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
        using var receipt = store.Open();
        Assert.NotNull(receipt.OperationId);
        Assert.Null(receipt.Identity);
    });

    [Fact]
    public Task CompletionWithCapturedIdentityAndBoundedExecutionAdvances() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        var service = new WinGetOperationFixture(2)
        {
            Run = (_, control, _) =>
            {
                control.RootIdentity = new(int.MaxValue, 1);
                return Task.FromResult(new ProcessRunResult { Observation = WinGetOperationFixture.Complete });
            }
        };
        var history = new FakeHistoryService();
        await RunQueueAsync(new UpdatesViewModel(service, history, settings, store));
        Assert.Equal(2, service.Launched.Count);
        Assert.All(history.Records, record => Assert.Equal(HistoryStatus.Success, record.Status));
        Assert.False(store.HasReceipt);
    });

    [Fact]
    public Task InitialIntentWriteFailurePreventsLaunch() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var path = Path.Combine(settings.DataDirectory, "winget-update.json");
        var store = new WinGetInterruptionStore(settings.DataDirectory, () =>
        {
            Directory.CreateDirectory(path); // Obstruct the initial rename after acquisition of the lease.
            return null;
        });
        var service = new WinGetOperationFixture(1);
        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        await RunQueueAsync(vm);
        Assert.Empty(service.Launched);
        Directory.Delete(path);
        vm.RefreshInterruptionState();
        Assert.False(vm.TryReservePackageChange(out _));
        Assert.True(Directory.Exists(path));
    });

    [Fact]
    public async Task AppDepartureBeforeExternalObservationFinishesMustLeaveGuard() =>
        await VerifyCrossProcessAsync("abandon");

    [Fact]
    public async Task FailedExternalReceiptWriteMustBlockFreshInstance() =>
        await VerifyCrossProcessAsync("fail-identity");

    [Fact]
    public async Task SimultaneousReservationsAllowExactlyOneProcessToCommit()
    {
        var settings = new TestSettings();
        var root = Directory.CreateDirectory(settings.DataDirectory).FullName;
        using var first = Process.Start(Fixture("receipt-worker", "compete", root, "first"))!;
        using var second = Process.Start(Fixture("receipt-worker", "compete", root, "second"))!;
        try
        {
            await Task.WhenAll(WaitForFileAsync(Path.Combine(root, "first")), WaitForFileAsync(Path.Combine(root, "second")));
            File.WriteAllText(Path.Combine(root, "race-start"), "1");
            var firstExit = first.WaitForExitAsync();
            var secondExit = second.WaitForExitAsync();
            await (await Task.WhenAny(firstExit, secondExit).WaitAsync(TimeSpan.FromSeconds(6)));
            File.WriteAllText(Path.Combine(root, "race-finish"), "1");
            await Task.WhenAll(firstExit, secondExit).WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(new[] { 0, 3 }, new[] { first.ExitCode, second.ExitCode }.Order());
            using var receipt = new WinGetInterruptionStore(root).Open();
            Assert.Equal(first.ExitCode == 0 ? "first" : "second", receipt.Target);
        }
        finally { File.WriteAllText(Path.Combine(root, "race-finish"), "1"); }
    }

    [Fact]
    public Task ATruncatedIntentCannotBeReclassifiedOrRemoved() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        using (store.BeginExternal(PackageChangeKind.Maintenance, "Fixture")) { }
        var path = Path.Combine(settings.DataDirectory, "winget-update.json");
        var truncated = File.ReadAllLines(path)[..4];
        File.WriteAllLines(path, truncated);
        boot = WinGetOperationTests.BootAfter;
        var vm = new UpdatesViewModel(new WinGetOperationFixture(1), new FakeHistoryService(), settings, store);
        await vm.RecheckInterruptionAsync();
        Assert.False(vm.ShowInterruptionReview);
        Assert.False(vm.TryReservePackageChange(out _));
        Assert.Equal(truncated, File.ReadAllLines(path));
    });

    [Fact]
    public Task ManualReviewDeniesActiveDelegateAfterSimulatedRestart() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        var marker = Path.Combine(settings.DataDirectory, "child");
        var release = marker + ".release";
        Directory.CreateDirectory(settings.DataDirectory);
        using var child = Process.Start(Fixture("wait-for-release", marker, release))!;
        try
        {
            await WaitForFileAsync(marker);
            using (var receipt = store.Begin(new WinGetPackage { Id = "Fixture.1", Name = "Fixture", AvailableVersion = "2.0" }))
            {
                receipt.RememberRoot(new(int.MaxValue, 1));
                receipt.RememberDelegates([new((uint)child.Id, child.StartTime.ToUniversalTime().ToFileTimeUtc())]);
            }
            boot = WinGetOperationTests.BootAfter;
            var history = new FakeHistoryService();
            var vm = new UpdatesViewModel(new WinGetOperationFixture(1), history, settings, store);
            await vm.RecheckInterruptionAsync();
            await vm.RecheckInterruptionAsync("2.0");
            Assert.False(child.HasExited);
            Assert.False(vm.ShowInterruptionReview);
            Assert.True(store.HasReceipt);
            Assert.Empty(history.Records);

            File.WriteAllText(release, "");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await vm.RecheckInterruptionAsync();
            Assert.True(vm.ShowInterruptionReview && store.HasReceipt);
            await vm.RecheckInterruptionAsync("2.0");
            Assert.False(store.HasReceipt);
            Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
        }
        finally { File.WriteAllText(release, ""); }
    });

    [Fact]
    public async Task SourceChangedAfterExportIsDeniedAtLaunchCheck()
    {
        var exports = 0;
        var applied = false;
        var service = new WinGetService(() => "WinGetFixture.exe", (start, _, _, _, _) =>
        {
            if (start.ArgumentList[0] != "source")
            {
                applied = true;
                return Task.FromResult(new ProcessRunResult());
            }
            var endpoint = ++exports == 1 ? "https://cdn.winget.microsoft.com/cache" : "https://changed.invalid/cache";
            return Task.FromResult(new ProcessRunResult { Output = [
                "{\"Name\":\"winget\",\"Arg\":\"" + endpoint + "\",\"Type\":\"Microsoft.PreIndexed.Package\","
                + "\"Identifier\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\",\"Data\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\","
                + "\"TrustLevel\":[\"Trusted\",\"StoreOrigin\"]}"
            ] });
        }, () => false);
        var result = await service.UpgradePackageAsync(new WinGetPackage
            { Name = "Fixture", Id = "Fixture.1", AvailableVersion = "2.0", Source = "winget" }, null, default);
        Assert.Equal(2, exports);
        Assert.False(applied);
        Assert.True(result.StartFailed);
    }

    private static async Task VerifyCrossProcessAsync(string action)
    {
        var settings = new TestSettings();
        var root = settings.DataDirectory;
        var path = Path.Combine(root, "winget-update.json");
        var store = new WinGetInterruptionStore(root);
        using var owner = Process.Start(Fixture("receipt-worker", action, root))!;
        try
        {
            var ready = Path.Combine(root, "ready");
            await WaitForFileAsync(ready);
            using var child = ReadChild(Path.Combine(root, "child"));
            var beforeDeparture = File.ReadAllBytes(path);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(WinGetService).Assembly.Location))),
                WinGetCancellationTests.ReadFixtureFile(ready));
            await AssertContenderBlockedAsync(root);
            File.WriteAllText(Path.Combine(root, "depart"), "1");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Equal(0, owner.ExitCode);
            Assert.False(child.HasExited);
            Assert.Equal(beforeDeparture, File.ReadAllBytes(path));
            await AssertContenderBlockedAsync(root);
            using var receipt = store.Open();
            Assert.Equal(PackageChangeKind.Maintenance, receipt.Kind);
            Assert.NotNull(receipt.OperationId);
            Assert.Null(receipt.Identity);
        }
        finally
        {
            if (!owner.HasExited) File.WriteAllText(Path.Combine(root, "depart"), "1");
            File.WriteAllText(Path.Combine(root, "release"), "");
        }
    }

    private static async Task AssertContenderBlockedAsync(string root)
    {
        using var contender = Process.Start(Fixture("receipt-worker", "contend", root))!;
        await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(0, contender.ExitCode);
    }

    private static async Task RunQueueAsync(UpdatesViewModel vm)
    {
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
    }

    private static ProcessStartInfo Fixture(string command, params string[] arguments)
    {
        var start = WinGetCancellationTests.Fixture(command);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static Process ReadChild(string marker) =>
        Process.GetProcessById(int.Parse(WinGetCancellationTests.ReadFixtureFile(marker), CultureInfo.InvariantCulture));

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!File.Exists(path) || new FileInfo(path).Length == 0) await Task.Delay(20, timeout.Token);
    }
}
