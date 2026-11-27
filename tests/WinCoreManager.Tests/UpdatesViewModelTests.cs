using System.Windows;
using System.Windows.Controls;
using WinCoreManager.Models;
using WinCoreManager.Services;
using WinCoreManager.ViewModels;
using WinCoreManager.Views;

namespace WinCoreManager.Tests;

public class UpdatesViewModelTests
{
    [Fact]
    public Task Pagina_de_atualizacoes_renderiza_dados_simulados_em_dois_tamanhos() =>
        WpfTestThread.Run(async () =>
        {
            var viewModel = new UpdatesViewModel(new FakeWinGet(), new HistoryService(new TestSettings()));
            await viewModel.RefreshCommand.ExecuteAsync(null);
            var page = new UpdatesPage { DataContext = viewModel };
            var grid = Assert.IsType<DataGrid>(page.FindName("PackagesGrid"));
            foreach (var size in new[] { new Size(920, 720), new Size(600, 400) })
            {
                page.Measure(size);
                page.Arrange(new Rect(size));
                page.UpdateLayout();
                Assert.Equal(2, grid.Items.Count);
                Assert.True(grid.ActualHeight > 0);
            }
        });

    [Fact]
    public async Task Nova_consulta_tenta_detectar_winget_novamente_apos_falha()
    {
        var winget = new FakeWinGet { Available = false };
        var viewModel = new UpdatesViewModel(winget, new HistoryService(new TestSettings()));

        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.True(viewModel.ScanFailed);

        winget.Available = true;
        await viewModel.RefreshCommand.ExecuteAsync(null);

        Assert.False(viewModel.ScanFailed);
        Assert.Equal(2, winget.Detections);
        Assert.Equal(2, viewModel.Rows.Count);
    }

    [Fact]
    public Task Selecionar_todos_respeita_o_filtro_do_grid() => WpfTestThread.Run(async () =>
    {
        var viewModel = new UpdatesViewModel(new FakeWinGet(), new FakeHistoryService());
        viewModel.AttachFilter(System.Windows.Data.CollectionViewSource.GetDefaultView(viewModel.Rows));
        await viewModel.RefreshCommand.ExecuteAsync(null);

        viewModel.AllSelected = true;
        viewModel.SearchText = "Dois";
        viewModel.AllSelected = false;
        viewModel.AllSelected = true;

        Assert.Equal(1, viewModel.SelectedCount);
        Assert.True(viewModel.AllSelected);
        Assert.Equal("Dois", Assert.Single(viewModel.Rows, row => row.IsSelected).Name);
    });

    [Fact]
    public async Task Cancelar_lote_aguarda_item_atual_e_preserva_o_que_nao_foi_iniciado()
    {
        var winget = new FakeWinGet();
        var history = new HistoryService(new TestSettings());
        var viewModel = new UpdatesViewModel(winget, history);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        viewModel.AllSelected = true;
        winget.Execute = token =>
        {
            viewModel.CancelUpdateCommand.Execute(null);
            Assert.True(token.IsCancellationRequested);
            return new ProcessRunResult { ExitCode = 0 };
        };

        await viewModel.UpdateSelectedCommand.ExecuteAsync(null);

        Assert.Equal(UpdateRowStatus.Success, viewModel.Rows[0].Status);
        Assert.Equal(UpdateRowStatus.Idle, viewModel.Rows[1].Status);
        Assert.False(viewModel.Rows[0].IsSelected);
        Assert.True(viewModel.Rows[1].IsSelected);
        Assert.True(viewModel.AllSelected);
        Assert.Equal(1, viewModel.RemainingUpdates);
        Assert.Contains("1 não iniciado", viewModel.StatusText);
        Assert.Equal(HistoryStatus.Success, Assert.Single(await history.RecentAsync(5)).Status);
    }

    private sealed class FakeWinGet : IWinGetService
    {
        public bool Available { get; set; } = true;
        public int Detections { get; private set; }
        public string? ExecutablePath => null;
        public WinGetAvailability? LastDetection { get; private set; }
        public Func<CancellationToken, ProcessRunResult>? Execute { get; set; }

        public Task<WinGetAvailability> DetectAsync(CancellationToken cancellationToken = default)
        {
            Detections++;
            LastDetection = new WinGetAvailability { IsInstalled = Available };
            return Task.FromResult(LastDetection);
        }

        public Task<WinGetScanResult> ListUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new WinGetScanResult
            {
                Packages =
                [
                    new WinGetPackage { Name = "Um", Id = "Test.One", AvailableVersion = "2.0" },
                    new WinGetPackage { Name = "Dois", Id = "Test.Two", AvailableVersion = "2.0" },
                ],
            });

        public Task<ProcessRunResult> UpgradePackageAsync(
            WinGetPackage package, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
            Task.FromResult(Execute!(cancellationToken));
    }
}
