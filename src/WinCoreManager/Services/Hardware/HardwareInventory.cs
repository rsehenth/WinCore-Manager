using System.Globalization;
using WinCoreManager.Localization;

namespace WinCoreManager.Services.Hardware;

public enum ReadingState { Available, NoReading, Unsupported, Restricted, Error, TimedOut }
public enum HardwareKind { Windows, Board, Cpu, Gpu, Ram, Disk, Network, Audio, Display, Battery }

public sealed record HardwareReading(string ComponentId, string Label, object? Value, string Source,
    ReadingState State, string Unit, DateTimeOffset Timestamp)
{
    public string? DisplayOverride { get; init; }
    public string? Reason { get; init; }
    internal bool LocalizeValue { get; init; }
    public string Display => Render(LocalizationService.Current.Culture);

    internal string Render(CultureInfo culture) => DisplayOverride is { } display
        ? PresentationText.Translate(display, culture)
        : State switch
    {
        ReadingState.Available when Unit == "B" && Value is ulong bytes => FormatBytes(bytes, culture),
        ReadingState.Available when Unit == "B/s" && Value is ulong rate => $"{FormatBytes(rate, culture)}/s",
        ReadingState.Available when Value is DateTime date => date.ToString("d", culture),
        ReadingState.Available when Value is TimeSpan time => string.Create(culture, $"{(int)time.TotalDays} d {time.Hours:D2} h {time.Minutes:D2} min"),
        ReadingState.Available when Value is double number => string.Create(culture, $"{number:0.##} {Unit}").TrimEnd(),
        ReadingState.Available when Value is not string => string.Create(culture, $"{Value} {Unit}").TrimEnd(),
        ReadingState.Available when LocalizeValue => PresentationText.Translate((string)Value!, culture),
        ReadingState.Available => $"{Value} {Unit}".TrimEnd(),
        _ => PresentationText.Translate(StateLabel(State), culture),
    };

    internal static string StateLabel(ReadingState state) => state switch
    {
        ReadingState.NoReading => LocalizationService.Source("Message.NoReading"),
        ReadingState.Unsupported => LocalizationService.Source("Message.Unsupported"),
        ReadingState.Restricted => LocalizationService.Source("Message.RestrictedAccess"),
        ReadingState.TimedOut => LocalizationService.Source("Sensors.ReadingTimedOut"),
        _ => LocalizationService.Source("Message.ReadingError"),
    };

    public double? Percentage => State == ReadingState.Available && Unit == "%" && Value is double value ? value : null;
    public string SampleSource => $"{Source} · {Timestamp.ToLocalTime():HH:mm:ss}";

    public static HardwareReading Create(string id, string label, object? value, string source,
        DateTimeOffset time, string unit = "", ReadingState absent = ReadingState.NoReading)
    {
        if (value is double number && (!double.IsFinite(number) || InvalidMeasurement(number, unit)))
            return new(id, label, null, source, ReadingState.Error, unit, time);
        return new(id, label, value, source, value is null ? absent : ReadingState.Available, unit, time);
    }

    private static bool InvalidMeasurement(double number, string unit) => unit switch
    {
        "%" => number is < 0 or > 100,
        "°C" => number < -273.15,
        "MHz" or "RPM" or "W" or "h" => number < 0,
        _ => false
    };

    public static string FormatBytes(ulong bytes) => FormatBytes(bytes, LocalizationService.Current.Culture);

    internal static string FormatBytes(ulong bytes, CultureInfo culture)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB", "EiB"];
        var value = (decimal)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return string.Create(culture, $"{value:0.##} {units[index]}");
    }
}

public sealed record HardwareDevice(string Id, HardwareKind Kind, string Name, IReadOnlyList<HardwareReading> Fields,
    IReadOnlyList<HardwareDevice>? Children = null)
{
    internal bool LocalizeName { get; init; }
}

public sealed record HardwareSection(string Title, string Summary, IReadOnlyList<HardwareDevice> Devices,
    ReadingState State = ReadingState.Available)
{
    public string? Status => State == ReadingState.Available ? null
        : HardwareReading.StateLabel(State);
}

