/*
 * Modelos do desinstalador. ProgramKind reproduz de forma simplificada a taxonomia
 * UninstallerType de Bulk Crap Uninstaller
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Alterações: taxonomia renomeada para ProgramKind e simplificada.
 */

using WinCoreManager.Localization;

namespace WinCoreManager.Models;

/// <summary>Tipo do instalador/desinstalador detectado para um programa (portado da taxonomia do BCUninstaller).</summary>
public enum ProgramKind
{
    Unknown = 0,
    Executable = 1,
    Msi = 2,
    InnoSetup = 3,
    Nsis = 4,
    InstallShield = 5,
    SdbInst = 6,
    PowerShell = 7,
    Steam = 8,
    StoreApp = 9,
    WindowsFeature = 10,
    WindowsUpdate = 11,
}

/// <summary>Categoria de um programa instalado (classificação de apresentação; não autoriza remoção por si só).</summary>
public enum ProgramCategory
{
    UserApplication = 0,
    BackgroundApplication = 1,
    DeveloperTool = 2,
    SharedDependency = 3,
    SystemComponent = 4,
    Unknown = 5,
}

/// <summary>Política de remoção, calculada independentemente da categoria (visibilidade ≠ autorização).</summary>
public enum RemovalPolicy
{
    AllowedWithConfirmation = 0,
    RequiresReview = 1,
    Blocked = 2,
}

/// <summary>Resultado da classificação: categoria, política e motivos legíveis (pt-BR).</summary>
public sealed record ProgramClassification(
    ProgramCategory Category,
    RemovalPolicy Policy,
    IReadOnlyList<string> Reasons);

/// <summary>Programa instalado, preenchido a partir das chaves de desinstalação do Registro.</summary>
public sealed class InstalledProgram
{
    public required string Name { get; init; }
    public string DisplayVersion { get; init; } = string.Empty;
    public string Publisher { get; init; } = string.Empty;
    /// <summary>Descrição da origem do registro (ex.: "Registro (64 bits)").</summary>
    public string Origin { get; init; } = string.Empty;
    /// <summary>Data de instalação informada pelo instalador; null quando não confiável/ausente.</summary>
    public DateTime? InstallDate { get; init; }
    public long? EstimatedSizeKb { get; init; }
    public string InstallLocation { get; init; } = string.Empty;
    public string InstallSource { get; init; } = string.Empty;
    /// <summary>True quando a entrada veio da visão de 64 bits do Registro.</summary>
    public bool Is64Bit { get; init; }
    public bool IsSystemComponent { get; init; }
    /// <summary>Entrada marcada com NoRemove pelo fabricante.</summary>
    public bool IsProtected { get; init; }
    /// <summary>Entrada de atualização/hotfix (ParentKeyName, ReleaseType ou padrão KB######).</summary>
    public bool IsUpdate { get; init; }
    public string RegistryPath { get; init; } = string.Empty;
    public string RegistryKeyName { get; init; } = string.Empty;
    public string UninstallString { get; init; } = string.Empty;
    public string QuietUninstallString { get; init; } = string.Empty;
    public Guid BundleProviderKey { get; init; }
    public Guid MsiProductCode { get; init; }
    public ProgramKind Kind { get; init; }

    public bool HasUninstallString => UninstallString.Length > 0;
    public bool HasQuietUninstall => QuietUninstallString.Length > 0;
    public bool IsMsi => Kind == ProgramKind.Msi;

    public string KindLabel => Kind switch
    {
        ProgramKind.Executable => LocalizationService.Source("Message.Executable"),
        ProgramKind.Msi => "MSI (Windows Installer)",
        ProgramKind.InnoSetup => "Inno Setup",
        ProgramKind.Nsis => "NSIS",
        ProgramKind.InstallShield => "InstallShield",
        ProgramKind.SdbInst => "SdbInst",
        ProgramKind.PowerShell => "PowerShell",
        ProgramKind.Steam => "Steam",
        ProgramKind.StoreApp => "Microsoft Store",
        ProgramKind.WindowsFeature => LocalizationService.Source("Message.WindowsFeature"),
        ProgramKind.WindowsUpdate => LocalizationService.Source("Message.WindowsUpdate"),
        _ => LocalizationService.Source("Message.Unknown3"),
    };
}

public enum UninstallMethod
{
    /// <summary>Desinstalador executável dividido em exe + argumentos.</summary>
    Executable = 0,
    /// <summary>Desinstalação via msiexec.exe usando o GUID do produto.</summary>
    Msi = 1,
    /// <summary>Nenhum caminho seguro nesta versão — apenas listar.</summary>
    NotSupported = 2,
}

/// <summary>Plano de desinstalação de um programa: como executar e quais avisos apresentar.</summary>
public sealed class UninstallPlan
{
    public required InstalledProgram Program { get; init; }
    public UninstallMethod Method { get; init; }
    /// <summary>Executável a iniciar (caminho validado quando absoluto). Nunca interpretado por shell.</summary>
    public string ExecutablePath { get; init; } = string.Empty;
    /// <summary>Argumentos brutos repassados ao processo alvo (sem shell intermediário).</summary>
    public string Arguments { get; init; } = string.Empty;
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    /// <summary>Motivo pelo qual o plano não pode ser executado por esta versão (null quando executável).</summary>
    public string? NotExecutableReason { get; init; }

    internal bool ReviewConfirmed { get; set; }

    public bool IsExecutable => Method != UninstallMethod.NotSupported && NotExecutableReason is null;

    public string DescribeCommand() => Method switch
    {
        UninstallMethod.Msi => $"{ExecutablePath} {Arguments}".Trim(),
        UninstallMethod.Executable => $"{ExecutablePath} {Arguments}".Trim(),
        _ => NotExecutableReason ?? LocalizationService.Source("Message.Unsupported"),
    };
}

public enum UninstallOutcome
{
    Pending = 0,
    Success = 1,
    SuccessRebootRequired = 2,
    Failure = 3,
    Cancelled = 4,
    NotExecutable = 5,
    Interrupted = 6,
}

/// <summary>Resultado de uma tentativa de desinstalação (real ou simulada).</summary>
public sealed class UninstallExecutionResult
{
    public required InstalledProgram Program { get; init; }
    public UninstallOutcome Outcome { get; init; }
    public int? ExitCode { get; init; }
    public long DurationMs { get; init; }
    public string Message { get; init; } = string.Empty;

    public string OutcomeLabel => Outcome switch
    {
        UninstallOutcome.Success => LocalizationService.Source("Message.Success"),
        UninstallOutcome.SuccessRebootRequired => LocalizationService.Source("Message.SuccessRestartRecommended"),
        UninstallOutcome.Failure => LocalizationService.Source("Message.Failed"),
        UninstallOutcome.Interrupted => LocalizationService.Source("UpdateOperation.Interrupted"),
        UninstallOutcome.Cancelled => LocalizationService.Source("Message.Canceled"),
        UninstallOutcome.NotExecutable => LocalizationService.Source("Message.Unsupported"),
        _ => "Pendente",
    };
}

/// <summary>Resumo do lote de desinstalação.</summary>
public sealed class UninstallBatchReport
{
    public IReadOnlyList<UninstallExecutionResult> Results { get; init; } = Array.Empty<UninstallExecutionResult>();
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public int CancelledCount { get; init; }
    public int NotExecutableCount { get; init; }
    public bool WasCancelledByUser { get; init; }
}
