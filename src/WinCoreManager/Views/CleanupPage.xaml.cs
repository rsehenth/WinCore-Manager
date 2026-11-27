using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinCoreManager.Localization;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

/// <summary>Soma dos tamanhos medidos de um grupo; sem nenhuma medida, "—".</summary>
public sealed class CleanupGroupSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var sizes = (value as IEnumerable<object>)?.OfType<CleanupRow>().Select(row => row.Category.Bytes).OfType<long>().ToArray();
        return sizes is { Length: > 0 }
            ? PresentationText.UseBinarySizeUnits(PresentationText.Translate(CleanupService.FormatBytes(sizes.Sum()), LocalizationService.Current.Culture))
            : "—";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class CleanupPage : UserControl
{
    public CleanupPage() => InitializeComponent();

    private void OnAnalyzeSpace(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not { } owner)
            return;

        if (owner.OwnedWindows.OfType<DiskSpaceWindow>().FirstOrDefault() is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }

        var dialog = new DiskAnalysisDialog { Owner = owner };
        if (dialog.ShowDialog() == true && dialog.Options is { } options)
            new DiskSpaceWindow(options) { Owner = owner }.Show();
    }
}

