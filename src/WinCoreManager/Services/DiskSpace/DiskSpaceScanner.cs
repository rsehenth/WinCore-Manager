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

    // Test and visual fixtures only. Production starts exclusively from a catalogued volume.
    internal DiskScanResult ScanFixture(string root, Action<DiskScanProgress>? progress, CancellationToken token)
    {
        var path = Path.GetFullPath(root);
        var volume = new DiskVolume(Path.GetPathRoot(path)!, "", "", null, null);
        return ScanRoot(volume, path, NativeDiskDirectory.OpenAbsolute(@"\\?\" + path), progress, token);
    }

    private DiskScanResult ScanRoot(DiskVolume volume, string root, SafeFileHandle handle,
        Action<DiskScanProgress>? progress, CancellationToken token)
    {
        using (handle)
        {
            var state = new ScanState(Math.Clamp(rankingLimit, 1, DefaultRankingLimit),
                NativeDiskDirectory.VolumeSupportsHardLinks(handle), progress, token);
            var tree = new DiskDirectory(root, null);
            state.Walk(tree, handle);
            var ranking = state.Ranking.UnorderedItems.Select(item => item.Element)
                .OrderByDescending(file => file.LogicalBytes)
                .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new(volume, tree, ranking, state.Publish());
        }
    }

    private sealed class Frame(DiskDirectory node, SafeFileHandle handle)
    {
        public DiskDirectory Node { get; } = node;
        public SafeFileHandle Handle { get; } = handle;
        public int NextChild { get; set; }
    }

    private sealed class ScanState(int limit, bool hardLinks, Action<DiskScanProgress>? report, CancellationToken token)
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly HashSet<ulong> shortIds = [];
        private readonly HashSet<UInt128> extendedIds = [];
        private long lastReport;
        private long files, directories, inaccessible, reparse, duplicate, unknownAllocation, unknownId, logical, allocated;
        private int entriesSinceUpdate;

        public PriorityQueue<DiskFile, long> Ranking { get; } = new();

        public void Walk(DiskDirectory root, SafeFileHandle handle)
        {
            var frames = new Stack<Frame>();
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

            if (hardLinks)
            {
                var id = entry.Id != 0 ? entry.Id : NativeDiskDirectory.ReadIdentity(frame.Handle, entry.Name);
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
                unknownId, logical, allocated, clock.Elapsed);
            lastReport = clock.ElapsedMilliseconds;
            report?.Invoke(current);
            token.ThrowIfCancellationRequested();
            return current;
        }
    }
}
