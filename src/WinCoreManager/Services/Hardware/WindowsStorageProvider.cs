using WinCoreManager.Localization;
namespace WinCoreManager.Services.Hardware;

internal sealed class WindowsStorageProvider(IHardwareWmi wmi) : IStorageProvider
{
    internal const string LegacyDisk = "WMI Win32_DiskDrive";
    internal const string StorageDisk = "Windows Storage · MSFT_Disk";
    private const string PhysicalDisk = "Windows Storage · MSFT_PhysicalDisk";
    internal const string DiskColumns = "Number,Model,Manufacturer,FirmwareVersion,Size,BusType,HealthStatus,UniqueId,UniqueIdFormat,SerialNumber";
    internal const string LegacyColumns = "Index,Model,Manufacturer,FirmwareRevision,Size,InterfaceType";

    public HardwareSection Read(CancellationToken token) => Read(token, out _);

    internal HardwareSection Read(CancellationToken token, out WmiResult modern)
    {
        var time = DateTimeOffset.Now;
        var legacy = wmi.Read(HardwareWmi.Cim, "Win32_DiskDrive", LegacyColumns, token);
        modern = wmi.Read(HardwareWmi.Storage, "MSFT_Disk", DiskColumns, token);
        var media = wmi.Read(HardwareWmi.Storage, "MSFT_PhysicalDisk", "UniqueId,UniqueIdFormat,Size,MediaType,SerialNumber,Manufacturer", token);
        var topology = ReadPartitions(token);
        var devices = Assemble(legacy, modern, media, topology, time);
        var present = devices.Count > 0 ? ReadingState.Available : modern.State == ReadingState.NoReading ? legacy.State : modern.State;
        return new(LocalizationService.Source("Message.Storage"), devices.Count == 0 ? "" : LocalizationService.Source("Message.ReportedDrives01", devices.Count, devices[0].Name), devices, present);
    }

    internal static IReadOnlyList<HardwareDevice> Assemble(WmiResult legacy, WmiResult modern, WmiResult media,
        IReadOnlyDictionary<uint, List<HardwareDevice>> partitions, DateTimeOffset time)
    {
        var legacyDisks = UniqueByNumber(legacy.Rows, "Index");
        var modernDisks = UniqueByNumber(modern.Rows, "Number");
        var devices = new List<HardwareDevice>();
        foreach (var number in legacyDisks.Keys.Union(modernDisks.Keys).Order())
        {
            legacyDisks.TryGetValue(number, out var old);
            modernDisks.TryGetValue(number, out var current);
            var legacyModel = old?.Text("Model");
            var modernModel = current?.Text("Model");
            var legacySize = InventoryText.Positive(old?.Number("Size"));
            var modernSize = InventoryText.Positive(current?.Number("Size"));
            var agree = old is not null && current is not null
                && (legacyModel is null || modernModel is null || string.Equals(legacyModel, modernModel, StringComparison.OrdinalIgnoreCase))
                && (legacySize is null || modernSize is null || legacySize == modernSize);
            var first = current ?? old!;
            var firstSource = current is null ? LegacyDisk : StorageDisk;
            var name = first.Text("Model") ?? (agree ? legacyModel : null);
            var physical = current is null ? null : MatchPhysical(current, modern.Rows, media.Rows);
            var medium = physical?.UInt("MediaType");
            var bus = current?.UInt("BusType");
            var type = bus == 17 ? "NVMe" : medium == 3 ? "HDD" : medium == 4 && bus == 11 ? "SSD SATA" : medium == 4 ? "SSD" : null;
            var manufacturer = Manufacturer(first.Text("Manufacturer"));
            var firmware = first.Text(current is null ? "FirmwareRevision" : "FirmwareVersion");
            var kind = bus is 14 or 15 or 16 ? LocalizationService.Source("Message.VirtualOrPooledDrive") : current?.UInt("BusType") == 9 ? LocalizationService.Source("Message.ISCSILogicalDrive") : LocalizationService.Source("Message.DrivePresentedByWindows");
            devices.Add(new InventoryDevice($"disk/{number}", HardwareKind.Disk, name ?? LocalizationService.Source("Message.Drive0ModelUnavailable", number), time, name is null)
                .Add(LocalizationService.Source("Message.DriveNumber"), number, firstSource)
                .Add(LocalizationService.Source("Message.Model"), name, current is not null && modernModel is null && agree ? LegacyDisk : firstSource)
                .Add(LocalizationService.Source("Message.Manufacturer"), manufacturer ?? Manufacturer(physical?.Text("Manufacturer")) ?? (agree ? Manufacturer(old?.Text("Manufacturer")) : null),
                    manufacturer is not null ? firstSource : Manufacturer(physical?.Text("Manufacturer")) is not null ? PhysicalDisk : agree ? LegacyDisk : firstSource)
                .Add(LocalizationService.Source("Message.Capacity"), InventoryText.Positive(first.Number("Size")) ?? (agree ? legacySize : null), modernSize is null && agree ? LegacyDisk : firstSource, "B")
                .Add(LocalizationService.Source("Message.Type"), type ?? LocalizationService.Source("Message.Unknown2"), bus == 17 ? StorageDisk : PhysicalDisk, localizeValue: type is null)
                .Add(LocalizationService.Source("Message.Presentation"), kind, firstSource)
                .Add("Interface", Bus(bus) ?? (current is null || agree ? old?.Text("InterfaceType") : null), Bus(bus) is null ? LegacyDisk : StorageDisk)
                .Add("Firmware", firmware ?? (agree ? old?.Text("FirmwareRevision") : null), firmware is null && agree ? LegacyDisk : firstSource)
                .Add(LocalizationService.Source("Message.ReportedHealth"), Health(current?.UInt("HealthStatus")), StorageDisk, absent: modern.State == ReadingState.Available ? ReadingState.NoReading : modern.State, localizeValue: true)
                .Build(partitions.GetValueOrDefault(number)));
        }
        return devices;
    }

