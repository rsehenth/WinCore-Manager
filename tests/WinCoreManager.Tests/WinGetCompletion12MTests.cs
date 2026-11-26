using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

public sealed class WinGetCompletion12MTests
{
    private const string Terminal = "Microsoft.WindowsTerminal";
    private const string Preview = "Microsoft.WindowsTerminal.Preview";
    private const string Installed = "1.23.19001.0";
    private const string Target = "1.24.11911.0";
    private const string OfficialWinget = """
        {"Name":"winget","Arg":"https://cdn.winget.microsoft.com/cache","Type":"Microsoft.PreIndexed.Package","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","TrustLevel":["Trusted","StoreOrigin"]}
        """;

    [Fact]
    public Task VerifiedMsixRegistrationReleasesEachItemAndAdvancesTheQueue() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab();
        await lab.RunAsync();
        Assert.Equal(new[] { Terminal, Preview }, lab.Launches);
        Assert.All(lab.ViewModel.Rows, row => Assert.Equal(UpdateRowStatus.Success, row.Status));
        Assert.Collection(lab.History.Records, record => Assert.Equal(HistoryStatus.Success, record.Status),
            record => Assert.Equal(HistoryStatus.Success, record.Status));
        Assert.False(lab.Store.HasReceipt);
        Assert.False(lab.ViewModel.HasInterruptedUpdate);
        Assert.False(lab.ViewModel.ShowInterruptionReview);
        Assert.Equal(2, lab.BootReads);
        Assert.True(lab.ContenderBlocked);
        Assert.Equal(2, lab.ReceiptIds.Distinct().Count());
        Assert.All(lab.Observations, observation => Assert.True(observation.IsConclusive));
    });

    [Theory]
    [InlineData("no-registration", HistoryStatus.Interrupted)]
    [InlineData("resource-only", HistoryStatus.Interrupted)]
    [InlineData("no-inventory", HistoryStatus.Interrupted)]
    [InlineData("no-identity", HistoryStatus.Interrupted)]
    [InlineData("identity-write-failure", HistoryStatus.Interrupted)]
    [InlineData("cancel-zero", HistoryStatus.Interrupted)]
    [InlineData("reboot", HistoryStatus.Interrupted)]
    [InlineData("failure", HistoryStatus.Failure)]
    [InlineData("early-exit", HistoryStatus.Interrupted)]
    [InlineData("old-client", HistoryStatus.Interrupted)]
    [InlineData("missing-framework", HistoryStatus.Interrupted)]
    [InlineData("already-registered", HistoryStatus.Interrupted)]
    [InlineData("generic-zero", HistoryStatus.Interrupted)]
    public Task MissingOrConflictingEvidenceRetainsTheGuard(string scenario, HistoryStatus expected) => WpfTestThread.Run(async () =>
    {
        var lab = new Lab(scenario == "generic-zero" ? "Fixture.Generic" : Terminal) { Mode = scenario };
        await lab.RunAsync();
        lab.AssertGuard(expected);
        using var receipt = lab.Store.Open();
        if (scenario is "no-identity" or "identity-write-failure") Assert.Null(receipt.Identity);
        if (scenario == "early-exit") Assert.Equal(ControlledProcessState.NotObserved, lab.RunResult!.Observation.ControlledProcesses);
        if (scenario == "no-inventory") Assert.True(lab.RunResult!.Observation.IsConclusive);
        else Assert.False(lab.RunResult!.Observation.IsConclusive);
    });

    [Fact]
    public Task KnownDelegateOverridesEvenAnExactMsixRegistration() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab { Mode = "quiet-delegate" };
        try
        {
            await lab.RunAsync();
            lab.AssertGuard(HistoryStatus.Interrupted);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(lab.Marker)));
            Assert.False(child.HasExited);
            Assert.Equal(DelegatedProcessState.KnownActive, lab.RunResult!.Observation.Delegation);
        }
        finally { File.WriteAllText(lab.Release, "release"); }
    });

    [Fact]
    public Task GenericSilentDelegateWithTargetInventoryStopsQueue() => WpfTestThread.Run(async () =>
    {
        var lab = new Lab("Fixture.Delegated");
        lab.Mode = "quiet-delegate";
        try
        {
            await lab.RunAsync();
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(lab.Marker)));
            Assert.False(child.HasExited);
            Assert.Equal(DelegatedProcessState.KnownActive, lab.RunResult!.Observation.Delegation);
            lab.AssertGuard(HistoryStatus.Interrupted);
            using var receipt = lab.Store.Open();
            Assert.Contains(receipt.Delegates, identity => identity.Pid == child.Id);
        }
        finally { File.WriteAllText(lab.Release, "release"); }
    });

    private sealed class Lab
    {
        private readonly TestSettings _settings = new();
        private readonly string _first;
        private readonly HashSet<string> _registeredTargets = [];
        private readonly WinGetService _service;
        internal readonly List<string> Launches = [];
        internal readonly List<Guid> ReceiptIds = [];
        internal readonly List<ExternalProcessObservation> Observations = [];
        internal readonly FakeHistoryService History = new();
        internal WinGetInterruptionStore Store { get; }
        internal UpdatesViewModel ViewModel { get; }
        internal ProcessRunResult? RunResult { get; private set; }
        internal int BootReads;
        internal bool ContenderBlocked { get; private set; }
        internal string Mode { get; set; } = "success";
        internal string Marker => Path.Combine(_settings.DataDirectory, "child");
        internal string Release => Marker + ".release";

        internal Lab(string first = Terminal)
        {
            _first = first;
            Store = new(_settings.DataDirectory, () => { BootReads++; return "100"; });
            _service = new(() => "synthetic-alias", RunCommandAsync, () => false, ReadRegisteredPackages);
            ViewModel = new(_service, History, _settings, Store);
        }

        internal async Task RunAsync()
        {
            await ViewModel.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, ViewModel.Rows.Count);
            ViewModel.AllSelected = true;
            await ViewModel.UpdateSelectedCommand.ExecuteAsync(null);
        }

        internal void AssertGuard(HistoryStatus status)
        {
            Assert.Single(Launches);
            Assert.Equal(UpdateRowStatus.Idle, ViewModel.Rows[1].Status);
            Assert.Equal(status, Assert.Single(History.Records).Status);
            Assert.True(Store.HasReceipt);
        }

        private static string Argument(ProcessStartInfo info, string name) => info.ArgumentList[info.ArgumentList.IndexOf(name) + 1];

        private static string Row(string name, string id, string version, string available = "") =>
            $"{name,-16}{id,-36}{version,-18}{available}";

        private static ProcessRunResult Output(params string[] lines) => new() { Output = [.. lines] };

        private IReadOnlyList<string> ReadRegisteredPackages(string family)
        {
            const string publisher = "_8wekyb3d8bbwe";
            var name = family[..^publisher.Length];
            if (name == "Microsoft.UI.Xaml.2.8")
                return Mode == "missing-framework" ? [] : [$"{name}_8.2306.22001.0_x64_{publisher}"];
            if (name == "Microsoft.VCLibs.140.00.UWPDesktop")
                return [$"{name}_14.0.1.0_x64_{publisher}"];

            var item = name == "Microsoft.WindowsTerminalPreview" ? Preview : Terminal;
            var version = Mode == "already-registered" || _registeredTargets.Contains(item) ? Target : Installed;
            var registration = $"{name}_{version}_x64_{publisher}";
            if (Mode == "resource-only" && version == Target)
                return [$"{name}_{Installed}_x64_{publisher}", $"{name}_{Target}_x64_en-us{publisher}"];
            return [registration];
        }

        private async Task<ProcessRunResult> RunCommandAsync(ProcessStartInfo command, Action<string>? output,
            CancellationToken token, WinGetProcessControl? control, bool externalInstallerPossible)
        {
            switch (command.ArgumentList[0])
            {
                case "--version": return Output(Mode == "old-client" ? "v1.11.0" : "v1.12.470");
                case "source": return Output(Argument(command, "--name") == "winget" ? OfficialWinget : "{}");
                case "list" when Mode == "no-inventory": return new() { ExitCode = 9 };
                case "list":
                    return Output(Row("Name", "Id", "Version"), new string('-', 70),
                        Row("Fixture", Argument(command, "--id"), Target));
                case "upgrade" when !command.ArgumentList.Contains("--id"):
                    return Output(Row("Name", "Id", "Version", "Available"), new string('-', 82),
                        Row("Alpha", _first, Installed, Target), Row("Beta", Preview, Installed, Target));
                default:
                    Assert.Equal("upgrade", command.ArgumentList[0]);
                    break;
            }

            Assert.NotNull(control);
            var item = Argument(command, "--id");
            var path = Path.Combine(_settings.DataDirectory, "winget-update.json");
            Assert.True(File.Exists(path));
            using var record = JsonDocument.Parse(File.ReadLines(path).Last());
            var operation = record.RootElement.GetProperty("Operation");
            Assert.Equal(item, operation.GetProperty("Target").GetString());
            ReceiptIds.Add(operation.GetProperty("Id").GetGuid());
            Assert.True(externalInstallerPossible);
            var bounded = item is Terminal or Preview && Mode is not ("old-client" or "missing-framework" or "already-registered");
            Assert.Equal(bounded, command.ArgumentList.Contains("--skip-dependencies"));
            if (bounded)
            {
                Assert.Equal("msix", Argument(command, "--installer-type"));
                Assert.Equal("user", Argument(command, "--scope"));
                Assert.Contains("--silent", command.ArgumentList);
                Assert.DoesNotContain("--interactive", command.ArgumentList);
            }
            Assert.Contains("--disable-interactivity", command.ArgumentList);
            Assert.DoesNotContain("--interactive", command.ArgumentList);

            if (Mode == "success" && Launches.Count == 0)
            {
                var contenderStart = WinGetCancellationTests.Fixture("receipt-worker");
                contenderStart.ArgumentList.Add("contend");
                contenderStart.ArgumentList.Add(_settings.DataDirectory);
                contenderStart.CreateNoWindow = true;
                using var contender = Process.Start(contenderStart)!;
                await contender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                ContenderBlocked = contender.ExitCode == 0;
            }
            Launches.Add(item);
            var fixture = WinGetCancellationTests.Fixture(Mode switch
            {
                "quiet-delegate" => Mode,
                "reboot" or "failure" => "exit-code",
                "early-exit" or "cancel-zero" => "wait-for-release",
                _ => "success",
            });
            if (Mode is "quiet-delegate" or "early-exit" or "cancel-zero")
            {
                fixture.ArgumentList.Add(Marker);
                fixture.ArgumentList.Add(Release);
            }
            if (Mode is "reboot" or "failure") fixture.ArgumentList.Add(Mode == "reboot" ? "3010" : "9");
            FileStream? identityWriteLock = null;
            if (Mode == "no-identity") control.PersistRoot = null;
            var afterStart = Mode switch
            {
                "identity-write-failure" => new OnDispose(() => identityWriteLock = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)),
                "early-exit" => new OnDispose(() =>
                {
                    using var root = WaitForFixture();
                    File.WriteAllText(Release, "release");
                    if (!root.WaitForExit(5000)) throw new IOException("Fixture did not exit before Job association.");
                    _registeredTargets.Add(item);
                }),
                _ => null,
            };
            var run = ProcessRunner.RunWinGetAsync(fixture, line =>
            {
                if (Mode != "no-registration") _registeredTargets.Add(item);
                output?.Invoke(line);
            }, token, control, externalInstallerPossible, afterStart is null ? null : () => afterStart);
            if (Mode == "cancel-zero")
            {
                using var root = await Task.Run(WaitForFixture, CancellationToken.None);
                ViewModel.CancelUpdateCommand.Execute(null);
                await WinGetCancellationTests.WaitUntilAsync(() => control.State == WinGetControlState.NeedsDecision);
                _registeredTargets.Add(item);
                File.WriteAllText(Release, "release");
            }
            try { RunResult = await run; }
            finally { identityWriteLock?.Dispose(); }
            Observations.Add(RunResult.Observation);
            return RunResult;
        }

        private Process WaitForFixture()
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (File.Exists(Marker) && int.TryParse(File.ReadAllText(Marker), out var pid)) return Process.GetProcessById(pid);
                Thread.Sleep(20);
            }
            throw new IOException("Fixture did not publish its PID.");
        }

        private sealed class OnDispose(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }
}
