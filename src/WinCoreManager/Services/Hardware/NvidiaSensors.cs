using WinCoreManager.Localization;
using System.IO;
using System.Runtime.InteropServices;

namespace WinCoreManager.Services.Hardware;

internal sealed unsafe class NvidiaSensors(IGraphicsAdapters graphics) : ISensorProvider
{
    private const string Source = "NVIDIA NVML";
    private readonly Dictionary<string, Delegate?> _exports = [];
    private readonly List<SensorNotice> _notices = [];
    private IReadOnlyList<Gpu> _devices = [];
    private nint _module;
    private bool _started;
    private bool _attempted;

    public string Category => LocalizationService.Source("Message.GraphicsCard");
    public TimeSpan Interval => TimeSpan.Zero;

    public SensorBatch Read(CancellationToken token)
    {
        if (!_attempted)
        {
            _attempted = true;
            Initialize(graphics.Read(token), token);
        }
        var groups = new List<SensorGroup>();
        var notices = new List<SensorNotice>(_notices);
        var time = DateTimeOffset.Now;
        foreach (var gpu in _devices)
        {
            token.ThrowIfCancellationRequested();
            var readings = ReadGpu(gpu, time);
            if (readings.Count > 0) groups.Add(new(gpu.Id, gpu.Name, readings));
            else notices.Add(new(gpu.Name, ReadingState.Unsupported, LocalizationService.Source("Message.TheDriverDidNotExposeCompatibleReadings")));
        }
        return new(groups, notices);
    }

    internal IReadOnlyDictionary<string, ulong> ReadCapacities(IReadOnlyList<DxgiAdapter> adapters, CancellationToken token)
    {
        Initialize(adapters, token);
        var result = new Dictionary<string, ulong>();
        var readMemory = Export<ReadMemory>("nvmlDeviceGetMemoryInfo");
        if (readMemory is null) return result;
        foreach (var device in _devices)
        {
            token.ThrowIfCancellationRequested();
            if (device.MatchedInventory && readMemory(device.Handle, out var memory) == 0 && memory.Total is > 0 and < ulong.MaxValue)
                result.TryAdd(device.Id, memory.Total);
        }
        return result;
    }

    private void Initialize(IReadOnlyList<DxgiAdapter> adapters, CancellationToken token)
    {
        var nvidia = adapters.Where(adapter => adapter.Pci.Vendor == 0x10DE).ToArray();
        foreach (var adapter in adapters.Where(a => a.Pci.Vendor != 0x10DE))
            _notices.Add(new(adapter.Name ?? Category, ReadingState.Unsupported, LocalizationService.Source("Message.TheNVMLProviderSupportsNVIDIAAdaptersOnly")));
        if (nvidia.Length == 0) return;
        _module = LoadLibraryEx("nvml.dll", 0, 0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32
        if (_module == 0 && Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } programFiles)
        {
            var installedPath = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll");
            _module = LoadLibraryEx(installedPath, 0, 0x100 | 0x800); // DLL directory + System32
        }
        var init = Export<NoArgs>("nvmlInit_v2");
        var shutdown = Export<NoArgs>("nvmlShutdown");
        if (init is null || shutdown is null)
        {
            _notices.Add(new(Category, ReadingState.Unsupported, LocalizationService.Source("Message.TheNVIDIADriverNVMLLibraryCouldNotBe")));
            return;
        }
        var initStatus = init();
        if (initStatus != 0)
        {
            _notices.Add(new(Category, State(initStatus), LocalizationService.Source("Message.TheNVIDIADriverDidNotOpenAnNVML")));
            return;
        }
        _started = true;
        var count = Export<Count>("nvmlDeviceGetCount_v2");
        var handleByIndex = Export<HandleByIndex>("nvmlDeviceGetHandleByIndex_v2");
        var getPci = Export<ReadPci>("nvmlDeviceGetPciInfo_v3");
        if (count is null || handleByIndex is null || count(out var handles) != 0)
        {
            _notices.Add(new(Category, ReadingState.Error, LocalizationService.Source("Message.TheNVMLSessionDidNotListAnyAdapters")));
            return;
        }
        var candidates = new List<(nint Handle, PciModel? Model, uint Index, string? Name)>();
        var readName = Export<ReadName>("nvmlDeviceGetName");
        var buffer = stackalloc byte[128];
        for (uint index = 0; index < Math.Min(handles, 128); index++)
        {
            token.ThrowIfCancellationRequested();
            var handleStatus = handleByIndex(index, out var handle);
            if (handleStatus != 0 || handle == 0)
            {
                _notices.Add(new(LocalizationService.Source("Message.NvidiaAdapterNumber", index + 1), State(handleStatus), LocalizationService.Source("Message.TheDriverDidNotProvideAccessToThis")));
                continue;
            }
            PciModel? model = null;
            if (getPci is not null && getPci(handle, out var pci) == 0 && (pci.DeviceId & 0xFFFF) == 0x10DE)
                model = new(0x10DE, pci.DeviceId >> 16, pci.Subsystem);
            string? name = null;
            if (readName is not null && readName(handle, buffer, 128) == 0)
            {
                var bytes = new ReadOnlySpan<byte>(buffer, 128);
                var end = bytes.IndexOf((byte)0);
                if (end >= 0) name = InventoryText.Clean(System.Text.Encoding.UTF8.GetString(bytes[..end]));
            }
            candidates.Add((handle, model, index, name));
        }
        var devices = new List<Gpu>();
        foreach (var entry in candidates)
        {
            var matches = nvidia.Where(a => a.Pci == entry.Model).ToArray();
            var canMatch = matches.Length == 1 && candidates.Count(other => other.Model == entry.Model) == 1;
            var id = canMatch ? matches[0].InventoryId : $"nvml/{entry.Index}";
            var name = canMatch ? matches[0].Name ?? entry.Name : entry.Name;
            name ??= LocalizationService.Source("Message.NvidiaAdapter");
            if (!canMatch) name += $" · NVML {entry.Index + 1}";
            devices.Add(new(entry.Handle, id, name, canMatch));
        }
        _devices = devices;
    }

