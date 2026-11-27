using System.IO;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.DiskSpace;

internal static class DiskScanPaths
{
    public static string[] RelativeParts(DiskVolume volume, string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new IOException("The path is not an absolute local path.");
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(volume.Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The path is outside the selected local volume.");
        return full[volume.Root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }

    public static IEnumerable<string> RelativeParts(DiskVolume volume, DiskDirectory directory)
    {
        var children = new Stack<string>();
        while (directory.Parent is { } parent)
        {
            children.Push(directory.Name);
            directory = parent;
        }
        return RelativeParts(volume, directory.Name).Concat(children);
    }

    public static SafeFileHandle Open(DiskVolume volume, string path)
    {
        var parts = RelativeParts(volume, path);
        return OpenChain(NativeDiskDirectory.OpenAbsolute(volume.VolumePath), parts);
    }

    public static SafeFileHandle OpenDescendant(SafeFileHandle root, DiskDirectory node)
    {
        var parts = new Stack<string>();
        for (var current = node; current.Parent is not null; current = current.Parent)
            parts.Push(current.Name);
        return OpenChain(root, parts, keepRoot: true);
    }

    public static T WithFile<T>(DiskVolume volume, DiskFile file, Func<SafeFileHandle, T> action)
    {
        var parts = RelativeParts(volume, file.Directory);
        var handles = new Stack<SafeFileHandle>();
        try
        {
            handles.Push(NativeDiskDirectory.OpenAbsolute(volume.VolumePath, guard: true));
            foreach (var part in parts)
                handles.Push(NativeDiskDirectory.OpenChild(handles.Peek(), part, directory: true, guard: true));
            using var handle = NativeDiskDirectory.OpenChild(handles.Peek(), file.Name, directory: false, guard: true, readData: true);
            return action(handle);
        }
        finally
        {
            while (handles.TryPop(out var handle))
                handle.Dispose();
        }
    }

    private static SafeFileHandle OpenChain(SafeFileHandle root, IEnumerable<string> parts, bool keepRoot = false)
    {
        var handle = root;
        try
        {
            foreach (var part in parts)
            {
                var next = NativeDiskDirectory.OpenChild(handle, part, directory: true);
                if (!keepRoot || handle != root)
                    handle.Dispose();
                handle = next;
            }
            return handle;
        }
        catch
        {
            if (!keepRoot || handle != root)
                handle.Dispose();
            throw;
        }
    }
}
