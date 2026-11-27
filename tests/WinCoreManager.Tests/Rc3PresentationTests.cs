using System.Windows;
using System.Windows.Controls;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class Rc3PresentationTests
{
    [Theory]
    [InlineData(AppTheme.Dark, "pt-BR", 460)]
    [InlineData(AppTheme.Light, "en-US", 620)]
    public Task Compiled_dialogs_handle_models_tabs_filters_and_keyboard_focus(AppTheme theme, string language, int width) =>
        WpfTestThread.Run(async () =>
        {
            var previousTheme = ThemeResources.Current;
            var previousLanguage = LocalizationService.Current.CultureName;
            ThemeResources.Install(Application.Current.Resources, theme);
            LocalizationService.Current.Apply(language);

            var fixture = Rc3FunctionalTests.TestDisks();
            var details = new DiskHealthDetailsWindow(new DiskHealthRow(fixture.Disks[0])) { Width = width };
            var planner = new UninstallPlanner();
            var model = new UninstallersViewModel(new FakeProgramInventory(), planner,
                new UninstallBatchRunner(planner, new FakeUninstallExecutor(), new FakeHistoryService()),
                new FakeResidueAnalysisService());
            var apps = OptionalWindowsApps.Classify([Rc3FunctionalTests.Package("Microsoft.GetHelp"),
                Rc3FunctionalTests.Package("Microsoft.WindowsFeedbackHub", false),
                Rc3FunctionalTests.Package("Microsoft.Windows.Photos")], [], planner);
            var optional = new OptionalWindowsAppsWindow(_ => Task.FromResult(apps), model) { Width = width };
            var program = new InstalledProgram { Name = "Fixture app" };
            var source = new FakeResidueAnalysisService
            {
                Result = new ResidueScanResult
                {
                    Program = program,
                    Items =
                    [
                        new() { Id = Guid.NewGuid(), Location = @"C:\Fixture\App", Category = ResidueCategory.Folder, Confidence = ResidueConfidence.High },
                        new() { Id = Guid.NewGuid(), Location = @"HKEY_CURRENT_USER\SOFTWARE\Fixture", Category = ResidueCategory.RegistryKey, Confidence = ResidueConfidence.High },
                        new() { Id = Guid.NewGuid(), Location = @"HKEY_LOCAL_MACHINE\SOFTWARE\Fixture", Category = ResidueCategory.RegistryValue,
                            Confidence = ResidueConfidence.Protected, ValueName = "AutoRun", ValueType = "REG_SZ" },
                    ],
                },
            };
            using var registry = new FakeResidueRegistryReader();
            var writer = new FakeResidueRegistryWriter();
            var quarantine = new FakeQuarantineService();
            var residueModel = new ResidueScanViewModel(program, [], source, new ResidueCleanupPlanner(source),
                new ResidueCleanupExecutor(quarantine, registry, writer, new FakeHistoryService()), quarantine);
            var residue = new ResidueScanWindow(residueModel) { Width = width, Height = 480 };
            try
            {
                details.Show();
                Assert.Contains("Fixture NVMe", Get<TextBlock>(details, "Overview").Text);
                Assert.Contains("94%", Get<TextBlock>(details, "Overview").Text);
                Assert.True(Get<ItemsControl>(details, "Metrics").Items.Count > 3);

                optional.Show();
                await Until(() => Get<ItemsControl>(optional, "Suggested").Items.Count == 2);
                var suggested = Get<ItemsControl>(optional, "Suggested").Items.Cast<OptionalAppRow>().ToArray();
                var other = Assert.IsType<OptionalAppRow>(Get<ItemsControl>(optional, "Others").Items[0]);
                Assert.All(suggested, item => Assert.False(item.IsSelected));
                Assert.False(other.IsSelected);
                Assert.False(Assert.Single(suggested, row => row.App.State == OptionalAppState.Protected).Eligible);
                Assert.False(Get<Button>(optional, "Remove").IsEnabled);
                other.IsSelected = true;
                Assert.True(Get<Button>(optional, "Remove").IsEnabled);
                Get<TextBox>(optional, "Search").Text = "No matching fixture";
                Assert.Empty(Get<ItemsControl>(optional, "Suggested").Items);
                Assert.True(other.IsSelected);
                Get<CheckBox>(optional, "ShowAbsent").IsChecked = true;
                Get<TextBox>(optional, "Search").Text = "Microsoft.Todos";
                Assert.Single(Get<ItemsControl>(optional, "Suggested").Items);

                residue.Show();
                await Until(() => residueModel.Rows.Count == 3);
                var resultTabs = Get<TabControl>(residue, "ResultTabs");
                var fileGrid = Assert.IsType<DataGrid>(((TabItem)resultTabs.Items[0]).Content);
                Assert.Single(fileGrid.Items);
                residueModel.SelectEligible(true);
                resultTabs.SelectedIndex = 1;
                var registryGrid = Assert.IsType<DataGrid>(((TabItem)resultTabs.Items[1]).Content);
                Assert.Equal(2, registryGrid.Items.Count);
                var evidence = Assert.Single(registryGrid.Items.Cast<ResidueItemRow>(), row => row.Item.ValueName == "AutoRun");
                Assert.False(evidence.CanSelect);
                Assert.Contains("REG_SZ", evidence.ValueDetails);
                registryGrid.SelectedItem = evidence;
                var resultDetails = Get<Expander>(residue, "ResultDetails");
                Assert.True(resultDetails.IsExpanded);
                Assert.True(resultDetails.IsVisible);
                Assert.Same(evidence, resultDetails.DataContext);
                residueModel.SelectEligible(true);
                Assert.Equal(2, residueModel.SelectedCount);
                Assert.Empty(writer.Operations);
                Assert.Equal(language == "en-US" ? "Optional Windows apps" : "Aplicativos opcionais do Windows", optional.Title);
            }
            finally
            {
                residue.Close();
                optional.Close();
                details.Close();
                ThemeResources.Install(Application.Current.Resources, previousTheme);
                LocalizationService.Current.Apply(previousLanguage);
            }
        });

    private static T Get<T>(Window window, string name) where T : FrameworkElement =>
        Assert.IsType<T>(window.FindName(name));

    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 120 && !condition(); attempt++)
            await Task.Delay(20);
        Assert.True(condition());
    }
}
