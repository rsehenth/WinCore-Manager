using WinCoreManager.Localization;
using WinCoreManager.Helpers;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WinCoreManager.Models;

namespace WinCoreManager.ViewModels;

public enum UpdateRowStatus
{
    Idle = 0,
    Queued = 1,
    Running = 2,
    Success = 3,
    SuccessRebootRequired = 4,
    Failed = 5,
    Cancelled = 6,
    Interrupted = 7,
}

/// <summary>Linha da lista de atualizações: empacota WinGetPackage com estado de UI.</summary>
public partial class UpdateItemRow : ObservableObject
{
    public UpdateItemRow(WinGetPackage package)
    {
        Package = package;
    }

    public WinGetPackage Package { get; }

    public string Name => Package.Name;

    public string Id => Package.Id;

    public string InstalledVersion => Package.InstalledVersion;

    public string AvailableVersion => Package.AvailableVersion;

    public string Source => Package.Source;
    public bool CanSelect => Status is not (UpdateRowStatus.Success or UpdateRowStatus.SuccessRebootRequired);

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private UpdateRowStatus _status = UpdateRowStatus.Idle;

    public string StatusText => Status switch
    {
        UpdateRowStatus.Idle => "—",
        UpdateRowStatus.Queued => LocalizationService.Source("Message.Queued"),
        UpdateRowStatus.Running => LocalizationService.Source("Message.Updating"),
        UpdateRowStatus.Success => LocalizationService.Source("Message.Success"),
        UpdateRowStatus.SuccessRebootRequired => LocalizationService.Source("Message.SuccessRestartPending"),
        UpdateRowStatus.Failed => LocalizationService.Source("Message.Failed"),
        UpdateRowStatus.Cancelled => LocalizationService.Source("Message.Canceled"),
        UpdateRowStatus.Interrupted => LocalizationService.Source("UpdateOperation.Interrupted"),
        _ => "—",
    };

    public Brush StatusBrush => Status switch
    {
        UpdateRowStatus.Success or UpdateRowStatus.SuccessRebootRequired => BrushExtensions.SolidSuccess,
        UpdateRowStatus.Failed => BrushExtensions.SolidDanger,
        UpdateRowStatus.Interrupted => BrushExtensions.SolidWarning,
        UpdateRowStatus.Running => BrushExtensions.SolidAccent,
        _ => BrushExtensions.SolidSecondary,
    };
}

/// <summary>Pincéis fixos referenciados pelas linhas (evita resolver ResourceDictionary por item).</summary>
internal static class BrushExtensions
{
    public static Brush SolidSuccess => ThemeBrushes.Success;
    public static Brush SolidDanger => ThemeBrushes.Danger;
    public static Brush SolidAccent => ThemeBrushes.Accent;
    public static Brush SolidSecondary => ThemeBrushes.Secondary;
    public static Brush SolidWarning => ThemeBrushes.Warning;
}
