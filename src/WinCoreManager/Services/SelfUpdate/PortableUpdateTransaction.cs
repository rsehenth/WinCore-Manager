using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WinCoreManager.Services.SelfUpdate;

internal sealed class PortableUpdateTransaction(string root, string work)
{
    public const string IncomingDirectory = "incoming";
    public const string BackupDirectory = "previous";
    public const string PendingFile = "pending";
    internal const string CompleteFile = "complete";
    internal const string RolledBackFile = "rolled-back";

    public string Staging => Path.Combine(work, IncomingDirectory);
    public string Backup => Path.Combine(work, BackupDirectory);
    public bool Pending => HasMarker(work, PendingFile);

    internal static IReadOnlyList<string> FindPending(string root, string? boundary = null)
    {
        var directory = Path.Combine(root, PortablePackage.WorkDirectory);
        PortablePackage.CheckPath(directory, boundary);
        try
        {
            return Directory.EnumerateDirectories(directory)
                .Where(path => Guid.TryParseExact(Path.GetFileName(path), "N", out _))
                .Where(path => HasMarker(path, PendingFile, boundary))
                .Take(2).ToArray();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    internal static bool HasMarker(string directory, string name, string? boundary = null)
    {
        var marker = Path.Combine(directory, name);
        PortablePackage.CheckPath(marker, boundary);
        try
        {
            var attributes = File.GetAttributes(marker);
            if ((attributes & FileAttributes.Directory) != 0 || new FileInfo(marker).Length != 0)
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    internal void VerifyRecoveryEvidence(string publisherKey)
    {
        VerifyReleaseArchive(publisherKey);
        PortablePackage.VerifyFiles(Staging, PortablePackage.ReadManifest(Staging));
    }

    private void VerifyReleaseArchive(string publisherKey, string? expectedManifest = null)
    {
        var zip = Path.Combine(work, "release.zip");
        var signature = zip + ".sig";
        PortablePackage.CheckPath(zip);
        PortablePackage.CheckPath(signature);
        using var input = PortableFileSystem.OpenRead(zip);
        using var proof = PortableFileSystem.OpenRead(signature);
        ReleaseTrust.Verify(input, proof, publisherKey);
        input.Position = 0;
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var manifest = archive.GetEntry(PortablePackage.Manifest);
        if (manifest is null || manifest.Length is <= 0 or > 1024 * 1024)
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }
        using var signedManifest = manifest.Open();
        if (expectedManifest is null)
        {
            using var stagedManifest = PortableFileSystem.OpenRead(PortablePackage.PayloadPath(Staging, PortablePackage.Manifest));
            expectedManifest = Convert.ToHexString(SHA256.HashData(stagedManifest));
        }
        if (!DigestMatches(signedManifest, expectedManifest))
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }
    }

    internal bool InstalledManifestMatchesIncoming() => FilesEqual(
        PortablePackage.PayloadPath(root, PortablePackage.Manifest), PortablePackage.PayloadPath(Staging, PortablePackage.Manifest));

    internal bool InstalledManifestMatchesBackup() => FilesEqual(
        PortablePackage.PayloadPath(root, PortablePackage.Manifest), PortablePackage.PayloadPath(Backup, PortablePackage.Manifest));

    public void Apply(Action? afterReplacement = null, string? publisherKey = null)
    {
        PortablePackage.CheckPath(work);
        if (Pending || Directory.Exists(Backup))
        {
            throw new UpdateException(UpdateState.Failed);
        }

        using var paths = new PortableFileSystem();
        paths.PinDirectory(work);
        paths.PinDirectory(Staging);
        var old = publisherKey is null ? null : PortablePackage.ReadSignedManifest(root, publisherKey);
        var incoming = publisherKey is null ? null : PortablePackage.ReadSignedManifest(Staging, publisherKey);
        if (incoming is not null) VerifyReleaseArchive(publisherKey!, incoming.ManifestDigest);
        var oldFiles = old?.Files ?? PortablePackage.ReadManifest(root);
        var newFiles = incoming?.Files ?? PortablePackage.ReadManifest(Staging);
        PinPayloadParents(paths, root, oldFiles.Keys);
        PinPayloadParents(paths, Staging, newFiles.Keys);
        PortablePackage.VerifyFiles(root, oldFiles);
        PortablePackage.VerifyFiles(Staging, newFiles);
        var oldBytes = oldFiles.Keys.Sum(path => new FileInfo(PortablePackage.PayloadPath(root, path)).Length);
        var largestNewFile = newFiles.Keys.Max(path => new FileInfo(PortablePackage.PayloadPath(Staging, path)).Length);
        PortablePackage.RequireSpace(root, checked(oldBytes + largestNewFile + 2 * 1024 * 1024));
        foreach (var name in newFiles.Keys.Except(oldFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var destination = PortablePackage.PayloadPath(root, name);
            if (File.Exists(destination) || Directory.Exists(destination))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
        }

        paths.PinDirectory(Backup, create: true);
        foreach (var (name, digest) in old?.WithMetadata() ??
            oldFiles.Append(new(PortablePackage.Manifest, null!)))
        {
            var destination = PortablePackage.PayloadPath(Backup, name);
            paths.PinDirectory(Path.GetDirectoryName(destination)!, create: true);
            DurableCopy(PortablePackage.PayloadPath(root, name), destination, digest, paths);
        }
        PortablePackage.VerifyFiles(Backup, oldFiles);
        if (old is not null)
            RecoveryAuthorization.Write(paths, root, work, old, incoming!);
        WriteMarker(paths, PendingFile);

        try
        {
            foreach (var (name, digest) in newFiles)
            {
                if (incoming is null) ReplaceFrom(Staging, name);
                else InstallEntry(paths, Staging, name, digest, oldFiles.GetValueOrDefault(name), "previous");
                afterReplacement?.Invoke();
            }

            foreach (var name in oldFiles.Keys.Except(newFiles.Keys, StringComparer.OrdinalIgnoreCase))
            {
                var obsolete = PortablePackage.PayloadPath(root, name);
                if (incoming is null) RetrySharingViolation(() => File.Delete(obsolete));
                else PreserveEntry(paths, obsolete, oldFiles[name], "previous");
            }

            if (incoming is not null)
            {
                InstallEntry(paths, Staging, PortablePackage.ManifestSignature, incoming.SignatureDigest, old!.SignatureDigest, "previous");
                InstallEntry(paths, Staging, PortablePackage.Manifest, incoming.ManifestDigest, old.ManifestDigest, "previous");
            }
            else ReplaceFrom(Staging, PortablePackage.Manifest);
            PortablePackage.VerifyFiles(root, newFiles);
        }
        catch
        {
            if (publisherKey is null) Rollback();
            else RollbackAuthenticated(publisherKey);
            throw;
        }
    }

    public void Complete()
    {
        using var paths = new PortableFileSystem();
        paths.PinDirectory(work);
        FinalizeMarker(paths, CompleteFile);
    }

    internal void FinishVerifiedUpdate(Action verifyStartup, string? publisherKey = null)
    {
        try
        {
            verifyStartup();
        }
        catch
        {
            if (publisherKey is null) Rollback();
            else RollbackAuthenticated(publisherKey);
            throw;
        }
        Complete();
    }

    public void Rollback()
    {
        if (!Pending)
        {
            return;
        }

        var oldFiles = PortablePackage.ReadManifest(Backup);
        var newFiles = PortablePackage.ReadManifest(Staging);
        PortablePackage.VerifyFiles(Backup, oldFiles);
        if (!InstalledManifestMatchesBackup() && !InstalledManifestMatchesIncoming())
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        // Check every target before modifying any. Unknown content remains where it was found.
        foreach (var name in oldFiles.Keys.Union(newFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var path = PortablePackage.PayloadPath(root, name);
            if (Directory.Exists(path) || (File.Exists(path) &&
                !MatchesEitherDigest(path, oldFiles.GetValueOrDefault(name), newFiles.GetValueOrDefault(name))))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
        }
        foreach (var name in newFiles.Keys.Except(oldFiles.Keys, StringComparer.OrdinalIgnoreCase))
        {
            var path = PortablePackage.PayloadPath(root, name);
            RetrySharingViolation(() => File.Delete(path));
        }
        foreach (var (name, digest) in oldFiles)
        {
            var path = PortablePackage.PayloadPath(root, name);
            if (File.Exists(path))
            {
                using var existing = File.OpenRead(path);
                if (CryptographicOperations.FixedTimeEquals(SHA256.HashData(existing), Convert.FromHexString(digest)))
                {
                    continue;
                }
            }
            ReplaceFrom(Backup, name);
        }

        ReplaceFrom(Backup, PortablePackage.Manifest);
        PortablePackage.VerifyFiles(root, oldFiles);
        var rolledBack = Path.Combine(work, RolledBackFile);
        PortablePackage.CheckPath(rolledBack);
        RetrySharingViolation(() => File.Move(Path.Combine(work, PendingFile), rolledBack));
    }

    internal void RollbackAuthenticated(string publisherKey, Action<long>? requireSpace = null, Action? afterReplacement = null)
    {
        if (!Pending) return;
        if (HasMarker(work, CompleteFile) || HasMarker(work, RolledBackFile))
            throw new UpdateException(UpdateState.VerificationFailed);

        using var paths = new PortableFileSystem();
        paths.PinDirectory(work);
        paths.PinDirectory(Backup);
        paths.PinDirectory(Staging);
        var previous = PortablePackage.ReadSignedManifest(Backup, publisherKey);
        var incoming = PortablePackage.ReadSignedManifest(Staging, publisherKey);
        RecoveryAuthorization.Verify(root, work, previous, incoming);
        PinPayloadParents(paths, Backup, previous.Files.Keys);
        PinPayloadParents(paths, Staging, incoming.Files.Keys);
        VerifyReleaseArchive(publisherKey, incoming.ManifestDigest);
        PortablePackage.VerifyFiles(Backup, previous.Files);
        PortablePackage.VerifyFiles(Staging, incoming.Files);
        PortablePackage.VerifyAvailableSignatures(Backup);

        var oldEntries = previous.WithMetadata().ToDictionary(StringComparer.OrdinalIgnoreCase);
        var newEntries = incoming.WithMetadata().ToDictionary(StringComparer.OrdinalIgnoreCase);
        var entries = oldEntries.Keys.Union(newEntries.Keys, StringComparer.OrdinalIgnoreCase).ToArray();
        PinPayloadParents(paths, root, entries, allowMissing: true);
        var manifest = ReadDigestIfPresent(PortablePackage.PayloadPath(root, PortablePackage.Manifest));
        if (manifest is not null && manifest != previous.ManifestDigest && manifest != incoming.ManifestDigest)
            throw new UpdateException(UpdateState.VerificationFailed);

        var restore = new List<string>();
        var remove = new List<string>();
        long swapBytes = 0;
        foreach (var name in entries)
        {
            var target = PortablePackage.PayloadPath(root, name);
            var oldDigest = oldEntries.GetValueOrDefault(name);
            var newDigest = newEntries.GetValueOrDefault(name);
            var actual = ReadDigestIfPresent(target);
            if (actual is not null && actual != oldDigest && actual != newDigest && !CanPreserveDamagedBinary(name))
                throw new UpdateException(UpdateState.VerificationFailed);
            if (actual != oldDigest)
            {
                if (oldDigest is null) remove.Add(name);
                else
                {
                    restore.Add(name);
                    swapBytes = Math.Max(swapBytes, new FileInfo(PortablePackage.PayloadPath(Backup, name)).Length);
                }
            }
        }

        if (swapBytes > 0)
            (requireSpace ?? (bytes => PortablePackage.RequireSpace(root, bytes)))(swapBytes);

        foreach (var name in restore)
        {
            var target = PortablePackage.PayloadPath(root, name);
            var actual = ReadDigestIfPresent(target);
            if (actual == oldEntries[name]) continue;
            var damaged = actual is not null && actual != newEntries.GetValueOrDefault(name);
            if (damaged && !CanPreserveDamagedBinary(name))
                throw new UpdateException(UpdateState.VerificationFailed);
            InstallEntry(paths, Backup, name, oldEntries[name], actual,
                damaged ? name == PortablePackage.Executable ? "main" : "damaged-binary" : "incoming");
            afterReplacement?.Invoke();
        }
        foreach (var name in remove)
        {
            var target = PortablePackage.PayloadPath(root, name);
            var actual = ReadDigestIfPresent(target);
            if (actual is null) continue;
            if (actual != newEntries[name]) throw new UpdateException(UpdateState.VerificationFailed);
            PreserveEntry(paths, target, actual, "incoming");
        }

        var installed = PortablePackage.ReadSignedManifest(root, publisherKey);
        if (installed.ManifestDigest != previous.ManifestDigest || installed.SignatureDigest != previous.SignatureDigest)
            throw new UpdateException(UpdateState.VerificationFailed);
        PortablePackage.VerifyFiles(root, installed.Files);
        FinalizeMarker(paths, RolledBackFile);
    }

    private static bool CanPreserveDamagedBinary(string name)
        => name is PortablePackage.Executable or PortablePackage.Helper or "WinCoreManager.dll";

    private void FinalizeMarker(PortableFileSystem paths, string finalName)
    {
        RetrySharingViolation(() =>
        {
            using var pending = paths.OpenForRename(Path.Combine(work, PendingFile));
            if (pending.Length != 0) throw new UpdateException(UpdateState.VerificationFailed);
            paths.Rename(pending, Path.Combine(work, finalName));
        });
    }

    private void InstallEntry(PortableFileSystem paths, string from, string name, string digest, string? currentDigest, string kind)
    {
        var source = PortablePackage.PayloadPath(from, name);
        var target = PortablePackage.PayloadPath(root, name);
        if (currentDigest == digest && ReadDigestIfPresent(target) == digest) return;
        paths.PinDirectory(Path.GetDirectoryName(target)!, create: true);
        var temp = Path.Combine(work, $"swap-{Guid.NewGuid():N}.tmp");
        using var replacement = paths.CreateNew(temp, forRename: true);
        try
        {
            CopyVerified(source, replacement, digest);
            if (currentDigest is not null) PreserveEntry(paths, target, currentDigest, kind);
            RetrySharingViolation(() => paths.Rename(replacement, target));
        }
        catch
        {
            PortableFileSystem.DiscardScratch(replacement);
            throw;
        }
    }

    private void PreserveEntry(PortableFileSystem paths, string target, string expected, string kind)
    {
        var preserved = Path.Combine(work, $"preserved-{kind}-{Guid.NewGuid():N}.bin");
        RetrySharingViolation(() =>
        {
            using var source = paths.OpenForRename(target);
            if (source.Length > PortablePackage.MaximumExpandedBytes || !DigestMatches(source, expected))
                throw new UpdateException(UpdateState.VerificationFailed);
            paths.Rename(source, preserved);
        });
    }

    private static void PinPayloadParents(PortableFileSystem paths, string directory, IEnumerable<string> files, bool allowMissing = false)
    {
        foreach (var name in files)
        {
            var parent = Path.GetDirectoryName(PortablePackage.PayloadPath(directory, name))!;
            if (!allowMissing || Directory.Exists(parent)) paths.PinDirectory(parent);
        }
    }

    private void ReplaceFrom(string from, string name)
        => RetrySharingViolation(() => ReplaceFile(from, name));

    private void ReplaceFile(string from, string name)
    {
        var source = PortablePackage.PayloadPath(from, name);
        var target = PortablePackage.PayloadPath(root, name);
        if (File.Exists(target) && FilesEqual(source, target))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temporary = Path.Combine(work, $"swap-{Guid.NewGuid():N}.tmp");
        try
        {
            DurableCopy(source, temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool MatchesEitherDigest(string path, string? first, string? second)
    {
        var digest = ReadDigestIfPresent(path);
        return digest is not null &&
            (digest.Equals(first, StringComparison.OrdinalIgnoreCase) || digest.Equals(second, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadDigestIfPresent(string path)
    {
        try
        {
            using var stream = PortableFileSystem.OpenRead(path);
            if (stream.Length > PortablePackage.MaximumExpandedBytes)
                throw new UpdateException(UpdateState.VerificationFailed);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void RetrySharingViolation(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (IOException ex) when (attempt < 4 && (ex.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(50 << attempt);
            }
        }
    }

    private static bool FilesEqual(string first, string second)
    {
        using var firstStream = PortableFileSystem.OpenRead(first);
        using var secondStream = PortableFileSystem.OpenRead(second);
        return firstStream.Length == secondStream.Length &&
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(firstStream), SHA256.HashData(secondStream));
    }

    private void WriteMarker(PortableFileSystem paths, string name)
    {
        var path = Path.Combine(work, name);
        PortablePackage.CheckPath(path);
        using var stream = paths.CreateNew(path);
        stream.Flush(flushToDisk: true);
    }

    private static void DurableCopy(string source, string destination, string? digest = null, PortableFileSystem? paths = null)
    {
        using var output = paths is null
            ? new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)
            : paths.CreateNew(destination);
        CopyVerified(source, output, digest);
    }

    private static void CopyVerified(string source, FileStream output, string? digest)
    {
        using var input = PortableFileSystem.OpenRead(source);
        if (digest is not null)
        {
            if (input.Length > PortablePackage.MaximumExpandedBytes || !DigestMatches(input, digest))
                throw new UpdateException(UpdateState.VerificationFailed);
            input.Position = 0;
        }
        input.CopyTo(output);
        output.Flush(flushToDisk: true);
    }

    private static bool DigestMatches(Stream stream, string digest) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(digest));
}

internal sealed class PortableUpdateLease : IDisposable
{
    private const string FileName = ".wincore-portable.lock";
    private readonly FileStream _lockFile;

    private PortableUpdateLease(FileStream lockFile) => _lockFile = lockFile;

    public static PortableUpdateLease? TryAcquire(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        using var paths = new PortableFileSystem();
        paths.PinDirectory(root);
        var path = Path.Combine(root, FileName);
        PortablePackage.CheckPath(path);
        FileStream lockFile;
        try
        {
            lockFile = paths.OpenExclusiveLease(path);
        }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33)
        {
            return null;
        }

        try
        {
            if (lockFile.Length != 0)
                throw new UpdateException(UpdateState.VerificationFailed);
            return new PortableUpdateLease(lockFile);
        }
        catch
        {
            lockFile.Dispose();
            throw;
        }
    }

    public void Dispose() => _lockFile.Dispose();
}
