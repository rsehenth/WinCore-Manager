using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace WinCoreManager.Services.DiskSpace;

internal sealed record DuplicateFile(DiskVolume Volume, DiskFile File);
internal sealed record DuplicateGroup(long Bytes, IReadOnlyList<DuplicateFile> Files);
internal sealed record DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, int Candidates, int Inaccessible, long Omitted);

internal sealed class DuplicateSearch(int limit = DuplicateSearch.MaxCandidates)
{
    internal const int MaxCandidates = 250_000;
    private readonly List<DuplicateFile> files = [];
    private long omitted;

    public void Add(DiskVolume volume, DiskFile file)
    {
        if (files.Count < limit)
            files.Add(new(volume, file));
        else
            omitted++;
    }

    public DuplicateScanResult Find(Action<int, int> report, CancellationToken token)
    {
        var bySize = new Dictionary<long, List<DuplicateFile>>();
        foreach (var candidate in files)
        {
            token.ThrowIfCancellationRequested();
            if (!bySize.TryGetValue(candidate.File.LogicalBytes, out var bucket))
                bySize.Add(candidate.File.LogicalBytes, bucket = []);
            bucket.Add(candidate);
        }

        var candidates = bySize.Values.Where(bucket => bucket.Count > 1)
            .OrderByDescending(bucket => bucket[0].File.LogicalBytes).ToArray();
        var groups = new List<DuplicateGroup>();
        var unavailable = 0;
        var checkedCount = 0;
        var total = candidates.Sum(bucket => bucket.Count);
        var update = Stopwatch.StartNew();
        report(checkedCount, total);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            foreach (var bucket in candidates)
            {
                var byHash = new Dictionary<string, List<DuplicateFile>>(StringComparer.Ordinal);
                foreach (var candidate in bucket)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var hash = Hash(candidate, buffer, token);
                        if (hash is null)
                            unavailable++;
                        else
                        {
                            if (!byHash.TryGetValue(hash, out var identical))
                                byHash.Add(hash, identical = []);
                            identical.Add(candidate);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        unavailable++;
                    }
                    checkedCount++;
                    if (update.ElapsedMilliseconds < 200)
                        continue;
                    report(checkedCount, total);
                    update.Restart();
                }
                foreach (var group in byHash.Values.Where(group => group.Count > 1))
                    groups.Add(new(bucket[0].File.LogicalBytes, group));
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
        token.ThrowIfCancellationRequested();
        report(checkedCount, total);
        return new(groups, total, unavailable, omitted);
    }

    private static string? Hash(DuplicateFile candidate, byte[] buffer, CancellationToken token) =>
        DiskScanPaths.WithFile(candidate.Volume, candidate.File, handle =>
        {
            if (!NativeDiskDirectory.MatchesFile(handle, candidate.File))
                return null;
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var offset = 0L;
            while (offset < candidate.File.LogicalBytes)
            {
                token.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(handle, buffer, offset);
                if (read == 0)
                    return null;
                sha.AppendData(buffer.AsSpan(0, read));
                offset += read;
            }
            return offset == candidate.File.LogicalBytes && NativeDiskDirectory.MatchesFile(handle, candidate.File)
                ? Convert.ToHexString(sha.GetHashAndReset()) : null;
        });
}