    internal static WmiRow? MatchPhysical(WmiRow disk, IReadOnlyList<WmiRow> disks, IReadOnlyList<WmiRow> physical)
    {
        var size = InventoryText.Positive(disk.Number("Size"));
        if (!size.HasValue) return null;
        var id = disk.Text("UniqueId");
        var format = disk.UInt("UniqueIdFormat");
        var serial = disk.Text("SerialNumber");
        var byId = id is not null && format.HasValue
            && disks.Count(row => row.Text("UniqueId") == id && row.UInt("UniqueIdFormat") == format) == 1
            ? physical.Where(row => row.Text("UniqueId") == id && row.UInt("UniqueIdFormat") == format && row.Number("Size") == size).ToArray() : [];
        if (byId is [var direct] && (serial is null || direct.Text("SerialNumber") is not { } compared || serial == compared)) return direct;
        if (serial is null || disks.Count(row => row.Text("SerialNumber") == serial) != 1) return null;
        var bySerial = physical.Where(row => row.Text("SerialNumber") == serial && row.Number("Size") == size
            && (id is null || row.Text("UniqueId") is not { } otherId || id == otherId)).ToArray();
        return bySerial is [var matched] ? matched : null;
    }

    internal static string? UniqueSerial(WmiRow? disk, IReadOnlyList<WmiRow> disks)
    {
        var serial = disk?.Text("SerialNumber");
        return serial is not null && disks.Count(row => string.Equals(row.Text("SerialNumber"), serial, StringComparison.OrdinalIgnoreCase)) == 1
            ? serial : null;
    }

    private Dictionary<uint, List<HardwareDevice>> ReadPartitions(CancellationToken token)
    {
        const string partitionSource = "Windows Storage · MSFT_Partition";
        const string volumeSource = "Windows Storage · MSFT_Volume";
        var time = DateTimeOffset.Now;
        var partitions = wmi.Read(HardwareWmi.Storage, "MSFT_Partition", "DiskNumber,PartitionNumber,DriveLetter,AccessPaths,Size", token);
        if (partitions.State != ReadingState.Available) return LegacyPartitions(time, token);
        var volumes = wmi.Read(HardwareWmi.Storage, "MSFT_Volume", "Path,DriveLetter,FileSystem,Size,SizeRemaining", token);
        var result = new Dictionary<uint, List<HardwareDevice>>();
        foreach (var row in partitions.Rows)
        {
            if (row.UInt("DiskNumber") is not { } disk || row.UInt("PartitionNumber") is not { } partition) continue;
            var id = $"disk/{disk}/part/{partition}";
            var letter = Letter(row.Get("DriveLetter"));
            var accessPaths = row.Get("AccessPaths") as string[] ?? [];
            var matches = volumes.Rows.Where(volume =>
                volume.Get("Path") is string path && accessPaths.Contains(path, StringComparer.OrdinalIgnoreCase)
                || letter is not null && Letter(volume.Get("DriveLetter")) == letter).ToArray();
            var children = matches is [var volume]
                ? new[] { Volume(id, letter ?? Letter(volume.Get("DriveLetter")), volume, "SizeRemaining", time, volumeSource) } : [];
            AddPartition(result, disk, new InventoryDevice(id, HardwareKind.Disk, LocalizationService.Source("Message.Partition0", partition), time, localizeName: true)
                .Add(LocalizationService.Source("Message.PartitionCapacity"), InventoryText.Positive(row.Number("Size")), partitionSource, "B")
                .Add(LocalizationService.Source("Message.StorageDriveLetter"), letter, partitionSource)
                .Build(children));
        }
        return result;
    }

