using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

/// <summary>Chave de desinstalação em memória para os testes (nenhum acesso ao Registro real).</summary>
public sealed class FakeRegistryKey : IUninstallRegistryKey
{
    private readonly Dictionary<string, object> _values;

    public FakeRegistryKey(string keyName, string fullName, bool is64Bit, Dictionary<string, object>? values = null)
    {
        KeyName = keyName;
        FullName = fullName;
        Is64BitView = is64Bit;
        Origin = is64Bit ? "Registro (64 bits)" : "Registro (32 bits)";
        _values = values ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    public string KeyName { get; }

    public string FullName { get; }

    public bool Is64BitView { get; }

    public string Origin { get; }

    public string? GetString(string valueName) => _values.TryGetValue(valueName, out var value) && value is string text ? text : null;

    public int GetInt(string valueName, int fallback = 0) => _values.TryGetValue(valueName, out var value) && value is int number ? number : fallback;

    public IReadOnlyList<string> GetValueNames() => _values.Keys.ToList();

    public void Dispose()
    {
    }
}

public sealed class FakeRegistryView(params IUninstallRegistryKey[] keys) : IUninstallRegistryView
{
    public IReadOnlyList<IUninstallRegistryKey> GetUninstallKeys() => keys;

    public bool KeyExists(string hiveName, string subKeyPath, bool is64BitView) =>
        keys.Any(k => string.Equals(k.FullName, $"{hiveName}\\{subKeyPath}", StringComparison.OrdinalIgnoreCase)
                      && k.Is64BitView == is64BitView);

    public void Dispose()
    {
    }
}

public class RegistryProgramScannerTests
{
    private static RegistryProgramScanner CreateScanner(params IUninstallRegistryKey[] keys)
        => new(new FakeRegistryView(keys));

    [Fact]
    public async Task EntradaValidaECamposSaoMapeados()
    {
        var key = new FakeRegistryKey("Exemplo", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Exemplo", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Aplicativo Exemplo",
            ["DisplayVersion"] = "1.2.3",
            ["Publisher"] = "Fabricante SA",
            ["UninstallString"] = @"C:\Program Files\Exemplo\unins000.exe",
            ["InstallLocation"] = @"C:\Program Files\Exemplo",
            ["InstallSource"] = @"C:\Fonte",
            ["InstallDate"] = "20250131",
            ["EstimatedSize"] = 15360,
            ["URLInfoAbout"] = "https://exemplo.com",
        });

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.Equal("Aplicativo Exemplo", program.Name);
        Assert.Equal("1.2.3", program.DisplayVersion);
        Assert.Equal("Fabricante SA", program.Publisher);
        Assert.Equal("Registro (64 bits)", program.Origin);
        Assert.True(program.Is64Bit);
        Assert.Equal(new DateTime(2025, 1, 31), program.InstallDate);
        Assert.Equal(15360, program.EstimatedSizeKb);
        Assert.Equal(ProgramKind.Executable, program.Kind);
        Assert.False(program.IsSystemComponent);
        Assert.False(program.IsUpdate);
        Assert.False(program.IsProtected);
    }

    [Fact]
    public async Task ChaveOrfaSemInformacaoUtilEIgnorada()
    {
        var key = new FakeRegistryKey("Orfao", @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Orfao", true);

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        Assert.Empty(programs);
    }

    [Fact]
    public async Task FlagWindowsInstallerSeparaCodigoMsiDaChaveDoBundle()
    {
        var guid = Guid.NewGuid();
        var bundle = Guid.NewGuid();
        var key = new FakeRegistryKey(guid.ToString("B"), $@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{guid:B}", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Produto MSI",
            ["WindowsInstaller"] = 1,
            ["BundleProviderKey"] = bundle.ToString("B"),
        });

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.Equal(ProgramKind.Msi, program.Kind);
        Assert.Equal(guid, program.MsiProductCode);
        Assert.Equal(bundle, program.BundleProviderKey);
    }

