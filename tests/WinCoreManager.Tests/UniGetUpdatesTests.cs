using System.IO;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public class UniGetUpdatesTests
{
    [Theory]
    [InlineData("Failed when searching source; results will not be included: winget")]
    [InlineData("Falha ao pesquisar origem; os resultados não serão incluídos: winget")]
    public async Task A_source_warning_with_exit_zero_is_a_partial_scan(string warning)
    {
        var result = await Scan(new() { Output = [warning] }, new() { ExitCode = unchecked((int)0x8A150014) });
        Assert.False(result.Failed);
        Assert.Empty(result.Packages);
        Assert.Contains("winget", result.Error);
    }

    [Theory]
    [InlineData(0x8A150014u)]
    [InlineData(0x8A15002Bu)]
    public async Task An_empty_catalog_is_not_a_scan_failure(uint exitCode)
    {
        var result = await Scan(new() { ExitCode = unchecked((int)exitCode) }, new() { ExitCode = unchecked((int)exitCode) });
        Assert.False(result.Failed);
        Assert.Empty(result.Packages);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Conflicting_rows_never_pick_a_target_arbitrarily()
    {
        const string header = "Name         Id           Version   Available";
        const string separator = "----------------------------------------------------";
        const string old = "Fixture      Fixture.App  1.0       2.0";
        const string conflict = "Fixture      fixture.app  1.0       3.0";
        var output = string.Join('\n', header, separator, old, old, conflict);

        Assert.Empty(WinGetService.ParseScopedUpdates(output, "winget", out var rejected));
        Assert.Equal(2, rejected);
    }

    [Fact]
    public void An_inventory_table_does_not_turn_its_source_name_into_a_target_version()
    {
        const string inventory = "Name         Id           Version   Source\n" +
            "----------------------------------------------------\nFixture      Fixture.App  1.0       winget";
        Assert.Empty(WinGetService.ParseScopedUpdates(inventory, "winget", out var unreadable));
        Assert.Equal(1, unreadable);
    }

    [Fact]
    public void Unknown_inventory_and_target_are_identified_without_launching()
    {
        var row = new UpdateItemRow(new WinGetPackage
        {
            Name = "Fixture", Id = "Fixture.App", Source = "winget", InstalledVersion = "Desconhecido", AvailableVersion = "2.0",
        });
        Assert.False(row.CanSelect);
        Assert.NotNull(row.VersionWarning);

        var target = new WinGetPackage
        {
            Name = "Fixture", Id = "Fixture.App", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "Unknown",
        };
        Assert.False(new UpdateItemRow(target).CanSelect);
        Assert.Null(WinGetService.BuildUpgradeStartInfo("winget.exe", target));
    }

    [Fact]
    public Task Conclusive_elevation_refusal_records_cancellation_and_continues_the_reviewed_queue() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var history = new HistoryService(settings);
        var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
        var service = new WinGetOperationFixture(2)
        {
            Packages = [Package(1), Package(2)],
            Version = package => package.Id == "Fixture.2" ? "2.0" : null,
            Run = (package, control, _) =>
            {
                control.RootIdentity = new(int.MaxValue, 1);
                return Task.FromResult(package.Id == "Fixture.1" ? new ProcessRunResult
                {
                    ExitCode = unchecked((int)0x8A150006),
                    Observation = WinGetOperationFixture.Complete,
                    Output = ["O instalador falhou com o código de saída: 1223"],
                } : new ProcessRunResult { Observation = WinGetOperationFixture.Complete, Output = ["fixture package output"] });
            },
        };
        var vm = new UpdatesViewModel(service, history, settings, store)
        {
            ReviewBatch = packages =>
            {
                Assert.Equal(new[] { "Fixture.1", "Fixture.2" }, packages.Select(package => package.Id));
                return true;
            },
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        Assert.False(store.HasReceipt);
        Assert.Equal(new[] { UpdateRowStatus.Cancelled, UpdateRowStatus.Success }, vm.Rows.Select(row => row.Status));
        Assert.Contains("1223", vm.Results[0].Message);
        Assert.Equal("fixture package output", vm.Results[1].Log);
        var entries = await history.QueryAsync(new() { Type = HistoryType.Update });
        Assert.Contains(entries, row => row.Target == "Fixture 1" && row.Status == HistoryStatus.Cancelled);
        Assert.Contains(entries, row => row.Target == "Fixture 2" && row.Status == HistoryStatus.Success && row.VersionTo == "2.0");
    });

    [Fact]
    public Task Remaining_packages_are_checkpointed_before_the_first_unsafe_process_runs() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var history = new FakeHistoryService();
        var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
        var checkpointVerified = false;
        var service = new WinGetOperationFixture(3)
        {
            Packages = [Package(1), Package(2), Package(3)],
            Run = (_, _, _) =>
            {
                var saved = File.ReadAllText(Path.Combine(settings.DataDirectory, "winget-update.json.queue"));
                Assert.DoesNotContain("Fixture.1", saved);
                Assert.Contains("Fixture.2", saved);
                Assert.Contains("Fixture.3", saved);
                checkpointVerified = true;
                return Task.FromResult(new ProcessRunResult { Interrupted = true, HasExitCode = false });
            },
        };
        var vm = new UpdatesViewModel(service, history, settings, store);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);

        var reopened = new UpdatesViewModel(service, history, settings, store);
        Assert.True(checkpointVerified);
        Assert.True(reopened.HasInterruptedUpdate);
        Assert.Equal(new[] { "Fixture.2", "Fixture.3" }, reopened.PendingBatch.Select(item => item.Id));
        Assert.Single(service.Launched);
    });

    private static WinGetPackage Package(int number) => new()
    {
        Name = $"Fixture {number}", Id = $"Fixture.{number}", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "2.0",
    };

    private static Task<WinGetScanResult> Scan(ProcessRunResult winget, ProcessRunResult store)
    {
        var service = new WinGetService(() => @"C:\Fixture\winget.exe", (start, _, _, _, _) =>
        {
            if (start.ArgumentList[0] == "source")
            {
                var source = start.ArgumentList[3];
                var official = source == "winget" ? """
                    {"Name":"winget","Arg":"https://cdn.winget.microsoft.com/cache","Type":"Microsoft.PreIndexed.Package","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","TrustLevel":["Trusted","StoreOrigin"]}
                    """ : """
                    {"Name":"msstore","Arg":"https://storeedgefd.dsx.mp.microsoft.com/v9.0","Type":"Microsoft.Rest","Identifier":"StoreEdgeFD","TrustLevel":["Trusted"]}
                    """;
                return Task.FromResult(new ProcessRunResult { Output = [official] });
            }
            return Task.FromResult(start.ArgumentList[2] == "winget" ? winget : store);
        }, () => false);
        return service.ListUpdatesAsync();
    }
}
