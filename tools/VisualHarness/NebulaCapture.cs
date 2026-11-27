using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace VisualHarness;

// --nebula true: todas as páginas com dados SIMULADOS e determinísticos. Nada é executado no Windows.
// --nebula-empty true: mesmas páginas com histórico, atualizações, programas e leituras vazios/indisponíveis.
internal static partial class Program
{
    private static bool NebulaEmpty => Args.ContainsKey("nebula-empty");

    // --public true: capturas do README, sem o marcador visível (o texto ao redor informa que os dados são sintéticos).
    private static string Sim(string name) => Args.ContainsKey("public") ? name : name + " (simulado)";

    private static void ConfigureNebula(IServiceCollection services)
    {
        var cleanupRoot = Path.Combine(Path.GetFullPath(Args["out"]), "cleanup-fixture");
        services.AddSingleton(_ => new DiskHealthService(_ => NebulaEmpty
            ? new DiskHealthReport(ReadingState.NoReading, []) : NebulaDisks(), TimeProvider.System));
        services.AddSingleton<ISensorScheduler, NebulaSensors>();
        services.AddSingleton<IHardwareInventory, NebulaInventory>();
        services.AddSingleton<IHistoryService, NebulaHistory>();
        services.AddSingleton<ICleanupPlatform, NebulaCleanupPlatform>();
        services.AddSingleton(sp => new CleanupService(sp.GetRequiredService<ICleanupPlatform>(),
            sp.GetRequiredService<IWindowsTools>(), sp.GetRequiredService<IHistoryService>())
        {
            DirectFiles = new CleanupFiles(NebulaCleanupRoots(cleanupRoot)),
        });
        services.AddSingleton<IWindowsActivationService, NebulaActivation>();
    }

    private static CleanupRoot[] NebulaCleanupRoots(string root)
    {
        (string Category, int Files, int KiB)[] plan =
        [
            (CleanupFiles.UserTemp, 12, 340), (CleanupFiles.UserDumps, 3, 2048), ("WinCore.Chrome", 40, 220),
            ("WinCore.Edge", 18, 96), ("WinCore.Firefox", 9, 64), ("WinCore.Discord", 22, 128), (CleanupFiles.UserReports, 0, 0),
        ];
        return plan.Select(item =>
        {
            var folder = Path.Combine(root, item.Category);
            Directory.CreateDirectory(folder);
            for (var i = 0; i < item.Files; i++)
            {
                var file = Path.Combine(folder, $"fixture-{i:000}.log");
                if (!File.Exists(file)) File.WriteAllBytes(file, new byte[item.KiB * 1024]);
            }
            return new CleanupRoot(item.Category, folder, "", TimeSpan.Zero, false);
        }).ToArray();
    }

    private static DiskHealthReport NebulaDisks()
    {
        var time = DateTimeOffset.Now;
        PhysicalDiskHealth Disk(int number, string name, string type, ulong capacity, string volume, DiskHealthSample sample) =>
            new(new InventoryDevice($"disk/{number}", HardwareKind.Disk, name, time)
                    .Add(LocalizationService.Source("Message.Type"), type, "Fixture")
                    .Add(LocalizationService.Source("Message.Capacity"), capacity, "Fixture", "B")
                    .Build([new($"disk/{number}/part/1", HardwareKind.Disk, "Partição 1", [],
                        [new($"volume/{volume}", HardwareKind.Disk, "Volume " + volume, [])]) { LocalizeName = true }]), sample);

        DiskHealthSample Sample(int celsius, int? life) => new(ReadingState.Available, DiskHealthState.Healthy, "Fixture", [])
        {
            Celsius = celsius, TemperatureSource = "Fixture", RemainingLife = life, WearSource = "Fixture",
        };

        return new(ReadingState.Available,
        [
            Disk(0, Sim("Example HDD 8 TB"), "HDD", 8_001_563_222_016UL, "D:", Sample(39, null)),
            Disk(1, Sim("Example SATA SSD 2 TB"), "SSD SATA", 2_000_398_934_016UL, "E:", Sample(37, 99)),
            Disk(2, Sim("Example SATA SSD 2 TB"), "SSD SATA", 2_000_398_934_016UL, "F:", Sample(33, 98)),
            Disk(3, Sim("Example NVMe 1 TB"), "NVMe", 1_000_204_886_016UL, "C:", Sample(52, 79)),
        ]);
    }

