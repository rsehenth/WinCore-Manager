using WinCoreManager.Localization;
using System.IO;
using System.Windows;
using WinCoreManager.Models;

namespace WinCoreManager.Views;

public partial class ManualResidueDialog : Window
{
    private ManualResidueDialog()
    {
        InitializeComponent();
        NameBox.Focus();
    }

    /// <summary>Retorna o programa construído a partir das informações informadas, ou null se cancelado.</summary>
    public static InstalledProgram? Show(Window owner)
    {
        var dialog = new ManualResidueDialog
        {
            Owner = owner,
        };
        return dialog.ShowDialog() == true ? dialog.BuildProgram() : null;
    }

    private void OnAnalyzeClick(object sender, RoutedEventArgs e)
    {
        if (NameBox.Text.Trim().Length == 0)
        {
            LocalizedDialog.Inform(this, LocalizationService.Source("Message.ScanForLeftovers"), LocalizationService.Source("Message.EnterTheAppName"));
            return;
        }

        DialogResult = true;
    }

    private InstalledProgram BuildProgram()
    {
        var location = LocationBox.Text.Trim();
        return new InstalledProgram
        {
            Name = NameBox.Text.Trim(),
            Publisher = PublisherBox.Text.Trim(),
            InstallLocation = location.Length > 0 && Path.IsPathRooted(location) ? location : string.Empty,
            RegistryKeyName = string.Empty,
            RegistryPath = string.Empty,
        };
    }
}
