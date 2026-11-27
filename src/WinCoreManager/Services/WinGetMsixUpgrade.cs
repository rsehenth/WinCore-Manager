using WinCoreManager.Models;

namespace WinCoreManager.Services;

// These catalog IDs have a known current-user AppX family and deployment contract.
// Other WinGet installers can start work outside the observed process tree.
internal sealed class WinGetMsixUpgrade(string family, string architecture, Version target,
    Func<string, IReadOnlyList<string>> readPackages)
{
    private const string XamlFamily = "Microsoft.UI.Xaml.2.8_8wekyb3d8bbwe";
    private const string VcLibrariesFamily = "Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe";
    private static readonly Version MinimumXaml = new(8, 2306, 22001, 0);
    private static readonly Version MinimumVcLibraries = new(14, 0, 0, 0);

    internal static bool IsExactTarget(WinGetPackage package) => package is
        { Source: "winget", Id: "Microsoft.WindowsTerminal" or "Microsoft.WindowsTerminal.Preview" };

    internal static WinGetMsixUpgrade? TryCreate(WinGetPackage package, Func<string, IReadOnlyList<string>> readPackages)
    {
        if (!IsExactTarget(package)) return null;
        var family = package.Id switch
        {
            "Microsoft.WindowsTerminal" => "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            "Microsoft.WindowsTerminal.Preview" => "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
            _ => null,
        };
        if (family is null || !Version.TryParse(package.InstalledVersion, out var installed) ||
            !Version.TryParse(package.AvailableVersion, out var target) || installed.Revision < 0 || target.Revision < 0 ||
            target <= installed) return null;

        var registrations = ReadMainPackages(family, readPackages);
        if (registrations is not [{ Version: var current, Architecture: var architecture }] || current != installed ||
            !HasFramework(XamlFamily, MinimumXaml, architecture, readPackages) ||
            !HasFramework(VcLibrariesFamily, MinimumVcLibraries, architecture, readPackages)) return null;

        return new(family, architecture, target, readPackages);
    }

    internal bool VerifyRegistration() => ReadMainPackages(family, readPackages) is
        [{ Version: var current, Architecture: var currentArchitecture }] && current == target && currentArchitecture == architecture;

    private static bool HasFramework(string family, Version minimum, string architecture,
        Func<string, IReadOnlyList<string>> readPackages) =>
        ReadMainPackages(family, readPackages).Any(package => package.Version >= minimum &&
            (package.Architecture == architecture || package.Architecture == "neutral"));

    private static List<(Version Version, string Architecture)> ReadMainPackages(string family,
        Func<string, IReadOnlyList<string>> readPackages)
    {
        var packages = new List<(Version, string)>();
        foreach (var fullName in readPackages(family))
        {
            var parts = fullName.Split('_');
            // Resource packages and the bundle container are not a registered application.
            if (parts is [var name, var version, "x86" or "x64" or "arm64" or "neutral", "", var publisher] &&
                string.Equals($"{name}_{publisher}", family, StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(version, out var parsed) && parsed.Revision >= 0)
                packages.Add((parsed, parts[2]));
        }
        return packages;
    }
}
