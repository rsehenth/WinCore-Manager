using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public class UninstallerSelectionTests
{
    private const string Auslogics = "Auslogics Disk Defrag Ultimate";

    internal static InstalledProgram SyntheticMsi(string name, string hive = "HKLM") => new()
    {
        Name = name,
        Publisher = "Fixture",
        DisplayVersion = "1.0",
        Kind = ProgramKind.Msi,
        MsiProductCode = Guid.NewGuid(),
        RegistryPath = $@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
    };

    [Fact]
    public Task Auslogics_grid_details_and_accessible_toggle_are_independent() => WpfTestThread.Run(async () =>
    {
        using var path = new TemporaryExe();
        var auslogics = path.Program(Auslogics);
        var inventory = new FakeProgramInventory(auslogics, SyntheticMsi("Orchard Sync"));
        var model = Create(inventory, new FakeUninstallExecutor(), new FakeHistoryService());
        var page = new UninstallersPage { DataContext = model };
        var window = new Window { Content = page, Width = 960, Height = 700, ShowInTaskbar = false };
        try
        {
            window.Show();
            await model.LoadCommand.ExecutionTask!;
            var grid = Assert.IsType<DataGrid>(page.FindName("ProgramsGrid"));
            var row = model.Catalog.Single(item => item.Name == Auslogics);
            grid.SelectedItem = row;
            grid.ScrollIntoView(row);
            await Settle();
            Assert.Same(row, model.SelectedRow);
            Assert.Equal(0, model.SelectedCount);
            var action = Descendants<Button>(page).Single(button => button.Command == model.UninstallSelectedCommand);
            Assert.False(action.IsEnabled);
            var check = CheckboxFor(grid, row);
            Assert.True(check.IsVisible && check.IsEnabled);
            var peer = new CheckBoxAutomationPeer(check);
            var toggle = Assert.IsAssignableFrom<IToggleProvider>(peer.GetPattern(PatternInterface.Toggle));
            toggle.Toggle();
            await Settle();
            Assert.Equal(1, model.SelectedCount);
            Assert.True(action.IsEnabled);
            Assert.Contains(Auslogics, peer.GetName());
            Assert.NotNull(grid.Columns[0].Header);
            toggle.Toggle();
            await Settle();
            Assert.Equal(0, model.SelectedCount);
            Assert.False(action.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Unavailable_command_and_hidden_component_stay_out_of_select_all() => WpfTestThread.Run(async () =>
    {
        var valid = SyntheticMsi("Editor");
        var invalid = ClassifierProgramFactory.Create("Editor sem comando", uninstallString: "");
        var component = ClassifierProgramFactory.Create("Componente técnico", isSystemComponent: true);
        var model = Create(new FakeProgramInventory(valid, invalid, component),
            new FakeUninstallExecutor(), new FakeHistoryService());
        await model.LoadCommand.ExecuteAsync(null);
        var invalidRow = model.Rows.Single(row => row.Program == invalid);
        Assert.Contains(invalidRow, model.Catalog);
        Assert.False(invalidRow.CanSelect);
        model.SelectedRow = invalidRow;
        Assert.Equal(LocalizationService.Source("Message.ThisEntryHasNoUninstallerInTheRegistry"),
            model.UninstallSelectedDisabledReason);

        model.AllSelected = true;
        Assert.Equal(1, model.SelectedCount);
        Assert.True(model.AllSelected);
        Assert.False(model.Rows.Single(row => row.Program == component).IsSelected);
    });

    [Theory]
    [InlineData("pt-BR", AppTheme.Light, 1.25)]
    [InlineData("pt-BR", AppTheme.Dark, 1.5)]
    [InlineData("pt-BR", AppTheme.Light, 2.0)]
    [InlineData("en-US", AppTheme.Dark, 1.25)]
    [InlineData("en-US", AppTheme.Light, 1.5)]
    [InlineData("en-US", AppTheme.Dark, 2.0)]
    public Task Wpf_confirmation_and_keyboard_work_at_scaled_viewports(string language, AppTheme theme, double scale) =>
        WpfTestThread.Run(async () =>
    {
        LocalizationService.Current.Apply(language);
        ThemeResources.Install(Application.Current.Resources, theme);
        using var path = new TemporaryExe();
        var auslogics = path.Program(Auslogics);
        var second = path.Program("Orchard Sync", ProgramKind.Nsis);
        var inventory = new MutableInventory(auslogics, second);
        var history = new FakeHistoryService();
        var executor = new InventoryExecutor(inventory);
        var model = Create(inventory, executor, history);
        var page = new UninstallersPage { DataContext = model };
        var window = new Window
        {
            Content = page, Width = 1680 / scale - 200, Height = 1000 / scale,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = (Brush)Application.Current.FindResource("WindowBgBrush"),
        };
        var tag = $"{language}-{theme}-{scale * 100:0}";
        var offers = new List<string>();
        var offerDeadline = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            window.Show();
            await model.LoadCommand.ExecutionTask!;
            var grid = Assert.IsType<DataGrid>(page.FindName("ProgramsGrid"));
            var row = model.Catalog.Single(item => item.Program == auslogics);
            var otherRow = model.Catalog.Single(item => item.Program == second);
            grid.SelectedItem = row;
            grid.CurrentCell = new DataGridCellInfo(row, grid.Columns[0]);
            await Settle();
            Assert.Contains(Descendants<TextBlock>(page), text => text.IsVisible &&
                text.Text == auslogics.RegistryPath);

            var checkbox = CheckboxFor(grid, row);
            var cell = (DataGridCell)VisualParent<DataGridCell>(checkbox);
            var keyboard = Keyboard.PrimaryDevice;
            cell.RaiseEvent(new KeyEventArgs(keyboard, PresentationSource.FromVisual(cell), 0, Key.Space)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Assert.True(row.IsSelected);
            Assert.Equal(1, model.SelectedCount);
            Assert.True(checkbox.IsChecked);
            Assert.True(checkbox.Focusable && checkbox.IsTabStop);

            grid.ScrollIntoView(row, grid.Columns[0]);
            await Settle();
            Assert.True(grid.Columns[0].ActualWidth >= 84);
            var center = checkbox.TranslatePoint(new Point(checkbox.ActualWidth / 2, checkbox.ActualHeight / 2), grid);
            Assert.Same(checkbox, VisualParent<CheckBox>(VisualTreeHelper.HitTest(grid, center).VisualHit));
            var action = Descendants<Button>(page).Single(button => button.Command == model.UninstallSelectedCommand);
            Assert.True(action.IsEnabled);
            Assert.Contains(Descendants<TextBlock>(page), text =>
                text.Text == LocalizationService.Current.Format("Common.SelectedCount", 1));
            if (Environment.GetEnvironmentVariable("WINCORE_UNINSTALL_VISUAL_DIR") is not null)
            {
                await Task.Delay(220);
                Capture(page, $"{tag}-selecionado", scale);
                if (scale == 2)
                {
                    var scroll = Assert.IsType<ScrollViewer>(page.FindName("PageScroll"));
                    scroll.ScrollToBottom();
                    await Settle();
                    Capture(page, $"{tag}-detalhes", scale);
                    scroll.ScrollToTop();
                    await Settle();
                }
            }

            var clicked = Assert.IsAssignableFrom<IInvokeProvider>(
                new ButtonAutomationPeer(action).GetPattern(PatternInterface.Invoke));
            var single = ReadDialogAsync(accept: false, screenshot: "confirmacao-individual");
            clicked.Invoke();
            Assert.Equal(new[] { Auslogics }, await single);
            await model.UninstallSelectedCommand.ExecutionTask!;
            Assert.Empty(executor.Launched);

            var selectAll = Descendants<CheckBox>(page).Single(box =>
                Equals(box.Content, LocalizationService.Current["Uninstaller.SelectVisible"]));
            Toggle(selectAll);
            await Settle();
            Assert.Equal(2, model.SelectedCount);
            Assert.True(CheckboxFor(grid, otherRow).IsChecked);
            var expected = new[] { Auslogics, "Orchard Sync" };
            var declined = ReadDialogAsync(accept: false, screenshot: "confirmacao");
            await model.UninstallSelectedCommand.ExecuteAsync(null);
            Assert.Equal(expected, await declined);
            Assert.Empty(executor.Launched);
            Assert.Empty(history.Records);
            Assert.Equal(2, model.SelectedCount);

            var approved = ReadDialogAsync(accept: true);
            offerDeadline.Restart();
            DeclineLeftoverOffers();
            await model.UninstallSelectedCommand.ExecuteAsync(null);
            Assert.Equal(expected, await approved);
            // The real page asks once per removed app, after the batch, and declining opens no leftover window.
            Assert.Equal(expected, offers.Order());
            Assert.DoesNotContain(Application.Current.Windows.OfType<ResidueScanWindow>(), item => item.IsVisible);
            Assert.Equal(expected, executor.Launched.Order());
            Assert.Equal(2, history.Records.Count);
            Assert.All(history.Records, item => Assert.Equal(HistoryStatus.Success, item.Status));
            Assert.Equal(2, inventory.Reads);
            Assert.Empty(model.Catalog);
            Assert.Equal(0, model.SelectedCount);
            Assert.False(action.IsEnabled);
        }
        finally
        {
            window.Close();
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }

        void DeclineLeftoverOffers() => Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            if (Application.Current.Windows.OfType<LocalizedDialog>().FirstOrDefault(item => item.IsVisible) is { } dialog)
            {
                dialog.UpdateLayout();
                offers.Add(new[] { Auslogics, second.Name }.Single(name =>
                    Descendants<TextBlock>(dialog).Any(text => text.Text.Contains(name, StringComparison.Ordinal))));
                if (offers.Count == 1) Capture((FrameworkElement)dialog.Content, $"{tag}-oferta-residuos", scale);
                dialog.DialogResult = false;
            }
            if (offers.Count < 2 && offerDeadline.Elapsed < TimeSpan.FromSeconds(20)) DeclineLeftoverOffers();
        }, DispatcherPriority.ApplicationIdle);

        Task<string[]> ReadDialogAsync(bool accept, string? screenshot = null) => Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            var dialog = Application.Current.Windows.OfType<UninstallConfirmDialog>().Single();
            try
            {
                dialog.UpdateLayout();
                var names = Descendants<TextBlock>(dialog).Where(text => text.IsVisible &&
                    (text.Text == Auslogics || text.Text == second.Name)).Select(text => text.Text).Order().ToArray();
                var confirm = Assert.IsType<Button>(dialog.FindName("ConfirmButton"));
                Assert.Equal(language == "en-US" ? "Uninstall" : "Desinstalar", confirm.Content);
                if (screenshot is not null)
                    Capture((FrameworkElement)dialog.Content, $"{tag}-{screenshot}", scale);
                if (accept)
                    confirm.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                return names;
            }
            finally
            {
                if (dialog.IsVisible) dialog.DialogResult = false;
            }
        }, DispatcherPriority.ApplicationIdle).Task;
    });

    [Fact]
    public Task Recycling_sorting_and_search_keep_checks_on_the_correct_rows() => WpfTestThread.Run(async () =>
    {
        var programs = Enumerable.Range(0, 130).Select(index => SyntheticMsi($"App {index:D3}")).ToArray();
        var model = Create(new FakeProgramInventory(programs), new FakeUninstallExecutor(), new FakeHistoryService());
        var page = new UninstallersPage { DataContext = model };
        var window = new Window { Content = page, Width = 850, Height = 560, ShowInTaskbar = false };
        try
        {
            window.Show();
            await model.LoadCommand.ExecutionTask!;
            var grid = Assert.IsType<DataGrid>(page.FindName("ProgramsGrid"));
            var first = model.Catalog.Single(row => row.Program == programs[0]);
            grid.ScrollIntoView(first);
            await Settle();
            Assert.True(grid.EnableRowVirtualization);
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(grid));
            Assert.InRange(Descendants<DataGridRow>(grid).Count(), 1, 50);
            Toggle(CheckboxFor(grid, first));
            await Settle();
            Assert.Equal(1, model.SelectedCount);

            grid.Items.SortDescriptions.Clear();
            grid.Items.SortDescriptions.Add(new(nameof(UninstallerItemRow.Name), System.ComponentModel.ListSortDirection.Descending));
            grid.ScrollIntoView(model.Catalog.Single(row => row.Program == programs[^1]));
            await Settle();
            Assert.Null(grid.ItemContainerGenerator.ContainerFromItem(first));
            Assert.All(Descendants<DataGridRow>(grid).Where(row => row.IsVisible)
                .SelectMany(row => Descendants<CheckBox>(row)), box => Assert.False(box.IsChecked));

            grid.ScrollIntoView(first);
            await Settle();
            Assert.True(CheckboxFor(grid, first).IsChecked);
            model.SearchText = "App 00";
            Assert.Equal(1, model.SelectedCount);
            model.AllSelected = true;
            Assert.Equal(10, model.SelectedCount);
            model.SelectedFilterIndex = 3;
            Assert.Equal(10, model.SelectedCount);
            model.SearchText = "App 129";
            Assert.Equal(0, model.SelectedCount);
            Assert.False(model.AllSelected);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(UninstallOutcome.Failure, HistoryStatus.Failure, UninstallRowStatus.Failed, 2)]
    [InlineData(UninstallOutcome.Cancelled, HistoryStatus.Cancelled, UninstallRowStatus.Cancelled, 1)]
    [InlineData(UninstallOutcome.Interrupted, HistoryStatus.Interrupted, UninstallRowStatus.Interrupted, 1)]
    public Task Failure_cancellation_and_delegation_keep_the_real_outcome(
        UninstallOutcome outcome, HistoryStatus historyStatus, UninstallRowStatus rowStatus, int launchCount) =>
        WpfTestThread.Run(async () =>
    {
        using var path = new TemporaryExe();
        var last = path.Program("Zeta", ProgramKind.Executable);
        var first = SyntheticMsi("Editor", "HKCU");
        var inventory = new MutableInventory(first, last);
        var executor = new InventoryExecutor(inventory, outcome);
        var history = new FakeHistoryService();
        var model = Create(inventory, executor, history);
        await model.LoadCommand.ExecuteAsync(null);
        model.AllSelected = true;
        model.ConfirmHandler = _ => Task.FromResult(true);
        await model.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(launchCount, executor.Launched.Count);
        Assert.Equal(historyStatus, history.Records[0].Status);
        Assert.Equal(rowStatus, model.Catalog.Single(row => row.Program == last).Status);
        Assert.Equal(0, model.SelectedCount);
        Assert.False(model.UninstallSelectedCommand.CanExecute(null));
    });

    [Fact]
    public Task New_global_pending_operation_blocks_execution_even_after_confirmation() => WpfTestThread.Run(async () =>
    {
        var inventory = new MutableInventory(SyntheticMsi("Editor"));
        var executor = new InventoryExecutor(inventory);
        var model = Create(inventory, executor, new FakeHistoryService());
        await model.LoadCommand.ExecuteAsync(null);
        var hasPending = false;
        model.PendingPackageChangeHint = () => hasPending ? "Fixture update is still running." : null;
        model.AllSelected = true;
        model.ConfirmHandler = _ =>
        {
            hasPending = true;
            model.NotifyPackageChangeState();
            return Task.FromResult(true);
        };
        await model.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Empty(executor.Launched);
        Assert.Equal(1, model.SelectedCount);
        Assert.False(model.UninstallSelectedCommand.CanExecute(null));
        Assert.Contains("Fixture update is still running.", model.UninstallSelectedDisabledReason);
        hasPending = false;
        model.NotifyPackageChangeState();
        Assert.True(model.UninstallSelectedCommand.CanExecute(null));
    });

    [Fact]
    public Task Exit_success_with_stale_inventory_leaves_the_entry_and_explains_it() => WpfTestThread.Run(async () =>
    {
        var product = SyntheticMsi("Editor");
        var history = new FakeHistoryService();
        var model = Create(new FakeProgramInventory(product), new FakeUninstallExecutor(), history);
        await model.LoadCommand.ExecuteAsync(null);
        model.AllSelected = true;
        model.ConfirmHandler = _ => Task.FromResult(true);
        await model.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(HistoryStatus.Success, Assert.Single(history.Records).Status);
        Assert.Equal(UninstallRowStatus.Success, Assert.Single(model.Catalog).Status);
        Assert.Contains(LocalizationService.Source("Message.Uninstaller.StillListed", product.Name), model.LogLines);
        Assert.Equal(0, model.SelectedCount);
    });

    [Fact]
    public Task Real_executor_preflight_refuses_unverified_machine_registration_without_launching() => WpfTestThread.Run(async () =>
    {
        using var path = new TemporaryExe();
        var product = path.Program(Auslogics, hive: "HKLM");
        var native = new WindowsFeaturesFixture.FakeWindowsTools();
        var model = Create(new FakeProgramInventory(product), new ProcessUninstallExecutor(), new FakeHistoryService(), native);
        await model.LoadCommand.ExecuteAsync(null);
        var row = Assert.Single(model.Catalog);
        model.SelectedRow = row;
        Assert.False(row.CanSelect);
        Assert.False(model.CanSelectAll);
        Assert.False(model.UninstallSelectedCommand.CanExecute(null));
        Assert.Equal(LocalizationService.Source(Environment.IsPrivilegedProcess
            ? "Message.Uninstaller.ElevatedSession" : "Message.Uninstaller.UntrustedMachineCommand"), model.UninstallSelectedDisabledReason);
        Assert.True(model.ShowWindowsUninstall);
        Assert.True(model.OpenWindowsInstalledAppsCommand.CanExecute(null));
        model.OpenWindowsInstalledAppsCommand.Execute(null);
        Assert.Equal((WinCoreManager.Services.WindowsFeatures.WindowsTool.InstalledApps, (string?)null), Assert.Single(native.Calls));
    });

    private static UninstallersViewModel Create(IProgramInventoryService inventory, IUninstallExecutor executor,
        FakeHistoryService history, WinCoreManager.Services.WindowsFeatures.IWindowsTools? native = null)
    {
        var planner = new UninstallPlanner();
        return new(inventory, planner, new(planner, executor, history), new FakeResidueAnalysisService(), native);
    }

    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static CheckBox CheckboxFor(DataGrid grid, UninstallerItemRow row)
    {
        var container = Assert.IsType<DataGridRow>(grid.ItemContainerGenerator.ContainerFromItem(row));
        return Assert.Single(Descendants<CheckBox>(container));
    }

    private static void Toggle(CheckBox box) =>
        Assert.IsAssignableFrom<IToggleProvider>(new CheckBoxAutomationPeer(box).GetPattern(PatternInterface.Toggle)).Toggle();

    private static DependencyObject VisualParent<T>(DependencyObject element) where T : DependencyObject
    {
        DependencyObject? parent = element;
        while (parent is not null && parent is not T)
            parent = VisualTreeHelper.GetParent(parent);
        return Assert.IsType<T>(parent);
    }

    private static void Capture(FrameworkElement element, string tag, double scale)
    {
        var directory = Environment.GetEnvironmentVariable("WINCORE_UNINSTALL_VISUAL_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        using (var drawing = surface.RenderOpen())
        {
            var area = new Rect(element.RenderSize);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("WindowBgBrush"), null, area);
            drawing.DrawRectangle(new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = area,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = area,
            }, null, area);
        }
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(directory);
        using var stream = File.Create(Path.Combine(directory, tag + ".png"));
        encoder.Save(stream);
    }

    private sealed class MutableInventory(params InstalledProgram[] programs) : IProgramInventoryService
    {
        private readonly List<InstalledProgram> _programs = [.. programs];
        public int Reads { get; private set; }

        public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            return Task.FromResult<IReadOnlyList<InstalledProgram>>(_programs.ToArray());
        }

        public void Remove(InstalledProgram program) => _programs.Remove(program);
    }

    private sealed class InventoryExecutor(MutableInventory inventory, UninstallOutcome first = UninstallOutcome.Success)
        : IUninstallExecutor
    {
        public List<string> Launched { get; } = [];

        public Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outcome = Launched.Count == 0 ? first : UninstallOutcome.Success;
            Launched.Add(plan.Program.Name);
            if (outcome == UninstallOutcome.Success) inventory.Remove(plan.Program);
            return Task.FromResult(new UninstallExecutionResult { Program = plan.Program, Outcome = outcome });
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private sealed class TemporaryExe : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"unins-{Guid.NewGuid():N}.exe");

        public TemporaryExe() => File.WriteAllText(_path, "Synthetic test data. This file cannot be executed.");

        public InstalledProgram Program(string name, ProgramKind kind = ProgramKind.InnoSetup, string hive = "HKCU") => new()
        {
            Name = name,
            Publisher = name == Auslogics ? "Auslogics Labs Pty Ltd" : "Fixture",
            DisplayVersion = "4.0",
            Origin = hive,
            Kind = kind,
            RegistryPath = $@"{hive}\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
            UninstallString = $"\"{_path}\"",
        };

        public void Dispose() => File.Delete(_path);
    }
}
