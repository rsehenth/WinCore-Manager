using System.Globalization;
using WinCoreManager.Localization;

namespace WinCoreManager.Services.Hardware;

internal sealed class WindowsGpuEngineSensors(IGraphicsAdapters graphics, IHardwareWmi wmi) : ISensorProvider
{
    private const string EngineClass = "Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine";
    private const string Source = "Windows GPU Engine (WDDM)";
    private DxgiAdapter[]? adapters;

    public string Category => LocalizationService.Source("Message.GraphicsCard");
    public TimeSpan Interval => TimeSpan.FromSeconds(8);

    public SensorBatch Read(CancellationToken token)
    {
        adapters ??= graphics.Read(token).Where(adapter => adapter.Pci.Vendor != 0x10DE && (adapter.Flags & 2) == 0).ToArray();
        if (adapters.Length == 0)
            return SensorBatch.Empty;

        var counters = wmi.Read(HardwareWmi.Cim, EngineClass, "Name,UtilizationPercentage", token);
        var engineTotals = new Dictionary<(long Luid, uint Physical, uint Engine), ulong>();
        foreach (var row in counters.Rows)
        {
            if (ReadEngine(row.Text("Name")) is { } engine && row.Number("UtilizationPercentage") is <= 100 and { } value)
                engineTotals[engine] = engineTotals.GetValueOrDefault(engine) + value;
        }

        var time = DateTimeOffset.Now;
        var groups = new List<SensorGroup>();
        var notices = new List<SensorNotice>();
        foreach (var adapter in adapters)
        {
            token.ThrowIfCancellationRequested();
            var name = adapter.Name ?? Category;
            var readings = engineTotals.Where(pair => pair.Key.Luid == adapter.Luid).Select(pair => pair.Value).ToArray();
            if (readings.Length == 0)
            {
                var state = counters.State == ReadingState.Available ? ReadingState.NoReading : counters.State;
                notices.Add(new(name, state, LocalizationService.Source("Sensors.GpuUsageMissing")));
                continue;
            }
            groups.Add(new(adapter.InventoryId, name,
                [HardwareReading.Create(adapter.InventoryId, LocalizationService.Source("Sensors.GpuUsage"),
                    (double)Math.Min(100, readings.Max()), Source, time, "%")]));
        }
        return new(groups, notices);
    }

    private static (long Luid, uint Physical, uint Engine)? ReadEngine(string? name)
    {
        var parts = name?.Split('_');
        if (parts is not ["pid", _, "luid", var high, var low, "phys", var physical, "eng", var engine, ..]
            || !Hex(high, out var highBits) || !Hex(low, out var lowBits)
            || !uint.TryParse(physical, NumberStyles.None, CultureInfo.InvariantCulture, out var physicalIndex)
            || !uint.TryParse(engine, NumberStyles.None, CultureInfo.InvariantCulture, out var engineIndex))
            return null;

        return (unchecked((long)(((ulong)highBits << 32) | lowBits)), physicalIndex, engineIndex);
    }

    private static bool Hex(string text, out uint value)
    {
        value = 0;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
    }

    public void Dispose() { }
}
