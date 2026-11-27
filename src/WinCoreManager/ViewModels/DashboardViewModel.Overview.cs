using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.ViewModels;

/// <summary>Medidor do painel. <see cref="Percent"/> = NaN quando não há leitura: nunca um número inventado.</summary>
public sealed record DashboardGauge(string Label, string IconKey, double Percent, string Value, string Detail);

// Visão geral: reaproveita a sessão de Sensores e o inventário de Meu Computador, sem coletores próprios.
public partial class DashboardViewModel
{
    public SensorPanelViewModel? Sensors { get; private set; }
    public HardwareViewModel? Hardware { get; private set; }

    internal Action<string>? OpenPageRequested { get; set; }

    internal Func<(long Total, long Free, string Root)?> SystemVolume { get; set; } = ReadSystemVolume;

    [ObservableProperty] private IReadOnlyList<DashboardGauge> _gauges = [];

    internal void Attach(SensorPanelViewModel sensors, HardwareViewModel hardware)
    {
        Sensors = sensors;
        Hardware = hardware;
        sensors.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SensorPanelViewModel.Groups)) UpdateGauges();
        };
        UpdateGauges();
    }

    /// <summary>Lê o inventário uma única vez; Meu Computador usa o mesmo resultado.</summary>
    internal void EnsureInventory()
    {
        if (Hardware is { IsReading: false } hardware && hardware.Sections.Count == 0 && hardware.RefreshCommand.CanExecute(null))
            hardware.RefreshCommand.Execute(null);
    }

    [RelayCommand]
    private void OpenPage(string page) => OpenPageRequested?.Invoke(page);

    private void UpdateGauges()
    {
        var groups = Sensors?.Groups ?? [];
        var cpu = groups.FirstOrDefault(group => group.Id == "cpu/system");
        var package = groups.Where(group => group.Id.StartsWith("cpu/temperature", StringComparison.Ordinal))
            .SelectMany(group => group.Readings).FirstOrDefault(reading => reading.Unit == "°C");
        var ram = groups.FirstOrDefault(group => group.Id == "ram");
        var graphics = groups.Where(group => group.Id != "cpu/system" && group.Id != "ram"
            && !group.Id.StartsWith("cpu/", StringComparison.Ordinal) && !group.Id.StartsWith("disk/", StringComparison.Ordinal)).ToArray();
        var gpu = graphics.FirstOrDefault(group => group.Readings.Any(reading => reading.Percentage.HasValue)) ?? graphics.FirstOrDefault();
        var gpuTemperature = gpu?.Readings.FirstOrDefault(reading => reading.Unit == "°C");

        Gauges =
        [
            Gauge("CPU", "Icon.Cpu", Load(cpu), Temperature(package)),
            Gauge("RAM", "Icon.Ram", Load(ram), ram?.Readings.FirstOrDefault(reading => reading.Unit == "B") is { Value: ulong free }
                ? LocalizationService.Source("Dashboard.Free", HardwareReading.FormatBytes(free, LocalizationService.SourceCulture)) : ""),
            Gauge("GPU", "Icon.Gpu", Load(gpu), Temperature(gpuTemperature)),
            DiskGauge(),
        ];

        // Rotulado para "Acesso restrito" não parecer referir-se ao uso mostrado no medidor.
        static string Temperature(HardwareReading? reading) =>
            reading is null ? "" : LocalizationService.Source("Dashboard.Temperature", reading.Display);

        static double Load(SensorGroup? group) =>
            group?.Readings.FirstOrDefault(reading => reading.Percentage.HasValue)?.Percentage ?? double.NaN;
    }

    private static DashboardGauge Gauge(string label, string icon, double percent, string detail) =>
        new(label, icon, percent, double.IsNaN(percent) ? "—" : string.Create(LocalizationService.Current.Culture, $"{percent:0}%"),
            detail.Length == 0 && double.IsNaN(percent) ? LocalizationService.Source("Message.NoReading") : detail);

    // Espaço ocupado do volume do Windows; não confundir com a vida útil do SSD.
    private DashboardGauge DiskGauge()
    {
        var volume = SystemVolume();
        if (volume is not { Total: > 0 } info)
            return Gauge(LocalizationService.Source("Dashboard.Disk"), "Icon.Disk", double.NaN, "");
        var used = info.Total - info.Free;
        return Gauge(LocalizationService.Source("Dashboard.DiskUsed", info.Root), "Icon.Disk", 100d * used / info.Total,
            LocalizationService.Source("Dashboard.DiskSpace", Size(used), Size(info.Total)));

        static string Size(long bytes) => HardwareReading.FormatBytes((ulong)bytes, LocalizationService.SourceCulture);
    }

    private static (long, long, string)? ReadSystemVolume()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
            return (drive.TotalSize, drive.TotalFreeSpace, drive.Name.TrimEnd('\\'));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
