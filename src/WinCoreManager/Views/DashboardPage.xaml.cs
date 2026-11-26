using System.Windows;
using System.Windows.Controls;
using WinCoreManager.Localization;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class DashboardPage : UserControl
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (DataContext is DashboardViewModel dashboard) dashboard.Activation.RefreshIfIdle();
        };
    }

    private void OpenActivationRepository(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DashboardViewModel dashboard) return;

        var language = LocalizationService.Current;
        dashboard.Activation.ActivateCommand.Execute(() =>
            LocalizedDialog.Confirm(Window.GetWindow(this), language["WindowsCard.Title"],
                language["WindowsCard.Confirm"], "WindowsCard.OpenRepository"));
    }
}
