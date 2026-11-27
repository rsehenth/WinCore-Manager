using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class DiskHealthCardTests
{
    private static DiskHealthRow Row(DiskHealthSample sample, string type = "NVMe", string bus = "NVMe")
    {
        var time = DateTimeOffset.Now;
        HardwareReading Field(string key, object value) => new("disk/0", LocalizationService.Source(key), value, "Fixture",
            ReadingState.Available, "", time);
        var letters = new HardwareDevice("volume/C:", HardwareKind.Disk, "Volume C:", []);
        var device = new HardwareDevice("disk/0", HardwareKind.Disk, "Fixture",
            [Field("Message.Type", type), Field("Interface", bus)],
            [new HardwareDevice("disk/0/part/1", HardwareKind.Disk, "Part", [], [letters])]);
        return new(new PhysicalDiskHealth(device, sample));
    }

    [Fact]
    public void Row_presents_badge_note_and_facts_from_what_the_drive_reported()
    {
        var restricted = Row(new(ReadingState.Restricted, DiskHealthState.Unknown, "", []), "SSD SATA", "SATA");
        Assert.Equal("Sem dados", restricted.BadgeText);
        Assert.Equal(restricted.ReadState, restricted.Note);
        Assert.False(restricted.HasRemaining);
        Assert.False(restricted.HasTemperature);
        Assert.Empty(restricted.Facts);
        Assert.Equal("Unidades C:", restricted.Volumes);
        Assert.Null(restricted.Capacity);

        var worn = Row(new(ReadingState.Available, DiskHealthState.Attention, "SMART",
        [
            new("PowerHours", "10 h"), new("DataWritten", "—"), new("Reallocated", "0x000000000001"),
            new("MediaErrors", "3", null, DiskHealthState.Attention),
        ], 50, 20));
        Assert.Equal("Atenção", worn.BadgeText);
        Assert.Equal("Faça backup dos arquivos importantes.", worn.Note);
        Assert.Equal(["10 h", "3"], worn.Facts.Select(fact => fact.Value));
        Assert.Equal([false, true], worn.Facts.Select(fact => fact.Flagged));

        var hdd = Row(new(ReadingState.Available, DiskHealthState.Healthy, "SMART", [], 30), "HDD", "USB");
        Assert.Equal("Este disco não informa desgaste.", hdd.Note);
        Assert.Equal("USB", Assert.Single(hdd.Facts).Value);
        Assert.Equal("HDD", hdd.TypeLabel);

        var unknown = Row(new(ReadingState.Available, DiskHealthState.Unknown, "SMART", [], 30, 90), "Desconhecido", "SATA");
        Assert.Equal("Desconhecido", unknown.BadgeText);
        Assert.True(unknown.HasUnknownType);
        Assert.Null(unknown.Note);
    }

    [Theory]
    [InlineData(false, 960, 2)]
    [InlineData(false, 600, 1)]
    [InlineData(true, 960, 1)]
    public Task Card_lays_out_columns_and_keeps_actions_reachable(bool detailed, int width, int columns) =>
        WpfTestThread.Run(async () =>
        {
            var fixture = Rc3FunctionalTests.TestDisks();
            var model = new DiskHealthViewModel(new DiskHealthService(_ => fixture, TimeProvider.System));
            await model.LoadAsync(false);
            var card = new DiskHealthCard { Detailed = detailed, DataContext = model };
            var window = new Window { Content = new ScrollViewer { Content = card }, Width = width, Height = 700 };
            try
            {
                window.Show();
                await Task.Delay(200);
                window.UpdateLayout();
                var list = Assert.IsType<ItemsControl>(card.FindName("DiskList"));
                Assert.Equal(model.Disks.Count, list.Items.Count);
                Assert.Equal(Math.Floor(list.ActualWidth / columns), card.ItemWidth);
                var buttons = Descendants<Button>(list).ToArray();
                Assert.Equal(model.Disks.Count, buttons.Length);
                Assert.All(buttons, button => Assert.True(button.Focusable && button.IsTabStop && button.ActualWidth > 0));
                Assert.All(buttons, button => Assert.NotNull(button.FocusVisualStyle));
                var refresh = Assert.IsType<Button>(card.FindName("RefreshButton"));
                Assert.True(refresh.Focusable);
                Assert.Same(model.RefreshCommand, refresh.Command);

                // "Mais detalhes" abre a janela do disco; o diálogo modal é fechado de dentro do próprio laço.
                string? opened = null;
                window.Activate();
                _ = card.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                {
                    var dialog = Application.Current.Windows.OfType<DiskHealthDetailsWindow>().Single();
                    opened = Assert.IsType<TextBlock>(dialog.FindName("Overview")).Text;
                    dialog.Close();
                });
                buttons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Contains(model.Disks[0].Model, opened);
                // Sem percentual não há ProgressBar visível: leitores de tela não anunciam "0".
                Assert.Equal(model.Disks.Count(disk => disk.HasRemaining),
                    Descendants<ProgressBar>(list).Count(bar => bar.IsVisible));
            }
            finally { window.Close(); }
        });

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
