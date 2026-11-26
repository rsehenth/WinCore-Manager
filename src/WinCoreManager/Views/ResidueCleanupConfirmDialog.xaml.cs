using System.Windows;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class ResidueCleanupConfirmDialog : Window
{
    internal ResidueCleanupConfirmDialog(ResidueCleanupConfirmationRequest request)
    {
        InitializeComponent();
        DataContext = request;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    public static bool Show(Window owner, ResidueCleanupConfirmationRequest request)
    {
        var dialog = new ResidueCleanupConfirmDialog(request)
        {
            Owner = owner,
        };
        return dialog.ShowDialog() == true;
    }
}
