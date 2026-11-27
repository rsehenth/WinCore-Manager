using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;
using static WinCoreManager.Tests.HardwareInventoryTests;

namespace WinCoreManager.Tests;

public sealed class CandidateSensorTests
{
    [Fact]
    public void Nvme_smart_supplies_temperature_and_physical_fallback_requires_an_unambiguous_match()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("MSFT_Disk",
            R(("Number", 7), ("Size", 1000UL), ("BusType", 17), ("SerialNumber", "INTERNAL-7"), ("HealthStatus", 0)) with { Path = "logical-7" },
            R(("Number", 8), ("Size", 1000UL), ("BusType", 11), ("SerialNumber", "INTERNAL-8"), ("HealthStatus", 0)) with { Path = "logical-8" });
        wmi.Add("MSFT_PhysicalDisk",
            R(("Size", 1000UL), ("SerialNumber", "INTERNAL-7")) with { Path = "physical-7" },
            R(("Size", 1000UL), ("SerialNumber", "INTERNAL-8")) with { Path = "physical-one" },
            R(("Size", 1000UL), ("SerialNumber", "INTERNAL-8")) with { Path = "physical-two" });
        wmi.Links["physical-7"] = new(ReadingState.Available, [R(("PowerOnHours", 43UL))]);
        wmi.Links["physical-one"] = new(ReadingState.Available, [R(("Temperature", 88))]);
        wmi.Add("Win32_PerfFormattedData_PerfDisk_PhysicalDisk",
            R(("Name", "7 C:"), ("PercentIdleTime", 25UL), ("DiskReadBytesPersec", 4096UL)));
        var probe = new NvmeProbe();
        using var provider = new StorageSensors(wmi, new MissingTemperature(), probe);
        var batch = provider.Read(default);

