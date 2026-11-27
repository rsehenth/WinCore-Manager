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
    private const uint PredictFailure = 0x002D1100;
    private const int SectorLength = 512;

    public DiskHealthSample Read(uint number, string? privateSerial, uint? bus, bool mechanical)
    {
        if (privateSerial is null)
            return Unknown(ReadingState.NoReading);
        using var disk = WindowsDiskTemperatures.OpenQueryHandle(number);
        if (disk.IsInvalid)
            return Unknown(WindowsDiskTemperatures.Error(Marshal.GetLastPInvokeError()));

        var identity = WindowsDiskTemperatures.VerifyIdentity(disk, privateSerial);
        if (identity != ReadingState.Available)
            return Unknown(identity);

        Span<byte> response = stackalloc byte[8 + 40 + SectorLength];
        if (bus != 17)
        {
            var temperature = ReadTemperature(disk, response);
            fixed (byte* output = response)
            {
                if (!WindowsDiskTemperatures.DeviceIoControl(disk, PredictFailure, null, 0, output,
                    (uint)response.Length, out var written, 0))
                    return Unknown(WindowsDiskTemperatures.Error(Marshal.GetLastPInvokeError()), temperature);
                if (written < 4)
                    return Unknown(ReadingState.Error, temperature);

                var status = BinaryPrimitives.ReadUInt32LittleEndian(response);
                var ata = (bus is 2 or 3 or 7 or 11) && written >= 4 + SectorLength
                    ? response.Slice(4, SectorLength) : ReadOnlySpan<byte>.Empty;
                return ParseAta(ata, status != 0, mechanical, temperature);
            }
        }

        var queryState = ReadingState.Unsupported;
        // Device first, then adapter, with exactly one read-only log page request per property.
        foreach (var property in new uint[] { 50, 49 })
        {
            response.Clear();
            Span<uint> request = MemoryMarshal.Cast<byte, uint>(response);
            request[0] = property;
            request[2] = 3; // ProtocolTypeNvme
            request[3] = 2; // NVMeDataTypeLogPage
            request[4] = 2; // SMART / Health Information
            request[6] = 40; // ProtocolDataOffset, relative to ProtocolSpecificData
            request[7] = SectorLength;
            fixed (byte* buffer = response)
            {
                if (!WindowsDiskTemperatures.DeviceIoControl(disk, 0x002D1400, buffer, (uint)response.Length,
                    buffer, (uint)response.Length, out var written, 0))
                {
                    queryState = WindowsDiskTemperatures.Error(Marshal.GetLastPInvokeError());
                    continue;
                }
                if (written <= response.Length && ExtractNvmeLog(response[..(int)written]) is { Length: SectorLength } log)
                    return ParseNvme(log);
                queryState = ReadingState.Error;
            }
        }
        return Unknown(queryState, ReadTemperature(disk, response));
    }

    private static short? ReadTemperature(SafeFileHandle disk, Span<byte> buffer)
    {
        if (WindowsDiskTemperatures.Query(disk, 52, buffer, out var length) == ReadingState.Available)
        {
            var temperatures = WindowsDiskTemperatures.ParseTemperatures(buffer[..length]).Values;
            if (temperatures.FirstOrDefault(value => value.Index == 0) is var primary
                && temperatures.Any(value => value.Index == 0) && primary.Celsius is >= -50 and <= 150)
                return primary.Celsius;
        }
        return null;
    }

    internal static ReadOnlySpan<byte> ExtractNvmeLog(ReadOnlySpan<byte> response)
    {
        if (response.Length < 48)
            return [];
        var fields = MemoryMarshal.Cast<byte, uint>(response[..48]);
        var offset = fields[6];
        if (fields[0] != 48 || fields[1] < 48 || fields[2] != 3 || fields[3] != 2 || fields[4] != 2 ||
            offset < 40 || fields[7] < SectorLength || offset > response.Length - 8 - SectorLength)
            return [];
        return response.Slice(8 + (int)offset, SectorLength);
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
        return new(ReadingState.Available, health, NvmeSource, metrics, celsius, Math.Max(0, 100 - wear));
    }

    internal static DiskHealthSample ParseAta(ReadOnlySpan<byte> data, bool predictsFailure, bool mechanical, int? celsius)
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
                    flagged ? DiskHealthState.Attention : DiskHealthState.Unknown));
            }
        }
        var health = predictsFailure ? DiskHealthState.Critical : attention ? DiskHealthState.Attention
            : evidence ? DiskHealthState.Healthy : DiskHealthState.Unknown;
        metrics.Insert(0, new("AtaStatus", predictsFailure ? "1" : "0", null,
            predictsFailure ? DiskHealthState.Critical : DiskHealthState.Unknown));
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
        new(state, DiskHealthState.Unknown, "", [], temperature);
}
