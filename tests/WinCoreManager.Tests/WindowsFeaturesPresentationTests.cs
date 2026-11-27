using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class WindowsFeaturesPresentationTests
{
    [Theory]
    [InlineData(840, 420, 1.0)]
    [InlineData(1160, 760, 1.0)]
    [InlineData(1500, 900, 1.0)]
    [InlineData(840, 420, 1.25)]
    [InlineData(1160, 760, 1.25)]
    [InlineData(1500, 900, 1.25)]
    [InlineData(840, 420, 1.5)]
    [InlineData(1160, 760, 1.5)]
    [InlineData(1500, 900, 1.5)]
    [InlineData(840, 420, 2.0)]
    public Task Renderiza_novas_paginas_em_tres_tamanhos_e_escalas(int width, int height, double scale) =>
        WpfTestThread.Run(async () =>
        {
            var state = new WindowsFeaturesFixture();
            state.System.Restricted.Add(TweakSetting.HiddenFiles);
            var direct = new CleanupService(state.Disk, state.Tools, new FakeHistoryService())
            {
                DirectFiles = new([new(CleanupFiles.UserDumps, Path.Combine(Path.GetTempPath(), $"WinCore-absent-{Guid.NewGuid():N}"),
                    ".dmp", TimeSpan.Zero, false)]),
            };
            var cleanup = new WinCoreManager.ViewModels.CleanupViewModel(direct, state.Confirmation);
            await cleanup.RefreshCommand.ExecuteAsync(null);
            await state.TweaksPage.RefreshCommand.ExecuteAsync(null);
            var disk = new CleanupPage { DataContext = cleanup };
            var tweaks = new TweaksPage { DataContext = state.TweaksPage };

            var contentWidth = width - 240; // Same fixed sidebar width as the application window.
            var size = new Size(contentWidth, height);
            AssertLayout(disk, "CleanupScroll", size, scale);
            var empty = Assert.IsType<Button>(disk.FindName("CleanFilesButton"));
            var emptyRecycle = Assert.IsType<Button>(disk.FindName("EmptyNativeRecycleButton"));
            Assert.False(empty.IsEnabled);
            Assert.False(emptyRecycle.IsEnabled);
            Assert.Single(Assert.IsType<ItemsControl>(disk.FindName("CleanupCategories")).Items);
            Assert.Single(Assert.IsType<ItemsControl>(disk.FindName("NativeCleanupCategories")).Items);
            cleanup.Recycle!.IsSelected = true;
            Assert.True(emptyRecycle.IsEnabled);
            Assert.False(empty.IsEnabled);

            AssertLayout(tweaks, "TweaksScroll", size, scale);
            Assert.Equal(5, Assert.IsType<ItemsControl>(tweaks.FindName("PowerRows")).Items.Count);
            Assert.Null(tweaks.FindName("EffectRows"));
            Assert.True(Assert.IsType<Button>(tweaks.FindName("OpenPerformanceButton")).IsEnabled);
            var folder = Assert.IsType<ItemsControl>(tweaks.FindName("FolderRows"));
            Assert.Equal(2, folder.Items.Count);
            Assert.Contains("política", state.TweaksPage.FolderOptions[^1].Details);
            Assert.False(Assert.IsType<Button>(tweaks.FindName("UndoTweakButton")).IsEnabled);
            Assert.Contains("Último ponto", Assert.IsType<TextBlock>(tweaks.FindName("RestoreSummary")).Text);
            Assert.Empty(state.Disk.Requested);
            Assert.Empty(state.System.Writes);
            Assert.Empty(state.Tools.Calls);
            Assert.Empty(state.Confirmation.Requests);
        });

    private static void AssertLayout(UserControl page, string scrollName, Size size, double scale)
    {
        VisualTreeHelper.SetRootDpi(page, new DpiScale(scale, scale));
        page.Measure(size);
        page.Arrange(new Rect(size));
        page.UpdateLayout();
        Assert.Equal(scale, VisualTreeHelper.GetDpi(page).DpiScaleX);
        var scroll = Assert.IsType<ScrollViewer>(page.FindName(scrollName));
        Assert.InRange(scroll.ViewportWidth, size.Width - 24, size.Width);
        Assert.InRange(scroll.ViewportHeight, size.Height - 24, size.Height);
        Assert.Equal(0, scroll.ScrollableWidth);
        if (size.Height <= 420) Assert.True(scroll.ScrollableHeight > 0);

        var image = new RenderTargetBitmap((int)(size.Width * scale), (int)(size.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        image.Render(page);
        var pixels = new byte[image.PixelWidth * image.PixelHeight * 4];
        image.CopyPixels(pixels, image.PixelWidth * 4, 0);
        var painted = 0;
        for (var index = 3; index < pixels.Length; index += 4)
        {
            if (pixels[index] > 200) painted++;
        }
        Assert.True(painted > pixels.Length / 40);
    }
}

