using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public sealed record ThemeOption(AppTheme Theme, string Label);
public sealed record LanguageOption(string Code, string Label);

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settings;
    private readonly IHistoryService _history;
    private readonly IWinGetService _winGet;
    private readonly IThemeService? _theme;

    private bool _suppressSave;

    public SelfUpdateViewModel SelfUpdate { get; } = new();

    public SettingsViewModel(ISettingsService settings, IHistoryService history, IWinGetService winGet, IThemeService? theme = null)
    {
        Title = LocalizationService.Source("Message.Settings");
        _settings = settings;
        _history = history;
        _winGet = winGet;
        _theme = theme;

        _suppressSave = true;
        _selectedThemeOption = ThemeOptions.First(o => o.Theme == (theme?.Current ?? AppTheme.Light));
        _selectedLanguageOption = Languages.First(o => o.Code == LocalizationService.Current.CultureName);
        _portableMode = settings.Settings.PortableMode;
        _retentionDaysText = settings.Settings.HistoryRetentionDays.ToString();
        _winGetInactivitySeconds = Math.Clamp(settings.Settings.WinGetInactivitySeconds, 1, 600);
        WinGetInactivityOptions = new[] { 30, 60, 120, 300, 600 }.Append(_winGetInactivitySeconds).Distinct().Order().ToArray();
        _suppressSave = false;

        AppVersionText = SelfUpdate.InstalledVersion;
        DataDirectoryText = settings.DataDirectory;
        HistoryDatabaseText = settings.HistoryDatabasePath;

        if (theme is not null)
        {
            theme.ThemeChanged += OnThemeChanged;
        }

        _ = DetectWingetAsync();
    }

    private void OnThemeChanged(AppTheme applied)
    {
        _suppressSave = true;
        SelectedThemeOption = ThemeOptions.First(o => o.Theme == applied);
        _suppressSave = false;
    }

    public IReadOnlyList<ThemeOption> ThemeOptions { get; } =
    [
        new(AppTheme.Dark, LocalizationService.Source("Message.Dark")),
        new(AppTheme.Light, LocalizationService.Source("Message.Light")),
    ];

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(LocalizationService.Portuguese, "Português (Brasil)"),
        new(LocalizationService.English, "English (United States)"),
    ];

    public IReadOnlyList<int> WinGetInactivityOptions { get; }

    [ObservableProperty]
    private int _winGetInactivitySeconds;

    partial void OnWinGetInactivitySecondsChanged(int oldValue, int newValue)
    {
        if (_suppressSave || !WinGetInactivityOptions.Contains(newValue)) return;
        var previous = _settings.Settings.WinGetInactivitySeconds;
        _settings.Settings.WinGetInactivitySeconds = newValue;
        if (SaveWithNote(LocalizationService.Source("UpdateOperation.InactivitySaved"))) return;
        _settings.Settings.WinGetInactivitySeconds = previous;
        _suppressSave = true;
        WinGetInactivitySeconds = oldValue;
        _suppressSave = false;
    }

    [ObservableProperty]
    private LanguageOption _selectedLanguageOption;

    partial void OnSelectedLanguageOptionChanged(LanguageOption value)
    {
        if (_suppressSave || value is null)
        {
            return;
        }

        var saved = LocalizationService.Current.Select(_settings, value.Code);
        StatusNote = saved
            ? LocalizationService.Source("Message.LanguageApplied")
            : LocalizationService.Source("Message.LanguageSaveFailed");
    }

    [ObservableProperty]
    private ThemeOption _selectedThemeOption;

    [ObservableProperty]
    private bool _portableMode;

    [ObservableProperty]
    private string _retentionDaysText = "365";

    [ObservableProperty]
    private string _statusNote = string.Empty;

    [ObservableProperty]
    private string _wingetVersionText = LocalizationService.Source("Message.Checking");

    [ObservableProperty]
    private string _wingetPathText = "—";

    public string AppVersionText { get; }

    public string DataDirectoryText { get; }

    public string HistoryDatabaseText { get; }

    partial void OnSelectedThemeOptionChanged(ThemeOption value)
    {
        if (_suppressSave || _theme is null || value is null)
        {
            return;
        }

        StatusNote = _theme.Select(value.Theme)
            ? LocalizationService.Source("Message.ThemeChangedTo0", value.Label)
            : LocalizationService.Source("Message.TheThemeWasAppliedButThePreferenceCould");
    }

    partial void OnPortableModeChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _settings.Settings.PortableMode = value;
        if (!SaveWithNote(LocalizationService.Source("Message.StorageModeChangedItTakesEffectTheNext")))
        {
            _settings.Settings.PortableMode = !value;
            _suppressSave = true;
            PortableMode = !value;
            _suppressSave = false;
        }
    }

    partial void OnRetentionDaysTextChanged(string value)
    {
        if (_suppressSave)
        {
            return;
        }

        if (!int.TryParse(value?.Trim(), out var days))
        {
            StatusNote = LocalizationService.Source("Message.EnterANumberOfDays0TurnsOff");
            return;
        }

        var clamped = Math.Clamp(days, 0, 3650);
        var previous = _settings.Settings.HistoryRetentionDays;
        _settings.Settings.HistoryRetentionDays = clamped;
        if (!SaveWithNote(clamped == 0
            ? LocalizationService.Source("Message.AutomaticRemovalOfOldRecordsIsOff")
            : LocalizationService.Source("Message.OnStartupTheAppWillRemoveRecordsOlder", clamped)))
        {
            _settings.Settings.HistoryRetentionDays = previous;
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
            StatusNote = LocalizationService.Source("Message.HistoryClearedRecordsRemoved0", removed);
        }
        catch (Exception ex)
        {
            StatusNote = LocalizationService.Source("Message.CouldNotClearHistory0", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DetectWingetAsync()
    {
        try
        {
            var detection = await _winGet.DetectAsync().ConfigureAwait(true);
            WingetVersionText = detection.IsInstalled ? detection.Version ?? "—" : LocalizationService.Source("Message.NotDetected");
            WingetPathText = detection.ExecutablePath ?? "—";

            if (!detection.IsInstalled && detection.Error is not null)
            {
                StatusNote = detection.Error;
            }
        }
        catch
        {
            WingetVersionText = LocalizationService.Source("Message.CouldNotCheck");
        }
    }

    private bool SaveWithNote(string note)
    {
        try
        {
            _settings.Save();
            StatusNote = note;
            return true;
        }
        catch (Exception ex)
        {
            StatusNote = LocalizationService.Source("Message.CouldNotSaveSettings0", ex.Message);
            return false;
        }
    }
}
