using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager;
using WinCoreManager.Models;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace VisualHarness;

internal static partial class Program
{
    private static DiskHealthReport Rc3Disks()
    {
        var page = new byte[512];
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), 315);
        page[3] = 100;
        page[4] = 10;
        page[5] = 6;
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(128), 1540);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(48), 35_086_000);
        var time = DateTimeOffset.Now;
        var disk = new InventoryDevice("disk/0", HardwareKind.Disk, "Samsung SSD 980 PRO (fixture)", time)
            .Add("Tipo", "NVMe", "Fixture").Add("Capacidade", 1_000_000_000_000UL, "Fixture", "B")
            .Add("Interface", "NVMe", "Fixture")
            .Build([new("disk/0/part/1", HardwareKind.Disk, "Partição 1", [], [new("volume/C:", HardwareKind.Disk, "Volume C:", [])]) { LocalizeName = true }]);
        var sata = new InventoryDevice("disk/1", HardwareKind.Disk, "SATA SSD (fixture)", time)
            .Add("Tipo", "SSD SATA", "Fixture").Add("Capacidade", 500_000_000_000UL, "Fixture", "B")
            .Add("Interface", "SATA", "Fixture").Build();
        var worn = (byte[])page.Clone();
        worn[5] = 78;
        BinaryPrimitives.WriteUInt64LittleEndian(worn.AsSpan(160), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(worn.AsSpan(1), 331);
        var second = new InventoryDevice("disk/2", HardwareKind.Disk, "KINGSTON SNV2S1000G with a deliberately long model name (fixture)", time)
            .Add("Tipo", "NVMe", "Fixture").Add("Capacidade", 1_000_000_000_000UL, "Fixture", "B")
            .Add("Interface", "NVMe", "Fixture")
            .Build([new("disk/2/part/1", HardwareKind.Disk, "Partição 1", [], [new("volume/D:", HardwareKind.Disk, "Volume D:", [])]) { LocalizeName = true }]);
        var hdd = new InventoryDevice("disk/3", HardwareKind.Disk, "ST2000DM008 (fixture)", time)
            .Add("Tipo", "HDD", "Fixture").Add("Capacidade", 2_000_000_000_000UL, "Fixture", "B")
            .Add("Interface", "SATA", "Fixture").Build();
        return new(ReadingState.Available,
        [
            new(disk, WindowsSmart.ParseNvme(page)),
            new(second, WindowsSmart.ParseNvme(worn)),
            new(sata, new(ReadingState.Restricted, DiskHealthState.Unknown, "", [])),
            new(hdd, WindowsSmart.ParseAta([], false, true, 34) with { Health = DiskHealthState.Healthy }),
            new(new InventoryDevice("disk/4", HardwareKind.Disk, "USB HDD (fixture)", time)
                    .Add("Tipo", "HDD", "Fixture").Add("Capacidade", 4_000_000_000_000UL, "Fixture", "B")
                    .Add("Interface", "USB", "Fixture").Build(),
                WindowsSmart.ParseAta([], true, true, 47)),
        ]);
    }

    private static async Task CaptureRc3(Window owner, MainViewModel model, string output, string tag, double scale)
    {
        var directory = Path.Combine(output, "captures");
        Directory.CreateDirectory(directory);

        void Save(FrameworkElement element, string name) =>
            Shot(element, Path.Combine(directory, $"{tag}-{name}.png"), scale);

        // Renderiza o elemento inteiro, inclusive o que está fora da área rolável.
        void SaveElement(FrameworkElement element, string name)
        {
            element.UpdateLayout();
            var area = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            // O VisualBrush enxerga o deslocamento do elemento no pai; a Viewbox absoluta compensa.
            var offset = VisualTreeHelper.GetOffset(element);
            var drawing = new DrawingVisual();
            using (var context = drawing.RenderOpen())
            {
                context.DrawRectangle((Brush)Application.Current.FindResource("WindowBgBrush"), null, area);
                context.DrawRectangle(new VisualBrush(element)
                {
                    Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(offset.X, offset.Y, area.Width, area.Height),
                    ViewportUnits = BrushMappingMode.Absolute, Viewport = area,
                }, null, area);
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(area.Width * scale),
                (int)Math.Ceiling(area.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(directory, $"{tag}-{name}.png"));
            encoder.Save(file);
        }

        void SaveDialog(Window window, string name)
        {
            var content = (FrameworkElement)window.Content;
            var parent = VisualTreeHelper.GetParent(content) as FrameworkElement;
            Save(parent ?? content, name);
        }

        model.NavItems.Single(item => item.ViewModel is DashboardViewModel).IsSelected = true;
        await Settle(400);
        var health = App.Services.GetRequiredService<DiskHealthViewModel>();
        await health.LoadAsync(false);
        await Settle(200);
        Save((FrameworkElement)owner.Content, "01-dashboard");
        var dashboardCard = Descendant<DiskHealthCard>(Descendant<DashboardPage>(owner));
        SaveElement(dashboardCard, "01b-dashboard-disks");
        if (Args.ContainsKey("disks-empty")) return;
        var width = double.Parse(Args.GetValueOrDefault("dialog-width", "740"),
            System.Globalization.CultureInfo.InvariantCulture);

        var detail = new DiskHealthDetailsWindow(health.Disks[0]) { Owner = owner, Width = width, Height = 535 };
        detail.Show();
        await Settle(200);
        SaveDialog(detail, "02-smart");
        detail.Close();

        model.NavItems.Single(item => item.ViewModel is MaintenanceViewModel).IsSelected = true;
        await Settle(250);
        var maintenance = Descendant<MaintenancePage>(owner);
        Descendant<DiskHealthCard>(maintenance).BringIntoView();
        await Settle(200);
        Save((FrameworkElement)owner.Content, "03-maintenance");
        SaveElement(Descendant<DiskHealthCard>(maintenance), "03b-maintenance-disks");
        if (Args.ContainsKey("disks-only")) return;

        model.NavItems.Single(item => item.ViewModel is UninstallersViewModel).IsSelected = true;
        await Settle(260);
        Save((FrameworkElement)owner.Content, "04-uninstaller");
        var catalog = OptionalWindowsApps.Classify(
        [
            Package("Microsoft.GetHelp"),
            Package("Microsoft.WindowsFeedbackHub"),
            Package("Microsoft.Windows.Photos"),
            Package("Microsoft.MicrosoftStickyNotes", false),
        ], [], new UninstallPlanner());
        var optional = new OptionalWindowsAppsWindow(_ => Task.FromResult(catalog), model.Uninstallers)
        { Owner = owner, Width = width, Height = 570 };
        optional.Show();
        await Settle(240);
        SaveDialog(optional, "05-optional");
        optional.Close();

        var program = new InstalledProgram { Name = "Fabrikam Editor (fixture)", Publisher = "Fabrikam" };
        var analysis = new Rc3ResidueEvidence(program);
        var residue = new ResidueScanWindow(new ResidueScanViewModel(program, [], analysis,
            App.Services.GetRequiredService<ResidueCleanupPlanner>(), App.Services.GetRequiredService<ResidueCleanupExecutor>(),
            App.Services.GetRequiredService<IResidueQuarantineService>()))
        { Owner = owner, Width = width, Height = 530 };
        residue.Show();
        await Settle(350);
        SaveDialog(residue, "06-residue-files");
        var tabs = (TabControl)residue.FindName("ResultTabs");
        tabs.SelectedIndex = 1;
        ((DataGrid)((TabItem)tabs.Items[1]).Content).SelectedIndex = 1;
        await Settle(220);
        SaveDialog(residue, "07-residue-registry");
        residue.Close();

        if (Args.ContainsKey("rc3-readonly"))
        {
            var packages = await OfficialAppx.ReadAsync(CancellationToken.None);
            var actual = await new DiskHealthService(App.Services.GetRequiredService<WindowsStorageProvider>(),
                new WindowsSmart()).ReadAsync(false);
            File.WriteAllLines(Path.Combine(directory, "readonly-summary.txt"),
            [
                $"AppX verified: {packages is not null}; total: {packages?.Count}; eligible metadata: {packages?.Count(item => item.Eligible)}",
                $"Physical inventory: {actual.State}; devices: {actual.Disks.Count}",
                .. actual.Disks.Select(disk => $"Disk {disk.Device.Id}: {disk.Sample.State}, {disk.Sample.Health}, SMART metrics: {disk.Sample.Metrics.Count}"),
            ]);
        }

        static OptionalPackage Package(string name, bool eligible = true) =>
            new(name + "_8wekyb3d8bbwe", name + "_1.0.0.0_x64__8wekyb3d8bbwe", "Microsoft Corporation", "1.0.0.0", eligible);
    }

    private sealed class Rc3ResidueEvidence(InstalledProgram program) : IResidueAnalysisService
    {
        public Task<ResidueScanReport> AnalyzeAsync(InstalledProgram application, CancellationToken cancellationToken) =>
            Task.FromResult(new ResidueScanReport { Program = application });

        public Task<ResidueScanResult> DetectAsync(InstalledProgram application, IReadOnlyList<InstalledProgram> allPrograms,
            CancellationToken cancellationToken) => Task.FromResult(new ResidueScanResult
        {
            Program = program,
            Items =
            [
                new() { Id = Guid.NewGuid(), Category = ResidueCategory.Folder, Location = @"C:\Fixture\Fabrikam\Editor",
                    Confidence = ResidueConfidence.High, SizeBytes = 2087158 },
                new() { Id = Guid.NewGuid(), Category = ResidueCategory.RegistryKey, Location = @"HKEY_CURRENT_USER\SOFTWARE\Fabrikam\Editor",
                    Confidence = ResidueConfidence.High, Is64BitView = true,
                    Justifications = [WinCoreManager.Localization.LocalizationService.Source("ResidueAdvanced.ProductCode")] },
                new() { Id = Guid.NewGuid(), Category = ResidueCategory.RegistryValue,
                    Location = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Run", Is64BitView = true,
                    Confidence = ResidueConfidence.Protected, ValueName = "Fabrikam Editor", ValueType = "REG_SZ",
                    Justifications = [WinCoreManager.Localization.LocalizationService.Source("ResidueAdvanced.ValuePathMatch")] },
            ],
        });
    }
}
