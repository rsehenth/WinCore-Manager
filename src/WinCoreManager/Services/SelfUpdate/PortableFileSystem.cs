using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace WinCoreManager.Services.SelfUpdate;

internal sealed class PortableFileSystem : IDisposable
{
    private const uint ReadAttributes = 0x80;
    private const uint ListDirectory = 0x01;
    private const uint ReadData = 0x80000000;
    private const uint WriteData = 0x40000000;
    private const uint Delete = 0x00010000;
    private const uint Synchronize = 0x00100000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private readonly Dictionary<string, SafeFileHandle> _directories = new(StringComparer.OrdinalIgnoreCase);

    public static string ResolveRoot(string location)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(location));
        if (Path.GetPathRoot(root) is not { Length: 3 } drive || root.Length <= drive.Length ||
            new DriveInfo(drive).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new UpdateException(UpdateState.CannotWrite);
        // Open the package directory itself without following a link. An alias above it may be resolved.
        using var directory = Open(root, ReadAttributes, FileShare.ReadWrite, BackupSemantics | OpenReparsePoint);
        RequireType(directory, isDirectory: true);
        var path = new StringBuilder(32768);
        var length = GetFinalPathNameByHandleW(directory, path, (uint)path.Capacity, 0);
        if (length == 0) throw FileError(Marshal.GetLastWin32Error());
        if (length >= path.Capacity || length < 7 || path[5] != ':' || path[6] != '\\')
            throw new UpdateException(UpdateState.CannotWrite);
        return PortablePackage.ResolveRoot(path.ToString()[4..]);
    }

    public void PinDirectory(string path, bool create = false)
    {
        if (_directories.ContainsKey(path)) return;
        SafeFileHandle directory;
        if (Path.GetDirectoryName(path) is { } parent)
        {
            PinDirectory(parent, create);
            directory = OpenRelative(_directories[parent], Path.GetFileName(path), ReadAttributes | ListDirectory,
                FileShare.ReadWrite, create ? 3u : 1u, options: 1);
        }
        else
        {
            directory = Open(path, ReadAttributes | ListDirectory, FileShare.ReadWrite, BackupSemantics | OpenReparsePoint);
        }
        try
        {
            RequireType(directory, isDirectory: true);
            _directories.Add(path, directory);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    public FileStream CreateNew(string path, bool forRename = false)
    {
        var file = OpenLeaf(path, WriteData | (forRename ? Delete : 0), forRename ? FileShare.None : FileShare.Read, disposition: 2);
        return new FileStream(file, FileAccess.Write);
    }

    public FileStream OpenForRename(string path)
        => new(OpenLeaf(path, ReadData | Delete, FileShare.Read, disposition: 1), FileAccess.Read);

    public FileStream OpenExclusiveLease(string path)
        => new(OpenLeaf(path, ReadData | WriteData, FileShare.None, disposition: 3), FileAccess.ReadWrite);

    public void Rename(FileStream source, string destination)
    {
        var parent = _directories[Path.GetDirectoryName(destination)!];
        RequireType(parent, isDirectory: true);
        var name = Encoding.Unicode.GetBytes(Path.GetFileName(destination));
        var rootOffset = IntPtr.Size;
        var lengthOffset = IntPtr.Size * 2;
        var nameOffset = lengthOffset + sizeof(uint);
        var info = new byte[Math.Max(nameOffset + name.Length + sizeof(char), IntPtr.Size == 8 ? 24 : 16)];
        if (IntPtr.Size == 8)
            BitConverter.TryWriteBytes(info.AsSpan(rootOffset), parent.DangerousGetHandle().ToInt64());
        else
            BitConverter.TryWriteBytes(info.AsSpan(rootOffset), parent.DangerousGetHandle().ToInt32());
        BitConverter.TryWriteBytes(info.AsSpan(lengthOffset), name.Length);
        name.CopyTo(info, nameOffset);
        // FileRenameInformation with ReplaceIfExists = false, relative to the held directory.
        var status = NtSetInformationFile(source.SafeFileHandle, out _, info, (uint)info.Length, 10);
        if (status < 0) throw FileError((int)RtlNtStatusToDosError(status));
        GC.KeepAlive(parent);
    }

    // A failed scratch copy is removed through the handle that created it.
    public static void DiscardScratch(FileStream scratch)
        => SetFileInformationByHandle(scratch.SafeFileHandle, 4, [1], 1);

    public static FileStream OpenRead(string path)
    {
        var file = Open(path, ReadData, FileShare.Read, OpenReparsePoint);
        try
        {
            RequireType(file, isDirectory: false);
            return new FileStream(file, FileAccess.Read);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var directory in _directories.Values.Reverse()) directory.Dispose();
    }

    private static SafeFileHandle Open(string path, uint access, FileShare share, uint flags)
    {
        var file = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!file.IsInvalid) return file;
        var error = Marshal.GetLastWin32Error();
        file.Dispose();
        throw FileError(error);
    }

    private SafeFileHandle OpenLeaf(string path, uint access, FileShare share, uint disposition)
    {
        var parent = _directories[Path.GetDirectoryName(path)!];
        RequireType(parent, isDirectory: true);
        var file = OpenRelative(parent, Path.GetFileName(path), access | ReadAttributes, share, disposition, options: 0x40);
        try
        {
            RequireType(file, isDirectory: false);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static unsafe SafeFileHandle OpenRelative(SafeFileHandle parent, string name, uint access,
        FileShare share, uint disposition, uint options)
    {
        // The parent is already held. Resolve a single child without traversing a reparse point.
        fixed (char* pointer = name)
        {
            var length = checked((ushort)(name.Length * sizeof(char)));
            var unicode = new UnicodeString { Length = length, MaximumLength = length, Buffer = pointer };
            var attributes = new ObjectAttributes
            {
                Length = sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &unicode,
                Attributes = 0x40 | 0x1000, // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
            };
            var status = NtCreateFile(out var file, access | Synchronize, in attributes, out _, IntPtr.Zero,
                0, share, disposition, options | OpenReparsePoint | 0x20, IntPtr.Zero, 0);
            GC.KeepAlive(parent);
            if (status >= 0) return file;
            file.Dispose();
            if (status == unchecked((int)0xC000050B)) throw new UpdateException(UpdateState.VerificationFailed);
            throw FileError((int)RtlNtStatusToDosError(status));
        }
    }

    private static Exception FileError(int error)
    {
        var message = new Win32Exception(error).Message;
        return error switch
        {
            2 => new FileNotFoundException(message),
            3 => new DirectoryNotFoundException(message),
            5 => new UnauthorizedAccessException(message),
            _ => new IOException(message, unchecked((int)0x80070000) | error),
        };
    }

    private static void RequireType(SafeFileHandle file, bool isDirectory)
    {
        if (!GetFileInformationByHandleEx(file, 9, out var attributes, 8))
            throw FileError(Marshal.GetLastWin32Error());
        if ((attributes.Flags & (FileAttributes.Directory | FileAttributes.ReparsePoint)) !=
            (isDirectory ? FileAttributes.Directory : 0))
            throw new UpdateException(UpdateState.VerificationFailed);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag
    {
        public FileAttributes Flags;
        public uint Tag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public UIntPtr Information;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int kind, out AttributeTag info, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, byte[] info, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle file, uint access, in ObjectAttributes attributes,
        out IoStatusBlock status, IntPtr allocationSize, uint fileAttributes, FileShare share, uint disposition,
        uint options, IntPtr extendedAttributes, uint extendedAttributesLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status,
        byte[] information, uint length, int informationClass);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
