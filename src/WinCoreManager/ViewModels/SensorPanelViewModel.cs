using WinCoreManager.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.ViewModels;

public sealed partial class SensorPanelViewModel(ISensorScheduler scheduler) : ViewModelBase
{
    private bool _active;
    private bool _closed;
    private bool _pausedByUser;
    private CancellationTokenSource? _monitor;
    private Task _running = Task.CompletedTask;

    [ObservableProperty] private IReadOnlyList<SensorGroup> _groups = [];
    [ObservableProperty] private IReadOnlyList<SensorNotice> _notices = [];
    [ObservableProperty] private string _status = LocalizationService.Source("Message.WaitingForTheFirstReading");
    [ObservableProperty] private string _lastUpdate = LocalizationService.Source("Message.LastUpdated");
    [ObservableProperty] private string _pauseLabel = LocalizationService.Source("Message.Pause");

    public void SetActive(bool active)
    {
        if (_closed || _active == active) return;
        _active = active;
        ApplyState();
    }

    public Task CloseAsync()
    {
        _closed = true;
        _monitor?.Cancel();
        return _running;
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_closed) return;
        _pausedByUser = !_pausedByUser;
        PauseLabel = _pausedByUser ? LocalizationService.Source("Message.Resume") : LocalizationService.Source("Message.Pause");
        ApplyState();
    }

    private void ApplyState()
    {
        if (_pausedByUser || !_active)
        {
            _monitor?.Cancel();
            Status = _pausedByUser ? LocalizationService.Source("Message.MonitoringPaused") : LocalizationService.Source("Message.MonitoringPausedWhileTheWindowIsInactive");
            return;
        }
        if (_monitor is { IsCancellationRequested: false }) return;
        var previous = _running;
        var monitor = new CancellationTokenSource();
        _monitor = monitor;
        Status = LocalizationService.Source("Message.WaitingForAReading");
        _running = MonitorAsync(previous, monitor);
    }

    private async Task MonitorAsync(Task previous, CancellationTokenSource monitor)
    {
        var token = monitor.Token;
        try
        {
            await previous;
            token.ThrowIfCancellationRequested();
            await foreach (var snapshot in scheduler.ObserveAsync(token))
            {
                if (token.IsCancellationRequested) break;
                Groups = snapshot.Groups;
                Notices = snapshot.Notices;
                LastUpdate = LocalizationService.Source("Message.LastUpdated0HHMmSs", snapshot.UpdatedAt.ToLocalTime());
                Status = Groups.Count == 0 ? LocalizationService.Source("Message.NoReadingsAreAvailableInThisSession") : "";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!token.IsCancellationRequested) Status = LocalizationService.Source("Message.CouldNotStartOrRefreshMonitoringSelectPause");
        }
        finally
        {
            if (ReferenceEquals(_monitor, monitor)) _monitor = null;
            monitor.Dispose();
        }
    }
}
