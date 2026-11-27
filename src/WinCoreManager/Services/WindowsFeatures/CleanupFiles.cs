// Enumeration and exclusions adapted from BitBroom's Engine/FileSystemWalker.cs and Engine/PathGuard.cs (MIT).
// Copyright (c) 2026 BitBroom Contributors. See distribution/Licencas/BitBroom.LICENSE.txt.

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using WinCoreManager.Localization;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Services.WindowsFeatures;

internal sealed record CleanupRoot(string Category, string Path, string Extension, TimeSpan MinimumAge,
    bool IncludeSubfolders, string? FilePrefix = null);
internal sealed record CleanupFileScan(CleanupFiles Owner, IReadOnlyList<CleanupRoot> Roots, IReadOnlyList<string> Preview,
    Dictionary<Guid, CleanupFileIdentity> Targets, int Count);
internal sealed record CleanupCategoryResult(string Category, int Removed, int Skipped, int Failed, int Remaining,
    long Recovered, IReadOnlyList<string> Details);
internal sealed record CleanupFileResult(IReadOnlyList<CleanupCategoryResult> Categories, bool Cancelled)
{
    internal int Removed => Categories.Sum(item => item.Removed);
    internal int Skipped => Categories.Sum(item => item.Skipped);
    internal int Failed => Categories.Sum(item => item.Failed);
    internal int Remaining => Categories.Sum(item => item.Remaining);
    internal long Recovered => Categories.Sum(item => item.Recovered);
}
internal readonly record struct CleanupProgress(int Completed, int Total);

internal sealed class CleanupFiles
{
    internal const string UserTemp = "WinCore.UserTemp";
    internal const string UserReports = "WinCore.UserReports";
    internal const string UserDumps = "WinCore.UserDumps";
    internal const FileAttributes IndirectAttributes = FileAttributes.ReparsePoint | FileAttributes.Offline
        | (FileAttributes)0x00040000 | (FileAttributes)0x00400000; // RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS
    private const int MaxTargets = 100_000;
    private const int MaxVisited = 300_000;
    private const int MaxDepth = 16;
    private const int MaxDetails = 8;
    internal static readonly EnumerationOptions Entries = new() { IgnoreInaccessible = false, AttributesToSkip = 0, BufferSize = 16384 };
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly Lazy<string[]> ProtectedPaths = new(GetProtectedPaths);
    private static readonly HashSet<string> ProtectedNames = new(Paths)
    {
        "Cookies", "Login Data", "History", "Bookmarks", "Preferences", "Secure Preferences", "Local State",
        "Web Data", "Sessions", "Session Storage", "Local Storage", "IndexedDB", "Service Worker", "Network",
        "WinSxS", "DriverStore", "System Volume Information", ".git", ".venv", "node_modules",
        "Documents", "Downloads", "Desktop",
    };
    private static readonly HashSet<string> DatabaseExtensions = new(Paths)
    {
        ".db", ".db-wal", ".db-shm", ".sqlite", ".sqlite3", ".sqlite-wal", ".sqlite-shm", ".ldb", ".kdbx",
    };
    private readonly IReadOnlyList<CleanupRoot> _explicitRoots;
    private readonly IReadOnlyList<CleanupRule> _rules;
    private readonly int _previewLimit;
    private readonly Func<string, bool> _isRunning;

    internal CleanupFiles(IReadOnlyList<CleanupRoot> roots, int previewLimit = 24)
        : this(roots, [], previewLimit, IsProcessRunning) { }

    private CleanupFiles(IReadOnlyList<CleanupRoot> roots, IReadOnlyList<CleanupRule> rules, int previewLimit,
        Func<string, bool> isRunning)
    {
        _explicitRoots = roots.ToArray();
        _rules = rules.ToArray();
        _previewLimit = Math.Clamp(previewLimit, 1, 100);
        _isRunning = isRunning;
    }

