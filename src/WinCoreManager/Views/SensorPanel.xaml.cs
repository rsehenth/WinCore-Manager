using System.Windows.Controls;

namespace WinCoreManager.Views;

/// <summary>Identificador do grupo de sensores → ícone do componente.</summary>
public sealed class SensorIconConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        var id = value as string ?? "";
        var key = id.StartsWith("cpu/", StringComparison.Ordinal) ? "Icon.Cpu"
            : id == "ram" ? "Icon.Ram"
            : id.StartsWith("disk/", StringComparison.Ordinal) ? "Icon.Disk" : "Icon.Gpu";
        return System.Windows.Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        throw new NotSupportedException();
}

public partial class SensorPanel : UserControl
{
    public SensorPanel() => InitializeComponent();
}
