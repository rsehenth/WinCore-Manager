using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Services;

internal enum AppInstallerOrigin { Inbox = 2, Store = 3 }
internal sealed record AppInstallerRegistration(string Family, AppInstallerOrigin Origin, string InstallPath);

internal static class WinGetTrust
{
    internal const string AppInstallerFamily = "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe";
    internal static readonly IReadOnlyList<string> AllowedSources = ["winget", "msstore"];

    internal static bool IsAllowedSource(string source) => AllowedSources.Contains(source, StringComparer.Ordinal);

    internal static string? ResolveAlias(string localAppData) =>
        ResolveAlias(localAppData, ReadRegistrations, ResidueFileGuard.IsPlainDirectory, ResidueFileGuard.ReadAppExecutionAlias);

    internal static string? ResolveAlias(string localAppData, Func<IReadOnlyList<AppInstallerRegistration>> readRegistrations,
        Func<string, bool> plainDirectory, Func<string, (string Family, string Target)?> readAlias)
    {
        if (!ResiduePathRules.TryNormalize(localAppData, out var normalized)) return null;

        // A package-specific alias is a Windows registration, independent of the user's default alias.
        var directory = Path.Combine(normalized, "Microsoft", "WindowsApps", AppInstallerFamily);
        var alias = Path.Combine(directory, "winget.exe");
        if (!plainDirectory(directory) || readAlias(alias) is not { } link ||
            !string.Equals(link.Family, AppInstallerFamily, StringComparison.OrdinalIgnoreCase))
            return null;

        var matched = readRegistrations().Count(registration =>
            string.Equals(registration.Family, AppInstallerFamily, StringComparison.OrdinalIgnoreCase) &&
            registration.Origin is AppInstallerOrigin.Inbox or AppInstallerOrigin.Store &&
            ResiduePathRules.TryNormalize(registration.InstallPath, out var installPath) &&
            string.Equals(link.Target, Path.Combine(installPath, "winget.exe"), StringComparison.OrdinalIgnoreCase));
        return matched == 1 ? alias : null;
    }

    internal static bool IsTrustedExport(string source, string json)
    {
        var (argument, type, identifier) = source switch
        {
            "winget" => ("https://cdn.winget.microsoft.com/cache", "Microsoft.PreIndexed.Package", "Microsoft.Winget.Source_8wekyb3d8bbwe"),
            "msstore" => ("https://storeedgefd.dsx.mp.microsoft.com/v9.0", "Microsoft.Rest", "StoreEdgeFD"),
            _ => ("", "", ""),
        };
        if (argument.Length == 0 || json.Length > 16_384) return false;
        try
        {
            var options = new JsonDocumentOptions { MaxDepth = 6, AllowDuplicateProperties = false };
            using var document = JsonDocument.Parse(json, options);
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) return false;
            return Matches(item, "Name", source) && Matches(item, "Arg", argument) && Matches(item, "Type", type) &&
                Matches(item, "Identifier", identifier) && MatchesCatalogData(item, source, identifier, options) &&
                item.TryGetProperty("TrustLevel", out var trust) &&
                trust.ValueKind == JsonValueKind.Array &&
                trust.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "Trusted") &&
                (source == "msstore" || trust.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "StoreOrigin"));
        }
        catch (JsonException) { return false; }
    }

    private static bool MatchesCatalogData(JsonElement item, string source, string identifier, JsonDocumentOptions options)
    {
        if (source == "msstore") return !item.TryGetProperty("Data", out _) || Matches(item, "Data", "");
        if (Matches(item, "Data", identifier)) return true;
        if (!item.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.String) return false;
        using var document = JsonDocument.Parse(data.GetString()!, options);
        var indexed = document.RootElement;
        return indexed.ValueKind == JsonValueKind.Object &&
            Matches(indexed, "fullIdentity", identifier) &&
            Matches(indexed, "deltaIdentity", "Microsoft.Winget.Source.Delta_8wekyb3d8bbwe") &&
            Matches(indexed, "baselineIdentity", "Microsoft.Winget.Source.Baseline_8wekyb3d8bbwe");
    }

    private static bool Matches(JsonElement item, string property, string value) =>
        item.TryGetProperty(property, out var actual) && actual.ValueKind == JsonValueKind.String && actual.GetString() == value;

    internal static ProcessStartInfo StartInfo(string path, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = Environment.SystemDirectory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static unsafe IReadOnlyList<AppInstallerRegistration> ReadRegistrations()
    {
        var result = new List<AppInstallerRegistration>();
        var path = new char[32_768];
        fixed (char* pathPtr = path)
        {
            foreach (var fullName in ReadRegisteredPackageNames(AppInstallerFamily))
            {
                uint pathLength = (uint)path.Length;
                if (GetStagedPackageOrigin(fullName, out var origin) != 0 ||
                    origin is not (AppInstallerOrigin.Inbox or AppInstallerOrigin.Store) ||
                    GetPackagePathByFullName(fullName, ref pathLength, pathPtr) != 0 || pathLength <= 1)
                    continue;
                result.Add(new(AppInstallerFamily, origin, new string(pathPtr).TrimEnd(Path.DirectorySeparatorChar)));
            }
        }
        return result;
    }

    internal static unsafe IReadOnlyList<string> ReadRegisteredPackageNames(string family)
    {
        const int errorInsufficientBuffer = 122;
        uint count = 0, characters = 0;
        if (GetPackagesByPackageFamily(family, ref count, null, ref characters, null) != errorInsufficientBuffer ||
            count is 0 or > 64 || characters is 0 or > 16_384)
            return [];

        var names = new nint[count];
        var buffer = new char[characters];
        fixed (nint* namesPtr = names)
        fixed (char* bufferPtr = buffer)
        {
            if (GetPackagesByPackageFamily(family, ref count, namesPtr, ref characters, bufferPtr) != 0)
                return [];
            return names.Take((int)count).Select(pointer => Marshal.PtrToStringUni(pointer)).OfType<string>().ToArray();
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe int GetPackagesByPackageFamily(string family, ref uint count, nint* names, ref uint length, char* buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe int GetPackagePathByFullName(string fullName, ref uint length, char* buffer);

    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetStagedPackageOrigin(string fullName, out AppInstallerOrigin origin);
}
