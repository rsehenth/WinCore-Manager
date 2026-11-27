using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.DiskSpace;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class AdvancedDiskAnalysisTests
{
    [Fact]
    public void Quick_scan_finds_shallow_files_and_reports_omitted_descendants()
    {
        var disk = Directory.CreateTempSubdirectory("wincore-advanced-");
        try
        {
            var folder = disk.CreateSubdirectory("large");
            File.WriteAllBytes(Path.Combine(folder.FullName, "visible.bin"), new byte[32_000]);
            var deep = folder.CreateSubdirectory("b/c/d/e");
            File.WriteAllBytes(Path.Combine(deep.FullName, "deep.bin"), new byte[64_000]);
            var snapshots = new List<DiskAnalysisProgress>();
            var catalog = new DiskVolumeCatalog();
            var volume = Assert.IsType<DiskVolume>(catalog.Find(Path.GetPathRoot(disk.FullName)!));
            var target = new DiskScanTarget(volume, disk.FullName);
            var analysis = new DiskAnalysis();
            var quick = analysis.Scan(new([target], true, false), snapshots.Add, default);
            var full = analysis.Scan(new([target], false, false), _ => { }, default);

            Assert.True(quick.Accelerated);
            Assert.False(full.Accelerated);
            var quickVolume = Assert.Single(quick.Volumes);
            var fullVolume = Assert.Single(full.Volumes);
            Assert.Equal("visible.bin", Assert.Single(quickVolume.Largest).Name);
            Assert.Equal("deep.bin", fullVolume.Largest[0].Name);
            Assert.Equal(32_000, quickVolume.Root.LogicalBytes);
            Assert.Equal(96_000, fullVolume.Root.LogicalBytes);
            Assert.Equal(1, quickVolume.Progress.OmittedFolders);
            Assert.True(quickVolume.Progress.Partial);
            Assert.Equal(0, fullVolume.Progress.OmittedFolders);
            Assert.True(fullVolume.Progress.Directories > quickVolume.Progress.Directories);
            Assert.Equal("large", quickVolume.LargestFolders[0].Name);
            Assert.Empty(snapshots[0].PreviewFiles);
            Assert.Contains(snapshots, item => item.PreviewFiles.Any(file => file.Path.EndsWith("visible.bin", StringComparison.Ordinal)));
            Assert.Contains(snapshots, item => item.PreviewFolders.Any(entry => entry.Path == folder.FullName));
            using var stop = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => analysis.Scan(new([target], true, false), _ => stop.Cancel(), stop.Token));
        }
        finally { disk.Delete(recursive: true); }
    }

    [Fact]
    public void Duplicates_use_content_confirm_alias_names_and_leave_files_unchanged()
    {
        var disk = Directory.CreateTempSubdirectory("wincore-duplicates-");
        try
        {
            var first = Path.Combine(disk.FullName, "alpha.bin");
            var second = Path.Combine(disk.CreateSubdirectory("sub").FullName, "renamed.dat");
            var other = Path.Combine(disk.FullName, "different.bin");
            File.WriteAllBytes(first, [10, 20, 30]);
            File.WriteAllBytes(second, [10, 20, 30]);
            File.WriteAllBytes(other, [11, 21, 31]);

            var volume = Assert.IsType<DiskVolume>(new DiskVolumeCatalog().Find(Path.GetPathRoot(disk.FullName)!));
            var request = new DiskAnalysisOptions([new(volume, disk.FullName)], false, true);
            var analysis = new DiskAnalysis();
            var snapshots = new List<DiskAnalysisProgress>();
            var result = analysis.Scan(request, snapshots.Add, default);
            var reviewed = Assert.IsType<DuplicateScanResult>(result.Duplicates);
            var group = Assert.Single(reviewed.Groups);
            Assert.Equal(new[] { first, second }.Order(), group.Files.Select(file => file.File.Path).Order());
            Assert.Equal(3, group.Bytes);
            Assert.Equal(3, reviewed.Candidates);
            Assert.Equal(0, reviewed.Inaccessible);
            Assert.Contains(snapshots, state => state is { HashChecked: 3, HashTotal: 3 });
            Assert.All(new[] { first, second, other }, path => Assert.True(File.Exists(path)));
            using var stop = new CancellationTokenSource();
            Assert.Throws<OperationCanceledException>(() => analysis.Scan(request, state =>
            {
                if (state.HashTotal.HasValue)
                    stop.Cancel();
            }, stop.Token));
        }
        finally { disk.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("pt-BR", AppTheme.Light, 1.25)]
    [InlineData("pt-BR", AppTheme.Light, 1.5)]
    [InlineData("pt-BR", AppTheme.Light, 2.0)]
    [InlineData("pt-BR", AppTheme.Dark, 1.25)]
    [InlineData("pt-BR", AppTheme.Dark, 1.5)]
    [InlineData("pt-BR", AppTheme.Dark, 2.0)]
    [InlineData("en-US", AppTheme.Light, 1.25)]
    [InlineData("en-US", AppTheme.Light, 1.5)]
    [InlineData("en-US", AppTheme.Light, 2.0)]
    [InlineData("en-US", AppTheme.Dark, 1.25)]
    [InlineData("en-US", AppTheme.Dark, 1.5)]
    [InlineData("en-US", AppTheme.Dark, 2.0)]
    public Task Modal_and_partial_preview_render_and_results_remain_interactive(string language, AppTheme theme, double scale) =>
        WpfTestThread.Run(async () =>
        {
            var previousTheme = ThemeResources.Current;
            ThemeResources.Install(Application.Current.Resources, theme);
            LocalizationService.Current.Apply(language);
            using var continueScan = new ManualResetEventSlim();
            var volume = new DiskVolume(@"C:\", "Fixture", "fixture", 10_000, 2_000);
            var target = new DiskScanTarget(volume, @"C:\Fixture");
            var dialog = new DiskAnalysisDialog(() => [volume], selectFolder: _ => target.Folder);
            var settings = new TaskCompletionSource();
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    await WaitUntil(() => Control<Button>(dialog, "Begin").IsEnabled);
                    Render(Assert.IsAssignableFrom<FrameworkElement>(dialog.Content), scale);
                    Control<RadioButton>(dialog, "AllDrives").IsChecked = true;
                    Assert.False(Control<ComboBox>(dialog, "Drives").IsEnabled);
                    Assert.True(Control<Button>(dialog, "Begin").IsEnabled);
                    Control<RadioButton>(dialog, "OneFolder").IsChecked = true;
                    Assert.False(Control<Button>(dialog, "Begin").IsEnabled);
                    Click(dialog, "BrowseButton");
                    Control<CheckBox>(dialog, "Fast").IsChecked = true;
                    Control<CheckBox>(dialog, "Duplicates").IsChecked = true;
                    Click(dialog, "Begin");
                    settings.SetResult();
                }
                catch (Exception error) { settings.SetException(error); dialog.Close(); }
            };
            var accepted = dialog.ShowDialog();
            await settings.Task;
            Assert.True(accepted);
            var options = Assert.IsType<DiskAnalysisOptions>(dialog.Options);
            Assert.Equal(target, Assert.Single(options.Targets));
            Assert.True(options.Accelerated && options.FindDuplicates);

            var root = new DiskDirectory(target.Folder!, null) { LogicalBytes = 5000 };
            var folder = root.Add("data");
            folder.LogicalBytes = 5000;
            var file = new DiskFile(folder, "largest.bin", 5000, 5000, 0);
            var counts = new DiskScanProgress(1, 2, 0, 0, 0, 0, 0, 5000, 5000, TimeSpan.FromSeconds(1));
            var completed = new DiskScanResult(volume, root, [file], counts) { LargestFolders = [folder] };
            var window = new DiskSpaceWindow(options, (_, report, token) =>
            {
                report(new(target, 1, 1, 1, 2, 5000, TimeSpan.FromSeconds(1), [new(file.Path, 5000)], [new(folder.Path, 5000)]));
                continueScan.Wait(token);
                return new([completed], [], true, new([], 0, 0, 0), TimeSpan.FromSeconds(1));
            })
            {
                Width = 680, Height = 490, ShowActivated = false
            };
            try
            {
                window.Show();
                await WaitUntil(() => Control<ItemsControl>(window, "PreviewFiles").Items.Count == 1);
                Assert.Single(Control<ItemsControl>(window, "PreviewFolders").Items);
                Assert.True(Control<ScrollViewer>(window, "AnalysisPreview").IsVisible);
                Render(Assert.IsAssignableFrom<FrameworkElement>(window.Content), scale);
                continueScan.Set();
                await WaitUntil(() => Control<Grid>(window, "ResultArea").IsVisible);
                Assert.False(Control<ScrollViewer>(window, "AnalysisPreview").IsVisible);
                Assert.Equal(Visibility.Visible, Control<TabItem>(window, "DuplicatesTab").Visibility);
                Control<TabControl>(window, "ResultTabs").SelectedIndex = 1;
                Assert.Single(Control<DataGrid>(window, "FolderGrid").Items);
                Control<TextBox>(window, "FolderFilter").Text = "unmatched";
                Assert.Empty(Control<DataGrid>(window, "FolderGrid").Items);
                Control<TextBox>(window, "FolderFilter").Text = "data";
                Assert.Single(Control<DataGrid>(window, "FolderGrid").Items);
                Render(Assert.IsAssignableFrom<FrameworkElement>(window.Content), scale);
            }
            finally
            {
                continueScan.Set();
                window.Close();
                ThemeResources.Install(Application.Current.Resources, previousTheme);
            }
        });

    private static void Render(FrameworkElement element, double scale)
    {
        element.UpdateLayout();
        Assert.True(element.ActualWidth > 300 && element.ActualHeight > 200);
        var available = new Size(element.ActualWidth, element.ActualHeight);
        element.Measure(available);
        element.Arrange(new Rect(available));
        var frame = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale),
            (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        frame.Render(element);
        Assert.Equal(scale * 96, frame.DpiX, 1);
    }

    private static T Control<T>(Window window, string name) => Assert.IsType<T>(window.FindName(name));
    private static void Click(Window window, string name) =>
        Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
