using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WinCoreManager.Helpers;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.Tests;

/// <summary>Redesign Nebula: apenas apresentação (traduções novas, ícones e recursos de arte).</summary>
public class NebulaPresentationTests
{
    [Fact]
    public void Medidas_do_painel_sao_traduzidas_com_a_cultura_exibida()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        var free = LocalizationService.Source("Dashboard.Free", "18,53 GiB");
        var space = LocalizationService.Source("Dashboard.DiskSpace", "773,23 GiB", "930,58 GiB");

        Assert.Equal("18.53 GiB free", PresentationText.Translate(free, english));
        Assert.Equal("Temperature: Restricted access",
            PresentationText.Translate(LocalizationService.Source("Dashboard.Temperature", "Restricted access"), english));
        Assert.Equal("773.23 GiB of 930.58 GiB", PresentationText.Translate(space, english));
        Assert.Equal("Used (C:)", PresentationText.Translate(LocalizationService.Source("Dashboard.DiskUsed", "C:"), english));
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    public Task Icones_e_arte_usados_pelas_telas_existem_nas_duas_skins(AppTheme theme) => WpfTestThread.Run(() =>
    {
        try
        {
            ThemeResources.Install(Application.Current.Resources, theme);
            foreach (var name in new[]
                     {
                         "Home", "Update", "Wrench", "Trash", "Power", "Clean", "Sliders", "Computer", "Sensor", "Box", "History",
                         "Settings", "Info", "External", "Disk", "Check", "Chevron", "Download", "Cpu", "Gpu", "Ram", "Windows",
                         "Shield", "Bolt", "Calendar", "Chart",
                     })
                Assert.IsAssignableFrom<Geometry>(Application.Current.FindResource("Icon." + name));
            foreach (var file in new[]
                     {
                         "montanhas-painel", "banner-bode-e-montanhas", "ondas-windows-azuis", "reciclagem-3d", "empty-history",
                         "hdd", "ssd", "nvme",
                     })
                Assert.NotNull(Application.GetResourceStream(
                    new Uri($"pack://application:,,,/WinCoreManager;component/Assets/Nebula/{file}.png")));
            Assert.NotNull(Application.GetResourceStream(new Uri("pack://application:,,,/WinCoreManager;component/Assets/logo.png")));
        }
        finally { ThemeResources.Install(Application.Current.Resources, AppTheme.Light); }
        return Task.CompletedTask;
    });

    [Fact]
    public void Medidor_sem_leitura_nao_inventa_valor() => Assert.True(double.IsNaN((double)ArcGauge.ValueProperty.DefaultMetadata.DefaultValue));
}
