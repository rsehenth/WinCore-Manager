using Microsoft.Win32;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

/// <summary>Reprodução dos defeitos da auditoria contra o código anterior às correções.</summary>
public class ResidueAuditReproTests : IDisposable
{
    private const string OwnKey = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp";
    private readonly ResidueTestEnvironment _env = new("repro");
    private readonly FakeResidueRegistryReader _reader = new();
    private readonly FakeResidueRegistryWriter _writer = new();

    private static InstalledProgram Program(string location = "", string uninstall = "") => new()
    {
        Name = "Exemplo App",
        RegistryKeyName = "ExemploApp",
        RegistryPath = OwnKey,
        Is64Bit = true,
        InstallLocation = location,
        UninstallString = uninstall,
    };

    [Fact]
    public void R01_ProgramaAindaInstaladoNaoPodeSerAlta()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var uninstaller = Path.Combine(directory, "unins000.exe");
        File.WriteAllText(uninstaller, "x");
        var view = new FakeRegistryView(new FakeRegistryKey("ExemploApp", OwnKey, true));
        var engine = new ResidueDetectionEngine(view, _reader, _env.Rules);

        var result = engine.Detect(Program(directory, $"\"{uninstaller}\""), [], CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Confidence == ResidueConfidence.High);
    }

    [Fact]
    public void R02_DiretorioPaiDeOutroProgramaNaoPodeSerAlta()
    {
        var shared = Path.Combine(_env.Root, "Apps", "Exemplo App");
        var other = Path.Combine(shared, "Outro Produto");
        Directory.CreateDirectory(other);
        var engine = new ResidueDetectionEngine(new FakeRegistryView(), _reader, _env.Rules);
        var otherProgram = new InstalledProgram
        {
            Name = "Outro Produto",
            RegistryKeyName = "Outro",
            RegistryPath = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Outro",
            InstallLocation = other,
        };

        var result = engine.Detect(Program(shared), [otherProgram], CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Location == shared && i.Confidence == ResidueConfidence.High);
    }

    [Fact]
    public void R03_BackupNaoPodeTruncarValoresSilenciosamente()
    {
        var values = new Dictionary<string, object>();
        for (var i = 0; i < 600; i++)
        {
            values[$"v{i}"] = "x";
        }

        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", values);
        var service = CreateQuarantine();

        var entry = TryExport(service);

        if (entry is not null)
        {
            var reg = File.ReadAllText(entry.RegistryBackupFile!);
            Assert.Contains("\"v599\"", reg);
        }
    }

    [Fact]
    public void R04_ExportacaoRegUsaFormatoValido()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Bin"] = new byte[] { 0xDE, 0xAD },
            ["Neg"] = -1,
            ["Q"] = 1L,
        });

        var entry = CreateQuarantine().ExportRegistryKey(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", true,
            @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App", "Exemplo App", CancellationToken.None);

        var reg = File.ReadAllText(entry.RegistryBackupFile!);
        Assert.Contains("\"Bin\"=hex:de,ad", reg);
        Assert.Contains("\"Neg\"=dword:ffffffff", reg);
        Assert.Contains("\"Q\"=hex(b):01,00,00,00,00,00,00,00", reg);
    }

    [Fact]
    public void R05_ManifestoCorrompidoNaoPodeSerSobrescrito()
    {
        var root = Path.Combine(_env.Root, "quarantine");
        Directory.CreateDirectory(root);
        var manifest = Path.Combine(root, "manifest.json");
        File.WriteAllText(manifest, "{ corrompido");
        var file = Path.Combine(_env.ProgramData, "Exemplo App.log");
        File.WriteAllText(file, "dados");

        try
        {
            CreateQuarantine().MoveToQuarantine(file, ResidueCategory.File, "Exemplo App", CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
        }

        Assert.Equal("{ corrompido", File.ReadAllText(manifest));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void R06_RecuperacaoNaoAceitaManifestoAdulterado()
    {
        var root = Path.Combine(_env.Root, "quarantine");
        Directory.CreateDirectory(root);
        var outside = Path.Combine(_env.Root, "fora.txt");
        File.WriteAllText(outside, "payload");
        var target = Path.Combine(_env.WindowsDir, "plantado.dll");
        var id = Guid.NewGuid();
        File.WriteAllText(Path.Combine(root, "manifest.json"), System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new QuarantineEntry { Id = id, OriginalPath = target, QuarantinedPath = outside, Category = ResidueCategory.File },
        }));

        var result = CreateQuarantine().Restore(id, overwriteConfirmed: false, CancellationToken.None);

        Assert.NotEqual(QuarantineRestoreStatus.Restored, result.Status);
        Assert.False(File.Exists(target));
        Assert.True(File.Exists(outside));
    }

    private ResidueQuarantineService CreateQuarantine() =>
        new(Path.Combine(_env.Root, "quarantine"), _reader, _writer, _env.Rules);

    private static QuarantineEntry? TryExport(ResidueQuarantineService service)
    {
        try
        {
            return service.ExportRegistryKey(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", true,
                @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App", "Exemplo App", CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public void Dispose() => _env.Dispose();
}
