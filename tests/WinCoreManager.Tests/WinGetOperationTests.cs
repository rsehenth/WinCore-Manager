using System.Windows.Threading;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public class WinGetOperationTests
{
    internal static string BootBefore => DateTime.UnixEpoch.AddDays(1).Ticks.ToString();
    internal static string BootAfter => DateTime.UnixEpoch.AddDays(3).Ticks.ToString();
    internal static WinGetInterruptionStore ReviewStore(TestSettings settings, Func<string?> readBoot) =>
        new(settings.DataDirectory, readBoot, () => DateTime.UnixEpoch.AddDays(2));

    [Theory]
    [InlineData(3010, null)]
    [InlineData(1641, "2.0")]
    [InlineData(unchecked((int)0x8A150109), null)]
    [InlineData(unchecked((int)0x8A15010A), "1.0")]
    [InlineData(unchecked((int)0x8A15010B), null)]
    [InlineData(1618, "2.0")]
    [InlineData(unchecked((int)0x8A150102), "1.0")]
    [InlineData(0, null)]
    public Task Audit12H01_reboot_without_inventory_holds_the_queue_and_receipt(int exitCode, string? version) => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        var service = new WinGetOperationFixture(2)
        {
            Version = _ => version,
            Run = (_, _, _) => Task.FromResult(new ProcessRunResult { ExitCode = exitCode }),
        };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history, settings, store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Single(service.Launched);
        Assert.Equal(UpdateRowStatus.Idle, vm.Rows[1].Status);
        var expected = exitCode is 1618 or unchecked((int)0x8A150102) or unchecked((int)0x8A15010A)
            ? HistoryStatus.Failure : HistoryStatus.Interrupted;
        Assert.Equal(expected, Assert.Single(history.Records).Status);
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
        Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
    });

    [Fact]
    public Task Audit12H02_reopening_with_unreadable_inventory_preserves_the_receipt() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = BootBefore;
        var store = ReviewStore(settings, () => boot);
        using (var receipt = store.Begin(new WinGetPackage { Id = "Fixture.1", Name = "Fixture 1", AvailableVersion = "2.0" }))
            receipt.RememberRoot(new WinGetProcessIdentity(int.MaxValue, 1));
        boot = BootAfter;
        var service = new WinGetOperationFixture(2) { Version = _ => null };
        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        await vm.RecheckInterruptionAsync();
        Assert.Equal(1, service.InventoryReads);
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
    });

    [Fact]
    public Task Audit12H02_manual_review_requires_a_current_version_and_history() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = BootBefore;
        var store = ReviewStore(settings, () => boot);
        using (var receipt = store.Begin(new() { Id = "Fixture.1", Name = "Fixture", AvailableVersion = "2.0" }))
            receipt.RememberRoot(new(int.MaxValue, 1));
        var service = new WinGetOperationFixture(1) { Version = _ => "1.0" };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history, settings, store);

        await vm.RecheckInterruptionAsync("1.0");
        // The recorded root is verifiably gone in this boot, so the review is offered without a restart,
        // but a version the owner never saw presented cannot release the receipt.
        Assert.True(store.HasReceipt);
        Assert.True(vm.ReviewInterruptionCommand.CanExecute(null));
        boot = BootAfter;
        await vm.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt && vm.ReviewInterruptionCommand.CanExecute(null));
        await vm.RecheckInterruptionAsync("1.2");
        Assert.True(store.HasReceipt);
        Assert.Empty(history.Records);
        service.Version = _ => null;
        await vm.RecheckInterruptionAsync("1.0");
        Assert.True(store.HasReceipt);
        service.Version = _ => "1.0";
        await vm.RecheckInterruptionAsync();
        await vm.RecheckInterruptionAsync("1.0");
        Assert.False(store.HasReceipt || vm.BlocksPackageChanges);
        var resolution = Assert.Single(history.Records);
        Assert.Equal(HistoryStatus.Interrupted, resolution.Status);
        Assert.Equal("1.0", resolution.VersionTo);
    });

    [Theory]
    [InlineData(1602, true, UpdateRowStatus.Cancelled, HistoryStatus.Cancelled)]
    [InlineData(unchecked((int)0x8A15010C), false, UpdateRowStatus.Cancelled, HistoryStatus.Cancelled)]
    [InlineData(9, false, UpdateRowStatus.Failed, HistoryStatus.Failure)]
    public Task Audit12H03_attempt_and_observed_version_are_independent(int code, bool cancelled, UpdateRowStatus rowStatus,
        HistoryStatus historyStatus) => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(1)
        {
            Version = _ => "2.0",
            Run = (_, _, _) => Task.FromResult(new ProcessRunResult { ExitCode = code, Cancelled = cancelled }),
        };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Equal(rowStatus, vm.Rows[0].Status);
        var entry = Assert.Single(history.Records);
        Assert.Equal(historyStatus, entry.Status);
        Assert.Equal("2.0", entry.VersionTo);
        Assert.Equal(code, entry.ExitCode);
        Assert.Contains("2.0", entry.Message);
        Assert.Contains("0 sucesso(s)", vm.StatusText);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Audit12H01_failure_after_launch_stops_when_evidence_is_missing(bool throws) => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        var service = new WinGetOperationFixture(2)
        {
            Version = _ => throw new IOException("synthetic inventory failure"),
            Run = (_, _, _) => throws ? throw new IOException("synthetic failure after launch") :
                Task.FromResult(new ProcessRunResult { ExitCode = 9 }),
        };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history, settings, store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Single(service.Launched);
        Assert.Single(history.Records);
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
        Assert.Equal(UpdateRowStatus.Idle, vm.Rows[1].Status);
    });

    [Fact]
    public Task Audit12H02_failed_identity_write_retains_initial_receipt_and_blocks_concurrent_copy() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory, () => "boot-1");
        var service = new WinGetOperationFixture(2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var file = Path.Combine(settings.DataDirectory, "winget-update.json");
        service.Run = async (_, control, _) =>
        {
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                control.RootIdentity = new(int.MaxValue, 1);
            entered.SetResult();
            await exit.Task;
            return new ProcessRunResult();
        };

        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        var contender = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        await vm.RefreshCommand.ExecuteAsync(null);
        await contender.RefreshCommand.ExecuteAsync(null);
        contender.AllSelected = vm.AllSelected = true;
        var batch = vm.UpdateSelectedCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(contender.UpdateSelectedCommand.CanExecute(null));
            Assert.False(contender.TryReservePackageChange(out _));
            Assert.Throws<IOException>(() => store.Open());
        }
        finally { exit.TrySetResult(); await batch; }

        Assert.Single(service.Launched);
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
        using (var receipt = store.Open())
        {
            Assert.NotNull(receipt.Package);
            Assert.Null(receipt.Identity);
        }
        await vm.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
    });

    [Fact]
    public Task Audit12H02_unreadable_receipt_remains_untouched() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var path = Path.Combine(settings.DataDirectory, "winget-update.json");
        var payload = new string('x', 16_385);
        Directory.CreateDirectory(settings.DataDirectory);
        File.WriteAllText(path, payload);
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        var vm = new UpdatesViewModel(new WinGetOperationFixture(1), new FakeHistoryService(), settings, store);
        await vm.RecheckInterruptionAsync();
        Assert.Equal(payload, File.ReadAllText(path));
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
    });

    [Fact]
    public Task Audit12H08_external_package_operation_retains_guard_until_verified_restart() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = BootBefore;
        var store = ReviewStore(settings, () => boot);
        var service = new WinGetOperationFixture(1);
        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        Assert.True(vm.TryReservePackageChange(out var reservation));
        using (var lease = Assert.IsType<PackageChangeReservation>(reservation))
        {
            Assert.False(new UpdatesViewModel(service, new FakeHistoryService(), settings, store).TryReservePackageChange(out _));
            Assert.True(store.HasReceipt);
        }
        Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
        var reopened = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        await reopened.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt && reopened.BlocksPackageChanges);
        boot = BootAfter;
        await reopened.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt && reopened.ReviewInterruptionCommand.CanExecute(null));
        await reopened.RecheckInterruptionAsync(PackageChangeReview.NoInventory);
        Assert.False(store.HasReceipt || reopened.BlocksPackageChanges);
        Assert.Empty(service.Launched);
    });

    [Fact]
    public Task Queue_survives_a_failure_verifies_inventory_and_allows_retry() => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(3);
        service.Run = (package, _, _) => Task.FromResult(new ProcessRunResult { ExitCode = package.Id == "Fixture.1" ? 9 : 0, Observation = WinGetOperationFixture.Complete });
        service.Version = package => package.Id == "Fixture.1" ? "1.0" : "2.0";
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { UpdateRowStatus.Failed, UpdateRowStatus.Success, UpdateRowStatus.Success }, vm.Rows.Select(row => row.Status));
        Assert.Equal(3, service.InventoryReads);
        Assert.Equal(3, history.Records.Count);
        Assert.False(vm.IsBusy);
        Assert.True(vm.UpdateSelectedCommand.CanExecute(null));

        service.Run = (_, _, _) => Task.FromResult(new ProcessRunResult { ExitCode = 0, Observation = WinGetOperationFixture.Complete });
        service.Version = _ => "2.0";
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.All(vm.Rows, row => Assert.Equal(UpdateRowStatus.Success, row.Status));
        Assert.Equal(new[] { "Fixture.1", "Fixture.2", "Fixture.3", "Fixture.1" }, service.Launched);
    });

    [Fact]
    public Task Cancelling_the_queue_during_a_batch_handles_unstarted_rows_and_repeated_clicks() => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(2);
        service.Run = async (package, _, ct) =>
        {
            if (package.Id != "Fixture.1") return new ProcessRunResult { ExitCode = 0, Observation = WinGetOperationFixture.Complete };
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            return new ProcessRunResult { Cancelled = true, ExitCode = 1602, Observation = WinGetOperationFixture.Complete };
        };
        service.Version = package => package.Id == "Fixture.1" ? "1.0" : "2.0";
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        var run = vm.UpdateSelectedCommand.ExecuteAsync(null);
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Single(service.Launched);
        var cancel = vm.CancelUpdateCommand;
        Assert.True(cancel.CanExecute(null));
        cancel.Execute(null);
        cancel.Execute(null);
        await run;
        Assert.Equal(UpdateRowStatus.Cancelled, vm.Rows[0].Status);
        Assert.Equal(UpdateRowStatus.Idle, vm.Rows[1].Status);
        Assert.Single(history.Records);
        Assert.False(vm.IsBusy);
        Assert.False(cancel.CanExecute(null));
    });

    [Fact]
    public Task Reported_success_requires_inventory_confirmation_and_reopening_preserves_interruption() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var service = new WinGetOperationFixture(2) { Version = _ => "1.0" };
        var history = new FakeHistoryService();
        var boot = BootBefore;
        var store = ReviewStore(settings, () => boot);
        service.Run = (_, control, _) =>
        {
            control.RootIdentity = new(int.MaxValue, 1);
            return Task.FromResult(new ProcessRunResult());
        };
        var vm = new UpdatesViewModel(service, history, settings, store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Equal(UpdateRowStatus.Interrupted, vm.Rows[0].Status);
        Assert.Equal(UpdateRowStatus.Idle, vm.Rows[1].Status);
        Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
        Assert.True(store.HasReceipt);

        var reopened = new UpdatesViewModel(service, history, settings, new WinGetInterruptionStore(settings.DataDirectory, () => boot));
        await reopened.RefreshCommand.ExecuteAsync(null);
        reopened.AllSelected = true;
        Assert.True(reopened.HasInterruptedUpdate && reopened.BlocksPackageChanges);
        Assert.False(reopened.UpdateSelectedCommand.CanExecute(null));
        var readsBeforeRestart = service.InventoryReads;
        // Same boot: the recorded root is verifiably gone, so the review reads inventory without a restart.
        await reopened.RecheckInterruptionAsync();
        Assert.True(reopened.HasInterruptedUpdate && store.HasReceipt);
        Assert.Equal(readsBeforeRestart + 1, service.InventoryReads);
        service.Version = _ => "2.0";
        await reopened.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt && reopened.ReviewInterruptionCommand.CanExecute(null));
        await reopened.RecheckInterruptionAsync("2.0");
        Assert.False(reopened.HasInterruptedUpdate || store.HasReceipt);
        Assert.Equal(readsBeforeRestart + 3, service.InventoryReads);
        reopened.AllSelected = true;
        Assert.True(reopened.UpdateSelectedCommand.CanExecute(null));
    });

    [Fact]
    public async Task Receipt_is_exclusive_while_the_upgrade_is_owned_and_reopen_reads_the_package()
    {
        var settings = new TestSettings();
        var first = new WinGetInterruptionStore(settings.DataDirectory);
        var second = new WinGetInterruptionStore(settings.DataDirectory);
        var package = new WinGetPackage { Id = "Fixture.1", Name = "Fixture 1" };
        using (first.Begin(package))
        {
            Assert.True(second.HasReceipt);
            Assert.Throws<IOException>(() => second.Begin(package));
            Assert.Throws<IOException>(() => second.Open());
            await Task.Yield();
        }
        using (var receipt = second.Open())
        {
            Assert.Equal(package.Id, receipt.Package?.Id);
            receipt.Clear();
        }
        Assert.False(first.HasReceipt);
    }

    [Theory]
    [InlineData("Name         Id           Version\n----------------------------------\nFixture      Fixture.1    2.0", "2.0")]
    [InlineData("Nome         ID           Versão  Disponível  Origem\n----------------------------------------------------\nFixture      Fixture.1    1.0     2.0         winget", "1.0")]
    [InlineData("Name         Id           Version\n----------------------------------\nFixture      Fixture.11   2.0", null)]
    [InlineData("Name         Id           Version\n----------------------------------\nFixture      Fixture.1    2.0\nFixture      Fixture.1    1.0", null)]
    public void Inventory_requires_exact_identity_and_unambiguous_version(string output, string? expected) =>
        Assert.Equal(expected, WingetTableParser.FindInstalledVersion(output, "Fixture.1"));

    [Fact]
    public void Inactivity_warning_setting_survives_restart()
    {
        var settings = new TestSettings();
        var local = Path.Combine(settings.DataDirectory, "local");
        var settingsService = new SettingsService(settings.DataDirectory, local);
        Assert.Equal(120, settingsService.Settings.WinGetInactivitySeconds);
        settingsService.Settings.WinGetInactivitySeconds = 60;
        settingsService.Save();
        Assert.Equal(60, new SettingsService(settings.DataDirectory, local).Settings.WinGetInactivitySeconds);
    }

    [Fact]
    public Task Silent_process_shows_warning_and_does_not_stop_without_user_action() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        settings.Settings.WinGetInactivitySeconds = 1;
        var service = new WinGetOperationFixture(1);
        WinGetProcessControl? processControl = null;
        service.Run = (_, control, token) =>
        {
            processControl = control;
            return ProcessRunner.RunWinGetAsync(WinGetCancellationTests.Fixture("silent"), null, token, control, false);
        };
        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, new WinGetInterruptionStore(settings.DataDirectory));
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        var run = vm.UpdateSelectedCommand.ExecuteAsync(null);
        try
        {
            await WinGetCancellationTests.WaitUntilAsync(() => vm.OperationStalled);
            Assert.False(run.IsCompleted || vm.ShowForce);
            await Dispatcher.Yield(DispatcherPriority.Input);
            Assert.True(vm.IsUpdating && vm.CancelUpdateCommand.CanExecute(null));
        }
        finally
        {
            vm.CancelUpdateCommand.Execute(null);
            await WinGetCancellationTests.WaitUntilAsync(() => vm.ShowForce || run.IsCompleted);
            if (!run.IsCompleted) Assert.True(processControl!.RequestForce());
            await run;
        }
    });
}

