using System.Windows;
using System.Windows.Controls;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class HardwarePage : UserControl
{
    public HardwarePage() => InitializeComponent();

    private void OnOpenSensors(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        if (owner?.OwnedWindows.OfType<SensorWindow>().FirstOrDefault() is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        if (DataContext is not HardwareViewModel viewModel) return;
        new SensorWindow(viewModel.CreateSensorPanel()) { Owner = owner }.Show();
    }
}
