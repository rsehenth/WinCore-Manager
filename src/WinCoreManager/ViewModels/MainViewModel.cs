using WinCoreManager.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

internal delegate bool TryReserveOperation(PackageChangeKind kind, string target, out PackageChangeReservation? reservation);

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    public IReadOnlyList<NavItem> NavItems { get; }
    public NavItem AboutNavItem { get; }

    public DashboardViewModel Dashboard { get; }
    public UpdatesViewModel Updates { get; }
    public MaintenanceViewModel Maintenance { get; }
    public UninstallersViewModel Uninstallers { get; }
    public StartupViewModel Startup { get; }
    public CleanupViewModel Cleanup { get; }
    public TweaksViewModel Tweaks { get; }
    public HardwareViewModel Hardware { get; }
    public EssentialsViewModel Essentials { get; }
    public HistoryViewModel History { get; }
    public SettingsViewModel Settings { get; }

    public AboutViewModel About { get; }
    public string AppVersion => About.AppVersion;

    public MainViewModel(
        DashboardViewModel dashboard,
        UpdatesViewModel updates,
        MaintenanceViewModel maintenance,
        UninstallersViewModel uninstallers,
        StartupViewModel startup,
        CleanupViewModel cleanup,
        TweaksViewModel tweaks,
        HardwareViewModel hardware,
        EssentialsViewModel essentials,
        HistoryViewModel history,
        SettingsViewModel settings,
        AboutViewModel about)
    {
        Dashboard = dashboard;
        Updates = updates;
        Maintenance = maintenance;
        Uninstallers = uninstallers;
        Startup = startup;
        Cleanup = cleanup;
        Tweaks = tweaks;
        Hardware = hardware;
        Essentials = essentials;
        History = history;
        Settings = settings;
        About = about;

        NavItems = new List<NavItem>
        {
            new(LocalizationService.Source("Message.Dashboard"), "\uE80F", Dashboard, true, OnNavSelected),
            new(LocalizationService.Source("Message.Updates"), "\uE895", Updates, false, OnNavSelected),
            new(LocalizationService.Source("Message.Maintenance"), "\uE90F", Maintenance, false, OnNavSelected),
            new(LocalizationService.Source("Message.Uninstaller"), "\uE74D", Uninstallers, false, OnNavSelected),
            new(LocalizationService.Source("Message.Startup"), "\uE7E8", Startup, false, OnNavSelected),
            new(LocalizationService.Source("Message.Cleanup"), "\uE894", Cleanup, false, OnNavSelected),
            new(LocalizationService.Source("Message.Tweaks"), "\uE9E9", Tweaks, false, OnNavSelected),
            new(LocalizationService.Source("Message.MyComputer"), "\uE950", Hardware, false, OnNavSelected),
            new(LocalizationService.Source("Message.Essentials"), "\uE7B8", Essentials, false, OnNavSelected),
            new(LocalizationService.Source("Message.History"), "\uE81C", History, false, OnNavSelected),
            new(LocalizationService.Source("Message.Settings"), "\uE713", Settings, false, OnNavSelected),
        };
        AboutNavItem = new(LocalizationService.Source("Message.About"), "\uE946", About, false, OnNavSelected);

        CurrentViewModel = Dashboard;
        Uninstallers.PackageChangesBlocked = Maintenance.PackageChangesBlocked = () => Updates.PackageChangesBlockedNow;
        Uninstallers.TryReservePackageChange = Maintenance.TryReservePackageChange = Updates.TryReservePackageChange;
        Uninstallers.ApplicationBusy = Updates.BlocksPackageChanges;
        Maintenance.ApplicationUpdateRunning = Updates.BlocksPackageChanges;
        Dashboard.NavigateRequested += OnNavigateRequested;
        Maintenance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MaintenanceViewModel.PackageConflictRunning))
                Updates.MaintenanceRunning = Maintenance.PackageConflictRunning || Uninstallers.IsUninstalling;
            if (args.PropertyName == nameof(MaintenanceViewModel.IsRunning))
            {
                Uninstallers.ApplicationBusy = Maintenance.IsRunning || Updates.IsScanning || Updates.BlocksPackageChanges;
                RefreshDashboardIfVisible();
            }
        };
        Updates.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UpdatesViewModel.BlocksPackageChanges))
            {
                Maintenance.ApplicationUpdateRunning = Updates.BlocksPackageChanges;
            }

            if (args.PropertyName is nameof(UpdatesViewModel.IsBusy) or nameof(UpdatesViewModel.BlocksPackageChanges))
            {
                Uninstallers.ApplicationBusy = Updates.IsScanning || Updates.BlocksPackageChanges || Maintenance.IsRunning;
            }

            if (args.PropertyName == nameof(UpdatesViewModel.IsBusy) && !Updates.IsBusy)
            {
                RefreshDashboardIfVisible();
                if (CurrentViewModel == History) History.Reload();
            }
        };
        Uninstallers.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(UninstallersViewModel.IsScanning) or nameof(UninstallersViewModel.IsUninstalling))
            {
                Updates.MaintenanceRunning = Maintenance.PackageConflictRunning || Uninstallers.IsUninstalling;
                Maintenance.ApplicationUninstallRunning = Uninstallers.IsRunning;
                RefreshDashboardIfVisible();
            }
        };
    }

    private void OnNavigateRequested(ViewModelBase target)
    {
        var item = NavItems.FirstOrDefault(n => n.ViewModel == target);
        if (item is not null)
        {
            item.IsSelected = true;
        }
    }

    private void RefreshDashboardIfVisible()
    {
        if (CurrentViewModel == Dashboard)
        {
            Dashboard.Refresh();
        }
    }

    private void OnNavSelected(NavItem item)
    {
        Updates.RefreshInterruptionState();
        foreach (var other in NavItems.Append(AboutNavItem))
            if (other != item) other.IsSelected = false;
        CurrentViewModel = item.ViewModel;

        if (item.ViewModel is DashboardViewModel dashboard)
        {
            dashboard.Refresh();
        }
        else if (item.ViewModel is HistoryViewModel history)
        {
            history.Reload();
        }
        else if (item.ViewModel is StartupViewModel startup && startup.RefreshCommand.CanExecute(null))
        {
            startup.RefreshCommand.Execute(null);
        }
        else if (item.ViewModel is CleanupViewModel cleanup && cleanup.RefreshCommand.CanExecute(null))
        {
            cleanup.RefreshCommand.Execute(null);
        }
        else if (item.ViewModel is TweaksViewModel tweaks && tweaks.RefreshCommand.CanExecute(null))
        {
            tweaks.RefreshCommand.Execute(null);
        }
        else if (item.ViewModel is HardwareViewModel hardware && hardware.Sections.Count == 0 && hardware.RefreshCommand.CanExecute(null))
        {
            hardware.RefreshCommand.Execute(null);
        }
    }
}
