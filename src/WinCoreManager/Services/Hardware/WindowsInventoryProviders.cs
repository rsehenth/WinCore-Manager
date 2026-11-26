using WinCoreManager.Localization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinCoreManager.Services.Hardware;

internal sealed class WindowsLicensingProvider(IHardwareWmi wmi) : ILicensingProvider
{
    private const string Filter = "ApplicationID = '55c92734-d682-4d71-983e-d6ec3f16059f' AND LicenseIsAddon = FALSE AND PartialProductKey IS NOT NULL";
    private const string Source = "WMI SoftwareLicensingProduct";

    internal WindowsActivationState ReadEditionActivation(string? edition, CancellationToken token) =>
        ReadEdition(edition, token).Activation;

    private (WindowsActivationState Activation, ReadingState Query) ReadEdition(string? edition, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(edition)) return (WindowsActivationState.Unknown, ReadingState.NoReading);

        var result = wmi.Read(HardwareWmi.Cim, "SoftwareLicensingProduct", "LicenseFamily,LicenseStatus,EvaluationEndDate", token, Filter);
        if (result.State != ReadingState.Available) return (WindowsActivationState.Unknown, result.State);

        var states = result.Rows
            .Where(row => string.Equals(row.Text("LicenseFamily"), edition, StringComparison.OrdinalIgnoreCase))
            .Select(row => row.UInt("LicenseStatus") switch
            {
                1 when edition.EndsWith("Eval", StringComparison.OrdinalIgnoreCase) || IsCurrentEvaluation(row.Get("EvaluationEndDate")) =>
                    WindowsActivationState.Grace,
                1 => WindowsActivationState.Activated,
                0 or 5 => WindowsActivationState.NotActivated,
                2 or 3 or 4 or 6 => WindowsActivationState.Grace,
                _ => WindowsActivationState.Unknown
            })
            .Distinct().ToArray();
        return states is [var state] && state != WindowsActivationState.Unknown
            ? (state, ReadingState.Available) : (WindowsActivationState.Unknown, ReadingState.NoReading);
    }

    private static bool IsCurrentEvaluation(object? value)
    {
        if (value is not string date || date.Length != 25 || date.Contains('*')) return false;
        try { return System.Management.ManagementDateTimeConverter.ToDateTime(date) > DateTime.Now; }
        catch (ArgumentException) { return false; }
    }

    public HardwareReading ReadActivation(string? edition, DateTimeOffset time, CancellationToken token)
    {
        var (activation, query) = ReadEdition(edition, token);
        var display = LocalizationService.Source(activation.StatusKey());
        return new("windows", LocalizationService.Source("Message.Activation"), display, Source, query,
            "", time) { DisplayOverride = display };
    }
}

internal interface IWindowsFacts
{
    string? Edition { get; }
    string? DisplayVersion { get; }
    uint? BuildRevision { get; }
    string? Firmware { get; }
    bool? SecureBoot { get; }
    string OsArchitecture { get; }
    Version KernelVersion { get; }
    TimeSpan Uptime { get; }
}

internal sealed partial class WindowsFacts : IWindowsFacts
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    public string? Edition => InventoryText.Clean(ReadRegistry(CurrentVersion, "EditionID") as string);
    public string? DisplayVersion => InventoryText.Clean(ReadRegistry(CurrentVersion, "DisplayVersion") as string);
    public uint? BuildRevision => ReadRegistry(CurrentVersion, "UBR") is int value && value >= 0 ? (uint)value : null;
    public string? Firmware => GetFirmwareType(out var type) ? type switch { 1 => "Legacy", 2 => "UEFI", _ => null } : null;
    public bool? SecureBoot => ReadRegistry(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled")
        switch { 1 => true, 0 => false, _ => null };
    public string OsArchitecture => RuntimeInformation.OSArchitecture.ToString();
    public Version KernelVersion => Environment.OSVersion.Version;
    public TimeSpan Uptime => TimeSpan.FromMilliseconds(Environment.TickCount64);

    private static object? ReadRegistry(string path, string value)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            return key?.GetValue(value);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFirmwareType(out uint type);
}

