using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WinCoreManager.Helpers;
using WinCoreManager.Localization;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.SelfUpdate;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;
    private PortableUpdateLease? _portableLease;
    private readonly Action<IServiceCollection>? _configureServices;

    public App() { }

    internal App(Action<IServiceCollection> configureServices) => _configureServices = configureServices;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args is ["--verify-wincore-package", var expectedVersion])
        {
            VerifyUpdatedPackage(expectedVersion);
            return;
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        var services = new ServiceCollection();

        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton(sp => new ThemeService(sp.GetRequiredService<ISettingsService>(), this));
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IHistoryService, HistoryService>();
        services.AddSingleton(sp => new WinGetInterruptionStore(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager"),
            () => WinGetInterruptionStore.ReadWindowsBoot(sp.GetRequiredService<IHardwareWmi>())));
        services.AddSingleton<IWinGetService>(sp => new WinGetService(sp.GetRequiredService<WinGetInterruptionStore>()));
        services.AddSingleton<MaintenanceService>();

        services.AddSingleton<IUninstallRegistryView, WindowsUninstallRegistryView>();
        services.AddSingleton<ResiduePathRules>(sp => ResiduePathRules.ForEnvironment(sp.GetRequiredService<ISettingsService>().DataDirectory));
        services.AddSingleton<WindowsResidueRegistry>();
        services.AddSingleton<IResidueRegistryReader>(sp => sp.GetRequiredService<WindowsResidueRegistry>());
        services.AddSingleton<IResidueRegistryWriter>(sp => sp.GetRequiredService<WindowsResidueRegistry>());
        services.AddSingleton<IProgramInventoryService, RegistryProgramScanner>();
        services.AddSingleton<IUninstallPlanner, UninstallPlanner>();
        services.AddSingleton<IUninstallExecutor, ProcessUninstallExecutor>();
        services.AddSingleton<IResidueAnalysisService, ResidueAnalysisService>();
        services.AddSingleton<IResidueQuarantineService>(sp => new ResidueQuarantineService(
            Path.Combine(sp.GetRequiredService<ISettingsService>().DataDirectory, "Quarantine"),
            sp.GetRequiredService<IResidueRegistryReader>(),
            sp.GetRequiredService<IResidueRegistryWriter>(),
            sp.GetRequiredService<ResiduePathRules>(),
            Environment.IsPrivilegedProcess,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager", "Quarantine")));
        services.AddSingleton<ResidueCleanupPlanner>();
        services.AddSingleton<ResidueCleanupExecutor>();
        services.AddSingleton<UninstallBatchRunner>();
        services.AddSingleton<UninstallersViewModel>();

        services.AddSingleton<IExternalNavigation, WindowsExternalNavigation>();
        services.AddSingleton<IEssentialNavigation, EssentialNavigation>();
        services.AddSingleton<IStartupConfirmation, WindowsStartupConfirmation>();
        services.AddSingleton<IStartupRegistry, WindowsStartupRegistry>();
        services.AddSingleton<IStartupRunRegistry, WindowsStartupRunRegistry>();
        services.AddSingleton(_ => new StartupRunBackups(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager", "StartupRunBackups")));
        services.AddSingleton<StartupRunSource>();
        services.AddSingleton<IStartupProductReader, WindowsStartupProductReader>();
        services.AddSingleton<IStartupShortcutReader, WindowsStartupShortcutReader>();
        services.AddSingleton<StartupPolicy>();
        services.AddSingleton(_ => new StartupBackupStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinCoreManager", "StartupBackups")));
        services.AddSingleton(sp => new StartupFolderSource(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup, Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup, Environment.SpecialFolderOption.DoNotVerify),
            sp.GetRequiredService<IStartupRegistry>(), sp.GetRequiredService<IStartupShortcutReader>(),
            sp.GetRequiredService<IStartupProductReader>(), sp.GetRequiredService<StartupPolicy>(),
            sp.GetRequiredService<StartupBackupStore>()));
        services.AddSingleton<IStartupSource>(sp => sp.GetRequiredService<StartupFolderSource>());
        services.AddSingleton<IStartupSource, StartupRegistrySource>();
        services.AddSingleton<IStartupSource, StartupTaskSource>();
        services.AddSingleton<IStartupInventory, StartupInventory>();
        services.AddSingleton<IStartupFileMove, WindowsStartupFileMove>();
        services.AddSingleton<IStartupChanges, StartupChanges>();
        services.AddSingleton<StartupViewModel>();
        services.AddSingleton<IWindowsTools, WindowsTools>();
        services.AddSingleton<IWindowsFeatureConfirmation, WindowsFeatureConfirmation>();
        services.AddSingleton<ICleanupPlatform, WindowsCleanupPlatform>();
        services.AddSingleton<CleanupService>();
        services.AddSingleton<CleanupViewModel>();
        services.AddSingleton<ITweakPlatform, WindowsTweakPlatform>();
        services.AddSingleton<ITweakJournal>(_ => new WindowsTweakJournal(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager", "Tweaks")));
        services.AddSingleton<TweakService>();
        services.AddSingleton<IRestorePointReader, WindowsRestorePointReader>();
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<TweaksViewModel>();
        services.AddSingleton<IHardwareWmi, HardwareWmi>();
        services.AddSingleton<IWindowsFacts, WindowsFacts>();
        services.AddSingleton<IMemoryFacts, WindowsMemoryFacts>();
        services.AddSingleton<IGraphicsAdapters, DxgiGraphicsAdapters>();
        services.AddSingleton<IGpuMemoryCapacity, NvidiaMemoryCapacity>();
        services.AddSingleton<IDesktopDisplays, WindowsDesktopDisplays>();
        services.AddSingleton<WindowsLicensingProvider>();
        services.AddSingleton<ILicensingProvider>(provider => provider.GetRequiredService<WindowsLicensingProvider>());
        services.AddSingleton<WindowsOsProvider>();
        services.AddSingleton<IOperatingSystemProvider>(provider => provider.GetRequiredService<WindowsOsProvider>());
        services.AddSingleton<IWindowsActivationService, WindowsActivationService>();
        services.AddSingleton<IBoardProvider, WindowsBoardProvider>();
        services.AddSingleton<ICpuProvider, WindowsCpuProvider>();
        services.AddSingleton<IGpuProvider, WindowsGpuProvider>();
        services.AddSingleton<IRamProvider, WindowsRamProvider>();
        services.AddSingleton<IStorageProvider, WindowsStorageProvider>();
        services.AddSingleton<IComplementaryProvider, WindowsComplementaryProvider>();
        services.AddSingleton<IHardwareInventory, HardwareInventory>();
        services.AddSingleton<ISensorProviderFactory, WindowsSensorFactory>();
        services.AddSingleton<ISensorScheduler>(sp => new SensorScheduler(sp.GetRequiredService<ISensorProviderFactory>(), TimeProvider.System, TimeSpan.FromSeconds(2)));
        services.AddSingleton<HardwareViewModel>();
        services.AddSingleton<AboutViewModel>();

        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<WindowsActivationViewModel>();
        services.AddSingleton<EssentialsViewModel>();
        services.AddSingleton(sp => new UpdatesViewModel(sp.GetRequiredService<IWinGetService>(),
            sp.GetRequiredService<IHistoryService>(), sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<WinGetInterruptionStore>()));
        services.AddSingleton<MaintenanceViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        services.AddSingleton<MainWindow>();
        _configureServices?.Invoke(services);

        Services = services.BuildServiceProvider();
        LocalizationService.Current.Apply(Services.GetRequiredService<ISettingsService>().Settings.Language);
        if (!AcquirePortableLease())
        {
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        WindowEffects.Initialize();
        Services.GetRequiredService<ThemeService>().Initialize();
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;

        var mainWindow = Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        if (Services.GetRequiredService<ISettingsService>() is SettingsService { LoadProblem: { } problem })
        {
            MessageBox.Show(mainWindow, PresentationText.Translate(problem, LocalizationService.Current.Culture),
                "WinCore Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        _ = InitializeDashboardAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _portableLease?.Dispose();
        base.OnExit(e);
    }

    private void OnSystemParametersChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            Dispatcher.InvokeAsync(() =>
            {
                var theme = Services.GetRequiredService<ThemeService>();
                theme.Apply(theme.Current);
            });
        }
    }

    private bool AcquirePortableLease()
    {
        var root = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(root, PortablePackage.Manifest)) &&
            !Directory.Exists(Path.Combine(root, PortablePackage.WorkDirectory)))
        {
            return true;
        }

        var needsRecovery = false;
        try
        {
            var canonicalRoot = ResidueFileGuard.ResolveLocalDirectory(root) ?? throw new UpdateException(UpdateState.CannotWrite);
            _portableLease = PortableUpdateLease.TryAcquire(canonicalRoot);
            if (_portableLease is not null)
            {
                PortablePackage.CheckPath(Path.Combine(canonicalRoot, PortablePackage.Manifest), canonicalRoot);
                var pending = PortableUpdateCoordinator.FindPending(canonicalRoot, includeRootAncestors: false);
                if (pending.Count == 0) return true;

                needsRecovery = true;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UpdateException or
            InvalidDataException or InvalidOperationException or BadImageFormatException or System.ComponentModel.Win32Exception)
        {
        }

        _portableLease?.Dispose();
        _portableLease = null;
        MessageBox.Show(LocalizationService.Current[needsRecovery ? "Updater.RecoveryBlocked" : "Updater.PackageBusy"],
            "WinCore Manager", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void VerifyUpdatedPackage(string expectedVersion)
    {
        try
        {
            if (!UpdateVersion.TryParse(expectedVersion, out var expected) ||
                GitHubReleases.InstalledVersion.CompareTo(expected) != 0)
            {
                Shutdown(2);
                return;
            }

            PortablePackage.VerifyLayout(AppContext.BaseDirectory, expected);
            var culture = System.Globalization.CultureInfo.GetCultureInfo(LocalizationService.English);
            if (LocalizationService.Resources.GetResourceSet(culture, true, false) is null)
            {
                Shutdown(2);
                return;
            }

            var themes = new ThemeService(new SettingsService(), this);
            themes.Apply(Models.AppTheme.Light);
            themes.Apply(Models.AppTheme.Dark);
            Shutdown();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or UpdateException or
            InvalidOperationException or BadImageFormatException)
        {
            Shutdown(2);
        }
    }

    private async Task InitializeDashboardAsync()
    {
        await ApplyRetentionAsync();
        var viewModel = Services.GetRequiredService<MainViewModel>();
        if (viewModel.CurrentViewModel == viewModel.Dashboard)
        {
            viewModel.Dashboard.Refresh();
        }
    }

    private async Task ApplyRetentionAsync()
    {
        try
        {
            var history = Services.GetRequiredService<IHistoryService>();
            var settings = Services.GetRequiredService<ISettingsService>();
            if (settings.Settings.HistoryRetentionDays > 0)
            {
                await history.DeleteOlderThanAsync(DateTimeOffset.Now.AddDays(-settings.Settings.HistoryRetentionDays));
            }
        }
        catch
        {
            // Limpeza de retenção não deve bloquear a inicialização.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatalError(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ShowFatalError(ex);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
    }

    private static void ShowFatalError(Exception ex)
    {
        MessageBox.Show(
            LocalizationService.Current.Format("Message.UnexpectedError", ex.Message),
            "WinCore Manager",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }
}
