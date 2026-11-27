using System.IO;

namespace WinCoreManager.Services.DiskSpace;

internal sealed record DiskVolume(string Root, string Label, string VolumePath, long? TotalBytes, long? FreeBytes)
{
    public long? UsedBytes => TotalBytes is { } total && FreeBytes is { } free ? Math.Max(0, total - free) : null;
    public string Display => string.IsNullOrWhiteSpace(Label) ? Root[..2] : $"{Root[..2]} — {Label}";
}

internal sealed class DiskVolumeCatalog
{
    public DiskVolume[] List() => DriveInfo.GetDrives()
        .Select(drive => Find(drive.Name))
        .OfType<DiskVolume>()
        .OrderBy(volume => volume.Root, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public DiskVolume? Find(string root)
    {
        if (root is not [>= 'A' and <= 'Z', ':', '\\'])
            return null;

        return Read(root, includeCapacity: true);
    }

    public static bool Matches(DiskVolume volume) =>
        volume.Root is [>= 'A' and <= 'Z', ':', '\\']
        && Read(volume.Root, includeCapacity: false) is { } current
        && string.Equals(current.VolumePath, volume.VolumePath, StringComparison.OrdinalIgnoreCase);

    private static DiskVolume? Read(string root, bool includeCapacity)
    {
        try
        {
            var drive = new DriveInfo(root);
            // Read the mount identity before any filesystem probes. SUBST paths
            // and mapped shares are excluded even if their reported type is fixed.
            if (drive.DriveType != DriveType.Fixed || NativeDiskDirectory.VolumePath(root) is not { } volumePath
                || !drive.IsReady)
                return null;

            return new(root, includeCapacity ? TryRead(() => drive.VolumeLabel) ?? "" : "", volumePath,
                includeCapacity ? TryRead<long?>(() => drive.TotalSize) : null,
                includeCapacity ? TryRead<long?>(() => drive.TotalFreeSpace) : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static DiskVolume? Preferred(IReadOnlyList<DiskVolume> volumes, string windowsDirectory)
    {
        var root = Path.GetPathRoot(windowsDirectory);
        return volumes.FirstOrDefault(volume => string.Equals(volume.Root, root, StringComparison.OrdinalIgnoreCase))
            ?? volumes.FirstOrDefault();
    }

    private static T? TryRead<T>(Func<T> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return default; }
    }
}
