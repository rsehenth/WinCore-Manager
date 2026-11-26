using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public class UpdatesUx12PTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Hidden_identity_survives_scan_restart_version_change_and_absence(bool portable) =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new SettingsFixture(portable);
            var settings = lab.Open();
            Assert.Equal(portable, settings.Settings.PortableMode);
            var battle = BattleNet();
            var sameId = BattleNet(source: "msstore");
            var other = App(1, name: battle.Name);
            var source = new FixtureWinGet { Packages = [battle, other, sameId] };
            var vm = lab.Create(source, settings);
            var view = Attach(vm);
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.AllSelected = true;
            var battleRow = vm.Rows[0];
            vm.HideUpdateCommand.Execute(battleRow);
            Assert.False(battleRow.IsSelected);
            Assert.Equal("Unknown", battleRow.InstalledVersion);
            Assert.Equal(2, vm.SelectedCount);
            vm.HideUpdateCommand.Execute(vm.Rows[1]);
            Assert.Equal(1, vm.SelectedCount);
            Assert.Same(sameId, Assert.Single(view.Cast<UpdateItemRow>()).Package);
            Assert.Equal(3, vm.AvailableCount);
            Assert.Contains("Disponíveis: 1", vm.UpdateListSummary);
            Assert.Empty(source.Launched);

            source.Packages = [other, sameId];
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, vm.HiddenUpdates.Count);
            vm.SearchText = "unmatched";
            vm.AllSelected = true;
            Assert.Empty(view.Cast<UpdateItemRow>());
            Assert.Equal(0, vm.SelectedCount);
            Assert.True(vm.ShowEmptyState);
            vm.SearchText = "msstore";
            vm.AllSelected = true;
            Assert.Same(sameId, Assert.Single(vm.Rows, row => row.IsSelected).Package);

            source.Packages = [BattleNet("3.0"), other, sameId];
            vm = lab.Create(source);
            view = Attach(vm);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Same(sameId, Assert.Single(view.Cast<UpdateItemRow>()).Package);
            Assert.Equal(2, vm.HiddenUpdates.Count);
            vm.ShowUpdateCommand.Execute(vm.HiddenUpdates.Single(entry => entry.Id == battle.Id));
            Assert.Equal(2, view.Cast<UpdateItemRow>().Count());
            Assert.Contains(view.Cast<UpdateItemRow>(), row => row.Id == battle.Id &&
                row.Source == "winget" && row.AvailableVersion == "3.0" && !row.IsSelected);
            Assert.Equal(other.Id, Assert.Single(lab.Open().Settings.HiddenUpdates).Id);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Failed_preferences_save_preserves_visible_state_and_keeps_resize_in_session(bool corrupt) =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new SettingsFixture();
            if (corrupt) File.WriteAllText(lab.SettingsPath, "{broken");
            var settings = lab.Open();
            if (!corrupt)
            {
                File.Delete(lab.SettingsPath);
                Directory.CreateDirectory(lab.SettingsPath);
            }
            var vm = lab.Create(new FixtureWinGet { Packages = [BattleNet()] }, settings);
            var view = Attach(vm);
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.AllSelected = true;
            vm.HideUpdateCommand.Execute(vm.Rows[0]);
            Assert.Empty(vm.HiddenUpdates);
            Assert.Single(view.Cast<UpdateItemRow>());
            Assert.Equal(1, vm.SelectedCount);
            Assert.NotEmpty(vm.ListPreferenceMessage);
            vm.SaveListRatio(0.53);
            Assert.Equal(0.53, vm.ListRatio);
            Assert.Null(settings.Settings.UpdatesListRatio);
            Assert.NotEmpty(vm.ListPreferenceMessage);
            if (corrupt) Assert.Equal("{broken", File.ReadAllText(lab.SettingsPath));
        });

    [Fact]
    public void Malformed_preference_members_preserve_other_settings_and_valid_entries()
    {
        using var lab = new SettingsFixture();
        File.WriteAllText(lab.SettingsPath, """
            {"Theme":"Light","Language":"en-US","UpdatesListRatio":1e999,"HiddenUpdates":[
              null, {"Source":"winget","Id":5}, {"Source":"winget","Id":"Blizzard.BattleNet","Name":"Battle.net"},
              {"Source":"winget","Id":"Blizzard.BattleNet"}, {"Source":"msstore","Id":"Blizzard.BattleNet"}
            ]}
            """);
        var settings = lab.Open().Settings;
        Assert.Null(settings.UpdatesListRatio);
        Assert.Equal("Light", settings.Theme);
        Assert.Equal("en-US", settings.Language);
        Assert.Collection(settings.HiddenUpdates, entry => Assert.Equal("Battle.net", entry.Name),
            entry => Assert.Equal("msstore", entry.Source));
    }

    [Fact]
    public Task Active_batch_rejects_hide_and_never_launches_hidden_rows() => WpfTestThread.Run(async () =>
    {
        using var lab = new SettingsFixture();
        var finish = new TaskCompletionSource<ProcessRunResult>();
        var winget = new FixtureWinGet { Packages = [BattleNet(), App(1), App(2)], Upgrade = _ => finish.Task };
        var vm = lab.Create(winget);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.HideUpdateCommand.Execute(vm.Rows[0]);
        vm.AllSelected = true;
        var update = vm.UpdateSelectedCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsUpdating);
            vm.HideUpdateCommand.Execute(vm.Rows[1]);
            vm.HideUpdateCommand.Execute(vm.Rows[2]);
            Assert.Single(vm.HiddenUpdates);
            Assert.Contains("em andamento", vm.ListPreferenceMessage);
        }
        finally
        {
            finish.SetResult(new ProcessRunResult { ExitCode = 0 });
            await update;
        }
        Assert.Equal(new[] { "Fixture.1", "Fixture.2" }, winget.Launched);
    });

    [Fact]
    public Task Hidden_BattleNet_preserves_pending_receipt_history_and_recovery_on_reopen() =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new SettingsFixture();
            var settings = lab.Open();
            var history = new HistoryService(settings);
            var battle = BattleNet();
            await history.AddAsync(new HistoryRecord { Type = HistoryType.Update, Target = battle.Name, Status = HistoryStatus.Interrupted });
            var store = new WinGetInterruptionStore(settings.DataDirectory, () => WinGetOperationTests.BootBefore);
            using (store.Begin(battle)) { }
            var receiptPath = Path.Combine(settings.DataDirectory, "winget-update.json");
            var before = File.ReadAllBytes(receiptPath);
            var source = new RecoveryWinGet { Packages = [battle, App(1)] };
            var vm = new UpdatesViewModel(source, history, settings, store);
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.HideUpdateCommand.Execute(vm.Rows[0]);
            Assert.Equal(before, File.ReadAllBytes(receiptPath));

            settings = lab.Open();
            history = new HistoryService(settings);
            store = new WinGetInterruptionStore(settings.DataDirectory, () => WinGetOperationTests.BootBefore);
            vm = new UpdatesViewModel(source, history, settings, store);
            var view = Attach(vm);
            await vm.LoadCommand.ExecuteAsync(null);
            Assert.Equal("Fixture.1", Assert.Single(view.Cast<UpdateItemRow>()).Id);
            Assert.Contains(battle.Id, vm.InterruptionTarget);
            Assert.NotEmpty(vm.InterruptionNote);
            Assert.True(vm.ResolveInterruptionCommand.CanExecute(null));
            vm.AllSelected = true;
            Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
            Assert.False(vm.TryReservePackageChange(out _));
            Assert.Equal(before, File.ReadAllBytes(receiptPath));
            var record = Assert.Single(await history.RecentAsync(5));
            Assert.Equal(battle.Name, record.Target);
            Assert.Equal(HistoryStatus.Interrupted, record.Status);
            Assert.Empty(source.Launched);
        });

    [Fact]
    public Task Splitter_is_keyboard_accessible_and_resizes_the_table_in_both_directions() =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new SettingsFixture();
            var fixture = new FixtureWinGet { Packages = Enumerable.Range(1, 42).Select(index => App(index)).ToArray() };
            var vm = lab.Create(fixture);
            var page = new UpdatesPage { DataContext = vm };
            var window = new Window { Content = page, Width = 920, Height = 740, ShowInTaskbar = false, Opacity = 0 };
            try
            {
                window.Show();
                await vm.LoadCommand.ExecutionTask!;
                await Settle();
                var table = Assert.IsType<DataGrid>(page.FindName("PackagesGrid"));
                var splitter = Assert.IsType<GridSplitter>(page.FindName("UpdatesSplitter"));
                Assert.Equal(42, table.Items.Count);
                Assert.True(splitter.Focusable && splitter.IsTabStop && splitter.FocusVisualStyle is not null);
                Assert.NotEmpty(AutomationProperties.GetHelpText(splitter));
                var initial = table.ActualHeight;
                Assert.True(initial >= 320, $"Initial grid height: {initial}");
                Drag(splitter, -90);
                await Settle();
                var shorter = table.ActualHeight;
                Assert.True(shorter < initial - 60);
                Assert.True(splitter.Focus());
                Press(splitter, Key.Down);
                await Settle();
                Assert.True(table.ActualHeight > shorter);
                var afterKeyboard = table.ActualHeight;
                Press(splitter, Key.Up);
                await Settle();
                Assert.True(table.ActualHeight < afterKeyboard);
                var ratio = Assert.IsType<double>(lab.Open().Settings.UpdatesListRatio);
                Assert.Equal(ratio, vm.ListRatio);

                window.WindowState = WindowState.Maximized;
                await Settle();
                window.WindowState = WindowState.Normal;
                await Settle();
                Assert.Equal(ratio, lab.Open().Settings.UpdatesListRatio);

                var reopened = lab.Create(fixture);
                page = new UpdatesPage { DataContext = reopened };
                window.Content = page;
                window.Width = 600;
                window.Height = 420;
                await reopened.LoadCommand.ExecutionTask!;
                await Settle();
                table = Assert.IsType<DataGrid>(page.FindName("PackagesGrid"));
                var log = Assert.IsType<ScrollViewer>(page.FindName("LogScroll"));
                Assert.Equal(ratio, reopened.ListRatio);
                Assert.True(table.ActualHeight >= 70 && log.ViewportHeight > 0);
                AssertWithinPage(page, log);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData("pt-BR", AppTheme.Light, 1.00)]
    [InlineData("en-US", AppTheme.Light, 1.25)]
    [InlineData("pt-BR", AppTheme.Dark, 1.50)]
    [InlineData("en-US", AppTheme.Dark, 2.00)]
    public Task Updates_page_renders_actions_and_recovery_in_each_theme_and_language(string language, AppTheme theme, double scale) =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new SettingsFixture();
            var settings = lab.Open();
            var source = new RecoveryWinGet
            {
                Packages = [BattleNet(), .. Enumerable.Range(1, 32).Select(index => App(index))],
                RawLog = string.Join(Environment.NewLine, Enumerable.Range(1, 610).Select(index => $"Fixture WinGet · {index:0000}")),
            };
            var store = new WinGetInterruptionStore(settings.DataDirectory, () => WinGetOperationTests.BootBefore);
            using (store.Begin(source.Packages[0])) { }
            var vm = new UpdatesViewModel(source, new FakeHistoryService(), settings, store);
            var page = new UpdatesPage { DataContext = vm };
            var window = new Window
            {
                Content = page, Width = Math.Max(600, 1320 / scale), Height = Math.Max(420, 1050 / scale),
                ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
                Background = (Brush)Application.Current.FindResource("WindowBgBrush"),
            };
            var english = language == "en-US";
            try
            {
                ThemeResources.Install(Application.Current.Resources, theme);
                LocalizationService.Current.Apply(language);
                window.SetResourceReference(Control.BackgroundProperty, "WindowBgBrush");
                window.Show();
                await vm.LoadCommand.ExecutionTask!;
                await Settle();
                var table = Assert.IsType<DataGrid>(page.FindName("PackagesGrid"));
                var hidden = Assert.IsType<DataGrid>(page.FindName("HiddenPackagesGrid"));
                var manage = Assert.IsType<Button>(page.FindName("ManageHiddenButton"));
                var show = Assert.IsType<Button>(page.FindName("ShowHiddenButton"));
                var splitter = Assert.IsType<GridSplitter>(page.FindName("UpdatesSplitter"));
                var summary = Assert.IsType<TextBlock>(page.FindName("UpdatesListSummaryText"));
                var controls = Assert.IsType<ScrollViewer>(page.FindName("UpdateControlsScroll"));
                var log = Assert.IsType<ScrollViewer>(page.FindName("LogScroll"));
                Assert.Equal(600, vm.LogLines.Count);
                Assert.Equal(33, table.Items.Count);
                Assert.True(log.ScrollableHeight > 100 && log.ViewportHeight > 0);
                Assert.True(controls.ViewportHeight > 0);
                AssertWithinPage(page, log);
                AssertWithinPage(page, controls);
                AssertWithinPage(page, manage);
                Assert.Contains(english ? "Available: 33" : "Disponíveis: 33", summary.Text);
                Assert.Equal(english ? "Manage hidden updates" : "Gerenciar ocultos", manage.Content);
                await CaptureAsync(page, $"updates-{theme}-{language}", scale);

                var row = Assert.IsType<DataGridRow>(table.ItemContainerGenerator.ContainerFromIndex(0));
                var menu = Assert.IsType<ContextMenu>(row.ContextMenu);
                var item = Assert.IsType<MenuItem>(Assert.Single(menu.Items));
                menu.PlacementTarget = row;
                menu.IsOpen = true;
                try
                {
                    await Settle();
                    Assert.Equal(english ? "Hide from this list" : "Ocultar desta lista", item.Header);
                    Assert.Same(row.DataContext, item.CommandParameter);
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                finally { menu.IsOpen = false; }
                await Settle();
                Assert.Equal(32, table.Items.Count);
                Assert.Contains(english ? "1 hidden" : "1 oculto", summary.Text);
                Assert.True(vm.HasInterruptedUpdate);

                Invoke(manage);
                await Settle();
                Assert.True(hidden.IsVisible && !table.IsVisible);
                Assert.Equal("Blizzard.BattleNet", Assert.IsType<HiddenUpdate>(Assert.Single(hidden.Items)).Id);
                hidden.SelectedIndex = 0;
                await Settle();
                Assert.True(show.IsEnabled && show.IsTabStop && manage.IsTabStop);
                Assert.Equal(english ? "Show again" : "Reexibir", show.Content);
                await CaptureAsync(page, $"hidden-{theme}-{language}", scale);
                Invoke(show);
                await Settle();
                Assert.Empty(hidden.Items);
                Assert.True(Assert.IsType<TextBlock>(page.FindName("HiddenUpdatesEmptyText")).IsVisible);
                Invoke(manage);
                source.Packages = [];
                await vm.RefreshCommand.ExecuteAsync(null);
                await Settle();
                Assert.True(Assert.IsType<TextBlock>(page.FindName("UpdatesEmptyText")).IsVisible);
                Assert.Equal(english ? "Resize updates list" : "Redimensionar lista de atualizações",
                    AutomationProperties.GetName(splitter));

                LocalizationService.Current.Apply(english ? "pt-BR" : "en-US");
                ThemeResources.Install(Application.Current.Resources, theme == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);
                await Settle();
                Assert.Equal(english ? "Gerenciar ocultos" : "Manage hidden updates", manage.Content);
                Assert.True(vm.HasInterruptedUpdate);
                AssertWithinPage(page, log);
            }
            finally
            {
                window.Close();
                ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
            }
        });

    private static ICollectionView Attach(UpdatesViewModel vm)
    {
        var view = CollectionViewSource.GetDefaultView(vm.Rows);
        vm.AttachFilter(view);
        return view;
    }

    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Drag(GridSplitter splitter, double offset)
    {
        splitter.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });
        splitter.RaiseEvent(new DragDeltaEventArgs(0, offset) { RoutedEvent = Thumb.DragDeltaEvent });
        splitter.RaiseEvent(new DragCompletedEventArgs(0, offset, false) { RoutedEvent = Thumb.DragCompletedEvent });
    }

    private static void Press(UIElement target, Key key) =>
        target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target)!, 0, key)
            { RoutedEvent = Keyboard.KeyDownEvent });

    private static void AssertWithinPage(FrameworkElement page, FrameworkElement child)
    {
        var area = child.TransformToAncestor(page).TransformBounds(new Rect(child.RenderSize));
        Assert.True(area.Right <= page.ActualWidth + 1 && area.Bottom <= page.ActualHeight + 1,
            $"{child.Name}: {area} outside {page.RenderSize}");
    }

    private static void Invoke(Button button) =>
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)!).Invoke();

    private static async Task CaptureAsync(FrameworkElement element, string name, double scale)
    {
        var evidence = Environment.GetEnvironmentVariable("WINCORE_12P_SCREENSHOTS");
        if (string.IsNullOrEmpty(evidence)) return;
        await Task.Delay(180);
        var width = (int)Math.Ceiling(element.ActualWidth * scale);
        var height = (int)Math.Ceiling(element.ActualHeight * scale);
        var image = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var frame = new DrawingVisual();
        using (var drawing = frame.RenderOpen())
        {
            var bounds = new Rect(element.RenderSize);
            drawing.DrawRectangle((Brush)element.FindResource("WindowBgBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element)
            {
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = bounds,
            }, null, bounds);
        }
        image.Render(frame);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(evidence, $"{name}-{scale.ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "")}.png"));
        png.Save(file);
    }

    private static WinGetPackage BattleNet(string available = "2.0.0.4232", string source = "winget") =>
        new() { Id = "Blizzard.BattleNet", Name = "Battle.net", Source = source, InstalledVersion = "Unknown", AvailableVersion = available };

    private static WinGetPackage App(int index, string? name = null) =>
        new() { Id = $"Fixture.{index}", Name = name ?? $"Fixture App {index}", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "2.0" };

    private sealed class SettingsFixture : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("wincore-12p-").FullName;
        public SettingsFixture(bool portable = false)
        {
            var settings = Open();
            settings.Settings.PortableMode = portable;
            settings.Save();
        }
        public string SettingsPath => Path.Combine(_root, "local", "settings.json");
        public SettingsService Open() => new(Path.Combine(_root, "portable"), Path.Combine(_root, "local"));
        public UpdatesViewModel Create(FixtureWinGet source, SettingsService? settings = null)
        {
            settings ??= Open();
            return new(source, new FakeHistoryService(), settings, new WinGetInterruptionStore(settings.DataDirectory));
        }
        public void Dispose()
        {
            var database = Open().HistoryDatabasePath;
            if (File.Exists(database))
            {
                using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = database, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5,
                }.ToString());
                SqliteConnection.ClearPool(pool);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    private class FixtureWinGet : IWinGetService
    {
        public IReadOnlyList<WinGetPackage> Packages { get; set; } = [];
        public string RawLog { get; set; } = string.Empty;
        public List<string> Launched { get; } = [];
        public Func<WinGetPackage, Task<ProcessRunResult>>? Upgrade { get; set; }
        public string? ExecutablePath => null;
        public WinGetAvailability? LastDetection => null;
        public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetAvailability { IsInstalled = true });
        public Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetScanResult { Packages = Packages, RawOutput = RawLog });
        public Task<ProcessRunResult> UpgradePackageAsync(
            WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
            Upgrade is null ? throw new InvalidOperationException("Unexpected upgrade in the UX fixture.") : Launch(package);

        private Task<ProcessRunResult> Launch(WinGetPackage package)
        {
            Launched.Add(package.Id);
            return Upgrade!(package);
        }
    }

    private sealed class RecoveryWinGet : FixtureWinGet, IControlledWinGetService
    {
        public Task<ProcessRunResult> UpgradeControlledAsync(WinGetPackage package, Action<string> onOutputLine,
            WinGetProcessControl control, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected controlled upgrade in the UX fixture.");
        public Task<string?> ReadInstalledVersionAsync(WinGetPackage package) =>
            Task.FromResult<string?>(package.InstalledVersion);
    }
}
