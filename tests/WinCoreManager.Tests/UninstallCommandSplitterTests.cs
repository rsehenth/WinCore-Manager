using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

public class UninstallCommandSplitterTests
{
    [Fact]
    public void CaminhoEntreAspasComArgumentosSeparados()
    {
        var result = UninstallCommandSplitter.SeparateArgsFromCommand(
            @"""C:\Program Files\Exemplo\unins000.exe"" /SILENT");

        Assert.Equal(@"C:\Program Files\Exemplo\unins000.exe", result.FileName);
        Assert.Equal("/SILENT", result.Arguments);
    }

    [Fact]
    public void CaminhoSemAspasComArgumentosSeparados()
    {
        var result = UninstallCommandSplitter.SeparateArgsFromCommand(
            @"C:\Ferramenta\setup.exe /remove /quiet");

        Assert.Equal(@"C:\Ferramenta\setup.exe", result.FileName);
        Assert.Equal("/remove /quiet", result.Arguments);
    }

    [Fact]
    public void CaminhoSemEspacosNaoPrecisaDeAspas()
    {
        var result = UninstallCommandSplitter.SeparateArgsFromCommand(
            @"C:\Ferramenta\uninstall.exe");

        Assert.Equal(@"C:\Ferramenta\uninstall.exe", result.FileName);
        Assert.Equal(string.Empty, result.Arguments);
    }

    [Fact]
    public void ExecutavelSemCaminhoAntesDeArgumentoComCaminho()
    {
        // Formato clássico: "ExecutableName C:\arg\path.exe" (heurística do BCU).
        var result = UninstallCommandSplitter.SeparateArgsFromCommand(
            @"uninstall C:\temp\arquivo.exe -mode x");

        Assert.Equal("uninstall", result.FileName);
        Assert.Equal(@"C:\temp\arquivo.exe -mode x", result.Arguments);
    }

    [Fact]
    public void AspasSemFechamentoFalha()
    {
        var ok = UninstallCommandSplitter.TrySeparate(
            @"""C:\Program Files\Exemplo\unins000.exe /x",
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void ComandoVazioFalha()
    {
        Assert.False(UninstallCommandSplitter.TrySeparate("   ", out _));
        Assert.False(UninstallCommandSplitter.TrySeparate(null, out _));
    }

    [Fact]
    public void ArgumentosComPontosNaoConfundemOFimDoExecutavel()
    {
        // O ponto pertence ao argumento; o executável termina no primeiro espaço após o ponto.
        var result = UninstallCommandSplitter.SeparateArgsFromCommand(
            @"C:\App\un.exe -config C:\App\conf\app.config");

        Assert.Equal(@"C:\App\un.exe", result.FileName);
        Assert.Equal(@"-config C:\App\conf\app.config", result.Arguments);
    }
}
