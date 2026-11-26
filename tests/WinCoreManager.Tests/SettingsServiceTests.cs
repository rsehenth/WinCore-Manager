using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class SettingsServiceTests
{
    [Fact]
    public void Ativar_portatil_preserva_configuracao_local_e_desativar_retorna_ao_banco_local()
    {
        var root = new TestSettings().DataDirectory;
        var executableDirectory = Path.Combine(root, "portable");
        var localDirectory = Path.Combine(root, "local");
        var settings = new SettingsService(executableDirectory, localDirectory);
        settings.Settings.HistoryRetentionDays = 30;
        settings.Save();

        settings.Settings.PortableMode = true;
        settings.Settings.HistoryRetentionDays = 7;
        settings.Save();
        Assert.Contains("30", File.ReadAllText(Path.Combine(localDirectory, "settings.json")));

        var portable = new SettingsService(executableDirectory, localDirectory);
        Assert.True(portable.Settings.PortableMode);
        Assert.Equal(Path.Combine(executableDirectory, "Data", "history.db"), portable.HistoryDatabasePath);
        Assert.Equal(7, portable.Settings.HistoryRetentionDays);

        portable.Settings.PortableMode = false;
        portable.Save();

        var local = new SettingsService(executableDirectory, localDirectory);
        Assert.False(local.Settings.PortableMode);
        Assert.Equal(Path.Combine(localDirectory, "history.db"), local.HistoryDatabasePath);
        Assert.Equal(7, local.Settings.HistoryRetentionDays);
    }

    [Fact]
    public void Marcador_portatil_bloqueado_preserva_modo_e_arquivo_original()
    {
        var root = new TestSettings().DataDirectory;
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "settings.json");
        const string original = "{\"PortableMode\":true,\"HistoryRetentionDays\":23}";
        File.WriteAllText(marker, original);
        SettingsService settings;
        using (File.Open(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = new SettingsService(root, Path.Combine(root, "local"));
            Assert.True(settings.Settings.PortableMode);
            Assert.Equal(Path.Combine(root, "Data"), settings.DataDirectory);
            Assert.Equal(0, settings.Settings.HistoryRetentionDays);
        }

        Assert.Throws<IOException>(() => settings.Save());
        Assert.Equal(original, File.ReadAllText(marker));
        Assert.Equal(23, new SettingsService(root, Path.Combine(root, "local")).Settings.HistoryRetentionDays);
    }

    [Fact]
    public void CampoInvalidoPreservaOutrasPreferencias()
    {
        var root = new TestSettings().DataDirectory;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), """{"PortableMode":true,"HistoryRetentionDays":[],"Language":"en-US","Theme":"Light"}""");
        var settings = new SettingsService(root, Path.Combine(root, "local"));
        Assert.True(settings.Settings.PortableMode);
        Assert.Equal(365, settings.Settings.HistoryRetentionDays);
        Assert.Equal("en-US", settings.Settings.Language);
        Assert.Equal("Light", settings.Settings.Theme);
    }

    [Fact]
    public void Marcador_portatil_legado_sem_propriedade_continua_ativando_modo_portatil()
    {
        var root = new TestSettings().DataDirectory;
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");

        var settings = new SettingsService(root, Path.Combine(root, "local"));

        Assert.True(settings.Settings.PortableMode);
        Assert.Equal(Path.Combine(root, "Data", "history.db"), settings.HistoryDatabasePath);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    public void JsonIlegivelPreservaMarcadorModoEConteudo(string original)
    {
        var root = new TestSettings().DataDirectory;
        var local = Path.Combine(root, "local");
        Directory.CreateDirectory(local);
        var marker = Path.Combine(root, "settings.json");
        File.WriteAllText(marker, original);
        File.WriteAllText(Path.Combine(local, "settings.json"), """{"Language":"en-US"}""");

        var settings = new SettingsService(root, local);
        Assert.True(settings.Settings.PortableMode);
        Assert.Equal(Path.Combine(root, "Data"), settings.DataDirectory);
        Assert.Equal(0, settings.Settings.HistoryRetentionDays);
        Assert.Contains(marker, settings.LoadProblem);
        Assert.Throws<IOException>(settings.Save);
        Assert.Equal(original, File.ReadAllText(marker));
    }
}
