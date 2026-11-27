using System.Diagnostics;
using System.IO;
using System.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

// Opus structural review: one completion contract for every installer technology, applied to live
// runs, uninstallers and same-session recovery alike. Package-specific profiles only add evidence.
public sealed class GenericCompletionContractTests
{
    private const ControlledProcessState Exited = ControlledProcessState.Exited;
    private const DelegatedProcessState Possible = DelegatedProcessState.Possible;

    private static ProcessRunResult Run(int exit = 0, ControlledProcessState controlled = Exited,
        DelegatedProcessState delegation = Possible) => new() { ExitCode = exit, Observation = new(controlled, delegation) };

    [Theory]
    [InlineData(0, "Exited", "Possible", "2.0", true, "Unspecified")]
    [InlineData(0, "Exited", "CompletionVerified", "2.0", true, "Unspecified")]
    [InlineData(9, "Exited", "Possible", null, true, "Unspecified")]
    [InlineData(0, "Exited", "Possible", "1.0", false, "InventoryMismatch")]
    [InlineData(0, "Exited", "Possible", null, false, "InventoryUnavailable")]
    [InlineData(0, "Exited", "KnownActive", "2.0", false, "Active")]
    [InlineData(0, "Active", "Possible", "2.0", false, "Active")]
    [InlineData(0, "NotObserved", "Possible", "2.0", false, "TerminationUnverified")]
    [InlineData(3010, "Exited", "Possible", "2.0", false, "RestartRequired")]
    [InlineData(1618, "Exited", "Possible", "1.0", false, "Active")]
    public void Possible_delegation_alone_never_holds_the_queue(int exit, string controlled, string delegation,
        string? observed, bool advances, string expectedReason)
    {
        var reason = Enum.Parse<WinGetPendingReason>(expectedReason);
        var decision = WinGetAttemptDecision.Evaluate(Run(exit, Enum.Parse<ControlledProcessState>(controlled),
            Enum.Parse<DelegatedProcessState>(delegation)), observed, "2.0",
            verifyInventory: true, identityPersisted: true);
        Assert.Equal(advances, decision.CanAdvance);
        Assert.Equal(reason, decision.PendingReason);
        if (advances) Assert.Equal(exit == 0 ? WinGetAttemptStatus.Succeeded : WinGetAttemptStatus.Failed, decision.Status);
    }

    [Fact]
    public void Missing_identity_still_keeps_the_receipt()
    {
        var decision = WinGetAttemptDecision.Evaluate(Run(), "2.0", "2.0", verifyInventory: true, identityPersisted: false);
        Assert.Equal(WinGetPendingReason.ReceiptIncomplete, decision.PendingReason);
    }

