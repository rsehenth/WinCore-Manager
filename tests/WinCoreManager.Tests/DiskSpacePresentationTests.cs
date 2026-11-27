using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.DiskSpace;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class DiskSpacePresentationTests
{
    [Fact]
    public void Map_preserves_proportions_and_groups_remaining_bytes()
    {
        var root = new DiskDirectory(@"C:\", null) { LogicalBytes = 1000 };
        for (var i = 0; i < 40; i++)
            root.Add($"folder-{i:00}").LogicalBytes = 20;
        DiskFile[] ranking = [new(root, "large.zip", 100, 96, 0), new(root, "small.txt", 20, 20, 0)];
        var items = DiskSpaceLayout.Items(root, ranking);
        Assert.Equal(DiskSpaceLayout.MapLimit, items.Length);
        Assert.Equal(1000, items.Sum(item => item.Bytes));
        Assert.Equal(300, Assert.Single(items, item => item.Target is null).Bytes);
        var area = new Rect(0, 0, 600, 300);
        var bounds = DiskSpaceLayout.Arrange(items, area);
        for (var i = 0; i < bounds.Length; i++)
        {
            Assert.True(area.Contains(bounds[i]));
            Assert.Equal(items[i].Bytes * 180, bounds[i].Width * bounds[i].Height, 4);
            for (var j = 0; j < i; j++)
            {
                var intersection = Rect.Intersect(bounds[i], bounds[j]);
                Assert.True(intersection.IsEmpty || intersection.Width * intersection.Height < 0.00001);
            }
        }
        Assert.Equal("large.zip", Assert.Single(DiskSpaceLayout.FilterFiles(ranking, "LARGE", ".ZIP")).Name);
    }

    [Theory]
    [InlineData(AppTheme.Dark, "pt-BR", 960)]
    [InlineData(AppTheme.Light, "en-US", 610)]
    public Task Window_browses_folders_filters_ranks_and_routes_actions(AppTheme theme, string language, int width) =>
        WpfTestThread.Run(async () =>
        {
            var previousTheme = ThemeResources.Current;
            ThemeResources.Install(Application.Current.Resources, theme);
            LocalizationService.Current.Apply(language);
            var drive = new DiskVolume(@"C:\", "Fixture", "fixed", 10_000, 2_000);
            var opened = (DiskDirectory?)null;
            var openedFile = (DiskFile?)null;
            var copied = "";
            var scans = 0;
            var window = new DiskSpaceWindow(() => [drive], (selected, _, _) =>
            {
                Interlocked.Increment(ref scans);
                return Result(selected);
            }, (_, folder, file) =>
            {
                opened = folder;
                openedFile = file;
                return true;
            }, path => copied = path)
            {
                Width = width,
                Height = 550,
            };

            try
            {
                window.Show();
                await Until(() => Control<Button>(window, "StartButton").IsEnabled);
                Assert.Equal(0, Volatile.Read(ref scans));
                Assert.Equal(drive, Control<ComboBox>(window, "VolumePicker").SelectedItem);
                Assert.Equal(language == "en-US" ? "Advanced Space Analysis" : "Análise Avançada de Espaço", window.Title);
                Click(window, "StartButton");
                await Until(() => Control<Grid>(window, "ResultArea").IsVisible);

                var map = Control<Canvas>(window, "SpaceMap");
                var folderTile = map.Children.OfType<Button>().First();
                var tooltip = Assert.IsType<TextBlock>(folderTile.ToolTip).Text;
                Assert.Contains(@"C:\Projects", tooltip);
                Assert.Contains("Projects", AutomationProperties.GetName(folderTile));
                Assert.Equal(80, Control<ProgressBar>(window, "VolumeBar").Value);

                folderTile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(@"C:\Projects", Control<TextBlock>(window, "CurrentPath").Text);
                Assert.Equal(@"C:\Projects", Control<TextBlock>(window, "SelectionText").Text);
                Assert.True(Control<Button>(window, "UpButton").IsEnabled);
                Click(window, "UpButton");
                Assert.Equal(@"C:\", Control<TextBlock>(window, "CurrentPath").Text);
                var tabs = Control<TabControl>(window, "ResultTabs");
                tabs.SelectedIndex = 1;
                window.UpdateLayout();
                Assert.Equal(2, Control<DataGrid>(window, "FolderGrid").Items.Count);
                Assert.NotNull(Control<TreeView>(window, "FolderTree").SelectedItem);
                var root = Assert.IsType<TreeViewItem>(Control<TreeView>(window, "FolderTree").ItemContainerGenerator.ContainerFromIndex(0));
                var branch = Assert.IsType<TreeViewItem>(root.ItemContainerGenerator.ContainerFromIndex(0));
                branch.IsExpanded = true;
                Assert.Single(branch.Items);
                branch.IsSelected = true;
                Assert.Equal(@"C:\Projects", Control<TextBlock>(window, "CurrentPath").Text);

                tabs.SelectedIndex = 2;
                var files = Control<DataGrid>(window, "FileGrid");
                Assert.Equal(100, files.Items.Count);
                Click(window, "MoreFilesButton");
                Assert.Equal(115, files.Items.Count);
                Control<TextBox>(window, "NameFilter").Text = "movie";
                Control<TextBox>(window, "ExtensionFilter").Text = ".ZIP";
                Assert.Single(files.Items);
                files.SelectedIndex = 0;
                Click(window, "CopyButton");
                Click(window, "ExplorerButton");
                Assert.Equal(@"C:\Projects\movie.zip", copied);
                Assert.Equal("Projects", opened?.Name);
                Assert.Equal("movie.zip", openedFile?.Name);
                Assert.Equal(1, Volatile.Read(ref scans));
            }
            finally
            {
                window.Close();
                ThemeResources.Install(Application.Current.Resources, previousTheme);
            }
        });

    [Fact]
    public Task Close_waits_until_cancelled_scan_releases_the_worker() => WpfTestThread.Run(async () =>
    {
        var drive = new DiskVolume(@"C:\", "Fixture", "fixed", null, null);
        using var started = new ManualResetEventSlim();
        using var cancelled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var window = new DiskSpaceWindow(() => [drive], (_, progress, token) =>
        {
            progress?.Invoke(new(21, 2, 0, 0, 0, 0, 0, 12, 4, TimeSpan.FromSeconds(1)));
            started.Set();
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(6));
            cancelled.Set();
            release.Wait(TimeSpan.FromSeconds(6));
            token.ThrowIfCancellationRequested();
            return Result(drive);
        });
        window.Show();
        try
        {
            await Until(() => Control<Button>(window, "StartButton").IsEnabled);
            Click(window, "StartButton");
            await Until(() => started.IsSet && Control<TextBlock>(window, "StatusText").Text.Contains("21"));
            Assert.True(Control<ProgressBar>(window, "ScanProgress").IsVisible);
            Assert.False(Control<ComboBox>(window, "VolumePicker").IsEnabled);
            Click(window, "CancelButton");
            await Until(() => cancelled.IsSet);
            window.Close();
            Assert.True(window.IsVisible);
            release.Set();
            await Until(() => !window.IsVisible);
        }
        finally
        {
            release.Set();
            if (window.IsVisible)
                window.Close();
        }
    });

    [Fact]
    public Task Cleanup_button_opens_configuration_without_starting_a_scan() => WpfTestThread.Run(() =>
    {
        LocalizationService.Current.Apply("pt-BR");
        var page = new CleanupPage();
        var owner = new Window { Content = page, Width = 780, Height = 600, ShowInTaskbar = false };
        owner.Show();
        try
        {
            var button = Assert.IsType<Button>(page.FindName("AnalyzeSpaceButton"));
            Assert.Equal("Análise Avançada de Espaço", AutomationProperties.GetName(button));
            DiskAnalysisDialog? shown = null;
            owner.Dispatcher.BeginInvoke(new Action(() =>
            {
                shown = owner.OwnedWindows.OfType<DiskAnalysisDialog>().SingleOrDefault();
                shown?.Close();
            }));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.NotNull(shown);
            Assert.Null(shown.Options);
            Assert.Empty(owner.OwnedWindows.OfType<DiskSpaceWindow>());
        }
        finally { owner.Close(); }
        return Task.CompletedTask;
    });

    private static DiskScanResult Result(DiskVolume drive)
    {
        var root = new DiskDirectory(drive.Root, null) { LogicalBytes = 1000, FileCount = 115 };
        var projects = root.Add("Projects");
        projects.LogicalBytes = 840;
        projects.DirectBytes = 700;
        projects.Add("Archive").LogicalBytes = 140;
        root.Add("Other folder").LogicalBytes = 160;
        var files = new[] { new DiskFile(projects, "movie.zip", 700, 640, 0) }
            .Concat(Enumerable.Range(0, 114).Select(index => new DiskFile(root, $"sample{index:000}.txt", 0, 0, index))).ToArray();
        return new(drive, root, files, new(115, 4, 0, 0, 0, 0, 0, 1000, 900, TimeSpan.FromSeconds(3)));
    }

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));

    private static void Click(Window window, string name) =>
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(20);

        Assert.True(condition());
    }
}