    private Dictionary<uint, List<HardwareDevice>> LegacyPartitions(DateTimeOffset time, CancellationToken token)
    {
        const string partitionSource = "WMI Win32_DiskPartition";
        const string volumeSource = "WMI Win32_DiskPartitionToLogicalDisk";
        var partitions = wmi.Read(HardwareWmi.Cim, "Win32_DiskPartition", "DiskIndex,Index,Size", token);
        var volumes = wmi.Read(HardwareWmi.Cim, "Win32_LogicalDisk", "DeviceID,Size,FreeSpace,FileSystem", token);
        var links = wmi.Read(HardwareWmi.Cim, "Win32_LogicalDiskToPartition", "Antecedent,Dependent", token);
        var result = new Dictionary<uint, List<HardwareDevice>>();
        foreach (var row in partitions.Rows)
        {
            if (row.UInt("DiskIndex") is not { } disk || row.UInt("Index") is not { } partition) continue;
            var id = $"disk/{disk}/part/{partition}";
            var paths = links.Rows.Where(link => string.Equals(row.Path, HardwareWmi.RelativePath(link.Get("Antecedent")), StringComparison.OrdinalIgnoreCase))
                .Select(link => HardwareWmi.RelativePath(link.Get("Dependent"))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            var children = volumes.Rows.Where(volume => paths.Contains(volume.Path))
                .Select(volume => Volume(id, Letter(volume.Get("DeviceID")), volume, "FreeSpace", time, volumeSource)).ToArray();
            AddPartition(result, disk, new InventoryDevice(id, HardwareKind.Disk, LocalizationService.Source("Message.Partition0", partition + 1), time, localizeName: true)
                .Add(LocalizationService.Source("Message.PartitionCapacity"), InventoryText.Positive(row.Number("Size")), partitionSource, "B").Build(children));
        }
        return result;
    }

    private static HardwareDevice Volume(string partitionId, string? letter, WmiRow volume, string freeKey, DateTimeOffset time, string source)
    {
        var size = InventoryText.Positive(volume.Number("Size"));
        var free = volume.Number(freeKey);
        return new InventoryDevice(partitionId + "/volume/" + letter, HardwareKind.Disk, letter is null ? LocalizationService.Source("Message.VolumeWithoutADriveLetter") : $"Volume {letter}", time, letter is null)
            .Add(LocalizationService.Source("Message.FileSystem"), volume.Text("FileSystem"), source)
            .Add(LocalizationService.Source("Message.VolumeCapacity"), size, source, "B")
            .Add(LocalizationService.Source("Message.FreeSpace"), free <= size ? free : null, source, "B").Build();
    }

    private static void AddPartition(Dictionary<uint, List<HardwareDevice>> result, uint number, HardwareDevice partition)
    {
        if (!result.TryGetValue(number, out var list)) result[number] = list = [];
        list.Add(partition);
    }

    private static Dictionary<uint, WmiRow> UniqueByNumber(IEnumerable<WmiRow> rows, string property) => rows
        .Where(r => r.UInt(property).HasValue).GroupBy(r => r.UInt(property)!.Value)
        .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single());

    private static string? Manufacturer(string? value) => value is "(Standard disk drives)" or "(Unidades de disco padrão)" ? null : value;
    internal static string? Letter(object? value)
    {
        var text = value?.ToString();
        return text is { Length: 1 or 2 } && char.IsAsciiLetter(text[0]) && (text.Length == 1 || text[1] == ':')
            ? $"{char.ToUpperInvariant(text[0])}:" : null;
    }

    internal static string? Health(uint? value) => value switch { 0 => LocalizationService.Source("Message.Healthy"), 1 => LocalizationService.Source("Message.Warning"), 2 => LocalizationService.Source("Message.Unhealthy"), 5 => LocalizationService.Source("Message.Unknown2"), _ => null };
    internal static string? Bus(uint? value) => value switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 6 => "Fibre Channel", 7 => "USB", 8 => "RAID",
        9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC", 14 => LocalizationService.Source("Message.Virtual"), 15 => LocalizationService.Source("Message.FileBackedVirtualDrive"),
        16 => "Storage Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS", 20 => "NVMe-oF", _ => null
    };
}