    [Theory]
    [InlineData("possible", true)]
    [InlineData("restart", false)]
    [InlineData("other-boot", false)]
    [InlineData("no-identity", false)]
    public void Same_session_review_needs_verifiable_processes_not_a_restart(string scenario, bool eligible)
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        using (var receipt = store.Begin(GitHubCli()))
        {
            if (scenario != "no-identity") receipt.RememberRoot(new(int.MaxValue, 1));
            receipt.RecordOutcome(Run(scenario == "restart" ? 3010 : 0), WinGetPendingReason.DelegationUnverified);
        }
        if (scenario == "other-boot") boot = WinGetOperationTests.BootAfter;
        using var review = store.OpenForReview();
        var activity = PackageChangeReview.ObserveActivity(review, boot);
        Assert.Equal(eligible, PackageChangeReview.CanReviewWithoutRestart(review, boot, activity));
    }

    // The real 14E receipt shape: GitHub CLI 2.105.0 -> 2.106.0, exit 0, processes ended,
    // delegation "possible", seven queued items. No restart and no package-specific verifier.
    [Fact]
    public Task Legacy_receipt_is_closed_in_the_same_session_and_resume_stays_explicit() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        Guid id;
        using (var receipt = store.Begin(GitHubCli()))
        {
            receipt.RememberRoot(new(int.MaxValue, 1));
            receipt.RecordOutcome(Run(), WinGetPendingReason.DelegationUnverified);
            id = receipt.OperationId!.Value;
        }
        var queue = Enumerable.Range(1, 7).Select(i => new WinGetPackage
        {
            Id = $"Fixture.{i}", Name = $"Fixture {i}", InstalledVersion = "1.0", AvailableVersion = "2.0", Source = "winget",
        }).ToArray();
        Assert.True(store.SaveSuspendedQueue(id, queue));
        var service = new WinGetOperationFixture(7) { Version = package => package.Id == "GitHub.cli" ? "2.106.0" : "1.0" };
        var history = new FakeHistoryService();
        var confirmations = 0;
        var vm = new UpdatesViewModel(service, history, settings, store)
        {
            ConfirmInterruptionReview = (_, _) => { confirmations++; return true; },
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.HasInterruptedUpdate && vm.BlocksPackageChanges);

        await vm.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt);
        Assert.Empty(history.Records);
        Assert.True(vm.ReviewInterruptionCommand.CanExecute(null));

        await vm.ReviewInterruptionCommand.ExecuteAsync(null);
        Assert.Equal(1, confirmations);
        Assert.False(store.HasReceipt || vm.HasInterruptedUpdate || vm.BlocksPackageChanges);
        var record = Assert.Single(history.Records);
        Assert.Equal(HistoryStatus.Success, record.Status);
        Assert.Equal("2.106.0", record.VersionTo);
        Assert.Empty(service.Launched);
        Assert.Equal(7, vm.SuspendedUpdateCount);
        Assert.True(vm.ResumeUpdatesCommand.CanExecute(null));
    });

    [Fact]
    public void Uninstall_reservation_releases_on_the_generic_contract()
    {
        var settings = new TestSettings();
        var store = new WinGetInterruptionStore(settings.DataDirectory);
        using var reservation = new PackageChangeReservation(store.BeginExternal(PackageChangeKind.Uninstall, "App"), () => { });
        reservation.Control.RootIdentity = new(int.MaxValue, 1);
        Assert.True(reservation.CanAdvance(Run()));
        Assert.False(reservation.CanAdvance(Run(delegation: DelegatedProcessState.KnownActive)));
        Assert.False(reservation.CanAdvance(Run(3010)));
        Assert.False(reservation.CanAdvance(new ProcessRunResult { Interrupted = true, HasExitCode = false }));
    }

    [Fact]
    public async Task Observed_runner_supervises_a_known_delegate_until_it_exits()
    {
        var settings = new TestSettings();
        Directory.CreateDirectory(settings.DataDirectory);
        var marker = Path.Combine(settings.DataDirectory, "delegate");
        var release = marker + ".release";
        // An ordinary child (no breakaway), like an NSIS uninstaller relaunching itself from %TEMP%.
        // Breakaway would also depend on the test host's own Job allowing it.
        var start = WinGetCancellationTests.Fixture("quiet-controlled");
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add(release);
        var control = new WinGetProcessControl();
        var run = ProcessRunner.RunObservedAsync(start, null, null, default, terminateOnCancellation: false,
            observation: new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1)), control: control);
        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                while (!File.Exists(marker) || new FileInfo(marker).Length == 0) await Task.Delay(20, timeout.Token);
            await Task.Delay(1500);
            Assert.False(run.IsCompleted);
        }
        finally { File.WriteAllText(release, "release"); }
        var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(result.HasExitCode && !result.Interrupted);
        Assert.NotEqual(DelegatedProcessState.KnownActive, result.Observation.Delegation);
    }

    // Owner report (Maxon App): WinGet failed while the old uninstaller, started outside the Job, was still
    // finishing. The runner must supervise that delegate so the failure is judged on its real outcome.
    [Fact]
    public async Task WinGet_runner_waits_for_a_known_delegate_that_finishes_after_winget()
    {
        var settings = new TestSettings();
        Directory.CreateDirectory(settings.DataDirectory);
        var marker = Path.Combine(settings.DataDirectory, "delegate");
        var release = marker + ".release";
        var start = WinGetCancellationTests.Fixture("quiet-delegate");
        start.ArgumentList.Add(marker);
        start.ArgumentList.Add(release);
        var lines = new List<string>();
        var control = new WinGetProcessControl { DelegateSettleWindow = TimeSpan.FromSeconds(20) };
        var run = ProcessRunner.RunWinGetAsync(start, line => { lock (lines) lines.Add(line); }, default, control);
        try
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                while (!File.Exists(marker) || new FileInfo(marker).Length == 0) await Task.Delay(20, timeout.Token);
            await Task.Delay(1500);
            Assert.False(run.IsCompleted);
        }
        finally { File.WriteAllText(release, "release"); }
        var result = await run.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(DelegatedProcessState.Possible, result.Observation.Delegation);
        Assert.True(result.Observation.IsConclusive);
        lock (lines) Assert.Contains(LocalizationService.Source("UpdateOperation.WaitingForExternalInstaller"), lines);
    }

    [Fact]
    public void A_kept_receipt_keeps_its_specific_reason()
    {
        var active = WinGetAttemptDecision.Evaluate(Run(9, delegation: DelegatedProcessState.KnownActive), "1.0", "2.0",
            verifyInventory: true, identityPersisted: true);
        Assert.Equal(WinGetPendingReason.Active, active.WithUnresolvedReceipt().PendingReason);
        var success = WinGetAttemptDecision.Evaluate(Run(), "2.0", "2.0", verifyInventory: true, identityPersisted: true);
        Assert.Equal(WinGetPendingReason.HistoryUnavailable,
            success.WithUnresolvedReceipt(WinGetPendingReason.HistoryUnavailable).PendingReason);
    }

    [Fact]
    public void Windows_installer_probe_reports_an_existing_execute_mutex()
    {
        var name = $@"Local\wcm-msiexecute-probe-{Guid.NewGuid():N}";
        Assert.False(WinGetNative.WindowsInstallerBusy(name));
        using var mutex = new Mutex(false, name);
        Assert.True(WinGetNative.WindowsInstallerBusy(name));
    }

    private static WinGetPackage GitHubCli() => new()
    {
        Id = "GitHub.cli", Name = "GitHub CLI", InstalledVersion = "2.105.0", AvailableVersion = "2.106.0", Source = "winget",
    };
}
