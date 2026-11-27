using WinCoreManager.Localization;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Conteúdo observado de um arquivo ou árvore, sem atravessar reparse points.</summary>
internal sealed record ResidueFileInspection(
    string Fingerprint,
    bool IsDirectory,
    bool Complete,
    bool HasExecutable,
    bool HasReparsePoint,
    bool HasHardLinks,
    long TotalBytes);

internal readonly record struct CleanupFileIdentity(
    uint Volume, ulong FileId, long Creation, long LastWrite, long Change, long Length, long Allocated);

/// <summary>Handle aberto e validado: o movimento atua neste objeto, não em um caminho reinterpretado.</summary>
internal sealed class GuardedFile(SafeFileHandle handle, bool isDirectory, uint volumeSerial) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;
    public bool IsDirectory { get; } = isDirectory;
    public uint VolumeSerial { get; } = volumeSerial;

    public void Dispose() => Handle.Dispose();
}

/// <summary>
/// Validação e movimento vinculados ao handle. O handle é aberto sem seguir reparse points e sem
/// FILE_SHARE_DELETE: enquanto estiver aberto, outro processo não renomeia nem troca o item.
/// </summary>
internal static class ResidueFileGuard
{
    private const uint Delete = 0x00010000;
    private const uint FileReadAttributes = 0x80;
    private const uint Synchronize = 0x00100000;
    private const uint ShareRead = 1;
    private const uint ShareWrite = 2;
    private const uint ShareDelete = 4;
    private const uint OpenExisting = 3;
    private const uint FlagBackupSemantics = 0x02000000;
    private const uint FlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 1;
    private const int FileRenameInfoClass = 3;
    private const int ErrorNotSameDevice = 17;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".com", ".scr", ".sys", ".ocx", ".msi", ".cpl", ".drv",
        ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta",
    };

    public static GuardedFile OpenForMove(string normalizedPath, ResidueCategory category)
    {
        var handle = Open(normalizedPath, Delete | FileReadAttributes | Synchronize, ShareRead | ShareWrite);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotSafelyOpenTheItem0", new Win32Exception(error).Message));
        }

        try
        {
            var info = ReadInfo(handle);
            var attributes = (FileAttributes)info.FileAttributes;
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            if (GetFileType(handle) != FileTypeDisk)
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheTargetIsNotAFileOnDisk"));
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheItemIsALinkJunctionOrReparse"));
            }

            if (isDirectory != (category == ResidueCategory.Folder))
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheItemTypeChangedAfterTheScan"));
            }

            if (!isDirectory && info.NumberOfLinks > 1)
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheFileHasHardLinksAndMayBe"));
            }

            if (!string.Equals(GetFinalPath(handle), normalizedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.ThePathCrossesALinkJunctionMountPoint"));
            }

            return new GuardedFile(handle, isDirectory, info.VolumeSerialNumber);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Verdadeiro quando o caminho existente não atravessa link, montagem ou nome curto.</summary>
    public static bool PathIsCanonical(string normalizedPath)
    {
        using var handle = Open(normalizedPath, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        return !handle.IsInvalid
               && string.Equals(GetFinalPath(handle), normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPlainReadFile(SafeFileHandle handle, string path) =>
        GetFileType(handle) == FileTypeDisk && GetFileInformationByHandle(handle, out var info) &&
        ((FileAttributes)info.FileAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
        info.NumberOfLinks == 1 && string.Equals(GetFinalPath(handle), path, StringComparison.OrdinalIgnoreCase);

    internal static SafeFileHandle OpenCleanupFile(string path, bool deleting) =>
        CreateFileW(@"\\?\" + path, FileReadAttributes | (deleting ? Delete : 0),
            deleting ? 0 : ShareRead | ShareWrite | ShareDelete, IntPtr.Zero, OpenExisting,
            FlagBackupSemantics | FlagOpenReparsePoint | 0x00100000, IntPtr.Zero); // FILE_FLAG_OPEN_NO_RECALL

    internal static CleanupFileIdentity? ReadCleanupIdentity(SafeFileHandle handle, string path)
    {
        const FileAttributes excluded = FileAttributes.Directory | FileAttributes.System | FileAttributes.ReadOnly |
            WindowsFeatures.CleanupFiles.IndirectAttributes;
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info) || GetFileType(handle) != FileTypeDisk
            || info.NumberOfLinks != 1 || ((FileAttributes)info.FileAttributes & excluded) != 0
            || !string.Equals(GetFinalPath(handle), path, StringComparison.OrdinalIgnoreCase)
            || !GetCleanupBasicInfo(handle, 0, out var basic, (uint)Marshal.SizeOf<CleanupBasicInfo>())
            || !GetCleanupStandardInfo(handle, 1, out var standard, (uint)Marshal.SizeOf<CleanupStandardInfo>())
            || standard.Links != 1 || standard.DeletePending != 0 || standard.Allocated < 0 || standard.Length < 0
            || ((FileAttributes)basic.Attributes & excluded) != 0)
            return null;

        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            basic.Creation, basic.LastWrite, basic.Change, standard.Length, standard.Allocated);
    }

    // FILE_DISPOSITION_INFO receives one BOOLEAN. The exclusive handle commits the deletion on close.
    internal static bool DeleteCleanupFile(SafeFileHandle handle) =>
        SetFileInformationByHandle(handle, 4, [1], 1);

    /// <summary>Diretório real: nem ele nem um ancestral é link, montagem ou nome curto.</summary>
    public static bool IsPlainDirectory(string normalizedPath)
    {
        using var handle = Open(normalizedPath, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return false;
        }

        var attributes = (FileAttributes)info.FileAttributes;
        return attributes.HasFlag(FileAttributes.Directory)
               && !attributes.HasFlag(FileAttributes.ReparsePoint)
               && string.Equals(GetFinalPath(handle), normalizedPath, StringComparison.OrdinalIgnoreCase);
    }

    internal static string? ResolveLocalDirectory(string path)
    {
        if (!ResiduePathRules.TryNormalize(path, out var normalized)) return null;
        using var handle = CreateFileW(@"\\?\" + normalized, FileReadAttributes, ShareRead | ShareWrite | ShareDelete,
            IntPtr.Zero, OpenExisting, FlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)
            || ((FileAttributes)info.FileAttributes & FileAttributes.Directory) == 0)
        {
            return null;
        }

        var finalPath = GetFinalPath(handle);
        return ResiduePathRules.TryNormalize(finalPath, out var fullPath) ? fullPath : null;
    }

    internal static bool IsAppExecutionAlias(string normalizedPath)
    {
        using var handle = Open(normalizedPath, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        const int FileAttributeTagInfo = 9;
        const int AppExecutionLink = unchecked((int)0x8000001B);
        return !handle.IsInvalid
            && GetFileAttributeTagInfo(handle, FileAttributeTagInfo, out var info, 8)
            && info.ReparseTag == AppExecutionLink;
    }

    internal static (string Family, string Target)? ReadAppExecutionAlias(string path)
    {
        using var handle = Open(path, 0, ShareRead | ShareWrite | ShareDelete);
        const uint getReparsePoint = 0x000900A8;
        var buffer = new byte[16_384];
        return !handle.IsInvalid && DeviceIoControl(handle, getReparsePoint, IntPtr.Zero, 0,
            buffer, (uint)buffer.Length, out var size, IntPtr.Zero)
            ? ParseAppExecutionAlias(buffer.AsSpan(0, (int)size)) : null;
    }

    internal static (string Family, string Target)? ParseAppExecutionAlias(ReadOnlySpan<byte> buffer)
    {
        const uint appExecutionLink = 0x8000001B;
        const uint knownVersion = 3;
        if (buffer.Length < 14 || (buffer.Length & 1) != 0 || BitConverter.ToUInt32(buffer) != appExecutionLink ||
            BitConverter.ToUInt16(buffer[4..]) != buffer.Length - 8 || BitConverter.ToUInt32(buffer[8..]) != knownVersion)
            return null;
        var parts = Encoding.Unicode.GetString(buffer[12..]).Split('\0');
        if (parts.Length < 5 || parts[0].Length == 0 || parts[2].Length == 0 ||
            !parts[1].StartsWith(parts[0] + "!", StringComparison.OrdinalIgnoreCase) || parts.Skip(4).Any(part => part.Length != 0))
            return null;
        return (parts[0], parts[2]);
    }

    internal static SafeFileHandle PinAppExecutionAlias(string path) => Open(path, FileReadAttributes, ShareRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle file, uint code, IntPtr input, uint inputSize,
        [Out] byte[] output, uint outputSize, out uint returned, IntPtr overlapped);

    public static string? NearestExisting(string normalizedPath)
    {
        var current = normalizedPath;
        while (!string.IsNullOrEmpty(current) && !Directory.Exists(current) && !File.Exists(current))
        {
            current = Path.GetDirectoryName(current);
        }

        return string.IsNullOrEmpty(current) ? null : current;
    }

    public static uint GetVolumeSerial(string normalizedPath)
    {
        using var handle = Open(normalizedPath, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotIdentifyTheQuarantineVolume"));
        }

        return ReadInfo(handle).VolumeSerialNumber;
    }

    internal static string ReadRecoveryIdentity(string path, bool directory)
    {
        using var handle = Open(path, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        var info = ReadInfo(handle);
        var attributes = (FileAttributes)info.FileAttributes;
        if (attributes.HasFlag(FileAttributes.ReparsePoint) || attributes.HasFlag(FileAttributes.Directory) != directory ||
            (!directory && info.NumberOfLinks != 1) ||
            !string.Equals(GetFinalPath(handle), path, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException();
        }

        return FormattableString.Invariant($"{info.VolumeSerialNumber:x8}:{info.FileIndexHigh:x8}{info.FileIndexLow:x8}:{Mask(attributes)}:{ToTicks(info.CreationTime)}");
    }

    /// <summary>Inspeciona sem seguir links. Falha de leitura ou excesso de itens resulta em Complete = false.</summary>
    public static ResidueFileInspection? Inspect(string normalizedPath, int maxEntries, CancellationToken ct)
    {
        using var handle = Open(normalizedPath, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
        if (handle.IsInvalid)
        {
            return null;
        }

        var info = ReadInfo(handle);
        var attributes = (FileAttributes)info.FileAttributes;
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        var identity = $"{info.VolumeSerialNumber:x8}:{fileId:x16}:{(isDirectory ? 'D' : 'F')}";
        var lines = new List<string>();
        var complete = true;
        var hasExecutable = false;
        var hasReparse = attributes.HasFlag(FileAttributes.ReparsePoint);
        var hasHardLinks = false;
        long total = 0;

        if (!isDirectory)
        {
            total = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            hasExecutable = ExecutableExtensions.Contains(Path.GetExtension(normalizedPath));
            hasHardLinks = info.NumberOfLinks > 1;
            lines.Add($"|{Mask(attributes)}|{total}|{ToTicks(info.LastWriteTime)}");
        }
        else if (!hasReparse)
        {
            complete = WalkTree(normalizedPath, maxEntries, lines, ref hasExecutable, ref hasReparse, ref hasHardLinks, ref total, ct);
        }

        lines.Sort(StringComparer.Ordinal);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
        return new ResidueFileInspection(
            $"{identity}:{lines.Count}:{total}:{digest}", isDirectory, complete, hasExecutable, hasReparse, hasHardLinks, total);
    }

    /// <summary>Renomeia pelo handle, sem substituir e sem cópia entre volumes.</summary>
    public static void RenameNoReplace(SafeFileHandle handle, string destinationFullPath)
    {
        var name = Encoding.Unicode.GetBytes(@"\\?\" + destinationFullPath);
        var lengthOffset = IntPtr.Size * 2;
        var nameOffset = lengthOffset + sizeof(uint);
        var buffer = new byte[nameOffset + name.Length + sizeof(char)];
        BitConverter.TryWriteBytes(buffer.AsSpan(lengthOffset), name.Length);
        name.CopyTo(buffer, nameOffset);

        if (SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)buffer.Length))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        throw new InvalidOperationException(error switch
        {
            ErrorNotSameDevice => LocalizationService.Source("Message.QuarantineIsOnAnotherVolumeTheMoveWas"),
            ErrorFileExists or ErrorAlreadyExists => LocalizationService.Source("Message.TheTargetAlreadyExistsNothingWasOverwritten"),
            _ => LocalizationService.Source("Message.CouldNotMoveTheItem0", new Win32Exception(error).Message),
        });
    }

    private static bool WalkTree(
        string root,
        int maxEntries,
        List<string> lines,
        ref bool hasExecutable,
        ref bool hasReparse,
        ref bool hasHardLinks,
        ref long total,
        CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            MatchType = MatchType.Simple,
        };
        var pending = new Stack<string>();
        pending.Push(root);

        try
        {
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (lines.Count >= maxEntries)
                    {
                        return false;
                    }

                    // O índice do diretório é atualizado com atraso pelo NTFS: só o handle é autoritativo.
                    using var entryHandle = Open(entry.FullName, FileReadAttributes, ShareRead | ShareWrite | ShareDelete);
                    if (entryHandle.IsInvalid)
                    {
                        return false;
                    }

                    var info = ReadInfo(entryHandle);
                    var attributes = (FileAttributes)info.FileAttributes;
                    var relative = entry.FullName[root.Length..];
                    long length = 0;
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        hasReparse = true;
                    }
                    else if (attributes.HasFlag(FileAttributes.Directory))
                    {
                        pending.Push(entry.FullName);
                    }
                    else
                    {
                        length = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
                        total += length;
                        hasExecutable |= ExecutableExtensions.Contains(entry.Extension);
                        hasHardLinks |= info.NumberOfLinks > 1;
                    }

                    lines.Add($"{relative}|{Mask(attributes)}|{length}|{ToTicks(info.LastWriteTime)}");
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        {
            return false;
        }
    }

    // Archive e indexação mudam sem alteração real do conteúdo.
    private static int Mask(FileAttributes attributes) => (int)(attributes
        & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System));

    private static long ToTicks(FILETIME time) => ((long)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private static SafeFileHandle Open(string path, uint access, uint share) =>
        CreateFileW(@"\\?\" + path, access, share, IntPtr.Zero, OpenExisting, FlagBackupSemantics | FlagOpenReparsePoint, IntPtr.Zero);

    private static ByHandleFileInformation ReadInfo(SafeFileHandle handle) =>
        GetFileInformationByHandle(handle, out var info)
            ? info
            : throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotReadTheItemSData0", new Win32Exception(Marshal.GetLastPInvokeError()).Message));

    private static string? GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(1024);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        }

        if (length == 0)
        {
            return null;
        }

        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInformation
    {
        public int Attributes;
        public int ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CleanupBasicInfo
    {
        public long Creation, LastAccess, LastWrite, Change;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CleanupStandardInfo
    {
        public long Allocated, Length;
        public uint Links;
        public byte DeletePending, Directory;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCleanupBasicInfo(SafeFileHandle file, int informationClass, out CleanupBasicInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCleanupStandardInfo(SafeFileHandle file, int informationClass, out CleanupStandardInfo info, uint size);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributeTagInfo(SafeFileHandle file, int informationClass,
        out FileAttributeTagInformation information, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, byte[] information, uint size);
}
