using WinCoreManager.Localization;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using WinCoreManager.Models;

namespace WinCoreManager.Helpers;

public sealed class HistoryStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value switch
        {
            HistoryStatus.Success => ThemeBrushes.Success,
            HistoryStatus.Failure => ThemeBrushes.Danger,
            HistoryStatus.Interrupted => ThemeBrushes.Warning,
            _ => ThemeBrushes.Secondary,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class HistoryTypeToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is HistoryType type
            ? type switch
            {
                HistoryType.Update => LocalizationService.Source("Message.Update"),
                HistoryType.Maintenance => LocalizationService.Source("Message.Maintenance"),
                HistoryType.Uninstall => LocalizationService.Source("Message.Uninstall"),
                HistoryType.Startup => LocalizationService.Source("Message.Startup"),
                HistoryType.Cleanup => LocalizationService.Source("Message.Cleanup"),
                HistoryType.Tweak => LocalizationService.Source("Message.Tweaks"),
                HistoryType.ResidueCleanup => LocalizationService.Source("Message.LeftoverCleanup"),
                _ => string.Empty,
            }
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}

public sealed class HistoryStatusToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is HistoryStatus status
            ? status switch
            {
                HistoryStatus.Success => LocalizationService.Source("Message.Success"),
                HistoryStatus.Failure => LocalizationService.Source("Message.Failed"),
                HistoryStatus.Cancelled => LocalizationService.Source("Message.Canceled"),
                HistoryStatus.NotExecutable => LocalizationService.Source("Message.Unsupported"),
                HistoryStatus.Interrupted => LocalizationService.Source("UpdateOperation.Interrupted"),
                _ => string.Empty,
            }
            : string.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
