using System.IO;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;
using Xunit;

namespace WinCoreManager.Tests;

// Final integration audit: queue checkpoint between attempts and recovery inventory that cannot correlate.
public class AuditoriaFinalTests
{
    private static ProcessRunResult Finished(WinGetProcessControl control, int exitCode = 0)
    {
        control.RootIdentity = new(int.MaxValue, 1);
        return new ProcessRunResult { ExitCode = exitCode, Observation = WinGetOperationFixture.Complete };
    }

    [Fact]
    public Task Queue_tail_survives_an_abrupt_exit_between_two_attempts_and_resumes_after_review() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
        TestSettings? snapshot = null;
        var service = new WinGetOperationFixture(3)
        {
            Packages = [Package(1), Package(2), Package(3)],
            Run = (_, control, _) => Task.FromResult(Finished(control)),
        };
        service.BeforePrepare = package =>
        {
            // Fixture 1 finished and released its receipt. Fixture 2 has no receipt yet: copy the data as a crash would leave it.
            if (package.Id != "Fixture.2") return;
            snapshot = new TestSettings();
            Directory.CreateDirectory(snapshot.DataDirectory);
            foreach (var file in Directory.GetFiles(settings.DataDirectory))
                File.Copy(file, Path.Combine(snapshot.DataDirectory, Path.GetFileName(file)));
        };
        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Fixture.1", "Fixture.2", "Fixture.3" }, service.Launched);

