using System.Buffers.Binary;
using WinCoreManager.Models;
using WinCoreManager.Services.Hardware;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

public sealed class Rc3FunctionalTests
{
    [Theory]
    [InlineData(6, 0, 0, (int)DiskHealthState.Healthy, 94)]
    [InlineData(6, 4, 0, (int)DiskHealthState.Critical, 94)]
    [InlineData(6, 0, 3, (int)DiskHealthState.Attention, 94)]
    [InlineData(107, 0, 0, (int)DiskHealthState.Attention, 0)]
    [InlineData(6, 128, 0, (int)DiskHealthState.Unknown, 94)]
    public void Nvme_health_uses_controller_evidence(int wear, int warning, int errors,
        int expected, int life)
    {
        var page = NvmePage(wear, warning, errors);
        var sample = WindowsSmart.ParseNvme(page);
        Assert.Equal((DiskHealthState)expected, sample.Health);
        Assert.Equal(life, sample.RemainingLife);
        Assert.Equal(42, sample.Celsius);
        Assert.Contains(sample.Metrics, row => row.Label == "PowerHours" && row.Raw.EndsWith(" h"));
        Assert.Contains(sample.Metrics, row => row.Label == "DataWritten");
    }

    [Fact]
    public void Nvme_validation_rejects_blank_truncated_and_mistyped_data()
    {
        Assert.Null(WindowsSmart.ParseNvme(new byte[512]).RemainingLife);
        Assert.Null(WindowsSmart.ParseNvme(new byte[511]).RemainingLife);
        Assert.Equal(DiskHealthState.Unknown, WindowsSmart.ParseNvme(Enumerable.Repeat((byte)255, 512).ToArray()).Health);
        var page = NvmePage(4, 0, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), ushort.MaxValue);
        Assert.Null(WindowsSmart.ParseNvme(page).Celsius);
        var descriptor = new byte[560];
        uint[] header = [48, 560, 3, 2, 2, 0, 40, 512, 0, 0, 0, 0];
        for (var index = 0; index < header.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(index * 4), header[index]);
        page.CopyTo(descriptor, 48);
        Assert.Equal(page, WindowsSmart.ExtractNvmeLog(descriptor).ToArray());
        descriptor[16] = 1;
        Assert.Equal(0, WindowsSmart.ExtractNvmeLog(descriptor).Length);
    }

    [Theory]
    [InlineData(true, 7, (int)DiskHealthState.Attention)]
    [InlineData(false, 7, (int)DiskHealthState.Healthy)]
    [InlineData(true, 0, (int)DiskHealthState.Healthy)]
    public void Ata_reports_documented_counters_without_inventing_usable_life(bool mechanical, int errors, int health)
    {
        var sector = new byte[512];
        sector[0] = 1;
        sector[2] = 197;
        sector[5] = 100;
        sector[6] = 100;
        sector[7] = (byte)errors;
        sector[511] = unchecked((byte)-sector.Sum(value => (int)value));
        var sample = WindowsSmart.ParseAta(sector, false, mechanical, null);
        Assert.Equal((DiskHealthState)health, sample.Health);
        Assert.Null(sample.RemainingLife);
        Assert.Null(sample.Celsius);
        sector[511]++;
        Assert.Equal(DiskHealthState.Unknown, WindowsSmart.ParseAta(sector, false, mechanical, null).Health);
        Assert.Equal(DiskHealthState.Critical, WindowsSmart.ParseAta([], true, mechanical, null).Health);
        Assert.Equal(DiskHealthState.Unknown, WindowsSmart.ParseAta([], false, mechanical, null).Health);
    }

    [Fact]
    public async Task Disk_health_single_flight_caches_visits_and_throttles_refresh()
    {
        using var gate = new ManualResetEventSlim();
        var attempts = 0;
        var clock = new TestClock();
        var disks = TestDisks();
        var service = new DiskHealthService(token =>
        {
            Interlocked.Increment(ref attempts);
            gate.Wait(token);
            return disks;
        }, clock);
        var dashboard = service.ReadAsync(false);
        var maintenance = service.ReadAsync(false);
        gate.Set();
        Assert.Same(await dashboard, await maintenance);
        Assert.Same(disks, await service.ReadAsync(true));
        Assert.Equal(1, attempts);
        clock.Now += TimeSpan.FromSeconds(12);
        Assert.Same(disks, await service.ReadAsync(false));
        await service.ReadAsync(true);
        Assert.Equal(2, attempts);
        Assert.Equal(2, diskCount(disks));
        static int diskCount(DiskHealthReport report) => report.Disks.Select(disk => disk.Device.Id).Distinct().Count();
    }

    [Fact]
    public void Registry_scans_exact_product_under_publisher_and_keeps_shared_root_and_values_protected()
    {
        using var registry = new FakeResidueRegistryReader();
        var code = Guid.NewGuid();
        const string branch = @"SOFTWARE\Acme";
        const string run = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
        registry.Add(ResidueRegistryHive.CurrentUser, branch + @"\Note Forge",
            new Dictionary<string, string> { ["ProductCode"] = code.ToString("B") });
        registry.Add(ResidueRegistryHive.CurrentUser, branch + @"\Other App");
        registry.Add(ResidueRegistryHive.LocalMachine, @"SOFTWARE\Wow6432Node\Acme\Note Forge");
        registry.Add(ResidueRegistryHive.CurrentUser, run, new Dictionary<string, string>
        {
            ["Note Forge"] = @"""C:\Acme\NoteForge.exe"" -startup",
            ["Other App"] = @"C:\Acme\OtherApp.exe",
        });
        var program = new InstalledProgram { Name = "Note Forge", Publisher = "Acme", MsiProductCode = code };
        using var environment = new ResidueTestEnvironment("rc3");
        var rules = environment.Rules;
        var report = new ResidueDetectionEngine(new FakeRegistryView(), registry, rules).Detect(program, [], default);

        Assert.False(report.ScanFailed, report.Error);
        Assert.Contains(report.Items, item => item.Location == @"HKEY_CURRENT_USER\SOFTWARE\Acme" &&
            item.Confidence == ResidueConfidence.Protected);
        Assert.Contains(report.Items, item => item.Location == @"HKEY_CURRENT_USER\SOFTWARE\Acme\Note Forge" &&
            item.Confidence == ResidueConfidence.High && item.Fingerprint is not null);
        Assert.Contains(report.Items, item => item.Location == @"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Acme\Note Forge" &&
            item.Confidence == ResidueConfidence.Medium);
        var autorun = Assert.Single(report.Items, item => item.Category == ResidueCategory.RegistryValue);
        Assert.Equal("Note Forge", autorun.ValueName);
        Assert.Equal(ResidueConfidence.Protected, autorun.Confidence);
        Assert.False(autorun.IsEligibleForCleanup);
        Assert.DoesNotContain(report.Items, item => item.Location.EndsWith("Other App"));
    }

    [Fact]
    public void Optional_inventory_matches_exact_family_and_only_approved_main_packages()
    {
        var officeHub = Package("Microsoft.MicrosoftOfficeHub");
        var feedback = Package("Microsoft.WindowsFeedbackHub", false);
        var teamsPersonal = Package("MicrosoftTeams");
        var xbox = Package("Microsoft.GamingApp");
        var programs = new[] { new InstalledProgram { Name = "Microsoft 365 Apps", Publisher = "Microsoft Corporation" } };
        var report = OptionalWindowsApps.Classify([officeHub, feedback, teamsPersonal, Package("MSTeams"),
            Package("Microsoft.WindowsStore"), Package("Microsoft.GamingServices"), xbox, xbox], programs, new UninstallPlanner());
        Assert.Equal(OptionalAppState.Available, Row(report, "Microsoft.MicrosoftOfficeHub").State);
        Assert.Equal(OptionalAppState.Available, Row(report, "MicrosoftTeams").State);
        Assert.Equal(OptionalAppState.Protected, Row(report, "Microsoft.WindowsFeedbackHub").State);
        Assert.Equal(OptionalAppState.Ambiguous, Row(report, "Microsoft.GamingApp").State);
        Assert.Equal(OptionalAppState.NotInstalled, Row(report, "Microsoft.GetHelp").State);
        Assert.DoesNotContain(report, row => row.Definition.Key is "MSTeams" or "Microsoft.WindowsStore" or "Microsoft.GamingServices");
        Assert.Equal("https://apps.microsoft.com/detail/9NBLGGH5R558", Row(report, "Microsoft.Todos").Definition.StorePage?.AbsoluteUri);
        Assert.False(Row(report, "Microsoft.Todos").Definition.CanSuggest);
    }

    [Fact]
    public void Optional_inventory_keeps_unverified_separate_and_checks_command_scope()
    {
        var planner = new UninstallPlanner();
        Assert.All(OptionalWindowsApps.Classify(null, null, planner), row => Assert.Equal(OptionalAppState.Unverified, row.State));
        var item = Row(OptionalWindowsApps.Classify([Package("Microsoft.GetHelp")], [], planner), "Microsoft.GetHelp");
        var plan = planner.CreatePlan(item.Program!, silentPreferred: false);
        Assert.Equal(UninstallMethod.CurrentUserAppx, plan.Method);
        var command = OfficialAppx.Removal(item.Program!);
        var script = System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(command.ArgumentList[^1]));
        Assert.Contains("Remove-AppxPackage -Package $full", script);
        Assert.Contains("Get-CurrentPackages", script);
        Assert.DoesNotContain("-AllUsers", script);
        Assert.DoesNotContain("Provisioned", script);
        Assert.False(OptionalWindowsApps.MatchesFullName(item.Definition.Family, "Microsoft.GetHelp_1.0.0.0_x64__otherpublisher"));
        Assert.False(OptionalWindowsApps.MatchesFullName(item.Definition.Family, "Microsoft.GetHelp_1.0.0.0_x64_resource_8wekyb3d8bbwe"));
        Assert.Contains("nuvem", OptionalWindowsApps.OneDrive.Impact);
    }

    internal static DiskHealthReport TestDisks() => new(ReadingState.Available,
    [
        new(new HardwareDevice("disk/0", HardwareKind.Disk, "Fixture NVMe", [],
                [new("disk/0/part/1", HardwareKind.Disk, "Partition 1", []), new("disk/0/part/2", HardwareKind.Disk, "Partition 2", [])]),
            WindowsSmart.ParseNvme(NvmePage(6, 0, 0))),
        new(new HardwareDevice("disk/1", HardwareKind.Disk, "Fixture SATA SSD", []),
            new(ReadingState.Restricted, DiskHealthState.Unknown, "", [])),
    ]);

    internal static byte[] NvmePage(int wear, int warning, int errors)
    {
        var page = new byte[512];
        page[0] = (byte)warning;
        BinaryPrimitives.WriteUInt16LittleEndian(page.AsSpan(1), 315);
        page[3] = 100;
        page[4] = 10;
        page[5] = (byte)wear;
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(48), 3000);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(128), 200);
        BinaryPrimitives.WriteUInt64LittleEndian(page.AsSpan(160), (ulong)errors);
        return page;
    }

    internal static OptionalPackage Package(string name, bool eligible = true) =>
        new(name + "_8wekyb3d8bbwe", name + "_1.0.0.0_x64__8wekyb3d8bbwe", "Microsoft Corporation", "1.0.0.0", eligible);

    private static OptionalApp Row(IReadOnlyList<OptionalApp> report, string key) =>
        Assert.Single(report, row => row.Definition.Key == key);

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
