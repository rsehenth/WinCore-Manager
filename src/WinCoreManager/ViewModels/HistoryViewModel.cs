using WinCoreManager.Localization;
using WinCoreManager.Views;
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

public sealed record HistoryTypeOption(string Label, HistoryType? Value);

public sealed record HistoryStatusOption(string Label, HistoryStatus? Value);

public partial class HistoryViewModel : ViewModelBase
{
    private readonly IHistoryService _history;
    private bool _reloadRequested;

    public HistoryViewModel(IHistoryService history)
    {
        Title = LocalizationService.Source("Message.History");
        _history = history;
    }

    public ObservableCollection<HistoryRecord> Records { get; } = new();

    public IReadOnlyList<HistoryTypeOption> TypeOptions { get; } = new List<HistoryTypeOption>
    {
        new(LocalizationService.Source("Message.AllTypes"), null),
        new(LocalizationService.Source("Message.Updates"), HistoryType.Update),
        new(LocalizationService.Source("Message.Maintenance"), HistoryType.Maintenance),
        new(LocalizationService.Source("Message.Uninstalls"), HistoryType.Uninstall),
        new(LocalizationService.Source("Message.LeftoverCleanup"), HistoryType.ResidueCleanup),
        new(LocalizationService.Source("Message.Startup"), HistoryType.Startup),
        new(LocalizationService.Source("Message.Cleanup"), HistoryType.Cleanup),
        new(LocalizationService.Source("Message.Tweaks"), HistoryType.Tweak),
    };

    public IReadOnlyList<HistoryStatusOption> StatusOptions { get; } = new List<HistoryStatusOption>
    {
        new(LocalizationService.Source("Message.AllResults"), null),
        new(LocalizationService.Source("Message.Success"), HistoryStatus.Success),
        new(LocalizationService.Source("Message.Failed"), HistoryStatus.Failure),
        new(LocalizationService.Source("Message.Canceled"), HistoryStatus.Cancelled),
        new(LocalizationService.Source("Message.Unsupported"), HistoryStatus.NotExecutable),
        new(LocalizationService.Source("UpdateOperation.Interrupted"), HistoryStatus.Interrupted),
    };

    [ObservableProperty]
    private HistoryTypeOption? _selectedType;

    [ObservableProperty]
    private HistoryStatusOption? _selectedStatus;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    partial void OnSelectedTypeChanged(HistoryTypeOption? value) => _ = ReloadAsync();

    partial void OnSelectedStatusChanged(HistoryStatusOption? value) => _ = ReloadAsync();

    partial void OnSearchTextChanged(string value) => _ = ReloadAsync();

    public void Reload() => _ = ReloadAsync();

    [RelayCommand]
    private async Task ReloadAsync()
    {
        _reloadRequested = true;
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            while (_reloadRequested)
            {
                _reloadRequested = false;
                var filter = new HistoryFilter
                {
                    Type = SelectedType?.Value,
                    Status = SelectedStatus?.Value,
                    Search = SearchText,
                    Limit = 500,
                };

                try
                {
                    var records = await _history.QueryAsync(filter).ConfigureAwait(true);
                    if (_reloadRequested)
                    {
                        continue;
                    }

                    Records.Clear();
                    foreach (var record in records)
                    {
                        Records.Add(record);
                    }

                    StatusText = LocalizationService.Source("Message.RecordsDisplayed0", Records.Count);
                }
                catch (HistoryStorageException ex)
                {
                    StatusText = ex.Message;
                }
                catch (Exception ex)
                {
                    StatusText = LocalizationService.Source("Message.CouldNotLoadHistory0", ex.Message);
                }
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!LocalizedDialog.Confirm(Application.Current?.MainWindow,
            LocalizationService.Source("Message.ClearHistory"),
            LocalizationService.Source("Message.PermanentlyRemoveAllHistoryRecords")))
        {
            return;
        }

        try
        {
            var removed = await _history.ClearAsync().ConfigureAwait(true);
            StatusText = LocalizationService.Source("Message.HistoryClearedRecordsRemoved0", removed);
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (HistoryStorageException ex)
        {
            StatusText = ex.Message;
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Source("Message.CouldNotClearHistory0", ex.Message);
        }
    }
}
