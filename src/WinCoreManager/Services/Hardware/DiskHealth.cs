using System.Globalization;

namespace WinCoreManager.Services.Hardware;

internal enum DiskHealthState { Unknown, Healthy, Attention, Critical }

internal sealed record SmartMetric(string Label, string Raw, string? Normalized = null,
    DiskHealthState Condition = DiskHealthState.Unknown);

internal sealed record DiskHealthSample(ReadingState State, DiskHealthState Health, string Source,
    IReadOnlyList<SmartMetric> Metrics, int? Celsius = null, int? RemainingLife = null);

internal sealed record PhysicalDiskHealth(HardwareDevice Device, DiskHealthSample Sample);

internal sealed record DiskHealthReport(ReadingState State, IReadOnlyList<PhysicalDiskHealth> Disks);

internal interface IDiskHealthProbe
{
    DiskHealthSample Read(uint number, string? privateSerial, uint? bus, bool mechanical);
}

internal sealed class DiskHealthService
{
    private readonly object gate = new();
    private readonly Func<CancellationToken, DiskHealthReport> read;
    private readonly TimeProvider clock;
    private readonly TimeSpan waitLimit;
    private Task<DiskHealthReport>? pending;
    private bool pendingTimedOut;
    private DiskHealthReport? cached;
    private DateTimeOffset cachedAt;

    public DiskHealthService(WindowsStorageProvider storage, IDiskHealthProbe probe)
        : this(token => Read(storage, probe, token), TimeProvider.System) { }

    internal DiskHealthService(Func<CancellationToken, DiskHealthReport> read, TimeProvider clock, TimeSpan? waitLimit = null)
    {
        this.read = read;
        this.clock = clock;
        this.waitLimit = waitLimit ?? TimeSpan.FromSeconds(15);
    }

    internal Task<DiskHealthReport> ReadAsync(bool refresh)
    {
        Task<DiskHealthReport> inFlight;
        lock (gate)
        {
            if (!refresh && cached is not null)
                return Task.FromResult(cached);
            if (pending is { IsCompleted: false })
            {
                if (!refresh && pendingTimedOut)
                    return Task.FromException<DiskHealthReport>(new TimeoutException());
                inFlight = pending;
            }
            else if (cached is not null && clock.GetUtcNow() - cachedAt < TimeSpan.FromSeconds(10))
            {
                return Task.FromResult(cached);
            }
            else
            {
                pendingTimedOut = false;
                inFlight = pending = Task.Run(() =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(14));
                    var report = read(timeout.Token);
                    lock (gate)
                    {
                        cached = report;
                        cachedAt = clock.GetUtcNow();
                        pendingTimedOut = false;
                    }
                    return report;
                });
            }
        }
        return WaitWithTimeoutAsync(inFlight);
    }

    private async Task<DiskHealthReport> WaitWithTimeoutAsync(Task<DiskHealthReport> task)
    {
        try
        {
            return await task.WaitAsync(waitLimit).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, task) && !task.IsCompleted)
                    pendingTimedOut = true;
            }
            throw;
        }
    }

    private static DiskHealthReport Read(WindowsStorageProvider storage, IDiskHealthProbe probe, CancellationToken token)
    {
        var inventory = storage.Read(token, out var modern);
        var result = new List<PhysicalDiskHealth>();
        foreach (var device in inventory.Devices)
        {
            if (!uint.TryParse(device.Id.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                continue;
            var rows = modern.Rows.Where(row => row.UInt("Number") == number).ToArray();
            var row = rows is [var exact] ? exact : null;
            var bus = row?.UInt("BusType");
            if (bus is 9 or 14 or 15 or 16 or 20)
                continue;

            var mechanical = device.Fields.Any(field => Equals(field.Value, "HDD"));
            var sample = token.IsCancellationRequested
                ? new DiskHealthSample(ReadingState.Error, DiskHealthState.Unknown, "", [])
                : probe.Read(number, row?.Text("SerialNumber"), bus, mechanical);
            result.Add(new(device, sample));
        }

        return new(result.Count == 0 && inventory.State == ReadingState.Available
            ? ReadingState.NoReading : inventory.State, result);
    }
}
