using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager;
using WinCoreManager.Localization;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace VisualHarness;

internal static partial class Program
{
    private static int ProbeTelemetry(string path, bool cpuOnly)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        if (cpuOnly && Args.TryGetValue("cpu-image", out var image))
            return CaptureCpuPreview(path, image, elevated);
        using var cpu = new CpuTemperatureSensors(() => new LibreCpuTemperatureSession());
        var first = cpu.Read(default);
        if (cpuOnly)
        {
            Thread.Sleep(2_000);
            var second = cpu.Read(default);
            Write(new { elevated, samples = new[] { first.Groups.Select(Evidence), second.Groups.Select(Evidence) } });
            return second.Groups.Any(group => group.Readings.Any(reading => reading.Label == "CPU Package" &&
                reading.State == ReadingState.Available)) ? 0 : 1;
        }

        var wmi = new HardwareWmi();
        var probe = new WindowsSmart();
        var health = new DiskHealthService(new WindowsStorageProvider(wmi), probe).ReadAsync(true).GetAwaiter().GetResult();
        using var storage = new StorageSensors(wmi, new WindowsDiskTemperatures(), probe);
        using var performance = new WindowsPerformanceSensors(new WindowsMemoryFacts());
        using var nvidia = new NvidiaSensors(new DxgiGraphicsAdapters());
        performance.Read(default);
        var gpu = nvidia.Read(default);
        var storageBatch = storage.Read(default);
        var system = performance.Read(default);
        var diskEvidence = health.Disks.Select(item =>
        {
            var row = new DiskHealthRow(item);
            var sample = item.Sample;
            return new
            {
                item.Device.Id, item.Device.Name, row.Volumes, row.TypeLabel,
                State = sample.State.ToString(), Smart = sample.Health.ToString(), sample.Source,
                sample.CollectedAt, sample.Celsius, sample.TemperatureSource, sample.RemainingLife, sample.WearSource,
                Metrics = sample.Metrics.Where(metric => metric.Label is "PowerHours" or "Reallocated" or "Pending" or "Uncorrectable" or "Wear")
            };
        });
        Write(new
        {
            elevated, cpu = first.Groups.Select(Evidence), disks = diskEvidence,
            sensors = storageBatch.Groups.Select(Evidence), system = system.Groups.Select(Evidence),
            nvidia = gpu.Groups.Select(Evidence)
        });
        return health.State == ReadingState.Available ? 0 : 1;

        void Write(object evidence) => File.WriteAllText(path, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static int CaptureCpuPreview(string path, string image, bool elevated)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        LocalizationService.Current.Apply("pt-BR");
        foreach (var name in new[] { "Tokens.Dark.xaml", "Controls.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/WinCoreManager;component/Themes/{name}")
            });
        var model = new SensorPanelViewModel(new SensorScheduler(new CpuOnlyFactory(), TimeProvider.System, TimeSpan.FromSeconds(2)))
        { ShowDetails = true };
        var window = new Window
        {
            Title = "WinCore Manager · Sensores", Width = 760, Height = 440,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, UseLayoutRounding = true,
            Background = (Brush)app.FindResource("WindowBgBrush"), Foreground = (Brush)app.FindResource("TextPrimaryBrush"),
            FontFamily = (FontFamily)app.FindResource("FontUi"), FontSize = 14,
            Content = new SensorPanel { DataContext = model }
        };
        app.Startup += (_, _) =>
        {
            window.Show();
            model.SetActive(true);
            _ = ReadAndCapture();
        };
        return app.Run();

        async Task ReadAndCapture()
        {
            var exit = 1;
            try
            {
                for (var attempt = 0; attempt < 20 && model.Groups.Count == 0; attempt++) await Settle(200);
                var first = model.Groups.Select(Evidence).ToArray();
                var timestamp = model.Groups.FirstOrDefault()?.Readings.FirstOrDefault()?.Timestamp;
                for (var attempt = 0; attempt < 20 && model.Groups.FirstOrDefault()?.Readings.FirstOrDefault()?.Timestamp == timestamp; attempt++)
                    await Settle(200);
                var second = model.Groups.Select(Evidence).ToArray();
                File.WriteAllText(path, JsonSerializer.Serialize(new { elevated, samples = new[] { first, second } },
                    new JsonSerializerOptions { WriteIndented = true }));
                Shot((FrameworkElement)window.Content, image, 1);
                exit = model.Groups.Any(group => group.Readings.Any(reading => reading.Label == "CPU Package"
                    && reading.State == ReadingState.Available)) ? 0 : 1;
            }
            finally
            {
                await model.CloseAsync();
                window.Close();
                app.Shutdown(exit);
            }
        }
    }

    private sealed class CpuOnlyFactory : ISensorProviderFactory
    {
        public IReadOnlyList<ISensorProvider> Create() => [new CpuTemperatureSensors(() => new LibreCpuTemperatureSession())];
    }

    private static object Evidence(SensorGroup group) => new
    {
        group.Id, group.Name, group.Condition,
        Readings = group.Readings.Select(ReadingEvidence),
        CpuCoreReadings = group.CpuCoreReadings.Select(ReadingEvidence)
    };

    private static object ReadingEvidence(HardwareReading reading) => new
    {
        reading.Label, reading.Value, reading.Unit, reading.Source, reading.Timestamp, reading.Reason,
        State = reading.State.ToString()
    };

    private static async Task CaptureTelemetry(Window owner, MainViewModel model, string directory, string tag, double scale)
    {
        void Save(string name) => Shot((FrameworkElement)owner.Content, Path.Combine(directory, $"{tag}-{name}.png"), scale);
        model.OpenSensors();
        model.Sensors.ShowDetails = true;
        for (var attempt = 0; attempt < 36 && model.Sensors.Groups.All(group => !group.Id.StartsWith("disk/")); attempt++)
        {
            model.Sensors.SetActive(true);
            await Settle(400);
        }
        if (model.Sensors.Groups.All(group => !group.Id.StartsWith("disk/")))
            throw new InvalidOperationException("Sensor capture did not receive a storage sample.");
        Save("sensores");
        var sensorPanel = Descendant<SensorPanel>(owner);
        var scroll = (ScrollViewer)sensorPanel.FindName("SensorScroll");
        scroll.ScrollToEnd();
        await Settle(220);
        Save("sensores-discos");

        model.NavItems.Single(item => item.ViewModel is MaintenanceViewModel).IsSelected = true;
        model.Sensors.SetActive(false);
        var health = App.Services.GetRequiredService<DiskHealthViewModel>();
        await health.LoadAsync(false);
        await Settle(350);
        var card = Descendant<DiskHealthCard>(Descendant<MaintenancePage>(owner));
        card.BringIntoView();
        await Settle(200);
        Save("saude-dos-discos");
    }
}
