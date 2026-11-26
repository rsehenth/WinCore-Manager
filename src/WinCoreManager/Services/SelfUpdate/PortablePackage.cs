using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace WinCoreManager.Services.SelfUpdate;

internal static class PortablePackage
{
    public const string Executable = "WinCoreManager.exe";
    public const string Helper = "WinCoreManager.Update.exe";
    public const string RecoveryHelper = "WinCoreManager.Recover.exe";
    public const string RecoveryDirectory = "Recovery";
    public const string Manifest = "WinCoreManager.package.sha256";
    public const string ManifestSignature = Manifest + ".sig";
    public const string WorkDirectory = ".wincore-update";
    public const long MaximumExpandedBytes = 1024L * 1024 * 1024;
    private const int MaximumEntries = 4096;
    private const int MaximumManifestBytes = 1024 * 1024;

    public static string ResolveRoot(string location)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        if (!Directory.Exists(path) || Path.GetPathRoot(path) is not { Length: 3 } drive ||
            path.Length <= drive.Length ||
            new DriveInfo(drive).DriveType is not (DriveType.Fixed or DriveType.Removable))
        {
            throw new UpdateException(UpdateState.CannotWrite);
        }

        CheckPath(path);
        return path;
    }

    public static void CheckPath(string path)
        => CheckPath(path, boundary: null);

    internal static void CheckPath(string path, string? boundary)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (current.Equals(boundary, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UpdateException(UpdateState.VerificationFailed);
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static string PayloadPath(string root, string relative)
    {
        if (relative is not (Manifest or ManifestSignature) && !IsPayloadPath(relative))
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        CheckPath(path);
        return path;
    }

    public static bool IsPayloadPath(string path)
    {
        if (path.Length is < 1 or > 180 || path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '/' or '_' or '-')))
        {
            return false;
        }

        var parts = path.Split('/');
        if (parts.Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') ||
            IsDeviceName(part.Split('.')[0])))
        {
            return false;
        }

        return parts.Length switch
        {
            1 => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                 path is Executable or Helper or "createdump.exe" or "WinCoreManager.deps.json" or
                     "WinCoreManager.runtimeconfig.json" or "NOTICE-BCUninstaller.md" or "LICENSE-BCUninstaller.txt",
            2 when parts[0] is "en" or "en-US" => parts[1].EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase),
            2 when parts[0] is "Licencas" => Path.GetExtension(parts[1]) is ".txt" or ".md",
            _ => false,
        };
    }

    private static bool IsDeviceName(string name) =>
        name.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
        (name.Length == 4 && char.IsAsciiDigit(name[3]) &&
         (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));

    public static void RequireSpace(string path, long needed)
    {
        if (new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < needed)
        {
            throw new UpdateException(UpdateState.InsufficientSpace);
        }
    }

    public static void Extract(string zip, string staging, CancellationToken token)
    {
        CheckPath(staging);
        if (Directory.Exists(staging) || File.Exists(staging))
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        using var archive = ZipFile.OpenRead(zip);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            var relative = entry.FullName;
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            var directory = relative.EndsWith('/');
            if (!seen.Add(relative.TrimEnd('/')) || (entry.ExternalAttributes & 0x400) != 0 ||
                (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)) ||
                ((entry.ExternalAttributes & 0x10) != 0 && !directory))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }

            if (directory)
            {
                if (entry.Length != 0 || relative is not ("Data/" or "en/" or "en-US/" or "Licencas/"))
                {
                    throw new UpdateException(UpdateState.VerificationFailed);
                }
                continue;
            }

            if (relative == "settings.json" && entry.Length <= 16 * 1024)
            {
                continue;
            }

            if ((relative is not (Manifest or ManifestSignature) && !IsPayloadPath(relative)) ||
                (relative == ManifestSignature && entry.Length != ReleaseTrust.SignatureSize) || entry.Length < 0 ||
                entry.Length > MaximumExpandedBytes - expanded)
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }

            entries.Add(relative, entry);
            expanded += entry.Length;
        }

        RequireSpace(staging, expanded);
        Directory.CreateDirectory(staging);
        var buffer = new byte[128 * 1024];
        foreach (var (relative, entry) in entries)
        {
            token.ThrowIfCancellationRequested();
            var destination = PayloadPath(staging, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long length = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                token.ThrowIfCancellationRequested();
                length += read;
                if (length > entry.Length)
                {
                    throw new UpdateException(UpdateState.VerificationFailed);
                }
                output.Write(buffer, 0, read);
            }

            if (length != entry.Length)
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
            output.Flush(flushToDisk: true);
        }

        var manifest = ReadManifest(staging);
        if (!manifest.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
                entries.Keys.Where(path => path is not (Manifest or ManifestSignature))))
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }
        VerifyFiles(staging, manifest, token);
    }

    public static Dictionary<string, string> ReadManifest(string root)
    {
        using var stream = PortableFileSystem.OpenRead(PayloadPath(root, Manifest));
        return ReadManifest(stream);
    }

    internal static SignedPortableManifest ReadSignedManifest(string root, string publisherKey)
    {
        using var manifest = PortableFileSystem.OpenRead(PayloadPath(root, Manifest));
        using var signature = PortableFileSystem.OpenRead(PayloadPath(root, ManifestSignature));
        if (manifest.Length is <= 0 or > MaximumManifestBytes)
            throw new UpdateException(UpdateState.VerificationFailed);
        ReleaseTrust.Verify(manifest, signature, publisherKey);
        manifest.Position = 0;
        signature.Position = 0;
        var manifestDigest = Convert.ToHexString(SHA256.HashData(manifest));
        var signatureDigest = Convert.ToHexString(SHA256.HashData(signature));
        manifest.Position = 0;
        return new SignedPortableManifest(ReadManifest(manifest), manifestDigest, signatureDigest);
    }

    private static Dictionary<string, string> ReadManifest(Stream stream)
    {
        if (stream.Length is <= 0 or > MaximumManifestBytes)
        {
            throw new UpdateException(UpdateState.VerificationFailed);
        }

        using var reader = new StreamReader(stream, leaveOpen: true);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length < 67 || line[64..66] != "  " || !line[..64].All(char.IsAsciiHexDigit) ||
                !IsPayloadPath(line[66..]) || !result.TryAdd(line[66..], line[..64].ToUpperInvariant()) || result.Count > MaximumEntries)
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
        }

        if (!result.ContainsKey(Executable) || !result.ContainsKey(Helper))
        {
            throw new UpdateException(UpdateState.IncompatiblePackage);
        }

        return result;
    }

    public static void VerifyFiles(string root, Dictionary<string, string> files, CancellationToken token = default)
    {
        long remaining = MaximumExpandedBytes;
        foreach (var (name, digest) in files)
        {
            token.ThrowIfCancellationRequested();
            using var stream = PortableFileSystem.OpenRead(PayloadPath(root, name));
            remaining -= stream.Length;
            if (remaining < 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(digest)))
            {
                throw new UpdateException(UpdateState.VerificationFailed);
            }
        }
    }

    public static void VerifyLayout(string root, UpdateVersion version)
    {
        var manifest = ReadManifest(root);
        string[] required = [Executable, Helper, "WinCoreManager.dll", "WinCoreManager.runtimeconfig.json",
            "WinCoreManager.deps.json", "en-US/WinCoreManager.resources.dll", "coreclr.dll", "hostpolicy.dll", "e_sqlite3.dll"];
        if (required.Any(path => !manifest.ContainsKey(path)))
        {
            throw new UpdateException(UpdateState.IncompatiblePackage);
        }

        var assembly = AssemblyName.GetAssemblyName(PayloadPath(root, "WinCoreManager.dll"));
        if (assembly.Name != "WinCoreManager" || assembly.Version is not { } numeric ||
            numeric != new Version(version.Major, version.Minor, version.Patch, 0))
        {
            throw new UpdateException(UpdateState.IncompatiblePackage);
        }

        VerifyAvailableSignatures(root);
    }

    internal static void VerifyAvailableSignatures(string root)
    {
        foreach (var binary in new[] { Executable, Helper, "WinCoreManager.dll" })
            VerifyExecutable(PayloadPath(root, binary));
    }

    internal static void VerifyExecutable(string path)
    {
        CheckPath(path);
        using var stream = PortableFileSystem.OpenRead(path);
        using var pe = new PEReader(stream);
        if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && pe.PEHeaders.CoffHeader.Machine != Machine.Amd64)
            throw new UpdateException(UpdateState.IncompatiblePackage);
        if (pe.PEHeaders.PEHeader is not { } header ||
            (header.CertificateTableDirectory.Size != 0 && !Authenticode.Verify(path)))
            throw new UpdateException(UpdateState.VerificationFailed);
    }
}

internal sealed record SignedPortableManifest(Dictionary<string, string> Files, string ManifestDigest, string SignatureDigest)
{
    public IEnumerable<KeyValuePair<string, string>> WithMetadata() => Files
        .Append(new(PortablePackage.ManifestSignature, SignatureDigest))
        .Append(new(PortablePackage.Manifest, ManifestDigest));
}
