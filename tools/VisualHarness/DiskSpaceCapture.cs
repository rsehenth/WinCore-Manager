using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinCoreManager.Services.DiskSpace;
using WinCoreManager.Views;

namespace VisualHarness;

internal static partial class Program
{
    private static async Task CaptureDiskSpace(Window owner, string output, string tag, double scale)
    {
        await Settle(500);
        var open = (Button)Descendant<CleanupPage>(owner).FindName("AnalyzeSpaceButton");
        Shot((FrameworkElement)owner.Content, Path.Combine(output, $"{tag}-01-limpeza.png"), scale);
        var modal = new TaskCompletionSource();
        _ = owner.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var configuration = owner.OwnedWindows.OfType<DiskAnalysisDialog>().Single();
            try
            {
                await WaitFor(() => Control<Button>(configuration, "Begin").IsEnabled);
                Shot((FrameworkElement)configuration.Content, Path.Combine(output, $"{tag}-02-configuracao.png"), scale);
                modal.SetResult();
            }
            catch (Exception exception) { modal.SetException(exception); }
            finally { configuration.Close(); }
        }));
        open.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await modal.Task;

        const long GiB = 1024L * 1024 * 1024;
        var drive = new DiskVolume(@"C:\", "Windows", "synthetic-c", 512 * GiB, 196 * GiB);
        var target = new DiskScanTarget(drive);
        var request = new DiskAnalysisOptions([target], true, true);
        var result = DiskResult(drive);
        var firstFolder = result.Root.Children[0];
        var firstFile = result.Largest[0];
        var secondFile = result.Largest[1];
        var duplicated = secondFile with { Name = "archive-copy.zip" };
        using var step = new AutoResetEvent(false);
        var window = new DiskSpaceWindow(request, (_, report, token) =>
        {
            report(new(target, 1, 1, 18214, 1460, 84 * GiB, TimeSpan.FromSeconds(2),
                [new(firstFile.Path, firstFile.LogicalBytes), new(secondFile.Path, secondFile.LogicalBytes)],
                [new(firstFolder.Path, firstFolder.LogicalBytes)]));
            WaitHandle.WaitAny([step, token.WaitHandle], TimeSpan.FromSeconds(12));
            token.ThrowIfCancellationRequested();
            return new([result], [], true,
                new([new(secondFile.LogicalBytes, [new(drive, secondFile), new(drive, duplicated)])], 12, 1, 0),
                TimeSpan.FromSeconds(8));
        })
        {
            Owner = owner,
            Width = double.Parse(Args.GetValueOrDefault("disk-width", "1040"), CultureInfo.InvariantCulture),
            Height = double.Parse(Args.GetValueOrDefault("disk-height", "680"), CultureInfo.InvariantCulture),
        };

        window.Show();
        try
        {
            await WaitFor(() => Control<ItemsControl>(window, "PreviewFiles").Items.Count > 0);
            await Settle(200);
            Save("03-previa");
            step.Set();
            await WaitFor(() => Control<Grid>(window, "ResultArea").IsVisible);
            await Settle(200);
            Save("04-visao-geral");
            var tabs = Control<TabControl>(window, "ResultTabs");
            foreach (var (index, name) in new[] { (1, "05-pastas"), (2, "06-arquivos"), (3, "07-duplicados") })
            {
                tabs.SelectedIndex = index;
                await Settle(200);
                Save(name);
            }
        }
        finally { window.Close(); }

        static T Control<T>(Window dialog, string name) => (T)dialog.FindName(name);
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
            root.LogicalBytes, root.AllocatedBytes, TimeSpan.FromSeconds(28)))
        {
            LargestFolders = root.Children.OrderByDescending(folder => folder.LogicalBytes).ToArray()
        };

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