    private sealed class NebulaSensors : ISensorScheduler
    {
        public async IAsyncEnumerable<SensorSnapshot> ObserveAsync([EnumeratorCancellation] CancellationToken token)
        {
            var time = DateTimeOffset.Now;
            HardwareReading R(string id, string label, object? value, string unit, ReadingState absent = ReadingState.NoReading) =>
                HardwareReading.Create(id, LocalizationService.Source(label), value, Sim("Fixture"), time, unit, absent);
            SensorGroup[] groups =
            [
                new("cpu/system", LocalizationService.Source("Message.ProcessorSystemUtilization"),
                    [R("cpu/system", "Message.TotalUtilization", 14d, "%"), R("cpu/system", "Message.AverageClockReportedByWindows", 3610d, "MHz")]),
                new("cpu/temperature/0", Sim("Example CPU 12-core"),
                    [HardwareReading.Create("cpu/temperature/0", "CPU Package", 46d, Sim("Fixture"), time, "°C")])
                {
                    CpuCoreReadings = Enumerable.Range(1, 12).Select(i =>
                        HardwareReading.Create("cpu/temperature/0", $"Core #{i}", 38d + i % 7, Sim("Fixture"), time, "°C")).ToArray(),
                },
                new("ram", LocalizationService.Source("Message.MemoryRAM"),
                    [R("ram", "Message.Utilization", 42d, "%"), R("ram", "Message.Available", 19_900_000_000UL, "B")]),
                new("gpu/0", Sim("Example GPU 12 GB"),
                    [R("gpu/0", "Message.GPUTemperature", 49d, "°C"), R("gpu/0", "Message.GPUUtilization", 36d, "%"),
                     R("gpu/0", "Message.GPUCoreClock", 1665d, "MHz"), R("gpu/0", "Message.ReportedPower", 112.4d, "W")]),
                new("disk/3", Sim("Example NVMe 1 TB"),
                    [R("disk/3", "Message.Temperature", 52d, "°C"), R("disk/3", "DiskHealth.LifeLabel", 79d, "%")])
                    { Condition = "Healthy", ConditionLabel = LocalizationService.Source("DiskHealth.State.Healthy") },
                new("disk/0", Sim("Example HDD 8 TB"), [R("disk/0", "Message.Temperature", 39d, "°C")])
                    { Condition = "Healthy", ConditionLabel = LocalizationService.Source("DiskHealth.State.Healthy") },
            ];
            SensorNotice[] notices =
            [
                new(LocalizationService.Source("Message.MemoryModules"), ReadingState.Unsupported,
                    LocalizationService.Source("Message.WindowsReportsMemoryUsageAndAvailabilityModuleTemperature")),
            ];
            while (!token.IsCancellationRequested)
            {
                yield return new(DateTimeOffset.Now, NebulaEmpty ? [] : groups, notices);
                await Task.Delay(2000, token);
            }
        }
    }

    private sealed class NebulaInventory : IHardwareInventory
    {
        public Task<IReadOnlyList<HardwareSection>> ReadAsync(CancellationToken token)
        {
            var time = DateTimeOffset.Now;
            HardwareSection Section(string title, HardwareKind kind, string name, params (string Label, object Value, string Unit)[] fields)
            {
                var device = new InventoryDevice("fixture/" + kind, kind, name, time);
                foreach (var field in fields) device.Add(LocalizationService.Source(field.Label), field.Value, Sim("Fixture"), field.Unit);
                return new(LocalizationService.Source(title), name, [device.Build()]);
            }
            return Task.FromResult<IReadOnlyList<HardwareSection>>(
            [
                Section("Message.OperatingSystem", HardwareKind.Windows, Sim("Windows 11 Pro 25H2"), ("Message.Capacity", "x64", "")),
                Section("Message.Processor", HardwareKind.Cpu, Sim("Example CPU 12-core"), ("Message.Type", "12 núcleos · 20 threads", "")),
                Section("Message.GraphicsCard", HardwareKind.Gpu, Sim("Example GPU 12 GB"), ("Message.Capacity", 12_884_901_888UL, "B")),
                Section("Message.MemoryRAM", HardwareKind.Ram, Sim("32 GiB DDR4"), ("Message.Capacity", 34_359_738_368UL, "B")),
                Section("Message.Storage", HardwareKind.Disk, Sim(Args.ContainsKey("public") ? "4 drives" : "4 discos"), ("Message.Capacity", 13_000_000_000_000UL, "B")),
            ]);
        }
    }