    private List<HardwareReading> ReadGpu(Gpu gpu, DateTimeOffset time)
    {
        var readings = new List<HardwareReading>();
        var temperature = Export<Selector>("nvmlDeviceGetTemperature");
        if (temperature is not null)
        {
            var result = temperature(gpu.Handle, 0, out var value);
            // NVML_ERROR_NOT_SUPPORTED omits the sensor entirely.
            Add(LocalizationService.Source("Message.GPUTemperature"), result, value, "°C");
        }
        var utilization = Export<ReadUtilization>("nvmlDeviceGetUtilizationRates");
        if (utilization is not null)
        {
            var result = utilization(gpu.Handle, out var value);
            Add(LocalizationService.Source("Message.GPUUtilization"), result, value.Gpu, "%");
        }
        var clock = Export<Selector>("nvmlDeviceGetClockInfo");
        if (clock is not null)
        {
            var result = clock(gpu.Handle, 0, out var core);
            Add(LocalizationService.Source("Message.GPUCoreClock"), result, core, "MHz");
            result = clock(gpu.Handle, 2, out var memoryClock);
            Add(LocalizationService.Source("Message.MemoryClock"), result, memoryClock, "MHz");
        }
        var power = Export<ReadUInt>("nvmlDeviceGetPowerUsage");
        if (power is not null)
        {
            var result = power(gpu.Handle, out var value);
            Add(LocalizationService.Source("Message.ReportedPower"), result, value == uint.MaxValue ? uint.MaxValue : value / 1000d, "W");
        }
        var memory = Export<ReadMemory>("nvmlDeviceGetMemoryInfo");
        if (memory is not null)
        {
            var result = memory(gpu.Handle, out var value);
            if (result != 3)
                readings.Add(HardwareReading.Create(gpu.Id, LocalizationService.Source("Message.ReportedVRAMUsage"), result == 0 && value.Used <= value.Total && value.Total > 0 && value.Total != ulong.MaxValue
                    ? value.Used : null, Source, time, "B", result == 0 ? ReadingState.NoReading : State(result)));
        }
        var fanCount = Export<ReadUInt>("nvmlDeviceGetNumFans");
        var readRpm = Export<ReadFan>("nvmlDeviceGetFanSpeedRPM");
        var readPercent = Export<Selector>("nvmlDeviceGetFanSpeed_v2");
        if (fanCount is not null && fanCount(gpu.Handle, out var fans) == 0)
        {
            for (uint index = 0; index < Math.Min(fans, 16); index++)
            {
                var info = new FanSpeed { Version = 0x0100000C, Fan = index };
                var result = readRpm?.Invoke(gpu.Handle, ref info) ?? 3;
                if (result == 3 && readPercent is not null)
                {
                    result = readPercent(gpu.Handle, index, out var percent);
                    Add(LocalizationService.Source("Message.Fan0TargetSpeed", index + 1), result, percent, "%");
                }
                else Add(LocalizationService.Source("Message.Fan0", index + 1), result, info.Speed, "RPM");
            }
        }
        return readings;

        void Add(string label, int result, double value, string unit)
        {
            if (result != 3)
                readings.Add(HardwareReading.Create(gpu.Id, label, result == 0 && value != uint.MaxValue ? value : null, Source, time, unit, State(result)));
        }
    }

    private T? Export<T>(string name) where T : Delegate
    {
        if (_module == 0) return null;
        if (!_exports.TryGetValue(name, out var function))
        {
            function = NativeLibrary.TryGetExport(_module, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
            _exports.Add(name, function);
        }
        return (T?)function;
    }

    internal static ReadingState State(int result) => result switch
    {
        0 => ReadingState.NoReading, 3 or 12 or 13 or 25 => ReadingState.Unsupported,
        4 or 27 => ReadingState.Restricted, 6 => ReadingState.NoReading, _ => ReadingState.Error
    };

    public void Dispose()
    {
        try { if (_started) Export<NoArgs>("nvmlShutdown")?.Invoke(); }
        finally
        {
            _started = false;
            if (_module != 0) NativeLibrary.Free(_module);
            _module = 0;
            _devices = [];
            _exports.Clear();
        }
    }

    private sealed record Gpu(nint Handle, string Id, string Name, bool MatchedInventory);
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlPci
    {
        public fixed byte LegacyBusId[16];
        public uint Domain, Bus, Device, DeviceId, Subsystem;
        public fixed byte BusId[32];
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FanSpeed { public uint Version, Fan, Speed; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Utilization { public uint Gpu, Memory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct VideoMemory { public ulong Total, Free, Used; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndex(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadPci(nint device, out NvmlPci value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadName(nint device, byte* name, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadUInt(nint device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Selector(nint device, uint selector, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadFan(nint device, ref FanSpeed fan);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadUtilization(nint device, out Utilization value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadMemory(nint device, out VideoMemory value);
    [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode)]
    private static extern nint LoadLibraryEx(string name, nint file, uint flags);
}

internal sealed class NvidiaMemoryCapacity(IGraphicsAdapters graphics) : IGpuMemoryCapacity
{
    public IReadOnlyDictionary<string, ulong> Read(IReadOnlyList<DxgiAdapter> adapters, CancellationToken token)
    {
        try
        {
            using var session = new NvidiaSensors(graphics);
            return session.ReadCapacities(adapters, token);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return new Dictionary<string, ulong>();
        }
    }
}
