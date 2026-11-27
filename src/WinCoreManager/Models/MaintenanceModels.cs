using System.IO;
using WinCoreManager.Localization;

namespace WinCoreManager.Models;

public enum MaintenanceStatus
{
    Pending = 0,
    Running = 1,
    Success = 2,
    Failed = 3,
    Cancelled = 4,
    Interrupted = 5,
}

/// <summary>
/// Definição imutável de uma ferramenta de manutenção permitida.
/// O executável é sempre um caminho absoluto em C:\Windows\System32
/// e os argumentos são fixos — nada vindo da interface é executado.
/// </summary>
public sealed class MaintenanceToolDef
{
    public MaintenanceToolDef(
        string key,
        string displayName,
        string systemExecutable,
        string arguments,
        bool requiresElevation,
        string description,
        string possibleEffects,
        string typicalDuration)
    {
        Key = key;
        DisplayName = displayName;
        SystemExecutable = systemExecutable;
        Arguments = arguments;
        RequiresElevation = requiresElevation;
        Description = description;
        PossibleEffects = possibleEffects;
        TypicalDuration = typicalDuration;
    }

    public string Key { get; }

    public string Glyph => Key switch
    {
        "flushdns" => "\uE968",
        "chkdsk_scan" => "\uEDA2",
        "dism_restorehealth" or "sfc_scannow" => "\uE90F",
        _ => "\uE73E",
    };

    public string ActionLabel => LocalizationService.Source(Key switch
    {
        "dism_restorehealth" or "sfc_scannow" => "Maintenance.StartRepair",
        "flushdns" => "Maintenance.FlushDns",
        _ => "Maintenance.RunCheck",
    });

    public string CardDescription => Key switch
    {
        "sfc_verifyonly" => LocalizationService.Source("Maintenance.VerifyDescription"),
        "dism_restorehealth" => LocalizationService.Source("Maintenance.RepairDescription"),
        _ => Description,
    };

    public string DisplayName { get; }

    /// <summary>Caminho absoluto do executável (C:\Windows\System32).</summary>
    public string SystemExecutable { get; }

    public string Arguments { get; }

    public bool RequiresElevation { get; }

    public string Description { get; }

    public string PossibleEffects { get; }

    public string TypicalDuration { get; }

    public string ExecutableName => Path.GetFileName(SystemExecutable);

    /// <summary>Texto do comando exibido ao usuário antes da execução.</summary>
    public string DisplayCommand => ExecutableName + " " + Arguments;
}
