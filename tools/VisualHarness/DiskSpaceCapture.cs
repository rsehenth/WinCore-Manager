using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WinCoreManager.Services.DiskSpace;
using WinCoreManager.Views;

namespace VisualHarness;

internal static partial class Program
{
    private static async Task CaptureDiskSpace(Window owner, string output, string tag, double scale)
    {
        await Settle(500);
        var cleanup = Descendant<CleanupPage>(owner);
        var open = (Button)cleanup.FindName("AnalyzeSpaceButton");
        Shot((FrameworkElement)owner.Content, Path.Combine(output, $"{tag}-01-limpeza.png"), scale);
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var real = owner.OwnedWindows.OfType<DiskSpaceWindow>().Single();
        await WaitFor(() => Control<ComboBox>(real, "VolumePicker").IsEnabled);
        real.Close();

        const long GiB = 1024L * 1024 * 1024;
        var drive = new DiskVolume(@"C:\", "Windows", "synthetic-c", 512 * GiB, 196 * GiB);
        var dataDrive = new DiskVolume(@"D:\", "Projetos", "synthetic-d", 1024 * GiB, 822 * GiB);
        var attempts = 0;
        using var step = new AutoResetEvent(false);
        var window = new DiskSpaceWindow(() => [drive, dataDrive], (volume, report, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                report?.Invoke(new(18214, 1460, 2, 3, 8, 0, 0, 84 * GiB, 81 * GiB, TimeSpan.FromSeconds(2)));
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
                token.ThrowIfCancellationRequested();
            }
            else
            {
                report?.Invoke(new(31237, 5184, 4, 6, 18, 0, 0, 164 * GiB, 155 * GiB, TimeSpan.FromSeconds(3)));
                WaitHandle.WaitAny([step, token.WaitHandle], TimeSpan.FromSeconds(12));
                token.ThrowIfCancellationRequested();
            }

            return DiskResult(volume);
        })
        {
            Owner = owner,
            Width = double.Parse(Args.GetValueOrDefault("disk-width", "1040"), CultureInfo.InvariantCulture),
            Height = double.Parse(Args.GetValueOrDefault("disk-height", "754"), CultureInfo.InvariantCulture),
        };

        window.Show();
        try
        {
            await WaitFor(() => Control<Button>(window, "StartButton").IsEnabled);
            Save("02-unidade");
            var volumePicker = Control<ComboBox>(window, "VolumePicker");
            volumePicker.IsDropDownOpen = true;
            await Settle(250);
            Save("03-unidades");
            volumePicker.IsDropDownOpen = false;

            Click("StartButton");
            await WaitFor(() => Control<ProgressBar>(window, "ScanProgress").IsVisible);
            await Settle(250);
            Save("04-progresso");
            Click("CancelButton");
            await WaitFor(() => Control<Button>(window, "StartButton").IsEnabled);
            Save("05-cancelada");

            Click("StartButton");
            step.Set();
            await WaitFor(() => Control<Grid>(window, "ResultArea").IsVisible);
            await Settle(250);
            Save("06-visao-geral");
            PrintWindow(window, Path.Combine(output, $"{tag}-06-janela-real.png"));
            var map = Control<Canvas>(window, "SpaceMap");
            var tile = map.Children.OfType<Button>().First();
            var tooltip = new ToolTip { Content = tile.ToolTip, PlacementTarget = tile, Placement = PlacementMode.Mouse };
            tooltip.IsOpen = true;
            await Settle(150);
            Shot(tooltip, Path.Combine(output, $"{tag}-07-tooltip.png"), scale);
            tooltip.IsOpen = false;
            tile.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle(150);
            Save("08-mapa-explorado");

            var tabs = Control<TabControl>(window, "ResultTabs");
            tabs.SelectedIndex = 1;
            await Settle(250);
            Save("09-arvore");
            tabs.SelectedIndex = 2;
            await Settle(250);
            Save("10-arquivos");

            var grid = Control<DataGrid>(window, "FileGrid");
            grid.SelectedIndex = 0;
            Control<TextBox>(window, "ExtensionFilter").Text = ".zip";
            await Settle(150);
            Save("11-filtro");
        }
        finally { window.Close(); }

        T Control<T>(Window dialog, string name) => (T)dialog.FindName(name);
        void Click(string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Save(string suffix) => Shot((FrameworkElement)window.Content, Path.Combine(output, $"{tag}-{suffix}.png"), scale);
    }

    private static DiskScanResult DiskResult(DiskVolume volume)
    {
        const long GiB = 1024L * 1024 * 1024;
        var root = new DiskDirectory(volume.Root, null) { LogicalBytes = 268 * GiB, AllocatedBytes = 232 * GiB, FileCount = 73182 };
        var users = Folder(root, "Users", 114, 22568);
        Folder(users, "Projects", 71, 5836);
        Folder(users, "Documents", 17, 8506);
        Folder(users, "Pictures", 12, 7619);
        var games = Folder(root, "Games", 53, 12803);
        Folder(root, "Windows", 40, 18913);
        Folder(root, "Program Files", 20, 12215);
        var archives = Folder(root, "Archive", 15, 1035);
        Folder(root, "ProgramData", 8, 5168);
        Folder(root, "System Volume Information", 0, 0).Inaccessible = true;

        var date = new DateTime(2026, 11, 10, 10, 30, 0, DateTimeKind.Local).ToFileTime();
        DiskFile[] largest =
        [
            new(root, "workspace-disk.vhdx", 11 * GiB, 8 * GiB, date),
            new(archives, "archive-2026.zip", 8 * GiB, 8 * GiB, date),
            new(users, "media-backup.zip", 7 * GiB, 7 * GiB, date),
            new(root, "system-report.img", 4 * GiB, 3 * GiB, date),
            new(games, "resources.pak", 3 * GiB, 3 * GiB, date),
            new(archives, "release-symbols.zip", 2 * GiB, 2 * GiB, date),
            .. Enumerable.Range(1, 124).Select(index => new DiskFile(games, $"asset-{index:000}.pak",
                (130 - index) * 1024L * 1024, (130 - index) * 1024L * 1024, date)),
        ];
        return new(volume, root, largest, new(root.FileCount, 8937, 17, 48, 32, 0, 0,
            root.LogicalBytes, root.AllocatedBytes, TimeSpan.FromSeconds(28)));

        DiskDirectory Folder(DiskDirectory parent, string name, long gib, long files)
        {
            var folder = parent.Add(name);
            folder.LogicalBytes = gib * GiB;
            folder.FileCount = files;
            return folder;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var retry = 0; !condition(); retry++)
        {
            if (retry == 80)
                throw new TimeoutException("The disk space window did not reach the expected state.");
            await Settle(50);
        }
    }

    private static T Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (Descendant<T>(child) is { } descendant)
                return descendant;
        }
        return null!;
    }
}
