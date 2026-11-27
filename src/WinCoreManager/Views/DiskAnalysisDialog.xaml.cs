using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WinCoreManager.Localization;
using WinCoreManager.Services.DiskSpace;

namespace WinCoreManager.Views;

public partial class DiskAnalysisDialog : Window
{
    private readonly Func<DiskVolume[]> listVolumes;
    private readonly DiskAnalysisOptions? previous;
    private readonly Func<Window, string?> selectFolder;
    private DiskVolume[] volumes = [];
    private string? folder;
    private bool loaded;

    public DiskAnalysisDialog() : this(new DiskVolumeCatalog().List) { }

    internal DiskAnalysisDialog(Func<DiskVolume[]> listVolumes, DiskAnalysisOptions? previous = null,
        Func<Window, string?>? selectFolder = null)
    {
        this.listVolumes = listVolumes;
        this.previous = previous;
        this.selectFolder = selectFolder ?? ChooseFolder;
        InitializeComponent();
        UpdateScope();
    }

    internal DiskAnalysisOptions? Options { get; private set; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 24);
        MaxHeight = SystemParameters.WorkArea.Height - 32;
        SetupStatus.Text = LocalizationService.Current["DiskSpace.Loading"];
        try { volumes = await Task.Run(listVolumes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { volumes = []; }
        if (!IsVisible)
            return;

        Drives.ItemsSource = volumes;
        var saved = previous?.Targets.FirstOrDefault();
        Drives.SelectedItem = volumes.FirstOrDefault(volume => volume.Root == saved?.Volume.Root)
            ?? DiskVolumeCatalog.Preferred(volumes, Environment.SystemDirectory);
        if (previous is not null)
        {
            Fast.IsChecked = previous.Accelerated;
            Duplicates.IsChecked = previous.FindDuplicates;
            if (saved?.Folder is { } lastFolder)
            {
                folder = lastFolder;
                FolderPath.Text = folder;
                OneFolder.IsChecked = true;
            }
            else if (previous.AllLocalDrives)
                AllDrives.IsChecked = true;
        }
        loaded = true;
        UpdateScope();
        if (OneDrive.IsChecked == true)
            Drives.Focus();
    }

    private void OnScopeChanged(object sender, RoutedEventArgs e) => UpdateScope();
    private void OnDriveChanged(object sender, SelectionChangedEventArgs e) => UpdateScope();

    private void UpdateScope()
    {
        if (Begin is null)
            return;
        Drives.IsEnabled = loaded && OneDrive.IsChecked == true;
        FolderPicker.IsEnabled = loaded && OneFolder.IsChecked == true && volumes.Length > 0;
        Begin.IsEnabled = loaded && (AllDrives.IsChecked == true ? volumes.Length > 0
            : OneFolder.IsChecked == true ? FolderVolume() is not null : Drives.SelectedItem is DiskVolume);
        if (loaded)
            SetupStatus.Text = volumes.Length == 0 ? LocalizationService.Current["DiskSpace.NoDrives"]
                : OneFolder.IsChecked == true && folder is not null && FolderVolume() is null ? LocalizationService.Current["DiskSpace.LocalFolderOnly"] : "";
    }

    private DiskVolume? FolderVolume() => folder is null ? null
        : volumes.FirstOrDefault(volume => string.Equals(volume.Root, Path.GetPathRoot(folder), StringComparison.OrdinalIgnoreCase));

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (selectFolder(this) is not { } chosen)
            return;
        folder = chosen;
        FolderPath.Text = folder;
        UpdateScope();
    }

    private static string? ChooseFolder(Window owner)
    {
        var picker = new OpenFolderDialog { Title = LocalizationService.Current["DiskSpace.OneFolder"], Multiselect = false };
        return picker.ShowDialog(owner) == true ? picker.FolderName : null;
    }

    private void OnBegin(object sender, RoutedEventArgs e)
    {
        if (!Begin.IsEnabled)
            return;
        DiskScanTarget[] targets = AllDrives.IsChecked == true
            ? volumes.Select(volume => new DiskScanTarget(volume)).ToArray()
            : OneFolder.IsChecked == true
                ? [new(FolderVolume()!, folder)]
                : [new((DiskVolume)Drives.SelectedItem)];
        Options = new(targets, Fast.IsChecked == true, Duplicates.IsChecked == true, AllDrives.IsChecked == true);
        DialogResult = true;
    }
}
