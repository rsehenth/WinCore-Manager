using System.Windows;
using WinCoreManager.Helpers;
using WinCoreManager.Models;

namespace WinCoreManager.Views;

public partial class UninstallConfirmDialog : Window
{
    private readonly UninstallConfirmationRequest _request;

    public UninstallConfirmDialog(UninstallConfirmationRequest request)
    {
        InitializeComponent();
        _request = request;
        DataContext = request;
        MaxHeight = SystemParameters.WorkArea.Height;
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
    }

    public static bool Show(Window owner, UninstallConfirmationRequest request)
    {
        var dialog = new UninstallConfirmDialog(request) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
