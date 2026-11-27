using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WinCoreManager.Services.DiskSpace;
using WinCoreManager.Services.Hardware;

namespace VisualHarness;

internal static partial class Program
{
    private static int MeasureCandidateHost(string drive, string output)
    {
        var catalog = new DiskVolumeCatalog();
        var volume = catalog.Find(drive) ?? throw new IOException("Requested volume is not a local fixed drive.");
        var analysis = new DiskAnalysis();
        var root = new DiskScanTarget(volume);
        var quick = ReadDisk(root, accelerated: true);
        var cancelled = false;
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
        {
            try
            {
                analysis.Scan(new([root], false, false), progress =>
                {
                    if (progress.Elapsed.TotalMilliseconds >= 150 && progress.PreviewFiles.Count > 0)
                        cancellation.Cancel();
                }, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        }

        HostScan? fullVolume = null;
        if (Args.ContainsKey("full-volume"))
        {
            try { fullVolume = ReadDisk(root, accelerated: false); }
            catch (OperationCanceledException) { /* Keep the other read-only evidence if the full scan takes longer than the budget. */ }
        }

        var systemFolder = Environment.SystemDirectory;
        var systemVolume = catalog.Find(Path.GetPathRoot(systemFolder)!)!;
        var systemTarget = new DiskScanTarget(systemVolume, systemFolder);
        var fullFolder = ReadDisk(systemTarget, accelerated: false);
        var quickFolder = ReadDisk(systemTarget, accelerated: true);

        var wmi = new HardwareWmi();
        var disks = wmi.Read(HardwareWmi.Storage, "MSFT_Disk", "Number,BusType,SerialNumber", default);
        var media = wmi.Read(HardwareWmi.Storage, "MSFT_PhysicalDisk", "MediaType,BusType", default);
        using var storage = new StorageSensors(wmi, new WindowsDiskTemperatures(), new WindowsSmart());
        var sensorTime = Stopwatch.StartNew();
        var sensors = storage.Read(default);
        sensorTime.Stop();
        var readings = disks.Rows.Where(row => row.UInt("Number").HasValue).Select(row =>
        {
            var number = row.UInt("Number")!.Value;
            var bus = row.UInt("BusType");
            var native = new WindowsDiskTemperatures().Read(number, row.Text("SerialNumber"));
            var associated = wmi.Associated(HardwareWmi.Storage, row.Path, "MSFT_DiskToStorageReliabilityCounter",
                "MSFT_StorageReliabilityCounter", "Temperature,Wear,PowerOnHours", default);
            var group = sensors.Groups.FirstOrDefault(item => item.Id == $"disk/{number}");
            return new
            {
                disk = number, bus,
                beforeNativeTemperature = native.Values.Count,
                beforeWmiTemperature = associated.Rows is [var counter] && counter.Number("Temperature") is > 0,
                candidateTemperatures = group?.Readings.Count(field => field.Unit == "°C") ?? 0,
                candidateSmart = group?.Readings.Count(field => field.Source == WindowsSmart.NvmeSource) ?? 0,
                candidateActivity = group?.Readings.Count(field => field.Source == "Windows PhysicalDisk") ?? 0,
                condition = group?.Condition
            };
        }).ToArray();
        var evidence = new
        {
            drive, quick, fullVolume, cancelled, fullFolder, quickFolder,
            physicalSsds = media.Rows.Count(row => row.UInt("MediaType") == 4),
            storageDurationMs = sensorTime.ElapsedMilliseconds,
            storageGroups = sensors.Groups.Count,
            storageNotices = sensors.Notices.Count,
            readings
        };
        File.WriteAllText(output, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
        return cancelled && quick.Files > 0 && quick.BeforeCompletion && sensors.Groups.Count > 0 ? 0 : 1;

        HostScan ReadDisk(DiskScanTarget target, bool accelerated)
        {
            using var timeLimit = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            var previews = new List<DiskAnalysisProgress>();
            var completed = analysis.Scan(new([target], accelerated, false), previews.Add, timeLimit.Token);
            var result = completed.Volumes.Single();
            return new(result.Progress.Files, result.Progress.Directories, result.Progress.LogicalBytes,
                result.Progress.Inaccessible, result.Progress.OmittedFolders, completed.Duration.TotalMilliseconds,
                previews.Count, previews.Take(previews.Count - 1).Any(sample => sample.PreviewFiles.Count > 0 && sample.PreviewFolders.Count > 0));
        }
    }

    private sealed record HostScan(long Files, long Folders, long LogicalBytes, long Inaccessible,
        long Omitted, double DurationMs, int ProgressSamples, bool BeforeCompletion);
}