internal interface IInventoryProvider { HardwareSection Read(CancellationToken token); }
internal interface IOperatingSystemProvider : IInventoryProvider;
internal interface ILicensingProvider { HardwareReading ReadActivation(string? edition, DateTimeOffset time, CancellationToken token); }
internal interface ICpuProvider : IInventoryProvider;
internal interface IGpuProvider : IInventoryProvider;
internal interface IBoardProvider : IInventoryProvider;
internal interface IRamProvider : IInventoryProvider;
internal interface IStorageProvider : IInventoryProvider;
internal interface IComplementaryProvider { IEnumerable<HardwareSection> Read(CancellationToken token); }

public interface IHardwareInventory
{
    Task<IReadOnlyList<HardwareSection>> ReadAsync(CancellationToken token);
}

internal sealed class HardwareInventory(
    IOperatingSystemProvider windows, IBoardProvider board, ICpuProvider cpu, IGpuProvider gpu,
    IRamProvider ram, IStorageProvider disks, IComplementaryProvider other) : IHardwareInventory
{
    public Task<IReadOnlyList<HardwareSection>> ReadAsync(CancellationToken token) => Task.Run<IReadOnlyList<HardwareSection>>(() =>
    {
        var sections = new List<HardwareSection>();
        foreach (var provider in new IInventoryProvider[] { windows, cpu, gpu, ram, disks, board })
        {
            token.ThrowIfCancellationRequested();
            sections.Add(provider.Read(token));
        }
        sections.AddRange(other.Read(token));
        return sections;
    }, token);
}

internal sealed class InventoryDevice(string id, HardwareKind kind, string name, DateTimeOffset time, bool localizeName = false)
{
    private readonly List<HardwareReading> _fields = [];

    public InventoryDevice Add(string label, object? value, string source, string unit = "", ReadingState absent = ReadingState.NoReading,
        bool localizeValue = false)
    {
        _fields.Add(HardwareReading.Create(id, label, value, source, time, unit, absent) with { LocalizeValue = localizeValue });
        return this;
    }

    public InventoryDevice Add(HardwareReading reading)
    {
        _fields.Add(reading);
        return this;
    }

    public HardwareDevice Build(IReadOnlyList<HardwareDevice>? children = null) =>
        new(id, kind, name, _fields.ToArray(), children) { LocalizeName = localizeName };
}

internal static class InventoryText
{
    private static readonly HashSet<string> Placeholders = new(StringComparer.OrdinalIgnoreCase)
    {
        "To Be Filled By O.E.M.", "To Be Filled By OEM", "Default string", "System Manufacturer",
        "System Product Name", "Base Board Product Name", "Base Board Manufacturer", "Not Specified",
        "Not Available", "Unknown", "None", "N/A", "OEM", "Intel Processor", "AMD Processor",
        "Intel Core i7", "Intel Core i5", "Intel Core i3", "Intel Core i9", "AMD Ryzen",
        "NVIDIA GPU", "AMD GPU", "Intel GPU", "GPU", "Disk drive", "Generic SSD", "SSD", "HDD"
    };

    public static string? Clean(string? value)
    {
        if (value is null || value.Length > 512) return null;
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim('\0', ' ');
        return text.Length == 0 || text.Any(char.IsControl) || Placeholders.Contains(text)
            || text.All(c => c is '0' or '-' or '.') ? null : text;
    }

    public static ulong? Positive(ulong? value) => value is > 0 ? value : null;
    public static string? Architecture(uint? value) => value switch
    {
        0 => "x86", 5 => "ARM", 6 => "IA-64", 9 => "x64", 12 => "ARM64", _ => null
    };

    public static HardwareSection Section(string title, IReadOnlyList<HardwareDevice> devices, ReadingState absent)
    {
        var names = string.Join(" • ", devices.Take(3).Select(d => d.Name));
        if (devices.Count > 3) names += $" • +{devices.Count - 3}";
        return new(title, names, devices, devices.Count > 0 ? ReadingState.Available : absent);
    }
}
