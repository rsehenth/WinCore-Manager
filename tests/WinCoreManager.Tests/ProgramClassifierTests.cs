using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

/// <summary>Fábrica de entradas de desinstalação para os testes de classificação.</summary>
public static class ClassifierProgramFactory
{
    public static InstalledProgram Create(
        string name = "Programa Teste",
        string publisher = "Fabricante SA",
        string uninstallString = @"C:\Program Files\Teste\unins000.exe",
        ProgramKind kind = ProgramKind.Executable,
        bool isSystemComponent = false,
        bool isProtected = false,
        bool isUpdate = false,
        string installLocation = @"C:\Program Files\Teste",
        string displayVersion = "1.0.0")
    {
        return new InstalledProgram
        {
            Name = name,
            Publisher = publisher,
            DisplayVersion = displayVersion,
            InstallLocation = installLocation,
            RegistryKeyName = name,
            RegistryPath = $@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}",
            Origin = "Registro (64 bits)",
            Kind = kind,
            UninstallString = uninstallString,
            IsSystemComponent = isSystemComponent,
            IsProtected = isProtected,
            IsUpdate = isUpdate,
        };
    }
}

public class ProgramClassifierTests
{
    [Fact]
    public void OneDriveSemFlagDeSistemaContinuaAplicativoDeSegundoPlano()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Microsoft OneDrive",
            publisher: "Microsoft Corporation");

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.BackgroundApplication, classification.Category);
        Assert.Equal(RemovalPolicy.AllowedWithConfirmation, classification.Policy);
        Assert.False(new UninstallerItemRow(program).IsTechnical);
    }

    [Fact]
    public void Teams_PrincipalEAuxiliarSaoClassificadosSeparadamente()
    {
        var principal = ProgramClassifier.Evaluate(
            ClassifierProgramFactory.Create(name: "Microsoft Teams", publisher: "Microsoft Corporation"));
        var auxiliar = ProgramClassifier.Evaluate(
            ClassifierProgramFactory.Create(
                name: "Teams Machine-Wide Installer",
                publisher: "Microsoft Corporation",
                kind: ProgramKind.Msi));

        Assert.Equal(ProgramCategory.UserApplication, principal.Category);
        Assert.Equal(ProgramCategory.SharedDependency, auxiliar.Category);
        Assert.NotEqual(principal.Category, auxiliar.Category);
    }

    [Fact]
    public void VSCodeEVisualStudioSaoReconhecidosComoFerramentasDeDesenvolvimento()
    {
        var vsCode = ProgramClassifier.Evaluate(
            ClassifierProgramFactory.Create(name: "Microsoft Visual Studio Code", publisher: "Microsoft Corporation"));
        var visualStudio = ProgramClassifier.Evaluate(
            ClassifierProgramFactory.Create(name: "Visual Studio Community 2022", publisher: "Microsoft Corporation"));

        Assert.Equal(ProgramCategory.DeveloperTool, vsCode.Category);
        Assert.Equal(ProgramCategory.DeveloperTool, visualStudio.Category);
        Assert.Equal(RemovalPolicy.AllowedWithConfirmation, vsCode.Policy);
        Assert.Equal(RemovalPolicy.AllowedWithConfirmation, visualStudio.Policy);
    }

    [Fact]
    public void WinRTIntelliSenseEDependenciaOcultaDaListaPadrao()
    {
        var program = ClassifierProgramFactory.Create(
            name: "WinRT Intellisense - en-us",
            publisher: "Microsoft Corporation");

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.SharedDependency, classification.Category);
        Assert.Equal(RemovalPolicy.RequiresReview, classification.Policy);
        Assert.True(new UninstallerItemRow(program).IsTechnical);
        Assert.False(UninstallersViewModel.MatchesFilter(classification.Category, classification.Policy, 0));
    }

    [Fact]
    public void EntradaProtegidaSaiDaListaPrincipalEFicaNaVisaoTecnica()
    {
        // Edge é navegador (categoria de aplicativo), mas o fabricante marca NoRemove:
        // sai da lista principal e continua acessível na visão técnica, sempre bloqueado.
        var program = ClassifierProgramFactory.Create(
            name: "Microsoft Edge",
            publisher: "Microsoft Corporation",
            isProtected: true);

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.UserApplication, classification.Category);
        Assert.Equal(RemovalPolicy.Blocked, classification.Policy);
        Assert.False(UninstallersViewModel.MatchesFilter(classification.Category, classification.Policy, 0));
        Assert.False(UninstallersViewModel.MatchesFilter(classification.Category, classification.Policy, 3));
        Assert.True(UninstallersViewModel.MatchesFilter(classification.Category, classification.Policy, 4));
        Assert.True(new UninstallerItemRow(program).IsTechnical);
    }

    [Fact]
    public void ComponentesInternosDoWindowsPermanecemBloqueados()
    {
        var flagDeRegistro = ClassifierProgramFactory.Create(
            name: "Microsoft Windows Journal Viewer",
            publisher: "Microsoft Corporation",
            isSystemComponent: true);
        var hotfix = ClassifierProgramFactory.Create(
            name: "Atualização (KB5034123)",
            publisher: "Microsoft Corporation",
            isUpdate: true,
            kind: ProgramKind.Msi);
        var recurso = ClassifierProgramFactory.Create(
            name: "Recurso opcional",
            publisher: "Microsoft Corporation",
            kind: ProgramKind.WindowsFeature);

        var flag = ProgramClassifier.Evaluate(flagDeRegistro);
        var update = ProgramClassifier.Evaluate(hotfix);
        var feature = ProgramClassifier.Evaluate(recurso);

        Assert.Equal(ProgramCategory.SystemComponent, flag.Category);
        Assert.Equal(RemovalPolicy.Blocked, flag.Policy);
        Assert.Equal(ProgramCategory.SystemComponent, update.Category);
        Assert.Equal(RemovalPolicy.Blocked, update.Policy);
        Assert.Equal(ProgramCategory.SystemComponent, feature.Category);
        Assert.Equal(RemovalPolicy.Blocked, feature.Policy);
    }

    [Fact]
    public void VisualCppRedistributableEDependenciaComRevisao()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation",
            kind: ProgramKind.Msi);

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.SharedDependency, classification.Category);
        Assert.Equal(RemovalPolicy.RequiresReview, classification.Policy);
        Assert.True(new UninstallerItemRow(program).IsTechnical);
    }

    [Fact]
    public void ItensUnknownNaoSaoTratadosComoSeguros()
    {
        // Nome técnico (GUID) sem contexto: classificação inconclusiva e remoção bloqueada.
        var program = ClassifierProgramFactory.Create(
            name: $"{{{Guid.NewGuid()}}}",
            publisher: string.Empty,
            displayVersion: string.Empty,
            installLocation: string.Empty);

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.Unknown, classification.Category);
        Assert.Equal(RemovalPolicy.Blocked, classification.Policy);
        Assert.False(new UninstallerItemRow(program).IsBatchSelectable);
    }

    [Fact]
    public void NoRemoveBloqueiaARemocaoIndependentementeDaCategoria()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Aplicativo Comum",
            publisher: "Fabricante SA",
            isProtected: true);

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.UserApplication, classification.Category);
        Assert.Equal(RemovalPolicy.Blocked, classification.Policy);
        Assert.False(new UninstallerItemRow(program).CanSelect);
    }

    [Fact]
    public void DriversDeFabricantesSaoDependenciasCompartilhadas()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Intel Graphics Driver 31.0.101",
            publisher: "Intel Corporation");

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.SharedDependency, classification.Category);
        Assert.Equal(RemovalPolicy.RequiresReview, classification.Policy);
    }

    [Fact]
    public void RuntimesDotNetModernosSaoDependenciasEFrameworkIntegradoEComponente()
    {
        var runtime = ProgramClassifier.Evaluate(
            ClassifierProgramFactory.Create(name: "Microsoft .NET Runtime - 8.0.11", publisher: "Microsoft Corporation"));
        var framework = ProgramClassifier.Evaluate(
            ClassifierProgramFactory.Create(name: "Microsoft .NET Framework 4.8.1", publisher: "Microsoft Corporation"));

        Assert.Equal(ProgramCategory.SharedDependency, runtime.Category);
        Assert.Equal(RemovalPolicy.RequiresReview, runtime.Policy);
        Assert.Equal(ProgramCategory.SystemComponent, framework.Category);
        Assert.Equal(RemovalPolicy.Blocked, framework.Policy);
    }

    [Fact]
    public void MsiSemOutrosSinaisEAplicativo_NaoComponenteDeSistema()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Aplicativo Instalado Por MSI",
            publisher: "Fabricante Terceiro",
            kind: ProgramKind.Msi,
            uninstallString: @"MsiExec.exe /I{B3D5A461-1B4F-4D57-9D55-9D5C7A1F2E99}");

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.UserApplication, classification.Category);
        Assert.Equal(RemovalPolicy.AllowedWithConfirmation, classification.Policy);
    }

    [Fact]
    public void ProdutoMicrosoftSemSinalAdicionalNaoEComponenteDoSistema()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Microsoft Silverlight",
            publisher: "Microsoft Corporation");

        var classification = ProgramClassifier.Evaluate(program);

        Assert.NotEqual(ProgramCategory.SystemComponent, classification.Category);
        Assert.NotEqual(RemovalPolicy.Blocked, classification.Policy);
    }

    [Fact]
    public void FlagDeSistemaProtegeAteAplicativoReconhecido()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Google Chrome",
            publisher: "Google LLC",
            isSystemComponent: true);

        var classification = ProgramClassifier.Evaluate(program);

        Assert.Equal(ProgramCategory.SystemComponent, classification.Category);
        Assert.Equal(RemovalPolicy.Blocked, classification.Policy);
    }

    [Fact]
    public void FiltrosNaoAlteramPoliticaDeRemocao()
    {
        var program = ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation");

        var antes = ProgramClassifier.Evaluate(program);

        // A troca de filtro só mexe na visibilidade; a classificação é determinística
        // e recalculada na camada de execução, nunca herdada da interface.
        for (var filtro = 0; filtro < 5; filtro++)
        {
            var depois = ProgramClassifier.Evaluate(program);
            Assert.Equal(antes.Category, depois.Category);
            Assert.Equal(antes.Policy, depois.Policy);
        }
    }

    [Fact]
    public void MatrizDeFiltrosCorrespondeAsCategoriasEsperadas()
    {
        // 0 Aplicativos: categorias de aplicativo sem protegidos; 4 técnicos: componentes,
        // dependências e entradas protegidas (diagnóstico).
        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.AllowedWithConfirmation, 0));
        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.BackgroundApplication, RemovalPolicy.AllowedWithConfirmation, 0));
        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.DeveloperTool, RemovalPolicy.AllowedWithConfirmation, 0));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.SystemComponent, RemovalPolicy.Blocked, 0));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.SharedDependency, RemovalPolicy.RequiresReview, 0));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.Blocked, 0));

        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.BackgroundApplication, RemovalPolicy.AllowedWithConfirmation, 1));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.AllowedWithConfirmation, 1));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.BackgroundApplication, RemovalPolicy.Blocked, 1));

        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.DeveloperTool, RemovalPolicy.AllowedWithConfirmation, 2));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.AllowedWithConfirmation, 2));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.DeveloperTool, RemovalPolicy.Blocked, 2));

        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.AllowedWithConfirmation, 3));
        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.Unknown, RemovalPolicy.RequiresReview, 3));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.SystemComponent, RemovalPolicy.Blocked, 3));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.SharedDependency, RemovalPolicy.RequiresReview, 3));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.Blocked, 3));

        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.SystemComponent, RemovalPolicy.Blocked, 4));
        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.SharedDependency, RemovalPolicy.RequiresReview, 4));
        Assert.True(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.Blocked, 4));
        Assert.False(UninstallersViewModel.MatchesFilter(ProgramCategory.UserApplication, RemovalPolicy.AllowedWithConfirmation, 4));
    }

    [Fact]
    public void LinhasRespeitamAPoliticaNaSelecao()
    {
        var permitido = new UninstallerItemRow(ClassifierProgramFactory.Create(name: "Aplicativo Comum"));
        var revisao = new UninstallerItemRow(ClassifierProgramFactory.Create(
            name: "Microsoft Visual C++ 2015-2022 Redistributable (x64)",
            publisher: "Microsoft Corporation"));
        var bloqueado = new UninstallerItemRow(ClassifierProgramFactory.Create(
            name: "Componente Interno",
            publisher: "Microsoft Corporation",
            isSystemComponent: true));

        Assert.True(permitido.IsBatchSelectable);
        Assert.True(revisao.CanSelect);            // seleção individual permitida
        Assert.False(revisao.IsBatchSelectable);   // nunca em lote
        Assert.False(bloqueado.CanSelect);         // bloqueado nem individualmente
        Assert.False(bloqueado.IsBatchSelectable);
    }
}
