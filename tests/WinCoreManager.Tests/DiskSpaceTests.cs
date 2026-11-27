using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Services.DiskSpace;

namespace WinCoreManager.Tests;

public sealed class DiskSpaceTests
{
    [Fact]
    public void Windows_volume_is_preferred_and_fallback_is_available()
    {
        DiskVolume[] volumes = [new(@"C:\", "Data", "id1", 0, 0), new(@"D:\", "Windows", "id2", 1, 10)];
        Assert.Same(volumes[1], DiskVolumeCatalog.Preferred(volumes, @"D:\Windows"));
        Assert.Same(volumes[0], DiskVolumeCatalog.Preferred(volumes, @"X:\Windows"));
        Assert.Null(DiskVolumeCatalog.Preferred([], @"D:\Windows"));
        var actual = new DiskVolumeCatalog().List();
        Assert.All(actual, volume => Assert.Equal(DriveType.Fixed, new DriveInfo(volume.Root).DriveType));
        var windows = Path.GetPathRoot(Environment.SystemDirectory);
        if (actual.Any(volume => volume.Root == windows))
            Assert.Equal(windows, DiskVolumeCatalog.Preferred(actual, Environment.SystemDirectory)?.Root);
    }

    [Theory]
    [InlineData(@"\\server\share\")]
    [InlineData(@"\\?\C:\")]
    [InlineData(@"C:\arbitrary")]
    [InlineData("C:")]
    [InlineData(@"?:\")]
    public void Rejects_paths_that_are_not_fixed_drive_roots(string path)
    {
        Assert.Null(new DiskVolumeCatalog().Find(path));
    }

    [Fact]
    public void Scan_aggregates_empty_and_deep_folders_and_keeps_only_the_largest_files()
    {
        using var fixture = new Fixture();
        var deep = fixture.Root;
        for (var i = 0; i < 14; i++)
            deep = Directory.CreateDirectory(Path.Combine(deep, $"nested-directory-{i:00}")).FullName;
        Assert.True(deep.Length > 260);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "Empty"));
        fixture.Write("medium.dat", 80);
        fixture.Write("small.txt", 1);
        File.WriteAllBytes(Path.Combine(deep, "large.zip"), new byte[5000]);
        var progress = new List<DiskScanProgress>();

        var result = fixture.Scan(2, progress.Add);

        Assert.Equal(5081, result.Root.LogicalBytes);
        Assert.Equal(3, result.Root.FileCount);
        Assert.Equal(16, result.Progress.Directories);
        Assert.Equal(0, result.Progress.Inaccessible);
        Assert.Equal(new[] { "large.zip", "medium.dat" }, result.Largest.Select(file => file.Name));
        Assert.Equal(0, result.Root.Children[1].LogicalBytes);
        Assert.Equal(5000, result.Root.Children[0].LogicalBytes);
        Assert.Equal(result.Progress, progress[^1]);
        Assert.True(File.Exists(Path.Combine(deep, "large.zip")));
        Assert.Equal(80, new FileInfo(Path.Combine(fixture.Root, "medium.dat")).Length);
    }

    [Fact]
    public void Cancel_after_progress_stops_without_returning_a_result()
    {
        using var fixture = new Fixture();
        using var cancel = new CancellationTokenSource();
        fixture.Write("file.dat", 80);
        var progress = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Scan(3, _ =>
        {
            progress++;
            cancel.Cancel();
        }, cancel.Token));
        Assert.Equal(1, progress);
    }

    [Fact]
    public void Unavailable_volume_does_not_start_a_scan()
    {
        var scanner = new DiskSpaceScanner();
        var actual = new DiskVolumeCatalog().List();
        var missing = Enumerable.Range('A', 26).Select(letter => $"{(char)letter}:\\")
            .FirstOrDefault(root => actual.All(volume => volume.Root != root));
        if (missing is not null)
            Assert.Throws<IOException>(() => scanner.Scan(new(missing, "", "missing", 0, 0), null, default));
        if (actual.FirstOrDefault() is { } drive)
            Assert.Throws<IOException>(() => scanner.Scan(drive with { VolumePath = "old mapping" }, null, default));
    }

    [Fact]
    public void Production_scan_validates_and_opens_a_real_fixed_volume_before_enumeration()
    {
        var drive = DiskVolumeCatalog.Preferred(new DiskVolumeCatalog().List(), Environment.SystemDirectory);
        Assert.NotNull(drive);
        using var stop = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => new DiskSpaceScanner().Scan(drive, progress =>
        {
            Assert.Equal(0, progress.Files);
            stop.Cancel();
        }, stop.Token));
    }

    [Fact]
    public void Junction_is_skipped_and_hard_link_is_counted_once()
    {
        using var fixture = new Fixture();
        using var outside = new Fixture();
        fixture.Write("original.dat", 371);
        outside.Write("outside.dat", 1389);
        var junction = Path.Combine(fixture.Root, "junction");
        CreateJunction(junction, outside.Root);
        try
        {
            Assert.True(CreateHardLink(Path.Combine(fixture.Root, "hard.dat"), Path.Combine(fixture.Root, "original.dat"), 0));
            var result = fixture.Scan(10);
            Assert.Equal(1, result.Progress.SkippedReparse);
            Assert.Equal(1, result.Progress.DuplicateLinks);
            Assert.Equal(1, result.Progress.Files);
            Assert.Equal(371, result.Root.LogicalBytes);
            Assert.True(result.Progress.Partial);
            Assert.Empty(result.Root.Children);
            Assert.True(File.Exists(Path.Combine(outside.Root, "outside.dat")));
        }
        finally { Directory.Delete(junction); }
    }

    [Fact]
    public void Symbolic_link_cannot_be_opened_as_a_scan_directory()
    {
        using var fixture = new Fixture();
        var parent = fixture.Root;
        var name = "symbolic";
        var fixtureLink = Path.Combine(parent, name);
        var created = false;
        try
        {
            Directory.CreateSymbolicLink(fixtureLink, fixture.Root);
            created = true;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) == 1314) // No SeCreateSymbolicLinkPrivilege.
        {
            // The Windows compatibility symlink provides a read-only fixture for a standard user.
            parent = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users");
            name = "All Users";
        }
        try
        {
            Assert.NotNull(Directory.ResolveLinkTarget(Path.Combine(parent, name), returnFinalTarget: false));
            using var handle = NativeDiskDirectory.OpenAbsolute(@"\\?\" + parent);
            Assert.Throws<IOException>(() => NativeDiskDirectory.OpenChild(handle, name, directory: true));
            if (created)
                Assert.Equal(1, fixture.Scan(10).Progress.SkippedReparse);
        }
        finally { if (created) Directory.Delete(fixtureLink); }
    }

    [Fact]
    public void Denied_directory_is_reported_and_other_files_complete()
    {
        using var fixture = new Fixture();
        var denied = Directory.CreateDirectory(Path.Combine(fixture.Root, "denied"));
        File.WriteAllBytes(Path.Combine(denied.FullName, "hidden.dat"), new byte[245]);
        fixture.Write("included.dat", 87);
        using var identity = WindowsIdentity.GetCurrent();
        var restricted = denied.GetAccessControl();
        var rule = new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny);
        restricted.AddAccessRule(rule);
        denied.SetAccessControl(restricted);
        try
        {
            var result = fixture.Scan(10);
            Assert.True(result.Progress.Partial);
            Assert.Equal(1, result.Progress.Inaccessible);
            Assert.Equal(1, result.Progress.Directories);
            Assert.True(Assert.Single(result.Root.Children).Inaccessible);
            Assert.Equal(87, result.Root.LogicalBytes);
        }
        finally
        {
            restricted.RemoveAccessRuleSpecific(rule);
            denied.SetAccessControl(restricted);
        }
    }

    [Fact]
    public void Sparse_and_compressed_fixtures_report_logical_and_allocated_sizes()
    {
        using var fixture = new Fixture();
        var sparse = Path.Combine(fixture.Root, "synthetic-sparse.bin");
        var compressed = Path.Combine(fixture.Root, "compressed.bin");
        const long sparseSize = 5L * 1024 * 1024 * 1024;
        using (var handle = File.OpenHandle(sparse, FileMode.CreateNew, FileAccess.Write))
        {
            Assert.True(DeviceIoControl(handle, 0x900C4, null, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE
            RandomAccess.SetLength(handle, sparseSize);
            RandomAccess.Write(handle, new byte[] { 1, 2, 3 }, 0);
            var hole = BitConverter.GetBytes(65536L).Concat(BitConverter.GetBytes(sparseSize)).ToArray();
            Assert.True(DeviceIoControl(handle, 0x980C8, hole, hole.Length, 0, 0, out _, 0)); // FSCTL_SET_ZERO_DATA
            RandomAccess.FlushToDisk(handle);
        }
        using (var handle = File.OpenHandle(compressed, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            Assert.True(DeviceIoControl(handle, 0x9C040, [1, 0], 2, 0, 0, out _, 0)); // FSCTL_SET_COMPRESSION
            RandomAccess.Write(handle, Enumerable.Repeat((byte)42, 512 * 1024).ToArray(), 0);
            RandomAccess.FlushToDisk(handle);
        }

        var result = fixture.Scan(10);

        Assert.Equal(sparseSize + 512 * 1024, result.Root.LogicalBytes);
        Assert.Equal(new[] { "synthetic-sparse.bin", "compressed.bin" }, result.Largest.Select(file => file.Name));
        foreach (var file in result.Largest)
        {
            var low = GetCompressedFileSize(file.Path, out var high);
            Assert.NotEqual(uint.MaxValue, low);
            Assert.Equal((long)high << 32 | low, file.AllocatedBytes);
            Assert.True(file.AllocatedBytes < file.LogicalBytes,
                $"{file.Name}: {file.AllocatedBytes} allocated for {file.LogicalBytes} logical ({File.GetAttributes(file.Path)})");
        }
        Assert.Equal(result.Largest.Sum(file => file.AllocatedBytes), result.Root.AllocatedBytes);
    }

    [Fact]
    public void Explorer_uses_a_verified_local_containing_directory_and_never_follows_links()
    {
        using var fixture = new Fixture();
        fixture.Write("sample archive.zip", 33);
        var drive = new DiskVolumeCatalog().Find(Path.GetPathRoot(fixture.Root)!);
        Assert.NotNull(drive);
        var directory = new DiskDirectory(drive.Root, null);
        foreach (var segment in fixture.Root[drive.Root.Length..].Split('\\'))
            directory = directory.Add(segment);
        var file = new DiskFile(directory, "sample archive.zip", 33, null, 0);
        Assert.True(DiskExplorer.WithVerifiedLocation(drive, directory, file, (folder, name) =>
        {
            Assert.Equal(fixture.Root, folder);
            Assert.Equal(file.Name, name);
            return true;
        }));
        Assert.False(DiskExplorer.WithVerifiedLocation(drive, directory, file with { Name = @"..\file.exe" }, (_, _) => true));
        Assert.False(DiskExplorer.WithVerifiedLocation(drive, directory, file with { Name = "NUL.txt" }, (_, _) => true));
        var link = Path.Combine(fixture.Root, "indirect");
        CreateJunction(link, Path.GetDirectoryName(fixture.Root)!);
        try
        {
            Assert.False(DiskExplorer.WithVerifiedLocation(drive, directory.Add("indirect"), null, (_, _) => true));
        }
        finally { Directory.Delete(link); }
    }

    private static void CreateJunction(string junction, string target)
    {
        var junctionCommand = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{junction}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(junctionCommand)!;
        Assert.True(process.WaitForExit(5000));
        Assert.Equal(0, process.ExitCode);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, nint security);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, int inputSize,
        nint output, int outputSize, out int bytes, nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "GetCompressedFileSizeW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSize(string path, out uint high);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("wincore-disk-test-").FullName;

        public void Write(string name, int length) => File.WriteAllBytes(Path.Combine(Root, name), new byte[length]);

        public DiskScanResult Scan(int limit, Action<DiskScanProgress>? progress = null, CancellationToken token = default) =>
            new DiskSpaceScanner(limit).ScanFixture(Root, progress, token);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
