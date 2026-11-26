using WinCoreManager.Localization;
using System.Reflection;
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

    private void Open(Uri destination)
    {
        LinkMessage = _external.OpenWeb(destination) ? "" : LocalizationService.Source("Message.CouldNotOpenTheDefaultBrowser");
    }
}
