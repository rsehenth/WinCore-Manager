using WinCoreManager.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.ViewModels;

public sealed partial class HardwareViewModel : ViewModelBase
{
    private readonly IHardwareInventory _inventory;
    private readonly ISensorScheduler _scheduler;

    public HardwareViewModel(IHardwareInventory inventory, ISensorScheduler scheduler)
    {
        _inventory = inventory;
        _scheduler = scheduler;
        Title = LocalizationService.Source("Message.MyComputer");
    }

    [ObservableProperty] private IReadOnlyList<HardwareSection> _sections = [];
    [ObservableProperty] private IReadOnlyList<HardwareSection> _summaryCards = [];
    [ObservableProperty] private string? _windowsSummary;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isReading;

    public SensorPanelViewModel CreateSensorPanel() => new(_scheduler);

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RefreshAsync(CancellationToken token)
    {
        IsReading = true;
        Status = LocalizationService.Source("Message.ReadingWindowsAndHardwareInformation");
        try
        {
            var sections = await _inventory.ReadAsync(token);
            token.ThrowIfCancellationRequested();
            Sections = sections;
            string[] summaries = ["Message.Processor", "Message.GraphicsCard", "Message.MemoryRAM", "Message.Storage"];
            var summaryTitles = summaries.Select(LocalizationService.Source).ToHashSet(StringComparer.Ordinal);
            SummaryCards = sections.Where(section => summaryTitles.Contains(section.Title)).ToArray();
            var windows = sections.FirstOrDefault(s => s.Title == LocalizationService.Source("Message.OperatingSystem"));
            var activation = windows?.Devices.FirstOrDefault()?.Fields.FirstOrDefault(f => f.Label == LocalizationService.Source("Message.Activation"));
            WindowsSummary = activation is null ? windows?.Summary : $"{windows!.Summary} · {activation.Display}";
            Status = "";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Status = LocalizationService.Source("Message.ScanCanceled"); }
        catch (Exception e) when (e is not OutOfMemoryException) { Status = LocalizationService.Source("Message.TheInventoryScanWasInterrupted"); }
        finally { IsReading = false; }
    }
}
