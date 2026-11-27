using WinCoreManager.Localization;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Rótulos de categoria e política para exibição (pt-BR).</summary>
public static class ProgramClassificationLabels
{
    public static string Category(ProgramCategory category) => category switch
    {
        ProgramCategory.UserApplication => LocalizationService.Source("Message.App"),
        ProgramCategory.BackgroundApplication => LocalizationService.Source("Message.Background"),
        ProgramCategory.DeveloperTool => LocalizationService.Source("Message.DevelopmentTool"),
        ProgramCategory.SharedDependency => LocalizationService.Source("Message.SharedDependency"),
        ProgramCategory.SystemComponent => LocalizationService.Source("Message.WindowsComponent"),
        ProgramCategory.Unknown => LocalizationService.Source("Message.Unclassified"),
        _ => "Desconhecido",
    };

    public static string Policy(RemovalPolicy policy) => policy switch
    {
        RemovalPolicy.AllowedWithConfirmation => LocalizationService.Source("Message.AllowedWithConfirmation"),
        RemovalPolicy.RequiresReview => LocalizationService.Source("Message.ReviewRequired"),
        RemovalPolicy.Blocked => "Bloqueado",
        _ => "Desconhecido",
    };
}

/// <summary>
/// Classificação central de entradas de desinstalação. Combina os metadados do Registro
/// (SystemComponent, NoRemove, ParentKeyName, ReleaseType, fabricante, nome, caminhos, tipo de
/// desinstalador, GUID MSI) com listas curadas de aplicativos conhecidos e padrões de componentes
/// internos do Windows, runtimes e SDKs.
///
/// Critérios investigados e conclusões:
/// - SystemComponent: evidência forte de componente oculto, mas alguns aplicativos legítimos se
///   ocultam de propósito (OneDrive, Edge) — por isso a allowlist sobrepõe a flag.
/// - NoRemove (IsProtected): o fabricante proibiu a remoção — política sempre Blocked.
/// - NoModify: só oculta "Alterar" no Painel de Controle; não é evidência de risco — não altera
///   categoria nem política (documentado; intencionalmente não capturado).
/// - ParentKeyName/ReleaseType/KB######: atualização ou hotfix — componente de sistema.
/// - MSI não implica componente de sistema: um produto MSI sem outros sinais é um aplicativo.
/// - Fabricante Microsoft não implica componente do Windows: sem sinal adicional, é aplicativo.
/// - Metadados insuficientes (nome técnico/GUID, sem qualquer contexto): Unknown.
/// </summary>
public static class ProgramClassifier
{
    private static readonly string[] BrowserNames =
    [
        "Google Chrome", "Chromium", "Mozilla Firefox", "Brave", "Vivaldi",
    ];

    private static readonly string[] GameLauncherNames =
    [
        "Steam", "Epic Games Launcher", "GOG GALAXY", "Battle.net", "EA app", "Ubisoft Connect", "Riot Client",
    ];

    private static readonly string[] ChatAppNames =
    [
        "Discord", "Slack", "WhatsApp", "Telegram", "Skype",
    ];

    private static readonly string[] CloudSyncNames =
    [
        "Microsoft OneDrive", "Dropbox", "Google Drive",
    ];

    private static readonly string[] DeveloperToolNames =
    [
        "Microsoft Visual Studio Code", "Visual Studio Code",
        "Visual Studio Community", "Visual Studio Professional", "Visual Studio Enterprise",
        "Microsoft Visual Studio Community", "Microsoft Visual Studio Professional",
        "Microsoft Visual Studio Enterprise", "Visual Studio Build Tools",
        "Microsoft Visual Studio Build Tools", "JetBrains ", "Android Studio",
    ];

    private static readonly string[] PopularAppNames =
    [
        "7-Zip", "WinRAR", "VLC media player", "Spotify", "OBS Studio", "Notepad++",
        "Paint.NET", "PowerToys", "iTunes", "Audacity", "GIMP", "Inkscape", "Blender",
    ];

    /// <summary>Classifica um programa e calcula a política de remoção correspondente.</summary>
    public static ProgramClassification Evaluate(InstalledProgram program)
    {
        if (OptionalWindowsApps.IsApprovedTarget(program))
            return new(ProgramCategory.UserApplication, RemovalPolicy.AllowedWithConfirmation, []);

        var reasons = new List<string>();
        var category = Classify(program, reasons);
        var policy = DerivePolicy(program, category, reasons);
        return new ProgramClassification(category, policy, reasons);
    }