        // The completed batch leaves nothing to resume.
        var after = new UpdatesViewModel(service, new FakeHistoryService(), settings,
            WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore));
        Assert.False(after.HasSuspendedQueue || after.HasInterruptedUpdate);

        // Reopening after the abrupt exit: no launch, the tail is offered, versions are requeried and reviewed.
        Assert.NotNull(snapshot);
        var restartedStore = WinGetOperationTests.ReviewStore(snapshot, () => WinGetOperationTests.BootBefore);
        Assert.False(restartedStore.HasReceipt);
        var restarted = new WinGetOperationFixture(2)
        {
            Packages = [Package(2), Package(3)],
            Run = (_, control, _) => Task.FromResult(Finished(control)),
        };
        IReadOnlyList<WinGetPackage>? reviewed = null;
        var reopened = new UpdatesViewModel(restarted, new FakeHistoryService(), snapshot, restartedStore)
        {
            ReviewBatch = packages => { reviewed = packages; return true; },
        };
        Assert.False(reopened.HasInterruptedUpdate);
        Assert.Equal(new[] { "Fixture.2", "Fixture.3" }, reopened.PendingBatch.Select(package => package.Id));
        Assert.True(reopened.ShowResumeUpdates);
        Assert.Empty(restarted.Launched);

        await reopened.ResumeUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Fixture.2", "Fixture.3" }, reviewed!.Select(package => package.Id));
        Assert.Equal(new[] { "Fixture.2", "Fixture.3" }, restarted.Launched);
        Assert.False(reopened.HasSuspendedQueue);
        Assert.False(File.Exists(Path.Combine(snapshot.DataDirectory, "winget-update.json.queue")));
    });

    [Fact]
    public Task Last_attempt_and_installer_cancellation_leave_no_stale_queue() => WpfTestThread.Run(async () =>
    {
        foreach (var exitCode in new[] { 0, 1602 })
        {
            var settings = new TestSettings();
            var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
            var queue = Path.Combine(settings.DataDirectory, "winget-update.json.queue");
            var service = new WinGetOperationFixture(2)
            {
                Packages = [Package(1), Package(2)],
                Run = (package, control, _) =>
                {
                    if (package.Id == "Fixture.2") Assert.False(File.Exists(queue));
                    return Task.FromResult(Finished(control, package.Id == "Fixture.1" ? exitCode : 0));
                },
            };
            var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, store);
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.AllSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            // 1602 is a cancellation inside the installer: the batch stops without offering a resume, as in the session.
            Assert.Equal(exitCode == 0 ? 2 : 1, service.Launched.Count);
            Assert.False(store.HasReceipt || vm.HasSuspendedQueue);
            Assert.False(File.Exists(queue));
            var reopened = new UpdatesViewModel(service, new FakeHistoryService(), settings,
                WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore));
            Assert.False(reopened.HasSuspendedQueue || reopened.HasInterruptedUpdate);
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_all_users_install_keeps_machine_scope_on_upgrade(bool machine)
    {
        const string official = """
            {"Name":"winget","Arg":"https://cdn.winget.microsoft.com/cache","Type":"Microsoft.PreIndexed.Package","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","TrustLevel":["Trusted","StoreOrigin"]}
            """;
        System.Diagnostics.ProcessStartInfo? upgrade = null;
        var service = new WinGetService(() => @"C:\Fixture\winget.exe", (start, _, _, _, _) =>
        {
            if (start.ArgumentList[0] == "source") return Task.FromResult(new ProcessRunResult { Output = [official] });
            if (start.ArgumentList[0] == "upgrade") upgrade = start;
            return Task.FromResult(new ProcessRunResult { Observation = WinGetOperationFixture.Complete });
        }, () => false, readMachineScope: package => machine && package.Id == "ArtCraft.DeckCraft");
        var deckCraft = new WinGetPackage
        {
            Name = "DeckCraft", Id = "ArtCraft.DeckCraft", Source = "winget", InstalledVersion = "0.18.1", AvailableVersion = "0.19.1",
        };
        await ((IControlledWinGetService)service).UpgradeControlledAsync(deckCraft, _ => { }, new WinGetProcessControl(), default);
        var arguments = Assert.IsType<System.Diagnostics.ProcessStartInfo>(upgrade).ArgumentList.ToArray();
        Assert.Equal(machine, arguments.Contains("--scope"));
        if (machine) Assert.Equal("machine", arguments[Array.IndexOf(arguments, "--scope") + 1]);
        Assert.DoesNotContain(arguments, argument => argument.Contains("ALLUSERS", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("< 4.0.2.65535")]
    [InlineData("> 4.0.2.65535")]
    public void Range_installed_version_is_unknown_and_not_offered(string installed)
    {
        // Unity Hub: WinGet maps the Hub and the Unity Editor to one ID and only shows a range.
        var row = new UpdateItemRow(new WinGetPackage
        {
            Name = "Unity Hub", Id = "Unity.UnityHub", Source = "winget", InstalledVersion = installed, AvailableVersion = "4.0.2.65535",
        });
        Assert.False(row.CanSelect);
        Assert.Equal(WinCoreManager.Localization.LocalizationService.Source("UpdateOperation.UnknownInstalledVersion"), row.VersionWarning);
        Assert.Null(WinGetService.BuildUpgradeStartInfo(@"C:\Fixture\winget.exe", row.Package));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Lingering_uninstaller_receipt_is_released_after_one_confirmation_when_the_entry_left(bool stillListed) =>
        WpfTestThread.Run(async () =>
    {
        // Auslogics receipt: root exited 0, entry gone, _iu14D2N.tmp alive with no window. The test process plays it.
        var settings = new TestSettings();
        var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        using (var receipt = store.BeginExternal(PackageChangeKind.Uninstall, "Synthetic Inno App"))
        {
            receipt.MarkUninstallLaunchPending("Synthetic Inno App");
            receipt.RememberRoot(new(int.MaxValue, 1));
            receipt.RememberDelegates([new((uint)current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc())]);
            receipt.RecordOutcome(new ProcessRunResult
            {
                ExitCode = 0, HasExitCode = true, Interrupted = true,
                Observation = new(ControlledProcessState.Exited, DelegatedProcessState.KnownActive),
            });
        }
        var original = PackageChangeReview.UninstallEntryListed;
        PackageChangeReview.UninstallEntryListed = name => name == "Synthetic Inno App" ? stillListed : original(name);
        try
        {
            var history = new FakeHistoryService();
            var vm = new UpdatesViewModel(new WinGetOperationFixture(0), history, settings, store)
            {
                ConfirmInterruptionReview = (_, _) => true,
            };
            Assert.True(vm.BlocksPackageChanges);
            await vm.DismissWarningCommand.ExecuteAsync(null);
            Assert.Equal(stillListed, store.HasReceipt);
            Assert.Equal(stillListed, vm.BlocksPackageChanges);
            if (stillListed) Assert.Empty(history.Records);
            else Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
            Assert.False(current.HasExited); // released, never terminated
        }
        finally { PackageChangeReview.UninstallEntryListed = original; }
    });

    private static WinGetPackage Package(int number) => new()
    {
        Name = $"Fixture {number}", Id = $"Fixture.{number}", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "2.0",
    };
}
