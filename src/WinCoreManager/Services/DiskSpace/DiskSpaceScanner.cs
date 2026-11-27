using System.Buffers;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace WinCoreManager.Services.DiskSpace;

internal sealed class DiskSpaceScanner(int rankingLimit = DiskSpaceScanner.DefaultRankingLimit)
{
    public const int DefaultRankingLimit = 1000;

    public DiskScanResult Scan(DiskVolume volume, Action<DiskScanProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (new DiskVolumeCatalog().Find(volume.Root) is not { } current
            || !string.Equals(current.VolumePath, volume.VolumePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected fixed volume is unavailable.");

        token.ThrowIfCancellationRequested();
        return ScanRoot(current, current.Root, NativeDiskDirectory.OpenAbsolute(current.VolumePath), progress, token);
    }

    internal DiskScanResult Scan(DiskScanTarget target, bool accelerated, Action<DiskScanProgress>? progress,
        Action<DiskFile>? candidates, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var volume = new DiskVolumeCatalog().Find(target.Volume.Root);
        if (volume is null || !string.Equals(volume.VolumePath, target.Volume.VolumePath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The selected fixed volume is unavailable.");

        var root = target.Folder ?? volume.Root;
        return ScanRoot(volume, root, DiskScanPaths.Open(volume, root), progress, token, accelerated, candidates);
    }

    // Test and visual fixtures only. Production starts exclusively from a catalogued volume.
    internal DiskScanResult ScanFixture(string root, Action<DiskScanProgress>? progress, CancellationToken token,
        bool accelerated = false, Action<DiskFile>? candidates = null)
    {
        var path = Path.GetFullPath(root);
        var volume = new DiskVolume(Path.GetPathRoot(path)!, "", "", null, null);
        return ScanRoot(volume, path, NativeDiskDirectory.OpenAbsolute(@"\\?\" + path), progress, token, accelerated, candidates);
    }

    private DiskScanResult ScanRoot(DiskVolume volume, string root, SafeFileHandle handle,
        Action<DiskScanProgress>? progress, CancellationToken token, bool accelerated = false, Action<DiskFile>? candidates = null)
    {
        using (handle)
        {
            var state = new ScanState(Math.Clamp(rankingLimit, 1, DefaultRankingLimit),
                NativeDiskDirectory.VolumeSupportsHardLinks(handle), progress, candidates, token);
            var tree = new DiskDirectory(root, null);
            state.Walk(tree, handle, accelerated);
            var ranking = state.Ranking.UnorderedItems.Select(item => item.Element)
                .OrderByDescending(file => file.LogicalBytes)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(volume, tree, ranking, state.Publish())
            {
                LargestFolders = state.FolderRanking.UnorderedItems.OrderByDescending(item => item.Priority)
                    .ThenBy(item => item.Element.Name, StringComparer.OrdinalIgnoreCase).Select(item => item.Element).ToArray()
            };
        }
    }

    private sealed class Frame(DiskDirectory node, SafeFileHandle handle)
    {
        public DiskDirectory Node { get; } = node;
        public SafeFileHandle Handle { get; } = handle;
        public int NextChild { get; set; }
    }

    private sealed class ScanState(int limit, bool hardLinks, Action<DiskScanProgress>? report, Action<DiskFile>? candidates, CancellationToken token)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HashSet<ulong> shortIds = [];
        private readonly HashSet<UInt128> extendedIds = [];
        private long lastReport;
        private long files, directories, inaccessible, reparse, duplicate, unknownAllocation, unknownId, logical, allocated;
        private const int AcceleratedDepth = 3;
        private const int AcceleratedDirectories = 1800;
        private const int PreviewLimit = 8;
        public PriorityQueue<DiskDirectory, long> FolderRanking { get; } = new();
        private Stack<Frame>? liveFrames;
        private long omittedFolders;
        private int entriesSinceUpdate;

        public PriorityQueue<DiskFile, long> Ranking { get; } = new();

        public void Walk(DiskDirectory root, SafeFileHandle handle, bool accelerated)
        {
            if (accelerated)
            {
                WalkShallow(root, handle);
                return;
            }
            var frames = liveFrames = new Stack<Frame>();
            var buffer = ArrayPool<byte>.Shared.Rent(NativeDiskDirectory.BufferSize);
            try
            {
                // A single depth-first I/O worker, a shared buffer and one open handle per
                // active ancestor. Native opens are relative to a verified directory handle.
                var first = new Frame(root, handle);
                frames.Push(first);
                Publish();
                ReadDirectory(first, buffer);
                while (frames.TryPeek(out var frame))
                {
                    CheckProgress();
                    if (frame.NextChild < frame.Node.Children.Count)
                    {
                        var child = frame.Node.Children[frame.NextChild++];
                        SafeFileHandle childHandle;
                        try { childHandle = NativeDiskDirectory.OpenChild(frame.Handle, child.Name, directory: true); }
                        catch (IOException)
                        {
                            MarkInaccessible(child);
                            continue;
                        }

                        var next = new Frame(child, childHandle);
                        frames.Push(next);
                        ReadDirectory(next, buffer);
                        continue;
                    }

                    frame.Node.SortChildren();
                    AddFolder(frame.Node);
                    frames.Pop().Handle.Dispose();
                    if (frames.TryPeek(out var parent))
                    {
                        parent.Node.LogicalBytes += frame.Node.LogicalBytes;
                        parent.Node.AllocatedBytes += frame.Node.AllocatedBytes;
                        parent.Node.FileCount += frame.Node.FileCount;
                    }
                }
            }
            finally
            {
                while (frames.TryPop(out var frame))
                    frame.Handle.Dispose();
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }
        }

        private void WalkShallow(DiskDirectory root, SafeFileHandle handle)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(NativeDiskDirectory.BufferSize);
            var pending = new PriorityQueue<DiskDirectory, (int Depth, long ParentSize, long Sequence)>();
            var scanned = new List<DiskDirectory>();
            long sequence = 0;
            try
            {
                Publish();
                pending.Enqueue(root, (0, 0, sequence++));
                while (scanned.Count < AcceleratedDirectories && pending.TryDequeue(out var node, out _))
                {
                    CheckProgress();
                    if (node == root)
                        ReadDirectory(new(node, handle), buffer);
                    else
                    {
                        try
                        {
                            using var childHandle = DiskScanPaths.OpenDescendant(handle, node);
                            ReadDirectory(new(node, childHandle), buffer);
                        }
                        catch (IOException) { MarkInaccessible(node); }
                    }
                    scanned.Add(node);
                    AddFolder(node);
                    foreach (var child in node.Children)
                    {
                        token.ThrowIfCancellationRequested();
                        if (child.Depth <= AcceleratedDepth)
                            pending.Enqueue(child, (child.Depth, -node.DirectBytes, sequence++));
                        else
                            Omit(child);
                    }
                }
                foreach (var entry in pending.UnorderedItems)
                {
                    token.ThrowIfCancellationRequested();
                    Omit(entry.Element);
                }

                FolderRanking.Clear();
                for (var i = scanned.Count - 1; i >= 0; i--)
                {
                    token.ThrowIfCancellationRequested();
                    var node = scanned[i];
                    node.SortChildren();
                    AddFolder(node);
                    if (node.Parent is not { } parent)
                        continue;
                    parent.LogicalBytes += node.LogicalBytes;
                    parent.AllocatedBytes += node.AllocatedBytes;
                    parent.FileCount += node.FileCount;
                }
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        }

        private void Omit(DiskDirectory folder)
        {
            folder.Omitted = true;
            omittedFolders++;
        }

        private void AddFolder(DiskDirectory node)
        {
            if (node.Parent is null || node.LogicalBytes == 0)
                return;
            if (FolderRanking.Count < limit)
                FolderRanking.Enqueue(node, node.LogicalBytes);
            else if (FolderRanking.TryPeek(out _, out var smallest) && node.LogicalBytes > smallest)
                FolderRanking.EnqueueDequeue(node, node.LogicalBytes);
        }

        private IReadOnlyList<DiskPreviewEntry> FolderPreview()
        {
            var entries = FolderRanking.UnorderedItems.Select(item => (item.Element, item.Priority)).ToList();
            long activeBytes = 0;
            if (liveFrames is not null)
            {
                foreach (var frame in liveFrames)
                {
                    activeBytes += frame.Node.LogicalBytes;
                    if (frame.Node.Parent is not null && activeBytes > 0)
                        entries.Add((frame.Node, activeBytes));
                }
            }
            return entries.OrderByDescending(entry => entry.Priority).Take(PreviewLimit)
                .Select(entry => new DiskPreviewEntry(entry.Element.Path, entry.Priority)).ToArray();
        }

        private void ReadDirectory(Frame frame, byte[] buffer)
        {
            var info = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                while (NativeDiskDirectory.ReadBatch(frame.Handle, buffer, ref info))
                {
                    var offset = 0;
                    while (true)
                    {
                        var entry = NativeDiskDirectory.ReadEntry(buffer.AsSpan(offset), info);
                        if (entry.Name is not ("." or ".."))
                            ReadEntry(frame, entry);

                        CheckProgress();
                        if (entry.Next == 0)
                            break;
                        offset += entry.Next;
                    }
                }
                directories++;
            }
            catch (IOException) { MarkInaccessible(frame.Node); }
        }

        private void ReadEntry(Frame frame, NativeDiskDirectory.Entry entry)
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                reparse++;
                return;
            }

            var node = frame.Node;
            if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                node.Add(entry.Name.ToString());
                return;
            }

            var id = entry.Id;
            if (hardLinks)
            {
                if (id == 0)
                    id = NativeDiskDirectory.ReadIdentity(frame.Handle, entry.Name);
                if (id == 0)
                    unknownId++;
                else if (!(id >> 64 == 0 ? shortIds.Add((ulong)id) : extendedIds.Add(id)))
                {
                    duplicate++;
                    return;
                }
            }

            var allocation = entry.Allocation >= 0 ? entry.Allocation : (long?)null;
            if ((entry.Attributes & (FileAttributes.SparseFile | FileAttributes.Compressed)) != 0)
                allocation = NativeDiskDirectory.ReadSpecialAllocation(frame.Handle, entry.Name);

            files++;
            logical += entry.Length;
            allocated += allocation ?? 0;
            if (allocation is null)
                unknownAllocation++;

            node.DirectBytes += entry.Length;
            node.DirectFiles++;
            node.LogicalBytes += entry.Length;
            node.AllocatedBytes += allocation ?? 0;
            node.FileCount++;

            if (candidates is not null && entry.Length > 0
                && ((uint)entry.Attributes & (0x1000 | 0x40000 | 0x400000)) == 0)
                candidates(new DiskFile(node, entry.Name.ToString(), entry.Length, allocation, entry.LastWrite) { Identity = id });

            if (Ranking.Count < limit)
                Ranking.Enqueue(new(node, entry.Name.ToString(), entry.Length, allocation, entry.LastWrite), entry.Length);
            else if (Ranking.TryPeek(out _, out var smallest) && entry.Length > smallest)
                Ranking.EnqueueDequeue(new(node, entry.Name.ToString(), entry.Length, allocation, entry.LastWrite), entry.Length);
        }

        private void MarkInaccessible(DiskDirectory node)
        {
            node.Inaccessible = true;
            inaccessible++;
        }

        private void CheckProgress()
        {
            token.ThrowIfCancellationRequested();
            if ((++entriesSinceUpdate & 127) == 0 && clock.ElapsedMilliseconds - lastReport >= 200)
                Publish();
        }

        public DiskScanProgress Publish()
        {
            token.ThrowIfCancellationRequested();
            var current = new DiskScanProgress(files, directories, inaccessible, reparse, duplicate, unknownAllocation,
                unknownId, logical, allocated, clock.Elapsed)
            {
                OmittedFolders = omittedFolders
            };
            if (report is not null)
            {
                current = current with
                {
                    PreviewFiles = Ranking.UnorderedItems.OrderByDescending(item => item.Priority).Take(PreviewLimit)
                        .Select(item => new DiskPreviewEntry(item.Element.Path, item.Priority)).ToArray(),
                    PreviewFolders = FolderPreview()
                };
            }
            lastReport = clock.ElapsedMilliseconds;
            report?.Invoke(current);
            token.ThrowIfCancellationRequested();
            return current;
        }
    }
}