internal sealed class WindowsOsProvider(IHardwareWmi wmi, ILicensingProvider licensing, IWindowsFacts facts) : IOperatingSystemProvider
{
    internal WindowsIdentity ReadIdentity(CancellationToken token)
    {
        var os = ReadOperatingSystem(token);
        var row = os.Rows.FirstOrDefault();
        var (build, _) = ReadBuild(row, facts.KernelVersion);
        return new(row?.Text("Caption"), facts.Edition, facts.DisplayVersion, build);
    }

    private WmiResult ReadOperatingSystem(CancellationToken token) =>
        wmi.Read(HardwareWmi.Cim, "Win32_OperatingSystem", "Caption,Version,BuildNumber,InstallDate", token);

    private (string? Text, bool HasRevision) ReadBuild(WmiRow? row, Version kernel)
    {
        var reportedBuild = row?.Number("BuildNumber");
        var kernelBuild = (ulong)Math.Max(0, kernel.Build);
        var build = reportedBuild is > 0 ? reportedBuild.Value : kernelBuild;
        var revision = build == kernelBuild ? facts.BuildRevision : null;
        return (build == 0 ? null : revision is null ? $"{build}" : $"{build}.{revision}", revision.HasValue);
    }

    public HardwareSection Read(CancellationToken token)
    {
        var time = DateTimeOffset.Now;
        const string source = "WMI Win32_OperatingSystem";
        var os = ReadOperatingSystem(token);
        var processor = wmi.Read(HardwareWmi.Cim, "Win32_Processor", "Architecture", token);
        var architectures = processor.Rows.Select(r => InventoryText.Architecture(r.UInt("Architecture"))).Distinct().ToArray();
        var row = os.Rows.FirstOrDefault();
        var name = row?.Text("Caption");
        var edition = facts.Edition;
        var kernel = facts.KernelVersion;
        var reportedVersion = row?.Text("Version");
        var version = Version.TryParse(reportedVersion, out var valid) && valid.Major > 0 ? reportedVersion : kernel.ToString(3);
        var friendlyVersion = facts.DisplayVersion;
        var (build, hasRevision) = ReadBuild(row, kernel);
        var firmware = facts.Firmware;
        var secureBoot = firmware == "UEFI" ? facts.SecureBoot : null;
        var device = new InventoryDevice("windows", HardwareKind.Windows, name ?? "Windows", time)
            .Add(LocalizationService.Source("Message.Name"), name, source, absent: os.State == ReadingState.Available ? ReadingState.NoReading : os.State)
            .Add(LocalizationService.Source("Message.Edition"), edition, "Windows CurrentVersion · EditionID")
            .Add(LocalizationService.Source("Message.Version"), friendlyVersion ?? version, friendlyVersion is null ? (version == reportedVersion ? source : ".NET Environment.OSVersion") : "Windows CurrentVersion · DisplayVersion")
            .Add(LocalizationService.Source("Message.Build"), build,
                hasRevision ? LocalizationService.Source("Message.WindowsBuildAndUBR") : row?.Number("BuildNumber") is > 0 ? source : ".NET Environment.OSVersion")
            .Add(LocalizationService.Source("Message.SystemArchitecture"), facts.OsArchitecture, ".NET RuntimeInformation.OSArchitecture")
            .Add(LocalizationService.Source("Message.ProcessorArchitecture"), architectures is [var architecture] ? architecture : null, "WMI Win32_Processor",
                absent: processor.State == ReadingState.Available ? ReadingState.NoReading : processor.State)
            .Add(LocalizationService.Source("Message.ReportedInstallationDate"), row?.Date("InstallDate"), source, absent: os.State == ReadingState.Available ? ReadingState.NoReading : os.State)
            .Add(licensing.ReadActivation(edition, time, token))
            .Add(LocalizationService.Source("Message.BootMode"), firmware, "Windows GetFirmwareType")
            .Add("Secure Boot", secureBoot is null ? null : secureBoot.Value ? LocalizationService.Source("Message.Active") : LocalizationService.Source("Message.Disabled"),
                "Windows SecureBoot State", absent: firmware == "Legacy" ? ReadingState.Unsupported : ReadingState.NoReading)
            .Add(LocalizationService.Source("Message.Uptime"), facts.Uptime, LocalizationService.Source("Message.WindowsTimeSinceStartup"))
            .Build();
        return new("Sistema Operacional", string.Join(" · ", new[] { name ?? "Windows", friendlyVersion }.OfType<string>()), [device]);
    }
}

