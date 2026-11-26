using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class WingetTableParserTests
{
    private const string PtBrFixture =
        """
        Nome                                 ID                                 Versão         Disponível     Origem
        ------------------------------------------------------------------------------------------------------------
        4K Video Downloader+                 OpenMedia.4KVideoDownloaderPlus    26.3.1.0389    26.4.0.406     winget
        Foxit PDF Editor                     Foxit.PhantomPDF.Subscription.MSI  2026.1.3.36551 2026.2.1.39815 winget
        MediaHuman YouTube Downloader 3.9.22 MediaHuman.YouTubeDownloader       3.9.22         3.9.24         winget
        Revo Uninstaller Pro 5.5.2           RevoUninstaller.RevoUninstallerPro 5.5.2          5.5.4.0        winget
        TcNo Account Switcher                TechNobo.TcNoAccountSwitcher.Beta  4.0.6          4.0.9.0        winget
        Waves Central                        WavesAudio.WavesCentral            16.7.2         17.1.5.343994  winget
        6 atualizações disponíveis.
        """;

    private const string EnUsFixture =
        """
        Name                                 ID                                 Version        Available      Source
        ------------------------------------------------------------------------------------------------------------
        7-Zip 24.09 (x64)                    7zip.7zip                          24.09          25.01          winget
        Git                                  Git.Git                            2.47.1         2.51.0         winget
        2 upgrades available.
        """;

    [Fact]
    public void Parse_ptBR_fixture_retorna_todos_os_pacotes()
    {
        var packages = WingetTableParser.Parse(PtBrFixture);

        Assert.Equal(6, packages.Count);

        var first = packages[0];
        Assert.Equal("4K Video Downloader+", first.Name);
        Assert.Equal("OpenMedia.4KVideoDownloaderPlus", first.Id);
        Assert.Equal("26.3.1.0389", first.InstalledVersion);
        Assert.Equal("26.4.0.406", first.AvailableVersion);
        Assert.Equal("winget", first.Source);
    }

    [Fact]
    public void Parse_fixture_enUS_é_independente_de_idioma()
    {
        var packages = WingetTableParser.Parse(EnUsFixture);

        Assert.Equal(2, packages.Count);
        Assert.Equal("7-Zip 24.09 (x64)", packages[0].Name);
        Assert.Equal("7zip.7zip", packages[0].Id);
        Assert.Equal("24.09", packages[0].InstalledVersion);
        Assert.Equal("25.01", packages[0].AvailableVersion);
        Assert.Equal("Git", packages[1].Name);
        Assert.Equal("2.51.0", packages[1].AvailableVersion);
    }

    [Fact]
    public void Parse_linhas_de_rodapé_não_viram_pacotes()
    {
        var packages = WingetTableParser.Parse(PtBrFixture);

        Assert.DoesNotContain(packages, p => p.Id.Contains("atualizações", StringComparison.OrdinalIgnoreCase));
        Assert.All(packages, p => Assert.DoesNotContain(' ', p.Id));
    }

    [Fact]
    public void Parse_linha_com_versão_disponível_vazia_é_ignorada()
    {
        const string output =
            """
            Nome          ID          Versão  Disponível  Origem
            -------------------------------------------------------
            App A         Id.A        1.0     2.0         winget
            App B         Id.B        3.0                 winget
            1 atualizações disponíveis.
            """;

        var packages = WingetTableParser.Parse(output);

        var pkg = Assert.Single(packages);
        Assert.Equal("App A", pkg.Name);
    }

    [Fact]
    public void Parse_sem_separador_retorna_lista_vazia()
    {
        var packages = WingetTableParser.Parse("Um erro qualquer do winget sem tabela.");

        Assert.Empty(packages);
    }

    [Fact]
    public void Parse_saída_vazia_ou_nula_retorna_lista_vazia()
    {
        Assert.Empty(WingetTableParser.Parse(string.Empty));
        Assert.Empty(WingetTableParser.Parse("   \n  "));
    }

    [Fact]
    public void Parse_linha_de_rodapé_iniciada_por_número_não_é_confundida_com_pacote()
    {
        const string output =
            """
            Nome          ID          Versão  Disponível  Origem
            -------------------------------------------------------
            App A         Id.A        1.0     2.0         winget
            7-Zip         7zip.7zip   24.09   25.01       winget
            2 atualizações disponíveis.
            """;

        var packages = WingetTableParser.Parse(output);

        Assert.Equal(2, packages.Count);
        Assert.Equal("7-Zip", packages[1].Name);
    }

    [Fact]
    public void Parse_aceita_nome_localizado_com_caracteres_largos_e_nome_iniciado_por_número()
    {
        const string output = """
            Nombre             Id        Versión actual  Disponible  Origen
            -----------------------------------------------------------------
            日本語 App         Test.App  1.0             2.0         winget
            360 Antivirus      Test.AV   1.0             2.0         winget
            2 actualizaciones disponibles.
            """;

        var packages = WingetTableParser.Parse(output);

        Assert.Equal(new[] { "Test.App", "Test.AV" }, packages.Select(package => package.Id));
        Assert.All(packages, package => Assert.Equal("2.0", package.AvailableVersion));
    }

    [Fact]
    public void Parse_ignora_id_truncado_e_lê_segunda_tabela()
    {
        const string output = """
            Name         Id          Version  Available  Source
            ----------------------------------------------------
            Truncated    Vendor.Ap…  1.0      2.0        winget
            1 upgrades available.

            Name         Id          Version  Available  Source
            ----------------------------------------------------
            App          Vendor.App  Unknown  2.0        msstore
            """;

        var package = Assert.Single(WingetTableParser.Parse(output));
        Assert.Equal("Vendor.App", package.Id);
        Assert.Equal("msstore", package.Source);
    }
}
