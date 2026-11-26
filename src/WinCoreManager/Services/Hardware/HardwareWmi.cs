using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;

namespace WinCoreManager.Services.Hardware;

internal sealed record WmiRow(string Path, IReadOnlyDictionary<string, object?> Values)
{
    public object? Get(string key) => Values.GetValueOrDefault(key);
    public string? Text(string key) => InventoryText.Clean(Get(key) as string);
    public ulong? Number(string key) => Get(key) switch
    {
        byte value => value, ushort value => value, uint value => value, ulong value => value,
        short value when value >= 0 => (ulong)value, int value when value >= 0 => (ulong)value,
        long value when value >= 0 => (ulong)value,
        string value when ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) => number,
        _ => null
    };
    public uint? UInt(string key) => Number(key) is <= uint.MaxValue and { } value ? (uint)value : null;
    public bool? Flag(string key) => Get(key) as bool?;
    public DateTime? Date(string key)
    {
        if (Get(key) is not string value || value.Length != 25 || value.Contains('*')) return null;
        try
        {
            var result = ManagementDateTimeConverter.ToDateTime(value);
            return result.Year >= 1980 && result <= DateTime.Now.AddDays(1) ? result : null;
        }
        catch (ArgumentException) { return null; }
    }
}

internal sealed record WmiResult(ReadingState State, IReadOnlyList<WmiRow> Rows);

internal interface IHardwareWmi
{
    WmiResult Read(string scope, string className, string columns, CancellationToken token, string? filter = null);
    WmiResult Associated(string scope, string path, string association, string resultClass, string columns, CancellationToken token);
}

internal sealed class HardwareWmi : IHardwareWmi
{
    public const string Cim = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string Monitor = @"root\wmi";
    private const int MaxRows = 512;

    public WmiResult Read(string scope, string className, string columns, CancellationToken token, string? filter = null)
        => Query(scope, $"SELECT {columns} FROM {className}{(filter is null ? "" : $" WHERE {filter}")}", columns, token);

    public WmiResult Associated(string scope, string path, string association, string resultClass, string columns, CancellationToken token)
    {
        // Paths come only from WMI. Reject delimiters that could escape the association expression.
        if (path.Length == 0 || path.IndexOfAny(['{', '}', '\r', '\n']) >= 0)
            return new(ReadingState.NoReading, []);
        return Query(scope, $"ASSOCIATORS OF {{{path}}} WHERE AssocClass={association} ResultClass={resultClass}", columns, token);
    }

    internal static string? RelativePath(object? reference)
    {
        if (reference is not string text) return null;
        try { return new ManagementPath(text).RelativePath; }
        catch (Exception error) when (error is ArgumentException or ManagementException or COMException) { return null; }
    }

    private static WmiResult Query(string scope, string query, string columns, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var properties = columns.Split(',', StringSplitOptions.TrimEntries);
        try
        {
            var options = new EnumerationOptions { ReturnImmediately = true, Rewindable = false, Timeout = TimeSpan.FromSeconds(4) };
            using var searcher = new ManagementObjectSearcher(scope, query, options);
            using var result = searcher.Get();
            var rows = new List<WmiRow>();
            foreach (ManagementObject item in result)
            {
                using (item)
                {
                    token.ThrowIfCancellationRequested();
                    var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var key in properties)
                        values[key] = item.Properties[key]?.Value;
                    rows.Add(new(item.Path.RelativePath ?? "", values));
                    if (rows.Count == MaxRows) break;
                }
            }
            return new(rows.Count > 0 ? ReadingState.Available : ReadingState.NoReading, rows);
        }
        catch (ManagementException e)
        {
            return new(e.ErrorCode switch
            {
                ManagementStatus.AccessDenied => ReadingState.Restricted,
                ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace => ReadingState.Unsupported,
                _ => ReadingState.Error
            }, []);
        }
        catch (UnauthorizedAccessException) { return new(ReadingState.Restricted, []); }
        catch (COMException e) { return new(e.HResult == unchecked((int)0x80070005) ? ReadingState.Restricted : ReadingState.Error, []); }
    }
}
