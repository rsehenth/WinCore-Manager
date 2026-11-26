using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class WingetExitCodesTests
{
    [Fact]
    public void Codigo_zero_indica_sucesso()
    {
        var text = WingetExitCodes.Describe(0);
        Assert.Contains("sucesso", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(3010, "necessário")]
    [InlineData(1641, "iniciou")]
    [InlineData(unchecked((int)0x8A150109), "necessário")]
    [InlineData(unchecked((int)0x8A15010B), "iniciou")]
    public void Codigos_de_reinicio_distinguem_necessidade_e_reinicio_iniciado(int exitCode, string expected)
    {
        var text = WingetExitCodes.Describe(exitCode);
        Assert.Contains("reinício", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(expected, text, StringComparison.OrdinalIgnoreCase);
        Assert.True(WingetExitCodes.RequiresReboot(exitCode));
    }

    [Fact]
    public void Codigo_winget_negativo_é_apresentado_em_hex()
    {
        // 0x8A15002B como int assinado.
        var wingetError = unchecked((int)0x8A15002B);
        var text = WingetExitCodes.Describe(wingetError);

        Assert.Contains("0x8A15002B", text);
        Assert.Contains("atualização aplicável", text);
    }

    [Fact]
    public void Codigo_desconhecido_é_informado_sem_invenção()
    {
        var text = WingetExitCodes.Describe(12345);
        Assert.Contains("12345", text);
    }
}