        var nvme = Assert.Single(batch.Groups, group => group.Id == "disk/7");
        Assert.Equal(7U, probe.Number);
        Assert.Equal("Attention", nvme.Condition);
        Assert.Contains(nvme.Readings, field => field.Unit == "°C" && Equals(field.Value, 46d) && field.Source == WindowsSmart.NvmeSource);
        Assert.Contains(nvme.Readings, field => field.Unit == "h" && Equals(field.Value, 43d));
        Assert.Contains(nvme.Readings, field => field.Unit == "%" && Equals(field.Value, 75d));
        Assert.DoesNotContain(Assert.Single(batch.Groups, group => group.Id == "disk/8").Readings, field => field.Unit == "°C");
        Assert.Contains(batch.Notices, notice => notice.Category.EndsWith("8", StringComparison.Ordinal));
        Assert.DoesNotContain("INTERNAL-", System.Text.Json.JsonSerializer.Serialize(batch), StringComparison.Ordinal);
    }

    [Fact]
    public void Gpu_counter_groups_processes_for_the_same_engine_and_uses_the_busiest_linked_engine()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine",
            R(("Name", "pid_70_luid_0x00000000_0x0000007b_phys_0_eng_0_engtype_3D"), ("UtilizationPercentage", 31UL)),
            R(("Name", "pid_80_luid_0x00000000_0x0000007b_phys_0_eng_0_engtype_3D"), ("UtilizationPercentage", 23UL)),
            R(("Name", "pid_90_luid_0x00000000_0x0000007b_phys_0_eng_1_engtype_Copy"), ("UtilizationPercentage", 40UL)),
            R(("Name", "pid_90_luid_0x00000000_0x0000007c_phys_0_eng_1_engtype_Copy"), ("UtilizationPercentage", 96UL)));
        var amd = new DxgiAdapter("Fixture GPU", new PciModel(0x1002, 2, 0), 0, 123, 0);
        var missing = amd with { Name = "Missing GPU", Luid = 125 };
        using var provider = new WindowsGpuEngineSensors(new TestGraphics([amd, missing]), wmi);

        var sample = provider.Read(default);
        Assert.Equal(54d, Assert.Single(Assert.Single(sample.Groups).Readings).Value);
        Assert.Equal(ReadingState.NoReading, Assert.Single(sample.Notices).State);
    }

    [Theory]
    [InlineData("pt-BR", AppTheme.Light, 1.25)]
    [InlineData("pt-BR", AppTheme.Light, 1.5)]
    [InlineData("pt-BR", AppTheme.Light, 2)]
    [InlineData("pt-BR", AppTheme.Dark, 1.25)]
    [InlineData("pt-BR", AppTheme.Dark, 1.5)]
    [InlineData("pt-BR", AppTheme.Dark, 2)]
    [InlineData("en-US", AppTheme.Light, 1.25)]
    [InlineData("en-US", AppTheme.Light, 1.5)]
    [InlineData("en-US", AppTheme.Light, 2)]
    [InlineData("en-US", AppTheme.Dark, 1.25)]
    [InlineData("en-US", AppTheme.Dark, 1.5)]
    [InlineData("en-US", AppTheme.Dark, 2)]
    public Task Sensor_cards_and_unavailable_notices_render_with_localized_app_center(string language, AppTheme theme, double scale) =>
        WpfTestThread.Run(() =>
        {
            var original = ThemeResources.Current;
            ThemeResources.Install(Application.Current.Resources, theme);
            LocalizationService.Current.Apply(language);
            try
            {
                var vm = new SensorPanelViewModel(new HardwareTestScheduler())
                {
                    Groups = [new("disk", "NVMe", [HardwareReading.Create("disk", "Temperature", 39d, "SMART", DateTimeOffset.Now, "°C")])],
                    Notices = [new("GPU", ReadingState.Unsupported, LocalizationService.Source("Sensors.GpuUsageMissing"))]
                };
                var panel = new SensorPanel { DataContext = vm };
                VisualTreeHelper.SetRootDpi(panel, new DpiScale(scale, scale));
                var viewport = new Size(540, 430);
                panel.Measure(viewport);
                panel.Arrange(new Rect(viewport));
                panel.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)(viewport.Width * scale), (int)(viewport.Height * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(panel);
                Assert.Single(Assert.IsType<ItemsControl>(panel.FindName("SensorGroups")).Items);
                Assert.Equal(1, vm.AvailableComponentCount);
                var details = Assert.IsType<Button>(panel.FindName("SensorDetailsButton"));
                Assert.True(details.Command.CanExecute(null));
                details.Command.Execute(null);
                Assert.True(vm.ShowDetails);
                Assert.Equal(language == "en-US" ? "Hide sources" : "Ocultar fontes", details.Content);
                Assert.Single(Assert.IsType<ItemsControl>(panel.FindName("SensorNotices")).Items);
                Assert.Equal(0, Assert.IsType<ScrollViewer>(panel.FindName("SensorScroll")).ScrollableWidth);
                var text = LocalizationService.Current;
                Assert.Equal(language == "en-US" ? "App Center" : "Central de Apps", text["Message.Essentials"]);
                Assert.Equal(language == "en-US" ? "Essential Apps Center" : "Central de Aplicativos Essenciais", text["Essentials.Title"]);
            }
            finally { ThemeResources.Install(Application.Current.Resources, original); }
            return Task.CompletedTask;
        });

    private sealed class MissingTemperature : IDiskTemperatures
    {
        public DiskTemperatureResult Read(uint number, string? privateSerial) => new(ReadingState.Unsupported, []);
    }

    private sealed class NvmeProbe : IDiskHealthProbe
    {
        public uint? Number { get; private set; }

        public DiskHealthSample Read(uint number, string? privateSerial, uint? bus, bool mechanical)
        {
            if (bus != 17) return new(ReadingState.Unsupported, DiskHealthState.Unknown, "", []);
            Number = number;
            return new(ReadingState.Available, DiskHealthState.Attention, WindowsSmart.NvmeSource, [], 46);
        }
    }
}
