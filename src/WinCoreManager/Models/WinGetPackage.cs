namespace WinCoreManager.Models;

/// <summary>
/// Aplicativo com atualização disponível, retornado pelo WinGet.
/// </summary>
public sealed class WinGetPackage
{
    public required string Name { get; init; }
    public required string Id { get; init; }
    public string InstalledVersion { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}
