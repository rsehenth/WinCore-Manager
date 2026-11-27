using WinCoreManager.Localization;
namespace WinCoreManager.Models;

/// <summary>Categoria de um resíduo encontrado.</summary>
public enum ResidueCategory
{
    File = 0,
    Folder = 1,
    RegistryKey = 2,
    RegistryValue = 3,
}

/// <summary>
/// Confiança na associação entre o resíduo e o programa analisado.
/// Alta: associação forte e verificável, elegível para exclusão.
/// Média: provável, exige avaliação manual (não elegível nesta etapa).
/// Baixa: associação fraca — apenas informativa.
/// Protegido: recurso do Windows, de outro aplicativo ou compartilhado — exclusão sempre bloqueada.
/// </summary>
public enum ResidueConfidence
{
    High = 0,
    Medium = 1,
    Low = 2,
    Protected = 3,
}

/// <summary>Um resíduo individual: arquivo, pasta ou chave do Registro.</summary>
public sealed class ResidueItem
{
    public required Guid Id { get; init; }
    public required ResidueCategory Category { get; init; }
    /// <summary>Caminho completo (arquivo/pasta) ou caminho completo da chave do Registro (com a colmeia).</summary>
    public required string Location { get; init; }
    /// <summary>Verdadeiro quando a chave veio da visão de 64 bits do Registro (irrelevante para arquivos).</summary>
    public bool Is64BitView { get; init; }
    public required ResidueConfidence Confidence { get; init; }
    /// <summary>Justificativas legíveis que sustentam a classificação.</summary>
    public IReadOnlyList<string> Justifications { get; init; } = Array.Empty<string>();
    /// <summary>Tamanho aproximado (arquivos/pastas); 0 para chaves.</summary>
    public long SizeBytes { get; init; }
    /// <summary>Identidade e conteúdo observados na detecção; a execução exige exatamente o mesmo valor.</summary>
    public string? Fingerprint { get; init; }

    public string? ValueName { get; init; }
    public string? ValueType { get; init; }

    public bool IsRegistry => Category is ResidueCategory.RegistryKey or ResidueCategory.RegistryValue;
    public string ReviewedLocation => IsRegistry
        ? $"{Location} [{(Is64BitView && !Location.Contains(@"\Wow6432Node\", StringComparison.OrdinalIgnoreCase) ? 64 : 32)}-bit]"
        : Location;
    public bool IsEligibleForCleanup => Confidence == ResidueConfidence.High && Category != ResidueCategory.RegistryValue;

    public string CategoryLabel => Category switch
    {
        ResidueCategory.File => LocalizationService.Source("Message.File"),
        ResidueCategory.Folder => LocalizationService.Source("Message.Folder"),
        ResidueCategory.RegistryValue => LocalizationService.Source("ResidueAdvanced.Value"),
        _ => LocalizationService.Source("Message.Registry"),
    };

    public string ConfidenceLabel => Confidence switch
    {
        ResidueConfidence.High => LocalizationService.Source("Message.High"),
        ResidueConfidence.Medium => LocalizationService.Source("Message.Medium"),
        ResidueConfidence.Low => LocalizationService.Source("Message.Low"),
        _ => LocalizationService.Source("Message.Protected"),
    };
}

/// <summary>Resultado da análise de resíduos de um programa (não destrutiva).</summary>
public sealed class ResidueScanResult
{
    public required InstalledProgram Program { get; init; }
    public IReadOnlyList<ResidueItem> Items { get; init; } = Array.Empty<ResidueItem>();
    public bool ScanFailed { get; init; }
    public string? Error { get; init; }

    public int CountOf(ResidueCategory category) => Items.Count(i => i.Category == category);
    public int CountOf(ResidueConfidence confidence) => Items.Count(i => i.Confidence == confidence);
    public long TotalBytes => Items.Sum(i => i.SizeBytes);
}

