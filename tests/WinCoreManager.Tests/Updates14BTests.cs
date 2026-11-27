using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;
using Xunit;

namespace WinCoreManager.Tests;

public sealed class Updates14BTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Stop_before_first_item_or_between_items_does_not_start_the_next_item(bool afterFirst) => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(10);
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        vm.PropertyChanged += StopAtBoundary;

        await vm.UpdateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(afterFirst ? 1 : 0, service.Launched.Count);
        Assert.Equal(afterFirst ? 1 : 0, history.Records.Count);
        Assert.Contains(LocalizationService.Source("UpdateOperation.QueueStopped"), vm.OperationStatus);
        Assert.Contains(LocalizationService.Source("UpdateOperation.BatchTotals", afterFirst ? 1 : 0, 0, 0, 0, afterFirst ? 9 : 10), vm.StatusText);
        Assert.False(vm.CancelUpdateCommand.CanExecute(null));
        Assert.True(vm.UpdateSelectedCommand.CanExecute(null));

        void StopAtBoundary(object? sender, PropertyChangedEventArgs args)
        {
            if ((!afterFirst && args.PropertyName == nameof(vm.IsUpdating) && vm.IsUpdating) ||
                (afterFirst && args.PropertyName == nameof(vm.OperationStatus) &&
                 vm.OperationStatus == LocalizationService.Source("UpdateOperation.Completed")))
            {
                vm.PropertyChanged -= StopAtBoundary;
                Assert.True(vm.CancelUpdateCommand.CanExecute(null));
                vm.CancelUpdateCommand.Execute(null);
                Assert.False(vm.CancelUpdateCommand.CanExecute(null));
                vm.CancelUpdateCommand.Execute(null);
            }
        }
    });

    [Fact]
    public Task Cancellation_during_launch_preparation_is_not_counted_as_an_executed_update() => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(10);
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        service.Run = (_, _, _) =>
        {
            vm.CancelUpdateCommand.Execute(null);
            return Task.FromResult(new ProcessRunResult { StartFailed = true, Cancelled = true, HasExitCode = false });
        };

        await vm.UpdateSelectedCommand.ExecuteAsync(null);

        Assert.Single(service.Launched);
        Assert.All(vm.Rows, row => Assert.Equal(UpdateRowStatus.Idle, row.Status));
        Assert.Empty(history.Records);
        Assert.Contains(LocalizationService.Source("UpdateOperation.BatchTotals", 0, 0, 0, 0, 10), vm.StatusText);
    });

    [Fact]
    public void Download_uses_reported_bytes_and_timed_advances_with_no_rate_for_stale_or_new_sessions()
    {
        var meter = new WinGetDownloadProgress();
        var start = Stopwatch.GetTimestamp();
        Assert.False(meter.Observe("-", start));
        Assert.False(meter.Observe("████ 42%", start));
        Assert.False(meter.Observe("200 MB", start));
        Assert.Null(meter.Read(start));
        Assert.True(meter.Observe(WinGetDownloadProgress.StripControls("\x1b[32m██░ 50.0 MB / 200 MB\x1b[0m"), start));
        Assert.Equal(25, meter.Read(start)!.Value.Percent);
        Assert.Null(meter.Read(start)!.Value.BytesPerSecond);

        var next = start + 2 * Stopwatch.Frequency;
        Assert.True(meter.Observe("██░ 62.0 MB / 200 MB", next));
        Assert.Equal(6 * (1 << 20), meter.Read(next)!.Value.BytesPerSecond);
        Assert.Equal(23, meter.Read(next)!.Value.RemainingSeconds);
        Assert.Null(meter.Read(next + 6 * Stopwatch.Frequency)!.Value.BytesPerSecond);
        Assert.True(meter.Observe("20.0 MB / 1.25 GB", next));
        Assert.Null(meter.Read(next)!.Value.BytesPerSecond);
        Assert.True(meter.Observe("400 MB / 200 MB", next));
        Assert.Null(meter.Read(next));
    }

    [Fact]
    public Task Real_pipe_delivers_cr_and_vt_samples_and_stop_ends_the_controlled_download() => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(10) { Version = _ => "1.0" };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history);
        ProcessRunResult? observed = null;
        service.Run = async (_, control, token) => observed = await ProcessRunner.RunWinGetAsync(WinGetCancellationTests.Fixture("download-progress"),
            line => service.Output!(line), token, control, externalInstallerPossible: false);
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        var run = vm.UpdateSelectedCommand.ExecuteAsync(null);
        try
        {
            await WinGetCancellationTests.WaitUntilAsync(() => vm.DownloadRate.Length > 0);
            Assert.True(vm.ShowDownloadProgress);
            Assert.False(vm.DownloadIndeterminate);
            Assert.InRange(vm.DownloadPercent, 42.6, 42.8);
            Assert.Contains("85,4 MB / 200,0 MB", vm.DownloadSize);
            Assert.DoesNotContain(vm.LogLines, line => line.Contains("▒", StringComparison.Ordinal));
        }
        finally
        {
            vm.CancelUpdateCommand.Execute(null);
            await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Single(service.Launched);
        var record = Assert.Single(history.Records);
        Assert.True(record.Status == HistoryStatus.Cancelled,
            $"{observed?.Observation} / exit={observed?.HasExitCode}:{observed?.ExitCode} / cancelled={observed?.Cancelled} / interrupted={observed?.Interrupted}\n{observed?.FullOutput}");
        Assert.Equal(9, vm.RemainingUpdates);
        Assert.False(vm.HasSuspendedQueue || vm.ShowDownloadProgress);
    });

    [Theory]
    [InlineData("unchanged")]
    [InlineData("different")]
    [InlineData("cancel-inventory")]
    [InlineData("other-instance")]
    [InlineData("restart")]
    [InlineData("claimed")]
    [InlineData("damaged-queue")]
    [InlineData("voluntary")]
    public Task Docker_receipt_holds_nine_items_until_review_and_explicit_resume_with_fresh_inventory(string scenario) => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        settings.Settings.UpdatesListRatio = .50;
        var boot = WinGetOperationTests.BootBefore;
        var store = WinGetOperationTests.ReviewStore(settings, () => boot);
        var packages = Enumerable.Range(1, 10).Select(index => new WinGetPackage
        {
            Id = index == 1 ? "Docker.DockerDesktop" : $"Fixture.{index}",
            Name = index == 1 ? "Docker Desktop" : $"Fixture {index}",
            InstalledVersion = "1.0", AvailableVersion = "2.0", Source = "winget",
        }).ToArray();
        var service = new WinGetOperationFixture(0) { Packages = packages, Version = package => package.Id == packages[0].Id ? "1.0" : "2.0" };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history, settings, store);
        service.Run = (package, control, _) =>
        {
            control.RootIdentity = new(int.MaxValue, 1);
            if (package.Id != packages[0].Id) return Task.FromResult(new ProcessRunResult { Observation = WinGetOperationFixture.Complete });
            if (scenario == "voluntary") vm.CancelUpdateCommand.Execute(null);
            return Task.FromResult(new ProcessRunResult { ExitCode = 0, Cancelled = scenario == "voluntary" });
        };
        await vm.LoadCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
        Assert.Equal(9, vm.RemainingUpdates);
        Assert.Equal(scenario == "voluntary" ? 0 : 9, vm.SuspendedUpdateCount);
        Assert.True(store.HasReceipt);
        Assert.False(vm.CancelUpdateCommand.CanExecute(null) || vm.ShowResumeUpdates || vm.UpdateSelectedCommand.CanExecute(null));
        await vm.RecheckInterruptionAsync();
        Assert.True(store.HasReceipt);
        Assert.Single(service.Launched);
        if (scenario == "unchanged") await CaptureRecoveryAsync(vm, "fila-suspensa-Dark-pt-BR");

        boot = WinGetOperationTests.BootAfter;
        if (scenario == "damaged-queue")
        {
            File.WriteAllText(Path.Combine(settings.DataDirectory, "winget-update.json.queue"), "{invalid");
            vm = new UpdatesViewModel(service, history, settings, store);
            Assert.True(vm.HasInterruptedUpdate);
            Assert.False(vm.HasSuspendedQueue || vm.ShowResumeUpdates);
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.AllSelected = true;
        }
        if (scenario == "restart")
        {
            vm = new UpdatesViewModel(service, history, settings, store);
            Assert.True(vm.HasInterruptedUpdate);
            Assert.Equal(9, vm.SuspendedUpdateCount);
            Assert.False(vm.ShowResumeUpdates);
            await vm.RefreshCommand.ExecuteAsync(null);
            vm.AllSelected = true;
        }
        await vm.RecheckInterruptionAsync();
        await vm.RecheckInterruptionAsync("1.0");
        Assert.False(store.HasReceipt);
        Assert.Equal(2, history.Records.Count);
        Assert.Single(service.Launched);
        Assert.True(vm.UpdateSelectedCommand.CanExecute(null));
        if (scenario is "voluntary" or "damaged-queue")
        {
            Assert.False(vm.ShowResumeUpdates);
            return;
        }

        Assert.True(vm.ShowResumeUpdates);
        if (scenario == "unchanged") await CaptureRecoveryAsync(vm, "retomada-liberada-Dark-pt-BR");
        var secondInstance = scenario == "claimed" ? new UpdatesViewModel(service, history, settings, store) : null;
        if (scenario == "restart")
        {
            vm = new UpdatesViewModel(service, history, settings, store);
            Assert.Equal(9, vm.SuspendedUpdateCount);
            Assert.True(vm.ShowResumeUpdates);
        }
        if (scenario == "different")
        {
            service.Packages = [packages[0], new() { Name = "Fixture 2", Id = "Fixture.2", Source = "winget", InstalledVersion = "1.1", AvailableVersion = "3.0" }, .. packages.Skip(3)];
        }
        else if (scenario == "cancel-inventory")
        {
            service.BeforeListUpdates = () => { vm.CancelUpdateCommand.Execute(null); return Task.CompletedTask; };
        }

        WinGetInterruptionStore.Receipt? competing = null;
        if (scenario == "other-instance")
            service.BeforeListUpdates = () => { competing = store.BeginPrepared(packages[0]); return Task.CompletedTask; };
        try
        {
            await vm.ResumeUpdatesCommand.ExecuteAsync(null);
            if (scenario is "unchanged" or "restart" or "claimed")
            {
                Assert.Equal(packages.Select(package => package.Id), service.Launched);
                Assert.Equal(11, history.Records.Count);
                if (secondInstance is not null)
                {
                    await secondInstance.ResumeUpdatesCommand.ExecuteAsync(null);
                    Assert.Equal(10, service.Launched.Count);
                }
            }
            else
            {
                Assert.Single(service.Launched);
                if (scenario == "different")
                {
                    Assert.Contains(vm.ResumeDifferences, entry => entry.Contains("1.1 → 3.0"));
                    Assert.Contains(vm.ResumeDifferences, entry => entry.Contains("Fixture 3"));
                    Assert.Equal(8, vm.SelectedCount);
                    Assert.False(vm.Rows.Single(row => row.Id == packages[0].Id).IsSelected);
                    await CaptureRecoveryAsync(vm, "inventario-alterado-Dark-pt-BR");
                }
                else Assert.Equal(9, vm.SuspendedUpdateCount);
            }
        }
        finally { competing?.Dispose(); }
    });

    [Fact]
    public Task Stop_with_no_positive_delegate_identity_keeps_the_receipt_and_history() => WpfTestThread.Run(async () =>
    {
        var settings = new TestSettings();
        var store = WinGetOperationTests.ReviewStore(settings, () => WinGetOperationTests.BootBefore);
        var service = new WinGetOperationFixture(10) { Version = _ => null };
        var history = new FakeHistoryService();
        var vm = new UpdatesViewModel(service, history, settings, store);
        service.Run = (_, control, _) =>
        {
            control.PersistLaunchIntent!();
            vm.CancelUpdateCommand.Execute(null);
            return Task.FromResult(new ProcessRunResult
            {
                Interrupted = true, HasExitCode = false,
                Observation = new(ControlledProcessState.NotObserved, DelegatedProcessState.Possible),
            });
        };
        await vm.RefreshCommand.ExecuteAsync(null);
        vm.AllSelected = true;
        await vm.UpdateSelectedCommand.ExecuteAsync(null);
        await vm.DismissWarningCommand.ExecuteAsync(null);
        Assert.True(vm.HasInterruptedUpdate && store.HasReceipt);
        Assert.False(vm.ShowResumeUpdates || vm.UpdateSelectedCommand.CanExecute(null));
        Assert.Equal(HistoryStatus.Interrupted, Assert.Single(history.Records).Status);
        using var receipt = store.Open();
        Assert.Null(receipt.Identity);
        Assert.Equal(DelegatedProcessState.Possible, receipt.Outcome?.Delegation);
        Assert.False(receipt.Outcome?.CancellationConfirmed);
    });

    [Fact]
    public async Task Stop_batch_leaves_a_known_external_delegate_alive()
    {
        var observed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var control = new WinGetProcessControl();
        var run = ProcessRunner.RunWinGetAsync(WinGetCancellationTests.Fixture("escaped"), line =>
        {
            if (line.StartsWith("escaped ", StringComparison.Ordinal)) observed.TrySetResult(int.Parse(line[8..]));
        }, cancellation.Token, control);
        using var delegateProcess = Process.GetProcessById(await observed.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        try
        {
            control.RequestBatchStop();
            cancellation.Cancel();
            var result = await run.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.True(result.Interrupted);
            Assert.False(result.Cancelled || delegateProcess.HasExited);
            Assert.Equal(DelegatedProcessState.KnownActive, result.Observation.Delegation);
        }
        finally
        {
            if (!delegateProcess.HasExited) delegateProcess.Kill();
            await delegateProcess.WaitForExitAsync();
        }
    }

    [Theory]
    [InlineData("pt-BR", AppTheme.Light)]
    [InlineData("en-US", AppTheme.Light)]
    [InlineData("pt-BR", AppTheme.Dark)]
    [InlineData("en-US", AppTheme.Dark)]
    public Task Wpf_download_phases_and_stop_are_accessible_in_both_languages_and_themes(string language, AppTheme theme) =>
        WpfTestThread.Run(async () =>
    {
        ThemeResources.Install(Application.Current.Resources, theme);
        LocalizationService.Current.Apply(language);
        var english = language == "en-US";
        var settings = new TestSettings();
        settings.Settings.UpdatesListRatio = .50;
        var service = new WinGetOperationFixture(10) { Version = _ => "1.0" };
        var vm = new UpdatesViewModel(service, new FakeHistoryService(), settings, new WinGetInterruptionStore(settings.DataDirectory));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Run = async (_, control, token) =>
        {
            control.RootIdentity = new(int.MaxValue, 1);
            using var registration = token.Register(() => stopRequested.TrySetResult());
            await release.Task;
            return new() { ExitCode = 1602, Cancelled = true, Observation = WinGetOperationFixture.Complete };
        };
        var page = new UpdatesPage { DataContext = vm };
        var window = Open(page);
        Task? run = null;
        try
        {
            await vm.LoadCommand.ExecutionTask!;
            vm.AllSelected = true;
            run = vm.UpdateSelectedCommand.ExecuteAsync(null);
            await WinGetCancellationTests.WaitUntilAsync(() => service.Output is not null);
            var button = Assert.IsType<Button>(page.FindName("StopAllUpdatesButton"));
            var progress = Assert.IsType<ProgressBar>(page.FindName("UpdateDownloadProgress"));
            var status = Assert.IsType<TextBlock>(page.FindName("UpdateOperationStatus"));
            var peer = new ButtonAutomationPeer(button);
            Assert.Equal(english ? "Stop all updates" : "Parar todas as atualizações", peer.GetName());
            Assert.False(string.IsNullOrWhiteSpace(peer.GetHelpText()));
            Assert.True(button.IsEnabled && button.IsTabStop && button.Focusable && button.IsVisible);
            Assert.Equal(Assert.IsType<SolidColorBrush>(page.FindResource("TextPrimaryBrush")).Color,
                Assert.IsType<SolidColorBrush>(status.Foreground).Color);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(status));
            Assert.Equal(english ? "Preparing" : "Preparando", status.Text);

            service.Output!("Downloading synthetic file");
            await WaitForStatus("DownloadIndeterminate");
            Assert.True(progress.IsIndeterminate && progress.IsVisible);
            await CaptureAsync(page, $"indeterminado-{theme}-{language}");

            service.Output("██░ 85.4 MB / 203.1 MB");
            await WaitForStatus("Downloading");
            Assert.False(progress.IsIndeterminate);
            Assert.InRange(progress.Value, 42.0, 42.1);
            Assert.Equal(english ? "Download progress" : "Progresso do download", AutomationProperties.GetName(progress));
            Assert.Equal(english ? "85.4 MB of 203.1 MB" : "85,4 MB / 203,1 MB", AutomationProperties.GetHelpText(progress));
            var range = Assert.IsAssignableFrom<IRangeValueProvider>(new ProgressBarAutomationPeer(progress).GetPattern(PatternInterface.RangeValue));
            Assert.Equal(progress.Value, range.Value);
            await Task.Delay(550);
            service.Output("██░ 88.0 MB / 203.1 MB");
            await WinGetCancellationTests.WaitUntilAsync(() => vm.DownloadEta.Length > 0);
            await CaptureAsync(page, $"download-{theme}-{language}");

            service.Output("Successfully verified installer hash");
            await WaitForStatus("Verifying");
            Assert.False(progress.IsVisible);
            Assert.Equal(0, vm.DownloadPercent);
            service.Output("Starting package install...");
            await WaitForStatus("Installing");

            if (theme == AppTheme.Light)
            {
                Assert.True(button.Focus());
                var presentation = PresentationSource.FromVisual(button)!;
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyDownEvent });
                button.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, presentation, Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.KeyUpEvent });
            }
            else Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke)).Invoke();

            await stopRequested.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitForStatus("QueueStopping");
            Assert.False(button.IsEnabled);
            await CaptureAsync(page, $"solicitando-parada-{theme}-{language}");
            release.TrySetResult();
            await run;
            await WaitForStatus("QueueStopped");
            Assert.False(button.IsVisible);
            await CaptureAsync(page, $"fila-interrompida-{theme}-{language}");

            async Task WaitForStatus(string key) => await WinGetCancellationTests.WaitUntilAsync(() =>
                status.Text == LocalizationService.Current["UpdateOperation." + key]);
        }
        finally
        {
            release.TrySetResult();
            if (run is not null) await run;
            window.Close();
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }
    });

    private static Window Open(UpdatesPage page)
    {
        var window = new Window
        {
            Content = page, Width = 1180, Height = 900, ShowInTaskbar = false, Opacity = 0,
        };
        window.SetResourceReference(Control.BackgroundProperty, "WindowBgBrush");
        window.Show();
        return window;
    }

    private static async Task CaptureRecoveryAsync(UpdatesViewModel viewModel, string name)
    {
        if (Environment.GetEnvironmentVariable("WINCORE_14B_SCREENSHOTS") is not { Length: > 0 }) return;
        ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        var page = new UpdatesPage { DataContext = viewModel };
        var window = Open(page);
        try { await CaptureAsync(page, name); }
        finally { window.Close(); }
    }

    private static async Task CaptureAsync(FrameworkElement element, string name)
    {
        if (Environment.GetEnvironmentVariable("WINCORE_14B_SCREENSHOTS") is not { Length: > 0 } path) return;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        element.UpdateLayout();
        var frame = new DrawingVisual();
        using (var drawing = frame.RenderOpen())
        {
            var bounds = new Rect(element.RenderSize);
            drawing.DrawRectangle((Brush)element.FindResource("WindowBgBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element), null, bounds);
        }
        var image = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        image.Render(frame);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(Path.Combine(path, name + ".png"));
        encoder.Save(file);
    }
}
