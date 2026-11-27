using System.Diagnostics;
using System.IO;
using WinCoreManager.Services.DiskSpace;

namespace VisualHarness;

internal static partial class Program
{
    private static int MeasureDiskScan(int count, string output)
    {
        var scratch = Directory.CreateTempSubdirectory("wincore-disk-bench-");
        try
        {
            var sample = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
            for (var index = 0; index < count;)
            {
                var folder = scratch.CreateSubdirectory($"folder-{index / 500:0000}");
                for (var i = 0; i < 500 && index < count; i++, index++)
                {
                    using var file = File.Create(Path.Combine(folder.FullName, $"file-{i:000}.dat"));
                    file.Write(sample.AsSpan(0, index % sample.Length));
                }
            }

            var scanner = new DiskSpaceScanner();
            var progress = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var result = scanner.ScanFixture(scratch.FullName, _ => progress++, CancellationToken.None);
            watch.Stop();
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            File.WriteAllLines(output, new string[]
            {
                $"files={result.Progress.Files}",
                $"directories={result.Progress.Directories}",
                $"logicalBytes={result.Root.LogicalBytes}",
                $"ranking={result.Largest.Count}",
                $"filesystem={new DriveInfo(Path.GetPathRoot(scratch.FullName)!).DriveFormat}",
                $"durationMs={watch.ElapsedMilliseconds}",
                $"threadAllocatedBytes={allocated}",
                $"progressSamples={progress}",
                $"inaccessible={result.Progress.Inaccessible}",
            });
            return result.Progress.Files == count && !result.Progress.Partial ? 0 : 1;
        }
        finally { scratch.Delete(recursive: true); }
    }
}