    private static ProgramCategory Classify(InstalledProgram program, List<string> reasons)
    {
        if (program.IsUpdate)
        {
            reasons.Add(LocalizationService.Source("Message.UpdateOrHotfixEntryParentKeyNameReleaseTypeKB"));
            return ProgramCategory.SystemComponent;
        }

        switch (program.Kind)
        {
            case ProgramKind.WindowsFeature:
                reasons.Add(LocalizationService.Source("Message.WindowsFeatureManagedByTheOperatingSystem"));
                return ProgramCategory.SystemComponent;
            case ProgramKind.WindowsUpdate:
                reasons.Add(LocalizationService.Source("Message.WindowsUpdateManagedByWindowsUpdate"));
                return ProgramCategory.SystemComponent;
            case ProgramKind.SdbInst:
                reasons.Add(LocalizationService.Source("Message.WindowsApplicationCompatibilityDatabase"));
                return ProgramCategory.SystemComponent;
        }

        if (program.IsSystemComponent)
        {
            reasons.Add(LocalizationService.Source("Message.MarkedAsASystemComponentSystemComponentInThe"));
            return ProgramCategory.SystemComponent;
        }

        if (MatchesSharedDependency(program, reasons))
        {
            return ProgramCategory.SharedDependency;
        }

        if (MatchesWindowsInternal(program, reasons))
        {
            return ProgramCategory.SystemComponent;
        }

        if (TryClassifyKnownApp(program, reasons, out var known))
        {
            return known;
        }

        if (HasInsufficientMetadata(program))
        {
            reasons.Add(LocalizationService.Source("Message.InsufficientMetadataForAConclusiveClassification"));
            return ProgramCategory.Unknown;
        }

        reasons.Add(LocalizationService.Source("Message.AnOfficialUninstallerIsRegisteredForTheApp"));
        return ProgramCategory.UserApplication;
    }

    /// <summary>Reconhece aplicativos conhecidos após aplicar as regras de proteção.</summary>
    private static bool TryClassifyKnownApp(InstalledProgram program, List<string> reasons, out ProgramCategory category)
    {
        var name = program.Name.Trim();

        // Nome exato para tokens curos/genéricos ("Edge", "Git", "Zoom") que casariam substrings
        // de outros produtos ("WebView2", "Logitech", "ZoomText").
        if (name.Equals("Microsoft Edge", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Opera", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedBrowserCuratedListOfApps"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        if (MatchesAny(name, BrowserNames))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedBrowserCuratedListOfApps"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        if (name.Equals("Git", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Git ", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedDevelopmentTool"));
            category = ProgramCategory.DeveloperTool;
            return true;
        }

        if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedAppGamingOrStorefront"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        if (MatchesAny(name, GameLauncherNames))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedAppGamingOrStorefront"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        // O instalador por máquina do Teams é um componente auxiliar, não o aplicativo.
        if (name.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
        {
            category = ProgramCategory.UserApplication;
            reasons.Add(LocalizationService.Source("Message.RecognizedAppChatOrCollaboration"));
            return true;
        }

        if (MatchesAny(name, ChatAppNames) || name.Equals("Zoom", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Zoom ", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedAppChatOrCollaboration"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        if (MatchesAny(name, CloudSyncNames))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedSyncAppThatRunsInTheBackground"));
            category = ProgramCategory.BackgroundApplication;
            return true;
        }

        if (name.Contains("PowerToys", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedUtilityThatRunsInTheBackground"));
            category = ProgramCategory.BackgroundApplication;
            return true;
        }

        if (MatchesAny(name, DeveloperToolNames))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedDevelopmentTool"));
            category = ProgramCategory.DeveloperTool;
            return true;
        }

        if (MatchesAny(name, PopularAppNames))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedAppCuratedList"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        // Suíte Office: SKU principal é aplicativo; componentes auxiliares não entram aqui.
        if (name.StartsWith("Microsoft Office", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Proof", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("MUI", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Click-to-Run", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Component", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RecognizedAppOfficeSuite"));
            category = ProgramCategory.UserApplication;
            return true;
        }

        category = ProgramCategory.Unknown;
        return false;
    }

    private static bool MatchesSharedDependency(InstalledProgram program, List<string> reasons)
    {
        var name = program.Name;
        var publisher = program.Publisher;

        if (name.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.RedistributablePackageVisualCOrSimilarRequiredBy"));
            return true;
        }

        if (name.Contains("Universal CRT", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Universal C Runtime", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.UniversalCRuntimeRequiredByOtherApps"));
            return true;
        }

        if (name.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".NET SDK", StringComparison.OrdinalIgnoreCase)
            || name.Contains("ASP.NET Core", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows App SDK", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.ModernNETRuntimeOrSDKUsedAsA"));
            return true;
        }

