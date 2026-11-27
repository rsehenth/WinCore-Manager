using System.Diagnostics;
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
    private static WinGetPackage BattleNet14A => new()
    {
        Id = "Blizzard.BattleNet", Name = "Battle.net", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "2.0",
    };

    private static WinGetPackage Docker14A => new()
    {
        Id = "Docker.DockerDesktop", Name = "Docker Desktop", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "2.0",
    };

    [Theory]
    [InlineData("pt-BR", AppTheme.Light, false)]
    [InlineData("en-US", AppTheme.Light, true)]
    [InlineData("pt-BR", AppTheme.Dark, true)]
    [InlineData("en-US", AppTheme.Dark, false)]
    public Task Etapa14A_resolution_unblocks_both_wpf_buttons_and_keeps_the_selected_update(
        string language, AppTheme theme, bool dismiss) => WpfTestThread.Run(async () =>
    {
        ThemeResources.Install(Application.Current.Resources, theme);
        var locale = LocalizationService.Current;
        locale.Apply(language);
        var fixture = new ScreenFixture();
        var store = WinGetOperationTests.ReviewStore(fixture.Settings, () => fixture.Boot);
        var winget = new WinGetOperationFixture(0) { Packages = [Docker14A, BattleNet14A], Version = _ => "1.0" };
        var history = new HistoryService(fixture.Settings);
        var inventory = new FakeProgramInventory(UninstallerSelectionTests.SyntheticMsi("Editor de Texto"));
        fixture.Settings.Settings.HiddenUpdates = [new("Battle.net", "Blizzard.BattleNet", "winget")];
        fixture.Settings.Settings.UpdatesListRatio = 0.62;
        await history.AddAsync(new HistoryRecord { Type = HistoryType.Update, Target = "Earlier record", Status = HistoryStatus.Success });
        var main = fixture.CreateMain(winget, history, inventory);
        var updates = main.Updates;
        var uninstallers = main.Uninstallers;
        var repair = main.Maintenance.Tools.First(tool => tool.Key == "sfc_scannow");
        var compatible = main.Maintenance.Tools.First(tool => tool.Key == "sfc_verifyonly");
        var updatePage = new UpdatesPage { DataContext = updates };
        var uninstallPage = new UninstallersPage { DataContext = uninstallers };
        var window = new Window
        {
            DataContext = main, Content = new StackPanel { Children = { updatePage, uninstallPage } },
            Width = 1180, Height = 840,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
        };
        try
        {
            window.Show();
            await updates.LoadCommand.ExecutionTask!;
            await uninstallers.LoadCommand.ExecutionTask!;
            var updateButton = Descendants<Button>(updatePage).Single(b => b.Command == updates.UpdateSelectedCommand);
            var uninstallButton = Descendants<Button>(uninstallPage).Single(b => b.Command == uninstallers.UninstallSelectedCommand);
            Assert.False(updateButton.IsEnabled || uninstallButton.IsEnabled);
            Assert.Equal(locale["Message.PackageAction.NoSelection"], Assert.IsType<string>(updateButton.ToolTip));
            Assert.Equal(locale["Message.Uninstaller.NoSelection"], AutomationProperties.GetHelpText(uninstallButton));
            updates.Rows.Single(row => row.Id == Docker14A.Id).IsSelected = true;
            Assert.Single(uninstallers.Catalog).IsSelected = true;
            Assert.True(updateButton.IsEnabled && uninstallButton.IsEnabled);
            Assert.True(main.Maintenance.RunToolCommand.CanExecute(repair));

            using (var receipt = store.Begin(BattleNet14A)) receipt.RememberRoot(new(int.MaxValue, 1));
            fixture.Boot = WinGetOperationTests.BootAfter;
            await updates.RecheckInterruptionAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var panel = Assert.IsType<Border>(updatePage.FindName("UpdateInterruptionPanel"));
            var reviewButton = Assert.IsType<Button>(updatePage.FindName("ReviewInterruptionButton"));
            var dismissButton = Assert.IsType<Button>(updatePage.FindName("DismissWarningButton"));
            Assert.Equal(language == "en-US" ? "Dismiss warning" : "Ignorar aviso", dismissButton.Content);
            Assert.True(panel.IsVisible && reviewButton.IsEnabled && dismissButton.IsEnabled);
            Assert.True(dismissButton.TransformToVisual(reviewButton).Transform(new Point()).X >= reviewButton.ActualWidth);
            var grid = Assert.IsType<DataGrid>(updatePage.FindName("PackagesGrid"));
            Assert.Equal(Docker14A.Id, Assert.IsType<UpdateItemRow>(Assert.Single(grid.Items)).Id);
            Assert.Contains("Blizzard.BattleNet", updates.PackageChangeBlockReason);
            Assert.False(updateButton.IsEnabled || uninstallButton.IsEnabled);
            Assert.Contains("Blizzard.BattleNet", Assert.IsType<string>(updateButton.ToolTip));
            Assert.Contains(locale["Message.PackageAction.Pending"], AutomationProperties.GetHelpText(updateButton));
            Assert.Contains("Blizzard.BattleNet", Assert.IsType<string>(uninstallButton.ToolTip));
            Assert.Contains(locale["Message.PackageAction.OpenUpdates"], AutomationProperties.GetHelpText(uninstallButton));
            Assert.False(main.Maintenance.RunToolCommand.CanExecute(repair));
            Assert.True(main.Maintenance.RunToolCommand.CanExecute(compatible));

            updates.ConfirmInterruptionReview = (_, message) =>
            {
                Assert.Contains("Battle.net", message);
                if (dismiss)
                    Assert.Contains(language == "en-US" ? "installation outcome remains inconclusive" : "resultado da instalação continua inconclusivo", message);
                return true;
            };
            var invoke = Assert.IsAssignableFrom<IInvokeProvider>(
                new ButtonAutomationPeer(dismiss ? dismissButton : reviewButton).GetPattern(PatternInterface.Invoke));
            invoke.Invoke();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await (dismiss ? updates.DismissWarningCommand : updates.ReviewInterruptionCommand).ExecutionTask!;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.False(store.HasReceipt || updates.HasInterruptedUpdate);
            Assert.False(panel.IsVisible);
            var recorded = await new HistoryService(fixture.Settings).RecentAsync(10);
            var record = Assert.Single(recorded, row => row.Target == "Battle.net");
            Assert.Equal(HistoryStatus.Interrupted, record.Status);
            Assert.Contains(dismiss ? "ignorar o aviso" : "risco residual", record.Message);
            Assert.Contains(recorded, row => row.Target == "Earlier record");
            Assert.True(uninstallButton.IsEnabled);
            Assert.True(updates.Rows.Single(row => row.Id == Docker14A.Id).IsSelected);
            Assert.True(updateButton.IsEnabled);
            Assert.True(main.Maintenance.RunToolCommand.CanExecute(repair));
            Assert.Null(updateButton.ToolTip);
            Assert.Null(uninstallButton.ToolTip);
            var reopened = fixture.CreateMain(winget, history, inventory);
            await reopened.Updates.LoadCommand.ExecuteAsync(null);
            await reopened.Uninstallers.LoadCommand.ExecuteAsync(null);
            Assert.False(reopened.Updates.HasInterruptedUpdate || reopened.Uninstallers.ApplicationBusy);
            Assert.Equal("Blizzard.BattleNet", Assert.Single(reopened.Updates.HiddenUpdates).Id);
            Assert.Equal(0.62, reopened.Updates.ListRatio);
            reopened.Updates.AllSelected = reopened.Uninstallers.AllSelected = true;
            Assert.True(reopened.Updates.UpdateSelectedCommand.CanExecute(null));
            Assert.True(reopened.Uninstallers.UninstallSelectedCommand.CanExecute(null));
            Assert.Empty(winget.Launched);
        }
        finally
        {
            window.Close();
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }
    });

    [Theory]
    [InlineData("active-root", "OriginalProcessPending")]
    [InlineData("active-delegate", "RecoveryDelegateActive")]
    [InlineData("unknown-delegate", "RecoveryDelegateUnverified")]
    [InlineData("launch-without-identity", "RecoveryRestart")]
    [InlineData("no-inventory", "RecoveryInventory")]
    [InlineData("unreadable", "DismissUnreadableReceipt")]
    public Task Etapa14A_dismiss_explains_unsafe_receipts_without_offering_confirmation(string scenario, string reason) =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var boot = WinGetOperationTests.BootBefore;
            var store = WinGetOperationTests.ReviewStore(settings, () => boot);
            using (var process = Process.GetCurrentProcess())
            using (var receipt = store.Begin(BattleNet14A))
            {
                var active = new WinGetProcessIdentity((uint)process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc());
                // Opus: a restart is only required when this session cannot verify the recorded root.
                if (scenario == "launch-without-identity")
                {
                    var control = new WinGetProcessControl();
                    receipt.Bind(control);
                    control.PersistLaunchIntent!();
                }
                else receipt.RememberRoot(scenario == "active-root" ? active : new(int.MaxValue, 1));
                if (scenario == "active-delegate") receipt.RememberDelegates([active]);
                if (scenario == "unknown-delegate")
                    receipt.RecordOutcome(new ProcessRunResult
                    {
                        Observation = new(ControlledProcessState.Exited, DelegatedProcessState.KnownActive),
                    });
            }
            if (scenario == "unreadable")
            {
                File.AppendAllText(Path.Combine(settings.DataDirectory, "winget-update.json"), "\ninvalid");
                // Damaged receipts require boot evidence newer than the file's timestamp.
                File.SetLastWriteTimeUtc(Path.Combine(settings.DataDirectory, "winget-update.json"), DateTime.UnixEpoch.AddDays(2));
            }
            if (scenario != "launch-without-identity") boot = WinGetOperationTests.BootAfter;
            var winget = new WinGetOperationFixture(0)
            {
                Packages = [Docker14A],
                Version = _ => scenario == "no-inventory" ? null : "1.0",
            };
            var history = new FakeHistoryService();
            var updates = new UpdatesViewModel(winget, history, settings, store)
            {
                ConfirmInterruptionReview = (_, _) => throw new InvalidOperationException("Unsafe receipt reached confirmation."),
            };
            await updates.RefreshCommand.ExecuteAsync(null);
            updates.AllSelected = true;

            Assert.True(updates.DismissWarningCommand.CanExecute(null));
            await updates.DismissWarningCommand.ExecuteAsync(null);

            Assert.True(store.HasReceipt && updates.HasInterruptedUpdate && updates.BlocksPackageChanges);
            Assert.False(updates.UpdateSelectedCommand.CanExecute(null));
            Assert.Equal(LocalizationService.Source("UpdateOperation." + reason, BattleNet14A.Id), updates.InterruptionNote);
            Assert.Empty(history.Records);
            Assert.Empty(winget.Launched);
        });

    [Theory]
    [InlineData("cancel")]
    [InlineData("history-unavailable")]
    [InlineData("receipt-changed")]
    public Task Etapa14A_dismiss_keeps_the_block_after_confirmation_failure_or_receipt_replacement(string scenario) =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var boot = WinGetOperationTests.BootBefore;
            var store = WinGetOperationTests.ReviewStore(settings, () => boot);
            using (var receipt = store.Begin(BattleNet14A)) receipt.RememberRoot(new(int.MaxValue, 1));
            boot = WinGetOperationTests.BootAfter;
            var history = new FakeHistoryService();
            if (scenario == "history-unavailable") Directory.CreateDirectory(settings.HistoryDatabasePath);
            var confirmations = 0;
            var updates = new UpdatesViewModel(new WinGetOperationFixture(0), scenario == "history-unavailable"
                ? new HistoryService(settings) : history, settings, store)
            {
                ConfirmInterruptionReview = (_, _) =>
                {
                    confirmations++;
                    if (scenario == "receipt-changed")
                        File.AppendAllText(Path.Combine(settings.DataDirectory, "winget-update.json"), "\nchanged");
                    return scenario != "cancel";
                },
            };

            await updates.DismissWarningCommand.ExecuteAsync(null);

            Assert.Equal(1, confirmations);
            Assert.True(store.HasReceipt && updates.HasInterruptedUpdate);
            Assert.False(updates.UpdateSelectedCommand.CanExecute(null));
            Assert.Empty(history.Records);
            if (scenario == "cancel")
                Assert.True(updates.ReviewInterruptionCommand.CanExecute(null));
            else
                Assert.Equal(LocalizationService.Source(scenario == "receipt-changed" ? "UpdateOperation.RecoveryReceiptChanged"
                    : "UpdateOperation.RecoveryHistoryUnavailable"), updates.InterruptionNote);
        });

    [Fact]
    public Task Etapa14A_dismiss_checks_the_receipt_again_after_history_is_saved() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        using (var receipt = store.Begin(BattleNet14A)) receipt.RememberRoot(new(int.MaxValue, 1));
        boot = WinGetOperationTests.BootAfter;
        var enteredScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winget = new WinGetOperationFixture(0) { Packages = [Docker14A] };
        var history = new FakeHistoryService();
        var updates = new UpdatesViewModel(winget, history, settings, store) { ConfirmInterruptionReview = (_, _) => true };
        await updates.RefreshCommand.ExecuteAsync(null);
        updates.AllSelected = true;
        winget.BeforeListUpdates = () =>
        {
            enteredScan.SetResult();
            return finishScan.Task;
        };

        var review = updates.DismissWarningCommand.ExecuteAsync(null);
        try
        {
            await enteredScan.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(history.Records);
            Assert.True(store.HasReceipt && updates.HasInterruptedUpdate);
            Assert.False(updates.UpdateSelectedCommand.CanExecute(null));
            File.AppendAllText(Path.Combine(settings.DataDirectory, "winget-update.json"), "\nchanged after the audit");
        }
        finally { finishScan.SetResult(); }
        await review;

        Assert.True(store.HasReceipt && updates.HasInterruptedUpdate);
        Assert.False(updates.UpdateSelectedCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Source("UpdateOperation.RecoveryReceiptChanged"), updates.InterruptionNote);
    });

    [Fact]
    public Task Etapa14A_dismiss_of_receipt_that_proves_no_launch_still_requires_audited_confirmation() =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = WinGetOperationTests.ReviewStore(settings, () => null);
            using (store.BeginPrepared(BattleNet14A)) { }
            var history = new FakeHistoryService();
            var updates = new UpdatesViewModel(new WinGetOperationFixture(0), history, settings, store)
            {
                ConfirmInterruptionReview = (_, _) => false,
            };
            await updates.DismissWarningCommand.ExecuteAsync(null);
            Assert.True(store.HasReceipt);
            Assert.Empty(history.Records);
            updates.ConfirmInterruptionReview = (_, _) => true;
            await updates.DismissWarningCommand.ExecuteAsync(null);
            Assert.False(store.HasReceipt);
            Assert.Contains("não iniciou", Assert.Single(history.Records).Message);
        });

    [Fact]
    public Task Etapa14A_disabled_reasons_follow_protection_maintenance_busy_and_inventory() => WpfTestThread.Run(async () =>
    {
        var fixture = new ScreenFixture();
        var inventory = new SwitchableInventory14A(ClassifierProgramFactory.Create(
            name: "System component", publisher: "Microsoft Corporation", isSystemComponent: true));
        var main = fixture.CreateMain(new WinGetOperationFixture(0) { Packages = [Docker14A] }, inventory: inventory);
        await main.Updates.RefreshCommand.ExecuteAsync(null);
        main.Updates.AllSelected = true;
        await main.Uninstallers.RefreshCommand.ExecuteAsync(null);
        main.Uninstallers.SelectedFilterIndex = 4;
        main.Uninstallers.SelectedRow = Assert.Single(main.Uninstallers.Catalog);
        Assert.False(main.Uninstallers.UninstallSelectedCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Source("Message.PackageAction.Protected"), main.Uninstallers.UninstallSelectedDisabledReason);

        main.Maintenance.PackageConflictRunning = main.Maintenance.IsRunning = true;
        Assert.False(main.Updates.UpdateSelectedCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Source("Message.PackageAction.Maintenance"), main.Updates.UpdateSelectedDisabledReason);
        Assert.Equal(LocalizationService.Source("Message.PackageAction.Maintenance"), main.Uninstallers.UninstallSelectedDisabledReason);
        main.Maintenance.PackageConflictRunning = main.Maintenance.IsRunning = false;
        Assert.True(main.Updates.UpdateSelectedCommand.CanExecute(null));

        main.Updates.IsUpdating = true;
        Assert.Equal(LocalizationService.Source("Message.PackageAction.Busy"), main.Updates.UpdateSelectedDisabledReason);
        Assert.Equal(LocalizationService.Source("Message.Uninstaller.UpdateRunning"), main.Uninstallers.UninstallSelectedDisabledReason);
        main.Updates.IsUpdating = false;
        main.Updates.ScanFailed = true;
        inventory.Unavailable = true;
        await main.Uninstallers.RefreshCommand.ExecuteAsync(null);
        Assert.False(main.Updates.UpdateSelectedCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Source("Message.PackageAction.InventoryUnavailable"), main.Updates.UpdateSelectedDisabledReason);
        Assert.Equal(LocalizationService.Source("Message.PackageAction.InventoryUnavailable"), main.Uninstallers.UninstallSelectedDisabledReason);
    });

    private sealed class SwitchableInventory14A(InstalledProgram program) : IProgramInventoryService
    {
        public bool Unavailable { get; set; }

        public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default) =>
            Unavailable ? Task.FromException<IReadOnlyList<InstalledProgram>>(new IOException("Fixture unavailable")) :
                Task.FromResult<IReadOnlyList<InstalledProgram>>([program]);
    }
}
