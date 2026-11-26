using WinCoreManager.Localization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinCoreManager.Services.Startup;

internal sealed class StartupRunBackups(string root)
{
    private const int MaxJournalBytes = 64512;
    private sealed record Journal(int Version, StartupRunSnapshot Snapshot, string Digest);

    public bool IsAvailable()
    {
        if (!StartupPaths.TryLocalPath(root, out var normalized)) return false;
        for (string? path = normalized; path is not null; path = Path.GetDirectoryName(path))
        {
            try { _ = File.GetAttributes(path); return StartupPaths.IsSafeDirectory(path); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { return false; }
        }
        return false;
    }

    public Dictionary<string, StartupRunSnapshot?> Read(List<string> warnings)
    {
        var snapshots = new Dictionary<string, StartupRunSnapshot?>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return snapshots;
        using var pins = new WindowsStartupFileMove.DirectoryPins();
        pins.Pin(root);
        foreach (var file in Directory.EnumerateFiles(root, "*.json"))
        {
            var key = Path.GetFileNameWithoutExtension(file);
            try
            {
                if (!StartupPaths.IsSafeFile(file)) throw new InvalidDataException();
                using var stream = WindowsStartupFileMove.OpenVerifiedFileForRead(file);
                if (stream.Length > MaxJournalBytes) throw new InvalidDataException();
                var journal = JsonSerializer.Deserialize<Journal>(stream);
                if (journal?.Snapshot?.Value?.Bytes is null || journal.Version != 1
                    || !StartupRunSource.ValidName(journal.Snapshot.Value.Name)
                    || journal.Snapshot.Value.Command is null || string.IsNullOrWhiteSpace(journal.Snapshot.Approval)
                    || key != Key(journal.Snapshot.Value.Name) || journal.Digest != journal.Snapshot.Fingerprint())
                    throw new InvalidDataException();
                snapshots.Add(journal.Snapshot.Value.Name, journal.Snapshot);
            }
            catch (Exception ex) when (StartupErrors.Expected(ex) || ex is InvalidDataException)
            {
                snapshots["#" + key] = null;
                warnings.Add(LocalizationService.Source("Message.AnHKCURunBackupIsUnreadableOrCorrupt"));
            }
        }

        return snapshots;
    }

    public IDisposable Lock()
    {
        var pins = new WindowsStartupFileMove.DirectoryPins();
        try
        {
            pins.Pin(root, create: true);
            return new JournalLock(pins, WindowsStartupFileMove.OpenExclusiveLock(Path.Combine(root, "changes.lock")));
        }
        catch
        {
            pins.Dispose();
            throw;
        }
    }

    public StartupRunSnapshot Prepare(StartupRunSnapshot snapshot)
    {
        var file = Path.Combine(root, Key(snapshot.Value.Name) + ".json");
        StartupJournalFile.Save(file, new Journal(1, snapshot, snapshot.Fingerprint()), replace: false);
        return snapshot;
    }

    public bool HasCorruption(string name, Dictionary<string, StartupRunSnapshot?> snapshots) =>
        snapshots.ContainsKey("#" + Key(name));

    public void Complete(StartupRunSnapshot snapshot) =>
        File.Delete(Path.Combine(root, Key(snapshot.Value.Name) + ".json"));

    private static string Key(string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Unicode.GetBytes(name.ToUpperInvariant())));

    private sealed class JournalLock(IDisposable pins, IDisposable file) : IDisposable
    {
        public void Dispose()
        {
            file.Dispose();
            pins.Dispose();
        }
    }

}

