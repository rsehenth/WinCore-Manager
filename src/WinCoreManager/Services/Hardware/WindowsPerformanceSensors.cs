using WinCoreManager.Localization;
using System.Runtime.InteropServices;

namespace WinCoreManager.Services.Hardware;

internal sealed unsafe partial class WindowsPerformanceSensors(IMemoryFacts memory) : ISensorProvider
{
    private CpuTimes? _previous;
    private readonly uint _processors = GetActiveProcessorCount(ushort.MaxValue);
    private readonly bool _singleGroup = GetActiveProcessorGroupCount() == 1;

    public string Category => LocalizationService.Source("Message.ProcessorAndMemory");
    public TimeSpan Interval => TimeSpan.Zero;

    public SensorBatch Read(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        const string cpu = "cpu/system";
        const string ram = "ram";
        var time = DateTimeOffset.Now;
        var cpuValues = new List<HardwareReading>();
        if (_singleGroup && GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var current = new CpuTimes(idle, kernel, user);
            cpuValues.Add(HardwareReading.Create(cpu, LocalizationService.Source("Message.TotalUtilization"), CpuLoad(_previous, current), "Windows GetSystemTimes", time, "%"));
            _previous = current;
        }
        else
            cpuValues.Add(HardwareReading.Create(cpu, LocalizationService.Source("Message.TotalUtilization"), null, "Windows GetSystemTimes", time, "%",
                _singleGroup ? ReadingState.Error : ReadingState.Unsupported));
        var speed = ReadClock();
        if (speed.HasValue)
            cpuValues.Add(HardwareReading.Create(cpu, LocalizationService.Source("Message.AverageClockReportedByWindows"), speed, "Windows ProcessorInformation", time, "MHz"));
        var groups = new List<SensorGroup> { new(cpu, LocalizationService.Source("Message.ProcessorSystemUtilization"), cpuValues) };
        var notices = new List<SensorNotice>
        {
            new(LocalizationService.Source("Message.ProcessorAndMotherboard"), ReadingState.Unsupported, LocalizationService.Source("Message.TemperaturesVoltagesPowerAndMotherboardFanSpeedsRequire")),
            new(LocalizationService.Source("Message.MemoryModules"), ReadingState.Unsupported, LocalizationService.Source("Message.WindowsReportsMemoryUsageAndAvailabilityModuleTemperature"))
        };
        if (memory.Read() is { } sample)
            groups.Add(new(ram, LocalizationService.Source("Message.MemoryRAM"), [
                HardwareReading.Create(ram, LocalizationService.Source("Message.Utilization"), (double)sample.Load, "Windows GlobalMemoryStatusEx", time, "%"),
                HardwareReading.Create(ram, LocalizationService.Source("Message.Available"), sample.Available, "Windows GlobalMemoryStatusEx", time, "B")
            ]));
        else
            notices.Add(new(LocalizationService.Source("Message.MemoryRAM"), ReadingState.Error, LocalizationService.Source("Message.WindowsDidNotReturnMemoryCounters")));
        return new(groups, notices);
    }

    internal readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

    internal static double? CpuLoad(CpuTimes? previous, CpuTimes current)
    {
        if (previous is not { } last || current.Idle < last.Idle || current.Kernel < last.Kernel || current.User < last.User) return null;
        var elapsed = (double)(current.Kernel - last.Kernel) + (current.User - last.User);
        var idle = current.Idle - last.Idle;
        return elapsed > 0 && idle <= elapsed ? 100 * (elapsed - idle) / elapsed : null;
    }

    private double? ReadClock()
    {
        if (!_singleGroup || _processors is < 1 or > 64) return null;
        var readings = stackalloc ProcessorPower[(int)_processors];
        var size = (uint)sizeof(ProcessorPower) * _processors;
        if (CallNtPowerInformation(11, 0, 0, readings, size) != 0) return null; // ProcessorInformation, output only.
        double total = 0;
        for (var index = 0; index < _processors; index++)
        {
            if (readings[index].CurrentMHz is 0 or > 100_000) return null;
            total += readings[index].CurrentMHz;
        }
        return total / _processors;
    }

    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPower
    {
        public uint Index, MaxMHz, CurrentMHz, LimitMHz, MaxIdleState, CurrentIdleState;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetActiveProcessorCount(ushort group);

    [LibraryImport("powrprof.dll")]
    private static partial uint CallNtPowerInformation(int level, nint input, uint inputLength, ProcessorPower* output, uint outputLength);
}
