// Portions adapted from DiskScope (MIT), Copyright (c) 2026 Syntaxo.
// SmartProbe.cs, AtaSmart.cs and NvmeHealth.cs at fc4ac0e15813071625d633969d12a438dc48592c.
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.Hardware;

internal sealed unsafe class WindowsSmart : IDiskHealthProbe
{
    internal const string NvmeSource = "NVMe SMART / Health Information (02h)";
    internal const string AtaSource = "Windows IOCTL_STORAGE_PREDICT_FAILURE";
    internal const string SctSource = "ATA SMART · SCT (E0h)";
    internal const string AtaEnduranceSource = "ATA Device Statistics (04h/07h)";
    private const string AtaHoursSource = "ATA Device Statistics (04h/01h)";
    private const uint PredictFailure = 0x002D1100;
    private const int SectorLength = 512;
    private readonly object _gate = new();
    private readonly Dictionary<uint, (string Serial, uint? Bus, bool Mechanical, DiskHealthSample Sample)> _cache = [];

    public DiskHealthSample Read(uint number, string? privateSerial, uint? bus, bool mechanical)
    {
        if (privateSerial is null)
            return Unknown(ReadingState.NoReading) with { UnavailableReason = "DiskHealth.IdentityUnavailable" };
        using var disk = WindowsDiskTemperatures.OpenQueryHandle(number);
        if (disk.IsInvalid)
            return Unknown(WindowsDiskTemperatures.Error(Marshal.GetLastPInvokeError()));

        var identity = WindowsDiskTemperatures.VerifyIdentity(disk, privateSerial, bus);
        if (identity != ReadingState.Available)
            return Unknown(identity) with { UnavailableReason = identity == ReadingState.NoReading ? "DiskHealth.IdentityUnavailable" : null };

        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (_cache.TryGetValue(number, out var entry) && entry.Bus == bus && entry.Mechanical == mechanical
                && string.Equals(entry.Serial, privateSerial, StringComparison.OrdinalIgnoreCase)
                && entry.Sample.CollectedAt is { } prior && now >= prior && now - prior < TimeSpan.FromSeconds(10))
                return entry.Sample;

            var native = WindowsDiskTemperatures.ReadNative(disk);
            var result = bus == 17 ? ReadNvme(disk, native) : ReadAta(disk, bus, mechanical, native);
            result = result with { NativeTemperature = native, CollectedAt = DateTimeOffset.UtcNow };
            if (_cache.Count >= 64) _cache.Clear();
            _cache[number] = (privateSerial, bus, mechanical, result);
            return result;
        }
    }

    private static DiskHealthSample ReadAta(SafeFileHandle disk, uint? bus, bool mechanical, DiskTemperatureResult native)
    {
        Span<byte> data = stackalloc byte[4 + SectorLength];
        uint written;
        ReadingState predictState;
        fixed (byte* output = data)
            predictState = WindowsDiskTemperatures.Invoke(disk, PredictFailure, null, 0, output, (uint)data.Length, out written);
        if (predictState == ReadingState.Available && (written < 4 || written > data.Length))
            predictState = ReadingState.Error;
        bool? predictsFailure = predictState == ReadingState.Available ? BinaryPrimitives.ReadUInt32LittleEndian(data) != 0 : null;
        var ataAttributes = IsAtaBus(bus) || bus == 7; // Some USB bridges expose the legacy ATA SMART sector.
        var attributes = ataAttributes && predictsFailure.HasValue && written == data.Length ? data[4..] : [];
        var sample = ParseAta(attributes, predictsFailure, mechanical, null);
        int? celsius = Primary(native);
        var temperatureState = native.State;
        var temperatureSource = celsius.HasValue ? WindowsDiskTemperatures.Source : null;
        byte? used = null;
        var wearState = ReadingState.Unsupported;
        var metrics = sample.Metrics.ToList();

        if (IsAtaBus(bus))
        {
            Span<byte> log = stackalloc byte[SectorLength];
            if (!celsius.HasValue)
            {
                temperatureState = ReadLog(disk, 50, 2, 0xE0, 0, log);
                if (temperatureState == ReadingState.Available)
                {
                    celsius = ParseSctTemperature(log);
                    temperatureState = celsius.HasValue ? ReadingState.Available : ReadingState.NoReading;
                    if (celsius.HasValue) temperatureSource = SctSource;
                }
                if (!celsius.HasValue && temperatureState != ReadingState.TimedOut
                    && ReadLog(disk, 50, 2, 4, 5, log) == ReadingState.Available
                    && ReadAtaStatistic(log, 5, 8, 1) is { } raw
                    && unchecked((sbyte)raw) is var degrees && WindowsDiskTemperatures.ValidTemperature(degrees))
                {
                    celsius = degrees;
                    temperatureSource = "ATA Device Statistics (04h/05h)";
                }
            }
            if (ReadLog(disk, 50, 2, 4, 1, log) == ReadingState.Available
                && ReadAtaStatistic(log, 1, 16, 4) is { } hours)
                metrics.Add(new("PowerHours", $"{Format(hours)} h", Count: hours, Source: AtaHoursSource));
            if (!mechanical)
            {
                wearState = ReadLog(disk, 50, 2, 4, 7, log);
                if (wearState == ReadingState.Available)
                {
                    used = ParseAtaEndurance(log);
                    wearState = used.HasValue ? ReadingState.Available : ReadingState.NoReading;
                    if (used.HasValue) metrics.Add(new("Wear", $"{used}%", Count: used.Value, Source: AtaEnduranceSource));
                }
            }
        }
        if (celsius.HasValue) metrics.Add(new("Temperature", $"{celsius} °C", Source: temperatureSource));
        var hasData = predictsFailure.HasValue || metrics.Count != 0;
        var source = predictsFailure.HasValue ? AtaSource
            : metrics.FirstOrDefault(metric => metric.Source is not null)?.Source ?? AtaSource;
        var health = sample.Health == DiskHealthState.Healthy && used is >= 100 ? DiskHealthState.Attention : sample.Health;
        return new(hasData ? ReadingState.Available : predictState, health, source, metrics, celsius, RemainingEndurance(used))
        {
            TemperatureSource = temperatureSource,
            TemperatureState = celsius.HasValue ? ReadingState.Available : temperatureState,
            WearSource = used.HasValue ? AtaEnduranceSource : null,
            WearState = wearState
        };
    }

    internal static bool IsAtaBus(uint? bus) => bus is 2 or 3 or 11;

    private static DiskHealthSample ReadNvme(SafeFileHandle disk, DiskTemperatureResult native)
    {
        Span<byte> log = stackalloc byte[SectorLength];
        var queryState = ReadingState.Unsupported;
        // Device first, then adapter, with exactly one read-only log page request per property.
        foreach (var property in new uint[] { 50, 49 })
        {
            queryState = ReadLog(disk, property, 3, 2, 0, log);
            if (queryState != ReadingState.Available) continue;
            var sample = ParseNvme(log);
            if (sample.State != ReadingState.Available) { queryState = sample.State; continue; }
            return sample.Celsius.HasValue ? sample
                : sample with { Celsius = Primary(native), TemperatureState = native.State, TemperatureSource = WindowsDiskTemperatures.Source };
        }
        return Unknown(queryState, Primary(native)) with
        {
            TemperatureState = native.State,
            TemperatureSource = WindowsDiskTemperatures.Source,
            WearState = queryState
        };
    }

    private static int? Primary(DiskTemperatureResult result) =>
        result.Values.Where(value => value.Index == 0).Select(value => (int?)value.Celsius).FirstOrDefault();

    private static ReadingState ReadLog(SafeFileHandle disk, uint property, uint protocol, uint address, uint page, Span<byte> log)
    {
        Span<byte> response = stackalloc byte[8 + 40 + SectorLength];
        response.Clear();
        var request = MemoryMarshal.Cast<byte, uint>(response);
        request[0] = property;
        request[2] = protocol; // ProtocolTypeAta or ProtocolTypeNvme
        request[3] = 2; // Log page for both protocols
        request[4] = address;
        request[5] = page;
        request[6] = 40; // Relative to STORAGE_PROTOCOL_SPECIFIC_DATA.
        request[7] = SectorLength;
        fixed (byte* buffer = response)
        {
            var status = WindowsDiskTemperatures.Invoke(disk, 0x002D1400, buffer, (uint)response.Length,
                buffer, (uint)response.Length, out var written);
            if (status != ReadingState.Available) return status;
            if (written > response.Length) return ReadingState.Error;
            var payload = ExtractLog(response[..(int)written], protocol, address, page);
            if (payload.Length != SectorLength) return ReadingState.Error;
            payload.CopyTo(log);
            return ReadingState.Available;
        }
    }

    internal static ReadOnlySpan<byte> ExtractNvmeLog(ReadOnlySpan<byte> response) => ExtractLog(response, 3, 2, 0);

    internal static ReadOnlySpan<byte> ExtractLog(ReadOnlySpan<byte> response, uint protocol, uint address, uint page)
    {
        if (response.Length < 48)
            return [];
        var fields = MemoryMarshal.Cast<byte, uint>(response[..48]);
        var offset = fields[6];
        if (fields[0] != 48 || fields[1] < 48 || fields[2] != protocol || fields[3] != 2 || fields[4] != address || fields[5] != page ||
            offset < 40 || fields[7] < SectorLength || offset > response.Length - 8 - SectorLength)
            return [];
        return response.Slice(8 + (int)offset, SectorLength);
    }

    // ACS-4 SCT status formats 2/3 encode current temperature as a signed byte at offset 200.
    internal static int? ParseSctTemperature(ReadOnlySpan<byte> log)
    {
        if (log.Length != SectorLength || BinaryPrimitives.ReadUInt16LittleEndian(log) is not (2 or 3)) return null;
        var value = unchecked((sbyte)log[200]);
        return WindowsDiskTemperatures.ValidTemperature(value) ? value : null;
    }

    // ATA Device Statistics requires a supported and valid statistic in the requested page.
    internal static byte? ParseAtaEndurance(ReadOnlySpan<byte> log) => (byte?)ReadAtaStatistic(log, 7, 8, 1);

    internal static int? RemainingEndurance(byte? used) => used.HasValue ? Math.Max(0, 100 - used.Value) : null;

    private static ulong? ReadAtaStatistic(ReadOnlySpan<byte> log, byte page, int offset, int bytes)
    {
        if (log.Length != SectorLength || BinaryPrimitives.ReadUInt16LittleEndian(log) != 1 || log[2] != page) return null;
        var stat = BinaryPrimitives.ReadUInt64LittleEndian(log[offset..]);
        return (stat & 0xC000000000000000UL) == 0xC000000000000000UL ? stat & ((1UL << (bytes * 8)) - 1) : null;
    }

    internal static DiskHealthSample ParseNvme(ReadOnlySpan<byte> log)
    {
        if (log.Length < SectorLength || !log[..SectorLength].ContainsAnyExcept((byte)0) ||
            !log[..SectorLength].ContainsAnyExcept(byte.MaxValue))
            return Unknown(ReadingState.NoReading);

        var warnings = log[0];
        var spare = log[3];
        var threshold = log[4];
        var wear = log[5];
        var kelvin = BinaryPrimitives.ReadUInt16LittleEndian(log[1..]);
        int? celsius = kelvin is >= 223 and <= 423 ? kelvin - 273 : null;
        var mediaErrors = ReadCounter(log, 160);
        var spareValid = spare <= 100 && threshold <= 100;
        var health = (warnings & 0x3F) != 0 ? DiskHealthState.Critical
            : mediaErrors > 0 || spareValid && spare < threshold || wear >= 100 ? DiskHealthState.Attention
            : warnings == 0 && spareValid ? DiskHealthState.Healthy : DiskHealthState.Unknown;
        List<SmartMetric> metrics =
        [
            new("NvmeWarning", $"0x{warnings:X2}", null,
                (warnings & 0x3F) != 0 ? DiskHealthState.Critical : warnings == 0 ? DiskHealthState.Healthy : DiskHealthState.Unknown),
            new("Temperature", celsius is { } temp ? $"{temp} °C" : "—"),
            new("Spare", spare <= 100 ? $"{spare}%" : "—", null,
                spareValid && spare < threshold ? DiskHealthState.Attention : DiskHealthState.Unknown),
            new("SpareThreshold", threshold <= 100 ? $"{threshold}%" : "—"),
            new("Wear", $"{wear}%", null, wear >= 100 ? DiskHealthState.Attention : DiskHealthState.Unknown),
            new("DataRead", DataBytes(log, 32)),
            new("DataWritten", DataBytes(log, 48)),
            new("PowerCycles", Format(ReadCounter(log, 112))),
            new("PowerHours", $"{Format(ReadCounter(log, 128))} h"),
            new("UnsafeShutdowns", Format(ReadCounter(log, 144))),
            new("MediaErrors", Format(mediaErrors), null, mediaErrors > 0 ? DiskHealthState.Attention : DiskHealthState.Unknown),
            new("ErrorLog", Format(ReadCounter(log, 176))),
        ];
        return new(ReadingState.Available, health, NvmeSource, metrics, celsius, RemainingEndurance(wear))
        {
            TemperatureSource = NvmeSource,
            TemperatureState = celsius.HasValue ? ReadingState.Available : ReadingState.NoReading,
            WearSource = NvmeSource,
            WearState = ReadingState.Available
        };
    }

    internal static DiskHealthSample ParseAta(ReadOnlySpan<byte> data, bool? predictsFailure, bool mechanical, int? celsius)
    {
        var metrics = new List<SmartMetric>();
        var evidence = false;
        var attention = false;
        if (HasAtaChecksum(data))
        {
            var attributes = new HashSet<byte>();
            for (var index = 0; index < 30; index++)
            {
                var entry = data.Slice(2 + index * 12, 12);
                var id = entry[0];
                if (id == 0 || !attributes.Add(id) || entry[3] is < 1 or > 253)
                    continue;
                evidence = true;
                var raw = ReadAtaRaw(entry.Slice(5, 6));
                var label = id switch
                {
                    5 when mechanical => "Reallocated",
                    9 => "PowerHoursAta",
                    12 => "PowerCycles",
                    197 when mechanical => "Pending",
                    198 when mechanical => "Uncorrectable",
                    _ => $"Ata.{id:X2}"
                };
                var sectorCount = label is "Reallocated" or "Pending" or "Uncorrectable";
                // The high bytes of these vendor-specific counters may contain additional fields.
                var confirmedCounter = sectorCount && raw <= ushort.MaxValue;
                var flagged = confirmedCounter && raw > 0;
                attention |= flagged;
                var interpreted = confirmedCounter || id == 12 && raw <= uint.MaxValue ? Format(raw) : $"0x{raw:X12}";
                metrics.Add(new(label, interpreted, entry[3].ToString(CultureInfo.InvariantCulture),
                    flagged ? DiskHealthState.Attention : DiskHealthState.Unknown, confirmedCounter ? raw : null, AtaSource));
            }
        }
        var health = predictsFailure == true ? DiskHealthState.Critical : attention ? DiskHealthState.Attention
            : evidence && predictsFailure == false ? DiskHealthState.Healthy : DiskHealthState.Unknown;
        if (predictsFailure.HasValue)
            metrics.Insert(0, new("AtaStatus", predictsFailure.Value ? "1" : "0", null,
                predictsFailure.Value ? DiskHealthState.Critical : DiskHealthState.Unknown, Source: AtaSource));
        return new(ReadingState.Available, health, AtaSource, metrics, celsius);
    }

    private static bool HasAtaChecksum(ReadOnlySpan<byte> data)
    {
        if (data.Length < SectorLength || BinaryPrimitives.ReadUInt16LittleEndian(data) is 0 or ushort.MaxValue)
            return false;
        byte sum = 0;
        foreach (var value in data[..SectorLength])
            sum = unchecked((byte)(sum + value));
        return sum == 0;
    }

    private static ulong ReadAtaRaw(ReadOnlySpan<byte> raw)
    {
        ulong value = 0;
        for (var i = raw.Length - 1; i >= 0; i--)
            value = (value << 8) | raw[i];
        return value;
    }

    private static UInt128 ReadCounter(ReadOnlySpan<byte> log, int offset) =>
        BinaryPrimitives.ReadUInt128LittleEndian(log[offset..]);

    private static string DataBytes(ReadOnlySpan<byte> log, int offset)
    {
        var units = ReadCounter(log, offset);
        return units <= ulong.MaxValue / 512_000
            ? HardwareReading.FormatBytes((ulong)units * 512_000) : $"{Format(units)} × 512000 B";
    }

    private static string Format(UInt128 value) => value.ToString("N0", Localization.LocalizationService.Current.Culture);

    private static DiskHealthSample Unknown(ReadingState state, int? temperature = null) =>
        new(state, DiskHealthState.Unknown, "", [], temperature) { TemperatureState = state, WearState = state };
}
