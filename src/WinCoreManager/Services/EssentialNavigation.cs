using System.Diagnostics;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public interface IEssentialNavigation
{
    bool OpenAppPage(string id);
    bool OpenActivationSettings();
    bool OpenActivationRepository();
}

internal sealed class EssentialNavigation : IEssentialNavigation
{
    private const string ActivationSettings = "ms-settings:activation";
    private const string ActivationRepository = "https://github.com/massgravel/Microsoft-Activation-Scripts";
    private readonly Func<ProcessStartInfo, bool> _start;

    public EssentialNavigation() : this(WindowsExternalNavigation.Start) { }
    internal EssentialNavigation(Func<ProcessStartInfo, bool> start) => _start = start;

    public bool OpenAppPage(string id)
    {
        var page = EssentialAppCatalog.Apps.FirstOrDefault(app => app.Id == id)?.Page;
        return page is not null && IsApprovedPage(page) && Open(page.AbsoluteUri);
    }

    internal static bool IsApprovedPage(Uri page) =>
        page.IsAbsoluteUri && page.IsWellFormedOriginalString()
        && page.Scheme == Uri.UriSchemeHttps && page.HostNameType == UriHostNameType.Dns
        && page.IsDefaultPort && page.UserInfo.Length == 0 && page.Fragment.Length == 0
        && EssentialAppCatalog.Apps.Any(app =>
            string.Equals(app.Page.AbsoluteUri, page.AbsoluteUri, StringComparison.Ordinal));

    public bool OpenActivationSettings() => Open(ActivationSettings);
    public bool OpenActivationRepository() => Open(ActivationRepository);

    private bool Open(string destination) => _start(new ProcessStartInfo(destination) { UseShellExecute = true });
}
