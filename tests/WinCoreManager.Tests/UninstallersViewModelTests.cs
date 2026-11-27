using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

/// <summary>Inventário falso: devolve a lista configurada (nenhum acesso ao Registro real).</summary>
public sealed class FakeProgramInventory(params InstalledProgram[] programs) : IProgramInventoryService
{
    public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstalledProgram>>(programs);
}

public class UninstallersViewModelTests
{
    private static InstalledProgram Usuario(string name) => UninstallerSelectionTests.SyntheticMsi(name);

    private static InstalledProgram Tecnico(string name, bool msi = false) => ClassifierProgramFactory.Create(
        name: name,
        publisher: "Microsoft Corporation",
        isSystemComponent: true,
        kind: msi ? ProgramKind.Msi : ProgramKind.Executable);

    private static UninstallersViewModel CreateViewModel(params InstalledProgram[] programs)
    {
        return new UninstallersViewModel(
            new FakeProgramInventory(programs),
            new UninstallPlanner(),
            new UninstallBatchRunner(new UninstallPlanner(), new FakeUninstallExecutor(), new FakeHistoryService()),
            new FakeResidueAnalysisService());
    }

    private static async Task<UninstallersViewModel> CreateScannedAsync(params InstalledProgram[] programs)
    {
        var vm = CreateViewModel(programs);
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task ListaPadraoMostraAplicativosEContaComponentesTecnicosOcultos()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Usuario("Microsoft OneDrive"),
            Tecnico("Microsoft Visual C++ 2015-2022 Redistributable (x64)"),
            Tecnico("Componente Interno do Windows"),
            Tecnico("Atualização (KB5034123)"));

        // Inventário completo permanece disponível nas linhas (nada é descartado).
        Assert.Equal(5, vm.Rows.Count);

