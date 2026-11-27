using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace WinCoreManager.Services;

internal static class WinGetInstallerRequirements
{
    private const int MaxManifestBytes = 256 * 1024;
    private const string OfficialRepository = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/";
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
    });

    internal static Task<bool?> ReadLocationRequirementAsync(WinGetPackage package, CancellationToken token) =>
        ReadRequirementAsync(package, token, requireAuditedMsi: false);

    internal static async Task<bool> ReadAuditedMsiAsync(WinGetPackage package, CancellationToken token) =>
        WinGetMsiUpgrade.IsExactTarget(package) &&
        await ReadRequirementAsync(package, token, requireAuditedMsi: true).ConfigureAwait(false) == false;

    private static async Task<bool?> ReadRequirementAsync(WinGetPackage package, CancellationToken token, bool requireAuditedMsi)
    {
        // Store packages use the Store installer contract and expose no custom install directory.
        if (package.Source == "msstore") return false;
        if (!WingetTableParser.IsActionableId(package.Id) || package.Id.Length > 128 ||
            !IsVersionSegment(package.AvailableVersion))
            return null;

        var idPath = string.Join('/', package.Id.Split('.').Select(Uri.EscapeDataString));
        if (package.Id.Split('.').Any(string.IsNullOrEmpty)) return null;
        var prefix = $"{OfficialRepository}{char.ToLowerInvariant(package.Id[0])}/{idPath}/" +
            $"{Uri.EscapeDataString(package.AvailableVersion)}/{Uri.EscapeDataString(package.Id)}";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var installer = await Client.GetAsync(prefix + ".installer.yaml", HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            if (installer.StatusCode != HttpStatusCode.NotFound)
                return await ReadManifestAsync(installer, package, "installer", deadline.Token, requireAuditedMsi).ConfigureAwait(false);

            using var singleton = await Client.GetAsync(prefix + ".yaml", HttpCompletionOption.ResponseHeadersRead,
                deadline.Token).ConfigureAwait(false);
            return await ReadManifestAsync(singleton, package, "singleton", deadline.Token, requireAuditedMsi).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException ||
                                   ex is OperationCanceledException && !token.IsCancellationRequested)
        { return null; }
    }

    private static bool IsVersionSegment(string value) => value.Length is > 0 and <= 128 &&
        value is not "." and not ".." &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '+');

    private static async Task<bool?> ReadManifestAsync(HttpResponseMessage response, WinGetPackage package,
        string type, CancellationToken token, bool requireAuditedMsi)
    {
        if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxManifestBytes)
            return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var bytes = new byte[MaxManifestBytes + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
        if (length > MaxManifestBytes) return null;
        return ParseLocationRequirement(Encoding.UTF8.GetString(bytes, 0, length).TrimStart('\uFEFF'), package, type, requireAuditedMsi);
    }

    internal static bool? ParseLocationRequirement(string content, WinGetPackage package, string type = "installer", bool requireAuditedMsi = false)
    {
        if (content.Length > MaxManifestBytes) return null;
        try
        {
            // WinGet manifests use plain YAML nodes. Decline aliases, anchors and custom tags.
            var parser = new Parser(new StringReader(content));
            var depth = 0;
            while (parser.MoveNext())
            {
                depth += parser.Current?.NestingIncrease ?? 0;
                if (depth > 24 || parser.Current is AnchorAlias or NodeEvent { Anchor.IsEmpty: false } or NodeEvent { Tag.IsEmpty: false })
                    return null;
            }

            var yaml = new YamlStream();
            yaml.Load(new StringReader(content));
            if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root ||
                Scalar(root, "PackageIdentifier") != package.Id ||
                Scalar(root, "PackageVersion") != package.AvailableVersion ||
                Scalar(root, "ManifestType") != type ||
                !root.Children.TryGetValue(new YamlScalarNode("Installers"), out var node) ||
                node is not YamlSequenceNode sequence || sequence.Children.Count == 0)
                return null;

            if (requireAuditedMsi && (!WinGetMsiUpgrade.IsExactTarget(package) || !MatchesAuditedMsi(root, sequence))) return null;

            var inherited = ReadRequirement(root, false);
            var required = false;
            var verifyBattleNet = WinGetBattleNetInstallation.IsExactTarget(package) &&
                WingetTableParser.IsUnknownVersion(package.InstalledVersion);
            // The CLI picks an installer later using the installed scope, type, locale and architecture.
            foreach (var child in sequence.Children)
            {
                if (child is not YamlMappingNode installer) return null;
                var requiresLocation = ReadRequirement(installer, inherited);
                if (verifyBattleNet && (!requiresLocation || !MatchesBattleNetInstaller(root, installer))) return null;
                required |= requiresLocation;
            }
            return required;
        }
        catch (Exception ex) when (ex is YamlException or InvalidOperationException or ArgumentException)
        { return null; }
    }

    private static string? Scalar(YamlMappingNode map, string key) =>
        (Value(map, key) as YamlScalarNode)?.Value;

    private static YamlNode? Value(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    private static bool MatchesAuditedMsi(YamlMappingNode root, YamlSequenceNode installers)
    {
        string? Effective(YamlMappingNode installer, string key) => Scalar(installer, key) ?? Scalar(root, key);
        var matches = installers.Children.OfType<YamlMappingNode>().Where(installer =>
            Scalar(installer, "Architecture") == "x64" && Effective(installer, "InstallerType") == "wix").ToArray();
        return matches is [var msi] && Effective(msi, "Scope") == "machine" && Effective(msi, "UpgradeBehavior") == "install" &&
            Scalar(msi, "InstallerUrl") == "https://github.com/cli/cli/releases/download/v2.106.0/gh_2.106.0_windows_amd64.msi" &&
            string.Equals(Scalar(msi, "InstallerSha256"), WinGetMsiUpgrade.TargetSha256, StringComparison.OrdinalIgnoreCase) &&
            Effective(msi, "ProductCode") == WinGetMsiUpgrade.TargetProduct &&
            new[] { "Dependencies", "InstallerSuccessCodes", "ExpectedReturnCodes", "NestedInstallerType" }
                .All(key => Value(root, key) is null && Value(msi, key) is null);
    }

    private static bool MatchesBattleNetInstaller(YamlMappingNode root, YamlMappingNode installer)
    {
        if ((Scalar(installer, "InstallerType") ?? Scalar(root, "InstallerType")) != "exe") return false;
        var direct = Value(installer, "ProductCode") ?? Value(root, "ProductCode");
        if (direct is not null && !IsProduct(direct)) return false;

        var arp = Value(installer, "AppsAndFeaturesEntries") ?? Value(root, "AppsAndFeaturesEntries");
        return arp is null ? direct is not null :
            arp is YamlSequenceNode { Children.Count: > 0 } sequence &&
            sequence.Children.All(item => item is YamlMappingNode entry && IsProduct(Value(entry, "ProductCode")));

        static bool IsProduct(YamlNode? node) => node is YamlScalarNode scalar &&
            string.Equals(scalar.Value, WinGetBattleNetInstallation.ProductCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadRequirement(YamlMappingNode map, bool fallback)
    {
        if (!map.Children.ContainsKey(new YamlScalarNode("InstallLocationRequired"))) return fallback;
        return bool.TryParse(Scalar(map, "InstallLocationRequired"), out var flag)
            ? flag : throw new InvalidOperationException();
    }

    internal static bool TryValidateLocation(WinGetPackage package, string? input, out string location)
    {
        location = string.Empty;
        try
        {
            if (input is null || input.Contains('"') || input.Any(char.IsControl) ||
                !ResiduePathRules.TryNormalize(input, out var path) ||
                path.Length <= 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' ||
                !ResidueFileGuard.IsPlainDirectory(path) || new DriveInfo(path[..3]).DriveType is not DriveType.Fixed)
                return false;

            // The chosen folder must identify the existing launcher, not a Battle.net game library.
            if (IsBattleNet(package) && !IsPlainFile(Path.Combine(path, "Battle.net Launcher.exe")) &&
                !IsPlainFile(Path.Combine(path, "Battle.net.exe")))
                return false;
            location = path;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    internal static bool IsBattleNet(WinGetPackage package) => package.Source == "winget" &&
        package.Id.Equals("Blizzard.BattleNet", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainFile(string path) => File.Exists(path) &&
        (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) == 0;
}
