using System.IO;

namespace WinCoreManager.Services.DiskSpace;

internal sealed class DiskDirectory(string name, DiskDirectory? parent)
{
    private List<DiskDirectory>? children;

    public string Name { get; } = name;
    public DiskDirectory? Parent { get; } = parent;
    public IReadOnlyList<DiskDirectory> Children => children is null ? Array.Empty<DiskDirectory>() : children;
    public long DirectBytes { get; set; }
    public long DirectFiles { get; set; }
    public long LogicalBytes { get; set; }
    public long AllocatedBytes { get; set; }
    public long FileCount { get; set; }
    public bool Inaccessible { get; set; }
    public bool Omitted { get; set; }
    public int Depth { get; } = parent is null ? 0 : parent.Depth + 1;

    public string Path
    {
        get
        {
            if (Parent is null)
                return Name;

            var parts = new Stack<string>();
            for (DiskDirectory? directory = this; directory is not null; directory = directory.Parent)
                parts.Push(directory.Name);
            return System.IO.Path.Join(parts.ToArray().AsSpan());
        }
    }

    public DiskDirectory Add(string childName)
    {
        var child = new DiskDirectory(childName, this);
        (children ??= []).Add(child);
        return child;
    }

    public void SortChildren() => children?.Sort(static (left, right) =>
    {
        var size = right.LogicalBytes.CompareTo(left.LogicalBytes);
        return size != 0 ? size : StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name);
    });
}

internal sealed record DiskFile(DiskDirectory Directory, string Name, long LogicalBytes, long? AllocatedBytes, long ModifiedFileTime)
{
    public UInt128 Identity { get; init; }
    public string Path => System.IO.Path.Join(Directory.Path, Name);
    public string Extension => System.IO.Path.GetExtension(Name);
    public DateTime? Modified => ModifiedFileTime is > 0 and <= 2650467743999999999
        ? DateTime.FromFileTimeUtc(ModifiedFileTime).ToLocalTime()
        : null;
}

internal sealed record DiskScanProgress(
    long Files, long Directories, long Inaccessible, long SkippedReparse, long DuplicateLinks,
    long MissingAllocation, long MissingIdentity, long LogicalBytes, long AllocatedBytes, TimeSpan Elapsed)
{
    public IReadOnlyList<DiskPreviewEntry> PreviewFiles { get; init; } = [];
    public IReadOnlyList<DiskPreviewEntry> PreviewFolders { get; init; } = [];
    public long OmittedFolders { get; init; }
    public bool Partial => OmittedFolders > 0 || Inaccessible > 0 || SkippedReparse > 0 || MissingAllocation > 0 || MissingIdentity > 0;
}

internal sealed record DiskScanResult(DiskVolume Volume, DiskDirectory Root, IReadOnlyList<DiskFile> Largest, DiskScanProgress Progress)
{
    public IReadOnlyList<DiskDirectory> LargestFolders { get; init; } = [];
}
