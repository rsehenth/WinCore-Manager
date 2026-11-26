using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.SelfUpdate;

internal sealed class PortableUpdateCoordinator(GitHubReleases releases)
{
    public async Task<string> PrepareAsync(UpdateOffer offer, IProgress<double> progress, CancellationToken token)
    {
        if (Environment.IsPrivilegedProcess)
        {
            throw new UpdateException(UpdateState.CannotWrite);
        }
        if (!releases.IsConfigured || ReleaseTrust.PublisherPublicKey is not { } publisherKey)
        {
            throw new UpdateException(UpdateState.NotConfigured);
        }

        var root = PortableFileSystem.ResolveRoot(AppContext.BaseDirectory);
        var installed = PortablePackage.ReadSignedManifest(root, publisherKey).Files;
        PortablePackage.VerifyFiles(root, installed, token);
        PortablePackage.VerifyExecutable(Path.Combine(root, PortablePackage.RecoveryDirectory, PortablePackage.RecoveryHelper));
        var helperSource = PortablePackage.PayloadPath(root, PortablePackage.Helper);
        var helperDigest = Convert.FromHexString(installed[PortablePackage.Helper]);
        VerifyHelper(helperSource, helperDigest);
        var baseWork = Path.Combine(root, PortablePackage.WorkDirectory);
        PortablePackage.CheckPath(baseWork);
        if (RequiresRecovery(root))
        {
            throw new UpdateException(UpdateState.Failed);
        }

        PruneFinalized(root);
        PortablePackage.RequireSpace(root, offer.Size + new FileInfo(helperSource).Length);
        var work = Path.Combine(baseWork, Guid.NewGuid().ToString("N"));
        PortablePackage.CheckPath(work);
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "release.zip");
            await releases.DownloadAsync(offer, zip, progress, token).ConfigureAwait(false);
            var signature = zip + ".sig";
            await releases.DownloadSignatureAsync(offer, signature, token).ConfigureAwait(false);
            var preview = Path.Combine(work, "preview");
            using (var input = File.OpenRead(zip))
            using (var proof = File.OpenRead(signature))
            {
                ReleaseTrust.Verify(input, proof, publisherKey);
                PortablePackage.Extract(zip, preview, token);
            }
            PortablePackage.ReadSignedManifest(preview, publisherKey);
            PortablePackage.VerifyLayout(preview, offer.Version);
            var previousBytes = installed.Keys.Sum(name => new FileInfo(PortablePackage.PayloadPath(root, name)).Length);
            var swapBytes = PortablePackage.ReadManifest(preview).Keys.Max(name => new FileInfo(PortablePackage.PayloadPath(preview, name)).Length);
            PortablePackage.RequireSpace(root, checked(previousBytes + swapBytes + 2 * 1024 * 1024));
            Directory.Delete(preview, recursive: true);
            var runner = Path.Combine(work, PortablePackage.Helper);
            File.Copy(helperSource, runner);
            VerifyHelper(runner, helperDigest);

