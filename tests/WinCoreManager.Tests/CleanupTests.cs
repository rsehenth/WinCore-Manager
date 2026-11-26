using WinCoreManager.Models;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.Tests;

public sealed class CleanupTests
{
    [Fact]
    public async Task Catalogo_dinamico_volume_D_e_estimativa_parcial()
    {
        var state = new WindowsFeaturesFixture();
        var snapshot = await state.Cleanup.ReadAsync();

        Assert.Equal(@"D:\", snapshot.Volume!.Root);
        Assert.Equal(512 * 1024 * 1024, snapshot.Volume.Used);
        Assert.Equal(state.Disk.Catalog.Select(row => row.Id), snapshot.Categories.Select(row => row.Id));
        Assert.Equal(1, snapshot.KnownCount);
        Assert.Equal(2 * 1024 * 1024, snapshot.KnownBytes);
        Assert.Null(snapshot.Categories[1].Bytes);
        Assert.False(snapshot.Categories[1].Selectable);

        await state.CleanupPage.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("D:", state.CleanupPage.VolumeText);
        Assert.Contains("1 de 2", state.CleanupPage.EstimateText);
        Assert.All(state.CleanupPage.Categories, row => Assert.False(row.IsSelected));
        Assert.False(state.CleanupPage.EmptyCommand.CanExecute(null));
        state.CleanupPage.Categories[0].IsSelected = true;
        Assert.Contains("2 MB", state.CleanupPage.SelectionText);
        Assert.True(state.CleanupPage.EmptyCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(null, "Tamanho indisponível")]
    [InlineData(0L, "0 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1048576L, "1 MB")]
    [InlineData(1073741824L, "1 GB")]
    [InlineData(1099511627776L, "1 TB")]
    public void Formata_bytes_e_tamanho_desconhecido(long? bytes, string expected) =>
        Assert.Equal(expected, CleanupService.FormatBytes(bytes));

    [Fact]
    public async Task Estimativa_ausente_e_ferramenta_indisponivel_desabilitam_acoes()
    {
        var state = new WindowsFeaturesFixture();
        state.Tools.Available = false;
        state.Disk.Estimate = null;
        await state.CleanupPage.RefreshCommand.ExecuteAsync(null);
        Assert.Contains("indisponível", state.CleanupPage.EstimateText);
        Assert.False(state.CleanupPage.OpenNativeCommand.CanExecute(null));
        Assert.All(state.CleanupPage.Categories, row => Assert.Equal("Tamanho indisponível", row.Size));
        Assert.Empty(state.Tools.Calls);
        Assert.Empty(state.Disk.Requested);
    }

    [Fact]
    public async Task Ferramenta_abre_com_volume_correto_e_repassa_falha_de_elevacao()
    {
        var state = new WindowsFeaturesFixture();
        var volume = (await state.Cleanup.ReadAsync()).Volume;
        state.Tools.AllowOpen = false;
        Assert.False(state.Cleanup.OpenNative(volume).Success);
        Assert.Equal((WindowsTool.DiskCleanup, @"D:\"), Assert.Single(state.Tools.Calls));
        state.Disk.Volume = state.Disk.Volume with { Identity = "volume trocado" };
        Assert.False(state.Cleanup.OpenNative(volume).Success);
        Assert.Single(state.Tools.Calls);
    }

    [Fact]
    public async Task Recusa_e_confirmacao_se_aplicam_somente_a_lixeira_do_volume_selecionado()
    {
        var history = new FakeHistoryService();
        var state = new WindowsFeaturesFixture(history);
        var snapshot = await state.Cleanup.ReadAsync();
        var selection = snapshot.Categories.First();
        Assert.False((await state.Cleanup.EmptyAsync(snapshot, selection, false, 0)).Success);
        Assert.False((await state.Cleanup.EmptyAsync(snapshot, snapshot.Categories[1], true, 0)).Success);
        Assert.Empty(state.Disk.Requested);

        var result = await state.Cleanup.EmptyAsync(snapshot, selection, true, 0);
        Assert.True(result.Success);
        Assert.Equal(@"D:\", Assert.Single(state.Disk.Requested));
        Assert.Equal(HistoryStatus.Success, Assert.Single(history.Records).Status);
        Assert.Empty(state.Tools.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mudanca_de_destino_ou_de_conteudo_recusa_execucao(bool changeDrive)
    {
        var state = new WindowsFeaturesFixture();
        var snapshot = await state.Cleanup.ReadAsync();
        if (changeDrive) state.Disk.Volume = state.Disk.Volume with { Root = @"E:\" };
        else state.Disk.Estimate = new(2048, 2);
        Assert.False((await state.Cleanup.EmptyAsync(snapshot, snapshot.Categories[0], true, 0)).Success);
        Assert.Empty(state.Disk.Requested);
    }

    [Fact]
    public async Task Volume_sem_letra_e_falha_de_deteccao_impedem_abertura_e_exclusao()
    {
        var state = new WindowsFeaturesFixture();
        state.Disk.Volume = state.Disk.Volume with { Root = @"D:\mount\" };
        await state.CleanupPage.RefreshCommand.ExecuteAsync(null);
        Assert.False(state.CleanupPage.EmptyCommand.CanExecute(null));
        Assert.False(state.CleanupPage.OpenNativeCommand.CanExecute(null));
        Assert.Empty(state.Disk.Requested);
        state.Disk.DiskError = new IOException();
        Assert.Null((await state.Cleanup.ReadAsync()).Volume);
    }

    [Fact]
    public async Task Lixeira_nao_confirmada_nao_gera_sucesso()
    {
        var state = new WindowsFeaturesFixture();
        state.Disk.DoNotActuallyEmpty = true;
        var snapshot = await state.Cleanup.ReadAsync();
        var result = await state.Cleanup.EmptyAsync(snapshot, snapshot.Categories[0], true, 0);
        Assert.False(result.Success);
        Assert.Contains("não pôde ser confirmada", result.Message);
    }

    [Fact]
    public async Task Recusa_no_dialogo_do_aplicativo_nao_chama_o_provedor()
    {
        var state = new WindowsFeaturesFixture();
        await state.CleanupPage.RefreshCommand.ExecuteAsync(null);
        state.CleanupPage.Categories[0].IsSelected = true;
        state.Confirmation.Accepted = false;
        await state.CleanupPage.EmptyCommand.ExecuteAsync(null);
        Assert.Single(state.Confirmation.Requests);
        Assert.Empty(state.Disk.Requested);
    }
}

