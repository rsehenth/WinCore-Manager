using WinCoreManager.Services;

namespace WinCoreManager.Tests;

/// <summary>
/// Garante que a allowlist de manutenção contém exatamente os comandos
/// esperados, com argumentos fixos e nada destrutivo.
/// </summary>
public class MaintenanceAllowlistTests
{
    [Fact]
    public void Catálogo_contém_as_sete_ferramentas_do_MVP()
    {
        var keys = MaintenanceService.Tools.Select(t => t.Key).ToHashSet();

        Assert.Equal(
            new HashSet<string>
            {
                "sfc_verifyonly",
                "sfc_scannow",
                "dism_checkhealth",
                "dism_scanhealth",
                "dism_restorehealth",
                "chkdsk_scan",
                "flushdns",
            },
            keys);
    }

    [Fact]
    public void Argumentos_correspondem_exatamente_à_allowlist()
    {
        var expected = new Dictionary<string, string>
        {
            ["sfc_verifyonly"] = "/verifyonly",
            ["sfc_scannow"] = "/scannow",
            ["dism_checkhealth"] = "/Online /Cleanup-Image /CheckHealth",
            ["dism_scanhealth"] = "/Online /Cleanup-Image /ScanHealth",
            ["dism_restorehealth"] = "/Online /Cleanup-Image /RestoreHealth",
            ["chkdsk_scan"] = "/scan",
            ["flushdns"] = "/flushdns",
        };

        foreach (var tool in MaintenanceService.Tools)
        {
            Assert.Equal(expected[tool.Key], tool.Arguments);
        }
    }

    [Fact]
    public void Executáveis_apontam_para_System32_com_caminho_absoluto()
    {
        foreach (var tool in MaintenanceService.Tools)
        {
            Assert.True(Path.IsPathRooted(tool.SystemExecutable), tool.Key);
            Assert.StartsWith(Environment.SystemDirectory, tool.SystemExecutable, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Nenhum_argumento_destrutivo_está_na_allowlist()
    {
        var forbidden = new[] { "/f", "/r", "/x", "/resetbase", "/reboot", "--silent", "--disable-interactivity" };

        foreach (var tool in MaintenanceService.Tools)
        {
            var tokens = tool.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var token in tokens)
            {
                Assert.DoesNotContain(token.ToLowerInvariant(), forbidden);
            }
        }
    }

    [Fact]
    public void Ferramentas_de_manutencao_seguem_o_fluxo_elevado()
    {
        Assert.True(MaintenanceService.Tools.First(t => t.Key == "sfc_verifyonly").RequiresElevation);
        Assert.True(MaintenanceService.Tools.First(t => t.Key == "sfc_scannow").RequiresElevation);
        Assert.True(MaintenanceService.Tools.First(t => t.Key == "dism_restorehealth").RequiresElevation);
        Assert.True(MaintenanceService.Tools.First(t => t.Key == "chkdsk_scan").RequiresElevation);
        Assert.True(MaintenanceService.Tools.First(t => t.Key == "flushdns").RequiresElevation);
    }

    [Fact]
    public void Letras_de_unidade_validam_contra_discos_fixos()
    {
        Assert.False(MaintenanceService.IsValidDriveLetter(null));
        Assert.False(MaintenanceService.IsValidDriveLetter(string.Empty));
        Assert.False(MaintenanceService.IsValidDriveLetter("CC"));
        Assert.False(MaintenanceService.IsValidDriveLetter("1"));
        Assert.False(MaintenanceService.IsValidDriveLetter("Z:"));

        // Toda unidade fixa listada deve ser aceita.
        foreach (var drive in MaintenanceService.GetFixedDriveLetters())
        {
            Assert.True(MaintenanceService.IsValidDriveLetter(drive));
            Assert.Equal("NTFS", new DriveInfo(drive).DriveFormat, ignoreCase: true);
        }
    }
}
