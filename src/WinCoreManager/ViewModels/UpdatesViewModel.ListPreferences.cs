using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;

namespace WinCoreManager.ViewModels;

public partial class UpdatesViewModel
{
    private readonly HashSet<(string Source, string Id)> _hiddenKeys = [];

    public IReadOnlyList<HiddenUpdate> HiddenUpdates { get; private set; } = [];
    internal double ListRatio { get; private set; } = 0.76;

    public string UpdateListSummary
    {
        get
        {
            var visible = VisibleSelectableRows.Count();
            var unverified = Rows.Count(row => IsVisible(row) && !row.CanSelect && row.VersionWarning is not null);
            var hidden = HiddenUpdates.Count;
            return unverified == 0
                ? LocalizationService.Source(hidden == 1 ? "Message.UpdatesList.SummaryOneHidden" : "Message.UpdatesList.Summary", visible, hidden)
                : LocalizationService.Source("UpdatesList.SummaryWithUnknown", visible, unverified, hidden);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHiddenEmptyState))]
    private bool _managingHiddenUpdates;

    [ObservableProperty]
    private string _listPreferenceMessage = string.Empty;

    public bool ShowHiddenEmptyState => ManagingHiddenUpdates && HiddenUpdates.Count == 0;
    public bool HasUpdateControls => IsScanning || HasOperation;
    public bool ShowUpdateDetails => HasUpdateControls || ShowLog;

    partial void OnIsUpdatingChanged(bool value)
    {
        if (!value && ListPreferenceMessage == LocalizationService.Source("Message.UpdatesList.HideBlocked"))
            ListPreferenceMessage = string.Empty;
    }

    private void LoadListPreferences(AppSettings settings)
    {
        if (settings.UpdatesListRatio is { } ratio && double.IsFinite(ratio) && ratio > 0 && ratio < 1)
            ListRatio = ratio;
        SetHiddenUpdates(settings.HiddenUpdates);
    }

    [RelayCommand]
    private void ToggleHiddenUpdates()
    {
        ListPreferenceMessage = string.Empty;
        ManagingHiddenUpdates = !ManagingHiddenUpdates;
    }

    [RelayCommand]
    private void HideUpdate(UpdateItemRow? row)
    {
        if (row is null || !Rows.Contains(row) || IsHidden(row)) return;
        if (IsUpdating)
        {
            ListPreferenceMessage = LocalizationService.Source("Message.UpdatesList.HideBlocked");
            return;
        }
        if (string.IsNullOrWhiteSpace(row.Id) || string.IsNullOrWhiteSpace(row.Source))
        {
            ListPreferenceMessage = LocalizationService.Source("Message.UpdatesList.HideMissingIdentity");
            return;
        }
        SaveHiddenUpdates([.. HiddenUpdates, new(row.Name, row.Id, row.Source)]);
    }

    [RelayCommand(CanExecute = nameof(CanShowUpdate))]
    private void ShowUpdate(HiddenUpdate? entry)
    {
        if (!CanShowUpdate(entry)) return;
        SaveHiddenUpdates(HiddenUpdates.Where(item => (item.Source, item.Id) != (entry!.Source, entry.Id)).ToList());
    }

    private bool CanShowUpdate(HiddenUpdate? entry) =>
        entry is not null && _hiddenKeys.Contains((entry.Source, entry.Id));

    private bool IsHidden(UpdateItemRow row) => _hiddenKeys.Contains((row.Source, row.Id));

    private void SetHiddenUpdates(IReadOnlyList<HiddenUpdate> entries)
    {
        HiddenUpdates = entries.ToArray();
        _hiddenKeys.Clear();
        foreach (var entry in entries) _hiddenKeys.Add((entry.Source, entry.Id));
        OnPropertyChanged(nameof(HiddenUpdates));
        OnPropertyChanged(nameof(ShowHiddenEmptyState));
        ShowUpdateCommand.NotifyCanExecuteChanged();
        ApplyFilter();
    }

    private void SaveHiddenUpdates(List<HiddenUpdate> entries)
    {
        if (_settings is null)
        {
            ListPreferenceMessage = LocalizationService.Source("Message.UpdatesList.HiddenSaveFailed");
            return;
        }

        var previous = _settings.Settings.HiddenUpdates;
        _settings.Settings.HiddenUpdates = entries;
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.Settings.HiddenUpdates = previous;
            ListPreferenceMessage = LocalizationService.Source("Message.UpdatesList.HiddenSaveFailed");
            return;
        }

        ListPreferenceMessage = string.Empty;
        SetHiddenUpdates(entries);
    }

    internal void SaveListRatio(double ratio)
    {
        if (!double.IsFinite(ratio) || ratio <= 0 || ratio >= 1 || Math.Abs(ListRatio - ratio) < 0.0001) return;
        ListRatio = ratio;
        if (_settings is null) return;

        var previous = _settings.Settings.UpdatesListRatio;
        _settings.Settings.UpdatesListRatio = ratio;
        try
        {
            _settings.Save();
            ListPreferenceMessage = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.Settings.UpdatesListRatio = previous;
            ListPreferenceMessage = LocalizationService.Source("Message.UpdatesList.RatioSaveFailed");
        }
    }
}