        // A lista apresentada no padrão contém somente aplicativos.
        Assert.All(vm.VisibleRows, row => Assert.False(row.IsTechnical));
        Assert.Equal(2, vm.VisibleRows.ToList().Count);
        Assert.Equal("2 aplicativo(s) · 3 componente(s) técnico(s) oculto(s)", vm.SummaryText);
    }

    [Fact]
    public async Task FiltroTecnicoExibeComponentesEOcultaAplicativos()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Tecnico("Componente Interno do Windows"));

        vm.SelectedFilterIndex = 4;

        Assert.All(vm.VisibleRows, row => Assert.True(row.IsTechnical));
        Assert.Single(vm.VisibleRows);
        Assert.Equal("1 componente(s) técnico(s) exibido(s) · 1 aplicativo(s) oculto(s)", vm.SummaryText);

        // A visibilidade não muda a classificação nem a política da linha.
        var row = vm.VisibleRows.Single();
        Assert.Equal(RemovalPolicy.Blocked, row.Classification.Policy);
        Assert.False(row.CanSelect);
    }

    [Fact]
    public async Task BuscaRespeitaOFiltroEscolhido()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Usuario("Navegador Exemplo"),
            ClassifierProgramFactory.Create(name: "Visual Studio Build Tools", publisher: "Microsoft Corporation"));

        vm.SelectedFilterIndex = 2; // Ferramentas de desenvolvimento
        vm.SearchText = "Visual";

        Assert.Single(vm.VisibleRows);
        Assert.Equal("Visual Studio Build Tools", vm.VisibleRows.Single().Name);

        // A busca nunca revela itens fora do filtro atual.
        vm.SearchText = "Editor";
        Assert.Empty(vm.VisibleRows);
    }

    [Fact]
    public async Task SelecionarTodosAtingeApenasVisiveisEElegiveis()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Usuario("Navegador Exemplo"),
            Tecnico("Microsoft Visual C++ 2015-2022 Redistributable (x64)"),
            Tecnico("Componente Interno do Windows"));

        vm.AllSelected = true;

        Assert.Equal(2, vm.SelectedCount);
        Assert.All(vm.VisibleRows, row => Assert.True(row.IsSelected));
        Assert.All(vm.VisibleRows.Where(r => r.IsTechnical), row => Assert.False(row.IsSelected));
    }

    [Fact]
    public async Task BuscaLimpaSelecaoQueFicouOcultaEMantemAQueContinuaVisivel()
    {
        var vm = await CreateScannedAsync(Usuario("Editor de Texto"), Usuario("Navegador Exemplo"));
        vm.AllSelected = true;
        var editor = vm.Catalog.Single(row => row.Name == "Editor de Texto");
        vm.SelectedRow = editor;

        vm.SearchText = "Editor";
        Assert.Same(editor, vm.SelectedRow);
        Assert.Equal(1, vm.SelectedCount);

        vm.SearchText = string.Empty;
        Assert.Equal(1, vm.SelectedCount);
        Assert.False(vm.AllSelected);
        Assert.True(editor.IsSelected);
    }

    [Fact]
    public async Task TrocaDeFiltroNaoPreservaSelecaoDeItensOcultos()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Tecnico("Componente Interno do Windows"));

        vm.AllSelected = true;
        Assert.Equal(1, vm.SelectedCount);

        vm.SelectedFilterIndex = 4; // vão para a visão técnica

        Assert.Equal(0, vm.SelectedCount);
        Assert.All(vm.Rows, row => Assert.False(row.IsSelected));
    }

    [Fact]
    public async Task TodosOsAplicativosIncluiNaoClassificadosMasNaoTecnicos()
    {
        var naoClassificado = ClassifierProgramFactory.Create(
            name: "Aplicativo sem metadados",
            publisher: string.Empty,
            displayVersion: string.Empty,
            installLocation: string.Empty,
            uninstallString: string.Empty);
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            naoClassificado,
            Tecnico("Componente Interno do Windows"));

        vm.SelectedFilterIndex = 3;

        Assert.Equal(2, vm.VisibleRows.ToList().Count);
        Assert.Contains(vm.VisibleRows, r => r.Classification.Category == ProgramCategory.Unknown);
    }

    [Fact]
    public async Task CatalogoExibidoNuncaContemItensTecnicosPorPadrao()
    {
        // Regressão do defeito real: a grade precisa consumir o catálogo, não o inventário.
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Usuario("Navegador Exemplo"),
            Tecnico("WinRT Intellisense UAP - en-us"),
            Tecnico("Windows SDK Desktop Headers x64"),
            Tecnico("Componente Interno do Windows"));

        Assert.Equal(5, vm.Rows.Count);
        Assert.Equal(2, vm.Catalog.Count);
        Assert.All(vm.Catalog, row => Assert.False(row.IsTechnical));
        Assert.DoesNotContain(vm.Catalog, row => row.Name.Contains("WinRT"));
    }

    [Fact]
    public async Task StatusDaConsultaNaoAnunciaOTamanhoDoInventarioBruto()
    {
        // O defeito observado era anunciar "384 programa(s) encontrado(s)" — inventário bruto.
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Tecnico("WinRT Intellisense UAP - en-us"),
            Tecnico("Componente Interno do Windows"));

        Assert.Equal(3, vm.Rows.Count);
        Assert.StartsWith("1 aplicativo(s) na lista", vm.StatusText);
        Assert.Contains("inventário técnico com 3 entrada(s)", vm.StatusText);
        Assert.DoesNotContain("3 programa(s) instalado(s)", vm.StatusText);
    }

    [Fact]
    public async Task ContadorSegueOCatalogoNaoOInventario()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Usuario("Navegador Exemplo"),
            Tecnico("WinRT Intellisense UAP - en-us"));

        Assert.Equal("2 aplicativo(s) · 1 componente(s) técnico(s) oculto(s)", vm.SummaryText);
        Assert.Contains("3 entrada(s) no inventário técnico", vm.InventoryText);

        vm.SearchText = "Editor";
        Assert.Equal("1 aplicativo(s) · 1 componente(s) técnico(s) oculto(s)", vm.SummaryText);
        Assert.Equal("3 entrada(s) no inventário técnico · 1 componente(s)/dependência(s)", vm.InventoryText);
    }

    [Fact]
    public async Task AtualizarListaRecalculaCatalogoEStatus()
    {
        var vm = await CreateScannedAsync(
            Usuario("Editor de Texto"),
            Tecnico("Componente Interno do Windows"));

        Assert.Single(vm.Catalog);
        Assert.Equal("1 aplicativo(s) · 1 componente(s) técnico(s) oculto(s)", vm.SummaryText);

        await vm.RefreshCommand.ExecuteAsync(null);

        // A releitura não duplica linhas nem deixa o catálogo fora de sincronia.
        Assert.Equal(2, vm.Rows.Count);
        Assert.Single(vm.Catalog);
        Assert.Equal(0, vm.SelectedCount);
        Assert.StartsWith("1 aplicativo(s) na lista", vm.StatusText);
    }

    [Fact]
    public async Task GradeXamlUsaOCatalogoENaoOInventario()
    {
        // Regressão de binding: a grade não pode voltar a apontar para o inventário bruto.
        var xaml = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "WinCoreManager", "Views", "UninstallersPage.xaml"));

        Assert.Contains("ItemsSource=\"{Binding Catalog}\"", xaml);
        Assert.DoesNotContain("ItemsSource=\"{Binding Rows}\"", xaml);
    }

    [Fact]
    public async Task PaginaNaoReinstalaFiltroPorCollectionView()
    {
        var codeBehind = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "WinCoreManager", "Views", "UninstallersPage.xaml.cs"));

        // O filtro é responsabilidade do ViewModel: sem hookup imperativo no code-behind,
        // que deixava o estado da grade dependente da ordem de Loaded/DataContextChanged.
        Assert.DoesNotContain("AttachFilter", codeBehind);
        Assert.DoesNotContain("CollectionViewSource", codeBehind);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinCoreManager.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