    [Fact]
    public async Task GuidNoNomeDaChaveDeUmExeNaoViraCodigoMsiOuBundle()
    {
        var guid = Guid.NewGuid();
        var key = new FakeRegistryKey($"{{{guid}}}", @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{X}", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Bundle",
            ["UninstallString"] = @"C:\ProgramData\Package Cache\bundle.exe /uninstall /quiet",
        });

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.Equal(Guid.Empty, program.MsiProductCode);
        Assert.Equal(Guid.Empty, program.BundleProviderKey);
    }

    [Fact]
    public async Task DataDeInstalacaoInvertidaOuInvalidaNaoViraUmaDataPresumida()
    {
        var diaMesInvertido = new FakeRegistryKey("A", @"HKCU\...\A", false, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "A",
            ["InstallDate"] = "20253101", // YYYYDDMM
        });
        var invalida = new FakeRegistryKey("B", @"HKCU\...\B", false, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "B",
            ["InstallDate"] = "nao-e-data",
        });

        var programs = await CreateScanner(diaMesInvertido, invalida).GetInstalledProgramsAsync();

        var a = programs.Single(p => p.RegistryKeyName == "A");
        var b = programs.Single(p => p.RegistryKeyName == "B");
        Assert.Null(a.InstallDate);
        Assert.Null(b.InstallDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("20240230")]
    [InlineData("20230229")]
    [InlineData("20241305")]
    [InlineData("2024110A")]
    [InlineData("23/11/2026")]
    [InlineData(" 20241103 ")]
    [InlineData(20241103)]
    public async Task DataArpAusenteMalformadaOuImpossivelPermaneceNula(object? value)
    {
        var values = new Dictionary<string, object> { ["DisplayName"] = "Programa" };
        if (value is not null)
        {
            values["InstallDate"] = value;
        }

        var program = Assert.Single(await CreateScanner(
            new FakeRegistryKey("Produto", @"HKLM\...\Produto", true, values)).GetInstalledProgramsAsync());

        Assert.Null(program.InstallDate);
    }

    [Fact]
    public async Task DataArpDoMsiEhLidaNovamenteEmCadaConsulta()
    {
        var values = new Dictionary<string, object>
        {
            ["DisplayName"] = "Produto MSI",
            ["WindowsInstaller"] = 1,
            ["InstallDate"] = "20240229",
        };
        var scanner = CreateScanner(new FakeRegistryKey("Produto", @"HKLM\...\Produto", true, values));
        var original = Assert.Single(await scanner.GetInstalledProgramsAsync());

        values["InstallDate"] = "20261123";
        var updated = Assert.Single(await scanner.GetInstalledProgramsAsync());

        Assert.Equal(ProgramKind.Msi, original.Kind);
        Assert.Equal(new DateTime(2024, 2, 29), original.InstallDate);
        Assert.Equal(new DateTime(2026, 11, 23), updated.InstallDate);
    }

    [Fact]
    public async Task EntradasDeAtualizacaoSaoDetectadas()
    {
        // Como no BCU, hotfixes têm o valor padrão da chave no formato KB######.
        var hotfix = new FakeRegistryKey("KB123456", @"HKLM\...\KB123456", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = "KB123456",
            ["DisplayName"] = "Atualização (KB123456)",
        });
        var comPai = new FakeRegistryKey("Filho", @"HKLM\...\Filho", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Componente",
            ["ParentKeyName"] = "KB123456",
        });

        var programs = await CreateScanner(hotfix, comPai).GetInstalledProgramsAsync();

        Assert.All(programs, p => Assert.True(p.IsUpdate));
    }

    [Fact]
    public async Task NoRemoveESystemComponentSaoLidos()
    {
        var key = new FakeRegistryKey("Protegido", @"HKLM\...\Protegido", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Protegido",
            ["NoRemove"] = 1,
            ["SystemComponent"] = 1,
        });

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.True(program.IsProtected);
        Assert.True(program.IsSystemComponent);
    }

    [Fact]
    public async Task ValorOcultoUninstallStringHiddenEEncontrado()
    {
        var key = new FakeRegistryKey("Oculto", @"HKLM\...\Oculto", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Oculto",
            ["UninstallString_hidden"] = @"C:\Program Files\Oculto\un.exe",
        });

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.Equal(@"C:\Program Files\Oculto\un.exe", program.UninstallString);
    }

    [Fact]
    public async Task EntradasDuplicadasEntreVisoesSaoDeduplicadas()
    {
        var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Duplicado",
            ["Publisher"] = "Fab",
            ["UninstallString"] = @"C:\Program Files\Dup\un.exe",
        };
        var key64 = new FakeRegistryKey("Dup", @"HKLM\...\Dup", true, values);
        var key32 = new FakeRegistryKey("Dup", @"HKLM\WOW\...\Dup", false, values);

        var programs = await CreateScanner(key64, key32).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.True(program.Is64Bit);
    }

    [Fact]
    public async Task TipoSteamDetectadoPeloNomeDaChave()
    {
        var key = new FakeRegistryKey("Steam App 1234", @"HKLM\...\Steam App 1234", true, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["DisplayName"] = "Jogo Steam",
            ["UninstallString"] = @"C:\Program Files (x86)\Steam\steam.exe //uninstall/1234",
        });

        var programs = await CreateScanner(key).GetInstalledProgramsAsync();

        var program = Assert.Single(programs);
        Assert.Equal(ProgramKind.Steam, program.Kind);
    }
}
