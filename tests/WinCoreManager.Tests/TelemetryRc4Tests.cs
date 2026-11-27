using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinCoreManager.Localization;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;
using static WinCoreManager.Tests.HardwareInventoryTests;

namespace WinCoreManager.Tests;

public sealed class TelemetryRc4Tests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 99)]
    [InlineData(2, 98)]
    [InlineData(100, 0)]
    [InlineData(255, 0)]
    public void Ata_endurance_requires_supported_and_valid_percentage_used(byte used, int remaining)
    {
        var page = new byte[512];
        page[0] = 1;
        page[2] = 7;
        page[8] = used;
        page[15] = 0xC0;
        Assert.Equal(used, WindowsSmart.ParseAtaEndurance(page));
        Assert.Equal(remaining, WindowsSmart.RemainingEndurance(WindowsSmart.ParseAtaEndurance(page)));
        page[15] = 0x80;
        Assert.Null(WindowsSmart.ParseAtaEndurance(page));
        page[15] = 0x40;
        Assert.Null(WindowsSmart.ParseAtaEndurance(page));
        page[15] = 0xC0;
        page[2] = 5;
        Assert.Null(WindowsSmart.ParseAtaEndurance(page));
        Assert.Null(WindowsSmart.ParseAtaEndurance(page.AsSpan(0, 511)));
        Assert.Null(WindowsSmart.RemainingEndurance(null));
    }

    [Theory]
    [InlineData(37, 37)]
    [InlineData(246, -10)]
    [InlineData(128, null)]
    [InlineData(126, null)]
    [InlineData(170, null)]
    public void Sct_uses_signed_celsius_and_rejects_invalid_or_unrelated_data(byte raw, int? temperature)
    {
        var page = new byte[512];
        page[0] = 3;
        page[200] = raw;
        Assert.Equal(temperature, WindowsSmart.ParseSctTemperature(page));
        page[0] = 1;
        Assert.Null(WindowsSmart.ParseSctTemperature(page));
        var response = new byte[560];
        uint[] descriptor = [48, 560, 2, 2, 4, 7, 40, 512, 0, 0, 0, 0];
        for (var index = 0; index < descriptor.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(index * 4), descriptor[index]);
        Assert.Equal(512, WindowsSmart.ExtractLog(response, 2, 4, 7).Length);
        Assert.True(WindowsSmart.ExtractLog(response, 2, 4, 5).IsEmpty);
        Assert.True(WindowsSmart.ExtractLog(response, 2, 0xE0, 0).IsEmpty);
    }

    [Fact]
    public void Vendor_attributes_alone_do_not_set_ssd_endurance()
    {
        var sector = new byte[512];
        sector[0] = 1;
        var index = 2;
        foreach (byte id in new byte[] { 177, 194, 202, 231, 233 })
        {
            sector[index] = id;
            sector[index + 3] = 100;
            sector[index + 5] = 50;
            index += 12;
        }
        sector[^1] = unchecked((byte)-sector.Sum(value => value));
        var sample = WindowsSmart.ParseAta(sector, false, false, null);
        Assert.Equal(DiskHealthState.Healthy, sample.Health);
        Assert.Null(sample.RemainingLife);
        Assert.Null(sample.Celsius);
        Assert.Equal(6, sample.Metrics.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_size_and_model_keep_physical_samples_separate_and_reject_duplicate_serials(bool duplicate)
    {
        const string first = "PRIVATE-A";
        var second = duplicate ? first.ToLowerInvariant() : "PRIVATE-B";
        var wmi = new TestHardwareWmi();
        wmi.Add("MSFT_Disk",
            R(("Number", 1), ("Model", "Netac SSD 2TB"), ("Size", 2_000_000_000_000UL), ("BusType", 11), ("SerialNumber", first)),
            R(("Number", 2), ("Model", "Netac SSD 2TB"), ("Size", 2_000_000_000_000UL), ("BusType", 11), ("SerialNumber", second)));
        wmi.Add("MSFT_Partition", R(("DiskNumber", 1), ("DriveLetter", "E")), R(("DiskNumber", 2), ("DriveLetter", "F")));
        var probe = new FixtureSmart();
        using var provider = new StorageSensors(wmi, new MissingNativeTemperature(), probe);
        var batch = provider.Read(default);
        Assert.Equal(2, batch.Groups.Count);
        Assert.EndsWith("(E:)", batch.Groups[0].Name);
        Assert.EndsWith("(F:)", batch.Groups[1].Name);
        Assert.DoesNotContain("PRIVATE-", System.Text.Json.JsonSerializer.Serialize(batch));
        if (duplicate)
        {
            Assert.All(probe.Calls, call => Assert.Null(call.Serial));
            Assert.All(batch.Groups, group => Assert.DoesNotContain(group.Readings, field => field.State == ReadingState.Available));
            return;
        }
        Assert.Equal(new (uint, string?)[] { (1, first), (2, second) }, probe.Calls);
        Assert.Contains(batch.Groups[0].Readings, field => field.Unit == "°C" && Equals(field.Value, 37d) && field.Source == WindowsSmart.SctSource);
        Assert.Contains(batch.Groups[1].Readings, field => field.Unit == "°C" && Equals(field.Value, 33d) && field.Source == WindowsSmart.SctSource);
        Assert.Contains(batch.Groups[0].Readings, field => field.Unit == "%" && Equals(field.Value, 99d));
        Assert.Contains(batch.Groups[1].Readings, field => field.Unit == "%" && Equals(field.Value, 98d));
    }

    [Fact]
    public void Cpu_session_initializes_once_reads_package_and_valid_cores_and_closes()
    {
        var cpu = new FixtureCpu();
        var opens = 0;
        using (var provider = new CpuTemperatureSensors(() => { opens++; return cpu; }))
        {
            var group = Assert.Single(provider.Read(default).Groups);
            var first = Assert.Single(group.Readings);
            Assert.Equal("CPU Package", first.Label);
            Assert.Equal(43d, first.Value);
            Assert.Equal(CpuTemperatureSensors.Source, first.Source);
            Assert.Equal(42d, Assert.Single(group.CpuCoreReadings).Value);
            Assert.Equal(44d, Assert.Single(provider.Read(default).Groups).Readings[0].Value);
            Assert.Equal(1, opens);
        }
        Assert.Equal(1, cpu.Closes);
    }

    [Theory]
    [InlineData("pt-BR", "Ver temperaturas por núcleo (1)", "Ocultar temperaturas por núcleo")]
    [InlineData("en-US", "Show core temperatures (1)", "Hide core temperatures")]
    public Task Core_temperature_rows_are_hidden_until_the_user_expands_them(
        string language, string show, string hide) => WpfTestThread.Run(() =>
    {
        var original = LocalizationService.Current.Culture.Name;
        LocalizationService.Current.Apply(language);
        try
        {
            using var provider = new CpuTemperatureSensors(() => new FixtureCpu());
            var model = new SensorPanelViewModel(new HardwareTestScheduler()) { Groups = provider.Read(default).Groups };
            var panel = new SensorPanel { DataContext = model };
            var viewport = new Size(640, 450);
            panel.Measure(viewport);
            panel.Arrange(new Rect(viewport));
            panel.UpdateLayout();
            var toggle = FindNamed<Button>(panel, "CpuCoresToggle");
            var cores = FindNamed<ItemsControl>(panel, "CpuCoreReadings");
            Assert.Equal(show, toggle.Content);
            Assert.Equal(Visibility.Collapsed, cores.Visibility);
            toggle.Command.Execute(null);
            Assert.Equal(hide, toggle.Content);
            Assert.Equal(Visibility.Visible, cores.Visibility);
            Assert.Single(cores.Items);
            toggle.Command.Execute(null);
            Assert.Equal(Visibility.Collapsed, cores.Visibility);
        }
        finally { LocalizationService.Current.Apply(original); }
        return Task.CompletedTask;
    });

    private static T FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var pending = new Queue<DependencyObject>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var current))
        {
            if (current is T match && match.Name == name) return match;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                pending.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
        throw new InvalidOperationException(name);
    }

    [Theory]
    [InlineData("P-Core #1", true)]
    [InlineData("E-Core #4", true)]
    [InlineData("Core #2", true)]
    [InlineData("P-Core #1 Distance to TjMax", false)]
    [InlineData("Core Max", false)]
    [InlineData("CPU Package", false)]
    [InlineData("ACPI Thermal Zone", false)]
    public void Core_temperatures_exclude_distances_and_aggregates(string name, bool expected) =>
        Assert.Equal(expected, LibreCpuTemperatureSession.IsCoreTemperatureName(name));

    [Theory]
    [InlineData(ReadingState.Restricted, "Sensors.CpuPawnIoDenied")]
    [InlineData(ReadingState.Unsupported, "Sensors.CpuPawnIoMissing")]
    [InlineData(ReadingState.Error, "Sensors.CpuLibraryUnavailable")]
    public void Cpu_failure_has_a_reason_and_is_not_repeated_each_poll(ReadingState state, string reason)
    {
        var opens = 0;
        using var provider = new CpuTemperatureSensors(() =>
        {
            opens++;
            throw state == ReadingState.Error ? new FileNotFoundException() : new CpuTemperatureAccessException(state, reason);
        });
        var reading = Assert.Single(Assert.Single(provider.Read(default).Groups).Readings);
        Assert.Equal(state, reading.State);
        Assert.Equal(LocalizationService.Source(reason), reading.Reason);
        Assert.Null(reading.Value);
        provider.Read(default);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void Cpu_group_and_access_reason_translate_on_the_sensors_panel()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("CPU temperature · Intel CPU", PresentationText.Translate(
            LocalizationService.Source("Sensors.CpuTemperature") + " · Intel CPU", english));
        Assert.Equal("Windows denied this session access to the PawnIO driver.",
            PresentationText.Translate(LocalizationService.Source("Sensors.CpuPawnIoDenied"), english));
    }

    private sealed class FixtureCpu : ICpuTemperatureSession
    {
        private int _reads;
        public int Closes { get; private set; }

        public IReadOnlyList<CpuPackageSample> Read(CancellationToken token) =>
            [new("/intelcpu/0", "Intel CPU", 42 + ++_reads, [("P-Core #1", 42), ("P-Core #2", float.NaN), ("P-Core #3", 400)])];

        public void Dispose() => Closes++;
    }

    private sealed class MissingNativeTemperature : IDiskTemperatures
    {
        public DiskTemperatureResult Read(uint number, string? privateSerial) => new(ReadingState.Unsupported, []);
    }

    private sealed class FixtureSmart : IDiskHealthProbe
    {
        public List<(uint Number, string? Serial)> Calls { get; } = [];

        public DiskHealthSample Read(uint number, string? privateSerial, uint? bus, bool mechanical)
        {
            Calls.Add((number, privateSerial));
            if (privateSerial is null) return new(ReadingState.NoReading, DiskHealthState.Unknown, "", []);
            return new(ReadingState.Available, DiskHealthState.Healthy, WindowsSmart.AtaSource, [],
                number == 1 && privateSerial == "PRIVATE-A" ? 37 : 33, 100 - (int)number)
            {
                TemperatureSource = WindowsSmart.SctSource,
                WearSource = WindowsSmart.AtaEnduranceSource
            };
        }
    }
}