    private sealed class NebulaHistory : IHistoryService
    {
        private static IReadOnlyList<HistoryRecord> Records => NebulaEmpty ? [] :
        [
            Record(1, HistoryType.Update, Sim("7-Zip"), HistoryStatus.Success, 5),
            Record(2, HistoryType.Maintenance, Sim("SFC /scannow"), HistoryStatus.Success, 42),
            Record(3, HistoryType.Cleanup, Sim("Cache do navegador"), HistoryStatus.Success, 130),
            Record(4, HistoryType.Uninstall, Sim("Atlas Notes"), HistoryStatus.Cancelled, 1500),
            Record(5, HistoryType.Startup, Sim("Orchard Sync"), HistoryStatus.Success, 2900),
            Record(6, HistoryType.Update, Sim("Orchard Sync"), HistoryStatus.Failure, 4400),
        ];

        private static HistoryRecord Record(long id, HistoryType type, string target, HistoryStatus status, int minutesAgo) => new()
        {
            Id = id, Type = type, Target = target, Status = status, Timestamp = DateTimeOffset.Now.AddMinutes(-minutesAgo),
            Details = "Registro simulado para captura visual.", DurationMs = 38_000,
        };

        public Task AddAsync(HistoryRecord record) => Task.CompletedTask;
        public Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryFilter filter) => Task.FromResult(Records);
        public Task<IReadOnlyList<HistoryRecord>> RecentAsync(int count) =>
            Task.FromResult<IReadOnlyList<HistoryRecord>>(Records.Take(count).ToArray());
        public Task<int> ClearAsync() => Task.FromResult(0);
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => Task.FromResult(0);
    }

    private sealed class NebulaCleanupPlatform : ICleanupPlatform
    {
        public WindowsVolume ReadWindowsVolume() => new(@"C:\", "fixture", 999_200_000_000, 172_900_000_000);
        public IReadOnlyList<CleanupCategory> ReadCatalog() => [];
        public RecycleEstimate? QueryRecycleBin(string root) => new(48_200_000, 12);
        public WindowsActionResult EmptyRecycleBin(string root, nint owner) => throw new NotSupportedException("captura visual não executa limpeza");
    }

    private sealed class NebulaActivation : IWindowsActivationService
    {
        public Task<WindowsActivationSnapshot> ReadAsync(CancellationToken token = default) =>
            Task.FromResult(new WindowsActivationSnapshot(new("Windows 11 Pro", "Professional", "25H2", "26200.1000"),
                WindowsActivationState.Activated));
    }

    private static IReadOnlyList<WinGetPackage> NebulaPackages() => NebulaEmpty ? [] :
    [
        new() { Name = Sim("7-Zip"), Id = "7zip.7zip", InstalledVersion = "24.09", AvailableVersion = "25.01", Source = "winget" },
        new() { Name = Sim("Mozilla Firefox"), Id = "Mozilla.Firefox", InstalledVersion = "144.0", AvailableVersion = "145.0.1", Source = "winget" },
        new() { Name = Sim("Visual Studio Code"), Id = "Microsoft.VisualStudioCode", InstalledVersion = "1.105.0", AvailableVersion = "1.106.2", Source = "winget" },
        new() { Name = Sim("Git"), Id = "Git.Git", InstalledVersion = "2.51.0", AvailableVersion = "2.52.0", Source = "winget" },
        new() { Name = Sim(Args.ContainsKey("public") ? "VLC media player" : "VLC media player com um nome propositalmente bem longo"), Id = "VideoLAN.VLC.LongIdentifierForLayout", InstalledVersion = "3.0.21", AvailableVersion = "3.0.22", Source = "winget" },
        new() { Name = Sim("Discord"), Id = "Discord.Discord", InstalledVersion = "1.0.9210", AvailableVersion = "1.0.9215", Source = "winget" },
    ];

    private static async Task CaptureNebula(Window owner, MainViewModel model, string output, string tag, double scale)
    {
        void Save(FrameworkElement element, string name) => Shot(element, Path.Combine(output, $"{tag}-{name}.png"), scale);
        var items = model.NavItems.Append(model.AboutNavItem).ToList();
        var only = Args.TryGetValue("pages", out var list) ? list.Split(',').Select(int.Parse).ToHashSet() : null;
        model.Dashboard.SystemVolume = () => (999_200_000_000, 172_900_000_000, "C:");
        if (Args.ContainsKey("public") && model.Updates.LastScanTime is null) await model.Updates.RefreshCommand.ExecuteAsync(null);

        for (var i = 0; i < items.Count; i++)
        {
            if (only is not null && !only.Contains(i)) continue;
            var page = items[i].ViewModel;
            items[i].IsSelected = true;
            await Settle(300);
            if (page is UpdatesViewModel updates)
            {
                await updates.RefreshCommand.ExecuteAsync(null);
                updates.AllSelected = true;
            }
            if (page is CleanupViewModel cleanup)
            {
                if (cleanup.RefreshCommand.ExecutionTask is { } scan) await scan;
                foreach (var row in cleanup.DirectCategories.Where(row => row.Selectable)) row.IsSelected = true;
            }
            if (page is HardwareViewModel hardware && hardware.RefreshCommand.ExecutionTask is { } read) await read;
            if (page is SensorPanelViewModel || page is DashboardViewModel) model.Sensors.SetActive(true);
            await Settle(page is EssentialsViewModel or UninstallersViewModel or TweaksViewModel ? 3000 : 1200);
            Save((FrameworkElement)owner.Content, $"{i:00}-{Slug(page.GetType().Name.Replace("ViewModel", ""))}");
        }

        if (!Args.ContainsKey("dialogs")) return;

        async Task Dialog(Window window, string name)
        {
            window.Owner = owner;
            window.Show();
            await Settle(700);
            var content = (FrameworkElement)window.Content;
            Save(System.Windows.Media.VisualTreeHelper.GetParent(content) as FrameworkElement ?? content, name);
            window.Close();
        }

        await Dialog(new MaintenanceConfirmDialog(new MaintenanceConfirmationRequest
        {
            Title = "CHKDSK /scan", Description = "Exemplo (simulado): verificar uma unidade NTFS fixa.", CommandText = "chkdsk.exe /scan",
            Effects = "Diálogo de exemplo para validação visual.", TypicalDuration = "5 a 20 minutos", RequiresElevation = true,
            DriveSelection = true, DriveLetters = ["C:", "D:"],
        }), "dialogo-manutencao");
        await Dialog(new UninstallConfirmDialog(new UninstallConfirmationRequest
        {
            Title = Sim("Atlas Notes"), Description = "Revisar desinstalação", CommandText = @"C:\Fixture\uninstall.exe",
            Effects = "Remove o aplicativo", Warnings = ["Aviso de exemplo para validação visual."], ItemCount = 1,
        }), "dialogo-desinstalar");
        await Dialog(new DiskAnalysisDialog(), "dialogo-analise-disco");
        var health = App.Services.GetRequiredService<DiskHealthViewModel>();
        await health.LoadAsync(false);
        if (health.Disks.Count > 0) await Dialog(new DiskHealthDetailsWindow(health.Disks[^1]) { Width = 740, Height = 540 }, "janela-detalhes-disco");
        await Dialog(new SensorWindow(model.Sensors) { Width = 760, Height = 620 }, "janela-sensores");
    }
}
