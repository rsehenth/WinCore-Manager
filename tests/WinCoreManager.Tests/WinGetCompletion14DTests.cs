using System.Diagnostics;
using System.IO;
using System.Text;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public sealed class WinGetCompletion14DTests
{
    private const string GitHub = "GitHub.cli";
    private const string Docker = "Docker.DockerDesktop";
    private const string Terminal = "Microsoft.WindowsTerminal";
    private const string Preview = "Microsoft.WindowsTerminal.Preview";
    private const string Previous = "2.105.0";
    private const string Target = "2.106.0";

    [Fact]
    public Task GitHub_cli_21050_to_21060_can_complete_after_verified_msi_transaction() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(GitHub);
        await lab.RunAsync();
        var success = Assert.Single(lab.History.Records);
        Assert.Equal(HistoryStatus.Success, success.Status);
        Assert.Equal(Previous, success.VersionFrom);
        Assert.Equal(Target, success.VersionTo);
        Assert.True(lab.History.ReceiptPresentAtSuccess);
        Assert.False(lab.Store.HasReceipt);
    });

    [Fact]
    // Opus structural review: the generic EXE case. Exit 0, the Job Object and known delegates ended,
    // no MSI transaction and a fresh inventory at the target version complete the item and the queue moves on.
    public Task Docker_success_and_fresh_inventory_complete_under_the_generic_contract() => WpfTestThread.Run(async () =>
    {
        var items = new[] { Docker, GitHub }.Concat(Enumerable.Range(1, 8).Select(i => $"Fixture.App{i}")).ToArray();
        var lab = new Lab(items);
        await lab.RunAsync();
        Assert.Equal(items, lab.Launches);
        Assert.Equal(10, lab.History.Records.Count(record => record.Status == HistoryStatus.Success));
        Assert.False(lab.Store.HasReceipt);
        Assert.Equal(0, lab.ViewModel.SuspendedUpdateCount);
    });

    // The MSI log profile only adds evidence now (restart detection, CompletionVerified label). A missing
    // or foreign log no longer holds an update whose generic completion evidence is complete.
    [Theory]
    [InlineData("missing-log")]
    [InlineData("truncated-log")]
    [InlineData("wrong-package")]
    [InlineData("no-server-return")]
    [InlineData("unrecognized-predecessor")]
    [InlineData("transformed")]
    [InlineData("registration-mismatch")]
    [InlineData("manifest-mismatch")]
    public Task Msi_log_profile_is_evidence_not_a_gate(string scenario) => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(GitHub, Terminal) { Scenario = scenario };
        await lab.RunAsync();
        Assert.Equal(new[] { GitHub, Terminal }, lab.Launches);
        Assert.Equal(HistoryStatus.Success, lab.History.Records[0].Status);
        Assert.False(lab.Store.HasReceipt);
    });

    [Theory]
    [InlineData("reboot-log", nameof(WinGetPendingReason.RestartRequired))]
    [InlineData("reboot-exit", nameof(WinGetPendingReason.RestartRequired))]
    [InlineData("reboot-warning", nameof(WinGetPendingReason.RestartRequired))]
    [InlineData("version-mismatch", nameof(WinGetPendingReason.InventoryMismatch))]
    [InlineData("duplicate-inventory", nameof(WinGetPendingReason.InventoryUnavailable))]
    [InlineData("missing-identity", nameof(WinGetPendingReason.ReceiptIncomplete))]
    [InlineData("detached-delegate", nameof(WinGetPendingReason.Active))]
    public Task Negative_evidence_keeps_the_receipt_and_prevents_another_launch(string scenario, string expectedReason) =>
        WpfTestThread.Run(async () =>
        {
            var expected = Enum.Parse<WinGetPendingReason>(expectedReason);
            var lab = new Lab(GitHub, Terminal) { Scenario = scenario };
            try
            {
                await lab.RunAsync();
                Assert.Equal(new[] { GitHub }, lab.Launches);
                Assert.Equal(HistoryStatus.Interrupted, Assert.Single(lab.History.Records).Status);
                Assert.Equal(1, lab.ViewModel.SuspendedUpdateCount);
                using var receipt = lab.Store.OpenForReview();
                Assert.Equal(expected, receipt.Outcome?.PendingReason);
                Assert.Equal(expected == WinGetPendingReason.RestartRequired, receipt.Outcome?.RestartRequired);
                if (scenario == "missing-identity") Assert.Null(receipt.Identity);
                if (scenario == "detached-delegate") Assert.NotEmpty(receipt.Delegates);
                var eligible = PackageChangeReview.CanReviewWithoutRestart(receipt, lab.Boot,
                    PackageChangeReview.ObserveActivity(receipt, lab.Boot));
                Assert.Equal(expected is WinGetPendingReason.InventoryMismatch or WinGetPendingReason.InventoryUnavailable, eligible);
            }
            finally { if (scenario == "detached-delegate") File.WriteAllText(lab.Release, "release"); }
        });

    [Fact]
    public Task Running_supervised_installer_keeps_its_receipt_until_it_exits() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(GitHub, Terminal) { Scenario = "active-installer" };
        var run = lab.RunAsync();
        try
        {
            await WinGetCancellationTests.WaitUntilAsync(() => File.Exists(lab.Marker));
            Assert.True(lab.Store.HasReceipt);
            Assert.Equal(new[] { GitHub }, lab.Launches);
            Assert.Empty(lab.History.Records);
            Assert.False(run.IsCompleted);
        }
        finally
        {
            File.WriteAllText(lab.Release, "release");
            await run;
        }
        Assert.Equal(new[] { GitHub, Terminal }, lab.Launches);
        Assert.False(lab.Store.HasReceipt);
    });

    [Fact]
    public Task Same_session_review_requires_proof_fresh_inventory_consent_and_explicit_resume() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(GitHub, Terminal) { Scenario = "version-mismatch" };
        await lab.RunAsync();
        await lab.ViewModel.RecheckInterruptionAsync(userRequested: true);
        Assert.True(lab.Store.HasReceipt);
        Assert.Equal(Previous, lab.ViewModel.ObservedVersionForReview);
        lab.ViewModel.ConfirmInterruptionReview = (_, _) => false;
        await lab.ViewModel.DismissWarningCommand.ExecuteAsync(null);
        Assert.True(lab.Store.HasReceipt);
        lab.ViewModel.ConfirmInterruptionReview = (_, _) => true;
        await lab.ViewModel.DismissWarningCommand.ExecuteAsync(null);
        Assert.False(lab.Store.HasReceipt);
        Assert.True(lab.ViewModel.ShowResumeUpdates);
        Assert.Equal(new[] { GitHub }, lab.Launches);
        Assert.Equal(HistoryStatus.Interrupted, lab.History.Records.Last().Status);
        await lab.ViewModel.ResumeUpdatesCommand.ExecuteAsync(null);
        Assert.Equal(new[] { GitHub, Terminal }, lab.Launches);
    });

    [Fact]
    public Task Audit_duplicate_inventory_rows_allow_a_supervised_release_instead_of_a_permanent_block() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(GitHub, Terminal) { Scenario = "duplicate-inventory" };
        await lab.RunAsync();
        Assert.True(lab.Store.HasReceipt);
        await lab.ViewModel.RecheckInterruptionAsync(userRequested: true);
        Assert.True(lab.Store.HasReceipt && lab.ViewModel.ShowInterruptionReview);
        Assert.Contains(WinCoreManager.Localization.LocalizationService.Source("UpdateOperation.RecoveryInventoryUncorrelated"),
            lab.ViewModel.InterruptionNote);
        lab.ViewModel.ConfirmInterruptionReview = (_, _) => true;
        await lab.ViewModel.ReviewInterruptionCommand.ExecuteAsync(null);
        Assert.False(lab.Store.HasReceipt || lab.ViewModel.BlocksPackageChanges);
        Assert.Equal(HistoryStatus.Interrupted, lab.History.Records.Last().Status);
        Assert.True(lab.ViewModel.ShowResumeUpdates);
        Assert.Equal(new[] { GitHub }, lab.Launches);
    });

    [Fact]
    public Task History_failure_keeps_the_receipt_and_stops_the_queue() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(GitHub, Terminal) { Scenario = "history-failure" };
        await lab.RunAsync();
        Assert.Equal(new[] { GitHub }, lab.Launches);
        Assert.Empty(lab.History.Records);
        Assert.True(lab.History.ReceiptPresentAtSuccess);
        using var receipt = lab.Store.OpenForReview();
        Assert.Equal(WinGetPendingReason.HistoryUnavailable, receipt.Outcome?.PendingReason);
    });

    [Theory]
    [InlineData("x64", "wix", "winget", true)]
    [InlineData("arm64", "wix", "winget", false)]
    [InlineData("x64", "exe", "winget", false)]
    [InlineData("x64", "wix", "msstore", false)]
    public void Only_the_reviewed_wix_catalog_entry_selects_the_bounded_path(string architecture, string type, string source, bool accepted)
    {
        var package = new WinGetPackage { Id = GitHub, Name = "GitHub CLI", InstalledVersion = Previous, AvailableVersion = Target, Source = source };
        var content = $"""
            PackageIdentifier: GitHub.cli
            PackageVersion: 2.106.0
            ManifestType: installer
            Installers:
            - Architecture: {architecture}
              InstallerType: {type}
              Scope: machine
              UpgradeBehavior: install
              ProductCode: '{WinGetMsiUpgrade.TargetProduct}'
              InstallerUrl: https://github.com/cli/cli/releases/download/v2.106.0/gh_2.106.0_windows_amd64.msi
              InstallerSha256: 6C82AB3E6FDF733B0467BB6F18CD87CA372D0B919AA9166E7E46E057948EF3CE
            - Architecture: x64
              InstallerType: zip
              NestedInstallerType: portable
            """;
        Assert.Equal(accepted ? false : null,
            WinGetInstallerRequirements.ParseLocationRequirement(content, package, requireAuditedMsi: true));
        Assert.Null(WinGetInstallerRequirements.ParseLocationRequirement(content.Replace("6C82AB3", "7C82AB3", StringComparison.Ordinal),
            package, requireAuditedMsi: true));
    }

    [Fact]
    public Task Ten_item_batch_advances_across_msi_msix_and_exe_items() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab([GitHub, Terminal, Preview, Docker, .. Enumerable.Range(1, 6).Select(i => $"Fixture.App{i}")]);
        await lab.RunAsync();
        Assert.Equal(10, lab.Launches.Count);
        Assert.Equal(10, lab.History.Records.Count(record => record.Status == HistoryStatus.Success));
        Assert.Equal(0, lab.ViewModel.SuspendedUpdateCount);
        Assert.False(lab.Store.HasReceipt);
    });

    private sealed class Lab
    {
        private const string OfficialSource = """
            {"Name":"winget","Arg":"https://cdn.winget.microsoft.com/cache","Type":"Microsoft.PreIndexed.Package","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","TrustLevel":["Trusted","StoreOrigin"]}
            """;
        private readonly TestSettings _settings = new();
        private readonly string[] _items;
        internal readonly List<string> Launches = [];
        internal readonly HistoryProbe History;
        internal WinGetInterruptionStore Store { get; }
        internal UpdatesViewModel ViewModel { get; }
        internal string Boot => "100";
        internal string Marker => Path.Combine(_settings.DataDirectory, "installer-active");
        internal string Release => Marker + ".release";
        internal string Scenario { get; init; } = "success";

        internal Lab(params string[] items)
        {
            _items = items;
            Store = new(_settings.DataDirectory, () => Boot);
            History = new(Store, () => Scenario == "history-failure");
            var service = new WinGetService(() => "synthetic-alias", RunCommandAsync, () => false, ReadRegisteredPackages,
                readMsiMetadata: (_, _) => Task.FromResult(Scenario != "manifest-mismatch"), readMsiRegistration: ReadMsiRegistration);
            ViewModel = new(service, History, _settings, Store);
        }

        private MsiProductRegistration? ReadMsiRegistration(string family)
        {
            Assert.Equal(WinGetMsiUpgrade.UpgradeCode, family);
            return !Launches.Contains(GitHub) || Scenario == "registration-mismatch"
                ? new(WinGetMsiUpgrade.PreviousProduct, WinGetMsiUpgrade.PreviousPackage, Previous)
                : new(WinGetMsiUpgrade.TargetProduct, WinGetMsiUpgrade.TargetPackage, Target);
        }

        internal async Task RunAsync()
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(_items.Length, ViewModel.Rows.Count);
            ViewModel.AllSelected = true;
            await ViewModel.UpdateSelectedCommand.ExecuteAsync(null);
        }

        private static (string Old, string New) Versions(string id) => id switch
        {
            GitHub => (Previous, Target),
            Docker => ("4.66.0", "4.67.0"),
            Terminal or Preview => ("1.23.19001.0", "1.24.11911.0"),
            _ => ("1.0.0", "2.0.0"),
        };

        private static string Row(string name, string id, string version, string available = "", string source = "") =>
            $"{name,-20}{id,-42}{version,-18}{available,-18}{source}";

        private IReadOnlyList<string> ReadRegisteredPackages(string family)
        {
            const string publisher = "_8wekyb3d8bbwe";
            var name = family[..^publisher.Length];
            if (name == "Microsoft.UI.Xaml.2.8") return [$"{name}_8.2306.22001.0_x64_{publisher}"];
            if (name == "Microsoft.VCLibs.140.00.UWPDesktop") return [$"{name}_14.0.1.0_x64_{publisher}"];
            var id = name.EndsWith("Preview", StringComparison.Ordinal) ? Preview : Terminal;
            var version = Launches.Contains(id) ? Versions(id).New : Versions(id).Old;
            return [$"{name}_{version}_x64_{publisher}"];
        }

        private Task<ProcessRunResult> RunCommandAsync(ProcessStartInfo command, Action<string>? output,
            CancellationToken token, WinGetProcessControl? control, bool externalInstallerPossible)
        {
            string Argument(string name) => command.ArgumentList[command.ArgumentList.IndexOf(name) + 1];
            static Task<ProcessRunResult> Output(params string[] lines) => Task.FromResult(new ProcessRunResult { Output = [.. lines] });
            switch (command.ArgumentList[0])
            {
                case "--version": return Output("v1.12.470");
                case "source": return Output(Argument("--name") == "winget" ? OfficialSource : "{}");
                case "list":
                    var id = Argument("--id");
                    var version = id == GitHub && Scenario == "version-mismatch" ? Previous : Versions(id).New;
                    var row = Row("Fixture", id, version);
                    return id == GitHub && Scenario == "duplicate-inventory"
                        ? Output(Row("Name", "Id", "Version"), new string('-', 96), row, row)
                        : Output(Row("Name", "Id", "Version"), new string('-', 96), row);
                case "upgrade" when !command.ArgumentList.Contains("--id"):
                    return Output([Row("Name", "Id", "Version", "Available", "Source"), new string('-', 105),
                        .. _items.Select((item, index) => Row($"{index:D2} Fixture", item, Versions(item).Old, Versions(item).New, "winget"))]);
                default:
                    Assert.Equal("upgrade", command.ArgumentList[0]);
                    Assert.NotNull(control);
                    Assert.True(externalInstallerPossible);
                    var item = Argument("--id");
                    Launches.Add(item);
                    var fixture = WinGetCancellationTests.Fixture(Scenario == "reboot-warning" ? "success-with-reboot-warning" : "success");
                    if (item == GitHub && Scenario == "missing-identity") control.PersistRoot = null;
                    if (item == GitHub && Scenario == "reboot-exit")
                    {
                        fixture = WinGetCancellationTests.Fixture("exit-code");
                        fixture.ArgumentList.Add("3010");
                    }
                    if (item == GitHub && Scenario is "active-installer" or "detached-delegate")
                    {
                        fixture = WinGetCancellationTests.Fixture(Scenario == "active-installer" ? "quiet-controlled" : "quiet-delegate");
                        control.DelegateSettleWindow = TimeSpan.FromMilliseconds(500);
                        fixture.ArgumentList.Add(Marker);
                        fixture.ArgumentList.Add(Release);
                    }
                    if (item == GitHub && Scenario != "manifest-mismatch")
                    {
                        Assert.Equal("wix", Argument("--installer-type"));
                        Assert.Equal("x64", Argument("--architecture"));
                        Assert.Equal("winget", Argument("--source"));
                        Assert.Equal(Target, Argument("--version"));
                        Assert.Contains("--exact", command.ArgumentList);
                        Assert.Contains("--skip-dependencies", command.ArgumentList);
                        var log = Assert.IsType<WinGetMsiUpgrade>(control.MsiUpgrade).LogPath;
                        Assert.Equal($"/passive /norestart /l*v \"{log}\"", Argument("--override"));
                        if (Scenario != "missing-log") WriteMsiLog(log);
                    }
                    return ProcessRunner.RunWinGetAsync(fixture, output, token, control, true);
            }
        }

        private void WriteMsiLog(string log)
        {
            var package = Scenario == "wrong-package" ? WinGetMsiUpgrade.PreviousPackage : WinGetMsiUpgrade.TargetPackage;
            var previous = WinGetMsiUpgrade.PreviousProduct;
            if (Scenario == "unrecognized-predecessor") previous += ";{DEADBEEF-0000-0000-0000-000000000000}";
            string[] lines = [
                "=== Verbose logging started: 26/11/2026  10:00:00  Build type: SHIP UNICODE  Calling process: msiexec.exe ===",
                "MSI (s) (64:A0) [10:00:00:503]: MainEngineThread is returning 0", // nested uninstall
                $"Property(S): ProductCode = {WinGetMsiUpgrade.TargetProduct}",
                $"Property(S): PackageCode = {package}",
                $"Property(S): WIX_UPGRADE_DETECTED = {previous}",
                Scenario == "transformed" ? "Property(S): TRANSFORMS = injected.mst" : "Property(S): ALLUSERS = 1",
                Scenario == "no-server-return" ? "" : "MSI (s) (64:D8) [10:00:00:510]: MainEngineThread is returning 0",
                "=== Logging stopped: 26/11/2026  10:00:00 ===",
                $"MSI (c) (24:58) [10:00:00:521]: MainEngineThread is returning {(Scenario == "reboot-log" ? 3010 : 0)}",
                "=== Verbose logging stopped: 26/11/2026  10:00:00 ===",
            ];
            File.WriteAllLines(log, Scenario == "truncated-log" ? lines[..^2] : lines, Encoding.Unicode);
        }
    }

    private sealed class HistoryProbe(WinGetInterruptionStore store, Func<bool> fail) : IHistoryService
    {
        private readonly FakeHistoryService _inner = new();
        internal List<HistoryRecord> Records => _inner.Records;
        internal bool ReceiptPresentAtSuccess { get; private set; }

        public Task AddAsync(HistoryRecord record)
        {
            if (record.Status == HistoryStatus.Success) ReceiptPresentAtSuccess = store.HasReceipt;
            return fail() ? Task.FromException(new IOException("Synthetic history write failure")) : _inner.AddAsync(record);
        }

        public Task<IReadOnlyList<HistoryRecord>> QueryAsync(HistoryFilter filter) => _inner.QueryAsync(filter);
        public Task<IReadOnlyList<HistoryRecord>> RecentAsync(int count) => _inner.RecentAsync(count);
        public Task<int> ClearAsync() => _inner.ClearAsync();
        public Task<int> DeleteOlderThanAsync(DateTimeOffset cutoff) => _inner.DeleteOlderThanAsync(cutoff);
    }
}