    internal static CleanupFiles ForCurrentUser() => ForUserPaths(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    internal static CleanupFiles ForUserPaths(string local, string roaming, int previewLimit = 24,
        Func<string, bool>? isRunning = null) =>
        new([], CleanupCatalog.Rules(local, roaming), previewLimit, isRunning ?? IsProcessRunning);

    internal IReadOnlyList<CleanupCategory> Scan(CancellationToken token, IProgress<CleanupProgress>? progress = null)
    {
        var definitions = CleanupCatalog.Definitions.Where(item => _rules.Any(rule => rule.Category == item.Id)
            || _explicitRoots.Any(root => root.Category == item.Id)).ToArray();
        var byCategory = definitions.ToDictionary(item => item.Id, _ => new List<CleanupRoot>(), StringComparer.Ordinal);
        var partial = new HashSet<string>(StringComparer.Ordinal);
        var accepted = new List<CleanupRoot>();
        foreach (var root in _explicitRoots) Add(root);
        foreach (var rule in _rules)
        {
            token.ThrowIfCancellationRequested();
            CleanupCatalog.Resolve(rule, token, Add, () => partial.Add(rule.Category));
        }

        var completed = 0;
        var rows = new CleanupCategory[definitions.Length];
        var now = DateTime.UtcNow;
        Parallel.For(0, definitions.Length, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = 2 }, index =>
        {
            var definition = definitions[index];
            rows[index] = ScanCategory(definition, byCategory[definition.Id], partial.Contains(definition.Id), now, token);
            progress?.Report(new(Interlocked.Increment(ref completed), definitions.Length));
        });
        return rows;

        void Add(CleanupRoot root)
        {
            if (!byCategory.TryGetValue(root.Category, out var list)) return;
            if (!StartupPaths.TryLocalPath(root.Path, out var path) || !Paths.Equals(path, root.Path) ||
                IsProtected(path) || accepted.Any(item => Paths.Equals(path, item.Path)
                    || StartupPaths.IsWithin(path, item.Path) || StartupPaths.IsWithin(item.Path, path)))
            {
                partial.Add(root.Category);
                return;
            }
            accepted.Add(root);
            list.Add(root);
        }
    }

    private CleanupCategory ScanCategory(CleanupDefinition definition, IReadOnlyList<CleanupRoot> roots, bool partial,
        DateTime now, CancellationToken token)
    {
        var targets = new Dictionary<Guid, CleanupFileIdentity>();
        var preview = new List<string>();
        var stats = new WalkStats { Partial = partial };
        long bytes = 0;
        foreach (var root in roots)
        {
            var cutoff = (now - root.MinimumAge).ToFileTimeUtc();
            Walk(root, stats, token, entry =>
            {
                if (!IsOrdinaryFile(entry.Attributes)) return;
                if (root.MinimumAge > TimeSpan.Zero && Math.Max(entry.CreationTimeUtc.ToFileTimeUtc(),
                    entry.LastWriteTimeUtc.ToFileTimeUtc()) > cutoff) return;

                using var handle = ResidueFileGuard.OpenCleanupFile(entry.FullName, deleting: false);
                if (handle.IsInvalid)
                {
                    if (Marshal.GetLastPInvokeError() is not (2 or 3)) stats.Partial = true;
                    return;
                }
                if (ResidueFileGuard.ReadCleanupIdentity(handle, entry.FullName) is not { } identity ||
                    Math.Max(identity.Creation, identity.LastWrite) > cutoff) return;
                if (!targets.TryAdd(PathKey(entry.FullName), identity)) return;
                bytes += identity.Allocated;
                if (preview.Count < _previewLimit) preview.Add(entry.FullName);
                if (targets.Count == MaxTargets) stats.Limit = true;
            });
        }
        var active = definition.Process is { } process && targets.Count != 0 && _isRunning(process);
        var availability = active ? "Cleanup.AppRunning" : stats.Partial || stats.Limit ? "Message.CleanupPartial"
            : targets.Count > 0 ? "Message.CleanupDirect" : stats.Detected ? "Cleanup.NoEligible" : "Cleanup.NotDetected";
        var measured = targets.Count > 0 || !(stats.Partial || stats.Limit);
        return new(definition.Id, LocalizationService.Source(definition.NameKey), LocalizationService.Source(definition.DescriptionKey),
            measured ? bytes : null, "", LocalizationService.Source("Message.AdministratorApprovalNotRequired"),
            targets.Count > 0 && !active, LocalizationService.Source(availability), measured ? targets.Count : null)
        { FileScan = new(this, roots, preview.AsReadOnly(), targets, targets.Count) };
    }

