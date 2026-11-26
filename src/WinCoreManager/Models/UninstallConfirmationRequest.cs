namespace WinCoreManager.Models;

/// <summary>
/// Dados exibidos no diálogo de confirmação antes de executar desinstalações.
/// Nada aqui é convertido em linha de comando — apenas orienta o usuário.
/// </summary>
public sealed class UninstallConfirmationRequest
{
    public required string Title { get; init; }

    public required string Description { get; init; }

    /// <summary>Comando único que será executado (quando houver exatamente 1 programa executável).</summary>
    public string? CommandText { get; init; }

    public required string Effects { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }

    public required int ItemCount { get; init; }

    public string ConfirmButtonText { get; init; } = "Desinstalar";

    public bool HasWarnings => Warnings.Count > 0;
}
