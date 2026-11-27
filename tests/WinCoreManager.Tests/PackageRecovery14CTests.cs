using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed partial class StartupPresentationTests
{
    private static WinGetPackage GitHubCli14C => new()
    {
        Id = "GitHub.cli", Name = "GitHub CLI", Source = "winget", InstalledVersion = "2.80.0", AvailableVersion = "2.81.0",
    };

    [Fact]
    // Opus structural review: the Docker receipt (exit 0, processes ended, delegation only "possible")
    // is reviewable in the same Windows session. Declining keeps every protection in place.
    public Task Etapa14C_docker_dismiss_in_the_same_session_needs_inventory_and_consent() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
        WriteDockerReceipt14C(store);
        var history = new FakeHistoryService();
        var winget = new WinGetOperationFixture(0) { Version = _ => "1.0" };
        var confirmations = 0;
        var updates = new UpdatesViewModel(winget, history, settings, store)
        {
            ConfirmInterruptionReview = (_, _) => { confirmations++; return false; },
        };
        await updates.RecheckInterruptionAsync();
        Assert.NotEqual(LocalizationService.Source("UpdateOperation.RecoveryRestart"), updates.InterruptionNote);
        var readsBeforeClick = winget.InventoryReads;
        await updates.DismissWarningCommand.ExecuteAsync(null);

        Assert.Equal(1, confirmations);
        Assert.True(winget.InventoryReads > readsBeforeClick);
        Assert.True(store.HasReceipt && updates.BlocksPackageChanges);
        Assert.Empty(history.Records);
        Assert.Empty(winget.Launched);
        Assert.Contains(LocalizationService.Source("UpdateOperation.RecoveryReviewDeclined"), updates.RecoveryActionStatus);
    });

    [Theory]
    [InlineData("pt-BR", AppTheme.Light, "1.0", "RecoveryInventoryDifferentVersion")]
    [InlineData("en-US", AppTheme.Light, "2.0", "RecoveryInventoryExpectedVersion")]
    [InlineData("pt-BR", AppTheme.Dark, "2.0", "RecoveryInventoryExpectedVersion")]
    [InlineData("en-US", AppTheme.Dark, "1.0", "RecoveryInventoryDifferentVersion")]
    public Task Etapa14C_docker_review_unblocks_wpf_modules_and_keeps_suspended_queue(
        string language, AppTheme theme, string version, string versionMessage) => WpfTestThread.Run(async () =>
    {
        var locale = LocalizationService.Current;
        var previous = locale.CultureName;
        locale.Apply(language);
        ThemeResources.Install(Application.Current.Resources, theme);
        var fixture = new ScreenFixture { Boot = WinGetOperationTests.BootBefore };
        var store = WinGetOperationTests.ReviewStore(fixture.Settings, () => fixture.Boot);
        var operationId = WriteDockerReceipt14C(store);
        Assert.True(store.SaveSuspendedQueue(operationId, [GitHubCli14C]));
        var history = new FakeHistoryService();
        var winget = new WinGetOperationFixture(0) { Packages = [GitHubCli14C], Version = _ => version };
        var main = fixture.CreateMain(winget, history,
            new FakeProgramInventory(UninstallerSelectionTests.SyntheticMsi("GitHub CLI")));
        var updates = main.Updates;
        var updatePage = new UpdatesPage { DataContext = updates };
        var uninstallPage = new UninstallersPage { DataContext = main.Uninstallers };
        var window = new Window
        {
            Content = updatePage, Width = 820, Height = 560, ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
        };
        try
        {
            window.Show();
            await updates.LoadCommand.ExecutionTask!;
            updates.Rows.Single(row => row.Id == GitHubCli14C.Id).IsSelected = true;
            var updateButton = Descendants<Button>(updatePage).Single(b => b.Command == updates.UpdateSelectedCommand);
            var dismiss = Assert.IsType<Button>(updatePage.FindName("DismissWarningButton"));
            var status = Assert.IsType<TextBlock>(updatePage.FindName("RecoveryActionText"));
            var panel = Assert.IsType<Border>(updatePage.FindName("UpdateInterruptionPanel"));
            Assert.True(dismiss.IsEnabled);
            Assert.True(panel.IsVisible);
            Assert.False(updateButton.IsEnabled || updates.ShowResumeUpdates);
            Assert.Contains(locale["Message.PackageAction.Pending"], AutomationProperties.GetHelpText(updateButton));
            var details = Descendants<Button>(panel).Single(b => b.Command == updates.ViewInterruptionDetailsCommand);
            Assert.Equal(locale["UpdateOperation.RecoveryStepsDetails"], details.Content);
            var readDialog = Dispatcher.CurrentDispatcher.InvokeAsync(() =>
            {
                var dialog = Application.Current.Windows.OfType<LocalizedDialog>().Single();
                var message = dialog.Message;
                dialog.Close();
                return message;
            });
            await updates.ViewInterruptionDetailsCommand.ExecuteAsync(null);
            Assert.Contains(locale["UpdateOperation.RecoverySteps"], await readDialog);

            window.Content = uninstallPage;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await main.Uninstallers.LoadCommand.ExecutionTask!;
            Assert.Single(main.Uninstallers.Catalog).IsSelected = true;
            var uninstallButton = Descendants<Button>(uninstallPage).Single(b => b.Command == main.Uninstallers.UninstallSelectedCommand);
            var uninstallNotice = Assert.IsType<Border>(uninstallPage.FindName("UninstallRecoveryNotice"));
            Assert.True(uninstallNotice.IsVisible);
            Assert.False(uninstallButton.IsEnabled);
            Assert.Contains(locale["Message.PackageAction.Pending"], AutomationProperties.GetHelpText(uninstallButton));
            Assert.True(Assert.IsType<TextBox>(uninstallPage.FindName("SearchBox")).IsEnabled);
            Assert.True(main.Uninstallers.RefreshCommand.CanExecute(null));
            var repair = main.Maintenance.Tools.Single(tool => tool.Key == "sfc_scannow");
            var diagnostic = main.Maintenance.Tools.Single(tool => tool.Key == "sfc_verifyonly");
            Assert.False(main.Maintenance.RunToolCommand.CanExecute(repair));
            Assert.True(main.Maintenance.RunToolCommand.CanExecute(diagnostic));
            main.NavItems.Single(item => item.ViewModel == main.Uninstallers).IsSelected = true;
            InvokeRecoveryButton(Descendants<Button>(uninstallNotice).Single(b => b.Command == main.Uninstallers.OpenPackageRecoveryCommand));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(updates, main.CurrentViewModel);
            window.Content = updatePage;

            // Same Windows session: no restart is needed to review this receipt.
            var inventoryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finishInventory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            winget.BeforeReviewVersion = async () =>
            {
                inventoryStarted.TrySetResult();
                await finishInventory.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };
            var confirmations = 0;
            updates.ConfirmInterruptionReview = (_, message) =>
            {
                Assert.Contains("Docker Desktop", message);
                Assert.Contains(locale.Format($"UpdateOperation.{versionMessage}", version), message);
                return ++confirmations == 2;
            };

            var review = updates.DismissWarningCommand.ExecuteAsync(null);
            try
            {
                await inventoryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Contains(locale["UpdateOperation.RecoveryCheckingInventory"], status.Text);
                Assert.False(dismiss.IsEnabled);
                var search = Assert.IsType<TextBox>(updatePage.FindName("SearchBox"));
                Assert.True(search.IsEnabled);
                search.Text = "GitHub";
                main.NavItems.Single(item => item.ViewModel == main.History).IsSelected = true;
                Assert.Same(main.History, main.CurrentViewModel);
                Assert.False(updates.ShowResumeUpdates);
            }
            finally { finishInventory.TrySetResult(); }
            await review;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Contains(locale["UpdateOperation.RecoveryReviewDeclined"], status.Text);
            Assert.Contains(locale.Format($"UpdateOperation.{versionMessage}", version), status.Text);
            Assert.True(store.HasReceipt);
            Assert.False(Assert.IsType<WinGetInterruptionStore.SuspendedUpdateQueue>(store.LoadSuspendedQueue()).Reviewed);
            Assert.Empty(history.Records);

            await updates.DismissWarningCommand.ExecuteAsync(null);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(2, confirmations);
            Assert.False(store.HasReceipt || panel.IsVisible);
            var verified = version == "2.0";
            Assert.Contains(locale[verified ? "UpdateOperation.VerifiedRecoveryReleased" : "UpdateOperation.DismissWarningReleased"],
                Assert.IsType<TextBlock>(updatePage.FindName("RecoveryCompletionText")).Text);
            var saved = Assert.IsType<WinGetInterruptionStore.SuspendedUpdateQueue>(store.LoadSuspendedQueue());
            Assert.True(saved.Reviewed);
            Assert.Equal(GitHubCli14C.Id, Assert.Single(saved.Packages).Id);
            Assert.True(updates.ShowResumeUpdates);
            Assert.True(updateButton.IsEnabled && Assert.Single(updates.Rows).IsSelected);
            Assert.True(main.Maintenance.RunToolCommand.CanExecute(repair));
            window.Content = uninstallPage;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(uninstallButton.IsEnabled && !uninstallNotice.IsVisible);
            var record = Assert.Single(history.Records);
            Assert.Equal("Docker Desktop", record.Target);
            Assert.Equal(version, record.VersionTo);
            Assert.Equal(verified ? HistoryStatus.Success : HistoryStatus.Interrupted, record.Status);
            Assert.Empty(winget.Launched);
        }
        finally
        {
            window.Close();
            locale.Apply(previous);
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }
    });

    [Theory]
    [InlineData("active-delegate", "RecoveryDelegateActive")]
    [InlineData("delegate-unverified", "RecoveryDelegateUnverified")]
    [InlineData("corrupt", "DismissUnreadableReceipt")]
    [InlineData("inventory-unavailable", "RecoveryInventory")]
    [InlineData("boot-invalid", "RecoveryBootUnavailable")]
    [InlineData("history-unavailable", "RecoveryHistoryUnavailable")]
    public Task Etapa14C_docker_block_reports_the_cause_and_keeps_the_receipt(string scenario, string reason) =>
        WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        using var current = Process.GetCurrentProcess();
        var delegated = scenario == "active-delegate"
            ? new WinGetProcessIdentity((uint)current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc()) : null;
        WriteDockerReceipt14C(store, delegated, scenario == "delegate-unverified");
        var receiptPath = Path.Combine(settings.DataDirectory, "winget-update.json");
        if (scenario == "corrupt") File.AppendAllText(receiptPath, "\ninvalid");
        var original = File.ReadAllBytes(receiptPath);
        boot = scenario == "boot-invalid" ? "invalid" : WinGetOperationTests.BootAfter;
        var history = new FakeHistoryService();
        var winget = new WinGetOperationFixture(0) { Version = _ => scenario == "inventory-unavailable" ? null : "1.0" };
        if (scenario == "history-unavailable") Directory.CreateDirectory(settings.HistoryDatabasePath);
        var confirmations = 0;
        var updates = new UpdatesViewModel(winget, scenario == "history-unavailable" ? new HistoryService(settings) : history, settings, store)
        {
            ConfirmInterruptionReview = (_, _) => { confirmations++; return true; },
        };
        await updates.DismissWarningCommand.ExecuteAsync(null);
        Assert.Contains(LocalizationService.Source("UpdateOperation.RecoveryDismissBlocked"), updates.RecoveryActionStatus);
        Assert.Contains(LocalizationService.Source($"UpdateOperation.{reason}", Docker14A.Id), updates.RecoveryActionStatus);
        Assert.True(updates.BlocksPackageChanges && store.HasReceipt);
        Assert.Equal(original, File.ReadAllBytes(receiptPath));
        Assert.Equal(scenario == "history-unavailable" ? 1 : 0, confirmations);
        if (delegated is not null) Assert.Contains($"PID {current.Id}", updates.InterruptionDetails);
        Assert.Empty(history.Records);
        Assert.Empty(winget.Launched);
    });

    [Fact]
    public Task Etapa14C_complete_inventory_with_unknown_version_allows_only_supervised_review() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        WriteDockerReceipt14C(store);
        boot = WinGetOperationTests.BootAfter;
        var history = new FakeHistoryService();
        var winget = new WinGetOperationFixture(0) { Version = _ => "Unknown" };
        var updates = new UpdatesViewModel(winget, history, settings, store)
        {
            ConfirmInterruptionReview = (_, _) => false,
        };
        await updates.DismissWarningCommand.ExecuteAsync(null);
        Assert.Contains(LocalizationService.Source("UpdateOperation.RecoveryInventoryUnknownVersion"), updates.RecoveryActionStatus);
        Assert.True(updates.ShowInterruptionReview && store.HasReceipt);
        Assert.Empty(history.Records);
    });

    private static Guid WriteDockerReceipt14C(WinGetInterruptionStore store, WinGetProcessIdentity? activeDelegate = null,
        bool delegationUnverified = false)
    {
        using var receipt = store.Begin(Docker14A);
        receipt.RememberRoot(new(int.MaxValue, 1));
        if (activeDelegate is not null) receipt.RememberDelegates([activeDelegate]);
        receipt.RecordOutcome(new ProcessRunResult
        {
            ExitCode = 0, HasExitCode = true,
            Observation = new(ControlledProcessState.Exited, activeDelegate is not null || delegationUnverified
                ? DelegatedProcessState.KnownActive : DelegatedProcessState.Possible),
        });
        return receipt.OperationId!.Value;
    }

    private static void InvokeRecoveryButton(Button button) => Assert.IsAssignableFrom<IInvokeProvider>(
        new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
}
