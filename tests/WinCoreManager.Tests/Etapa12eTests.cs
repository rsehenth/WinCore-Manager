using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class Etapa12eTests
{
    [Fact]
    public void Catalog_contains_unique_approved_pages_and_localized_metadata()
    {
        var apps = EssentialAppCatalog.Apps;
        Assert.Equal(30, apps.Count);
        Assert.Equal(apps.Count, apps.Select(app => app.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(apps.Count, apps.Select(app => app.Page).Distinct().Count());
        Assert.All(apps, app =>
        {
            Assert.True(EssentialNavigation.IsApprovedPage(app.Page));
            Assert.NotEqual(AppCategory.All, app.Category);
            foreach (var language in new[] { "pt-BR", "en-US" })
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
                foreach (var key in new[] { $"Essentials.App.{app.Id}", $"Essentials.Category.{app.Category}",
                             $"Essentials.Origin.{app.Origin}", $"Essentials.Note.{app.Note}" })
                    Assert.False(string.IsNullOrWhiteSpace(LocalizationService.Resources.GetString(key, culture)), key);
            }
        });
        Assert.Equal(AppNote.Unsupported, apps.Single(app => app.Id == "desktop8").Note);
        Assert.Equal(AppNote.ThirdParty, apps.Single(app => app.Id == "vc-aio").Note);
    }

    [Theory]
    [InlineData("http://code.visualstudio.com/download")]
    [InlineData("https://code.visualstudio.com.evil.test/download")]
    [InlineData("https://name@code.visualstudio.com/download")]
    [InlineData("https://code.visualstudio.com:444/download")]
    [InlineData("https://code.visualstudio.com/download?next=evil")]
    [InlineData("https://code.visualstudio.com/download#fragment")]
    [InlineData("https://github.com/Unlisted/Repository")]
    [InlineData("file:///C:/Windows/notepad.exe")]
    [InlineData("ms-settings:activation")]
    [InlineData("/download")]
    public void Catalog_destination_rejects_unapproved_targets(string destination) =>
        Assert.False(EssentialNavigation.IsApprovedPage(new Uri(destination, UriKind.RelativeOrAbsolute)));

    [Fact]
    public void Launch_uses_only_the_approved_shell_protocols_without_arguments_and_propagates_failure()
    {
        var calls = new List<ProcessStartInfo>();
        var navigation = new EssentialNavigation(info => { calls.Add(info); return calls.Count == 1; });
        Assert.False(navigation.OpenAppPage("https://example.com"));
        Assert.Empty(calls);
        Assert.True(navigation.OpenAppPage("vscode"));
        Assert.False(navigation.OpenActivationSettings());
        Assert.False(navigation.OpenActivationRepository());
        Assert.Equal(new[] { "https://code.visualstudio.com/download", "ms-settings:activation",
            "https://github.com/massgravel/Microsoft-Activation-Scripts" }, calls.Select(call => call.FileName));
        Assert.All(calls, call =>
        {
            Assert.True(call.UseShellExecute);
            Assert.Empty(call.Arguments);
            Assert.Empty(call.ArgumentList);
            Assert.Empty(call.Verb);
        });
    }

    [Fact]
    public Task Search_and_categories_follow_language_and_report_open_failure() => WpfTestThread.Run(() =>
    {
        var culture = LocalizationService.Current;
        var previous = culture.CultureName;
        try
        {
            culture.Apply("pt-BR");
            var vm = new EssentialsViewModel(new EssentialNavigation(_ => false));
            Assert.Equal(30, vm.Results.Count);
            vm.SearchText = "  VIDEO   nextgen ";
            Assert.Equal("nextgenup", Assert.Single(vm.Results).App.Id);
            vm.Category = AppCategory.Drivers;
            Assert.True(vm.IsEmpty);
            vm.SearchText = "NVIDIA";
            Assert.Equal("Drivers NVIDIA", Assert.Single(vm.Results).Name);
            culture.Apply("en-US");
            Assert.Equal("NVIDIA Drivers", Assert.Single(vm.Results).Name);
            Assert.Equal(AppCategory.Drivers, vm.Category);
            vm.SearchText = "hardware";
            Assert.Equal(2, vm.Results.Count);
            vm.OpenPageCommand.Execute("nvidia");
            Assert.True(vm.OpenFailed);
            vm.Category = AppCategory.All;
            vm.SearchText = "system runtimes";
            Assert.Equal(8, vm.Results.Count);
        }
        finally { culture.Apply(previous); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(0, WindowsActivationState.NotActivated)]
    [InlineData(1, WindowsActivationState.Activated)]
    [InlineData(2, WindowsActivationState.Grace)]
    [InlineData(3, WindowsActivationState.Grace)]
    [InlineData(4, WindowsActivationState.Grace)]
    [InlineData(5, WindowsActivationState.NotActivated)]
    [InlineData(6, WindowsActivationState.Grace)]
    [InlineData(7, WindowsActivationState.Unknown)]
    public async Task Licensing_uses_installed_edition_and_read_only_fields(int status, WindowsActivationState expected)
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("Win32_OperatingSystem", HardwareInventoryTests.R(("Caption", "Windows Test Edition"), ("BuildNumber", "26300")));
        wmi.Add("SoftwareLicensingProduct", License("Professional", status), License("OtherEdition", status == 1 ? 0 : 1));
        var service = CreateActivationService(wmi, "Professional");
        var snapshot = await service.ReadAsync();
        Assert.Equal(new WindowsIdentity("Windows Test Edition", "Professional", "25H2", "26300.888"), snapshot.Identity);
        Assert.Equal(expected, snapshot.State);
        Assert.Collection(wmi.Requests,
            request => Assert.Equal("Win32_OperatingSystem", request.Class),
            request =>
            {
                Assert.Equal("SoftwareLicensingProduct", request.Class);
                Assert.Equal("LicenseFamily,LicenseStatus,EvaluationEndDate", request.Columns);
                Assert.Equal("ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND LicenseIsAddon = FALSE AND PartialProductKey IS NOT NULL", request.Filter);
            });
    }

    [Theory]
    [InlineData(ReadingState.Error)]
    [InlineData(ReadingState.Restricted)]
    [InlineData(ReadingState.Unsupported)]
    [InlineData(ReadingState.NoReading)]
    public async Task Wmi_failure_and_missing_rows_are_unknown(ReadingState failure)
    {
        var wmi = new TestHardwareWmi();
        wmi.Results["SoftwareLicensingProduct"] = new(failure, []);
        var snapshot = await CreateActivationService(wmi, "Professional").ReadAsync();
        Assert.Equal(WindowsActivationState.Unknown, snapshot.State);
        Assert.Null(snapshot.Identity.Name);
        Assert.Equal("26300.888", snapshot.Identity.Build);
    }

    [Fact]
    public async Task Multiple_rows_must_agree_and_evaluation_is_distinct()
    {
        var wmi = new TestHardwareWmi();
        wmi.Add("SoftwareLicensingProduct", License("Professional", 1), License("Professional", 0));
        var service = CreateActivationService(wmi, "Professional");
        Assert.Equal(WindowsActivationState.Unknown, (await service.ReadAsync()).State);
        wmi.Add("SoftwareLicensingProduct", License("professional", 1), License("Professional", 1));
        Assert.Equal(WindowsActivationState.Activated, (await service.ReadAsync()).State);
        wmi.Add("SoftwareLicensingProduct", License("EnterpriseEval", 1));
        Assert.Equal(WindowsActivationState.Unknown, (await service.ReadAsync()).State);
        Assert.Equal(WindowsActivationState.Grace, (await CreateActivationService(wmi, "EnterpriseEval").ReadAsync()).State);
        Assert.Equal(WindowsActivationState.Unknown, (await CreateActivationService(wmi, null).ReadAsync()).State);
        var expiry = System.Management.ManagementDateTimeConverter.ToDmtfDateTime(DateTime.Now.AddDays(30));
        wmi.Add("SoftwareLicensingProduct", HardwareInventoryTests.R(("LicenseFamily", "Professional"), ("LicenseStatus", 1), ("EvaluationEndDate", expiry)));
        Assert.Equal(WindowsActivationState.Grace, (await service.ReadAsync()).State);
    }

    [Fact]
    public Task Activation_confirmation_and_return_never_modify_licensing() => WpfTestThread.Run(async () =>
    {
        var source = new TestWindowsActivationService { State = WindowsActivationState.NotActivated };
        var launches = new List<string>();
        var vm = new WindowsActivationViewModel(source, new EssentialNavigation(info =>
        {
            launches.Add(info.FileName);
            return true;
        }));
        var language = LocalizationService.Current;
        var previous = language.CultureName;
        try
        {
            language.Apply("en-US");
            Assert.False(vm.ShowActivate);
            Assert.False(vm.ActivateCommand.CanExecute(null));
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.ShowActivate);
            Assert.True(vm.ActivateCommand.CanExecute(null));
            Assert.Equal("Activation settings", vm.SettingsLabel);
            Assert.Equal("Open on GitHub", language["WindowsCard.OpenRepository"]);
            vm.ActivateCommand.Execute(() => false);
            Assert.Empty(launches);
            vm.ActivateCommand.Execute(() => true);
            Assert.Equal("https://github.com/massgravel/Microsoft-Activation-Scripts", Assert.Single(launches));
            Assert.Equal(WindowsActivationState.NotActivated, vm.Snapshot?.State);
            source.State = WindowsActivationState.Activated;
            vm.RefreshOnReturn();
            await vm.RefreshCommand.ExecutionTask!;
            Assert.Equal(2, source.Reads);
            Assert.Equal("View activation", vm.SettingsLabel);
            language.Apply("pt-BR");
            Assert.Equal("Ver ativação", vm.SettingsLabel);
            Assert.Equal("Abrir no GitHub", language["WindowsCard.OpenRepository"]);

            foreach (var state in new[] { WindowsActivationState.Activated, WindowsActivationState.Unknown, WindowsActivationState.Grace })
            {
                source.State = state;
                await vm.RefreshCommand.ExecuteAsync(null);
                Assert.False(vm.ShowActivate);
                Assert.False(vm.ActivateCommand.CanExecute(null));
                vm.ActivateCommand.Execute(() => throw new InvalidOperationException("Confirmation should be skipped."));
                Assert.Single(launches);
            }

            vm.OpenSettingsCommand.Execute(null);
            Assert.Equal("ms-settings:activation", launches[1]);

            source.State = WindowsActivationState.NotActivated;
            var failing = new WindowsActivationViewModel(source, new EssentialNavigation(_ => false));
            await failing.RefreshCommand.ExecuteAsync(null);
            var readsBeforeFailure = source.Reads;
            failing.ActivateCommand.Execute(() => true);
            Assert.True(failing.OpenFailed);
            Assert.Equal(language["WindowsCard.RepositoryOpenFailed"], failing.OpenFailureText);
            failing.OpenSettingsCommand.Execute(null);
            Assert.True(failing.OpenFailed);
            Assert.Equal(language["WindowsCard.OpenFailed"], failing.OpenFailureText);
            failing.RefreshOnReturn();
            Assert.Equal(readsBeforeFailure, source.Reads);
        }
        finally { language.Apply(previous); }
    });

    [Theory]
    [InlineData(1.0, 1120, 3)]
    [InlineData(1.25, 840, 2)]
    [InlineData(1.5, 600, 1)]
    public Task Essentials_reflows_at_scaled_root_dpi(double scale, double width, int columns) =>
        WpfTestThread.Run(async () =>
        {
            var view = new EssentialsPage { DataContext = new EssentialsViewModel(new EssentialNavigation(_ => true)) };
            VisualTreeHelper.SetRootDpi(view, new DpiScale(scale, scale));
            Assert.Equal(scale, VisualTreeHelper.GetDpi(view).DpiScaleX);
            var size = new Size(width, 460);
            view.Measure(size);
            view.Arrange(new Rect(size));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            view.UpdateLayout();
            Assert.Equal(columns, view.CardColumns);
            var cards = Assert.IsType<ItemsControl>(view.FindName("AppCards"));
            Assert.Equal(30, cards.Items.Count);
            var search = Assert.IsType<TextBox>(view.FindName("EssentialsSearch"));
            var filter = Assert.IsType<ComboBox>(view.FindName("CategoryFilter"));
            Assert.True(search.IsTabStop);
            Assert.True(filter.IsTabStop);
        });

    private static WmiRow License(string family, int status) =>
        HardwareInventoryTests.R(("LicenseFamily", family), ("LicenseStatus", status));

    private static WindowsActivationService CreateActivationService(TestHardwareWmi wmi, string? edition)
    {
        var licensing = new WindowsLicensingProvider(wmi);
        var facts = new TestWindowsFacts(edition, "25H2", 888, null, null, "X64", new(10, 0, 26300));
        return new(new WindowsOsProvider(wmi, licensing, facts), licensing);
    }
}

internal sealed class TestWindowsActivationService : IWindowsActivationService
{
    public WindowsActivationState State { get; set; } = WindowsActivationState.Activated;
    public int Reads { get; private set; }

    public Task<WindowsActivationSnapshot> ReadAsync(CancellationToken token = default)
    {
        Reads++;
        return Task.FromResult(new WindowsActivationSnapshot(new("Windows de teste", "Professional", "25H2", "26300.888"), State));
    }
}
