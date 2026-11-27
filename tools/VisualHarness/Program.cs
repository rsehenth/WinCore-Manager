using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace VisualHarness;

// Uso: VisualHarness --out <pasta> [--size 1160x760] [--scale 1.0] [--tag nome] [--theme Dark|Light] [--pages 0,1,..] [--wait 2500]
internal static partial class Program
{
    private static readonly Dictionary<string, string> Args = new();

    [STAThread]
    private static int Main(string[] argv)
    {
        for (var i = 0; i < argv.Length - 1; i += 2) Args[argv[i].TrimStart('-')] = argv[i + 1];
        if (Args.TryGetValue("disk-benchmark", out var count))
            return MeasureDiskScan(int.Parse(count), Args["out"]);
        var app = new App(services =>
        {
            services.AddSingleton<IWinGetService, FixtureWinGet>();
            if (Args.ContainsKey("rc3"))
            {
                services.AddSingleton(_ => new DiskHealthService(_ => Args.ContainsKey("disks-empty")
                    ? new DiskHealthReport(ReadingState.NoReading, []) : Rc3Disks(), TimeProvider.System));
                services.AddSingleton<IResidueQuarantineService>(sp => new ResidueQuarantineService(
                    Path.Combine(Args["out"], "isolated-quarantine"), sp.GetRequiredService<IResidueRegistryReader>(),
                    sp.GetRequiredService<IResidueRegistryWriter>(), sp.GetRequiredService<ResiduePathRules>()));
            }
            if (!Args.ContainsKey("startup-readonly")) services.AddSingleton<IStartupInventory, FixtureStartup>();
            services.AddSingleton(_ => new WinGetInterruptionStore(Path.Combine(Args["out"], "isolated-receipts"), () => null));
            services.AddSingleton<ISettingsService>(_ => new SettingsService(Path.Combine(Args["out"], "isolated-settings"),
                Path.Combine(Args["out"], "isolated-local")));
            services.AddSingleton<IProgramInventoryService, FixturePrograms>();
            if (Args.ContainsKey("activation-fixture"))
            {
                services.AddSingleton<IWindowsActivationService, FixtureActivation>();
            }
        });
        app.InitializeComponent();
        app.Startup += (_, _) => app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
        {
            try { await RunAsync(app); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Args["out"], "harness-error.txt"), ex.ToString()); }
            finally { app.Shutdown(); }
        }));
        return app.Run();
    }

    private static async Task RunAsync(App app)
    {
        var outDir = Args["out"];
        Directory.CreateDirectory(outDir);
        var size = Args.GetValueOrDefault("size", "1160x760").Split('x');
        var scale = double.Parse(Args.GetValueOrDefault("scale", "1"), System.Globalization.CultureInfo.InvariantCulture);
        var tag = Args.GetValueOrDefault("tag", "shot");
        var wait = int.Parse(Args.GetValueOrDefault("wait", "2500"));
        var window = (MainWindow)app.MainWindow;
        var vm = (MainViewModel)window.DataContext;

        if (Args.TryGetValue("theme", out var theme))
        {
            ApplyTheme(theme);
        }
        if (Args.TryGetValue("language", out var language))
        {
            vm.Settings.SelectedLanguageOption = vm.Settings.Languages.Single(option => option.Code == language);
        }

        window.MinWidth = 0; window.MinHeight = 0;
        window.Width = double.Parse(size[0]); window.Height = double.Parse(size[1]);
        await Settle(300);

        var items = vm.NavItems.Append(vm.AboutNavItem).ToList();
        if (Args.ContainsKey("rc3"))
        {
            await CaptureRc3(window, vm, outDir, tag, scale);
            return;
        }
        if (Args.ContainsKey("disk-space"))
        {
            items.Single(item => item.ViewModel is CleanupViewModel).IsSelected = true;
            await CaptureDiskSpace(window, outDir, tag, scale);
            return;
        }
        var only = Args.TryGetValue("pages", out var p) ? p.Split(',').Select(int.Parse).ToHashSet() : null;
        for (var i = 0; i < items.Count; i++)
        {
            if (only is not null && !only.Contains(i)) continue;
            items[i].IsSelected = true;
            await Settle(i == 7 ? Math.Max(wait, 6000) : wait);
            if (items[i].ViewModel is SettingsViewModel)
            {
                if (vm.Settings.SelfUpdate.CheckCommand.ExecutionTask is { } check) await check;
                await Settle(200);
            }
            if (items[i].ViewModel is EssentialsViewModel && vm.Essentials.RefreshCommand.ExecutionTask is { } detection)
            {
                await detection;
                await Settle(100);
                File.WriteAllLines(Path.Combine(outDir, "essentials-readonly.tsv"), vm.Essentials.Results
                    .Select(card => $"{card.App.Id}\t{card.Detection.State}\t{card.Detection.Version}"));
            }
            if (items[i].ViewModel is StartupViewModel && Args.ContainsKey("startup-readonly"))
            {
                if (vm.Startup.RefreshCommand.ExecutionTask is { } scan) await scan;
                await Settle(100);
                var names = new[] { "Discord", "Steam", "Docker Desktop", "Lightshot", "IDMan", "SecurityHealth" };
                File.WriteAllLines(Path.Combine(outDir, "startup-readonly.tsv"), vm.Startup.Rows
                    .Where(row => names.Contains(row.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(row => $"{row.Name}\t{row.Entry.Source}\t{row.Entry.Status}\t{row.Entry.CanChange}\t{row.Entry.FilePath is not null}"));
            }
            if (items[i].ViewModel is CleanupViewModel && vm.Cleanup.RefreshCommand.ExecutionTask is { } cleanup)
            {
                await cleanup;
                await Settle(100);
            }
            Shot((FrameworkElement)window.Content, Path.Combine(outDir, $"{tag}-{i:00}-{Slug(items[i].Title)}.png"), scale);
        }

        if (Args.ContainsKey("dialogs"))
        {
            // Dados SIMULADOS (identificados como tal) apenas para a inspeção visual; nada é executado.
            var request = new WinCoreManager.Models.MaintenanceConfirmationRequest
            {
                Title = "CHKDSK /scan",
                Description = Args.GetValueOrDefault("language") == "en-US" ? "Sample: scan a fixed NTFS drive." : "Exemplo: verificar uma unidade NTFS fixa.",
                CommandText = "chkdsk.exe /scan",
                Effects = Args.GetValueOrDefault("language") == "en-US" ? "Sample dialog for visual validation." : "Diálogo de exemplo para validação visual.",
                TypicalDuration = Args.GetValueOrDefault("language") == "en-US" ? "5 to 20 minutes" : "5 a 20 minutos",
                RequiresElevation = true,
                DriveSelection = true,
                DriveLetters = new[] { "C:", "D:" },
            };
            var dialog = new WinCoreManager.Views.MaintenanceConfirmDialog(request) { Owner = window, Width = 620 };
            dialog.Show();
            await Settle(1200);
            Shot((FrameworkElement)dialog.Content, Path.Combine(outDir, $"{tag}-dialogo-manutencao.png"), scale);
            if (Args.ContainsKey("screen")) PrintWindow(dialog, Path.Combine(outDir, $"{tag}-dialogo-manutencao-janela.png"));
            dialog.Close();
        }

        if (Args.ContainsKey("screen"))
        {
            items[0].IsSelected = true;
            await Settle(1500);
            PrintWindow(window, Path.Combine(outDir, $"{tag}-janela-real.png"));
        }

        if (Args.ContainsKey("sensors"))
        {
            items[7].IsSelected = true;
            await Settle(6000);
            var hw = vm.Hardware;
            var panel = hw.GetType().GetMethod("CreateSensorPanel")!.Invoke(hw, null)!;
            var type = typeof(MainWindow).Assembly.GetType("WinCoreManager.Views.SensorWindow")!;
            var sw = (Window)Activator.CreateInstance(type, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public, null, new[] { panel }, null)!;
            sw.Owner = window; sw.Width = 760; sw.Height = 620; sw.Show();
            await Settle(5000);
            Shot((FrameworkElement)sw.Content, Path.Combine(outDir, $"{tag}-sensores.png"), scale);
            sw.Close();
        }
    }

    // Aplica o tema via ThemeService quando existir (reflexão: o baseline roda antes do serviço existir).
    private static void ApplyTheme(string theme)
    {
        var type = typeof(MainWindow).Assembly.GetType("WinCoreManager.Services.ThemeService");
        if (type is null) return;
        var svc = App.Services.GetService(type) ?? throw new InvalidOperationException("ThemeService não registrado");
        var enumType = typeof(MainWindow).Assembly.GetType("WinCoreManager.Models.AppTheme")!;
        type.GetMethod("Apply")!.Invoke(svc, new[] { Enum.Parse(enumType, theme, true) });
    }

    private static async Task Settle(int ms)
    {
        await Task.Delay(ms);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private sealed class FixtureActivation : IWindowsActivationService
    {
        public Task<WindowsActivationSnapshot> ReadAsync(CancellationToken token = default) =>
            Task.FromResult(new WindowsActivationSnapshot(new("Windows 11 Pro", "Professional", "25H2", "26200.1234"),
                Enum.Parse<WindowsActivationState>(Args["activation-fixture"], ignoreCase: true)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private struct RECT { public int Left, Top, Right, Bottom; }

    // Captura a janela real, com a barra de título nativa (DWM), via PrintWindow.
    private static void PrintWindow(Window window, string path)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        GetWindowRect(hwnd, out var r);
        using var bmp = new System.Drawing.Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(hwnd, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private static string Slug(string s) => new string(s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');

    private static void Shot(FrameworkElement el, string path, double scale)
    {
        el.UpdateLayout();
        var w = Math.Max(1, (int)Math.Ceiling(el.ActualWidth * scale));
        var h = Math.Max(1, (int)Math.Ceiling(el.ActualHeight * scale));
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)Application.Current.FindResource("WindowBgBrush"), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            var area = new Rect(0, 0, el.ActualWidth, el.ActualHeight);
            dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, ViewportUnits = BrushMappingMode.Absolute, Viewport = area }, null, area);
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private sealed class FixtureWinGet : IWinGetService
    {
        public string ExecutablePath => @"C:\WindowsApps\winget.exe";
        public WinGetAvailability LastDetection => new() { IsInstalled = true, Version = "1.12.0", ExecutablePath = ExecutablePath };
        public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(LastDetection);
        public Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WinGetScanResult());
        public Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FixtureStartup : IStartupInventory
    {
        public Task<StartupInventoryResult> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new StartupInventoryResult(
            [
                new("fixture-run", "Atlas Notes", "Atlas Labs", StartupSource.UserRun, StartupStatus.Enabled,
                    @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run", @"C:\Program Files\Atlas Notes\Atlas.exe", null, null),
                new("fixture-task", "Orchard Sync", "Orchard", StartupSource.ScheduledTask, StartupStatus.Disabled,
                    @"\Orchard\Sync", null, null, null),
            ], []));
    }

    private sealed class FixturePrograms : IProgramInventoryService
    {
        public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<InstalledProgram>>(
            [
                new() { Name = "Atlas Notes", Publisher = "Atlas Labs", DisplayVersion = "2.5.1", InstallDate = new DateTime(2026, 11, 23),
                    Kind = ProgramKind.Executable, UninstallString = @"C:\AuditFixtures\NonExistentAtlasUninstaller.exe" },
                new() { Name = "Orchard Sync", Publisher = "Orchard", DisplayVersion = "3.0.0", InstallDate = new DateTime(2026, 2, 18),
                    Kind = ProgramKind.Executable, UninstallString = @"C:\AuditFixtures\NonExistentOrchardUninstaller.exe" },
            ]);
    }
}