            token.ThrowIfCancellationRequested();
            return runner;
        }
        catch
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    public static void Launch(string runner, UpdateVersion version)
    {
        if (Environment.IsPrivilegedProcess || ReleaseTrust.PublisherPublicKey is not { } publisherKey ||
            !GitHubReleases.OfficialIdentityConfigured)
        {
            throw new UpdateException(UpdateState.NotConfigured);
        }

        var root = PortableFileSystem.ResolveRoot(AppContext.BaseDirectory);
        var work = Path.GetDirectoryName(runner);
        if (work is null || !Guid.TryParseExact(Path.GetFileName(work), "N", out _) ||
            !string.Equals(Path.GetDirectoryName(work), Path.Combine(root, PortablePackage.WorkDirectory), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(runner), PortablePackage.Helper, StringComparison.Ordinal))
            throw new UpdateException(UpdateState.VerificationFailed);

        using var pins = new PortableFileSystem();
        pins.PinDirectory(work);
        var installed = PortablePackage.ReadSignedManifest(root, publisherKey);
        using var pinned = PortableFileSystem.OpenRead(PortablePackage.PayloadPath(work, PortablePackage.Helper));
        VerifyHelper(pinned, Convert.FromHexString(installed.Files[PortablePackage.Helper]));
        using var main = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(runner) { UseShellExecute = false, WorkingDirectory = work };
        start.ArgumentList.Add(version.ToString());
        start.ArgumentList.Add(main.Id.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(main.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
        using var helper = Process.Start(start) ?? throw new UpdateException(UpdateState.Failed);
    }

    public static void Discard(string runner)
    {
        var work = Path.GetDirectoryName(runner)!;
        PortablePackage.CheckPath(work);
        if (Path.GetFileName(runner) == PortablePackage.Helper && Guid.TryParseExact(Path.GetFileName(work), "N", out _) &&
            !File.Exists(Path.Combine(work, PortableUpdateTransaction.PendingFile)))
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static void VerifyHelper(string path, byte[] digest)
    {
        using var helper = PortableFileSystem.OpenRead(path);
        VerifyHelper(helper, digest);
    }

    private static void VerifyHelper(Stream helper, byte[] digest)
    {
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(helper), digest))
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }
    }

    public static bool RequiresRecovery(string root, bool includeRootAncestors = true)
        => FindPending(root, includeRootAncestors).Count > 0;

    public static IReadOnlyList<string> FindPending(string root, bool includeRootAncestors = true)
        => PortableUpdateTransaction.FindPending(root, includeRootAncestors ? null : root);

    internal static void PruneFinalized(string root)
    {
        var baseWork = Path.Combine(root, PortablePackage.WorkDirectory);
        PortablePackage.CheckPath(baseWork);
        if (!Directory.Exists(baseWork)) return;
        foreach (var work in Directory.EnumerateDirectories(baseWork))
        {
            if (!Guid.TryParseExact(Path.GetFileName(work), "N", out _)) continue;
            try { PruneJob(work); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or UpdateException) { }
        }
    }

    private static void PruneJob(string work)
    {
        if (PortableUpdateTransaction.HasMarker(work, PortableUpdateTransaction.PendingFile)) return;
        var complete = PortableUpdateTransaction.HasMarker(work, PortableUpdateTransaction.CompleteFile);
        var rolledBack = PortableUpdateTransaction.HasMarker(work, PortableUpdateTransaction.RolledBackFile);
        if (complete == rolledBack) return;
        var marker = complete ? PortableUpdateTransaction.CompleteFile : PortableUpdateTransaction.RolledBackFile;

        var files = new List<string>();
        var manifests = new List<string>();
        var subdirectories = new List<string>();
        using (var pins = new WindowsStartupFileMove.DirectoryPins())
        {
            pins.Pin(work);
            foreach (var entry in new DirectoryInfo(work).EnumerateFileSystemInfos())
            {
                PortablePackage.CheckPath(entry.FullName);
                if (entry is DirectoryInfo directory)
                {
                    if (directory.Name is not (PortableUpdateTransaction.IncomingDirectory or PortableUpdateTransaction.BackupDirectory)) return;
                    pins.Pin(directory.FullName);
                    subdirectories.Add(directory.FullName);
                    var manifest = Path.Combine(directory.FullName, PortablePackage.Manifest);
                    var expected = File.Exists(manifest) ? PortablePackage.ReadManifest(directory.FullName) : [];
                    if (File.Exists(manifest)) manifests.Add(manifest);
                    foreach (var payload in directory.EnumerateFileSystemInfos())
                    {
                        PortablePackage.CheckPath(payload.FullName);
                        if (payload is DirectoryInfo child)
                        {
                            if (child.Name is not ("en" or "en-US" or "Licencas")) return;
                            pins.Pin(child.FullName);
                            subdirectories.Add(child.FullName);
                            foreach (var item in child.EnumerateFileSystemInfos())
                            {
                                if (item is not FileInfo || !expected.ContainsKey(child.Name + "/" + item.Name)) return;
                                PortablePackage.CheckPath(item.FullName);
                                files.Add(item.FullName);
                            }
                        }
                        else if (payload.Name != PortablePackage.Manifest)
                        {
                            if (payload.Name != PortablePackage.ManifestSignature && !expected.ContainsKey(payload.Name)) return;
                            files.Add(payload.FullName);
                        }
                    }
                }
                else if (entry.Name != marker)
                {
                    if (entry.Name is not (PortablePackage.Helper or "release.zip" or "release.zip.sig" or RecoveryAuthorization.FileName) &&
                        !(entry.Name is ['s', 'w', 'a', 'p', '-', .., '.', 't', 'm', 'p'] &&
                          Guid.TryParseExact(entry.Name.AsSpan(5, entry.Name.Length - 9), "N", out _))) return;
                    files.Add(entry.FullName);
                }
            }

            foreach (var file in files) File.Delete(file);
            foreach (var manifest in manifests) File.Delete(manifest);
        }

        foreach (var directory in subdirectories.AsEnumerable().Reverse())
        {
            PortablePackage.CheckPath(directory);
            Directory.Delete(directory);
        }
        using (var pins = new WindowsStartupFileMove.DirectoryPins())
        {
            pins.Pin(work);
            if (Directory.EnumerateFileSystemEntries(work).SingleOrDefault() == Path.Combine(work, marker))
            {
                File.Delete(Path.Combine(work, marker));
            }
        }
        PortablePackage.CheckPath(work);
        Directory.Delete(work);
    }
}