    internal CleanupFileResult Delete(IReadOnlyList<CleanupCategory> categories, CancellationToken token,
        IProgress<CleanupProgress>? progress)
    {
        var total = categories.Sum(item => item.FileScan!.Count);
        var completed = 0;
        var results = new List<CleanupCategoryResult>();
        foreach (var category in categories)
        {
            var scan = category.FileScan!;
            var definition = CleanupCatalog.Definitions.Single(item => item.Id == category.Id);
            var pending = scan.Targets;
            var details = new List<string>();
            var removed = 0;
            var skipped = 0;
            var failed = 0;
            long recovered = 0;
            var protectedByProcess = false;
            var nextProcessCheck = 0L;
            var stats = new WalkStats();

            try
            {
                foreach (var root in scan.Roots)
                {
                    token.ThrowIfCancellationRequested();
                    if (pending.Count == 0 || AppRunning()) break;
                    Walk(root, stats, token, DeleteEntry, () => pending.Count == 0 || protectedByProcess);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }

            var remaining = token.IsCancellationRequested ? pending.Count : 0;
            if (pending.Count > remaining)
            {
                skipped += pending.Count;
                completed += pending.Count;
                Detail(LocalizationService.Source(protectedByProcess ? "Cleanup.AppRunning" : "Cleanup.UnavailableAfterScan", pending.Count));
            }
            pending.Clear();
            results.Add(new(category.Name, removed, skipped, failed, remaining, recovered, details));
            progress?.Report(new(completed, total));

            void DeleteEntry(FileSystemInfo entry)
            {
                token.ThrowIfCancellationRequested();
                if (AppRunning() || !pending.Remove(PathKey(entry.FullName), out var expected)) return;
                var outcome = DeleteFile(entry, expected, token);
                switch (outcome)
                {
                    case 0: removed++; recovered += expected.Allocated; break;
                    case 2 or 3 or 32 or 33 or 303 or -1: skipped++; break;
                    default: failed++; break;
                }

                if (outcome != 0)
                    Detail(LocalizationService.Source(outcome switch
                    {
                        2 or 3 or 303 => "Cleanup.MissingFile",
                        32 or 33 => "Cleanup.LockedFile",
                        -1 => "Cleanup.GuardRefused",
                        _ => "Cleanup.FileError",
                    }, entry.FullName, outcome));
                if (++completed % 32 == 0) progress?.Report(new(completed, total));
            }

            bool AppRunning()
            {
                if (definition.Process is not { } process) return false;
                if (protectedByProcess || Stopwatch.GetTimestamp() < nextProcessCheck) return protectedByProcess;
                nextProcessCheck = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 4;
                return protectedByProcess = _isRunning(process);
            }

            void Detail(string value)
            {
                if (details.Count < MaxDetails) details.Add(value);
            }
        }
        return new(results, token.IsCancellationRequested);
    }

    private static int DeleteFile(FileSystemInfo entry, CleanupFileIdentity expected, CancellationToken token)
    {
        if (!IsOrdinaryFile(entry.Attributes)) return -1;
        using var handle = ResidueFileGuard.OpenCleanupFile(entry.FullName, deleting: true);
        if (handle.IsInvalid) return NativeError();
        if (ResidueFileGuard.ReadCleanupIdentity(handle, entry.FullName) != expected || token.IsCancellationRequested)
            return -1;
        return ResidueFileGuard.DeleteCleanupFile(handle) ? 0 : NativeError();

        static int NativeError() => Marshal.GetLastPInvokeError() is > 0 and var error ? error : -1;
    }

    private static void Walk(CleanupRoot root, WalkStats stats, CancellationToken token, Action<FileSystemInfo> file,
        Func<bool>? finished = null)
    {
        if (stats.Limit || finished?.Invoke() == true) return;
        token.ThrowIfCancellationRequested();
        try
        {
            if (IsProtected(root.Path) || !new DriveInfo(root.Path[..3]).DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
            {
                stats.Partial = true;
                return;
            }
            using var pins = new WindowsStartupFileMove.DirectoryPins();
            pins.Pin(root.Path, forCleanup: true);
            if (!ResidueFileGuard.IsPlainDirectory(root.Path)) { stats.Partial = true; return; }
            stats.Detected = true;
            Visit(root.Path, 0);
        }
        catch (Exception ex) when (IsMissing(ex)) { }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { stats.Partial = true; }

        void Visit(string directory, int depth)
        {
            try
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", Entries))
                {
                    token.ThrowIfCancellationRequested();
                    if (stats.Limit || finished?.Invoke() == true) break;
                    if (++stats.Visited > MaxVisited) { stats.Limit = true; break; }
                    if (ProtectedNames.Contains(entry.Name) || IsProtected(entry.FullName)) continue;
                    if ((entry.Attributes & FileAttributes.Directory) == 0)
                    {
                        if (Matches(root, entry.Name)) file(entry);
                        continue;
                    }
                    if (!root.IncludeSubfolders || (entry.Attributes & IndirectAttributes) != 0) continue;
                    if (depth == MaxDepth) { stats.Partial = true; continue; }
                    try
                    {
                        using var pin = WindowsStartupFileMove.DirectoryPins.PinCleanupDirectory(entry.FullName);
                        Visit(entry.FullName, depth + 1);
                    }
                    catch (Exception ex) when (IsMissing(ex)) { }
                    catch (Exception ex) when (StartupErrors.Expected(ex)) { stats.Partial = true; }
                }
            }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { stats.Partial = true; }
        }
    }

    private static bool Matches(CleanupRoot root, string name)
    {
        if (name.Contains(':') || name.EndsWith('.') || name.EndsWith(' ') ||
            (root.FilePrefix is { } prefix && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
        var extension = Path.GetExtension(name);
        if (root.Extension.Length != 0 && !Paths.Equals(root.Extension, extension)) return false;
        return !DatabaseExtensions.Contains(extension) ||
            root is { Category: CleanupCatalog.Thumbnails, FilePrefix: "thumbcache_", Extension: ".db" };
    }

    internal static bool IsProtected(string path) => ProtectedPaths.Value.Any(root => Paths.Equals(root, path)
        || StartupPaths.IsWithin(path, root));

    private static string[] GetProtectedPaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = "";
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
        var result = SHGetKnownFolderPath(in id, 0x4000, 0, out var value); // KF_FLAG_DONT_VERIFY
        try { if (result == 0) downloads = Marshal.PtrToStringUni(value) ?? ""; }
        finally { Marshal.FreeCoTaskMem(value); }

        return new[] { downloads, profile.Length == 0 ? "" : Path.Combine(profile, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveCommercial"),
            Environment.GetEnvironmentVariable("OneDriveConsumer") }
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.TrimEndingDirectorySeparator(path!)).ToArray();
    }

    private static bool IsOrdinaryFile(FileAttributes attributes) =>
        (attributes & (IndirectAttributes | FileAttributes.System | FileAttributes.ReadOnly)) == 0;

    private static bool IsMissing(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException
        or Win32Exception { NativeErrorCode: 2 or 3 };

    private static Guid PathKey(string path)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(path.AsSpan()), hash);
        return new Guid(hash[..16]);
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return true; }
    }

    [DllImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, nint token, out nint path);

    private sealed class WalkStats
    {
        internal bool Partial, Detected, Limit;
        internal int Visited;
    }
}
