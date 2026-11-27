using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.ViewModels;

internal sealed partial class DiskHealthViewModel(DiskHealthService service) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _status = LocalizationService.Source("DiskHealth.Loading");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle), nameof(IsEmpty), nameof(Captured))]
    private IReadOnlyList<DiskHealthRow> _disks = [];

    public bool IsEmpty => Disks.Count == 0;

    // Sem discos na tela, o estado aparece no bloco vazio e o subtítulo continua descritivo.
    public string Subtitle => !IsEmpty && Status is { } status ? status : LocalizationService.Source("DiskHealth.Subtitle");

    public string? Captured => Disks.Count == 0 || Disks[0].Device.Fields.FirstOrDefault() is not { } reading
        ? null : LocalizationService.Source("DiskHealth.Captured", reading.Timestamp.ToLocalTime());

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(true);

    internal async Task LoadAsync(bool refresh)
    {
        if (refresh || Disks.Count == 0)
            Status = LocalizationService.Source("DiskHealth.Loading");
        try
        {
            var report = await service.ReadAsync(refresh);
            Disks = report.Disks.Select(disk => new DiskHealthRow(disk)).ToArray();
            Status = Disks.Count != 0 ? null : report.State == ReadingState.NoReading
                ? LocalizationService.Source("DiskHealth.Empty") : HardwareReading.StateLabel(report.State);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            Status = LocalizationService.Source("DiskHealth.Timeout");
        }
    }
}

internal sealed record DiskFact(string Label, string Value, bool Flagged);

internal sealed class DiskHealthRow(PhysicalDiskHealth disk)
{
    // Contadores mostrados em Manutenção. A lista completa continua em "Mais detalhes".
    private static readonly string[] FactMetrics = ["PowerHours", "DataWritten", "MediaErrors", "Reallocated", "Pending"];

    internal HardwareDevice Device => disk.Device;
    internal DiskHealthSample Sample => disk.Sample;
    public string Model => disk.Device.Name;
    public DiskHealthState HealthState => Sample.Health;
    public string Health => StateLabel(HealthState);
    public string BadgeText => !ReadingAvailable && HealthState == DiskHealthState.Unknown
        ? LocalizationService.Source("DiskHealth.Badge.NoData") : Health;
    public string Temperature => Sample.Celsius is { } value ? $"{value} °C" : LocalizationService.Source("DiskHealth.Unavailable");
    public string Remaining => Sample.RemainingLife is { } value ? $"{value}%" : LocalizationService.Source("DiskHealth.Unavailable");
    public bool HasTemperature => Sample.Celsius.HasValue;
    public bool HasNoTemperature => !HasTemperature;
    public bool HasRemaining => Sample.RemainingLife.HasValue;
    public bool HasNoRemaining => !HasRemaining;
    public int RemainingValue => Sample.RemainingLife ?? 0;
    public string Caption => string.Join(" · ", new[] { Field("Message.Type"), Field("Message.Capacity") });
    public string? TypeLabel => Find("Message.Type")?.Value is string type && type is "NVMe" or "SSD SATA" or "SSD" or "HDD" ? type : null;
    public bool HasUnknownType => TypeLabel is null;
    public HardwareReading? Capacity => Find("Message.Capacity") is { State: ReadingState.Available } reading ? reading : null;
    public bool ReadingAvailable => Sample.State == ReadingState.Available;
    public string ReadState => ReadingAvailable
        ? Sample.Source : HardwareReading.StateLabel(Sample.State);
    public string? Source => ReadingAvailable ? Sample.Source : null;

    public string? Volumes
    {
        get
        {
            var letters = (Device.Children ?? []).SelectMany(part => part.Children ?? [])
                .Where(volume => volume.Name.StartsWith("Volume ", StringComparison.Ordinal))
                .Select(volume => volume.Name["Volume ".Length..]).Where(letter => letter.Length == 2)
                .Distinct().ToArray();
            return letters.Length == 0 ? null : LocalizationService.Source("DiskHealth.Volumes", string.Join(", ", letters));
        }
    }

    public string? Note => !ReadingAvailable ? ReadState
        : HealthState is DiskHealthState.Attention or DiskHealthState.Critical ? LocalizationService.Source("DiskHealth.Note.Backup")
        : HasNoRemaining ? LocalizationService.Source("DiskHealth.Note.NoWear")
        : null;
    public bool HasNote => Note is not null;

    public IReadOnlyList<DiskFact> Facts
    {
        get
        {
            var facts = new List<DiskFact>();
            // Só quando acrescenta algo à plaqueta de tipo (ex.: HDD em USB; não "NVMe" duas vezes).
            if (Find("Interface") is { State: ReadingState.Available } bus && TypeLabel?.Contains(bus.Display, StringComparison.Ordinal) != true)
                facts.Add(new(LocalizationService.Source("DiskHealth.Interface"), bus.Display, false));
            foreach (var key in FactMetrics)
            {
                // Valores ausentes ("—") e contadores ATA sem interpretação confirmada (hexadecimal) ficam só nos detalhes.
                if (Sample.Metrics.FirstOrDefault(metric => metric.Label == key) is { } metric &&
                    metric.Raw != "—" && !metric.Raw.StartsWith("0x", StringComparison.Ordinal))
                    facts.Add(new(LocalizationService.Source($"DiskHealth.Metric.{key}"), metric.Raw,
                        metric.Condition is DiskHealthState.Attention or DiskHealthState.Critical));
            }
            return facts;
        }
    }

    internal string Field(string key) => Find(key)?.Display ?? LocalizationService.Source("DiskHealth.Unavailable");

    private HardwareReading? Find(string key) => Device.Fields
        .FirstOrDefault(field => field.Label == LocalizationService.Source(key));

    internal static string StateLabel(DiskHealthState state) => LocalizationService.Source($"DiskHealth.State.{state}");
}
