using CommunityToolkit.Mvvm.ComponentModel;

namespace WinCoreManager.Models;

/// <summary>
/// Dados exibidos no diálogo de confirmação antes de executar uma
/// ferramenta de manutenção. Nada aqui é convertido em linha de comando —
/// apenas orienta o usuário.
/// </summary>
public sealed partial class MaintenanceConfirmationRequest : ObservableObject
{
    private string _commandText = string.Empty;

    public required string Title { get; init; }

    public required string Description { get; init; }

    public required string CommandText
    {
        get => DriveCommandPreview?.Invoke(SelectedDrive) ?? _commandText;
        init => _commandText = value;
    }
    internal Func<string?, string>? DriveCommandPreview { get; init; }

    public required string Effects { get; init; }

    public required string TypicalDuration { get; init; }

    public bool RequiresElevation { get; init; }

    /// <summary>Quando verdadeiro, o diálogo oferece a lista de unidades fixas (CHKDSK).</summary>
    public bool DriveSelection { get; init; }

    public IReadOnlyList<string> DriveLetters { get; init; } = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandText))]
    private string? _selectedDrive;
}