internal sealed class WindowsBoardProvider(IHardwareWmi wmi) : IBoardProvider
{
    public HardwareSection Read(CancellationToken token)
    {
        const string boardSource = "WMI Win32_BaseBoard · SMBIOS";
        const string biosSource = "WMI Win32_BIOS · SMBIOS";
        var time = DateTimeOffset.Now;
        var boards = wmi.Read(HardwareWmi.Cim, "Win32_BaseBoard", "Manufacturer,Product,Version", token);
        var firmwares = wmi.Read(HardwareWmi.Cim, "Win32_BIOS", "Manufacturer,SMBIOSBIOSVersion,ReleaseDate", token);
        var devices = new List<HardwareDevice>();
        foreach (var board in boards.Rows)
        {
            var model = board.Text("Product");
            devices.Add(new InventoryDevice($"board/{devices.Count}", HardwareKind.Board, model ?? LocalizationService.Source("Message.MotherboardModelUnavailable"), time, model is null)
                .Add(LocalizationService.Source("Message.Manufacturer"), board.Text("Manufacturer"), boardSource).Add(LocalizationService.Source("Message.Model"), model, boardSource)
                .Add(LocalizationService.Source("Message.BoardVersion"), board.Text("Version"), boardSource)
                .Add(LocalizationService.Source("Message.Chipset"), null, LocalizationService.Source("Message.ChipsetIdentificationUnavailable"), absent: ReadingState.Unsupported).Build());
        }
        foreach (var bios in firmwares.Rows)
        {
            devices.Add(new InventoryDevice($"firmware/{devices.Count}", HardwareKind.Board, "Firmware BIOS/UEFI", time)
                .Add(LocalizationService.Source("Message.Manufacturer"), bios.Text("Manufacturer"), biosSource)
                .Add(LocalizationService.Source("Message.FirmwareVersion"), bios.Text("SMBIOSBIOSVersion"), biosSource)
                .Add(LocalizationService.Source("Message.FirmwareDate"), bios.Date("ReleaseDate"), biosSource).Build());
        }
        return InventoryText.Section(LocalizationService.Source("Message.MotherboardAndFirmware"), devices, boards.State);
    }
}

internal sealed class WindowsCpuProvider(IHardwareWmi wmi) : ICpuProvider
{
    internal const string Columns = "Manufacturer,Name,Architecture,NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,CurrentClockSpeed,L2CacheSize,L3CacheSize,ProcessorId";
    public HardwareSection Read(CancellationToken token)
    {
        const string source = "WMI Win32_Processor";
        var time = DateTimeOffset.Now;
        var cpus = wmi.Read(HardwareWmi.Cim, "Win32_Processor", Columns, token);
        var devices = cpus.Rows.Select((row, index) =>
        {
            var name = row.Text("Name");
            var processorId = row.Text("ProcessorId");
            return new InventoryDevice($"cpu/{index}", HardwareKind.Cpu, name ?? LocalizationService.Source("Message.ProcessorModelUnavailable"), time, name is null)
                .Add(LocalizationService.Source("Message.Manufacturer"), row.Text("Manufacturer"), source)
                .Add(LocalizationService.Source("Message.Model"), name, source)
                .Add(LocalizationService.Source("Message.CpuArchitecture"), InventoryText.Architecture(row.UInt("Architecture")), source)
                .Add(LocalizationService.Source("Message.PhysicalCores"), InventoryText.Positive(row.Number("NumberOfCores")), source)
                .Add(LocalizationService.Source("Message.LogicalProcessors"), InventoryText.Positive(row.Number("NumberOfLogicalProcessors")), source)
                .Add(LocalizationService.Source("Message.ReportedBaseClock"), InventoryText.Positive(row.Number("MaxClockSpeed")), source, "MHz")
                .Add(LocalizationService.Source("Message.ReportedCurrentClock"), InventoryText.Positive(row.Number("CurrentClockSpeed")), source, "MHz")
                .Add("Cache L2", InventoryText.Positive(row.Number("L2CacheSize")), source, "KiB")
                .Add("Cache L3", InventoryText.Positive(row.Number("L3CacheSize")), source, "KiB")
                .Add(LocalizationService.Source("Message.CpuidSignature"), processorId is { Length: 16 } && processorId.All(Uri.IsHexDigit)
                    && row.UInt("Architecture") is 0 or 9 ? processorId : null, "SMBIOS · CPUID x86/x64")
                .Build();
        }).ToArray();
        return InventoryText.Section(LocalizationService.Source("Message.Processor"), devices, cpus.State);
    }
}

