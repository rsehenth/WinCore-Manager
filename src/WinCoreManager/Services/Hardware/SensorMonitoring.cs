using WinCoreManager.Localization;
using System.Runtime.CompilerServices;

namespace WinCoreManager.Services.Hardware;

public sealed record SensorGroup(string Id, string Name, IReadOnlyList<HardwareReading> Readings);
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
        IReadOnlyList<ISensorProvider> providers = [];
        try
        {
            var created = await Task.Run(factory.Create, token).ConfigureAwait(false);
            providers = created;
            var slots = providers.Select(p => new Slot(p)).ToArray();
            var first = true;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                foreach (var slot in slots.Where(s => !s.HasRead || clock.GetElapsedTime(s.LastRead) >= s.Provider.Interval))
                {
                    if (first && slot.Provider.Interval > interval) continue;
                    slot.Batch = await Task.Run(() => Read(slot.Provider, token), token).ConfigureAwait(false);
                    slot.LastRead = clock.GetTimestamp();
                    slot.HasRead = true;
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
                await Task.Run(() =>
                {
                    foreach (var provider in providers)
                        try { provider.Dispose(); }
                        catch (Exception e) when (e is not OutOfMemoryException) { /* Continue closing other providers. */ }
                }).ConfigureAwait(false);
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
        public SensorBatch Batch { get; set; } = SensorBatch.Empty;
    }
}

internal sealed class WindowsSensorFactory(IMemoryFacts memory, IGraphicsAdapters graphics, IHardwareWmi wmi) : ISensorProviderFactory
{
    // This provider retains only the most recent slow snapshot across focus changes.
    private readonly StorageSensors _storage = new(wmi, new WindowsDiskTemperatures());

    public IReadOnlyList<ISensorProvider> Create() =>
        [new WindowsPerformanceSensors(memory), new NvidiaSensors(graphics), _storage];
}
