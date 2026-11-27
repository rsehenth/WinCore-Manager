using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WinCoreManager.Localization;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

/// <summary>Título (recurso de origem) da seção de inventário → ícone.</summary>
public sealed class HardwareIconConverter : IValueConverter
{
    private static readonly (string Title, string Icon)[] Icons =
    [
        ("Message.OperatingSystem", "Icon.Windows"), ("Message.Processor", "Icon.Cpu"), ("Message.GraphicsCard", "Icon.Gpu"),
        ("Message.MemoryRAM", "Icon.Ram"), ("Message.Storage", "Icon.Disk"),
    ];

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = Icons.FirstOrDefault(item => Equals(value, LocalizationService.Source(item.Title))).Icon ?? "Icon.Computer";
        return Application.Current?.TryFindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public partial class HardwarePage : UserControl
{
    public HardwarePage() => InitializeComponent();

    private void OnOpenSensors(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is MainViewModel viewModel)
            viewModel.OpenSensors();
    }
}
