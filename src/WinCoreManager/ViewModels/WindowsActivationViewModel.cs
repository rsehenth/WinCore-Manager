using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Localization;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.ViewModels;

public partial class WindowsActivationViewModel : ObservableObject
{
    private readonly IWindowsActivationService _windows;
    private readonly IEssentialNavigation _navigation;
    private bool _refreshOnReturn;

    public WindowsActivationViewModel(IWindowsActivationService windows, IEssentialNavigation navigation)
    {
        _windows = windows;
        _navigation = navigation;
        PropertyChangedEventManager.AddHandler(LocalizationService.Current, OnLanguageChanged, nameof(LocalizationService.Culture));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(SettingsLabel), nameof(Name), nameof(Edition), nameof(DisplayVersion), nameof(Build))]
    [NotifyPropertyChangedFor(nameof(ShowActivate), nameof(CanActivate))]
    [NotifyCanExecuteChangedFor(nameof(ActivateCommand))]
    private WindowsActivationSnapshot? _snapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanActivate))]
    [NotifyCanExecuteChangedFor(nameof(OpenSettingsCommand), nameof(ActivateCommand))]
    private bool _checking;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenFailed), nameof(OpenFailureText))]
    private string? _openFailureKey;

    public bool ShowActivate => Snapshot?.State == WindowsActivationState.NotActivated;
    public bool CanActivate => ShowActivate && !Checking;
    public bool OpenFailed => OpenFailureKey is not null;
    public string OpenFailureText => OpenFailureKey is { } key ? LocalizationService.Current[key] : string.Empty;

    public string Name => Snapshot?.Identity.Name ?? LocalizationService.Current["WindowsCard.Unavailable"];
    public string Edition => Snapshot?.Identity.Edition ?? LocalizationService.Current["WindowsCard.Unavailable"];
    public string DisplayVersion => Snapshot?.Identity.DisplayVersion ?? LocalizationService.Current["WindowsCard.Unavailable"];
    public string Build => Snapshot?.Identity.Build ?? LocalizationService.Current["WindowsCard.Unavailable"];

    public string StatusText => LocalizationService.Current[Checking ? "WindowsCard.Checking" : (Snapshot?.State ?? WindowsActivationState.Unknown).StatusKey()];

    public string SettingsLabel => LocalizationService.Current[Snapshot?.State switch
    {
        WindowsActivationState.Activated => "WindowsCard.View",
        WindowsActivationState.NotActivated => "WindowsCard.Settings",
        _ => "WindowsCard.Check"
    }];

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SettingsLabel));
        OnPropertyChanged(nameof(OpenFailureText));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Edition));
        OnPropertyChanged(nameof(DisplayVersion));
        OnPropertyChanged(nameof(Build));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        Checking = true;
        try
        {
            Snapshot = await _windows.ReadAsync();
        }
        finally
        {
            Checking = false;
        }
    }

    private bool CanOpenSettings() => !Checking;

    [RelayCommand(CanExecute = nameof(CanOpenSettings))]
    private void OpenSettings()
    {
        if (Checking) return;
        HandleNavigationResult(_navigation.OpenActivationSettings(), "WindowsCard.OpenFailed");
    }

    [RelayCommand(CanExecute = nameof(CanActivate))]
    private void Activate(Func<bool>? confirm)
    {
        if (!CanActivate || confirm?.Invoke() != true || !CanActivate) return;
        HandleNavigationResult(_navigation.OpenActivationRepository(), "WindowsCard.RepositoryOpenFailed");
    }

    private void HandleNavigationResult(bool opened, string errorKey)
    {
        OpenFailureKey = opened ? null : errorKey;
        _refreshOnReturn = opened;
    }

    internal void RefreshOnReturn()
    {
        if (!_refreshOnReturn) return;
        _refreshOnReturn = false;
        RefreshIfIdle();
    }

    internal void RefreshIfIdle()
    {
        if (RefreshCommand.CanExecute(null)) RefreshCommand.Execute(null);
    }
}
