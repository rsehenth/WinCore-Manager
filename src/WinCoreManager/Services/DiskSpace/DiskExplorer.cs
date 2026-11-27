using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.DiskSpace;

internal static class DiskExplorer
{
    private const string SelectableDeviceSuffixes = "123456789¹²³";

    public static bool Show(DiskVolume volume, DiskDirectory directory, DiskFile? file) =>
        WithVerifiedLocation(volume, directory, file, SelectInExplorer);

    internal static bool WithVerifiedLocation(
        DiskVolume volume, DiskDirectory directory, DiskFile? file, Func<string, string?, bool> action)
    {
        if (!DiskVolumeCatalog.Matches(volume))
            return false;

        string[] names;
        try { names = DiskScanPaths.RelativeParts(volume, directory).ToArray(); }
        catch (IOException) { return false; }
        if (names.Any(name => !IsShellSegment(name)) || (file is not null && !IsShellSegment(file.Name)))
            return false;

        var handles = new Stack<SafeFileHandle>();
        try
        {
            handles.Push(NativeDiskDirectory.OpenAbsolute(volume.VolumePath, guard: true));
            var path = volume.Root;
            foreach (var name in names)
            {
                handles.Push(NativeDiskDirectory.OpenChild(handles.Peek(), name, directory: true, guard: true));
                path = Path.Join(path, name);
            }
            if (file is not null)
                handles.Push(NativeDiskDirectory.OpenChild(handles.Peek(), file.Name, directory: false, guard: true));

            return DiskVolumeCatalog.Matches(volume) && action(path, file?.Name);
        }
        catch (IOException) { return false; }
        finally
        {
            while (handles.TryPop(out var handle))
                handle.Dispose();
        }
    }

    private static bool IsShellSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.EndsWith('.') || name.EndsWith(' ')
            || name.AsSpan().IndexOfAny("\\/:?*\"<>|".AsSpan()) >= 0 || name.Any(char.IsControl))
            return false;

        var device = name.Split('.', 2)[0].TrimEnd().ToUpperInvariant();
        return device is not ("CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$")
            && !(device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) ||
                device.StartsWith("LPT", StringComparison.Ordinal)) && SelectableDeviceSuffixes.Contains(device[3]));
    }

    private static bool SelectInExplorer(string folder, string? file)
    {
        nint folderId = 0;
        nint fileId = 0;
        try
        {
            if (SHParseDisplayName(folder, 0, out folderId, 0, out _) < 0)
                return false;

            if (file is null)
                return SHOpenFolderAndSelectItems(folderId, 0, null, 0) >= 0;

            if (SHParseDisplayName(Path.Join(folder, file), 0, out fileId, 0, out _) < 0)
                return false;

            return SHOpenFolderAndSelectItems(folderId, 1, [ILFindLastID(fileId)], 0) >= 0;
        }
        finally
        {
            Marshal.FreeCoTaskMem(fileId);
            Marshal.FreeCoTaskMem(folderId);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHParseDisplayName(string name, nint binding, out nint item, uint attributes, out uint actual);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHOpenFolderAndSelectItems(nint folder, uint count, nint[]? items, uint flags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern nint ILFindLastID(nint item);
}