        if (name.Contains("WebView2", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Microsoft Edge Update", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.SharedBrowserComponentWebView2OrEdgeUpdate"));
            return true;
        }

        if (name.Contains("Machine-Wide Installer", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.HelperComponentForAnotherAppSPerMachine"));
            return true;
        }

        if (name.Contains("WinRT Intellisense", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.InternalVisualStudioComponentIntelliSenseForWinRT"));
            return true;
        }

        if (name.Contains("Windows Software Development Kit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows SDK", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows App Certification Kit", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows Assessment and Deployment Kit", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.WindowsSDKComponentOrPackage"));
            return true;
        }

        if (publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            && name.Contains("Runtime", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.MicrosoftRuntimeUsedAsASharedDependency"));
            return true;
        }

        if (name.Contains("Visual Studio", StringComparison.OrdinalIgnoreCase)
            && !name.Contains("Build Tools", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("Installer", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Shell", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Tools for Office", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Tools for Applications", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add(LocalizationService.Source("Message.VisualStudioHelperComponent"));
            return true;
        }

        if (publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            && (name.Contains("Setup", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Installer", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Bootstrapper", StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add(LocalizationService.Source("Message.HelperComponentForAnotherProductSInstaller"));
            return true;
        }

        if (IsDeviceDriver(program))
        {
            reasons.Add(LocalizationService.Source("Message.DeviceDriverSharedByHardware"));
            return true;
        }

        return false;
    }

    private static bool MatchesWindowsInternal(InstalledProgram program, List<string> reasons)
    {
        var name = program.Name;
        var publisher = program.Publisher;

        if (!publisher.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.StartsWith("Security Update", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Hotfix", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Update for Windows", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.WindowsSecurityUpdateOrFix"));
            return true;
        }

        if (name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Microsoft Antimalware", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.BuiltInWindowsAntimalware"));
            return true;
        }

        if (name.Equals("Internet Explorer", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows Internal", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.BuiltInWindowsComponent"));
            return true;
        }

        if (name.Contains(".NET Framework", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(LocalizationService.Source("Message.NETFrameworkRuntimeBuiltIntoWindows"));
            return true;
        }

        return false;
    }

    private static bool IsDeviceDriver(InstalledProgram program)
    {
        if (!program.Name.Contains("Driver", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var vendor in (string[])[
                     "Intel", "Realtek", "NVIDIA", "AMD", "Advanced Micro Devices",
                     "Broadcom", "Qualcomm", "Conexant", "Synaptics", "ELAN",
                 ])
        {
            if (program.Publisher.Contains(vendor, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInsufficientMetadata(InstalledProgram program)
    {
        var name = program.Name.Trim();

        // Nome técnico (GUID/chave bruta): nada conclusivo mesmo com desinstalador.
        if (name.Length == 0 || (name.StartsWith('{') && name.EndsWith('}')))
        {
            return true;
        }

        // Nome comum sem fabricante, versão, local e data: inconclusivo só quando também
        // não há desinstalador oficial — sua presença já evidencia um aplicativo real.
        return !program.HasUninstallString
               && program.Publisher.Length == 0
               && program.DisplayVersion.Length == 0
               && program.InstallLocation.Length == 0
               && program.InstallDate is null;
    }

    private static bool MatchesAny(string name, string[] patterns) => patterns.Any(pattern =>
        name.Contains(pattern, StringComparison.OrdinalIgnoreCase));

    /// <summary>Política de remoção: derivada dos metadados de proteção e da categoria.</summary>
    private static RemovalPolicy DerivePolicy(InstalledProgram program, ProgramCategory category, List<string> reasons)
    {
        if (program.IsProtected)
        {
            reasons.Add(LocalizationService.Source("Message.ThePublisherMarkedTheEntryNoRemove"));
            return RemovalPolicy.Blocked;
        }

        var name = program.Name.Trim();
        if (name.Length == 0 || Guid.TryParseExact(name, "B", out _))
        {
            reasons.Add(LocalizationService.Source("Message.InsufficientMetadataForAConclusiveClassification"));
            return RemovalPolicy.Blocked;
        }

        switch (category)
        {
            case ProgramCategory.SystemComponent:
                reasons.Add(LocalizationService.Source("Message.InternalWindowsComponentRemovalIsBlockedInThis"));
                return RemovalPolicy.Blocked;
            case ProgramCategory.SharedDependency:
                reasons.Add(LocalizationService.Source("Message.SharedDependencyReviewItAndUninstallIndividuallyBatch"));
                return RemovalPolicy.RequiresReview;
            case ProgramCategory.Unknown:
                reasons.Add(LocalizationService.Source("Message.ClassificationIsUncertainReviewTheItemManuallyBefore"));
                return RemovalPolicy.RequiresReview;
            default:
                return RemovalPolicy.AllowedWithConfirmation;
        }
    }
}
