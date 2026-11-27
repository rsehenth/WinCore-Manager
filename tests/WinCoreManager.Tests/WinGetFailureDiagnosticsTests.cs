using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public sealed class WinGetFailureDiagnosticsTests
{
    [Fact]
    public Task DeckCraft_1603_reports_the_scope_conflict_and_continues_only_after_proven_failure() => WpfTestThread.Run(async () =>
    {
        var directory = Directory.CreateTempSubdirectory("WinCoreDeckCraftFixture-");
        try
        {
            var started = DateTime.UtcNow.AddSeconds(-1);
            var log = Path.Combine(directory.FullName, "ArtCraft.DeckCraft.0.19.1-fixture.log");
            await File.WriteAllLinesAsync(log, [
                "MSI (s) (11:22) [17:59:33:428]: Product: DeckCraft -- This app is already installed for everyone.",
                "Action ended 17:59:33: LaunchConditions. Return value 3.",
                "Property(S): MSIINSTALLPERUSER = 1",
                "Property(S): ProductCode = {033807C7-AC25-4481-AAA0-FF64F4E0AA22}",
                "MSI (s) (11:22): Installation success or error status: 1603.",
            ]);
            var target = new WinGetPackage
            {
                Id = "ArtCraft.DeckCraft", Name = "DeckCraft", Source = "winget",
                InstalledVersion = "0.18.1", AvailableVersion = "0.19.1",
            };
            var run = new ProcessRunResult
            {
                ExitCode = unchecked((int)0x8A150006),
                Observation = WinGetOperationFixture.Complete,
                Output = [$"Installer log is available at: {log}"],
            };
            Assert.Null(WinGetFailureDiagnostics.Read(target, run, DateTime.UtcNow.AddMinutes(1), directory.FullName));
            Assert.Null(WinGetFailureDiagnostics.Read(target, run, started, directory.Parent!.FullName));
            run.InstallerFailureDetail = WinGetFailureDiagnostics.Read(target, run, started, directory.FullName);
            Assert.Contains("1603", run.InstallerFailureDetail);
            Assert.Contains("Everyone", run.InstallerFailureDetail);

            var next = new WinGetPackage
            {
                Id = "Fixture.Next", Name = "Next", Source = "winget", InstalledVersion = "1.0", AvailableVersion = "2.0",
            };
            var winget = new WinGetOperationFixture(2)
            {
                Packages = [target, next],
                Run = (item, _, _) => Task.FromResult(item.Id == target.Id ? run : new ProcessRunResult
                {
                    Observation = WinGetOperationFixture.Complete,
                }),
                Version = item => item.Id == target.Id ? "0.18.1" : "2.0",
            };
            var history = new FakeHistoryService();
            var page = new UpdatesViewModel(winget, history);
            await page.RefreshCommand.ExecuteAsync(null);
            page.AllSelected = true;
            await page.UpdateSelectedCommand.ExecuteAsync(null);
            Assert.Equal(new[] { target.Id, next.Id }, winget.Launched);
            Assert.Equal(UpdateRowStatus.Failed, Assert.Single(page.Rows, row => row.Id == target.Id).Status);
            Assert.Equal(UpdateRowStatus.Success, Assert.Single(page.Rows, row => row.Id == next.Id).Status);
            var failed = Assert.Single(history.Records, record => record.Target == "DeckCraft");
            Assert.Equal(HistoryStatus.Failure, failed.Status);
            Assert.Equal("0.18.1", failed.VersionTo);
            Assert.Contains(run.InstallerFailureDetail!, failed.Message);
        }
        finally { directory.Delete(recursive: true); }
    });
}
