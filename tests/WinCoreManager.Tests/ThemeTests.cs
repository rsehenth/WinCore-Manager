using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

/// <summary>Etapa 9: skins Dark/Light, persistência, tokens, contraste e troca em tempo de execução.</summary>
public class ThemeTests
{
    [Fact]
    public Task PincelCSharpJaVinculadoAcompanhaTemasEAltoContraste() => WpfTestThread.Run(() =>
    {
        try
        {
            var resources = Application.Current.Resources;
            ThemeResources.Install(resources, AppTheme.Dark, highContrast: false);
            var row = new WinCoreManager.ViewModels.UpdateItemRow(new WinCoreManager.Models.WinGetPackage { Name = "Fixture", Id = "Test.Fixture" })
            {
                Status = WinCoreManager.ViewModels.UpdateRowStatus.Success,
            };
            var label = new TextBlock { DataContext = row };
            label.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding("StatusBrush"));
            var brush = Assert.IsType<SolidColorBrush>(label.Foreground);
            Assert.Equal(BrushColor("SuccessBrush"), brush.Color);
            var dark = brush.Color;
            ThemeResources.Install(resources, AppTheme.Light, highContrast: false);
            Assert.Same(brush, label.Foreground);
            Assert.NotEqual(dark, brush.Color);
            Assert.Equal(BrushColor("SuccessBrush"), brush.Color);
            ThemeResources.Install(resources, AppTheme.Light, highContrast: true);
            Assert.Same(brush, label.Foreground);
            Assert.Equal(SystemColors.WindowTextColor, brush.Color);
        }
        finally { Restore(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(null)]
    [InlineData("uninstall.exe")]
    public Task Dialogo_de_desinstalacao_carrega_com_e_sem_comando(string? command) => WpfTestThread.Run(() =>
    {
        ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
        var request = new UninstallConfirmationRequest
        {
            Title = "Desinstalar",
            Description = "Revisar desinstalação",
            CommandText = command,
            Effects = "Remove o aplicativo",
            Warnings = [],
            ItemCount = 1,
        };

        var dialog = new UninstallConfirmDialog(request);
        try
        {
            Assert.Same(request, dialog.DataContext);
        }
        finally
        {
            dialog.Close();
        }

        return Task.CompletedTask;
    });

    // ---------- escolha e persistência ----------

    [Theory]
    [InlineData(null, AppTheme.Light)]
    [InlineData("", AppTheme.Light)]
    [InlineData("Banana", AppTheme.Light)]
    [InlineData("7", AppTheme.Light)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("Light", AppTheme.Light)]
    [InlineData("light", AppTheme.Light)]
    [InlineData(" LIGHT ", AppTheme.Light)]
    public void Valor_salvo_vira_skin_e_qualquer_valor_invalido_cai_em_Light(string? saved, AppTheme expected) =>
        Assert.Equal(expected, ThemeService.Parse(saved));

    [Fact]
    public Task Primeira_execucao_usa_Light_e_nao_grava_nada() => WpfTestThread.Run(() =>
    {
        var settings = new RecordingSettings();
        var service = new ThemeService(settings, Application.Current);
        try
        {
            service.Initialize();
            Assert.Equal(AppTheme.Light, service.Current);
            Assert.Equal(Color.FromRgb(0xF6, 0xF7, 0xF8), BrushColor("WindowBgBrush"));
            Assert.Equal(0, settings.Saves);
            Assert.Null(settings.Settings.Theme);
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Selecionar_Light_e_voltar_a_Dark_aplica_e_grava() => WpfTestThread.Run(() =>
    {
        var settings = new RecordingSettings();
        var service = new ThemeService(settings, Application.Current);
        var raised = new List<AppTheme>();
        service.ThemeChanged += raised.Add;
        try
        {
            Assert.True(service.Select(AppTheme.Light));
            Assert.Equal(AppTheme.Light, service.Current);
            Assert.Equal("Light", settings.Settings.Theme);
            Assert.Equal(Color.FromRgb(0xF6, 0xF7, 0xF8), BrushColor("WindowBgBrush"));
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), BrushColor("CardBgBrush"));

            Assert.True(service.Select(AppTheme.Dark));
            Assert.Equal("Dark", settings.Settings.Theme);
            Assert.Equal(Color.FromRgb(0x1B, 0x1C, 0x20), BrushColor("WindowBgBrush"));
            Assert.Equal([AppTheme.Light, AppTheme.Dark], raised);
            Assert.Equal(2, settings.Saves);
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Falha_ao_gravar_mantem_a_skin_aplicada_e_restaura_a_preferencia_anterior() => WpfTestThread.Run(() =>
    {
        var settings = new RecordingSettings { FailSave = true };
        settings.Settings.Theme = "Dark";
        var service = new ThemeService(settings, Application.Current);
        try
        {
            Assert.False(service.Select(AppTheme.Light));
            Assert.Equal(AppTheme.Light, service.Current);
            Assert.Equal("Dark", settings.Settings.Theme);
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Escolha_sobrevive_a_reinicio_sem_perder_outras_preferencias() => WpfTestThread.Run(() =>
    {
        var root = new TestSettings().DataDirectory;
        var exe = Path.Combine(root, "exe");
        var local = Path.Combine(root, "local");
        var first = new SettingsService(exe, local);
        first.Settings.HistoryRetentionDays = 90;
        first.Settings.OfficialRepositoryUrl = "https://github.com/Owner/Repo";
        first.Save();
        try
        {
            Assert.True(new ThemeService(first, Application.Current).Select(AppTheme.Light));

            var reopened = new SettingsService(exe, local);
            Assert.Equal("Light", reopened.Settings.Theme);
            Assert.Equal(90, reopened.Settings.HistoryRetentionDays);
            Assert.Equal("https://github.com/Owner/Repo", reopened.Settings.OfficialRepositoryUrl);
            Assert.False(reopened.Settings.PortableMode);

            var second = new ThemeService(reopened, Application.Current);
            second.Initialize();
            Assert.Equal(AppTheme.Light, second.Current);
            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), BrushColor("CardBgBrush"));
            Assert.Empty(Directory.GetFiles(local, "*.tmp"));
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public void Configuracao_legada_sem_tema_abre_em_Light_e_preserva_o_restante()
    {
        var root = new TestSettings().DataDirectory;
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "settings.json"), """{"PortableMode":false,"HistoryRetentionDays":45}""");

        var settings = new SettingsService(Path.Combine(root, "exe"), local);

        Assert.Null(settings.Settings.Theme);
        Assert.Equal(AppTheme.Light, ThemeService.Parse(settings.Settings.Theme));
        Assert.Equal(45, settings.Settings.HistoryRetentionDays);
    }

    [Fact]
    public void Tema_invalido_no_arquivo_cai_em_Light_sem_descartar_as_outras_preferencias()
    {
        var root = new TestSettings().DataDirectory;
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, "settings.json"), """{"Theme":"Banana","HistoryRetentionDays":30}""");

        var settings = new SettingsService(Path.Combine(root, "exe"), local);

        Assert.Equal(AppTheme.Light, ThemeService.Parse(settings.Settings.Theme));
        Assert.Equal(30, settings.Settings.HistoryRetentionDays);
    }

    [Fact]
    public void Arquivo_corrompido_ou_com_tipo_errado_nao_impede_a_abertura()
    {
        var root = new TestSettings().DataDirectory;
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var path = Path.Combine(local, "settings.json");

        foreach (var content in new[] { "{ nao e json", """{"Theme":5}""" })
        {
            File.WriteAllText(path, content);
            var settings = new SettingsService(Path.Combine(root, "exe"), local);
            Assert.Equal(AppTheme.Light, ThemeService.Parse(settings.Settings.Theme));
        }
    }

    [Fact]
    public void Trocar_o_tema_depende_apenas_de_configuracoes_e_da_aplicacao()
    {
        // Nenhum serviço nativo (energia, registro, limpeza, desinstalação, sensores) é alcançável pela troca de skin.
        var parameters = typeof(ThemeService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType);
        Assert.Equal([typeof(ISettingsService), typeof(Application)], parameters);
    }

    // ---------- tokens ----------

    [Fact]
    public Task Dark_e_Light_definem_exatamente_as_mesmas_chaves_e_tipos() => WpfTestThread.Run(() =>
    {
        var dark = Tokens(AppTheme.Dark);
        var light = Tokens(AppTheme.Light);

        var darkKeys = dark.Keys.OfType<string>().ToList();
        var lightKeys = light.Keys.OfType<string>().ToList();
        Assert.Empty(darkKeys.Except(lightKeys));
        Assert.Empty(lightKeys.Except(darkKeys));
        foreach (var key in darkKeys)
        {
            Assert.Equal(dark[key]!.GetType(), light[key]!.GetType());
        }

        foreach (var required in new[]
        {
            "WindowBgBrush", "SidebarBgBrush", "CardBgBrush", "PopupBgBrush", "BorderBrush", "TextPrimaryBrush", "TextSecondaryBrush",
            "TextMutedBrush", "TextDisabledBrush", "AccentBrush", "AccentHoverBrush", "AccentSubtleBrush", "SuccessBrush", "WarningBrush",
            "DangerBrush", "ScrollThumbBrush", "HoverLayerBrush", "BorderControlBrush",
        })
        {
            Assert.True(dark.Contains(required), required);
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Contraste_de_texto_e_componentes_atende_WCAG_nas_duas_skins() => WpfTestThread.Run(() =>
    {
        var failures = new List<string>();
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            var t = Tokens(theme);
            Color C(string key) => ((SolidColorBrush)t[key]!).Color;
            void Check(string fg, string bg, double min)
            {
                var ratio = Contrast(C(fg), C(bg));
                if (ratio < min) failures.Add($"{theme}: {fg} sobre {bg} = {ratio:F2} (mín. {min})");
            }

            foreach (var surface in new[] { "WindowBgBrush", "SidebarBgBrush", "CardBgBrush", "SubtleBgBrush", "InsetBgBrush" })
            {
                Check("TextPrimaryBrush", surface, 7);
                Check("TextSecondaryBrush", surface, 4.5);
            }

            foreach (var surface in new[] { "WindowBgBrush", "SidebarBgBrush", "CardBgBrush", "InsetBgBrush" })
            {
                Check("TextMutedBrush", surface, 4.5);
                Check("AccentTextBrush", surface, 4.5);
                Check("SuccessBrush", surface, 4.5);
                Check("WarningBrush", surface, 4.5);
                Check("DangerTextBrush", surface, 4.5);
            }

            foreach (var surface in new[] { "ButtonBgBrush", "PopupBgBrush", "RowHoverBrush", "RowSelectedBrush", "AccentSubtleBrush", "CardHoverBrush" })
            {
                Check("TextPrimaryBrush", surface, 4.5);
            }

            Check("OnAccentBrush", "AccentBrush", 4.5);
            Check("OnAccentBrush", "DangerBrush", 4.5);
            Check("SuccessBrush", "SuccessSubtleBrush", 4.5);
            Check("WarningBrush", "WarningSubtleBrush", 4.5);
            Check("DangerTextBrush", "DangerSubtleBrush", 4.5);
            Check("AccentTextBrush", "InfoSubtleBrush", 4.5);
            Check("TextPrimaryBrush", "InfoSubtleBrush", 4.5);
            Check("TextPrimaryBrush", "WarningSubtleBrush", 4.5);
            Check("BorderControlBrush", "InsetBgBrush", 3.1);
            Check("ScrollThumbBrush", "CardBgBrush", 3.1);
            Check("ScrollThumbBrush", "InsetBgBrush", 3.1);

            // Elementos gráficos e estados (3:1): contorno de campos e caixas, acento e foco.
            foreach (var surface in new[] { "WindowBgBrush", "CardBgBrush" })
            {
                Check("BorderControlBrush", surface, 3);
                Check("AccentBrush", surface, 3);
                Check("AccentTextBrush", surface, 3);
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Texto_desabilitado_continua_perceptivel_mas_abaixo_do_texto_comum() => WpfTestThread.Run(() =>
    {
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            var t = Tokens(theme);
            Color C(string key) => ((SolidColorBrush)t[key]!).Color;
            var disabled = Contrast(C("TextDisabledBrush"), C("CardBgBrush"));
            Assert.InRange(disabled, 1.6, 4.4);
        }

        return Task.CompletedTask;
    });

    // ---------- troca em tempo de execução ----------

    [Fact]
    public Task Troca_com_pagina_e_janela_secundaria_abertas_atualiza_tudo_sem_recriar() => WpfTestThread.Run(() =>
    {
        var settings = new RecordingSettings();
        var service = new ThemeService(settings, Application.Current);
        var card = new Border { Width = 200, Height = 80 };
        card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        var main = Hidden(card);
        var secondary = Hidden(new TextBox { Text = "campo" });
        secondary.SetResourceReference(Control.BackgroundProperty, "WindowBgBrush");
        var secondaryField = (TextBox)secondary.Content;
        main.Show();
        secondary.Show();
        try
        {
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
            Assert.Equal(Color.FromRgb(0x23, 0x24, 0x28), ((SolidColorBrush)card.Background).Color);

            service.Apply(AppTheme.Light);

            Assert.Equal(Color.FromRgb(0xFF, 0xFF, 0xFF), ((SolidColorBrush)card.Background).Color);
            Assert.Equal(Color.FromRgb(0xF6, 0xF7, 0xF8), ((SolidColorBrush)secondary.Background).Color);
            Assert.Same(card, main.Content);
            Assert.Equal("campo", secondaryField.Text);
            Assert.Contains(secondary, Application.Current.Windows.Cast<Window>());
        }
        finally
        {
            main.Close();
            secondary.Close();
            Restore();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Janela_Sensores_segue_a_skin_ao_abrir_e_durante_a_troca() => WpfTestThread.Run(() =>
    {
        var hardware = new HardwareViewModel(new HardwareTestInventory(), new HardwareTestScheduler());
        var window = new SensorWindow(hardware.CreateSensorPanel()) { ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };
        try
        {
            ThemeResources.Install(Application.Current.Resources, AppTheme.Light);
            window.Show();
            window.UpdateLayout();
            Assert.Equal(Color.FromRgb(0xF6, 0xF7, 0xF8), ((SolidColorBrush)window.Background).Color);
            Assert.Equal(Color.FromRgb(0x1B, 0x1D, 0x21), ((SolidColorBrush)window.Foreground).Color);
            var panel = (SensorPanel)window.Content;
            var pause = (Button)panel.FindName("SensorPauseButton")!;
            Assert.NotNull(pause);

            new ThemeService(new RecordingSettings(), Application.Current).Apply(AppTheme.Dark);
            Assert.Equal(Color.FromRgb(0x1B, 0x1C, 0x20), ((SolidColorBrush)window.Background).Color);
            Assert.Same(pause, panel.FindName("SensorPauseButton"));
        }
        finally
        {
            window.Close();
            Restore();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task DataGrid_mantem_ordenacao_e_selecao_e_acompanha_as_duas_skins() => WpfTestThread.Run(() =>
    {
        var rows = new List<GridRow> { new("B", new DateTime(2026, 1, 5)), new("A", new DateTime(2026, 3, 1)), new("C", new DateTime(2025, 12, 31)) };
        var grid = new DataGrid { ItemsSource = rows, Width = 400, Height = 220 };
        grid.Columns.Add(new DataGridTextColumn { Header = "Nome", Binding = new Binding("Name") });
        grid.Columns.Add(new DataGridTextColumn { Header = "Data", Binding = new Binding("Date") { StringFormat = "dd/MM/yyyy" }, SortMemberPath = "Date" });
        var window = Hidden(grid);
        window.Show();
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeResources.Install(Application.Current.Resources, theme);
                grid.UpdateLayout();

                grid.Items.SortDescriptions.Clear();
                grid.Items.SortDescriptions.Add(new System.ComponentModel.SortDescription("Date", System.ComponentModel.ListSortDirection.Descending));
                grid.UpdateLayout();
                Assert.Equal(["A", "B", "C"], grid.Items.Cast<GridRow>().Select(r => r.Name));

                grid.SelectedIndex = 1;
                grid.UpdateLayout();
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(1)!;
                Assert.True(row.IsSelected);
                Assert.Equal(BrushColor("RowSelectedBrush"), ((SolidColorBrush)row.Background).Color);

                var header = Descendants<System.Windows.Controls.Primitives.DataGridColumnHeader>(grid).First();
                Assert.Equal(BrushColor("TextSecondaryBrush"), ((SolidColorBrush)header.Foreground).Color);
                grid.SelectedIndex = -1;
            }

            // Troca com a linha selecionada: o estado funcional permanece e a cor acompanha a skin nova.
            ThemeResources.Install(Application.Current.Resources, AppTheme.Dark);
            grid.SelectedIndex = 0;
            grid.UpdateLayout();
            ThemeResources.Install(Application.Current.Resources, AppTheme.Light);
            var selected = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(0)!;
            Assert.True(selected.IsSelected);
            Assert.Equal(Color.FromRgb(0xE6, 0xEC, 0xF6), ((SolidColorBrush)selected.Background).Color);
        }
        finally
        {
            window.Close();
            Restore();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Tooltip_e_menu_de_contexto_usam_a_superficie_elevada_da_skin() => WpfTestThread.Run(() =>
    {
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeResources.Install(Application.Current.Resources, theme);
                var tip = new ToolTip { Content = "Dica" };
                tip.ApplyTemplate();
                var menu = new ContextMenu { Items = { new MenuItem { Header = "Copiar" } } };
                menu.ApplyTemplate();
                foreach (Control popup in new Control[] { tip, menu })
                {
                    var border = (Border)VisualTreeHelper.GetChild(popup, 0);
                    Assert.Equal(BrushColor("PopupBgBrush"), ((SolidColorBrush)border.Background).Color);
                    Assert.Equal(BrushColor("BorderStrongBrush"), ((SolidColorBrush)border.BorderBrush).Color);
                }
            }
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Controles_desabilitados_ficam_atenuados_nas_duas_skins() => WpfTestThread.Run(() =>
    {
        var enabled = new Button { Content = "Ativo" };
        var disabled = new Button { Content = "Inativo", IsEnabled = false };
        var checkbox = new CheckBox { Content = "Opção", IsEnabled = false };
        var panel = new StackPanel { Children = { enabled, disabled, checkbox } };
        var window = Hidden(panel);
        window.Show();
        try
        {
            foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
            {
                ThemeResources.Install(Application.Current.Resources, theme);
                window.UpdateLayout();
                Assert.Equal(1.0, RootOpacity(enabled));
                Assert.Equal(0.4, RootOpacity(disabled), 2);
                Assert.Equal(0.4, RootOpacity(checkbox), 2);
            }
        }
        finally
        {
            window.Close();
            Restore();
        }

        return Task.CompletedTask;
    });

    // ---------- Configurações e Sobre ----------

    [Fact]
    public Task Configuracoes_mostram_Aparencia_e_trocam_a_skin_pelo_seletor() => WpfTestThread.Run(async () =>
    {
        var settings = new RecordingSettings();
        var theme = new ThemeService(settings, Application.Current);
        var viewModel = new SettingsViewModel(settings, new HistoryService(new TestSettings()), new OfflineWinGet(), theme);
        var page = new SettingsPage { DataContext = viewModel };
        var window = Hidden(page);
        window.Show();
        try
        {
            Assert.Equal(["Dark — Escuro", "Light — Claro"], viewModel.ThemeOptions.Select(o => o.Label));
            Assert.Equal(AppTheme.Light, viewModel.SelectedThemeOption.Theme);
            window.UpdateLayout();

            var texts = Descendants<TextBlock>(page).Select(t => t.Text).ToList();
            Assert.Contains("Aparência", texts);
            Assert.Contains("Personalize a aparência do WinCore Manager.", texts);
            Assert.Contains("Light — Claro", texts);
            Assert.DoesNotContain(texts, t => t.Contains("ThemeOption {", StringComparison.Ordinal));

            var selector = (ComboBox)page.FindName("ThemeSelector")!;
            selector.SelectedItem = viewModel.ThemeOptions[0];
            await Task.Yield();
            window.UpdateLayout();

            Assert.Equal(AppTheme.Dark, theme.Current);
            Assert.Equal("Dark", settings.Settings.Theme);
            Assert.Equal("Aparência alterada para Dark — Escuro.", viewModel.StatusNote);
            Assert.Contains("Dark — Escuro", Descendants<TextBlock>(page).Select(t => t.Text));
            Assert.Equal(Color.FromRgb(0x23, 0x24, 0x28), BrushColor("CardBgBrush"));
        }
        finally
        {
            window.Close();
            Restore();
        }
    });

    [Fact]
    public Task Seletor_acompanha_mudancas_de_skin_feitas_fora_da_pagina() => WpfTestThread.Run(() =>
    {
        var settings = new RecordingSettings();
        var theme = new ThemeService(settings, Application.Current);
        var viewModel = new SettingsViewModel(settings, new HistoryService(new TestSettings()), new OfflineWinGet(), theme);
        try
        {
            theme.Apply(AppTheme.Light);
            Assert.Equal(AppTheme.Light, viewModel.SelectedThemeOption.Theme);
            Assert.Equal(0, settings.Saves);
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Falha_de_gravacao_aparece_para_o_usuario_e_nao_desfaz_a_skin() => WpfTestThread.Run(() =>
    {
        var settings = new RecordingSettings { FailSave = true };
        var theme = new ThemeService(settings, Application.Current);
        var viewModel = new SettingsViewModel(settings, new HistoryService(new TestSettings()), new OfflineWinGet(), theme);
        try
        {
            viewModel.SelectedThemeOption = viewModel.ThemeOptions[0];
            Assert.Equal(AppTheme.Dark, theme.Current);
            Assert.Contains("não foi possível salvar", viewModel.StatusNote);
        }
        finally { Restore(); }

        return Task.CompletedTask;
    });

    [Fact]
    public Task Sobre_explica_o_app_e_credita_o_autor_com_link_para_o_perfil() => WpfTestThread.Run(() =>
    {
        var navigation = new RecordingNavigation();
        var about = new AboutViewModel(navigation);
        var page = new AboutPage { DataContext = about };
        var window = Hidden(page);
        window.Show();
        try
        {
            window.UpdateLayout();
            var texts = Descendants<TextBlock>(page).Select(t => t.Text).ToList();
            Assert.Contains("Criado por", texts);
            Assert.Equal("Lucas Sehnem", ((TextBlock)page.FindName("AboutAuthor")!).Text);
            Assert.Contains("github.com/rsehenth", texts);
            foreach (var module in new[] { "Atualizações", "Manutenção", "Desinstalador", "Inicializar", "Limpeza", "Tweaks", "Meu Computador", "Histórico" })
            {
                Assert.Contains(module, texts);
            }

            Assert.DoesNotContain(texts, t => t.Contains("Dar uma estrela", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("Licença: MIT. Copyright (c) 2026 Lucas Sehnem.", ((TextBlock)page.FindName("AboutLicense")!).Text);
            Assert.Contains(Descendants<Button>(page), b => Equals(b.Content, "Experimente o FixMyPC"));

            ((Button)page.FindName("AuthorLink")!).Command.Execute(null);
            Assert.Equal("https://github.com/rsehenth", Assert.Single(navigation.Urls));
        }
        finally { window.Close(); }

        return Task.CompletedTask;
    });

    [Fact]
    public void Perfil_do_autor_e_a_unica_url_alem_de_repositorios_aceita_para_abrir()
    {
        Assert.Equal("https://github.com/rsehenth", WindowsExternalNavigation.AuthorProfile.AbsoluteUri);
        Assert.False(new WindowsExternalNavigation().OpenWeb(new Uri("https://github.com/someone-else")));
        Assert.False(new WindowsExternalNavigation().OpenWeb(new Uri("https://example.com/rsehenth")));
    }

    // ---------- guardas de código-fonte ----------

    [Fact]
    public void Paginas_nao_tem_cores_fixas_nem_referencias_estaticas_a_pincéis_de_tema()
    {
        var problems = new List<string>();
        foreach (var file in XamlSources())
        {
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (Regex.IsMatch(text, "=\"#[0-9A-Fa-f]{6,8}\"")) problems.Add($"{name}: cor hexadecimal literal");
            if (Regex.IsMatch(text, @"Converter=\{DynamicResource")) problems.Add($"{name}: DynamicResource em Converter");
            var declared = Regex.Matches(text, "x:Key=\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();
            foreach (Match m in Regex.Matches(text, @"\{StaticResource (\w+Brush)\}"))
            {
                if (!declared.Contains(m.Groups[1].Value)) problems.Add($"{name}: StaticResource {m.Groups[1].Value} (use DynamicResource)");
            }
        }

        var controls = File.ReadAllText(Path.Combine(SourceRoot(), "Themes", "Controls.xaml"));
        if (Regex.IsMatch(controls, "=\"#[0-9A-Fa-f]{6,8}\"")) problems.Add("Controls.xaml: cor hexadecimal literal");
        if (Regex.IsMatch(controls, @"\{StaticResource \w+Brush\}")) problems.Add("Controls.xaml: StaticResource de pincel");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public Task Toda_chave_DynamicResource_usada_existe_nos_tokens_das_duas_skins() => WpfTestThread.Run(() =>
    {
        var dark = Tokens(AppTheme.Dark);
        var light = Tokens(AppTheme.Light);
        var missing = new List<string>();
        foreach (var file in XamlSources().Append(Path.Combine(SourceRoot(), "Themes", "Controls.xaml")))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{DynamicResource (\w+)\}"))
            {
                var key = m.Groups[1].Value;
                if (!dark.Contains(key) || !light.Contains(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }

        Assert.True(missing.Count == 0, string.Join(Environment.NewLine, missing.Distinct()));
        return Task.CompletedTask;
    });

    // ---------- utilidades ----------

    private sealed record GridRow(string Name, DateTime Date);

    private sealed class RecordingSettings : ISettingsService
    {
        public AppSettings Settings { get; } = new();
        public string DataDirectory => Path.GetTempPath();
        public string HistoryDatabasePath => Path.Combine(Path.GetTempPath(), "history.db");
        public int Saves { get; private set; }
        public bool FailSave { get; init; }

        public void Save()
        {
            if (FailSave) throw new IOException("disco cheio (teste)");
            Saves++;
        }
    }

    private sealed class RecordingNavigation : IExternalNavigation
    {
        public List<string> Urls { get; } = [];
        public bool OpenWeb(Uri uri) { Urls.Add(uri.AbsoluteUri); return true; }
        public bool OpenFileLocation(string path) => false;
    }

    private sealed class OfflineWinGet : IWinGetService
    {
        public string? ExecutablePath => null;
        public WinGetAvailability? LastDetection => null;
        public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetAvailability { IsInstalled = false });
        public Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new WinGetScanResult());
        public Task<ProcessRunResult> UpgradePackageAsync(WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static void Restore() => ThemeResources.Install(Application.Current.Resources, AppTheme.Light);

    private static Window Hidden(object content) =>
        new() { Content = content, Width = 480, Height = 320, ShowActivated = false, ShowInTaskbar = false, Opacity = 0 };

    private static Color BrushColor(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

    private static ResourceDictionary Tokens(AppTheme theme) =>
        new() { Source = new Uri($"pack://application:,,,/WinCoreManager;component/Themes/Tokens.{theme}.xaml") };

    private static double RootOpacity(Control control)
    {
        control.ApplyTemplate();
        return ((UIElement)control.Template.FindName("layoutRoot", control)).Opacity;
    }

    private static double Contrast(Color a, Color b)
    {
        static double Lin(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double L(Color c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
        var (hi, lo) = (Math.Max(L(a), L(b)), Math.Min(L(a), L(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static string SourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "WinCoreManager", "WinCoreManager.csproj")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("src/WinCoreManager"), "src", "WinCoreManager");
    }

    private static IEnumerable<string> XamlSources() =>
        Directory.GetFiles(Path.Combine(SourceRoot(), "Views"), "*.xaml").Append(Path.Combine(SourceRoot(), "MainWindow.xaml"));
}
