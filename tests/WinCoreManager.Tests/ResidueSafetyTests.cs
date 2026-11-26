using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

/// <summary>
/// Testa as regras determinísticas de proteção com um ambiente sintético
/// (nenhum diretório real do Windows é avaliado).
/// </summary>
public class ResidueSafetyTests
{
    private static ResiduePathRules CreateRules(string root)
    {
        return new ResiduePathRules
        {
            WindowsDirectory = Path.Combine(root, "Windows"),
            ProgramFiles = Path.Combine(root, "Program Files"),
            ProgramFilesX86 = Path.Combine(root, "Program Files (x86)"),
            ProgramData = Path.Combine(root, "ProgramData"),
            UserProfile = Path.Combine(root, "Users", "atual"),
            AppDataRoaming = Path.Combine(root, "Users", "atual", "AppData", "Roaming"),
            AppDataLocal = Path.Combine(root, "Users", "atual", "AppData", "Local"),
            AppBaseDirectory = Path.Combine(root, "Program Files", "WinCore Manager"),
        };
    }

    private const string Root = @"C:\";

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    public void RaizDeUnidadeEProtegida(string path)
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(path, isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\regedit.exe")]
    public void DiretorioDoWindowsEProtegido(string path)
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(path, isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)")]
    [InlineData(@"C:\ProgramData")]
    public void RaizesDeEscopoSaoProtegidas(string path)
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(path, isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(@"\\servidor\compartilhado\Pasta")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"pasta\relativa")]
    [InlineData(@" ")]
    public void CaminhosAmbiguosOuRemotosSaoProtegidos(string path)
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(path, isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(@"C:\Users\outro\AppData\Roaming\Foo")]
    [InlineData(@"C:\Users\outro\Documents")]
    public void DadosDeOutroUsuarioSaoProtegidos(string path)
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(path, isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(@"C:\Users\atual\Documents\Programa")]
    [InlineData(@"C:\Users\atual\Desktop\lixo.txt")]
    public void PerfilDoUsuarioForaDoEscopoEProtegido(string path)
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(path, isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ProprioAplicativoEProtegido()
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(
            @"C:\Program Files\WinCore Manager\WinCoreManager.dll", isReparsePoint: false);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ReparsePointEProtegido()
    {
        var reason = CreateRules(Root).DescribeFileSystemProtection(
            @"C:\Program Files\Exemplo\link", isReparsePoint: true);
        Assert.Contains("nova análise", reason);
    }

    [Fact]
    public void PastasDentroDoEscopoNaoSaoProtegidasPorPadrao()
    {
        var rules = CreateRules(Root);
        Assert.Null(rules.DescribeFileSystemProtection(@"C:\Program Files\Exemplo", isReparsePoint: false));
        Assert.Null(rules.DescribeFileSystemProtection(@"C:\ProgramData\Exemplo\arquivo.txt", isReparsePoint: false));
        Assert.Null(rules.DescribeFileSystemProtection(@"C:\Users\atual\AppData\Local\Exemplo", isReparsePoint: false));
        Assert.Null(rules.DescribeFileSystemProtection(@"C:\Users\atual\AppData\Roaming\Exemplo", isReparsePoint: false));
    }

    [Fact]
    public void EscopoAprovadoReconheceRaizesPadraoEDiretorioDeInstalacao()
    {
        var rules = CreateRules(Root);
        Assert.True(rules.IsInsideApprovedScope(@"C:\Program Files\Exemplo\sub", null));
        Assert.True(rules.IsInsideApprovedScope(@"C:\Users\atual\AppData\Local\Exemplo", null));
        Assert.False(rules.IsInsideApprovedScope(@"C:\Ferramentas\Exemplo", null));

        // Diretório de instalação conhecido fora das raízes padrão é aceito.
        Assert.True(rules.IsInsideApprovedScope(@"C:\Ferramentas\Exemplo", @"C:\Ferramentas\Exemplo"));
        Assert.False(rules.IsInsideApprovedScope(@"C:\Ferramentas\Outro", @"C:\Ferramentas\Exemplo"));
    }

    [Fact]
    public void InstalacaoConhecidaDentroDoWindowsNaoExpandeEscopo()
    {
        var rules = CreateRules(Root);
        // O diretório de instalação informado está protegido (Windows): não pode expandir escopo.
        Assert.False(rules.IsInsideApprovedScope(@"C:\Windows\Foo", @"C:\Windows\Foo"));
    }

    [Fact]
    public void RegistroColmeiasInvalidasSaoProtegidas()
    {
        var rules = CreateRules(Root);
        Assert.NotNull(rules.DescribeRegistryProtection(@"HKEY_CLASSES_ROOT\Foo", []));
        Assert.NotNull(rules.DescribeRegistryProtection(@"chave-sem-hive", []));
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE")]
    [InlineData(@"HKEY_CURRENT_USER\")]
    [InlineData(@"HKEY_LOCAL_MACHINE")]
    public void RegistroNivelMuitoAltoEProtegido(string path)
    {
        var reason = CreateRules(Root).DescribeRegistryProtection(path, []);
        Assert.NotNull(reason);
    }

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Classes")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\Classes\Foo")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Exemplo")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Clients\Exemplo")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node")]
    public void RamificacoesCriticasSaoProtegidas(string path)
    {
        var reason = CreateRules(Root).DescribeRegistryProtection(path, []);
        Assert.NotNull(reason);
    }

    [Fact]
    public void ChaveDeDesinstalacaoPropriaEhExcecaoExplicita()
    {
        var rules = CreateRules(Root);
        var ownKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Exemplo";
        Assert.Null(rules.DescribeRegistryProtection(ownKey, [ownKey]));
        // Irmãs na mesma ramificação continuam bloqueadas.
        Assert.NotNull(rules.DescribeRegistryProtection(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Outra", [ownKey]));
    }

    [Fact]
    public void ChavesNormaisSobSoftwareNaoSaoProtegidas()
    {
        var rules = CreateRules(Root);
        Assert.Null(rules.DescribeRegistryProtection(@"HKEY_CURRENT_USER\SOFTWARE\Exemplo", []));
        Assert.Null(rules.DescribeRegistryProtection(@"HKEY_LOCAL_MACHINE\SOFTWARE\Exemplo\Fabricante", []));
    }
}
