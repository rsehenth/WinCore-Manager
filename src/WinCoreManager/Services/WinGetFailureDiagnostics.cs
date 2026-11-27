using System.IO;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Services;

internal static class WinGetFailureDiagnostics
{
    private const int ShellExecuteFailed = unchecked((int)0x8A150006);
    private const int MaximumLogBytes = 2 * 1024 * 1024;
    private const string ProductMessage = "Product: DeckCraft -- This app is already installed for everyone.";
    private const string ProductCode = "Property(S): ProductCode = {033807C7-AC25-4481-AAA0-FF64F4E0AA22}";
    private static readonly string[] LogPrefixes = ["Installer log is available at: ", "O registro do instalador está disponível em: "];

    internal static string? Read(WinGetPackage package, ProcessRunResult run, DateTime startedUtc, string directory)
    {
        if (package is not { Id: "ArtCraft.DeckCraft", Source: "winget", InstalledVersion: "0.18.1", AvailableVersion: "0.19.1" }
            || run.StartFailed || run.Cancelled || run.Interrupted || !run.HasExitCode || run.ExitCode != ShellExecuteFailed)
            return null;

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in run.Output.Concat(run.Errors))
        {
            var line = WinGetDownloadProgress.StripControls(raw).Trim();
            foreach (var prefix in LogPrefixes)
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    paths.Add(line[prefix.Length..].Trim().Trim('"'));
        }
        if (paths.Count != 1) return null;
        var path = paths.Single();

        try
        {
            if (!StartupPaths.TryLocalPath(path, out var normalized) || normalized != path
                || !string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(path).StartsWith("ArtCraft.DeckCraft.0.19.1-", StringComparison.Ordinal)
                || !Path.GetExtension(path).Equals(".log", StringComparison.OrdinalIgnoreCase))
                return null;
            using var pins = new WindowsStartupFileMove.DirectoryPins();
            pins.Pin(directory);
            using var stream = WindowsStartupFileMove.OpenVerifiedFileForRead(path);
            if (!ResidueFileGuard.IsPlainReadFile(stream.SafeFileHandle, path) || stream.Length is <= 0 or > MaximumLogBytes
                || File.GetLastWriteTimeUtc(path) < startedUtc) return null;

            using var reader = new StreamReader(stream);
            var matchedProduct = false;
            var scopeConflict = false;
            var userScope = false;
            var launchConditionsFailed = false;
            var installerFailed = false;
            while (reader.ReadLine() is { } line)
            {
                matchedProduct |= line == ProductCode;
                scopeConflict |= line.StartsWith("MSI (s) (", StringComparison.Ordinal)
                    && line.Contains(ProductMessage, StringComparison.Ordinal);
                userScope |= line == "Property(S): MSIINSTALLPERUSER = 1";
                launchConditionsFailed |= line.StartsWith("Action ended ", StringComparison.Ordinal)
                    && line.EndsWith(": LaunchConditions. Return value 3.", StringComparison.Ordinal);
                installerFailed |= line.StartsWith("MSI (s) (", StringComparison.Ordinal)
                    && (line.EndsWith("Installation success or error status: 1603.", StringComparison.Ordinal)
                        || line.EndsWith("Status de erro ou êxito da instalação: 1603.", StringComparison.Ordinal));
            }
            return matchedProduct && scopeConflict && userScope && launchConditionsFailed && installerFailed
                ? LocalizationService.Source("Message.DeckCraftScopeConflict") : null;
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return null; }
    }
}
