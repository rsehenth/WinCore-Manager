using System.Management;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.Tests;

public sealed class RestorePointTests
{
    [Fact]
    public async Task Seleciona_ponto_mais_recente_por_instante_com_fuso()
    {
        var state = new WindowsFeaturesFixture();
        state.RestoreReader.Points =
        [
            new("20261123150000.123456+060", "Anterior"),
            new("20261123140000.123456-120", "Mais recente"),
        ];
        var result = await state.Restore.ReadAsync();
        Assert.Equal(RestoreQueryState.Found, result.State);
        Assert.Equal("Mais recente", result.Description);
        Assert.Equal(TimeSpan.FromHours(-2), result.Created!.Value.Offset);
        Assert.Equal(16, result.Created.Value.UtcDateTime.Hour);
    }

    [Theory]
    [InlineData("20261323150000.123456+060")]
    [InlineData("20261123150000.123456+900")]
    [InlineData("20261123150000.123456+***")]
    [InlineData("indisponível")]
    [InlineData(null)]
    public async Task Data_invalida_nao_inventa_ponto(string? invalid)
    {
        var state = new WindowsFeaturesFixture();
        state.RestoreReader.Points = [new(invalid, "Inválido")];
        var result = await state.Restore.ReadAsync();
        Assert.Equal(RestoreQueryState.InvalidDate, result.State);
        Assert.Null(result.Created);
    }

    [Fact]
    public async Task Distingue_sem_pontos_desativado_permissao_e_falha()
    {
        var state = new WindowsFeaturesFixture();
        state.RestoreReader.Points = [];
        Assert.Equal(RestoreQueryState.Empty, (await state.Restore.ReadAsync()).State);
        state.RestoreReader.Disabled = true;
        Assert.Equal(RestoreQueryState.Disabled, (await state.Restore.ReadAsync()).State);
        Assert.Equal(1, state.RestoreReader.Reads);
        state.RestoreReader.Disabled = false;
        state.RestoreReader.Error = new UnauthorizedAccessException();
        Assert.Equal(RestoreQueryState.AccessDenied, (await state.Restore.ReadAsync()).State);
        state.RestoreReader.Error = new ManagementException("WMI indisponível");
        Assert.Equal(RestoreQueryState.Unavailable, (await state.Restore.ReadAsync()).State);
    }

    [Fact]
    public async Task Consulta_e_abertura_da_tool_nativa_sao_acoes_distintas()
    {
        var state = new WindowsFeaturesFixture();
        await state.TweaksPage.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(1, state.RestoreReader.Reads);
        Assert.Contains("Último ponto", state.TweaksPage.RestoreStatus);
        Assert.Equal("Ponto de teste", state.TweaksPage.RestoreDescription);
        Assert.Empty(state.Tools.Calls);
        Assert.Empty(state.System.Writes);

        state.TweaksPage.OpenProtectionCommand.Execute(null);
        Assert.Equal((WindowsTool.SystemProtection, null), Assert.Single(state.Tools.Calls));
        Assert.Empty(state.System.Writes);
    }
}

