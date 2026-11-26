using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public class LocalizationTests
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Portuguese = CultureInfo.GetCultureInfo("pt-BR");

    [Fact]
    public Task Audit12H05_loaded_pages_recompose_multiple_warnings_on_language_switch() => WpfTestThread.Run(async () =>
    {
        var fixture = new WindowsFeaturesFixture();
        fixture.Tools.Available = false;
        fixture.Disk.Catalog = [];
        var cleanup = new CleanupPage { DataContext = fixture.CleanupPage };
        var tweaks = new TweaksPage { DataContext = fixture.TweaksPage };
        var window = new Window
        {
            ShowInTaskbar = false, ShowActivated = false, Opacity = 0,
            Content = new StackPanel { Children = { cleanup, tweaks } }
        };
        window.Show();
        try
        {
            await fixture.CleanupPage.RefreshCommand.ExecuteAsync(null);
            await fixture.TweaksPage.RefreshCommand.ExecuteAsync(null);
            string[] Displayed() => Descendants<TextBlock>(window).Select(text => text.Text).ToArray();
            var loc = LocalizationService.Current;
            loc.Apply(LocalizationService.English);
            window.UpdateLayout();
            Assert.Contains("No registered categories were found. Windows Disk Cleanup is unavailable.", Displayed());
            Assert.Contains("Performance Options unavailable. System Protection unavailable.", Displayed());
            loc.Apply(LocalizationService.Portuguese);
            window.UpdateLayout();
            Assert.Contains(fixture.CleanupPage.Status.Render(Portuguese), Displayed());
            Assert.Contains(fixture.TweaksPage.Status.Render(Portuguese), Displayed());
            Assert.DoesNotContain("Performance Options unavailable. System Protection unavailable.", Displayed());
        }
        finally { window.Close(); }
    });

    [Fact]
    public async Task Audit12H06_async_wpf_cases_have_exclusive_culture_scope()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = WpfTestThread.Run(async () =>
        {
            LocalizationService.Current.Apply(LocalizationService.English);
            entered.SetResult();
            await release.Task;
            Assert.Equal(LocalizationService.English, LocalizationService.Current.CultureName);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = WpfTestThread.Run(() =>
        {
            Assert.Equal(LocalizationService.Portuguese, LocalizationService.Current.CultureName);
            Assert.Equal(LocalizationService.Portuguese, CultureInfo.CurrentUICulture.Name);
            return Task.CompletedTask;
        });
        release.SetResult();
        await Task.WhenAll(first, second);
    }

    [Theory]
    [InlineData("{}", "pt-BR")]
    [InlineData("""{"Language":"es-ES"}""", "pt-BR")]
    [InlineData("""{"Language":{"invalid":true}}""", "pt-BR")]
    [InlineData("""{"Language":"EN-us"}""", "en-US")]
    public void Settings_legacy_or_invalid_language_preserves_other_preferences(string input, string expected)
    {
        var root = new TestSettings().DataDirectory;
        Directory.CreateDirectory(root);
        var json = input.Insert(1, "\"HistoryRetentionDays\":17,\"Theme\":\"Light\"" +
            (input == "{}" ? "" : ","));
        File.WriteAllText(Path.Combine(root, "settings.json"), json);
        var settings = new SettingsService(root, Path.Combine(root, "local"));
        var localization = new LocalizationService();
        localization.Apply(settings.Settings.Language);
        Assert.Equal(expected, localization.CultureName);
        Assert.Equal("Light", settings.Settings.Theme);
        Assert.Equal(17, settings.Settings.HistoryRetentionDays);
        Assert.True(localization.Select(settings, "en-US"));
        var reopened = new SettingsService(root, Path.Combine(root, "local"));
        Assert.Equal("en-US", reopened.Settings.Language);
        Assert.Equal("Light", reopened.Settings.Theme);
        Assert.Equal(17, reopened.Settings.HistoryRetentionDays);
    }

    [Fact]
    public void Resources_have_unique_keys_matching_placeholders_and_valid_references()
    {
        var directory = SourceRoot();
        var pt = ReadResources(Path.Combine(directory, "Localization", "Strings.resx"));
        var en = ReadResources(Path.Combine(directory, "Localization", "Strings.en-US.resx"));
        Assert.Equal(pt.Keys.Order(), en.Keys.Order());
        foreach (var (key, text) in pt)
        {
            var ptArguments = Arguments(text);
            Assert.Equal(ptArguments, Arguments(en[key]));
            Assert.False(string.IsNullOrWhiteSpace(en[key]), key);
            Assert.Equal(en[key], LocalizationService.Resources.GetString(key, English)?.ReplaceLineEndings("\n"));
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                           !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var extension = Path.GetExtension(file);
            if (extension is not (".xaml" or ".cs"))
            {
                continue;
            }
            var references = Regex.Matches(File.ReadAllText(file), extension == ".xaml"
                ? @"(?:\{loc:Loc |FormatKey=)([\w.]+)"
                : """LocalizationService\.(?:Source|Current\[)\(?"([\w.]+)" """.TrimEnd());
            foreach (Match reference in references)
            {
                Assert.True(pt.ContainsKey(reference.Groups[1].Value), $"{file}: {reference.Groups[1].Value}");
            }
        }
    }

    [Fact]
    public void Xaml_visible_literals_are_resources_or_product_identifiers()
    {
        var source = SourceRoot();
        var properties = new HashSet<string>
        {
            "Text", "Content", "Header", "ToolTip", "Title", "AutomationProperties.Name",
            "AutomationProperties.HelpText", "AutomationProperties.ItemStatus", "Tag",
        };
        var product = new HashSet<string> { "W", "WinCore Manager", "https://github.com/rsehenth" };
        var files = Directory.EnumerateFiles(Path.Combine(source, "Views"), "*.xaml")
            .Append(Path.Combine(source, "MainWindow.xaml"));
        foreach (var file in files)
        {
            foreach (var element in XDocument.Load(file).Descendants())
            {
                foreach (var attribute in element.Attributes())
                {
                    var property = attribute.Name.LocalName == "Value" && element.Name.LocalName == "Setter"
                        ? (string?)element.Attribute("Property") ?? "" : attribute.Name.LocalName;
                    var text = attribute.Value.Trim();
                    if (properties.Contains(property) && !text.StartsWith('{') &&
                        text.Any(char.IsLetter) && !product.Contains(text))
                    {
                        Assert.Fail($"{Path.GetFileName(file)}: {attribute}");
                    }
                }
            }
        }
    }

    [Fact]
    public void Presentation_code_has_no_detectable_hardcoded_portuguese_except_known_inputs()
    {
        var source = SourceRoot();
        var allowed = new HashSet<string> { "Português (Brasil)", "Placa de vídeo", "Memória RAM", "Ativação" };
        var files = Directory.EnumerateFiles(Path.Combine(source, "Views"), "*.cs")
            .Concat(Directory.EnumerateFiles(Path.Combine(source, "ViewModels"), "*.cs"));
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            {
                foreach (Match match in Regex.Matches(line, @"""(?:[^""\\]|\\.)*"""))
                {
                    var literal = match.Value[1..^1];
                    if (Regex.IsMatch(literal, "[À-ÖØ-öø-ÿ]"))
                    {
                        Assert.Contains(literal, allowed);
                    }
                }
            }
        }
    }

    [Theory]
    [InlineData("pt-BR", "23/11/2026", "31,5 °C")]
    [InlineData("en-US", "11/23/2026", "31.5 °C")]
    public void Typed_dates_and_measurements_use_the_display_culture(string language, string date, string temperature)
    {
        var culture = CultureInfo.GetCultureInfo(language);
        var timestamp = new DateTime(2026, 11, 23);
        Assert.Equal(date, HardwareReading.Create("pc", "date", timestamp, "Windows", default).Render(culture));
        Assert.Equal(temperature, HardwareReading.Create("gpu", "temperature", 31.5, "NVML", default, "°C").Render(culture));
        Assert.Equal("1 GiB", HardwareReading.FormatBytes(1UL << 30, culture));
        Assert.Equal("Core i7-14700K", HardwareReading.Create("cpu", "CPU", "Core i7-14700K", "WMI", default).Render(culture));
        Assert.Equal("Painel", HardwareReading.Create("device", "Model", "Painel", "WMI", default).Render(culture));
        var dates = new[] { timestamp, new DateTime(2026, 2, 28) };
        Assert.Equal(2, dates.Order().First().Month);
    }

    [Fact]
    public void Fallback_messages_and_plural_counts_are_observable()
    {
        var loc = new LocalizationService();
        var changes = new List<string?>();
        loc.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        Assert.Equal("Configurações", LocalizationService.Source("Message.Settings"));
        loc.Apply("en-US");
        Assert.Contains("Item[]", changes);
        Assert.Equal("unknown.key", loc["unknown.key"]);
        Assert.Equal("Cancel", loc.Format("UI.Cancel"));
        Assert.Equal("Atualizar", LocalizationService.Resources.GetString("UI.Refresh", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal("1 entry found", PresentationText.Translate("1 entrada encontrada", English));
        Assert.Equal("2 entries found", PresentationText.Translate(LocalizationService.Source("Message.0EntriesFound", 2), English));
        Assert.Equal("Theme changed to Dark.", PresentationText.Translate("Aparência alterada para Dark — Escuro.", English));
        Assert.Equal("32 GiB installed", PresentationText.Translate("32 GiB instalados", English));
        Assert.Equal("Review recommended", PresentationText.Translate("Atenção revisar", English));
        Assert.Equal("Size: 20 MiB", PresentationText.UseBinarySizeUnits("Size: 20 MB"));
    }

    [Theory]
    [InlineData("pt-BR", AppTheme.Dark)]
    [InlineData("pt-BR", AppTheme.Light)]
    [InlineData("en-US", AppTheme.Dark)]
    [InlineData("en-US", AppTheme.Light)]
    public Task DialogosReaisCarregamNosDoisTemasEIdiomas(string language, AppTheme theme) => WpfTestThread.Run(() =>
    {
        var loc = LocalizationService.Current;
        var previous = loc.Culture.Name;
        loc.Apply(language);
        ThemeResources.Install(Application.Current.Resources, theme);
        try
        {
            Window[] dialogs =
            [
                new UninstallConfirmDialog(new() { Title = "Fixture", Description = "", ItemCount = 1, Effects = "", Warnings = [], CommandText = "test.exe" }),
                new MaintenanceConfirmDialog(new() { Title = "Fixture", Description = "", CommandText = "test.exe", Effects = "", TypicalDuration = "" }),
                new ResidueCleanupConfirmDialog(new() { Title = "Fixture", Description = "", Items = [], BlockedReasons = [] }),
            ];
            foreach (var dialog in dialogs)
            {
                try
                {
                    Assert.False(Assert.IsType<Button>(dialog.FindName("ConfirmButton")).IsDefault);
                    dialog.Measure(new Size(1024, 720));
                }
                finally
                {
                    dialog.Close();
                }
            }

            var manual = (Window)Activator.CreateInstance(typeof(ManualResidueDialog), nonPublic: true)!;
            try { manual.Measure(new Size(1024, 720)); }
            finally { manual.Close(); }

            foreach (var confirmation in new[] { false, true })
            {
                var dialog = (Window)Activator.CreateInstance(typeof(LocalizedDialog),
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
                    [null, "Fixture", "Fixture", "Common.OK", confirmation], null)!;
                try
                {
                    dialog.Measure(new Size(1024, 720));
                    Assert.Equal(!confirmation, Assert.IsType<Button>(dialog.FindName("AcceptButton")).IsDefault);
                }
                finally { dialog.Close(); }
            }
        }
        finally
        {
            loc.Apply(previous);
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData("pt-BR", AppTheme.Dark)]
    [InlineData("pt-BR", AppTheme.Light)]
    [InlineData("en-US", AppTheme.Dark)]
    [InlineData("en-US", AppTheme.Light)]
    public Task TodasAsAbasRenderizamComCulturaETemaSelecionados(string language, AppTheme theme) => WpfTestThread.Run(() =>
    {
        var loc = LocalizationService.Current;
        var previous = loc.Culture.Name;
        loc.Apply(language);
        ThemeResources.Install(Application.Current.Resources, theme);
        try
        {
            UserControl[] pages =
            [
                new DashboardPage(), new EssentialsPage(), new UpdatesPage(), new MaintenancePage(), new UninstallersPage(),
                new StartupPage(), new CleanupPage(), new TweaksPage(), new HardwarePage(),
                new HistoryPage(), new SettingsPage(), new AboutPage(),
            ];
            foreach (var page in pages)
            {
                page.Measure(new Size(1024, 720));
                page.Arrange(new Rect(0, 0, 1024, 720));
                Assert.NotEmpty(Descendants<TextBlock>(page));
            }

            var formatted = (string)new WinCoreManager.Helpers.DurationConverter()
                .Convert(1500L, typeof(string), null!, loc.Culture);
            Assert.Equal(language == "en-US" ? "1.5 s" : "1,5 s", formatted);
            Assert.Equal(language == "en-US" ? "1.5 GB" : "1,5 GB",
                WinCoreManager.Services.WindowsFeatures.CleanupService.FormatBytes(1536L * 1024 * 1024));
            var name = LocalizationService.Source("Message.Uninstalling0", "1,5 GB");
            Assert.Contains("1,5 GB", PresentationText.Translate(name, loc.Culture));
        }
        finally
        {
            loc.Apply(previous);
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void HistoricoRotulaLimpezaENomesDeAplicativosPermanecemLiterais()
    {
        var converter = new WinCoreManager.Helpers.HistoryTypeToLabelConverter();
        foreach (var type in Enum.GetValues<HistoryType>())
            Assert.False(string.IsNullOrEmpty((string) converter.Convert(type, typeof(string), null!, English)));

        var message = LocalizationService.Source("Message.Uninstalling0", "Inicializar");
        Assert.Contains("Inicializar", PresentationText.Translate(message, English));
        Assert.DoesNotContain("Startup", PresentationText.Translate(message, English));
        Assert.Contains("\n", PresentationText.Translate(message + "\n" + message, English));
        var warning = LocalizationService.Source("Message.ReviewRequiredSharedDependencyOrUnrecognizedApp");
        Assert.Contains("only if", PresentationText.Translate(warning, English));
    }

    [Theory]
    [InlineData("pt-BR", AppTheme.Dark)]
    [InlineData("pt-BR", AppTheme.Light)]
    [InlineData("en-US", AppTheme.Dark)]
    [InlineData("en-US", AppTheme.Light)]
    public Task Settings_layers_keep_theme_selection_and_update_in_place(string language, AppTheme theme) => WpfTestThread.Run(async () =>
    {
        var loc = LocalizationService.Current;
        loc.Apply("pt-BR");
        var settings = new TestSettings();
        var themes = new ThemeService(settings, Application.Current);
        themes.Apply(theme);
        var vm = new SettingsViewModel(settings, new HistoryService(settings), new OfflineWinGet(), themes);
        var page = new SettingsPage { DataContext = vm };
        var window = new Window { Content = page, Width = 840, Height = 420, ShowInTaskbar = false };
        window.Show();
        try
        {
            var selector = Assert.IsType<ComboBox>(page.FindName("LanguageSelector"));
            selector.SelectedItem = vm.Languages.Single(option => option.Code == language);
            await Task.Yield();
            window.UpdateLayout();

            var texts = Descendants<TextBlock>(page).Select(t => t.Text).ToArray();
            Assert.Contains(language == "en-US" ? "Settings" : "Configurações", texts);
            Assert.Contains(language == "en-US" ? "WinCore Manager Updates" : "Atualizações do WinCore Manager", texts);
            Assert.Contains(language == "en-US" ? "Online updates are not configured for this version yet."
                : "As atualizações online ainda não estão configuradas para esta versão.", texts);
            Assert.Equal(theme, themes.Current);
            Assert.Equal(language, loc.CultureName);
            themes.Select(theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
            Assert.Equal(language, loc.CultureName);
            Assert.Same(page, window.Content);
        }
        finally
        {
            window.Close();
            loc.Apply("pt-BR");
            themes.Apply(AppTheme.Dark);
        }
    });

    [Fact]
    public Task Open_sensor_window_and_confirmation_retranslate_without_new_readings_or_command_changes() => WpfTestThread.Run(async () =>
    {
        var loc = LocalizationService.Current;
        loc.Apply("pt-BR");
        var scheduler = new CountingScheduler();
        var panel = new SensorPanelViewModel(scheduler);
        var sensors = new SensorWindow(panel) { ShowInTaskbar = false, ShowActivated = false };
        const string command = @"C:\Windows\System32\sfc.exe /verifyonly";
        var confirmation = new MaintenanceConfirmDialog(new MaintenanceConfirmationRequest
        {
            Title = "SFC VerifyOnly", Description = "", Effects = "", TypicalDuration = "", CommandText = command,
        }) { ShowInTaskbar = false, ShowActivated = false };
        sensors.Show();
        confirmation.Show();
        try
        {
            panel.SetActive(true);
            await Task.Yield();
            Assert.Equal(1, scheduler.Starts);
            Assert.Contains("Sensores", sensors.Title);
            loc.Apply("en-US");
            await Task.Yield();
            Assert.Contains("Sensors", sensors.Title);
            Assert.Contains(Descendants<Button>(sensors), button => Equals("Pause", button.Content));
            Assert.Contains(Descendants<Button>(confirmation), button => Equals("Cancel", button.Content));
            Assert.Contains(Descendants<TextBlock>(confirmation), block => block.Text == command);
            Assert.Equal(1, scheduler.Starts);
        }
        finally
        {
            confirmation.Close();
            sensors.Close();
            await panel.CloseAsync();
            loc.Apply("pt-BR");
        }
    });

    private static Dictionary<string, string> ReadResources(string path) => XDocument.Load(path)
        .Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => e.Element("value")!.Value);

    private static string[] Arguments(string text) => Regex.Matches(text, @"\{(\d+)(?:[^{}]*)\}")
        .Select(m => m.Groups[1].Value).Order().ToArray();

    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinCoreManager.sln")))
        {
            dir = dir.Parent;
        }
        return Path.Combine(dir!.FullName, "src", "WinCoreManager");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private sealed class CountingScheduler : ISensorScheduler
    {
        public int Starts { get; private set; }
        public async IAsyncEnumerable<SensorSnapshot> ObserveAsync([EnumeratorCancellation] CancellationToken token)
        {
            Starts++;
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }
    }

    private sealed class OfflineWinGet : IWinGetService
    {
        public string? ExecutablePath => null;
        public WinGetAvailability? LastDetection => null;
        public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetAvailability());
        public Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetScanResult());
        public Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
