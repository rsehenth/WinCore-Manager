using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Win32;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Services;

internal sealed record BattleNetRegistration(string? Name, string? Publisher, string? Location);
internal sealed record BattleNetLauncher(string? Company, string? SignedPublisher, string? Version);
internal sealed record BattleNetInstallation(string Location, string LauncherName, string LauncherVersion, string LauncherHash);
internal sealed record BattleNetUpgradeAuthorization(WinGetPackage Target, BattleNetInstallation Installation, string Location);

internal sealed class WinGetBattleNetInstallation(
    Func<IReadOnlyList<BattleNetRegistration>> readRegistrations,
    Func<string, BattleNetLauncher> readLauncher)
{
    internal const string ProductCode = "Battle.net";
    internal static WinGetBattleNetInstallation Windows { get; } = new(ReadRegistrations, ReadLauncher);

    internal static bool IsExactTarget(WinGetPackage package) =>
        package.Source == "winget" && package.Id == "Blizzard.BattleNet";

    internal BattleNetInstallation? Find(WinGetPackage package)
    {
        try
        {
            if (!IsExactTarget(package) ||
                readRegistrations() is not [var entry] ||
                !string.Equals(entry.Name, ProductCode, StringComparison.OrdinalIgnoreCase) ||
                !IsBlizzard(entry.Publisher) ||
                !WinGetInstallerRequirements.TryValidateLocation(package, entry.Location, out var location))
                return null;

            foreach (var name in new[] { "Battle.net.exe", "Battle.net Launcher.exe" })
            {
                if (InspectLauncher(location, name) is { } launcher) return launcher;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        { return null; }
    }

    internal bool TryConfirmLocation(WinGetPackage package, BattleNetInstallation installation, string selected, out string location)
    {
        location = string.Empty;
        try
        {
            if (Find(package) != installation ||
                !WinGetInstallerRequirements.TryValidateLocation(package, selected, out var candidate))
                return false;

            // An alternative folder must contain the same verified launcher as the registered installation.
            if (!string.Equals(candidate, installation.Location, StringComparison.OrdinalIgnoreCase) &&
                InspectLauncher(candidate, installation.LauncherName)?.LauncherHash != installation.LauncherHash)
                return false;

            location = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        { return false; }
    }

    private BattleNetInstallation? InspectLauncher(string location, string name)
    {
        var path = Path.Combine(location, name);
        if (!File.Exists(path) ||
            (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 ||
            !ResidueFileGuard.PathIsCanonical(path))
            return null;

        using var stream = File.OpenRead(path);
        var launcher = readLauncher(path);
        if (!IsBlizzard(launcher.Company) || !IsBlizzard(launcher.SignedPublisher) ||
            !Version.TryParse(launcher.Version, out var version))
            return null;

        return new(location, name, version.ToString(), Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool IsBlizzard(string? name) =>
        string.Equals(name, "Blizzard Entertainment", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "Blizzard Entertainment, Inc.", StringComparison.OrdinalIgnoreCase);

    private static BattleNetLauncher ReadLauncher(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new(version.CompanyName, StartupSignature.VerifiedPublisher(path), version.FileVersion);
    }

    private static IReadOnlyList<BattleNetRegistration> ReadRegistrations()
    {
        var entries = new List<BattleNetRegistration>();
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var entry = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}", writable: false);
            if (entry is not null)
                entries.Add(new(entry.GetValue("DisplayName") as string, entry.GetValue("Publisher") as string,
                    entry.GetValue("InstallLocation") as string));
        }
        return entries;
    }
}