/// <summary>Resultado individual da limpeza de um resíduo.</summary>
public enum ResidueCleanupItemOutcome
{
    Pending = 0,
    Success = 1,
    /// <summary>Backup obrigatório falhou — o resíduo NÃO foi removido.</summary>
    BackupFailed = 2,
    /// <summary>Remoção falhou após o backup (quando aplicável).</summary>
    Failed = 3,
    /// <summary>Revalidação bloqueou: item mudou, desapareceu ou virou protegido desde a prévia.</summary>
    SkippedInvalid = 4,
    /// <summary>Não executado porque o usuário cancelou antes.</summary>
    SkippedCancelled = 5,
}

public sealed class ResidueCleanupItemResult
{
    public required ResidueItem Item { get; init; }
    public ResidueCleanupItemOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    /// <summary>Identificador da entrada de quarentena (arquivos/pastas).</summary>
    public Guid? QuarantineEntryId { get; init; }
    /// <summary>Caminho do backup .reg exportado (chaves).</summary>
    public string? RegistryBackupFile { get; init; }
}

/// <summary>Relatório completo de uma operação de limpeza de resíduos.</summary>
public sealed class ResidueCleanupReport
{
    public required Guid OperationId { get; init; }
    public required string ProgramName { get; init; }
    public IReadOnlyList<ResidueCleanupItemResult> Results { get; init; } = Array.Empty<ResidueCleanupItemResult>();
    public bool WasCancelledByUser { get; init; }

    public int SuccessCount => Results.Count(r => r.Outcome == ResidueCleanupItemOutcome.Success);
    public int FailureCount => Results.Count(r => r.Outcome is ResidueCleanupItemOutcome.BackupFailed or ResidueCleanupItemOutcome.Failed);
    public int SkippedCount => Results.Count(r => r.Outcome is ResidueCleanupItemOutcome.SkippedInvalid or ResidueCleanupItemOutcome.SkippedCancelled);
    /// <summary>Verdadeiro somente quando TODOS os itens foram concluídos.</summary>
    public bool IsFullSuccess => Results.Count > 0 && !WasCancelledByUser && SuccessCount == Results.Count;
}

/// <summary>Plano de limpeza aprovado: somente itens revalidados com confiança alta.</summary>
public sealed class ResidueCleanupPlan
{
    public required Guid OperationId { get; init; }
    public required InstalledProgram Program { get; init; }
    /// <summary>Itens aprovados para execução (revalidados na montagem do plano).</summary>
    public IReadOnlyList<ResidueItem> Approved { get; init; } = Array.Empty<ResidueItem>();
    /// <summary>Itens bloqueados na montagem, com o motivo (nunca são executados).</summary>
    public IReadOnlyList<ResidueCleanupItemResult> Blocked { get; init; } = Array.Empty<ResidueCleanupItemResult>();

    internal bool RegistryConsentGranted { get; set; }
    public bool IsEmpty => Approved.Count == 0;
}

/// <summary>Estado no manifesto. Pending é gravado antes do movimento para sobreviver a queda de energia.</summary>
public enum QuarantineEntryState
{
    Complete = 0,
    Pending = 1,
    Restoring = 2,
}

/// <summary>Registro persistido de um item movido para a quarentena (permite recuperação).</summary>
public sealed class QuarantineEntry
{
    public string? ContentSha256 { get; init; }
    public string? RecoveryFingerprint { get; init; }
    public Guid Id { get; set; }
    public QuarantineEntryState State { get; set; }
    /// <summary>Impressão digital do conteúdo guardado; recuperação recusa conteúdo diferente.</summary>
    public string? Fingerprint { get; set; }
    /// <summary>Localização original do item.</summary>
    public string OriginalPath { get; set; } = string.Empty;
    /// <summary>Localização atual dentro da área de quarentena.</summary>
    public string QuarantinedPath { get; set; } = string.Empty;
    public ResidueCategory Category { get; set; }
    public string ProgramName { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; }
    public long SizeBytes { get; set; }
    /// <summary>Caminho do backup .reg associado (quando o item era uma chave).</summary>
    public string? RegistryBackupFile { get; set; }
    /// <summary>Visão de 64 bits do Registro original (quando o item era uma chave).</summary>
    public bool Is64BitView { get; set; }
}