internal sealed class WinGetOperationFixture(int count) : IControlledWinGetService
{
    internal static ExternalProcessObservation Complete => new(ControlledProcessState.Exited, DelegatedProcessState.CompletionVerified);
    public Func<WinGetPackage, WinGetProcessControl, CancellationToken, Task<ProcessRunResult>> Run { get; set; } =
        (_, _, _) => Task.FromResult(new ProcessRunResult { Observation = Complete });
    public Func<WinGetPackage, string?> Version { get; set; } = _ => "2.0";
    public List<string> Launched { get; } = [];
    public Action<string>? Output { get; private set; }
    public int InventoryReads { get; private set; }
    public IReadOnlyList<WinGetPackage>? Packages { get; set; }
    public Func<Task>? BeforeListUpdates { get; set; }
    public Func<Task>? BeforeReviewVersion { get; set; }
    public Action<WinGetPackage>? BeforePrepare { get; set; }
    public Task<ProcessRunResult?> PrepareUpgradeAsync(WinGetPackage package, WinGetProcessControl control, CancellationToken token)
    {
        BeforePrepare?.Invoke(package);
        return Task.FromResult<ProcessRunResult?>(null);
    }
    public string? ExecutablePath => null;
    public WinGetAvailability? LastDetection => null;
    public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WinGetAvailability { IsInstalled = true });
    public async Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default)
    {
        if (BeforeListUpdates is { } beforeListUpdates) await beforeListUpdates();
        return new WinGetScanResult
        {
            Packages = Packages ?? Enumerable.Range(1, count).Select(index => new WinGetPackage
            {
                Id = $"Fixture.{index}", Name = $"Fixture {index}", InstalledVersion = "1.0", AvailableVersion = "2.0",
            }).ToArray(),
        };
    }
    public Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
    public Task<ProcessRunResult> UpgradeControlledAsync(WinGetPackage package, Action<string> onOutputLine, WinGetProcessControl control, CancellationToken cancellationToken)
    {
        Launched.Add(package.Id);
        Output = onOutputLine;
        return Run(package, control, cancellationToken);
    }
    public Task<string?> ReadInstalledVersionAsync(WinGetPackage package)
    {
        InventoryReads++;
        return Task.FromResult(Version(package));
    }

    public async Task<string?> ReadReviewVersionAsync(WinGetPackage package)
    {
        if (BeforeReviewVersion is { } beforeReview) await beforeReview();
        return await ReadInstalledVersionAsync(package);
    }
}
