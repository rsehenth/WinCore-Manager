using WinCoreManager.Localization;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.Startup;

internal interface IStartupFileMove
{
    bool MoveVerified(string source, string destination, string digest, Func<bool> revalidate, CancellationToken token);
}

internal sealed class WindowsStartupFileMove : IStartupFileMove
{
    private const int MaxShortcutBytes = 4 * 1024 * 1024;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    private static string? Digest(FileStream stream) =>
        stream.Length <= MaxShortcutBytes ? Convert.ToHexString(SHA256.HashData(stream)) : null;

    public bool MoveVerified(string source, string destination, string digest, Func<bool> revalidate, CancellationToken token)
    {
        if (!StartupPaths.TryLocalPath(source, out source) || !StartupPaths.TryLocalPath(destination, out destination)
            || !source[..3].Equals(destination[..3], StringComparison.OrdinalIgnoreCase))
            return false;

        using var directories = new DirectoryPins();
        directories.Pin(Path.GetDirectoryName(source)!);
        directories.Pin(Path.GetDirectoryName(destination)!);
        using var file = Open(source, 0x80010000, 1, 3, OpenReparsePoint); // GENERIC_READ | DELETE, read sharing only.
        if (!HasType(file, directory: false)) return false;

        using var stream = new FileStream(file, FileAccess.Read);
        if (!string.Equals(Digest(stream), digest, StringComparison.Ordinal) || !revalidate())
            return false;
        token.ThrowIfCancellationRequested();
        Rename(file, destination);
        return true;
    }

    internal sealed class DirectoryPins : IDisposable
    {
        private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);

        public void Pin(string path, bool create = false, bool forCleanup = false)
        {
            if (!StartupPaths.TryLocalPath(path, out var normalized))
                throw new IOException(LocalizationService.Source("Message.TheFolderIsNotOnALocalFixed"));
            var parents = new Stack<string>();
            for (string? parent = normalized; parent is not null; parent = Path.GetDirectoryName(parent))
                parents.Push(parent);
            while (parents.TryPop(out var parent))
            {
                if (_directories.ContainsKey(parent)) continue;
                if (create && Path.GetDirectoryName(parent) is not null)
                    Directory.CreateDirectory(parent);
                var directory = forCleanup ? PinCleanupDirectory(parent)
                    : Open(parent, 0x80, 3, 3, OpenReparsePoint | BackupSemantics); // READ_ATTRIBUTES, no delete sharing.
                _directories.Add(parent, directory);
                if (!forCleanup && !HasType(directory, directory: true))
                    throw new IOException(LocalizationService.Source("Message.TheStartupOrBackupFolderUsesAnIndirect"));
            }
        }

        // Callers keep ancestors pinned while a descendant is enumerated.
        internal static SafeFileHandle PinCleanupDirectory(string path)
        {
            const uint openNoRecall = 0x00100000;
            var directory = Open(path, 0x80, 1, 3, OpenReparsePoint | BackupSemantics | openNoRecall);
            if (GetFileInformationByHandleEx(directory, 9, out var info, 8)
                && (info.Attributes & (FileAttributes.Directory | WindowsFeatures.CleanupFiles.IndirectAttributes))
                    == FileAttributes.Directory)
                return directory;
            directory.Dispose();
            throw new IOException(LocalizationService.Source("Message.TheStartupOrBackupFolderUsesAnIndirect"));
        }

        public void Dispose()
        {
            foreach (var handle in _directories.Values.Reverse())
                handle.Dispose();
        }
    }

    internal static SafeFileHandle OpenExclusiveLock(string path)
    {
        var file = Open(path, 0x80000000, 0, 4, OpenReparsePoint); // GENERIC_READ, no sharing, OPEN_ALWAYS.
        try
        {
            if (!HasType(file, directory: false)) throw new IOException(LocalizationService.Source("Message.TheLockFileUsesAnIndirectPath"));
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    internal static FileStream OpenVerifiedFileForRead(string path, bool allowReplacement = false)
    {
        var file = Open(path, 0x80000000, allowReplacement ? 5u : 1u, 3, OpenReparsePoint);
        try
        {
            if (!HasType(file, directory: false)) throw new IOException(LocalizationService.Source("Message.TheFileUsesAnIndirectPath"));
            return new FileStream(file, FileAccess.Read);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static bool HasType(SafeFileHandle file, bool directory)
    {
        if (!GetFileInformationByHandleEx(file, 9, out var attributes, 8))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (attributes.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint))
            == (directory ? FileAttributes.Directory : 0);
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFileW(path, access, share, IntPtr.Zero, disposition, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    private static void Rename(SafeFileHandle source, string destination)
    {
        var name = Encoding.Unicode.GetBytes(destination + "\0");
        // FILE_RENAME_INFO: flags zero (no replacement), null RootDirectory, byte length, then UTF-16 name.
        var lengthOffset = IntPtr.Size * 2;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = new byte[Math.Max(nameOffset + name.Length, IntPtr.Size == 8 ? 24 : 16)];
        BitConverter.TryWriteBytes(buffer.AsSpan(lengthOffset), name.Length - sizeof(char));
        name.CopyTo(buffer, nameOffset);
        if (!SetFileInformationByHandle(source, 3, buffer, (uint)buffer.Length))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTag info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] info, uint size);
}
