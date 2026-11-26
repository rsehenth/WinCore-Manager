using WinCoreManager.Localization;
using System.Diagnostics;

namespace WinCoreManager.Services.Hardware;

internal sealed class StorageSensors(IHardwareWmi wmi, IDiskTemperatures temperatures) : ISensorProvider
{
    private const string CounterSource = "Windows Storage · MSFT_StorageReliabilityCounter";
    private const string CounterColumns = "Temperature,PowerOnHours,Wear,ReadErrorsUncorrected,WriteErrorsUncorrected";
    private SensorBatch? _lastBatch;
    private long _lastRead;
    public string Category => LocalizationService.Source("Message.Storage");
    public TimeSpan Interval => TimeSpan.FromSeconds(45);

    public SensorBatch Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_lastBatch is not null && Stopwatch.GetElapsedTime(_lastRead) < Interval) return _lastBatch;
        _lastBatch = ReadDisks(token);
        _lastRead = Stopwatch.GetTimestamp();
        return _lastBatch;
    }

    private SensorBatch ReadDisks(CancellationToken token)
    {
        var disks = wmi.Read(HardwareWmi.Storage, "MSFT_Disk", "Number,Model,HealthStatus,SerialNumber,BusType", token);
        if (disks.State != ReadingState.Available)
            return new([], [new(Category, disks.State, LocalizationService.Source("Message.WindowsDidNotProvideAccessToDriveCounters"))]);
        var time = DateTimeOffset.Now;
        var groups = new List<SensorGroup>();
        var notices = new List<SensorNotice>();
        var targets = disks.Rows.Where(disk => disk.UInt("Number").HasValue).GroupBy(disk => disk.UInt("Number")!.Value)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        foreach (var disk in targets.Take(16))
        {
            token.ThrowIfCancellationRequested();
            var number = disk.UInt("Number")!.Value;
            var id = $"disk/{number}";
            var name = LocalizationService.Source("Message.Drive0", number) + (disk.Text("Model") is { } model ? $" · {model}" : "");
            var fields = new List<HardwareReading>();
            if (WindowsStorageProvider.Health(disk.UInt("HealthStatus")) is { } health)
                fields.Add(HardwareReading.Create(id, LocalizationService.Source("Message.HealthReportedByWindows"), health, WindowsStorageProvider.StorageDisk, time) with { LocalizeValue = true });
            var native = disk.UInt("BusType") is 14 or 15 or 16 ? new DiskTemperatureResult(ReadingState.Unsupported, [])
                : temperatures.Read(number, disk.Text("SerialNumber"));
            foreach (var (index, celsius) in native.Values)
                fields.Add(HardwareReading.Create(id, LocalizationService.Source("Message.TemperatureSensor0", index), (double)celsius, WindowsDiskTemperatures.Source, time, "°C"));
            var counters = wmi.Associated(HardwareWmi.Storage, disk.Path, "MSFT_DiskToStorageReliabilityCounter",
                "MSFT_StorageReliabilityCounter", CounterColumns, token);
            if (counters.Rows is [var row])
            {
                if (native.Values.Count == 0) Add(LocalizationService.Source("Message.Temperature"), "Temperature", "°C", positive: true);
                Add(LocalizationService.Source("Message.PowerOnHours"), "PowerOnHours", "h");
                Add(LocalizationService.Source("Message.ReportedWear"), "Wear", "%");
                Add(LocalizationService.Source("Message.UncorrectedReadErrors"), "ReadErrorsUncorrected", "");
                Add(LocalizationService.Source("Message.UncorrectedWriteErrors"), "WriteErrorsUncorrected", "");

                void Add(string label, string key, string unit, bool positive = false)
                {
                    var value = row.Number(key);
                    if (!value.HasValue || (positive && value == 0)) return;
                    fields.Add(HardwareReading.Create(id, label, (double)value.Value, CounterSource, time, unit));
                }
            }
            if (fields.Count > 0) groups.Add(new(id, name, fields));
            if (fields.All(field => field.Unit != "°C"))
                notices.Add(new(name, native.State == ReadingState.Available ? ReadingState.NoReading : native.State,
                    LocalizationService.Source("Message.TheDeviceDidNotReturnATemperature")));
            if (counters.Rows.Count != 1 || fields.All(field => field.Source != CounterSource))
                notices.Add(new(name, counters.State == ReadingState.Available ? ReadingState.NoReading : counters.State,
                    LocalizationService.Source("Message.WindowsDidNotReturnReliabilityCounters")));
        }
        if (targets.Length > 16)
            notices.Add(new(Category, ReadingState.NoReading, LocalizationService.Source("Message.PeriodicReadingsAreLimitedTo16Drives")));
        return new(groups, notices);
    }

    public void Dispose() { }
}
