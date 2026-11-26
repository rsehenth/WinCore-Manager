using WinCoreManager.Localization;
using System.Globalization;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.ViewModels;

public enum UninstallRowStatus
{
    Idle = 0,
    Queued = 1,
    Running = 2,
    Success = 3,
    SuccessRebootRequired = 4,
    Failed = 5,
    Cancelled = 6,
    NotExecutable = 7,
    Interrupted = 8,
}

/// <summary>Linha da lista de programas instalados: empacota InstalledProgram com estado de UI.</summary>
public partial class UninstallerItemRow : ObservableObject
{
    public UninstallerItemRow(InstalledProgram program)
    {
        Program = program;
        Classification = ProgramClassifier.Evaluate(program);
    }

    public InstalledProgram Program { get; }

    /// <summary>Classificação calculada a partir do inventário (categoria + política + motivos).</summary>
    public ProgramClassification Classification { get; }

    public string Name => Program.Name;

    public string DisplayVersion => Program.DisplayVersion;

    public string Publisher => Program.Publisher;

    public string Origin => Program.Origin;

    public string KindLabel => Program.KindLabel;

    public string CategoryLabel => ProgramClassificationLabels.Category(Classification.Category);

    public DateTime? InstallDate => Program.InstallDate;
    public bool HasInstallDate => InstallDate.HasValue;
    public string InstallDateText => InstallDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—";

    public string EstimatedSizeText => Program.EstimatedSizeKb is long kb && kb > 0
        ? kb >= 1024 ? string.Create(LocalizationService.Current.Culture, $"{kb / 1024.0:F1} MiB") : $"{kb} KiB"
        : "—";

    /// <summary>Itens fora da lista principal: componentes/dependências técnicas ou entradas
    /// protegidas pelo fabricante (visíveis apenas no filtro avançado, para diagnóstico).</summary>
    public bool IsTechnical => Classification.Category
            is ProgramCategory.SystemComponent or ProgramCategory.SharedDependency
        || Classification.Policy is RemovalPolicy.Blocked;

    /// <summary>Motivos da proteção/bloqueio (vazio quando a remoção é permitida com confirmação).</summary>
    public string ProtectionInfo => Classification.Policy == RemovalPolicy.AllowedWithConfirmation
        ? string.Empty
        : string.Join(" ", Classification.Reasons);

    /// <summary>Checkbox: itens bloqueados nunca são selecionáveis (nem individualmente).</summary>
    public bool CanSelect => Classification.Policy is not RemovalPolicy.Blocked
                             && Status is not (UninstallRowStatus.Success or UninstallRowStatus.SuccessRebootRequired);

    /// <summary>Entrada em lote: somente itens com remoção permitida por confirmação simples.</summary>
    public bool IsBatchSelectable => Classification.Policy is RemovalPolicy.AllowedWithConfirmation && CanSelect;

    /// <summary>Nunca executável nesta versão (listado apenas como referência).</summary>
    public bool IsUnsupportedKind => Program.Kind is
        ProgramKind.PowerShell or ProgramKind.SdbInst or ProgramKind.Steam
        or ProgramKind.StoreApp or ProgramKind.WindowsFeature or ProgramKind.WindowsUpdate;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private UninstallRowStatus _status = UninstallRowStatus.Idle;

    public string StatusText => Status switch
    {
        UninstallRowStatus.Idle => "—",
        UninstallRowStatus.Queued => LocalizationService.Source("Message.Queued"),
        UninstallRowStatus.Running => LocalizationService.Source("Message.Uninstalling"),
        UninstallRowStatus.Success => LocalizationService.Source("Message.Success"),
        UninstallRowStatus.SuccessRebootRequired => LocalizationService.Source("Message.SuccessRestartRecommended"),
        UninstallRowStatus.Failed => LocalizationService.Source("Message.Failed"),
        UninstallRowStatus.Cancelled => LocalizationService.Source("Message.Canceled"),
        UninstallRowStatus.Interrupted => LocalizationService.Source("UpdateOperation.Interrupted"),
        UninstallRowStatus.NotExecutable => LocalizationService.Source("Message.Unsupported"),
        _ => "—",
    };

    public Brush StatusBrush => Status switch
    {
        UninstallRowStatus.Success or UninstallRowStatus.SuccessRebootRequired => BrushExtensions.SolidSuccess,
        UninstallRowStatus.Failed => BrushExtensions.SolidDanger,
        UninstallRowStatus.Running => BrushExtensions.SolidAccent,
        UninstallRowStatus.NotExecutable or UninstallRowStatus.Interrupted => BrushExtensions.SolidWarning,
        _ => BrushExtensions.SolidSecondary,
    };
}
