using System.Windows;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class SensorWindow : Window
{
    internal SensorWindow(SensorPanelViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Activated += (_, _) => viewModel.SetActive(WindowState != WindowState.Minimized);
        Deactivated += (_, _) => viewModel.SetActive(false);
        StateChanged += (_, _) => viewModel.SetActive(IsActive && WindowState != WindowState.Minimized);
        Closed += async (_, _) => await viewModel.CloseAsync();
    }
}
