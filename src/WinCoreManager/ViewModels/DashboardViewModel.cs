using WinCoreManager.Localization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IWinGetService _winGet;
    private readonly IHistoryService _history;
    private readonly UpdatesViewModel _updates;
    private readonly MaintenanceViewModel _maintenance;
    private readonly HistoryViewModel _historyPage;
    private bool _detectingWinget;

    public DashboardViewModel(
        IWinGetService winGet,
        IHistoryService history,
        UpdatesViewModel updates,
        MaintenanceViewModel maintenance,
        HistoryViewModel historyPage,
        WindowsActivationViewModel activation)
    {
        Title = LocalizationService.Source("Message.Dashboard");
        _winGet = winGet;
        _history = history;
        _updates = updates;
        _maintenance = maintenance;
        _historyPage = historyPage;
        Activation = activation;
    }

    public event Action<ViewModelBase>? NavigateRequested;
    public WindowsActivationViewModel Activation { get; }

    public ObservableCollection<HistoryRecord> RecentItems { get; } = new();

    public bool HasRecentItems => RecentItems.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecentItems))]
    private string _wingetStatusText = LocalizationService.Source("Message.Checking");

    [ObservableProperty]
    private string _updatesSummaryText = LocalizationService.Source("Message.Loading");

    [ObservableProperty]
    private string _maintenanceStatusText = LocalizationService.Source("Message.NoOperationInProgress");

    public void Refresh()
    {
        MaintenanceStatusText = _maintenance.IsRunning
            ? LocalizationService.Source("Message.OperationInProgress0", _maintenance.CurrentOperation)
            : LocalizationService.Source("Message.NoMaintenanceOperationInProgress");

        var detection = _winGet.LastDetection;
        if (detection is null)
        {
            WingetStatusText = LocalizationService.Source("Message.CheckingWinGet");
            if (!_detectingWinget)
            {
                _ = DetectWingetAsync();
            }
        }
        else
        {
            WingetStatusText = detection.IsInstalled
                ? LocalizationService.Source("Message.WinGetDetected0", detection.Version)
                : detection.Error ?? LocalizationService.Source("Message.WinGetNotDetected");
        }

        UpdatesSummaryText = _updates.LastScanTime is null
            ? LocalizationService.Source("Message.AnUpdateCheckHasYetToRun")
            : _updates.ScanFailed
                ? LocalizationService.Source("Message.LastWinGetCheckFailed0HHMmSs", _updates.LastScanTime.Value)
                : _updates.AvailableCount == 0
                ? LocalizationService.Source("Message.NoUpdatesFoundLastChecked0HHMm", _updates.LastScanTime.Value)
                : LocalizationService.Source("Message.AvailableUpdates0LastChecked1HHMm", _updates.AvailableCount, _updates.LastScanTime.Value);

        _ = LoadRecentAsync();
    }

    [RelayCommand]
    private void GoToUpdates() => NavigateRequested?.Invoke(_updates);

    [RelayCommand]
    private void GoToMaintenance() => NavigateRequested?.Invoke(_maintenance);

    [RelayCommand]
    private void GoToHistory() => NavigateRequested?.Invoke(_historyPage);

    private async Task DetectWingetAsync()
    {
        _detectingWinget = true;
        try
        {
            var detection = await _winGet.DetectAsync().ConfigureAwait(true);
            WingetStatusText = detection.IsInstalled
                ? LocalizationService.Source("Message.WinGetDetected0", detection.Version)
                : detection.Error ?? LocalizationService.Source("Message.WinGetNotDetected");
        }
        catch
        {
            WingetStatusText = LocalizationService.Source("Message.CouldNotCheckWinGet");
        }
        finally
        {
            _detectingWinget = false;
        }
    }

    private async Task LoadRecentAsync()
    {
        try
        {
            var recent = await _history.RecentAsync(5).ConfigureAwait(true);
            RecentItems.Clear();
            foreach (var record in recent)
            {
                RecentItems.Add(record);
            }

            OnPropertyChanged(nameof(HasRecentItems));
        }
        catch
        {
            // Sem histórico carregado, o painel mostra o estado vazio.
        }
    }
}
