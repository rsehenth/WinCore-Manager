using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class Rc3CarbonAuditTests
{
    [Fact]
    public void Registry_mutation_rejects_real_handles_in_the_lab_and_respects_the_real_process_privilege()
    {
        using var environment = new ResidueTestEnvironment("rc3-carbon-boundary");
        const string path = @"SOFTWARE\Contoso\WidgetPro";
        using var userSoftware = Registry.CurrentUser.OpenSubKey("SOFTWARE")!;
        using var machineSoftware = Registry.LocalMachine.OpenSubKey("SOFTWARE")!;
        var production = new ResidueRegistryMutation(environment.Rules);
        Assert.Equal(!Environment.IsPrivilegedProcess, production.CanWrite(ResidueRegistryHive.CurrentUser, path));
        Assert.False(production.CanWrite(ResidueRegistryHive.LocalMachine, path));
        Assert.False(new ResidueRegistryMutation(environment.Rules, userSoftware.Handle)
            .CanWrite(ResidueRegistryHive.CurrentUser, path));
        Assert.False(new ResidueRegistryMutation(environment.Rules, machineSoftware.Handle)
            .CanWrite(ResidueRegistryHive.CurrentUser, path));
    }

    [Fact]
    public async Task Hkcu_fallback_removes_and_restores_verified_tree_when_ktm_returns_6801_and_reports_non_atomic_mode()
    {
        using var environment = new ResidueTestEnvironment("rc3-carbon-6801");
        var hiveFile = Path.Combine(Path.GetTempPath(), $"wincore-carbon-{Guid.NewGuid():N}.hive");
        try
        {
            Assert.Equal(0, RegLoadAppKeyW(hiveFile, out var handle, 0xF003F, 1, 0));
            using (handle)
            using (var root = RegistryKey.FromHandle(handle))
            {
                const string parentPath = @"SOFTWARE\Contoso\WidgetPro";
                const string childPath = @"SOFTWARE\Contoso\WidgetPro\Settings";
                const string fullKeyPath = @"HKEY_CURRENT_USER\" + parentPath;
                var installDir = Directory.CreateDirectory(Path.Combine(environment.Local, "WidgetPro")).FullName;
                byte[] rawBinary = [0x10, 0x00, 0xFE, 0x42];

                using (var parent = root.CreateSubKey(parentPath))
                {
                    parent.SetValue("InstallDir", installDir, RegistryValueKind.String);
                    parent.SetValue("State", rawBinary, RegistryValueKind.Binary);
                }
                using (var child = root.CreateSubKey(childPath))
                {
                    child.SetValue("Zoom", 125, RegistryValueKind.DWord);
                }
                Directory.Delete(installDir, recursive: true);

                using var hiveAdapter = new IsolatedHiveRegistry(handle, environment.Rules);
                Assert.True(hiveAdapter.Mutation.CanWrite(ResidueRegistryHive.CurrentUser, parentPath),
                    $"Application hive blocked with IsPrivilegedProcess={Environment.IsPrivilegedProcess}.");
                Assert.False(hiveAdapter.Mutation.CanWrite(ResidueRegistryHive.LocalMachine, parentPath));
                Assert.False(hiveAdapter.Mutation.CanWrite(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Classes\CLSID"));
                Assert.False(hiveAdapter.Mutation.CanWrite(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"));
                var quarantineRoot = Path.Combine(environment.Root, "quarantine");
                var quarantine = new ResidueQuarantineService(quarantineRoot, hiveAdapter, hiveAdapter, environment.Rules);
                var history = new FakeHistoryService();
                var executor = new ResidueCleanupExecutor(quarantine, hiveAdapter, hiveAdapter, history);

                var snapshot = RegistrySnapshot.Capture(hiveAdapter, ResidueRegistryHive.CurrentUser, parentPath, true);
                var fingerprint = RegistrySnapshot.ComputeHash(snapshot);
                var item = new ResidueItem
                {
                    Id = Guid.NewGuid(),
                    Category = ResidueCategory.RegistryKey,
                    Location = fullKeyPath,
                    Is64BitView = true,
                    Confidence = ResidueConfidence.High,
                    Fingerprint = fingerprint,
                };
                var plan = new ResidueCleanupPlan
                {
                    OperationId = Guid.NewGuid(),
                    Program = new InstalledProgram { Name = "WidgetPro", Publisher = "Contoso" },
                    Approved = [item],
                    RegistryConsentGranted = true,
                };

                var report = await executor.ExecuteAsync(plan);
                var single = Assert.Single(report.Results);
                Assert.Equal(ResidueCleanupItemOutcome.Success, single.Outcome);
                Assert.Contains("sem transação", single.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Null(root.OpenSubKey(parentPath));
                Assert.True(File.Exists(single.RegistryBackupFile));

                // Destination conflict during supervised restore keeps existing data intact.
                using (var conflicting = root.CreateSubKey(parentPath))
                    conflicting.SetValue("Live", "KeepMe", RegistryValueKind.String);
                var conflict = quarantine.Restore(single.QuarantineEntryId!.Value, overwriteConfirmed: true, CancellationToken.None);
                Assert.Equal(QuarantineRestoreStatus.ConflictDestinationExists, conflict.Status);
                using (var checkConflict = root.OpenSubKey(parentPath))
                    Assert.Equal("KeepMe", checkConflict!.GetValue("Live"));
                root.DeleteSubKeyTree(@"SOFTWARE\Contoso");

                // Supervised restore under 6801 recreates missing ancestor + full tree byte-for-byte.
                var restored = quarantine.Restore(single.QuarantineEntryId.Value, overwriteConfirmed: false, CancellationToken.None);
                Assert.Equal(QuarantineRestoreStatus.Restored, restored.Status);
                Assert.Contains("sem transação", restored.Message, StringComparison.OrdinalIgnoreCase);
                using var restoredParent = root.OpenSubKey(parentPath);
                using var restoredChild = root.OpenSubKey(childPath);
                Assert.NotNull(restoredParent);
                Assert.NotNull(restoredChild);
                Assert.Equal(rawBinary, Assert.IsType<byte[]>(restoredParent!.GetValue("State")));
                Assert.Equal(125, restoredChild!.GetValue("Zoom"));
                Assert.Empty(quarantine.GetEntries());
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", ".LOG", ".LOG1", ".LOG2" })
                File.Delete(hiveFile + suffix);
        }
    }

    [Fact]
    public async Task Partial_non_transactional_removal_halts_on_concurrent_change_and_records_partial_history_with_backup()
    {
        using var environment = new ResidueTestEnvironment("rc3-carbon-partial");
        var hiveFile = Path.Combine(Path.GetTempPath(), $"wincore-carbon-partial-{Guid.NewGuid():N}.hive");
        try
        {
            Assert.Equal(0, RegLoadAppKeyW(hiveFile, out var handle, 0xF003F, 1, 0));
            using (handle)
            using (var root = RegistryKey.FromHandle(handle))
            {
                const string parentPath = @"SOFTWARE\Contoso\WidgetPro";
                const string childPath = @"SOFTWARE\Contoso\WidgetPro\Cache";
                using (var parent = root.CreateSubKey(parentPath))
                    parent.SetValue("Version", "1.0", RegistryValueKind.String);
                using (var child = root.CreateSubKey(childPath))
                    child.SetValue("Temp", 1, RegistryValueKind.DWord);

                using var hiveAdapter = new IsolatedHiveRegistry(handle, environment.Rules);
                hiveAdapter.Mutation.BeforeNodeDeleteForTest = (relative, removed) =>
                {
                    if (removed == 1 && relative.Equals(parentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        using var concurrent = root.OpenSubKey(parentPath, writable: true)!;
                        concurrent.SetValue("ConcurrentWrite", "DoNotDelete", RegistryValueKind.String);
                    }
                };

                var quarantineRoot = Path.Combine(environment.Root, "quarantine");
                var quarantine = new ResidueQuarantineService(quarantineRoot, hiveAdapter, hiveAdapter, environment.Rules);
                var history = new FakeHistoryService();
                var executor = new ResidueCleanupExecutor(quarantine, hiveAdapter, hiveAdapter, history);

                var snapshot = RegistrySnapshot.Capture(hiveAdapter, ResidueRegistryHive.CurrentUser, parentPath, true);
                var item = new ResidueItem
                {
                    Id = Guid.NewGuid(),
                    Category = ResidueCategory.RegistryKey,
                    Location = @"HKEY_CURRENT_USER\" + parentPath,
                    Is64BitView = true,
                    Confidence = ResidueConfidence.High,
                    Fingerprint = RegistrySnapshot.ComputeHash(snapshot),
                };
                var plan = new ResidueCleanupPlan
                {
                    OperationId = Guid.NewGuid(),
                    Program = new InstalledProgram { Name = "WidgetPro", Publisher = "Contoso" },
                    Approved = [item],
                    RegistryConsentGranted = true,
                };

                var report = await executor.ExecuteAsync(plan);
                var single = Assert.Single(report.Results);
                Assert.Equal(ResidueCleanupItemOutcome.Failed, single.Outcome);
                Assert.Contains("REMOÇÃO PARCIAL: 1 de 2", single.Message);
                Assert.True(File.Exists(single.RegistryBackupFile));
                Assert.Null(root.OpenSubKey(childPath));
                using (var survivingParent = root.OpenSubKey(parentPath))
                {
                    Assert.NotNull(survivingParent);
                    Assert.Equal("DoNotDelete", survivingParent!.GetValue("ConcurrentWrite"));
                }
                var historyRecord = Assert.Single(history.Records);
                Assert.Equal(HistoryStatus.Failure, historyRecord.Status);
                Assert.Single(quarantine.GetEntries());
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", ".LOG", ".LOG1", ".LOG2" })
                File.Delete(hiveFile + suffix);
        }
    }

    [Fact]
    public void English_presentation_translates_all_rc3_domains_and_preserves_nvme_slash_source()
    {
        var en = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("Healthy", PresentationText.Translate(LocalizationService.Source("DiskHealth.State.Healthy"), en));
        Assert.Equal("Unavailable", PresentationText.Translate(LocalizationService.Source("DiskHealth.Unavailable"), en));
        Assert.Equal("Protected or shared", PresentationText.Translate(LocalizationService.Source("ResidueAdvanced.Confidence.Protected"), en));
        Assert.Equal("The ProductCode matches the product.", PresentationText.Translate(LocalizationService.Source("ResidueAdvanced.ProductCode"), en));
        Assert.Equal("Removes the Microsoft 365 hub. The Office suite uses separate installations.",
            PresentationText.Translate(LocalizationService.Source("Optional.Impact.Microsoft.MicrosoftOfficeHub"), en));
        Assert.Equal("Installed for the current user", PresentationText.Translate(LocalizationService.Source("Uninstaller.ScopeUser"), en));
        Assert.Equal(WindowsSmart.NvmeSource, PresentationText.Translate(WindowsSmart.NvmeSource, en));
        Assert.Equal("12.5 MB of 50.0 MB", PresentationText.Translate("12,5 MB / 50,0 MB", en));
    }

    [Fact]
    public Task Optional_apps_batch_buttons_only_select_visible_filtered_rows() =>
        WpfTestThread.Run(async () =>
        {
            var planner = new UninstallPlanner();
            var catalog = OptionalWindowsApps.Classify(
            [
                Rc3FunctionalTests.Package("Microsoft.GetHelp"),
                Rc3FunctionalTests.Package("Microsoft.BingWeather"),
                Rc3FunctionalTests.Package("Microsoft.Windows.Photos"),
                Rc3FunctionalTests.Package("Microsoft.MicrosoftStickyNotes"),
            ], [], planner);
            var model = new UninstallersViewModel(new FakeProgramInventory(), planner,
                new UninstallBatchRunner(planner, new FakeUninstallExecutor(), new FakeHistoryService()),
                new FakeResidueAnalysisService());
            var window = new OptionalWindowsAppsWindow(_ => Task.FromResult(catalog), model);
            try
            {
                window.Show();
                var suggested = (ItemsControl)window.FindName("Suggested");
                var others = (ItemsControl)window.FindName("Others");
                for (var i = 0; i < 100 && suggested.Items.Count == 0; i++)
                    await Task.Delay(20);

                var search = (TextBox)window.FindName("Search");
                search.Text = "Weather";

                var selectEligible = Descendants<Button>(window)
                    .Single(b => Equals(b.Content, LocalizationService.Current["Optional.SelectEligible"]));
                selectEligible.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                var removeButton = (Button)window.FindName("Remove");
                Assert.Contains("(1)", removeButton.Content?.ToString());
                Assert.Empty(others.Items);

                search.Text = "";
                var clearButton = Descendants<Button>(window)
                    .Single(b => Equals(b.Content, LocalizationService.Current["Optional.Clear"]));
                clearButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(removeButton.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });

    [Fact]
    public async Task Disk_health_passive_navigation_does_not_reblock_when_driver_call_is_hung()
    {
        using var refreshBlocked = new ManualResetEventSlim();
        using var refreshStarted = new ManualResetEventSlim();
        var calls = 0;
        var clock = new AdvanceableClock();
        var report = Rc3FunctionalTests.TestDisks();
        var service = new DiskHealthService(token =>
        {
            if (Interlocked.Increment(ref calls) > 1)
            {
                refreshStarted.Set();
                refreshBlocked.Wait(token);
            }
            return report;
        }, clock);

        Assert.Same(report, await service.ReadAsync(false));
        clock.Now += TimeSpan.FromSeconds(15);
        var slowRefresh = service.ReadAsync(true);
        Assert.True(refreshStarted.Wait(TimeSpan.FromSeconds(2)));

        var passive = await service.ReadAsync(false).WaitAsync(TimeSpan.FromMilliseconds(300));
        Assert.Same(report, passive);
        refreshBlocked.Set();
        await slowRefresh;

        // Initial load hang: times out once, then passive navigations fail fast without spawning new threads.
        using var initialHung = new ManualResetEventSlim();
        var hungCalls = 0;
        var hungService = new DiskHealthService(_ =>
        {
            Interlocked.Increment(ref hungCalls);
            initialHung.Wait();
            return report;
        }, clock, TimeSpan.FromMilliseconds(60));

        await Assert.ThrowsAsync<TimeoutException>(() => hungService.ReadAsync(false));
        await Assert.ThrowsAsync<TimeoutException>(() => hungService.ReadAsync(false).WaitAsync(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(1, hungCalls);
        initialHung.Set();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var inner in Descendants<T>(child)) yield return inner;
        }
    }

    private sealed class AdvanceableClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class IsolatedHiveRegistry(SafeRegistryHandle hive, ResiduePathRules rules)
        : IResidueRegistryReader, IResidueRegistryWriter, IAtomicResidueRegistryWriter
    {
        internal readonly ResidueRegistryMutation Mutation = new(rules, hive);
        private RegistryKey Root => RegistryKey.FromHandle(new SafeRegistryHandle(hive.DangerousGetHandle(), ownsHandle: false));

        public bool SupportsMutation => true;
        public bool CanWriteKey(ResidueRegistryHive h, string subKeyPath) => Mutation.CanWrite(h, subKeyPath);

        public RegistryDeleteOutcome DeleteVerifiedOutcome(ResidueRegistryHive h, string path, bool is64Bit,
            RegistryKeyData snapshot, CancellationToken token) => Mutation.DeleteVerified(h, path, is64Bit, snapshot, token);

        public RegistryRestoreOutcome RestoreVerifiedOutcome(ResidueRegistryHive h, string path, bool is64Bit,
            RegistryKeyData snapshot) => Mutation.RestoreVerified(h, path, is64Bit, snapshot);

        public IReadOnlyList<string> GetSubKeyNames(ResidueRegistryHive h, string subKeyPath, bool is64BitView)
        {
            using var root = Root;
            using var key = root.OpenSubKey(subKeyPath, writable: false);
            return key?.GetSubKeyNames() ?? [];
        }

        public IReadOnlyList<string> GetValueNames(ResidueRegistryHive h, string subKeyPath, bool is64BitView)
        {
            using var root = Root;
            using var key = root.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValueNames() ?? [];
        }

        public string? GetStringValue(ResidueRegistryHive h, string subKeyPath, string valueName, bool is64BitView)
        {
            using var root = Root;
            using var key = root.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }

        public object? GetRawValue(ResidueRegistryHive h, string subKeyPath, string valueName, bool is64BitView)
        {
            using var root = Root;
            using var key = root.OpenSubKey(subKeyPath, writable: false);
            return key?.GetValue(valueName);
        }

        public bool KeyExists(ResidueRegistryHive h, string subKeyPath, bool is64BitView)
        {
            using var root = Root;
            using var key = root.OpenSubKey(subKeyPath, writable: false);
            return key is not null;
        }

        public RegistryKeyContents ReadKeyStrict(ResidueRegistryHive h, string subKeyPath, bool is64BitView)
        {
            using var root = Root;
            using var key = root.OpenSubKey(subKeyPath, writable: false) ?? throw new InvalidOperationException();
            var values = key.GetValueNames().Select(n => WindowsResidueRegistry.ReadNativeValue(key.Handle, n)).ToList();
            return new RegistryKeyContents(values, key.GetSubKeyNames(), IsSymbolicLink: false);
        }

        public bool DeleteEmptySubKey(ResidueRegistryHive h, string parent, string name, bool is64, out string error) { error = "unused"; return false; }
        public bool CreateKey(ResidueRegistryHive h, string path, bool is64, out string error) { error = "unused"; return false; }
        public bool SetValue(ResidueRegistryHive h, string path, string name, RegistryValueKind kind, object data, bool is64, out string error) { error = "unused"; return false; }
        public void Dispose() { }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle key, int access, int options, int reserved);
}
