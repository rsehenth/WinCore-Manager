using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using Xunit.Abstractions;

namespace WinCoreManager.Tests;

public sealed class Rc3SafetyTests(ITestOutputHelper output)
{
    [Fact]
    public void Native_registry_transaction_stays_in_an_application_hive_and_refuses_changed_data()
    {
        using var environment = new ResidueTestEnvironment("rc3-native");
        var file = Path.Combine(Path.GetTempPath(), $"wincore-rc3-{Guid.NewGuid():N}.hive");
        try
        {
            Assert.Equal(0, RegLoadAppKeyW(file, out var handle, 0xF003F, 1, 0));
            using (handle)
            using (var root = RegistryKey.FromHandle(handle))
            {
                const string path = @"Software\FixtureVendor\FixtureProduct";
                var bytes = new byte[] { 0x21, 0xFF, 0, 7 };
                using (var leaf = root.CreateSubKey(path))
                    leaf.SetValue("Data", bytes, RegistryValueKind.Binary);

                var mutation = new ResidueRegistryMutation(environment.Rules, handle);
                var snapshot = new RegistryKeyData
                {
                    SubKeyPath = "FixtureProduct",
                    Values = [RegistryValueData.From("Data", RegistryValueKind.Binary, new NativeRegistryValue(bytes))],
                };
                var old = snapshot.Values[0];
                var stale = snapshot with { Values = [old with { NativeHex = "4142" }] };
                Assert.False(mutation.DeleteVerified(ResidueRegistryHive.CurrentUser, path, true, stale, default, out _));
                using (var stillThere = root.OpenSubKey(path))
                    Assert.Equal(bytes, Assert.IsType<byte[]>(stillThere?.GetValue("Data")));
                Assert.False(mutation.RestoreVerified(ResidueRegistryHive.CurrentUser, path, true, snapshot, out _));
                Assert.False(mutation.DeleteVerified(ResidueRegistryHive.LocalMachine, path, true, snapshot, default, out _));

                var removed = mutation.DeleteVerified(ResidueRegistryHive.CurrentUser, path, true, snapshot, default, out _);
                output.WriteLine($"Isolated hive transaction committed: {removed}");
                if (removed)
                {
                    Assert.Null(root.OpenSubKey(path));
                    Assert.True(mutation.RestoreVerified(ResidueRegistryHive.CurrentUser, path, true, snapshot, out _));
                }
                using var recovered = root.OpenSubKey(path);
                Assert.Equal(bytes, Assert.IsType<byte[]>(recovered?.GetValue("Data")));
                Assert.Equal(RegistryValueKind.Binary, recovered!.GetValueKind("Data"));
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", ".LOG", ".LOG1", ".LOG2" })
                File.Delete(file + suffix);
        }
    }

    [Theory]
    [InlineData(UninstallOutcome.Failure, 3, 3)]
    [InlineData(UninstallOutcome.Cancelled, 2, 2)]
    [InlineData(UninstallOutcome.Interrupted, 2, 2)]
    public async Task Optional_apps_use_one_sequential_queue_and_preserve_the_recorded_identity(
        UninstallOutcome middle, int launched, int historyCount)
    {
        var planner = new UninstallPlanner();
        var apps = OptionalWindowsApps.Classify([Rc3FunctionalTests.Package("Microsoft.GetHelp"),
            Rc3FunctionalTests.Package("Microsoft.WindowsFeedbackHub"), Rc3FunctionalTests.Package("Microsoft.Windows.Photos")], [], planner);
        var programs = apps.Where(app => app.State == OptionalAppState.Available).Select(app => app.Program!).ToArray();
        var history = new FakeHistoryService();
        var active = 0;
        var launches = new List<string>();
        var runner = new UninstallBatchRunner(planner, new DelegateExecutor(async plan =>
        {
            Assert.Equal(1, Interlocked.Increment(ref active));
            Assert.Equal(UninstallMethod.CurrentUserAppx, plan.Method);
            launches.Add(plan.Program.OptionalPackageFullName!);
            await Task.Yield();
            Interlocked.Decrement(ref active);
            return launches.Count == 2 ? middle : UninstallOutcome.Success;
        }), history);

        var report = await runner.RunAsync(programs, reviewConfirmed: true);
        Assert.Equal(launched, launches.Count);
        Assert.Equal(UninstallOutcome.Success, report.Results[0].Outcome);
        Assert.Equal(middle, report.Results[1].Outcome);
        var records = history.Records;
        Assert.Equal(historyCount, records.Count);
        Assert.Contains(records, entry => entry.Details.Contains(programs[0].OptionalPackageFullName!, StringComparison.Ordinal));
    }

    private sealed class DelegateExecutor(Func<UninstallPlan, Task<UninstallOutcome>> run) : IUninstallExecutor
    {
        public async Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine,
            CancellationToken cancellationToken) => new() { Program = plan.Program, Outcome = await run(plan) };
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle key, int access, int options, int reserved);
}
