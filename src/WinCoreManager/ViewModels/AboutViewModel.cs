using WinCoreManager.Localization;
using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services;
using WinCoreManager.Services.SelfUpdate;

namespace WinCoreManager.ViewModels;

public partial class AboutViewModel : ViewModelBase
{
    private readonly IExternalNavigation _external;

    public string AppName { get; }
    public string AppVersion { get; }
    public string Platform => $"Windows · {RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
    public string RepositoryText => $"github.com/{GitHubReleases.OfficialRepository}";
    public string BuildChannel => LocalizationService.Source(GitHubReleases.IsPublishedBuild ? "About.Published"
        : AppVersion.Contains("-rc.", StringComparison.Ordinal) ? "About.Candidate" : "About.Development");
    public string AuthorName => "Lucas Sehnem";
    public string AuthorProfileText => "github.com/rsehenth";
    [ObservableProperty] private string _linkMessage = "";

    public AboutViewModel(IExternalNavigation external)
    {
        _external = external;
        var assembly = typeof(AboutViewModel).Assembly;
        AppName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "WinCore Manager";
        AppVersion = GitHubReleases.InstalledVersion.ToString();
        Title = LocalizationService.Source("Message.About");
    }

    [RelayCommand]
    private void OpenFixMyPc() => Open(WindowsExternalNavigation.FixMyPc);

    [RelayCommand]
    private void OpenAuthor() => Open(WindowsExternalNavigation.AuthorProfile);

    [RelayCommand]
    private void OpenRepository() => Open(new Uri("https://" + RepositoryText));

    [RelayCommand]
    private void OpenBcu() => Open(new Uri("https://github.com/BCUninstaller/Bulk-Crap-Uninstaller"));

    [RelayCommand]
    private void OpenBitBroom() => Open(new Uri("https://github.com/pwnapplehat/BitBroom"));

    [RelayCommand]
    private void OpenUniGetUI() => Open(new Uri("https://github.com/Devolutions/UniGetUI"));

    [RelayCommand]
    private void OpenDiskScope() => Open(new Uri("https://github.com/UnstableNT/diskscope"));

    private void Open(Uri destination)
    {
        LinkMessage = _external.OpenWeb(destination) ? "" : LocalizationService.Source("Message.CouldNotOpenTheDefaultBrowser");
    }
}
