using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public class MachineUninstallTests
{
    // Read-only probe. Opt in on the owner's computer, where this exact entry is installed.
    [Fact]
    public async Task Auslogics_registered_command_is_eligible_when_the_local_probe_is_enabled()
    {
        if (Environment.GetEnvironmentVariable("WINCORE_PROBE_AUSLOGICS") != "1") return;
        using var registry = new WindowsUninstallRegistryView();
        var entries = await new RegistryProgramScanner(registry).GetInstalledProgramsAsync();
        var app = Assert.Single(entries, entry => entry.Name == "Auslogics Disk Defrag Ultimate"
            && entry.DisplayVersion == "4.13.0.2");
        Assert.False(app.Is64Bit);
        var plan = new UninstallPlanner().CreatePlan(app, silentPreferred: false);
        Assert.True(plan.IsExecutable, plan.NotExecutableReason);
        var executor = new ProcessUninstallExecutor();
        Assert.Null(executor.GetLaunchRestriction(plan));
        Assert.Null(executor.PrepareForConsent(plan));
        Assert.NotNull(plan.ApprovedImage);
        Assert.False(plan.ConsentGranted);
    }

    [Theory]
    [InlineData(ProgramKind.InnoSetup, false)]
    [InlineData(ProgramKind.InnoSetup, true)]
    [InlineData(ProgramKind.Nsis, true)]
    [InlineData(ProgramKind.Executable, true)]
    [InlineData(ProgramKind.Msi, true)]
    public Task Wpf_confirmation_executes_only_the_selected_fixture_and_requeries_it(ProgramKind kind, bool machine) =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new Lab(kind, machine);
            var model = await lab.LoadAsync();
            Assert.True(Assert.Single(model.Catalog).CanSelect);
            model.AllSelected = true;
            Assert.Equal(1, model.SelectedCount);
            var confirmed = false;
            model.ConfirmHandler = request =>
            {
                Assert.Empty(lab.History.Records);
                Assert.True(File.Exists(lab.Installed));
                Assert.Same(lab.Product, Assert.Single(request.Programs));
                Assert.Equal(machine, request.Warnings.Contains(LocalizationService.Source("Message.Uninstaller.UacWarning")));
                Assert.Equal(LocalizationService.Source(machine ? "Uninstaller.ScopeMachine" : "Uninstaller.ScopeUser"),
                    request.Programs[0].InstallationScope);
                var dialog = new UninstallConfirmDialog(request) { ShowInTaskbar = false };
                var click = dialog.Dispatcher.InvokeAsync(() =>
                {
                    try { Assert.IsType<Button>(dialog.FindName("ConfirmButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent)); }
                    finally { if (dialog.IsVisible) dialog.DialogResult = false; }
                }, DispatcherPriority.ApplicationIdle);
                confirmed = dialog.ShowDialog() == true;
                return Task.FromResult(confirmed && click.Task.IsCompletedSuccessfully);
            };

            await model.UninstallSelectedCommand.ExecuteAsync(null);

            Assert.True(confirmed);
            Assert.Equal(machine ? 1 : 0, lab.UacRequests);
            Assert.False(File.Exists(lab.Installed));
            Assert.Equal(HistoryStatus.Success, Assert.Single(lab.History.Records).Status);
            Assert.Empty(model.Catalog);
            Assert.Equal(2, lab.InventoryReads);
            Assert.False(lab.Store.HasReceipt);
            Assert.Equal(1, lab.Releases);
        });

    [Fact]
    public Task Declining_the_Wpf_confirmation_never_requests_elevation() => WpfTestThread.Run(async () =>
    {
        using var lab = new Lab();
        var model = await lab.LoadAsync();
        model.ConfirmHandler = _ => Task.FromResult(false);
        model.AllSelected = true;
        await model.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, lab.UacRequests);
        Assert.Empty(lab.History.Records);
        Assert.True(File.Exists(lab.Installed));
        Assert.False(lab.Store.HasReceipt);
    });

    [Fact]
    public Task Uac_refusal_records_cancellation_and_releases_the_modules() => WpfTestThread.Run(async () =>
    {
        using var lab = new Lab() { DenyUac = true };
        await lab.ExecuteAsync();
        Assert.Equal(1, lab.UacRequests);
        Assert.True(File.Exists(lab.Installed));
        var history = Assert.Single(lab.History.Records);
        Assert.Equal(HistoryStatus.Cancelled, history.Status);
        Assert.Equal(LocalizationService.Source("Message.AdministratorApprovalWasCanceled"), history.Message);
        Assert.False(lab.Store.HasReceipt);
        Assert.Equal(1, lab.Releases);
    });

    [Fact]
    public Task A_pending_Uac_request_keeps_the_reservation_until_Windows_answers() => WpfTestThread.Run(async () =>
    {
        using var response = new ManualResetEventSlim(false);
        using var lab = new Lab() { DenyUac = true, UacResponse = response };
        var model = await lab.LoadAsync();
        model.AllSelected = true;
        model.ConfirmHandler = _ => Task.FromResult(true);
        var run = model.UninstallSelectedCommand.ExecuteAsync(null);
        try
        {
            await lab.UacRequested.Task.WaitAsync(TimeSpan.FromSeconds(8));
            model.CancelUninstallCommand.Execute(null);
            Assert.False(run.IsCompleted);
            Assert.True(lab.Store.HasReceipt);
            Assert.Equal(0, lab.Releases);
        }
        finally { response.Set(); }
        await run;
        Assert.False(lab.Store.HasReceipt);
        Assert.Equal(HistoryStatus.Cancelled, Assert.Single(lab.History.Records).Status);
    });

    [Fact]
    public Task Completion_is_persisted_in_the_portable_history_database() => WpfTestThread.Run(async () =>
    {
        using var lab = new Lab(persistentHistory: true);
        await lab.ExecuteAsync();
        var history = new HistoryService(lab.Settings);
        var record = Assert.Single(await history.RecentAsync(1));
        Assert.Equal(lab.Product.Name, record.Target);
        Assert.Equal(HistoryStatus.Success, record.Status);
        Assert.Equal(0, record.ExitCode);
        Assert.False(lab.Store.HasReceipt);
    });

    [Theory]
    [InlineData("stay", HistoryStatus.Failure, 0, false)]
    [InlineData("failure", HistoryStatus.Failure, 23, false)]
    [InlineData("installer-cancel", HistoryStatus.Cancelled, 1602, false)]
    [InlineData("reboot", HistoryStatus.Interrupted, 3010, true)]
    public Task Result_and_receipt_follow_process_and_inventory_evidence(string behavior, HistoryStatus status,
        int exitCode, bool pending) => WpfTestThread.Run(async () =>
    {
        using var lab = new Lab(behavior: behavior);
        var model = await lab.ExecuteAsync();
        var result = Assert.Single(lab.History.Records);
        Assert.Equal(status, result.Status);
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Single(model.Catalog);
        Assert.Equal(pending, lab.Store.HasReceipt);
        if (behavior == "reboot")
        {
            Assert.Equal(LocalizationService.Source("Message.CompleteRestartingWindowsIsRecommended"), result.Message);
            using var receipt = lab.Store.Open();
            Assert.True(receipt.Outcome?.RestartRequired);
            Assert.Equal(3010, receipt.Outcome?.ExitCode);
            Assert.False(PackageChangeReview.CanReviewWithoutRestart(receipt, receipt.Boot,
                new PackageActivity(PackageProcessState.Exited, PackageProcessState.Unknown, null)));
        }
    });

    [Theory]
    [InlineData("rebuilt-image")]
    [InlineData("changed-registration")]
    public Task Changes_after_confirmation_was_prepared_prevent_launch(string change) => WpfTestThread.Run(async () =>
    {
        using var lab = new Lab();
        await lab.ExecuteAsync(_ =>
        {
            if (change == "changed-registration") lab.RegistrationMatches = false;
            else
            {
                // Same bytes and path, different file identity.
                File.Move(lab.Image, lab.Image + ".old");
                File.Copy(lab.Image + ".old", lab.Image);
            }
            return Task.FromResult(true);
        });
        Assert.Equal(0, lab.UacRequests);
        Assert.True(File.Exists(lab.Installed));
        Assert.Equal(HistoryStatus.NotExecutable, Assert.Single(lab.History.Records).Status);
        Assert.False(lab.Store.HasReceipt);
    });

    [Theory]
    [InlineData("interactive", false)]
    [InlineData("delegated", false)]
    [InlineData("delegated", true)]
    public Task Waits_for_the_interactive_root_or_known_child_and_cancels_without_killing(string behavior, bool cancel) =>
        WpfTestThread.Run(async () =>
        {
            using var lab = new Lab(behavior: behavior);
            var model = await lab.LoadAsync();
            model.AllSelected = true;
            model.ConfirmHandler = _ => Task.FromResult(true);
            var run = model.UninstallSelectedCommand.ExecuteAsync(null);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (!File.Exists(lab.Started) || new FileInfo(lab.Started).Length == 0)
                    await Task.Delay(20, deadline.Token);
                Assert.True(model.IsUninstalling && lab.Store.HasReceipt);
                Assert.False(run.IsCompleted);
                if (cancel)
                {
                    while (File.Exists(lab.Installed)) await Task.Delay(20, deadline.Token);
                    model.CancelUninstallCommand.Execute(null);
                    await run.WaitAsync(deadline.Token);
                    Assert.True(lab.Store.HasReceipt);
                    Assert.Equal(HistoryStatus.Interrupted, Assert.Single(lab.History.Records).Status);
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(lab.Started)));
                    Assert.False(child.HasExited);
                }
            }
            finally { File.WriteAllText(lab.Release, "continue"); }
            await run;
            if (!cancel)
            {
                Assert.Equal(HistoryStatus.Success, Assert.Single(lab.History.Records).Status);
                Assert.False(lab.Store.HasReceipt);
            }
            else
            {
                try
                {
                    using var child = Process.GetProcessById(int.Parse(File.ReadAllText(lab.Started)));
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { } // Already exited after the release marker.
                using var receipt = lab.Store.Open();
                Assert.Equal(DelegatedProcessState.KnownActive, receipt.Outcome?.Delegation);
                Assert.True(PackageChangeReview.CanReviewWithoutRestart(receipt, receipt.Boot,
                    PackageChangeReview.ObserveActivity(receipt, receipt.Boot)));
            }
        });

    [Fact]
    public Task Lingering_second_phase_after_the_entry_left_completes_and_offers_leftovers() => WpfTestThread.Run(async () =>
    {
        // Auslogics (Inno): unins000 exited 0 and removed the entry while _iu*.tmp stayed open with no window.
        using var lab = new Lab(behavior: "delegated") { SettleWindow = TimeSpan.FromMilliseconds(500) };
        var model = await lab.LoadAsync();
        var offered = new List<string>();
        var scanned = new List<InstalledProgram>();
        model.OfferResidueScanHandler = name => { offered.Add(name); return true; };
        model.ShowResidueScanHandler = program => { scanned.Add(program); return Task.CompletedTask; };
        try
        {
            model.AllSelected = true;
            model.ConfirmHandler = _ => Task.FromResult(true);
            await model.UninstallSelectedCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(15));
            var record = Assert.Single(lab.History.Records);
            Assert.Equal(HistoryStatus.Success, record.Status);
            Assert.Contains(LocalizationService.Source("Message.Uninstaller.Lingering", lab.Product.Name), record.Message);
            Assert.False(lab.Store.HasReceipt);
            using var child = Process.GetProcessById(int.Parse(File.ReadAllText(lab.Started)));
            Assert.False(child.HasExited); // reported, never killed
            Assert.Equal(new[] { lab.Product.Name }, offered);
            Assert.Equal(new[] { lab.Product }, scanned);
            Assert.Contains(LocalizationService.Source("Message.Uninstaller.Running", lab.Product.Name), model.LogLines);
        }
        finally
        {
            File.WriteAllText(lab.Release, "continue");
            try
            {
                using var child = Process.GetProcessById(int.Parse(File.ReadAllText(lab.Started)));
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { } // Already exited after the release marker.
        }
    });

    [Fact]
    public Task Failed_or_declined_leftover_offer_opens_nothing() => WpfTestThread.Run(async () =>
    {
        using var lab = new Lab(behavior: "failure");
        var model = await lab.LoadAsync();
        var offered = 0;
        var scanned = 0;
        model.OfferResidueScanHandler = _ => { offered++; return true; };
        model.ShowResidueScanHandler = _ => { scanned++; return Task.CompletedTask; };
        model.AllSelected = true;
        model.ConfirmHandler = _ => Task.FromResult(true);
        await model.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(0, offered + scanned);

        using var removed = new Lab();
        var accepted = await removed.LoadAsync();
        accepted.OfferResidueScanHandler = _ => false;
        accepted.ShowResidueScanHandler = _ => { scanned++; return Task.CompletedTask; };
        accepted.AllSelected = true;
        accepted.ConfirmHandler = _ => Task.FromResult(true);
        await accepted.UninstallSelectedCommand.ExecuteAsync(null);
        Assert.Equal(HistoryStatus.Success, Assert.Single(removed.History.Records).Status);
        Assert.Equal(0, scanned);
    });

    [Theory]
    [InlineData("\"C:\\Program Files\\Demo\\remove.exe\"/silent")]
    [InlineData("\"C:\\Program Files\\Demo\\remove.exe\" \"unclosed")]
    [InlineData("\"C:\\Program Files\\Demo\\remove.exe\" /a & whoami")]
    [InlineData("C:\\Program Files\\Demo\\remove.exe\n /a")]
    [InlineData("C:\\Program Files\\Demo\\remove.exe.backup /a")]
    public void Ambiguous_or_malformed_registered_commands_are_rejected(string command) =>
        Assert.False(MachineUninstallPolicy.IsExplicitCommand(command, @"C:\Program Files\Demo\remove.exe",
            command.EndsWith("whoami") ? "/a & whoami" : command.EndsWith("unclosed") ? "\"unclosed" : "/a"));

    [Fact]
    public void An_existing_executable_in_the_writable_test_directory_is_not_a_protected_machine_target()
    {
        using var lab = new Lab();
        Assert.False(MachineUninstallPolicy.IsProtectedImagePath(lab.Image));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Demo\\remove.exe\"", "")]
    [InlineData("C:\\Program Files\\Demo\\remove.exe /uninstall", "/uninstall")]
    [InlineData("\"C:\\Program Files\\Demo\\remove.exe\" /log \"C:\\Log files\\a.log\"", "/log \"C:\\Log files\\a.log\"")]
    public void The_explicit_executable_and_its_original_arguments_are_preserved(string command, string arguments) =>
        Assert.True(MachineUninstallPolicy.IsExplicitCommand(command, @"C:\Program Files\Demo\remove.exe", arguments));

    private sealed class Lab : IProgramInventoryService, IDisposable
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("wincore-uninstall-").FullName;
        private readonly string _behavior;
        private readonly UninstallersViewModel _model;
        internal string Image { get; }
        internal string Installed => Path.Combine(_directory, "synthetic-registration");
        internal string Started => Path.Combine(_directory, "synthetic-started");
        internal string Release => Path.Combine(_directory, "synthetic-release");
        internal InstalledProgram Product { get; }
        internal readonly FakeHistoryService History = new();
        internal ISettingsService Settings { get; }
        internal WinGetInterruptionStore Store { get; }
        internal TaskCompletionSource UacRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim? UacResponse { get; init; }
        internal int UacRequests { get; private set; }
        internal int InventoryReads { get; private set; }
        internal int Releases { get; private set; }
        internal bool DenyUac { get; init; }
        internal bool RegistrationMatches { get; set; } = true;

        internal Lab(ProgramKind kind = ProgramKind.InnoSetup, bool machine = true, string behavior = "remove",
            bool persistentHistory = false)
        {
            _behavior = behavior;
            foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "WinGetFixture")))
                File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
            Image = Path.Combine(_directory, kind == ProgramKind.InnoSetup ? "unins000.exe" : "Uninstall.exe");
            File.Move(Path.Combine(_directory, "WinGetFixture.exe"), Image);
            File.WriteAllText(Installed, "Only this synthetic marker represents the application.");
            var productId = Guid.NewGuid();
            var key = kind == ProgramKind.Msi ? productId.ToString("B") : "WinCoreSynthetic";
            var scope = machine ? "HKLM" : "HKCU";
            Product = new InstalledProgram
            {
                Name = $"Synthetic {kind}",
                Publisher = "WinCore Test Fixture",
                Origin = scope,
                RegistryKeyName = key,
                RegistryPath = $@"{scope}\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{key}",
                Kind = kind,
                InstallLocation = _directory,
                UninstallString = kind == ProgramKind.Msi ? $"MsiExec.exe /I{productId:B}" : $"\"{Image}\" {FixtureArguments}",
                MsiProductCode = kind == ProgramKind.Msi ? productId : Guid.Empty,
            };
            Settings = new IsolatedSettings(Path.Combine(_directory, "data"));
            var boot = DateTime.UtcNow.AddDays(-1).Ticks.ToString(CultureInfo.InvariantCulture);
            Store = new WinGetInterruptionStore(Settings.DataDirectory, () => boot);
            var planner = new UninstallPlanner();
            var executor = new ProcessUninstallExecutor(
                plan => RegistrationMatches && (plan.Method == UninstallMethod.Msi ||
                    MachineUninstallPolicy.IsExplicitCommand(Product.UninstallString, plan.ExecutablePath, plan.Arguments)),
                path => path == Image, StartWithSimulatedUac, program => File.Exists(Installed));
            IHistoryService history = persistentHistory ? new HistoryService(Settings) : History;
            _model = new UninstallersViewModel(this, planner, new(planner, executor, history), new FakeResidueAnalysisService())
            {
                TryReservePackageChange = Reserve,
            };
        }

        private string FixtureArguments => $"uninstall-fixture {_behavior}";

        private Process? StartWithSimulatedUac(ProcessStartInfo start)
        {
            UacRequests++;
            Assert.True(start.UseShellExecute);
            Assert.Equal("runas", start.Verb);
            UacRequested.TrySetResult();
            if (UacResponse is not null && !UacResponse.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            if (DenyUac) throw new Win32Exception(1223);
            if (Product.Kind == ProgramKind.Msi)
            {
                Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), start.FileName);
                Assert.Equal($"/x{Product.MsiProductCode:B} REBOOT=ReallySuppress /norestart", start.Arguments);
                start.FileName = Image;
                start.Arguments = FixtureArguments;
                start.WorkingDirectory = _directory;
            }
            Assert.Equal(Image, start.FileName);
            start.Verb = "open"; // Actual ShellExecute process handle, with the UAC decision simulated.
            start.WindowStyle = ProcessWindowStyle.Hidden;
            return Process.Start(start);
        }

        internal TimeSpan? SettleWindow { get; init; }

        private bool Reserve(PackageChangeKind kind, string target, out PackageChangeReservation? reservation)
        {
            reservation = new PackageChangeReservation(Store.BeginExternal(kind, target), () => Releases++);
            if (SettleWindow is { } window) reservation.Control.DelegateSettleWindow = window;
            return true;
        }

        internal async Task<UninstallersViewModel> LoadAsync()
        {
            await _model.LoadCommand.ExecuteAsync(null);
            return _model;
        }

        internal async Task<UninstallersViewModel> ExecuteAsync(Func<UninstallConfirmationRequest, Task<bool>>? confirm = null)
        {
            var model = await LoadAsync();
            model.AllSelected = true;
            model.ConfirmHandler = confirm ?? (_ => Task.FromResult(true));
            await model.UninstallSelectedCommand.ExecuteAsync(null);
            return model;
        }

        public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default)
        {
            InventoryReads++;
            return Task.FromResult<IReadOnlyList<InstalledProgram>>(File.Exists(Installed) ? [Product] : []);
        }

        public void Dispose()
        {
            if (File.Exists(Settings.HistoryDatabasePath))
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = Settings.HistoryDatabasePath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    DefaultTimeout = 5,
                };
                using var database = new SqliteConnection(connectionString.ToString());
                SqliteConnection.ClearPool(database);
            }
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class IsolatedSettings(string directory) : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public string DataDirectory => directory;
        public string HistoryDatabasePath => Path.Combine(directory, "history.db");
        public void Save() { }
    }
}
