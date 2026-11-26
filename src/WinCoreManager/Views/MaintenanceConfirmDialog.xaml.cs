using WinCoreManager.Localization;
using System.Windows;
using WinCoreManager.Helpers;
using WinCoreManager.Models;

namespace WinCoreManager.Views;

public partial class MaintenanceConfirmDialog : Window
{
    private readonly MaintenanceConfirmationRequest _request;

    public MaintenanceConfirmDialog(MaintenanceConfirmationRequest request)
    {
        InitializeComponent();
        _request = request;
        DataContext = request;
        MaxHeight = SystemParameters.WorkArea.Height;
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
    }

    public static bool Show(Window owner, MaintenanceConfirmationRequest request)
    {
        var dialog = new MaintenanceConfirmDialog(request) { Owner = owner };
        return dialog.ShowDialog() == true;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_request.DriveSelection && string.IsNullOrWhiteSpace(_request.SelectedDrive))
        {
            LocalizedDialog.Inform(this, "WinCore Manager", LocalizationService.Source("Message.SelectTheDriveToScan"));
            return;
        }

        DialogResult = true;
    }
}
