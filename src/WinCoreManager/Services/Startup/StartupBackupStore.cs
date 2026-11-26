using WinCoreManager.Localization;
using System.IO;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.Startup;

internal sealed record StartupSnapshot(Guid Id, string Original, string Digest, string Approval, DateTimeOffset CreatedAt);
internal sealed record StartupBackup(StartupSnapshot Snapshot, string FilePath);

internal sealed class StartupBackupStore(string root)
{
    public bool CanStore(string folder, string common = "")
    {
        if (!StartupPaths.TryLocalPath(root, out var safeRoot) || !StartupPaths.IsSafeDirectory(folder)
            || !safeRoot[..3].Equals(folder[..3], StringComparison.OrdinalIgnoreCase)
            || Overlaps(safeRoot, folder) || (common.Length != 0 && Overlaps(safeRoot, common)))
            return false;

        for (string? p = safeRoot; p is not null; p = Path.GetDirectoryName(p))
        {
            try
            {
                _ = File.GetAttributes(p);
                return StartupPaths.IsSafeDirectory(p);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { return false; }
        }
        return false;
    }

    private static bool Overlaps(string first, string second) =>
        first.Equals(second, StringComparison.OrdinalIgnoreCase)
        || StartupPaths.IsWithin(first, second) || StartupPaths.IsWithin(second, first);

    public IReadOnlyList<StartupBackup> Read(List<string> warnings, CancellationToken token)
    {
        try { _ = File.GetAttributes(root); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return []; }
        if (!StartupPaths.IsSafeDirectory(root))
        {
            warnings.Add(LocalizationService.Source("Message.StartupBackupsCouldNotVerifyTheLocation"));
            return [];
        }

        List<StartupBackup> backups = [];
        foreach (var path in Directory.EnumerateFiles(root, "*.lnk.bin"))
        {
            token.ThrowIfCancellationRequested();
            var key = Path.GetFileName(path)[..^8];
            try
            {
                var manifest = Path.Combine(root, $"{key}.json");
                if (!Guid.TryParseExact(key, "N", out var id) || !StartupPaths.IsSafeFile(path)
                    || !StartupPaths.IsSafeFile(manifest) || new FileInfo(manifest).Length > 16_384)
                    throw new InvalidDataException();

                using var stream = File.OpenRead(manifest);
                var snapshot = JsonSerializer.Deserialize<StartupSnapshot>(stream);
                if (snapshot is null || snapshot.Id != id || snapshot.Digest?.Length != 64
                    || string.IsNullOrEmpty(snapshot.Approval) || string.IsNullOrEmpty(snapshot.Original))
                    throw new InvalidDataException();
                backups.Add(new(snapshot, path));
            }
            catch (Exception ex) when (StartupErrors.Expected(ex) || ex is InvalidDataException)
            { warnings.Add(LocalizationService.Source("Message.StartupBackupsAFileNeedsReview")); }
        }
        return backups;
    }

    public IDisposable Lock(string folder)
    {
        if (!CanStore(folder)) throw new IOException(LocalizationService.Source("Message.BackupLocationUnavailable"));
        var pins = new WindowsStartupFileMove.DirectoryPins();
        try
        {
            pins.Pin(root, create: true);
            return new BackupLock(WindowsStartupFileMove.OpenExclusiveLock(Path.Combine(root, "changes.lock")), pins);
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    public StartupBackup Prepare(string original, string digest, string approval)
    {
        var snapshot = new StartupSnapshot(Guid.NewGuid(), original, digest, approval, DateTimeOffset.UtcNow);
        var prefix = Path.Combine(root, snapshot.Id.ToString("N"));
        using (var stream = new FileStream(prefix + ".json", FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, snapshot);
            stream.Flush(flushToDisk: true);
        }
        return new(snapshot, prefix + ".lnk.bin");
    }

    public void RemoveEmptyBackup(StartupBackup backup)
    {
        var prefix = Path.Combine(root, backup.Snapshot.Id.ToString("N"));
        try
        {
            _ = File.GetAttributes(prefix + ".lnk.bin");
        }
        catch (FileNotFoundException)
        {
            File.Delete(prefix + ".json");
        }
    }

    private sealed class BackupLock(SafeFileHandle file, WindowsStartupFileMove.DirectoryPins pins) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            pins.Dispose();
        }
    }
}
