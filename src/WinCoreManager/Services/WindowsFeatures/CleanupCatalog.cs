// Cache and profile rules adapted from BitBroom's Catalog/CategoryCatalog.cs (MIT).
// Copyright (c) 2026 BitBroom Contributors. See distribution/Licencas/BitBroom.LICENSE.txt.

using System.ComponentModel;
using System.IO;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.WindowsFeatures;

internal sealed record CleanupRule(string Category, string Anchor, string RelativePath, string Extension = "",
    int Days = 0, bool Recursive = true, string? ProfileCache = null, string? FilePrefix = null);

internal sealed record CleanupDefinition(string Id, string NameKey, string DescriptionKey, string? Process = null);

internal static class CleanupCatalog
{
    internal const string Thumbnails = "WinCore.Thumbnails";
    internal static readonly IReadOnlyList<CleanupDefinition> Definitions =
    [
        new(CleanupFiles.UserTemp, "Message.CleanupUserTemp", "Message.CleanupUserTempDescription"),
        new(CleanupFiles.UserReports, "Message.CleanupUserReports", "Message.CleanupUserReportsDescription"),
        new(CleanupFiles.UserDumps, "Message.CleanupUserDumps", "Message.CleanupUserDumpsDescription"),
        new("WinCore.WerLogs", "Cleanup.WerLogs", "Cleanup.WerLogsDescription"),
        new(Thumbnails, "Cleanup.Thumbnails", "Cleanup.ThumbnailsDescription"),
        new("WinCore.Chrome", "Cleanup.Chrome", "Cleanup.BrowserDescription", "chrome"),
        new("WinCore.Edge", "Cleanup.Edge", "Cleanup.BrowserDescription", "msedge"),
        new("WinCore.Brave", "Cleanup.Brave", "Cleanup.BrowserDescription", "brave"),
        new("WinCore.Firefox", "Cleanup.Firefox", "Cleanup.BrowserDescription", "firefox"),
        new("WinCore.Discord", "Cleanup.Discord", "Cleanup.DiscordDescription", "Discord"),
        new("WinCore.Nvidia", "Cleanup.Nvidia", "Cleanup.ShaderDescription"),
        new("WinCore.Amd", "Cleanup.Amd", "Cleanup.ShaderDescription"),
        new("WinCore.Npm", "Cleanup.Npm", "Cleanup.DownloadDescription"),
        new("WinCore.Pip", "Cleanup.Pip", "Cleanup.DownloadDescription"),
        new("WinCore.NuGet", "Cleanup.NuGet", "Cleanup.DownloadDescription"),
    ];

    internal static IReadOnlyList<CleanupRule> Rules(string local, string roaming) =>
    [
        new(CleanupFiles.UserTemp, local, "Temp", ".tmp", 7, false),
        new(CleanupFiles.UserReports, local, @"Microsoft\Windows\WER\ReportArchive", ".wer"),
        new(CleanupFiles.UserReports, local, @"Microsoft\Windows\WER\ReportQueue", ".wer"),
        new(CleanupFiles.UserDumps, local, "CrashDumps", ".dmp", Recursive: false),
        new("WinCore.WerLogs", local, @"Microsoft\Windows\WER\Temp", ".log", 7, false),
        new(Thumbnails, local, @"Microsoft\Windows\Explorer", ".db", Recursive: false, FilePrefix: "thumbcache_"),
        new("WinCore.Chrome", local, @"Google\Chrome\User Data", ProfileCache: "Cache"),
        new("WinCore.Edge", local, @"Microsoft\Edge\User Data", ProfileCache: "Cache"),
        new("WinCore.Brave", local, @"BraveSoftware\Brave-Browser\User Data", ProfileCache: "Cache"),
        new("WinCore.Firefox", local, @"Mozilla\Firefox\Profiles", ProfileCache: @"cache2\entries"),
        new("WinCore.Discord", roaming, @"discord\Cache"),
        new("WinCore.Discord", roaming, @"discord\Code Cache"),
        new("WinCore.Discord", roaming, @"discord\GPUCache"),
        new("WinCore.Nvidia", local, @"NVIDIA\DXCache", Days: 1),
        new("WinCore.Nvidia", local, @"NVIDIA\GLCache", Days: 1),
        new("WinCore.Nvidia", local, @"NVIDIA\OptixCache", Days: 1),
        new("WinCore.Amd", local, @"AMD\DxCache", Days: 1),
        new("WinCore.Amd", local, @"AMD\DxcCache", Days: 1),
        new("WinCore.Amd", local, @"AMD\GLCache", Days: 1),
        new("WinCore.Amd", local, @"AMD\VkCache", Days: 1),
        new("WinCore.Npm", local, @"npm-cache\_cacache", Days: 1),
        new("WinCore.Pip", local, @"pip\cache\http-v2", Days: 1),
        new("WinCore.Pip", local, @"pip\cache\http", Days: 1),
        new("WinCore.NuGet", local, @"NuGet\v3-cache", Days: 1),
    ];

    internal static void Resolve(CleanupRule rule, CancellationToken token, Action<CleanupRoot> accept, Action partial)
    {
        if (!StartupPaths.TryLocalPath(rule.Anchor, out var anchor)) { partial(); return; }
        var path = Path.Combine(anchor, rule.RelativePath);
        if (!StartupPaths.IsWithin(path, anchor) || CleanupFiles.IsProtected(path)) { partial(); return; }
        if (rule.ProfileCache is null) { Add(path); return; }

        try
        {
            using var pins = new WindowsStartupFileMove.DirectoryPins();
            pins.Pin(path, forCleanup: true);
            if (!Uninstaller.ResidueFileGuard.IsPlainDirectory(path)) { partial(); return; }
            var visited = 0;
            foreach (var entry in new DirectoryInfo(path).EnumerateDirectories("*", CleanupFiles.Entries))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > 1024) { partial(); break; }
                if ((entry.Attributes & CleanupFiles.IndirectAttributes) != 0) continue;
                if (rule.Category != "WinCore.Firefox" && !IsChromiumProfile(entry.Name)) continue;
                Add(Path.Combine(entry.FullName, rule.ProfileCache));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 2 or 3) { }
        catch (Exception ex) when (ex is not (FileNotFoundException or DirectoryNotFoundException) && StartupErrors.Expected(ex))
        { partial(); }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }

        void Add(string root) => accept(new(rule.Category, root, rule.Extension, TimeSpan.FromDays(rule.Days),
            rule.Recursive, rule.FilePrefix));
    }

    private static bool IsChromiumProfile(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase) && name.Length > 8 && name[8..].All(char.IsAsciiDigit));
}