internal interface IMemoryFacts { ulong? Installed { get; } MemorySample? Read(); }
internal sealed record MemorySample(ulong Total, ulong Available, uint Load);

internal sealed partial class WindowsMemoryFacts : IMemoryFacts
{
    public ulong? Installed => GetPhysicallyInstalledSystemMemory(out var kib) && kib is > 0 and <= ulong.MaxValue / 1024 ? kib * 1024 : null;

    public MemorySample? Read()
    {
        var layout = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref layout) && layout.Total > 0 && layout.Available <= layout.Total && layout.Load <= 100
            ? new(layout.Total, layout.Available, layout.Load) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

internal sealed class WindowsRamProvider(IHardwareWmi wmi, IMemoryFacts memory) : IRamProvider
{
    public HardwareSection Read(CancellationToken token)
    {
        var time = DateTimeOffset.Now;
        const string source = "WMI Win32_PhysicalMemory · SMBIOS";
        var modules = wmi.Read(HardwareWmi.Cim, "Win32_PhysicalMemory", "Capacity,Manufacturer,PartNumber,DeviceLocator,ConfiguredClockSpeed,SMBIOSMemoryType", token);
        var installed = memory.Installed;
        var usable = memory.Read()?.Total;
        var children = modules.Rows.Select((row, index) =>
        {
            var capacity = InventoryText.Positive(row.Number("Capacity"));
            var part = row.Text("PartNumber");
            return new InventoryDevice($"ram/{index}", HardwareKind.Ram,
                    LocalizationService.Source("Message.Module0", index + 1) + (capacity is { } bytes ? $" · {HardwareReading.FormatBytes(bytes)}" : ""), time, localizeName: true)
                .Add(LocalizationService.Source("Message.Capacity"), capacity, source, "B")
                .Add(LocalizationService.Source("Message.Manufacturer"), row.Text("Manufacturer"), source)
                .Add(LocalizationService.Source("Message.ModelPartNumber"), part ?? LocalizationService.Source("Message.NotReportedBySMBIOS"), source, localizeValue: part is null)
                .Add("Slot", row.Text("DeviceLocator"), source)
                .Add(LocalizationService.Source("Message.Type"), MemoryType(row.UInt("SMBIOSMemoryType")), source)
                .Add(LocalizationService.Source("Message.ConfiguredSpeed"), InventoryText.Positive(row.Number("ConfiguredClockSpeed")), source, "MT/s").Build();
        }).ToArray();

        var types = modules.Rows.Select(r => MemoryType(r.UInt("SMBIOSMemoryType"))).OfType<string>().Distinct().ToArray();
        var speeds = modules.Rows.Select(r => InventoryText.Positive(r.Number("ConfiguredClockSpeed"))).OfType<ulong>().Distinct().ToArray();
        var name = installed is { } size
            ? LocalizationService.Source("Message.MemoryInstalledAmount", HardwareReading.FormatBytes(size))
            : LocalizationService.Source("Message.InstalledMemory");
        var device = new InventoryDevice("ram", HardwareKind.Ram, name, time, localizeName: true)
            .Add(LocalizationService.Source("Message.TotalInstalled"), installed, "Windows GetPhysicallyInstalledSystemMemory · SMBIOS", "B")
            .Add(LocalizationService.Source("Message.TotalUsable"), usable, "Windows GlobalMemoryStatusEx", "B")
            .Add(LocalizationService.Source("Message.ModulesDetected"), modules.State is ReadingState.Available or ReadingState.NoReading ? children.Length : null, source,
                absent: modules.State)
            .Add(LocalizationService.Source("Message.ReportedTypes"), types.Length > 0 ? string.Join(", ", types) : null, source)
            .Add(LocalizationService.Source("Message.ConfiguredSpeeds"), speeds.Length > 0 ? string.Join(", ", speeds) : null, source, "MT/s")
            .Build(children);
        return new(LocalizationService.Source("Message.MemoryRAM"), name, [device]);
    }

    internal static string? MemoryType(uint? type) => type switch
    {
        18 => "DDR", 19 => "DDR2", 20 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4",
        27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5",
        36 => "HBM3", _ => null
    };
}
