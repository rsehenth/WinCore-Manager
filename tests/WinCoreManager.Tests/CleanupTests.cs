using WinCoreManager.Models;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.Views;
using System.Windows;
using System.Windows.Controls;

namespace WinCoreManager.Tests;

public sealed class CleanupTests
{
    [Fact]
    public void Scan_counts_more_than_the_preview_and_does_not_count_overlapping_rules_twice()
    {
        using var lab = new TempCleanupLab();
        for (var i = 0; i < 275; i++)
            File.WriteAllBytes(Path.Combine(lab.Root, $"{i}.dmp"), new byte[8192]);

        var cleaner = new CleanupFiles([
            new(CleanupFiles.UserDumps, lab.Root, ".dmp", TimeSpan.Zero, false),
            new(CleanupFiles.UserReports, lab.Root, ".dmp", TimeSpan.Zero, false),
        ], previewLimit: 7);
        var rows = cleaner.Scan(CancellationToken.None).ToArray();
        Assert.Equal(275, rows.Sum(row => row.Items.GetValueOrDefault()));
        Assert.Equal(275 * 8192, rows.Sum(row => row.Bytes.GetValueOrDefault()));
        Assert.Equal(7, rows.Single(row => row.Selectable).FileScan!.Preview.Count);
        Assert.Null(rows.Single(row => row.Id == CleanupFiles.UserReports).Bytes);
    }

    private sealed class TempCleanupLab : IDisposable
    {
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("WinCoreCleanupTest-");
        public string Root => _directory.FullName;
        public void Dispose() => _directory.Delete(recursive: true);
    }

    [Fact]
    public Task Direct_cleanup_previews_and_only_deletes_authorized_unchanged_files() => WpfTestThread.Run(async () =>
    {
        var scratch = Directory.CreateTempSubdirectory("WinCoreCleanupTest-");
        try
        {
            var temporary = scratch.CreateSubdirectory("temporary");
            var dumps = scratch.CreateSubdirectory("dumps");
            var history = new FakeHistoryService();
            var state = new WindowsFeaturesFixture();
            var service = new CleanupService(state.Disk, state.Tools, history)
            {
                DirectFiles = new([
                    new(CleanupFiles.UserTemp, temporary.FullName, ".tmp", TimeSpan.FromDays(7), false),
                    new(CleanupFiles.UserDumps, dumps.FullName, ".dmp", TimeSpan.Zero, false),
                ]),
            };
            var page = new WinCoreManager.ViewModels.CleanupViewModel(service, state.Confirmation);
            var disposable = Put(temporary, "disposable.tmp");
            var changed = Put(temporary, "changed.tmp");
            var busy = Put(temporary, "busy.tmp");
            var untouched = Put(temporary, "personal.txt");
            var unselected = Put(dumps, "unselected.dmp");
            var recent = Path.Combine(temporary.FullName, "recent.tmp");
            File.WriteAllText(recent, "recent");

            await page.RefreshCommand.ExecuteAsync(null);
            var category = Assert.Single(page.Categories, row => row.Category.Id == CleanupFiles.UserTemp);
            Assert.Equal(3, category.Category.Items);
            Assert.True(category.Category.Bytes >= 8192 * 3);
            Assert.Equal(new[] { busy, changed, disposable }, category.PreviewPaths.Order(StringComparer.Ordinal));
            var view = new CleanupPage { DataContext = page };
            var size = new Size(840, 720);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.UpdateLayout();
            var categories = Assert.IsType<ItemsControl>(view.FindName("CleanupCategories"));
            var row = Assert.IsType<ContentPresenter>(categories.ItemContainerGenerator.ContainerFromItem(category));
            var preview = Assert.IsType<Expander>(row.ContentTemplate.FindName("DirectFilePreview", row));
            Assert.Equal(Visibility.Visible, preview.Visibility);
            Assert.False(preview.IsExpanded);
            preview.IsExpanded = true;
            view.UpdateLayout();
            var targets = Assert.Single(Assert.IsType<StackPanel>(preview.Content).Children.OfType<ListBox>());
            Assert.Equal(3, targets.Items.Count);
            Assert.Contains(disposable, targets.Items.Cast<string>());
            var checkbox = Assert.IsType<CheckBox>(row.ContentTemplate.FindName("CategorySelection", row));
            checkbox.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, true);
            Assert.True(category.IsSelected);
            var clean = Assert.IsType<Button>(view.FindName("CleanFilesButton"));
            Assert.True(clean.IsEnabled);
            state.Confirmation.Accepted = false;
            clean.Command.Execute(null);
            await page.EmptyCommand.ExecutionTask!;
            Assert.True(File.Exists(disposable));
            Assert.Empty(history.Records);

            state.Confirmation.Accepted = true;
            await File.AppendAllTextAsync(changed, "changed after scan");
            using (File.OpenRead(busy))
            {
                clean.Command.Execute(null);
                await page.EmptyCommand.ExecutionTask!;
            }
            Assert.False(File.Exists(disposable));
            Assert.All(new[] { changed, busy, untouched, unselected, recent }, path => Assert.True(File.Exists(path), path));
            Assert.Contains("1", page.Message);
            Assert.Contains("2", page.Message);
            Assert.Contains("8 KB", page.Message);
            Assert.Equal(HistoryStatus.Failure, Assert.Single(history.Records).Status);
            Assert.Empty(state.Tools.Calls);
        }
        finally { scratch.Delete(recursive: true); }

        static string Put(DirectoryInfo folder, string name)
        {
            var path = Path.Combine(folder.FullName, name);
            File.WriteAllBytes(path, new byte[8192]);
            File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            return path;
        }
    });

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
        Assert.Empty(state.CleanupPage.DirectCategories);
        Assert.Single(state.CleanupPage.NativeCategories);
        Assert.Contains("indisponível", state.CleanupPage.EstimateText);
        Assert.All(state.CleanupPage.Categories, row => Assert.False(row.IsSelected));
        Assert.False(state.CleanupPage.EmptyCommand.CanExecute(null));
        state.CleanupPage.Categories[0].IsSelected = true;
        Assert.Equal("2 MB", state.CleanupPage.Recycle!.Size);
        Assert.True(state.CleanupPage.EmptyNativeCommand.CanExecute(null));
        Assert.False(state.CleanupPage.EmptyCommand.CanExecute(null));
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
        await state.CleanupPage.EmptyNativeCommand.ExecuteAsync(null);
        Assert.Single(state.Confirmation.Requests);
        Assert.Empty(state.Disk.Requested);
    }
}

