using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.Uninstaller;

internal static class ResidueRecoveryIntegrity
{
    private const long MaximumBytes = 1024L * 1024 * 1024;
    private const int MaximumStreams = 128;
    private const AccessControlSections Security = AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group;

    internal static bool Supports(string path) =>
        Path.GetPathRoot(path) is { Length: 3 } root && new DriveInfo(root).DriveFormat == "NTFS";

    internal static string Capture(string root, bool directory, CancellationToken token)
    {
        using var manifest = new MemoryStream();
        using var writer = new BinaryWriter(manifest, Encoding.UTF8, leaveOpen: true);
        var pending = new Stack<(string Path, bool Directory)>();
        pending.Push((root, directory));
        var entries = 0;
        long bytes = 0;
        while (pending.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (++entries > ResidueQuarantineService.MaxTreeEntries) throw new IOException();
            writer.Write(item.Path[root.Length..]);
            writer.Write(item.Directory);
            var identity = ResidueFileGuard.ReadRecoveryIdentity(item.Path, item.Directory);
            writer.Write(identity);
            var security = item.Directory
                ? new DirectoryInfo(item.Path).GetAccessControl(Security).GetSecurityDescriptorBinaryForm()
                : new FileInfo(item.Path).GetAccessControl(Security).GetSecurityDescriptorBinaryForm();
            writer.Write(SHA256.HashData(security));
            var streams = ListStreams(item.Path);
            writer.Write(streams.Count);
            foreach (var (name, length) in streams.OrderBy(stream => stream.Name, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (length < 0 || length > MaximumBytes - bytes) throw new IOException();
                bytes += length;
                var path = name == "::$DATA" ? item.Path : item.Path + name;
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (stream.Length != length) throw new IOException();
                writer.Write(name);
                writer.Write(length);
                writer.Write(SHA256.HashData(stream));
            }
            if (!item.Directory && !streams.Any(stream => stream.Name == "::$DATA")) throw new IOException();

            if (item.Directory)
            {
                var children = new DirectoryInfo(item.Path).EnumerateFileSystemInfos()
                    .Take(ResidueQuarantineService.MaxTreeEntries - entries - pending.Count + 1)
                    .OrderByDescending(child => child.Name, StringComparer.Ordinal);
                foreach (var child in children)
                {
                    if (entries + pending.Count >= ResidueQuarantineService.MaxTreeEntries ||
                        child.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException();
                    pending.Push((child.FullName, child.Attributes.HasFlag(FileAttributes.Directory)));
                }
            }
            // Reject objects whose path or identity changed during inspection.
            if (ResidueFileGuard.ReadRecoveryIdentity(item.Path, item.Directory) != identity)
            {
                throw new IOException();
            }
        }
        return Convert.ToHexString(SHA256.HashData(manifest.GetBuffer().AsSpan(0, (int)manifest.Length)));
    }

    private static List<(string Name, long Length)> ListStreams(string path)
    {
        const int endOfStreams = 38;
        using var find = FindFirstStreamW(@"\\?\" + path, 0, out var data, 0);
        var lastError = Marshal.GetLastPInvokeError();
        var streams = new List<(string, long)>();
        if (!find.IsInvalid)
        {
            do
            {
                if (streams.Count >= MaximumStreams || !data.Name.StartsWith(':') ||
                    !data.Name.EndsWith(":$DATA", StringComparison.Ordinal) || data.Name.Contains('\\') || data.Name.Contains('/'))
                {
                    throw new IOException();
                }
                streams.Add((data.Name, data.Length));
            }
            while (FindNextStreamW(find, out data));
            lastError = Marshal.GetLastPInvokeError();
        }
        if (lastError != endOfStreams) throw new IOException(new Win32Exception(lastError).Message);
        return streams;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData
    {
        public long Length;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)]
        public string Name;
    }

    private sealed class FindStreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private FindStreamHandle() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern FindStreamHandle FindFirstStreamW(string path, int level, out StreamData data, int flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextStreamW(FindStreamHandle find, out StreamData data);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);
}
