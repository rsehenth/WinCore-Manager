using WinCoreManager.Localization;
using System.Diagnostics;
using System.Globalization;

namespace WinCoreManager.Services.Hardware;

internal sealed class StorageSensors(IHardwareWmi wmi, IDiskTemperatures temperatures, IDiskHealthProbe? smart = null) : ISensorProvider
{
    private const string CounterSource = "Windows Storage · MSFT_StorageReliabilityCounter";
    private const string ActivitySource = "Windows PhysicalDisk";
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
        var disks = wmi.Read(HardwareWmi.Storage, "MSFT_Disk", WindowsStorageProvider.DiskColumns, token);
        if (disks.State != ReadingState.Available)
            return new([], [new(Category, disks.State, LocalizationService.Source("Message.WindowsDidNotProvideAccessToDriveCounters"))]);
        var time = DateTimeOffset.Now;
        var groups = new List<SensorGroup>();
        var notices = new List<SensorNotice>();
        var targets = disks.Rows.Where(disk => disk.UInt("Number").HasValue).GroupBy(disk => disk.UInt("Number")!.Value)
            .Where(group => group.Count() == 1).Select(group => group.Single()).ToArray();
        var activity = wmi.Read(HardwareWmi.Cim, "Win32_PerfFormattedData_PerfDisk_PhysicalDisk",
            "Name,PercentIdleTime,DiskReadBytesPersec,DiskWriteBytesPersec", token);
        var activityRows = activity.Rows.Select(row => (Number: DiskNumber(row.Text("Name")), Row: row))
            .Where(entry => entry.Number.HasValue).GroupBy(entry => entry.Number!.Value)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().Row);
        var physicalDisks = wmi.Read(HardwareWmi.Storage, "MSFT_PhysicalDisk",
            "UniqueId,UniqueIdFormat,Size,SerialNumber,MediaType", token);
        var volumes = wmi.Read(HardwareWmi.Storage, "MSFT_Partition", "DiskNumber,DriveLetter", token).Rows
            .Select(row => (Number: row.UInt("DiskNumber"), Letter: WindowsStorageProvider.Letter(row.Get("DriveLetter"))))
            .Where(row => row.Number.HasValue && row.Letter is not null)
            .GroupBy(row => row.Number!.Value)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(row => row.Letter).Distinct().Order()));
        var temperatureLabel = LocalizationService.Source("Message.Temperature");
        var hoursLabel = LocalizationService.Source("Message.PowerOnHours");
        var wearLabel = LocalizationService.Source("Message.ReportedWear");
        foreach (var disk in targets.Take(16))
        {
            token.ThrowIfCancellationRequested();
            var number = disk.UInt("Number")!.Value;
            var id = $"disk/{number}";
            var name = LocalizationService.Source("Message.Drive0", number) + (disk.Text("Model") is { } model ? $" · {model}" : "");
            if (volumes.TryGetValue(number, out var letters)) name += $" ({letters})";
            var fields = new List<HardwareReading>();
            if (WindowsStorageProvider.Health(disk.UInt("HealthStatus")) is { } health)
                fields.Add(HardwareReading.Create(id, LocalizationService.Source("Message.HealthReportedByWindows"), health, WindowsStorageProvider.StorageDisk, time) with { LocalizeValue = true });
            var bus = disk.UInt("BusType");
            var serial = WindowsStorageProvider.UniqueSerial(disk, disks.Rows);
            var localDevice = bus is not (9 or 14 or 15 or 16 or 20);
            var physical = localDevice ? WindowsStorageProvider.MatchPhysical(disk, disks.Rows, physicalDisks.Rows) : null;
            var mechanical = physical?.UInt("MediaType") == 3;
            var ata = WindowsSmart.IsAtaBus(bus);
            var sample = bus == 17 || ata ? smart?.Read(number, serial, bus, mechanical) : null;
            var sampleTime = sample?.CollectedAt ?? time;
            var native = sample?.NativeTemperature ?? (localDevice ? temperatures.Read(number, serial)
                : new DiskTemperatureResult(ReadingState.Unsupported, []));
            foreach (var (index, celsius) in native.Values)
                fields.Add(HardwareReading.Create(id, LocalizationService.Source("Message.TemperatureSensor0", index), (double)celsius, WindowsDiskTemperatures.Source, sampleTime, "°C"));
            if (native.Values.Count == 0 && sample?.Celsius is { } composite)
                fields.Add(HardwareReading.Create(id, temperatureLabel, (double)composite,
                    sample.TemperatureSource ?? sample.Source, sampleTime, "°C"));
            if (sample is { Health: not DiskHealthState.Unknown })
                fields.Add(HardwareReading.Create(id, LocalizationService.Source(bus == 17 ? "Sensors.NvmeHealth" : "DiskHealth.SmartLabel"),
                    ConditionLabel(sample.Health), sample.Source, sampleTime) with { LocalizeValue = true });
            if (ata && sample is { RemainingLife: { } remaining, WearSource: { } source })
                fields.Add(HardwareReading.Create(id, LocalizationService.Source("DiskHealth.LifeLabel"), (double)remaining, source, sampleTime, "%"));
            if (ata && sample is not null)
            {
                foreach (var metric in sample.Metrics)
                {
                    if (metric.Count is not { } count) continue;
                    if (metric.Label == "Wear" && count > 100)
                        fields.Add(HardwareReading.Create(id, wearLabel, metric.Raw, metric.Source ?? sample.Source, sampleTime));
                    else
                    {
                        var (label, unit) = metric.Label switch
                        {
                            "Wear" => (wearLabel, "%"),
                            "PowerHours" => (hoursLabel, "h"),
                            _ => (LocalizationService.Source($"DiskHealth.Metric.{metric.Label}"), "")
                        };
                        fields.Add(HardwareReading.Create(id, label, (double)count, metric.Source ?? sample.Source, sampleTime, unit));
                    }
                }
            }

            var counters = wmi.Associated(HardwareWmi.Storage, disk.Path, "MSFT_DiskToStorageReliabilityCounter",
                "MSFT_StorageReliabilityCounter", CounterColumns, token);
            if (physical is not null && (counters.Rows is not [var direct] || !HasCounters(direct)))
            {
                var fallback = wmi.Associated(HardwareWmi.Storage, physical.Path, "MSFT_PhysicalDiskToStorageReliabilityCounter",
                    "MSFT_StorageReliabilityCounter", CounterColumns, token);
                if (fallback.Rows is [var match] && HasCounters(match))
                    counters = fallback;
            }
            if (counters.Rows is [var row])
            {
                if (fields.All(field => field.Unit != "°C") && row.Number("Temperature") is > 0 and <= 125)
                    Add(temperatureLabel, "Temperature", "°C");
                if (fields.All(field => field.Label != hoursLabel)) Add(hoursLabel, "PowerOnHours", "h");
                if (!mechanical && fields.All(field => field.Label != wearLabel)) Add(wearLabel, "Wear", "%");
                Add(LocalizationService.Source("Message.UncorrectedReadErrors"), "ReadErrorsUncorrected", "");
                Add(LocalizationService.Source("Message.UncorrectedWriteErrors"), "WriteErrorsUncorrected", "");

                void Add(string label, string key, string unit)
                {
                    var value = row.Number(key);
                    if (!value.HasValue) return;
                    fields.Add(HardwareReading.Create(id, label, (double)value.Value, CounterSource, time, unit));
                }
            }
            if (bus == 17 && sample is not null)
            {
                if (fields.All(field => field.Label != wearLabel) && sample.Metrics.FirstOrDefault(metric => metric.Label == "Wear") is { } wear)
                    fields.Add(HardwareReading.Create(id, wearLabel, wear.Raw, sample.Source, sampleTime));
                if (fields.All(field => field.Label != hoursLabel) && sample.Metrics.FirstOrDefault(metric => metric.Label == "PowerHours") is { } hours)
                    fields.Add(HardwareReading.Create(id, hoursLabel, hours.Raw, sample.Source, sampleTime));
            }

            if (activityRows.TryGetValue(number, out var performance))
            {
                if (performance.Number("PercentIdleTime") is <= 100 and { } idle)
                    fields.Add(HardwareReading.Create(id, LocalizationService.Source("Sensors.DiskActive"), 100d - idle, ActivitySource, time, "%"));
                AddRate("Sensors.DiskRead", "DiskReadBytesPersec");
                AddRate("Sensors.DiskWrite", "DiskWriteBytesPersec");

                void AddRate(string label, string key)
                {
                    if (performance.Number(key) is { } rate)
                        fields.Add(HardwareReading.Create(id, LocalizationService.Source(label), rate, ActivitySource, time, "B/s"));
                }
            }
            if (fields.All(field => field.Source != ActivitySource))
                notices.Add(new(name, activity.State == ReadingState.Available ? ReadingState.NoReading : activity.State,
                    LocalizationService.Source("Sensors.DiskActivityMissing")));

            var condition = (DiskHealthState)Math.Max((int)(sample?.Health ?? DiskHealthState.Unknown),
                disk.UInt("HealthStatus") switch { 0 => 1, 1 => 2, 2 => 3, _ => 0 });
            if (fields.Count == 0)
                fields.Add(HardwareReading.Create(id, LocalizationService.Source("Message.Temperature"), null, "", time, absent: native.State));
            groups.Add(new(id, name, fields) { Condition = condition.ToString(), ConditionLabel = ConditionLabel(condition) });
            if (fields.All(field => field.Unit != "°C"))
                notices.Add(new(name, sample?.TemperatureState ?? (native.State == ReadingState.Available ? ReadingState.NoReading : native.State),
                    LocalizationService.Source(localDevice && serial is null ? "DiskHealth.IdentityUnavailable" : "Message.TheDeviceDidNotReturnATemperature")));
            if (ata && physical?.UInt("MediaType") == 4 && sample is { RemainingLife: null })
                notices.Add(new(name, sample.WearState, LocalizationService.Source("DiskHealth.Note.NoWear")));
            if (sample?.State != ReadingState.Available && fields.All(field => field.Source != CounterSource))
                notices.Add(new(name, counters.State == ReadingState.Available ? ReadingState.NoReading : counters.State,
                    LocalizationService.Source("Message.WindowsDidNotReturnReliabilityCounters")));
        }
        if (targets.Length == 0)
            notices.Add(new(Category, ReadingState.NoReading, LocalizationService.Source("Message.WindowsDidNotProvideAccessToDriveCounters")));
        if (targets.Length > 16)
            notices.Add(new(Category, ReadingState.NoReading, LocalizationService.Source("Message.PeriodicReadingsAreLimitedTo16Drives")));
        return new(groups, notices);
    }

    private static string ConditionLabel(DiskHealthState state) => state switch
    {
        DiskHealthState.Healthy => LocalizationService.Source("Message.Healthy"),
        DiskHealthState.Attention => LocalizationService.Source("Message.Warning"),
        DiskHealthState.Critical => LocalizationService.Source("Message.Unhealthy"),
        _ => ""
    };

    private static bool HasCounters(WmiRow row) => row.Number("Temperature") is > 0
        || row.Number("PowerOnHours").HasValue || row.Number("Wear").HasValue
        || row.Number("ReadErrorsUncorrected").HasValue || row.Number("WriteErrorsUncorrected").HasValue;

    private static uint? DiskNumber(string? name)
    {
        var span = name.AsSpan();
        var separator = span.IndexOf(' ');
        return uint.TryParse(separator < 0 ? span : span[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;
    }

    public void Dispose() { }
}
