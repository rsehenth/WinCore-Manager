using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public sealed class StartupPresentationTests
{
    [Fact]
    public async Task Pesquisa_por_nome_fabricante_e_caminho_combina_com_filtros()
    {
        var state = new ScreenFixture();
        await state.Startup.RefreshCommand.ExecuteAsync(null);

        Assert.Equal("3 entradas encontradas", state.Startup.CountText);
        state.Startup.SearchText = "valve";
        Assert.Collection(state.Startup.Rows, row => Assert.Equal("Steam", row.Name));
        state.Startup.SearchText = "teams.EXE";
        Assert.Collection(state.Startup.Rows, row => Assert.Equal("Teams", row.Name));
        state.Startup.SearchText = "monitor";
        Assert.Collection(state.Startup.Rows, row => Assert.Equal("Monitor de segurança", row.Name));
        state.Startup.SearchText = "";
        state.Startup.SelectedStatus = state.Startup.StatusOptions.Single(option => option.Value == StartupFilter.Enabled);
        Assert.Equal(2, state.Startup.Rows.Count);
        state.Startup.SelectedStatus = state.Startup.StatusOptions.Single(option => option.Value == StartupFilter.Disabled);
        Assert.Collection(state.Startup.Rows, row => Assert.Equal("Teams", row.Name));
        state.Startup.SelectedStatus = state.Startup.StatusOptions.Single(option => option.Value == StartupFilter.Protected);
        Assert.Equal(2, state.Startup.Rows.Count);
        state.Startup.SelectedSource = state.Startup.SourceOptions.Single(option => option.Value == StartupSource.UserRun);
        Assert.Collection(state.Startup.Rows, row => Assert.Equal("Teams", row.Name));
        Assert.Equal("3 entradas encontradas · 1 na lista", state.Startup.CountText);
    }

    [Fact]
    public async Task Controle_protegido_fica_indisponivel_e_so_alteracao_confirmada_chega_ao_servico()
    {
        var state = new ScreenFixture();
        await state.Startup.RefreshCommand.ExecuteAsync(null);
        var command = state.Startup.ChangeCommand;
        var protectedRow = state.Startup.Rows.Single(row => row.Name == "Teams");
        var userRow = state.Startup.Rows.Single(row => row.Name == "Steam");

        Assert.False(command.CanExecute(protectedRow));
        Assert.True(command.CanExecute(userRow));
        await command.ExecuteAsync(protectedRow);
        Assert.Empty(state.Confirmation.Requests);
        await command.ExecuteAsync(userRow);
        Assert.Empty(state.Changes.Requests);

        state.Confirmation.Approved = true;
        await command.ExecuteAsync(userRow);
        Assert.Equal((userRow.Entry, false), Assert.Single(state.Changes.Requests));
        Assert.Equal(2, state.Inventory.Loads);
        Assert.Equal("Desativado pelo WinCore", state.Startup.Rows.Single(row => row.Name == "Steam").Status);
        Assert.False(command.CanExecute(userRow));
    }

    [Fact]
    public async Task Local_do_arquivo_so_abre_para_destino_identificado()
    {
        var state = new ScreenFixture();
        await state.Startup.RefreshCommand.ExecuteAsync(null);
        state.Startup.SelectedRow = state.Startup.Rows.Single(row => row.Name == "Monitor de segurança");
        Assert.False(state.Startup.OpenLocationCommand.CanExecute(null));
        state.Startup.SelectedRow = state.Startup.Rows.Single(row => row.Name == "Steam");
        Assert.True(state.Startup.OpenLocationCommand.CanExecute(null));
        state.Startup.OpenLocationCommand.Execute(null);
        Assert.Equal(state.Startup.SelectedRow.FilePath, Assert.Single(state.External.Files));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://github.com.evil.test/user/repo")]
    [InlineData("http://github.com/user/repo")]
    [InlineData("https://github.com@evil.test/user/repo")]
    [InlineData("https://github.com/user/repo#fragment")]
    [InlineData("https://github.com/user/repo/issues")]
    [InlineData("https://github.com:443/user/repo")]
    [InlineData("https://github.com/user/%2E%2E")]
    public void Preferencia_legada_nao_e_apagada_nem_abre_link_automaticamente(string? url)
    {
        var state = new ScreenFixture(url);
        Assert.Equal(url, state.Settings.Settings.OfficialRepositoryUrl);
        Assert.DoesNotContain(state.About.GetType().GetProperties(), property => property.Name == "OpenGitHubCommand");
        Assert.Empty(state.External.Urls);
    }

    [Fact]
    public void Somente_FixMyPC_abre_quando_existe_um_repositorio_legado()
    {
        const string configured = "https://github.com/ExampleOwner/ExampleRepository";
        var state = new ScreenFixture(configured);
        state.About.OpenFixMyPcCommand.Execute(null);
        Assert.Equal("https://github.com/rsehenth/FixMyPC", Assert.Single(state.External.Urls));
    }

    [Fact]
    public void Sobre_e_sidebar_usam_versao_e_nome_dos_metadados()
    {
        var state = new ScreenFixture();
        var assembly = typeof(App).Assembly;
        Assert.Equal(assembly.GetName().Version!.ToString(3), state.About.AppVersion);
        Assert.Equal(assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product, state.About.AppName);
    }

    [Fact]
    public Task JanelaPrincipalBloqueiaFechamentoDuranteDesinstalacaoEOuAtualizacao() => WpfTestThread.Run(() =>
    {
        var model = new ScreenFixture().CreateMain();
        var window = new MainWindow(model);
        try
        {
            Assert.True(window.CanCloseNormally);
            model.Uninstallers.IsUninstalling = true;
            Assert.False(window.CanCloseNormally);
            Assert.False(window.CanCloseForUpdate);
            model.Uninstallers.IsUninstalling = false;
            model.Settings.SelfUpdate.IsBusy = true;
            Assert.False(window.CanCloseNormally);
            Assert.True(window.CanCloseForUpdate);
        }
        finally
        {
            model.Uninstallers.IsUninstalling = false;
            model.Settings.SelfUpdate.IsBusy = false;
            window.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Smoke_WPF_navegacao_layout_controle_protegido_e_link_com_dados_falsos() =>
        WpfTestThread.Run(async () =>
        {
            var state = new ScreenFixture();
            var vm = state.CreateMain();
            Assert.Equal(new[] { "Painel", "Atualizações", "Manutenção", "Desinstalador", "Inicializar", "Limpeza", "Tweaks", "Meu Computador", "Essenciais", "Histórico", "Configurações" },
                vm.NavItems.Select(item => item.Title));
            var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
            window.Show();
            try
            {
                var footer = Assert.IsType<StackPanel>(window.FindName("FooterNav"));
                var about = Assert.IsType<RadioButton>(window.FindName("AboutNav"));
                Assert.Same(about, footer.Children[^2]);
                Assert.Same(window.FindName("VersionLabel"), footer.Children[^1]);
                Assert.Null(window.FindName("GitHubLink"));
                var fix = Assert.IsType<Button>(window.FindName("FixMyPcLink"));
                Assert.Equal("Experimente o FixMyPC", fix.Content);
                Assert.Same(fix, footer.Children[^3]);
                Assert.True(fix.IsEnabled);
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(fix).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal("https://github.com/rsehenth/FixMyPC", Assert.Single(state.External.Urls));

                about.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                window.UpdateLayout();
                Assert.Equal(state.About, vm.CurrentViewModel);
                var aboutPage = Assert.Single(Descendants<AboutPage>(window));
                var version = Assert.IsType<TextBlock>(aboutPage.FindName("AboutVersion"));
                Assert.Contains(vm.AppVersion, version.Text);
                Assert.Contains("Licença: MIT", Assert.IsType<TextBlock>(aboutPage.FindName("AboutLicense")).Text);

                var startupNav = Descendants<RadioButton>(window).Single(button => Equals(button.Content, "Inicializar"));
                startupNav.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                await state.Startup.RefreshCommand.ExecutionTask!;
                Assert.Equal(state.Startup, vm.CurrentViewModel);
                Assert.False(about.IsChecked);
                Assert.Single(vm.NavItems, item => item.IsSelected);
                Assert.Equal(1, state.Inventory.Loads);
                Assert.Empty(state.Changes.Requests);
                Assert.Empty(state.Confirmation.Requests);

                foreach (var size in new[] { new Size(1160, 760), new Size(840, 420), new Size(1500, 900) })
                {
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    var page = Assert.Single(Descendants<StartupPage>(window));
                    var grid = Assert.IsType<DataGrid>(page.FindName("StartupGrid"));
                    Assert.Equal(3, grid.Items.Count);
                    Assert.True(grid.ActualHeight >= 56);
                    grid.ScrollIntoView(grid.Items[0], grid.Columns[^1]);
                    grid.UpdateLayout();
                    Assert.Contains(Descendants<Button>(grid), button => Equals(button.Content, "Desativar") && button.IsEnabled);
                    grid.ScrollIntoView(grid.Items[1], grid.Columns[^1]);
                    grid.UpdateLayout();
                    Assert.Contains(Descendants<Button>(grid), button => Equals(button.Content, "Protegido") && !button.IsEnabled);
                    grid.SelectedIndex = 1;
                    window.UpdateLayout();
                    Assert.True(Assert.IsType<Border>(page.FindName("EntryDetails")).IsVisible);
                    var search = Assert.IsType<TextBox>(page.FindName("StartupSearch"));
                    search.Text = "Valve";
                    window.UpdateLayout();
                    Assert.Single(grid.Items);
                    search.Text = "";
                    var status = Assert.IsType<ComboBox>(page.FindName("StatusFilter"));
                    status.SelectedIndex = (int)StartupFilter.Protected;
                    Assert.Equal(2, grid.Items.Count);
                    status.SelectedIndex = 0;
                }
                Assert.Empty(state.Changes.Requests);
                var cleanup = Descendants<RadioButton>(window).Single(button => Equals(button.Content, "Limpeza"));
                cleanup.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                await vm.Cleanup.RefreshCommand.ExecutionTask!;
                Assert.Same(vm.Cleanup, vm.CurrentViewModel);
                Assert.Single(Descendants<CleanupPage>(window));
                Assert.False(vm.Cleanup.EmptyCommand.CanExecute(null));
                var tweaks = Descendants<RadioButton>(window).Single(button => Equals(button.Content, "Tweaks"));
                tweaks.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                await vm.Tweaks.RefreshCommand.ExecutionTask!;
                Assert.Same(vm.Tweaks, vm.CurrentViewModel);
                Assert.Single(Descendants<TweaksPage>(window));
                Assert.False(vm.Tweaks.UndoCommand.CanExecute(null));
                var hardware = Descendants<RadioButton>(window).Single(button => Equals(button.Content, "Meu Computador"));
                hardware.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                await vm.Hardware.RefreshCommand.ExecutionTask!;
                window.UpdateLayout();
                Assert.Same(vm.Hardware, vm.CurrentViewModel);
                Assert.Single(Descendants<HardwarePage>(window));
                Assert.Single(vm.NavItems, item => item.IsSelected);
            }
            finally { window.Close(); }
        });

    [Theory]
    [InlineData("pt-BR", AppTheme.Dark)]
    [InlineData("pt-BR", AppTheme.Light)]
    [InlineData("en-US", AppTheme.Dark)]
    [InlineData("en-US", AppTheme.Light)]
    public Task Etapa12e_navigates_all_twelve_pages_in_each_language_and_theme(string language, AppTheme theme) =>
        WpfTestThread.Run(async () =>
        {
            var locale = LocalizationService.Current;
            var previous = locale.CultureName;
            locale.Apply(language);
            ThemeResources.Install(Application.Current.Resources, theme);
            var vm = new ScreenFixture().CreateMain();
            var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 840, Height = 420 };
            try
            {
                window.Show();
                var layout = new[] { typeof(DashboardPage), typeof(UpdatesPage), typeof(MaintenancePage), typeof(UninstallersPage),
                    typeof(StartupPage), typeof(CleanupPage), typeof(TweaksPage), typeof(HardwarePage), typeof(EssentialsPage),
                    typeof(HistoryPage), typeof(SettingsPage), typeof(AboutPage) };
                var items = vm.NavItems.Append(vm.AboutNavItem).ToArray();
                Assert.Equal(layout.Length, items.Length);
                for (var index = 0; index < items.Length; index++)
                {
                    items[index].IsSelected = true;
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    Assert.Same(items[index].ViewModel, vm.CurrentViewModel);
                    Assert.Single(Descendants<UserControl>(window), control => control.GetType() == layout[index]);
                }

                var settings = Descendants<RadioButton>(window).Single(button => ReferenceEquals(button.DataContext, items[^2]));
                settings.BringIntoView();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var nav = Assert.IsType<ScrollViewer>(window.FindName("MainNavigationScroll"));
                Assert.True(nav.VerticalOffset > 0);
                Assert.True(settings.IsTabStop);
                Assert.True(Assert.IsType<RadioButton>(window.FindName("AboutNav")).IsVisible);

                items[8].IsSelected = true;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var page = Assert.Single(Descendants<EssentialsPage>(window));
                var cards = Assert.IsType<ItemsControl>(page.FindName("AppCards"));
                Assert.Equal(30, cards.Items.Count);
                var search = Assert.IsType<TextBox>(page.FindName("EssentialsSearch"));
                var filter = Assert.IsType<ComboBox>(page.FindName("CategoryFilter"));
                Assert.Same(filter, search.PredictFocus(FocusNavigationDirection.Right));
                filter.SelectedValue = AppCategory.Drivers;
                search.Text = "nvidia";
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                var result = Assert.IsType<EssentialAppCard>(Assert.Single(cards.Items));
                Assert.Equal(language == "en-US" ? "NVIDIA Drivers" : "Drivers NVIDIA", result.Name);
                var button = Assert.Single(Descendants<Button>(cards));
                Assert.True(button.IsTabStop);
                var invoke = Assert.IsAssignableFrom<IInvokeProvider>(new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke));
                invoke.Invoke();
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.False(vm.Essentials.OpenFailed);
            }
            finally
            {
                window.Close();
                locale.Apply(previous);
                ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
            }
        });

    [Theory]
    [InlineData("pt-BR", AppTheme.Dark)]
    [InlineData("pt-BR", AppTheme.Light)]
    [InlineData("en-US", AppTheme.Dark)]
    [InlineData("en-US", AppTheme.Light)]
    public Task Etapa12g_navigates_and_closes_safely_while_WinGet_streams_output(string language, AppTheme theme) => WpfTestThread.Run(async () =>
    {
        var locale = LocalizationService.Current;
        var previous = locale.CultureName;
        locale.Apply(language);
        ThemeResources.Install(Application.Current.Resources, theme);
        var service = new WinGetOperationFixture(2);
        WinGetProcessControl? control = null;
        ProcessRunResult? observation = null;
        service.Version = _ => "1.0";
        service.Run = async (_, operation, token) =>
        {
            control = operation;
            observation = await ProcessRunner.RunWinGetAsync(WinGetCancellationTests.Fixture("flood-live"), service.Output,
                token, operation, false);
            return observation;
        };
        var vm = new ScreenFixture().CreateMain(service);
        var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0, Width = 840, Height = 520 };
        Task? run = null;
        try
        {
            window.Show();
            vm.NavItems[1].IsSelected = true;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await vm.Updates.RefreshCommand.ExecuteAsync(null);
            vm.Updates.AllSelected = true;
            run = vm.Updates.UpdateSelectedCommand.ExecuteAsync(null);
            await WinGetCancellationTests.WaitUntilAsync(() => vm.Updates.OperationLastMessage.Length > 0);
            var page = Assert.Single(Descendants<UpdatesPage>(window));
            var cancel = Assert.IsType<Button>(page.FindName("CancelCurrentUpdateButton"));
            Assert.Equal(language == "pt-BR" ? "Cancelar atualização" : "Cancel update", cancel.Content);
            Assert.True(cancel.IsEnabled && cancel.IsTabStop);
            var operationPanel = Assert.IsType<Border>(page.FindName("UpdateOperationPanel"));
            Assert.True(operationPanel.IsVisible && operationPanel.ActualHeight > 0);
            Assert.Equal(language == "pt-BR" ? "Baixando" : "Downloading",
                Assert.IsType<TextBlock>(page.FindName("UpdateOperationStatus")).Text);
            Assert.Contains(language == "pt-BR" ? "1 de 2" : "1 of 2", locale.Format("UpdateOperation.QueuePosition", "Fixture 1", 1, 2));

            foreach (var (item, pageType) in new (NavItem, Type)[]
            {
                (vm.NavItems[0], typeof(DashboardPage)), (vm.NavItems[8], typeof(EssentialsPage)),
                (vm.NavItems[3], typeof(UninstallersPage)), (vm.NavItems[7], typeof(HardwarePage)),
                (vm.NavItems[9], typeof(HistoryPage)), (vm.NavItems[10], typeof(SettingsPage)),
                (vm.AboutNavItem, typeof(AboutPage)),
            })
            {
                var tab = ReferenceEquals(item, vm.AboutNavItem) ? Assert.IsType<RadioButton>(window.FindName("AboutNav")) :
                    Assert.Single(Descendants<RadioButton>(window), button => ReferenceEquals(button.DataContext, item));
                Assert.True(tab.IsEnabled);
                tab.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Same(item.ViewModel, vm.CurrentViewModel);
                var rendered = Assert.Single(Descendants<UserControl>(window), candidate => candidate.GetType() == pageType);
                Assert.False(run.IsCompleted);
                if (rendered is UninstallersPage)
                {
                    Assert.True(vm.Uninstallers.RefreshCommand.CanExecute(null));
                    await vm.Uninstallers.RefreshCommand.ExecuteAsync(null);
                    Assert.False(vm.Uninstallers.UninstallSelectedCommand.CanExecute(null));
                }
                if (rendered is HardwarePage hardware)
                {
                    var button = Assert.IsType<Button>(hardware.FindName("SensorsButton"));
                    button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var sensors = Assert.Single(window.OwnedWindows.OfType<SensorWindow>());
                    Assert.True(sensors.IsVisible);
                    sensors.Close();
                }
            }

            Assert.True(vm.Maintenance.RunToolCommand.CanExecute(vm.Maintenance.Tools.First(tool => tool.Key == "dism_checkhealth")));
            Assert.False(vm.Maintenance.RunFullRepairCommand.CanExecute(null));
            window.ChooseUpdateCloseAction = choices => { Assert.Contains("UpdateOperation.CancelQueue", choices); return null; };
            window.Close();
            Assert.True(window.IsVisible && !window.CanCloseNormally);
            window.ChooseUpdateCloseAction = _ => "UpdateOperation.CancelQueue";
            window.Close();
            await WinGetCancellationTests.WaitUntilAsync(() => vm.Updates.ShowForce);
            Assert.True(window.IsVisible && vm.Updates.IsCancelRequested);
            Assert.True(vm.Updates.CanCloseWithoutWaiting);

            _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                var dialog = Assert.Single(window.OwnedWindows.OfType<LocalizedDialog>());
                Assert.IsType<Button>(dialog.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Assert.Equal("A atualização pode estar instalando arquivos neste momento. Forçar o encerramento pode deixar o aplicativo parcialmente atualizado. Deseja continuar?",
                LocalizationService.Source("UpdateOperation.ForceWarning"));
            Assert.True(vm.Updates.ConfirmForce(window));
            await run;
            Assert.True(window.CanCloseNormally);
            var result = Assert.IsType<ProcessRunResult>(observation);
            Assert.True(result.HasExitCode, result.StartError);
            Assert.Equal(1223, result.ExitCode);
            Assert.Equal(ControlledProcessState.Exited, result.Observation.ControlledProcesses);
            if (result.Observation.Delegation == DelegatedProcessState.KnownActive)
            {
                // An observed process outside the Job can keep an inherited pipe open.
                Assert.True(vm.Updates.HasInterruptedUpdate && vm.Updates.OperationInterrupted);
                Assert.True(vm.Updates.PackageChangesBlockedNow);
            }
            else
            {
                Assert.True(result.Cancelled && !result.Interrupted,
                    $"{vm.Updates.OperationResult}\n{string.Join(Environment.NewLine, result.Errors.TakeLast(5))}");
                Assert.Equal(UpdateRowStatus.Cancelled, vm.Updates.Rows[0].Status);
                Assert.False(vm.Updates.HasInterruptedUpdate);
            }
            Assert.Equal(UpdateRowStatus.Idle, vm.Updates.Rows[1].Status);
        }
        finally
        {
            if (run is { IsCompleted: false })
            {
                vm.Updates.CancelUpdateCommand.Execute(null);
                await WinGetCancellationTests.WaitUntilAsync(() => vm.Updates.ShowForce || run.IsCompleted);
                if (!run.IsCompleted) control?.RequestForce();
                await run;
            }
            window.Close();
            locale.Apply(previous);
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        }
    });

    [Fact]
    public Task Etapa12g_window_detaches_only_after_confirmation_and_reopens_with_receipt() => WpfTestThread.Run(async () =>
    {
        var service = new WinGetOperationFixture(1);
        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Run = (_, control, token) => ProcessRunner.RunWinGetAsync(WinGetCancellationTests.Fixture("never"), line =>
        {
            if (line.StartsWith("ready ")) ready.TrySetResult(int.Parse(line[6..]));
            service.Output!(line);
        }, token, control);
        var fixture = new ScreenFixture();
        var vm = fixture.CreateMain(service);
        var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await vm.Updates.RefreshCommand.ExecuteAsync(null);
        vm.Updates.AllSelected = true;
        var batch = vm.Updates.UpdateSelectedCommand.ExecuteAsync(null);
        using var child = System.Diagnostics.Process.GetProcessById(await ready.Task.WaitAsync(TimeSpan.FromSeconds(8)));
        var childPid = child.Id;
        var childExited = false;
        try
        {
            vm.Updates.CancelUpdateCommand.Execute(null);
            await WinGetCancellationTests.WaitUntilAsync(() => vm.Updates.CanCloseWithoutWaiting);
            window.ChooseUpdateCloseAction = choices =>
            {
                Assert.Contains("UpdateOperation.Force", choices);
                Assert.Contains("UpdateOperation.CloseWithoutWaiting", choices);
                return "UpdateOperation.CloseWithoutWaiting";
            };
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(() =>
            {
                var dialog = Assert.Single(window.OwnedWindows.OfType<LocalizedDialog>());
                Assert.IsType<Button>(dialog.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await batch;
            Assert.False(child.HasExited);
            Assert.True(vm.Updates.HasInterruptedUpdate);
            Assert.Equal(UpdateRowStatus.Interrupted, vm.Updates.Rows[0].Status);
            var reopened = fixture.CreateMain(service);
            Assert.True(reopened.Updates.HasInterruptedUpdate && reopened.Uninstallers.ApplicationBusy);
            Assert.True(reopened.Uninstallers.RefreshCommand.CanExecute(null));
            await reopened.Updates.RecheckInterruptionAsync();
            Assert.True(reopened.Updates.HasInterruptedUpdate);
            Assert.Contains("processo original", reopened.Updates.InterruptionNote);
            child.Kill();
            await child.WaitForExitAsync();
            childExited = true;
            child.Dispose();
            var marker = new WinGetInterruptionStore(fixture.Settings.DataDirectory);
            using (var receipt = marker.Open())
            {
                var identity = Assert.IsType<WinGetProcessIdentity>(receipt.Identity);
                Assert.Equal((uint)childPid, identity.Pid);
                await WinGetCancellationTests.WaitUntilAsync(() => !WinGetNative.OriginalProcessMayStillBeActive(identity, out _));
            }
            await reopened.Updates.RecheckInterruptionAsync();
            Assert.True(reopened.Updates.HasInterruptedUpdate, reopened.Updates.InterruptionNote);
            fixture.Boot = DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await reopened.Updates.RecheckInterruptionAsync();
            Assert.True(reopened.Updates.HasInterruptedUpdate && reopened.Updates.ShowInterruptionReview);
            await reopened.Updates.RecheckInterruptionAsync("2.0");
            Assert.False(reopened.Updates.HasInterruptedUpdate, reopened.Updates.InterruptionNote);
        }
        finally
        {
            if (!childExited && !child.HasExited) child.Kill();
            await batch;
            if (!closed.Task.IsCompleted) window.Close();
        }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T result) yield return result;
            foreach (var resultChild in Descendants<T>(child)) yield return resultChild;
        }
    }

    private sealed class ScreenFixture
    {
        public FakeNavigation External { get; } = new();
        public FakeConfirmation Confirmation { get; } = new();
        public ScreenInventory Inventory { get; } = new();
        public FakeChanges Changes { get; }
        public StartupViewModel Startup { get; }
        public AboutViewModel About { get; }
        public TestSettings Settings { get; } = new();
        public string Boot { get; set; } = WinGetOperationTests.BootBefore;

        public ScreenFixture(string? url = null)
        {
            Settings.Settings.OfficialRepositoryUrl = url;
            Changes = new(Inventory);
            Startup = new(Inventory, Changes, Confirmation, External);
            About = new(External);
        }

        public MainViewModel CreateMain(IWinGetService? service = null)
        {
            var history = new FakeHistoryService();
            var winGet = service ?? new OfflineWinGet();
            var updates = service is null ? new UpdatesViewModel(winGet, history) :
                new UpdatesViewModel(winGet, history, Settings, WinGetOperationTests.ReviewStore(Settings, () => Boot));
            var maintenance = new MaintenanceViewModel(new MaintenanceService(history));
            var historyPage = new HistoryViewModel(history);
            var essentialNavigation = new EssentialNavigation(_ => true);
            var activation = new WindowsActivationViewModel(new TestWindowsActivationService(), essentialNavigation);
            var dashboard = new DashboardViewModel(winGet, history, updates, maintenance, historyPage, activation);
            var planner = new UninstallPlanner();
            var uninstallers = new UninstallersViewModel(new FakeProgramInventory(), planner,
                new UninstallBatchRunner(planner, new FakeUninstallExecutor(), history), new FakeResidueAnalysisService());
            var features = new WindowsFeaturesFixture(history);
            var hardware = new HardwareViewModel(new HardwareTestInventory(), new HardwareTestScheduler());
            return new(dashboard, updates, maintenance, uninstallers, Startup, features.CleanupPage, features.TweaksPage, hardware, new(essentialNavigation), historyPage,
                new SettingsViewModel(Settings, history, winGet), About);
        }
    }

    private sealed class OfflineWinGet : IWinGetService
    {
        public string? ExecutablePath => null;
        public WinGetAvailability? LastDetection => null;
        public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetAvailability());
        public Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetScanResult());
        public Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ScreenInventory : IStartupInventory
    {
        public int Loads { get; private set; }
        public StartupEntry[] Entries { get; } =
        [
            new("steam", "Steam", "Valve", StartupSource.UserFolder, StartupStatus.Enabled,
                @"C:\Startup\Steam.lnk", @"C:\Apps\Steam.exe", "-silent", null, Fingerprint: "fixture"),
            new("teams", "Teams", "Microsoft", StartupSource.UserRun, StartupStatus.Disabled,
                @"HKCU\Run", @"C:\Apps\Teams.exe", null, null, Protection: "Somente leitura."),
            new("system", "Monitor de segurança", null, StartupSource.CommonFolder, StartupStatus.Enabled,
                @"C:\Shared\Monitor.lnk", null, null, "Indireto.", Protection: "Protegido."),
        ];

        public Task<StartupInventoryResult> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Loads++;
            return Task.FromResult(new StartupInventoryResult(Entries.ToArray(), []));
        }
    }

    private sealed class FakeChanges(ScreenInventory inventory) : IStartupChanges
    {
        public List<(StartupEntry Entry, bool Enabled)> Requests { get; } = [];
        public Task<StartupChangeResult> SetEnabledAsync(StartupEntry expected, bool enabled, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add((expected, enabled));
            var index = Array.IndexOf(inventory.Entries, expected);
            inventory.Entries[index] = expected with { Status = enabled ? StartupStatus.Enabled : StartupStatus.Disabled, Stored = !enabled };
            return Task.FromResult(new StartupChangeResult(StartupChangeStatus.Applied, "Aplicado."));
        }
    }

    private sealed class FakeConfirmation : IStartupConfirmation
    {
        public List<(string Name, bool Enabled)> Requests { get; } = [];
        public bool Approved { get; set; }
        public bool Confirm(string name, bool enable)
        {
            Requests.Add((name, enable));
            return Approved;
        }
    }

    private sealed class FakeNavigation : IExternalNavigation
    {
        public List<string> Urls { get; } = [];
        public List<string> Files { get; } = [];
        public bool OpenWeb(Uri uri) { Urls.Add(uri.AbsoluteUri); return true; }
        public bool OpenFileLocation(string path) { Files.Add(path); return true; }
    }
}
