using WinCoreManager.Localization;
using System.Runtime.CompilerServices;

namespace WinCoreManager.Services.Hardware;

public sealed record SensorGroup(string Id, string Name, IReadOnlyList<HardwareReading> Readings)
{
    public string Condition { get; init; } = "Unknown";
    public string ConditionLabel { get; init; } = "";
    public IReadOnlyList<HardwareReading> CpuCoreReadings { get; init; } = [];
    public int CpuCoreCount => CpuCoreReadings.Count;
    public bool HasCpuCoreReadings => CpuCoreCount > 0;
}
public sealed record SensorNotice(string Category, ReadingState State, string Reason)
{
    public string Status => HardwareReading.Create("", "", null, "", default, absent: State).Display;
}
public sealed record SensorBatch(IReadOnlyList<SensorGroup> Groups, IReadOnlyList<SensorNotice> Notices)
{
    public static SensorBatch Empty { get; } = new([], []);
}
public sealed record SensorSnapshot(DateTimeOffset UpdatedAt, IReadOnlyList<SensorGroup> Groups, IReadOnlyList<SensorNotice> Notices);

internal interface ISensorProvider : IDisposable
{
    string Category { get; }
    TimeSpan Interval { get; }
    SensorBatch Read(CancellationToken token);
}

internal interface ISensorProviderFactory { IReadOnlyList<ISensorProvider> Create(); }

public interface ISensorScheduler
{
    IAsyncEnumerable<SensorSnapshot> ObserveAsync(CancellationToken token);
}

internal sealed class SensorScheduler(ISensorProviderFactory factory, TimeProvider clock, TimeSpan interval) : ISensorScheduler
{
    // Pausing, reopening and rapid focus changes can leave a native call draining.
    private readonly SemaphoreSlim _session = new(1, 1);

    public async IAsyncEnumerable<SensorSnapshot> ObserveAsync([EnumeratorCancellation] CancellationToken token)
    {
        await _session.WaitAsync(token).ConfigureAwait(false);
        Slot[] slots = [];
        try
        {
            var created = await Task.Run(factory.Create, token).ConfigureAwait(false);
            slots = created.Select(p => new Slot(p)).ToArray();
            var first = true;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var slot in slots)
                {
                    var slowerThanTick = slot.Provider.Interval > interval;
                    if (first && slowerThanTick) continue;
                    if (slot.Pending is null && (!slot.HasRead || clock.GetElapsedTime(slot.LastRead) >= slot.Provider.Interval))
                        slot.Pending = Task.Run(() => Read(slot.Provider, token), token);
                    if (slot.Pending is { } read && (!slowerThanTick || read.IsCompleted))
                    {
                        slot.Batch = await read.ConfigureAwait(false);
                        slot.Pending = null;
                        slot.LastRead = clock.GetTimestamp();
                        slot.HasRead = true;
                    }
                }
                first = false;
                token.ThrowIfCancellationRequested();
                yield return new(clock.GetLocalNow(), slots.SelectMany(s => s.Batch.Groups).ToArray(),
                    slots.SelectMany(s => s.Batch.Notices).ToArray());
                await Task.Delay(interval, clock, token).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                try
                {
                    await Task.WhenAll(slots.Select(slot => slot.Pending ?? Task.CompletedTask)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                finally
                {
                    await Task.Run(() =>
                    {
                        foreach (var slot in slots)
                            try { slot.Provider.Dispose(); }
                            catch (Exception e) when (e is not OutOfMemoryException) { /* Continue closing other providers. */ }
                    }).ConfigureAwait(false);
                }
            }
            finally { _session.Release(); }
        }
    }

    private static SensorBatch Read(ISensorProvider provider, CancellationToken token)
    {
        try { return provider.Read(token); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            var state = e is UnauthorizedAccessException ? ReadingState.Restricted : ReadingState.Error;
            return new([], [new(provider.Category, state, LocalizationService.Source("Message.TheProviderReturnedNoReadings"))]);
        }
    }

    private sealed class Slot(ISensorProvider provider)
    {
        public ISensorProvider Provider { get; } = provider;
        public long LastRead { get; set; }
        public bool HasRead { get; set; }
        public Task<SensorBatch>? Pending { get; set; }
        public SensorBatch Batch { get; set; } = SensorBatch.Empty;
    }
}

internal sealed class WindowsSensorFactory(IMemoryFacts memory, IGraphicsAdapters graphics, IHardwareWmi wmi, IDiskHealthProbe smart) : ISensorProviderFactory
{
    // This provider retains only the most recent slow snapshot across focus changes.
    private readonly StorageSensors _storage = new(wmi, new WindowsDiskTemperatures(), smart);

    public IReadOnlyList<ISensorProvider> Create() =>
        [new WindowsPerformanceSensors(memory), new CpuTemperatureSensors(() => new LibreCpuTemperatureSession()),
            new NvidiaSensors(graphics), new WindowsGpuEngineSensors(graphics, wmi), _storage];
}
