using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

public class ResidueAnalysisTests
{
    private readonly ResidueAnalysisService _service = new(new FakeRegistryView());

    [Fact]
    public async Task AnaliseEhSomentePrevia()
    {
        var program = new InstalledProgram { Name = "X", RegistryKeyName = "X", RegistryPath = @"HKLM\...\X" };
        var report = await _service.AnalyzeAsync(program);
        Assert.True(report.IsPreviewOnly);
    }

    [Fact]
    public async Task ChavePresenteNaFonteConfiguradaEReportada()
    {
        var registryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Exemplo";
        var key = new FakeRegistryKey("Exemplo", registryPath, true);
        var service = new ResidueAnalysisService(new FakeRegistryView(key));
        var program = new InstalledProgram
        {
            Name = "X",
            RegistryKeyName = "Exemplo",
            RegistryPath = registryPath,
            Is64Bit = true,
        };

        var report = await service.AnalyzeAsync(program);

        Assert.True(report.RegistryKeyStillPresent);
        Assert.Contains(report.Findings, f => f.Kind == ResidueKind.RegistryKey);
    }

    [Fact]
    public async Task DiretorioPresenteEContabilizado()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", $"res_{Guid.NewGuid():N}");
        var subdirectory = Path.Combine(directory, "sub");
        Directory.CreateDirectory(subdirectory);
        var file1 = Path.Combine(directory, "a.txt");
        var file2 = Path.Combine(subdirectory, "b.txt");
        await File.WriteAllTextAsync(file1, new string('a', 2048));
        await File.WriteAllTextAsync(file2, new string('b', 1024));

        try
        {
            var program = new InstalledProgram
            {
                Name = "X",
                RegistryKeyName = "X",
                RegistryPath = @"HKLM\...\X",
                InstallLocation = directory,
            };

            var report = await _service.AnalyzeAsync(program);

            Assert.True(report.LocationExists);
            Assert.Equal(2, report.FileCount);
            Assert.Equal(1, report.DirectoryCount);
            Assert.Equal(3072, report.TotalBytes);
            Assert.Contains(report.Findings, f => f.Kind == ResidueKind.InstallationDirectory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DiretorioInexistenteNaoGeraConstatacao()
    {
        var program = new InstalledProgram
        {
            Name = "X",
            RegistryKeyName = "X",
            RegistryPath = @"HKLM\...\X",
            InstallLocation = @"C:\caminho\que\nao\existe\12345",
        };

        var report = await _service.AnalyzeAsync(program);

        Assert.False(report.LocationExists);
        Assert.Equal(0, report.FileCount);
        Assert.DoesNotContain(report.Findings, f => f.Kind == ResidueKind.InstallationDirectory);
    }

    [Fact]
    public async Task ChaveDeRegistroInexistenteNaoEReportadaComoPresente()
    {
        var program = new InstalledProgram
        {
            Name = "X",
            RegistryKeyName = "DefinitivamenteNaoExiste_98765",
            RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\DefinitivamenteNaoExiste_98765",
            Is64Bit = true,
        };

        var report = await _service.AnalyzeAsync(program);

        Assert.False(report.RegistryKeyStillPresent);
    }

    [Fact]
    public async Task CaminhoDeRegistroInvalidoNaoQuebraAAnalise()
    {
        var program = new InstalledProgram
        {
            Name = "X",
            RegistryKeyName = "X",
            RegistryPath = @"chave-invalida-sem-hive",
        };

        var report = await _service.AnalyzeAsync(program);

        Assert.False(report.RegistryKeyStillPresent);
        Assert.False(report.ScanFailed);
    }
}
