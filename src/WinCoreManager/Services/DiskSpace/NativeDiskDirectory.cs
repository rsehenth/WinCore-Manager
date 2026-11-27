using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.DiskSpace;

internal static unsafe class NativeDiskDirectory
{
    public const int BufferSize = 16 * 1024;
    private const uint ListDirectory = 1;
    private const uint ReadAttributes = 0x80;
    private const uint Synchronize = 0x00100000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const uint DirectoryFile = 1;
    private const uint NonDirectoryFile = 0x40;
    private const uint SynchronousIoNonAlert = 0x20;
    private const uint ShareAll = 7;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const uint FileOpen = 1;
    private const uint DontReparse = 0x00001000;
    private const int AttributeTagInfo = 9;
    private const int IdBothDirectoryInfo = 10;
    private const int FullDirectoryInfo = 14;
    private const int IdInfo = 18;
    private const int IdExtdDirectoryInfo = 19;

    public static string? VolumePath(string root)
    {
        var target = stackalloc char[512];
        var guid = stackalloc char[64];
        if (QueryDosDevice(root[..2], target, 512) == 0
            || !new ReadOnlySpan<char>(target, 8).SequenceEqual(@"\Device\")
            || !GetVolumeNameForVolumeMountPoint(root, guid, 64))
            return null;

        return new string(guid);
    }

    public static SafeFileHandle OpenAbsolute(string extendedPath, bool guard = false) =>
        RequireOrdinary(CreateFile(extendedPath, (guard ? 0 : ListDirectory) | ReadAttributes,
            guard ? ShareRead : ShareAll, 0, OpenExisting, BackupSemantics | OpenReparsePoint, 0), directory: true);

    public static SafeFileHandle OpenChild(SafeFileHandle parent, ReadOnlySpan<char> name, bool directory, bool guard = false)
    {
        if (name.IsEmpty || name is "." or ".." || name.IndexOfAny('\\', '/', ':') >= 0 || name.Contains('\0'))
            throw new IOException("Invalid directory entry name.");

        fixed (char* chars = name)
        {
            var length = checked((ushort)(name.Length * sizeof(char)));
            var unicode = new UnicodeString { Buffer = chars, Length = length, MaximumLength = length };
            var attributes = new ObjectAttributes
            {
                Length = (uint)sizeof(ObjectAttributes),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = &unicode,
                Attributes = DontReparse,
            };
            var status = NtCreateFile(out var handle, Synchronize | ReadAttributes | (directory && !guard ? ListDirectory : 0),
                &attributes, out _, 0, 0, guard ? ShareRead : ShareAll, FileOpen,
                OpenReparsePoint | SynchronousIoNonAlert | (directory ? DirectoryFile : NonDirectoryFile), 0, 0);
            GC.KeepAlive(parent);
            if (status < 0)
                throw Failure((int)RtlNtStatusToDosError(status));

            return RequireOrdinary(new SafeFileHandle(handle, ownsHandle: true), directory);
        }
    }

    private static SafeFileHandle RequireOrdinary(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure(error);
        }

        uint* info = stackalloc uint[2];
        if (!GetFileInformation(handle, AttributeTagInfo, info, 2 * sizeof(uint)))
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw Failure(error);
        }
        var attributes = (FileAttributes)info[0];
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory)
        {
            handle.Dispose();
            throw new IOException("The entry is a reparse point or its type changed.");
        }

        return handle;
    }

    public static bool ReadBatch(SafeFileHandle handle, byte[] buffer, ref int informationClass)
    {
        fixed (byte* bytes = buffer)
        {
            var first = informationClass == 0;
            if (first)
                informationClass = IdExtdDirectoryInfo;

            while (!GetFileInformation(handle, informationClass, bytes, (uint)buffer.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 18 or 38) // ERROR_NO_MORE_FILES, ERROR_HANDLE_EOF
                    return false;

                if (!first || (error is not (1 or 50 or 87 or 124)) || informationClass == FullDirectoryInfo)
                    throw Failure(error);

                informationClass = informationClass == IdExtdDirectoryInfo ? IdBothDirectoryInfo : FullDirectoryInfo;
            }
        }

        return true;
    }

    public static Entry ReadEntry(ReadOnlySpan<byte> bytes, int informationClass)
    {
        // FILE_ID_EXTD_DIR_INFO, FILE_ID_BOTH_DIR_INFO and FILE_FULL_DIR_INFO
        // share their prefix through FileNameLength. The ID and name offsets differ.
        var nameOffset = informationClass switch { IdExtdDirectoryInfo => 88, IdBothDirectoryInfo => 104, _ => 68 };
        var nameLength = BinaryPrimitives.ReadInt32LittleEndian(bytes[60..]);
        var next = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (nameLength <= 0 || (nameLength & 1) != 0 || nameLength > bytes.Length - nameOffset
            || (next != 0 && (next < nameOffset + nameLength || next > bytes.Length - nameOffset)))
            throw new IOException("Invalid directory enumeration buffer.");

        var id = informationClass switch
        {
            IdExtdDirectoryInfo => BinaryPrimitives.ReadUInt128LittleEndian(bytes[72..88]),
            IdBothDirectoryInfo => BinaryPrimitives.ReadUInt64LittleEndian(bytes[96..104]),
            _ => UInt128.Zero,
        };
        return new(next, id, (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(bytes[56..]),
            Math.Max(0, BinaryPrimitives.ReadInt64LittleEndian(bytes[40..])),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            MemoryMarshal.Cast<byte, char>(bytes.Slice(nameOffset, nameLength)));
    }

    public static bool VolumeSupportsHardLinks(SafeFileHandle handle) =>
        !GetVolumeInformationByHandle(handle, 0, 0, 0, 0, out var flags, 0, 0) || (flags & 0x00400000) != 0;

    public static UInt128 ReadIdentity(SafeFileHandle parent, ReadOnlySpan<char> name)
    {
        try
        {
            using var file = OpenChild(parent, name, directory: false);
            byte* info = stackalloc byte[24]; // Volume serial number, followed by FILE_ID_128.
            return GetFileInformation(file, IdInfo, info, 24)
                ? BinaryPrimitives.ReadUInt128LittleEndian(new ReadOnlySpan<byte>(info + 8, 16)) : UInt128.Zero;
        }
        catch (IOException) { return UInt128.Zero; }
    }

    public static long? ReadSpecialAllocation(SafeFileHandle parent, ReadOnlySpan<char> name)
    {
        try
        {
            using var file = OpenChild(parent, name, directory: false);
            long* compressionInfo = stackalloc long[2];
            // FILE_COMPRESSION_INFO reports allocated bytes for compressed and sparse streams.
            return GetFileInformation(file, 8, compressionInfo, 2 * sizeof(long)) && compressionInfo[0] >= 0
                ? compressionInfo[0] : null;
        }
        catch (IOException) { return null; }
    }

    private static IOException Failure(int error) => new("Unable to read disk metadata.", new Win32Exception(error));

    public readonly ref struct Entry(int next, UInt128 id, FileAttributes attributes, long length,
        long allocation, long lastWrite, ReadOnlySpan<char> name)
    {
        public int Next { get; } = next;
        public UInt128 Id { get; } = id;
        public FileAttributes Attributes { get; } = attributes;
        public long Length { get; } = length;
        public long Allocation { get; } = allocation;
        public long LastWrite { get; } = lastWrite;
        public ReadOnlySpan<char> Name { get; } = name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public nint RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public nint SecurityDescriptor;
        public nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public nint Status;
        public nuint Information;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFile(string path, uint desiredAccess, uint shareMode,
        nint securityAttributes, uint creationDisposition, uint flags, nint template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformation(SafeFileHandle handle, int informationClass, void* information, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandle(SafeFileHandle handle, nint label, uint labelLength,
        nint serialNumber, nint maxComponentLength, out uint flags, nint filesystemName, uint filesystemLength);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint QueryDosDevice(string device, char* target, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mount, char* path, uint size);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out nint handle, uint desiredAccess, ObjectAttributes* attributes,
        out IoStatusBlock status, nint allocationSize, uint fileAttributes, uint shareAccess, uint disposition,
        uint options, nint eaBuffer, uint eaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
}
