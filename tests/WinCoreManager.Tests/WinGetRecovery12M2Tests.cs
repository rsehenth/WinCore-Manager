using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public class WinGetRecovery12M2Tests
{
    private static WinGetPackage BattleNet(string installed = "1.0") => new()
    {
        Id = "Blizzard.BattleNet", Name = "Battle.net", Source = "winget",
        InstalledVersion = installed, AvailableVersion = "2.0.0.4232",
    };

    private const string Manifest = """
        PackageIdentifier: Blizzard.BattleNet
        PackageVersion: 2.0.0.4232
        InstallerType: exe
        Installers:
        - Architecture: x86
          InstallerSwitches:
            InstallLocation: --installpath="<INSTALLPATH>"
          AppsAndFeaturesEntries:
          - ProductCode: Battle.net
          InstallLocationRequired: true
        ManifestType: installer
        """;

    [Fact]
    public void Installer_manifest_checks_identity_structure_and_inherited_requirement()
    {
        Assert.True(WinGetInstallerRequirements.ParseLocationRequirement(Manifest, BattleNet()));
        Assert.False(WinGetInstallerRequirements.ParseLocationRequirement(Manifest.Replace("true", "false"), BattleNet()));
        Assert.True(WinGetInstallerRequirements.ParseLocationRequirement(
            Manifest.Replace("  InstallLocationRequired: true", "InstallLocationRequired: true"), BattleNet()));
        Assert.Null(WinGetInstallerRequirements.ParseLocationRequirement(Manifest.Replace("2.0.0.4232", "2.0.0.1"), BattleNet()));
        Assert.Null(WinGetInstallerRequirements.ParseLocationRequirement(Manifest.Replace("true", "broken"), BattleNet()));
        Assert.Null(WinGetInstallerRequirements.ParseLocationRequirement(Manifest.Replace("true", "true\n  InstallLocationRequired: false"), BattleNet()));
        Assert.Null(WinGetInstallerRequirements.ParseLocationRequirement(Manifest.Replace("x86", "&anchor x86"), BattleNet()));
        Assert.Null(WinGetInstallerRequirements.ParseLocationRequirement(Manifest.Replace("x86", new string('[', 32) + "x86" + new string(']', 32)), BattleNet()));
    }

    [Theory]
    [InlineData("dismiss", "1.0")]
    [InlineData("games", "1.0")]
    [InlineData("metadata", "1.0")]
    [InlineData("unknown", "Unknown")]
    [InlineData("unknown", "Desconhecido")]
    public Task Preflight_failure_leaves_no_receipt_and_another_app_can_run(string mode, string installed) =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = NewStore(settings);
            var source = new InstallerLab(BattleNet(installed)) { MissingManifest = mode == "metadata" };
            var history = new RecoveryHistory();
            var folder = Directory.CreateDirectory(Path.Combine(settings.DataDirectory, "Games"));
            var selections = 0;
            var vm = new UpdatesViewModel(source.Service, history, settings, store)
            {
                ChooseUpdateLocation = (_, _) =>
                {
                    selections++;
                    Assert.False(store.HasReceipt);
                    return Task.FromResult<string?>(mode == "games" ? folder.FullName : null);
                },
            };
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.Rows[0].IsSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            var failure = Assert.Single(history.Records);
            Assert.Equal(HistoryStatus.Failure, failure.Status);
            Assert.Contains("antes do lançamento", failure.Message);
            Assert.Equal(mode is "dismiss" or "games" ? 1 : 0, selections);
            Assert.Empty(source.Launches);
            Assert.False(store.HasReceipt || vm.BlocksPackageChanges || vm.IsUpdating);
            Assert.True(vm.UpdateSelectedCommand.CanExecute(null));

            vm.Rows[0].IsSelected = false;
            vm.Rows[1].IsSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);
            Assert.Equal("Fixture.Next", Assert.Single(source.Launches)["--id"]);
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Unknown_BattleNet_inventory_reaches_manifest_preflight_and_runs_only_with_consent(bool changeFolder) =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = NewStore(settings);
            var registered = LauncherFolder(settings, "Registered launcher");
            var selected = changeFolder ? LauncherFolder(settings, "Other launcher folder") : registered;
            var source = new InstallerLab(BattleNet("Unknown")) { ObservedVersion = "Unknown", Installation = Installation(registered) };
            var steps = new List<string>();
            var vm = new UpdatesViewModel(source.Service, new RecoveryHistory(), settings, store)
            {
                ConfirmUnknownBattleNetUpdate = (title, message) =>
                {
                    Assert.False(store.HasReceipt);
                    Assert.Equal(1, source.ManifestReads);
                    Assert.Equal("Versão instalada não identificada", title);
                    Assert.Contains("2.53.8.17986", message);
                    Assert.Contains("2.0.0.4232", message);
                    Assert.Contains(registered, message);
                    Assert.Contains("pode executar novamente o instalador", message);
                    steps.Add("consent");
                    return true;
                },
                ChooseBattleNetUpdateLocation = (package, suggestion) =>
                {
                    Assert.False(store.HasReceipt);
                    Assert.Equal("Unknown", package.InstalledVersion);
                    Assert.Equal(registered, suggestion);
                    steps.Add("directory");
                    return selected;
                },
            };
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(("Blizzard.BattleNet", "winget", "Unknown"),
                (vm.Rows[0].Package.Id, vm.Rows[0].Package.Source, vm.Rows[0].Package.InstalledVersion));
            vm.Rows[0].IsSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            Assert.Equal(["consent", "directory"], steps);
            Assert.Equal(1, source.ManifestReads);
            Assert.Equal(new[]
            {
                "upgrade", "--id", "Blizzard.BattleNet", "--exact", "--version", "2.0.0.4232",
                "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements",
                "--source", "winget", "--include-unknown", "--location", selected
            }, Assert.Single(source.UpgradeArguments));
            Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
            Assert.Equal(UpdateRowStatus.Interrupted, vm.Rows[0].Status);
        });

    [Theory]
    [InlineData("cancel-consent", 1, 0, false)]
    [InlineData("cancel-folder", 1, 1, false)]
    [InlineData("nonexistent", 1, 1, false)]
    [InlineData("games", 1, 1, false)]
    [InlineData("wrong-publisher", 1, 1, false)]
    [InlineData("wrong-company", 0, 0, false)]
    [InlineData("different-launcher", 1, 1, false)]
    [InlineData("manifest-unavailable", 0, 0, false)]
    [InlineData("manifest-version", 0, 0, false)]
    [InlineData("manifest-product", 0, 0, false)]
    [InlineData("duplicate-registration", 0, 0, false)]
    [InlineData("duplicate-inventory", 1, 1, true)]
    [InlineData("source-changed", 1, 1, true)]
    [InlineData("launcher-changed-at-launch", 1, 1, true)]
    [InlineData("receipt", 0, 0, false)]
    public Task Unknown_BattleNet_refuses_before_launch_and_recovers_controls(string scenario, int confirmations, int selections,
        bool expectsWinGetQueries) => WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = NewStore(settings);
            var registered = LauncherFolder(settings, "Registered launcher");
            var alternate = LauncherFolder(settings, "Unregistered folder", scenario == "different-launcher" ? "different bytes" : "fixture launcher");
            var games = Directory.CreateDirectory(Path.Combine(settings.DataDirectory, "Games")).FullName;
            var package = BattleNet("Unknown");
            var source = new InstallerLab(package)
            {
                Installation = Installation(registered, scenario, alternate),
                ObservedVersion = "Unknown",
                MissingManifest = scenario == "manifest-unavailable",
                InstallerManifest = scenario switch
                {
                    "manifest-version" => Manifest.Replace("2.0.0.4232", "2.0.0.1"),
                    "manifest-product" => Manifest.Replace("ProductCode: Battle.net", "ProductCode: Other"),
                    _ => Manifest,
                },
                DuplicateInstalledRow = scenario == "duplicate-inventory",
            };
            source.BeforeUpgradeSourceChecks = () =>
            {
                if (scenario == "launcher-changed-at-launch")
                    File.WriteAllText(Path.Combine(registered, "Battle.net.exe"), "replacement bytes");
            };
            var history = new RecoveryHistory();
            var prompts = 0;
            var folders = 0;
            var vm = new UpdatesViewModel(source.Service, history, settings, store)
            {
                ConfirmUnknownBattleNetUpdate = (_, _) =>
                {
                    prompts++;
                    Assert.False(store.HasReceipt);
                    if (scenario == "source-changed") source.UntrustedWingetSource = true;
                    return scenario != "cancel-consent";
                },
                ChooseBattleNetUpdateLocation = (_, _) =>
                {
                    folders++;
                    Assert.False(store.HasReceipt);
                    return scenario switch
                    {
                        "cancel-folder" => null,
                        "nonexistent" => Path.Combine(settings.DataDirectory, "Missing folder"),
                        "games" => games,
                        "wrong-publisher" or "different-launcher" => alternate,
                        _ => registered,
                    };
                },
            };
            await vm.RefreshCommand.ExecuteAsync(null);
            var scanCommands = source.Commands;
            byte[]? receiptBytes = null;
            var receiptPath = Path.Combine(settings.DataDirectory, "winget-update.json");
            if (scenario == "receipt")
            {
                using (store.Begin(package)) { }
                receiptBytes = File.ReadAllBytes(receiptPath);
            }

            vm.Rows[0].IsSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            Assert.Equal(confirmations, prompts);
            Assert.Equal(selections, folders);
            Assert.Empty(source.UpgradeArguments);
            Assert.Equal(expectsWinGetQueries, source.Commands > scanCommands);
            Assert.False(vm.IsUpdating);
            if (receiptBytes is not null)
            {
                Assert.Equal(receiptBytes, File.ReadAllBytes(receiptPath));
                Assert.Empty(history.Records);
                Assert.True(vm.BlocksPackageChanges);
                Assert.False(vm.UpdateSelectedCommand.CanExecute(null));
                return;
            }
            var failure = Assert.Single(history.Records);
            Assert.Equal(HistoryStatus.Failure, failure.Status);
            Assert.Contains("antes do lançamento", failure.Message);
            Assert.False(File.Exists(receiptPath) || store.HasReceipt || vm.BlocksPackageChanges);
            Assert.True(vm.UpdateSelectedCommand.CanExecute(null));
        });

    [Theory]
    [InlineData("Blizzard.BattleNetXY", "winget")]
    [InlineData("blizzard.battlenet", "winget")]
    [InlineData("Blizzard.BattleNet", "msstore")]
    [InlineData("Blizzard.BattleNet", "thirdparty")]
    [InlineData("Fixture.Unknown", "winget")]
    public async Task Unknown_exception_never_applies_to_another_id_or_source(string id, string source)
    {
        var package = new WinGetPackage
        {
            Name = "Battle.net", Id = id, Source = source, InstalledVersion = "Unknown", AvailableVersion = "2.0.0.4232"
        };
        var location = LauncherFolder(new TestSettings(), "Registered launcher");
        var lab = new InstallerLab(package) { Installation = Installation(location) };
        IControlledWinGetService service = lab.Service;
        var control = new WinGetProcessControl
        {
            ConfirmBattleNetLocation = (_, _, _) => throw new InvalidOperationException("Unexpected consent"),
        };
        Assert.True((await service.PrepareUpgradeAsync(package, control, default))?.StartFailed);
        Assert.Equal(0, lab.ManifestReads);
        Assert.Equal(0, lab.Commands);
        Assert.Null(WinGetService.BuildUpgradeStartInfo("fixture", package, location,
            new(package, lab.Installation.Find(BattleNet("Unknown"))!, location)));
    }

    [Fact]
    public void Unknown_command_rejects_missing_consent_and_consent_for_another_target_or_folder()
    {
        var package = BattleNet("Unknown");
        var location = LauncherFolder(new TestSettings(), "Registered launcher");
        var installation = Installation(location).Find(package)!;
        Assert.Null(WinGetService.BuildUpgradeStartInfo("fixture", package, location));
        Assert.Null(WinGetService.BuildUpgradeStartInfo("fixture", package, location, new(BattleNet("Unknown"), installation, location)));
        Assert.Null(WinGetService.BuildUpgradeStartInfo("fixture", package, location + " different", new(package, installation, location)));
    }

    [Fact]
    public Task Valid_launcher_directory_is_a_single_location_argument_without_a_hidden_prompt() =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = NewStore(settings);
            var source = new InstallerLab(BattleNet());
            var folder = Directory.CreateDirectory(Path.Combine(settings.DataDirectory, "Launcher folder"));
            File.WriteAllText(Path.Combine(folder.FullName, "Battle.net Launcher.exe"), "synthetic marker");
            var vm = new UpdatesViewModel(source.Service, new RecoveryHistory(), settings, store)
            {
                ChooseUpdateLocation = (_, _) => Task.FromResult<string?>(folder.FullName),
            };
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.Rows[0].IsSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            var command = Assert.Single(source.Launches);
            Assert.Equal(folder.FullName, command["--location"]);
            Assert.Contains("--disable-interactivity", command.Keys);
            Assert.DoesNotContain("--interactive", command.Keys);
            Assert.DoesNotContain("--include-unknown", command.Keys);
            Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
        });

    [Fact]
    public Task History_failure_during_preflight_never_blocks_package_actions() =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = NewStore(settings);
            var source = new InstallerLab(BattleNet());
            var history = new RecoveryHistory { Fail = true };
            var vm = new UpdatesViewModel(source.Service, history, settings, store)
            {
                ChooseUpdateLocation = (_, _) => Task.FromResult<string?>(null),
            };
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.Rows[0].IsSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            Assert.Equal(1, history.Writes);
            Assert.False(store.HasReceipt || vm.BlocksPackageChanges);
            Assert.True(vm.UpdateSelectedCommand.CanExecute(null));
        });

    [Theory]
    [InlineData("Prepared")]
    [InlineData("NotStarted")]
    [InlineData("LaunchPending")]
    public Task Reopening_distinguishes_positive_prelaunch_proof_from_crash_at_launch(string stageName) =>
        WpfTestThread.Run(async () =>
        {
            var stage = Enum.Parse<WinGetReceiptStage>(stageName);
            var settings = new TestSettings();
            var store = NewStore(settings, () => WinGetOperationTests.BootBefore);
            using (var receipt = store.BeginPrepared(BattleNet()))
            {
                if (stage == WinGetReceiptStage.NotStarted) receipt.MarkNotStarted();
                if (stage == WinGetReceiptStage.LaunchPending)
                {
                    var control = new WinGetProcessControl();
                    receipt.Bind(control);
                    control.PersistLaunchIntent!();
                }
            }
            var service = new WinGetOperationFixture(1);
            var vm = new UpdatesViewModel(service, new RecoveryHistory(), settings, store);
            Assert.True(vm.BlocksPackageChanges);
            await vm.RecheckInterruptionAsync();

            var ambiguous = stage == WinGetReceiptStage.LaunchPending;
            Assert.Equal(ambiguous, store.HasReceipt);
            Assert.Equal(ambiguous, vm.BlocksPackageChanges);
            Assert.False(vm.ShowInterruptionReview);
            Assert.Empty(service.Launched);
            if (ambiguous)
            {
                Assert.Contains("Blizzard.BattleNet", vm.InterruptionTarget);
                Assert.Contains("início do processo ficou inconclusivo", vm.InterruptionDetails);
            }
        });

    [Theory]
    [InlineData("legacy-anonymous")]
    [InlineData("malformed")]
    [InlineData("oversized")]
    public Task Anonymous_or_unreadable_receipt_requires_later_boot_inventory_and_explicit_review(string format) =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var before = DateTime.UtcNow.AddMinutes(-10);
            var stamp = before.AddMinutes(1);
            var after = stamp.AddMinutes(1);
            var boot = before.Ticks.ToString(CultureInfo.InvariantCulture);
            var store = NewStore(settings, () => boot);
            var path = Path.Combine(Directory.CreateDirectory(settings.DataDirectory).FullName, "winget-update.json");
            var payload = format switch
            {
                "legacy-anonymous" => "null\nnull\nnull",
                "oversized" => new string('x', 16_385),
                _ => "truncated {",
            };
            File.WriteAllText(path, payload);
            File.SetCreationTimeUtc(path, stamp);
            File.SetLastWriteTimeUtc(path, stamp);
            var source = new InstallerLab(BattleNet()) { InventoryUnavailable = true };
            var history = new RecoveryHistory();
            var vm = new UpdatesViewModel(source.Service, history, settings, store);

            await vm.RecheckInterruptionAsync();
            Assert.False(vm.ShowInterruptionReview);
            Assert.Equal(payload, File.ReadAllText(path));
            boot = after.Ticks.ToString(CultureInfo.InvariantCulture);
            await vm.RecheckInterruptionAsync();
            Assert.False(vm.ShowInterruptionReview);
            Assert.Contains("Indisponível", vm.InterruptionDetails);
            source.InventoryUnavailable = false;
            await vm.RecheckInterruptionAsync();
            Assert.Contains("sem aplicativo identificado", vm.InterruptionTarget);
            Assert.Contains("sem associação ao recibo", vm.InterruptionDetails);
            Assert.True(vm.ShowInterruptionReview && store.HasReceipt);

            vm.ConfirmInterruptionReview = (_, _) => false;
            await vm.ReviewInterruptionCommand.ExecuteAsync(null);
            Assert.Equal(payload, File.ReadAllText(path));
            Assert.Empty(history.Records);

            vm.ConfirmInterruptionReview = (_, _) => true;
            history.Fail = true;
            await vm.ReviewInterruptionCommand.ExecuteAsync(null);
            Assert.True(store.HasReceipt);
            Assert.Empty(history.Records);
            Assert.Contains("Histórico", vm.InterruptionNote);

            history.Fail = false;
            await vm.RecheckInterruptionAsync();
            await vm.ReviewInterruptionCommand.ExecuteAsync(null);
            Assert.False(store.HasReceipt || vm.BlocksPackageChanges);
            var entry = Assert.Single(history.Records);
            Assert.Contains("SHA-256", entry.Details);
            Assert.Equal(HistoryStatus.Interrupted, entry.Status);
        });

    [Theory]
    [InlineData("WinCoreManager.PackageChange/v2")]
    [InlineData("damaged-header")]
    public Task Corrupt_receipt_with_readable_active_delegate_cannot_be_released(string header) =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var store = NewStore(settings, () => DateTime.UtcNow.AddMinutes(-1).Ticks.ToString(CultureInfo.InvariantCulture));
            var folder = Directory.CreateDirectory(settings.DataDirectory);
            var path = Path.Combine(folder.FullName, "winget-update.json");
            var marker = Path.Combine(folder.FullName, "marker");
            var release = Path.Combine(folder.FullName, "release");
            var fixture = WinGetCancellationTests.Fixture("wait-for-release");
            fixture.ArgumentList.Add(marker);
            fixture.ArgumentList.Add(release);
            using var child = Process.Start(fixture)!;
            try
            {
                await WinGetCancellationTests.WaitUntilAsync(() => File.Exists(marker));
                var identity = JsonSerializer.Serialize(new WinGetProcessIdentity((uint)child.Id, child.StartTime.ToFileTimeUtc()));
                File.WriteAllText(path, $$"""
                    null
                    null
                    null
                    "{{header}}"
                    {"Operation":{"Delegates":[{{identity}}],"Target":
                    """);
                var stamp = DateTime.UtcNow.AddMinutes(-2);
                File.SetCreationTimeUtc(path, stamp);
                File.SetLastWriteTimeUtc(path, stamp);
                var vm = new UpdatesViewModel(new WinGetOperationFixture(1), new RecoveryHistory(), settings, store);
                await vm.RecheckInterruptionAsync();
                Assert.Contains("Atividade confirmada", vm.InterruptionDetails);
                Assert.True(store.HasReceipt && vm.BlocksPackageChanges);
                Assert.False(vm.ShowInterruptionReview);
                File.WriteAllText(release, string.Empty);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                await vm.RecheckInterruptionAsync();
                Assert.True(vm.ShowInterruptionReview);
                Assert.Contains("Encerramento confirmado", vm.InterruptionDetails);
            }
            finally { File.WriteAllText(release, string.Empty); }
        });

    [Fact]
    public Task Resolved_ambiguous_receipt_allows_next_proven_update() =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var boot = WinGetOperationTests.BootBefore;
            var store = NewStore(settings, () => boot);
            using (store.Begin(BattleNet())) { }
            var service = new WinGetOperationFixture(1)
            {
                Run = (_, control, _) =>
                {
                    control.RootIdentity = new(int.MaxValue, 1);
                    return Task.FromResult(new ProcessRunResult { Observation = WinGetOperationFixture.Complete });
                },
            };
            var history = new RecoveryHistory();
            var vm = new UpdatesViewModel(service, history, settings, store) { ConfirmInterruptionReview = (_, _) => true };
            await vm.RecheckInterruptionAsync();
            Assert.False(vm.ShowInterruptionReview);
            boot = WinGetOperationTests.BootAfter;
            await vm.RecheckInterruptionAsync();
            await vm.ReviewInterruptionCommand.ExecuteAsync(null);
            Assert.False(store.HasReceipt);
            vm.AllSelected = true;
            await vm.UpdateSelectedCommand.ExecuteAsync(null);

            Assert.Single(service.Launched);
            Assert.Equal(new[] { HistoryStatus.Interrupted, HistoryStatus.Success }, history.Records.Select(r => r.Status));
            Assert.False(store.HasReceipt || vm.BlocksPackageChanges);
        });

    [Fact]
    public Task Unknown_installed_version_is_evidence_only_for_supervised_recovery() =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var boot = WinGetOperationTests.BootBefore;
            var store = NewStore(settings, () => boot);
            var package = BattleNet("Unknown");
            using (store.Begin(package)) { }
            var source = new InstallerLab(package) { ObservedVersion = "Unknown" };
            var service = source.Service;
            Assert.Null(await ((IControlledWinGetService)service).ReadInstalledVersionAsync(package));
            var history = new RecoveryHistory();
            var vm = new UpdatesViewModel(service, history, settings, store) { ConfirmInterruptionReview = (_, _) => true };

            boot = WinGetOperationTests.BootAfter;
            await vm.RecheckInterruptionAsync();
            Assert.Contains("versão instalada é desconhecida", vm.InterruptionDetails);
            Assert.True(vm.ShowInterruptionReview && store.HasReceipt);
            await vm.ReviewInterruptionCommand.ExecuteAsync(null);
            Assert.False(store.HasReceipt);
            Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
            Assert.Empty(source.Launches);
        });

    [Fact]
    public Task Consent_cannot_release_a_replaced_receipt_with_the_same_inventory_version() =>
        WpfTestThread.Run(async () =>
        {
            var settings = new TestSettings();
            var boot = WinGetOperationTests.BootBefore;
            var store = NewStore(settings, () => boot);
            var package = BattleNet();
            using (store.Begin(package)) { }
            var history = new RecoveryHistory();
            var vm = new UpdatesViewModel(new WinGetOperationFixture(1), history, settings, store)
            {
                ConfirmInterruptionReview = (_, _) =>
                {
                    using (var current = store.Open()) current.Clear();
                    using (NewStore(settings).Begin(package)) { }
                    return true;
                },
            };
            boot = WinGetOperationTests.BootAfter;
            await vm.RecheckInterruptionAsync();
            Assert.True(vm.ShowInterruptionReview);
            await vm.ReviewInterruptionCommand.ExecuteAsync(null);
            Assert.True(store.HasReceipt);
            Assert.Empty(history.Records);
        });

    [Fact]
    public Task Recovery_details_show_process_and_inventory_status_in_the_selected_language() =>
        WpfTestThread.Run(async () =>
        {
            var locale = LocalizationService.Current;
            var original = locale.CultureName;
            locale.Apply(LocalizationService.English);
            try
            {
                var settings = new TestSettings();
                var store = NewStore(settings);
                using (store.Begin(BattleNet())) { }
                var vm = new UpdatesViewModel(new WinGetOperationFixture(1), new RecoveryHistory(), settings, store);
                await vm.RecheckInterruptionAsync();

                Assert.Contains("Pending operation: Battle.net (Blizzard.BattleNet)", vm.PackageChangeBlockReason);
                Assert.Contains("Main process: No evidence available", vm.InterruptionDetails);
                Assert.Contains("Inventory: Observed version: 2.0", vm.InterruptionDetails);
            }
            finally { locale.Apply(original); }
        });

    private static WinGetInterruptionStore NewStore(TestSettings settings, Func<string?>? boot = null) =>
        WinGetOperationTests.ReviewStore(settings, boot ?? (() => WinGetOperationTests.BootBefore));

    private static string LauncherFolder(TestSettings settings, string name, string content = "fixture launcher")
    {
        var directory = Directory.CreateDirectory(Path.Combine(settings.DataDirectory, name)).FullName;
        File.WriteAllText(Path.Combine(directory, "Battle.net.exe"), content);
        return directory;
    }

    private static WinGetBattleNetInstallation Installation(string registered, string? scenario = null, string? alternate = null)
    {
        var registration = new BattleNetRegistration("Battle.net", "Blizzard Entertainment", registered);
        return new(() => scenario == "duplicate-registration" ? [registration, registration] : [registration],
            file => new(scenario == "wrong-company" ? "Other company" : "Blizzard Entertainment",
                scenario == "wrong-publisher" && Path.GetDirectoryName(file) == alternate ? "Other company" : "Blizzard Entertainment, Inc.",
                "2.53.8.17986"));
    }

    private sealed class InstallerLab(WinGetPackage package)
    {
        private const string Winget = """
            {"Name":"winget","Arg":"https://cdn.winget.microsoft.com/cache","Type":"Microsoft.PreIndexed.Package","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","TrustLevel":["Trusted","StoreOrigin"]}
            """;
        private const string Store = """
            {"Name":"msstore","Arg":"https://storeedgefd.dsx.mp.microsoft.com/v9.0","Type":"Microsoft.Rest","Identifier":"StoreEdgeFD","TrustLevel":["Trusted"]}
            """;
        internal bool MissingManifest { get; set; }
        internal string InstallerManifest { get; init; } = Manifest;
        internal int ManifestReads { get; private set; }
        internal bool InventoryUnavailable { get; set; }
        internal string ObservedVersion { get; set; } = "2.0";
        internal WinGetBattleNetInstallation? Installation { get; init; }
        internal bool DuplicateInstalledRow { get; init; }
        internal bool UntrustedWingetSource { get; set; }
        internal Action? BeforeUpgradeSourceChecks { get; set; }
        internal int Commands { get; private set; }
        private bool _listedInstalled;
        internal List<Dictionary<string, string>> Launches { get; } = [];
        internal List<string[]> UpgradeArguments { get; } = [];
        internal WinGetService Service => new(() => "fixture-alias", RunAsync, () => false,
            readLocationRequirement: (item, _) =>
            {
                ManifestReads++;
                return Task.FromResult(item.Id == package.Id
                    ? MissingManifest ? null : WinGetInstallerRequirements.ParseLocationRequirement(InstallerManifest, item) : false);
            }, battleNetInstallation: Installation);

        private async Task<ProcessRunResult> RunAsync(ProcessStartInfo command, Action<string>? output,
            CancellationToken token, WinGetProcessControl? control, bool externalInstallerPossible)
        {
            Commands++;
            string Get(string option) => command.ArgumentList[command.ArgumentList.IndexOf(option) + 1];
            static ProcessRunResult Lines(params string[] lines) => new() { Output = [.. lines] };
            static string Row(string name, string id, string version, string available = "") =>
                $"{name,-18}{id,-28}{version,-16}{available}";
            var divider = new string('-', 76);
            switch (command.ArgumentList[0])
            {
                case "--version": return Lines("v1.12.470");
                case "source":
                    if (_listedInstalled)
                    {
                        BeforeUpgradeSourceChecks?.Invoke();
                        BeforeUpgradeSourceChecks = null;
                    }
                    return Lines(Get("--name") == "winget" ? UntrustedWingetSource ? "{}" : Winget : Store);
                case "list":
                    _listedInstalled = true;
                    var header = Row("Name", "Id", "Version");
                    var installed = Row("Fixture", Get("--id"), ObservedVersion);
                    return DuplicateInstalledRow ? Lines(header, divider, installed, installed) : Lines(header, divider, installed);
                case "upgrade" when !command.ArgumentList.Contains("--id"):
                    return InventoryUnavailable ? new() { ExitCode = 9 } : Get("--source") == "msstore" ? Lines() :
                        Lines(Row("Name", "Id", "Version", "Available"), divider,
                            Row("Alpha", package.Id, package.InstalledVersion, package.AvailableVersion),
                            Row("Beta", "Fixture.Next", "1.0", "2.0"));
            }
            Assert.Equal("upgrade", command.ArgumentList[0]);
            var args = command.ArgumentList;
            UpgradeArguments.Add([.. args]);
            Launches.Add(args.Where(arg => arg.StartsWith("--", StringComparison.Ordinal))
                .ToDictionary(arg => arg, arg => args.IndexOf(arg) + 1 < args.Count ? Get(arg) : ""));
            return await ProcessRunner.RunWinGetAsync(WinGetCancellationTests.Fixture("success"), output,
                token, control, externalInstallerPossible);
        }
    }

    private sealed class RecoveryHistory : IHistoryService
    {
        internal List<HistoryRecord> Records { get; } = [];
        internal bool Fail { get; set; }
        internal int Writes { get; private set; }
        public Task AddAsync(HistoryRecord record)
        {
            Writes++;
            if (Fail) throw new IOException("synthetic History failure");
            Records.Add(record);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryFilter filter) => throw new NotSupportedException();
        public Task<IReadOnlyList<HistoryRecord>> RecentAsync(int count) => throw new NotSupportedException();
        public Task<int> ClearAsync() => throw new NotSupportedException();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => throw new NotSupportedException();
    }
}
