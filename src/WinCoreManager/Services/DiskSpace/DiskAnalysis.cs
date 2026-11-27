using System.Diagnostics;
using System.IO;

namespace WinCoreManager.Services.DiskSpace;

internal sealed record DiskPreviewEntry(string Path, long Bytes);
internal sealed record DiskScanTarget(DiskVolume Volume, string? Folder = null);
internal sealed record DiskAnalysisOptions(IReadOnlyList<DiskScanTarget> Targets, bool Accelerated, bool FindDuplicates, bool AllLocalDrives = false);
internal sealed record DiskAnalysisProgress(DiskScanTarget Target, int TargetNumber, int TargetCount,
    long Files, long Folders, long Bytes, TimeSpan Elapsed, IReadOnlyList<DiskPreviewEntry> PreviewFiles,
    IReadOnlyList<DiskPreviewEntry> PreviewFolders, int? HashChecked = null, int? HashTotal = null);
internal sealed record DiskAnalysisResult(IReadOnlyList<DiskScanResult> Volumes, IReadOnlyList<DiskScanTarget> Unavailable,
    bool Accelerated, DuplicateScanResult? Duplicates, TimeSpan Duration);

internal sealed class DiskAnalysis
{
    private readonly DiskSpaceScanner scanner = new();

    public DiskAnalysisResult Scan(DiskAnalysisOptions options, Action<DiskAnalysisProgress> report, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var results = new List<DiskScanResult>();
        var unavailable = new List<DiskScanTarget>();
        var duplicates = options.FindDuplicates ? new DuplicateSearch() : null;
        var files = 0L;
        var folders = 0L;
        var bytes = 0L;
        DiskPreviewEntry[] topFiles = [], topFolders = [];

        for (var i = 0; i < options.Targets.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var target = options.Targets[i];
            try
            {
                var result = scanner.Scan(target, options.Accelerated, state =>
                {
                    report(new(target, i + 1, options.Targets.Count, files + state.Files, folders + state.Directories,
                        bytes + state.LogicalBytes, clock.Elapsed, Merge(topFiles, state.PreviewFiles),
                        Merge(topFolders, state.PreviewFolders)));
                }, duplicates is null ? null : file => duplicates.Add(target.Volume, file), token);
                results.Add(result);
                files += result.Progress.Files;
                folders += result.Progress.Directories;
                bytes += result.Progress.LogicalBytes;
                topFiles = Merge(topFiles, result.Progress.PreviewFiles);
                topFolders = Merge(topFolders, result.Progress.PreviewFolders);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                unavailable.Add(target);
            }
        }

        var reviewed = results.Count > 0 ? duplicates?.Find((checkedCount, total) =>
            report(new(options.Targets[^1], options.Targets.Count, options.Targets.Count, files, folders, bytes,
                clock.Elapsed, topFiles, topFolders, checkedCount, total)), token) : null;
        return new(results, unavailable, options.Accelerated, reviewed, clock.Elapsed);
    }

    private static DiskPreviewEntry[] Merge(IEnumerable<DiskPreviewEntry> previous, IEnumerable<DiskPreviewEntry> current) =>
        previous.Concat(current).OrderByDescending(entry => entry.Bytes).Take(8).ToArray();
}
