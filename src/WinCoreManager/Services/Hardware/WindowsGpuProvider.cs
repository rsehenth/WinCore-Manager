using WinCoreManager.Localization;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WinCoreManager.Services.Hardware;

internal readonly partial record struct PciModel(uint Vendor, uint Device, uint Subsystem)
{
    public string Display => Subsystem == 0 ? $"{Vendor:X4}:{Device:X4}" : $"{Vendor:X4}:{Device:X4} · {Subsystem:X8}";
    public string? Manufacturer => Vendor switch { 0x10DE => "NVIDIA", 0x1002 => "AMD", 0x8086 => "Intel", 0x1414 => "Microsoft", _ => null };

    public static PciModel? Parse(string? pnpId)
    {
        if (pnpId is null) return null;
        var parts = PciPattern().Match(pnpId);
        return parts.Success ? new(ToHex(parts.Groups[1].Value), ToHex(parts.Groups[2].Value), ToHex(parts.Groups[3].Value)) : null;
    }

    private static uint ToHex(string value) => uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) ? number : 0;

    [GeneratedRegex(@"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})(?:&SUBSYS_([0-9A-F]{8}))?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PciPattern();
}

internal sealed record DxgiAdapter(string? Name, PciModel Pci, ulong DedicatedBytes, long Luid, uint Flags)
{
    public string InventoryId => $"gpu/dxgi/{Luid:X16}";
}
internal interface IGraphicsAdapters { IReadOnlyList<DxgiAdapter> Read(CancellationToken token); }
internal interface IGpuMemoryCapacity { IReadOnlyDictionary<string, ulong> Read(IReadOnlyList<DxgiAdapter> adapters, CancellationToken token); }

internal sealed unsafe class DxgiGraphicsAdapters : IGraphicsAdapters
{
    private static readonly Guid FactoryInterface = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int EndOfEnumeration = unchecked((int)0x887A0002);

    public IReadOnlyList<DxgiAdapter> Read(CancellationToken token)
    {
        var devices = new List<DxgiAdapter>();
        if (CreateDXGIFactory1(in FactoryInterface, out var factory) < 0 || factory == 0) return devices;
        try
        {
            var factoryVtable = *(nint**)factory;
            // IDXGIFactory1::EnumAdapters1 follows IDXGIFactory's five methods.
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)factoryVtable[12];
            for (uint index = 0; index < 128; index++)
            {
                token.ThrowIfCancellationRequested();
                nint adapter = 0;
                var result = enumerate(factory, index, &adapter);
                if (result == EndOfEnumeration || result < 0 || adapter == 0) break;
                try
                {
                    var adapterVtable = *(nint**)adapter;
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)adapterVtable[10];
                    AdapterDescription description = default;
                    if (getDescription(adapter, &description) < 0 || (description.Flags & 2) != 0) continue;
                    var text = new ReadOnlySpan<char>(description.Name, 128);
                    var end = text.IndexOf('\0');
                    devices.Add(new(InventoryText.Clean((end < 0 ? text : text[..end]).ToString()),
                        new(description.Vendor, description.Device, description.Subsystem), description.DedicatedBytes,
                        ((long)description.LuidHigh << 32) | description.LuidLow, description.Flags));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return devices;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDescription
    {
        public fixed char Name[128];
        public uint Vendor, Device, Subsystem, Revision;
        public nuint DedicatedBytes, DedicatedSystemBytes, SharedBytes;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid iid, out nint factory);
}

internal sealed class WindowsGpuProvider(IHardwareWmi wmi, IGraphicsAdapters graphics, IGpuMemoryCapacity capacity) : IGpuProvider
{
    internal const string VendorSource = "DXGI IDXGIAdapter1";
    private const string WmiSource = "WMI Win32_VideoController";

    public HardwareSection Read(CancellationToken token)
    {
        var time = DateTimeOffset.Now;
        var controllers = wmi.Read(HardwareWmi.Cim, "Win32_VideoController", "Name,AdapterCompatibility,PNPDeviceID,DriverVersion", token);
        var rows = controllers.Rows.Select(r => (Row: r, Pci: PciModel.Parse(r.Get("PNPDeviceID") as string))).ToArray();
        var adapters = graphics.Read(token);
        var vendorCapacity = capacity.Read(adapters, token);
        var devices = new List<HardwareDevice>();
        foreach (var adapter in adapters)
        {
            var candidates = rows.Where(r => r.Pci == adapter.Pci).ToArray();
            var match = adapter.Pci.Vendor != 0 && adapter.Pci.Device != 0 && candidates.Length == 1
                && adapters.Count(g => g.Pci == adapter.Pci) == 1 ? candidates[0].Row : null;
            var name = adapter.Name ?? match?.Text("Name");
            var device = new InventoryDevice(adapter.InventoryId, HardwareKind.Gpu, name ?? LocalizationService.Source("Message.GPUModelUnavailable"), time, name is null)
                .Add(LocalizationService.Source("Message.Manufacturer"), adapter.Pci.Manufacturer ?? match?.Text("AdapterCompatibility"),
                    adapter.Pci.Manufacturer is null ? WmiSource : VendorSource + LocalizationService.Source("Message.PCIManufacturer"))
                .Add(LocalizationService.Source("Message.Model"), name, adapter.Name is null ? WmiSource : VendorSource)
                .Add(LocalizationService.Source("Message.DedicatedVideoMemory"), adapter.DedicatedBytes, VendorSource, "B")
                .Add(LocalizationService.Source("Message.AdapterType"), (adapter.Flags & 1) != 0 ? LocalizationService.Source("Message.Remote") : adapter.Pci.Vendor != 0 ? "Hardware" : LocalizationService.Source("Message.GraphicsAdapter"), VendorSource, localizeValue: true)
                .Add("Driver", match?.Text("DriverVersion"), WmiSource, absent: controllers.State == ReadingState.Available ? ReadingState.NoReading : controllers.State)
                .Add(LocalizationService.Source("Message.PciDevice"), adapter.Pci.Vendor == 0 ? null : adapter.Pci.Display, VendorSource);
            if (vendorCapacity.TryGetValue(adapter.InventoryId, out var bytes))
                device.Add(LocalizationService.Source("Message.TotalMemoryReportedByNVIDIA"), bytes, "NVIDIA NVML · nvmlDeviceGetMemoryInfo", "B");
            devices.Add(device.Build());
        }
        if (adapters.Count == 0)
        {
            foreach (var (row, pci) in rows)
            {
                var name = row.Text("Name");
                devices.Add(new InventoryDevice($"gpu/{devices.Count}", HardwareKind.Gpu, name ?? LocalizationService.Source("Message.GPUModelUnavailable"), time, name is null)
                    .Add(LocalizationService.Source("Message.Model"), name, WmiSource)
                    .Add(LocalizationService.Source("Message.Manufacturer"), pci?.Manufacturer ?? row.Text("AdapterCompatibility"), WmiSource)
                    .Add("Driver", row.Text("DriverVersion"), WmiSource)
                    .Add(LocalizationService.Source("Message.AdapterType"), pci is null ? null : "Hardware PCI", WmiSource)
                    .Add(LocalizationService.Source("Message.PciDevice"), pci?.Display, WmiSource)
                    .Add(LocalizationService.Source("Message.DedicatedVideoMemory"), null, VendorSource, absent: ReadingState.NoReading).Build());
            }
        }
        var summary = string.Join(" • ", devices.Take(3).Select(device =>
        {
            var memory = vendorCapacity.TryGetValue(device.Id, out var bytesFromVendor) ? bytesFromVendor :
                device.Fields.First(field => field.Label == LocalizationService.Source("Message.DedicatedVideoMemory")).Value;
            return memory is ulong bytes && bytes > 0 ? $"{device.Name} — {HardwareReading.FormatBytes(bytes)}" : device.Name;
        }));
        return new(LocalizationService.Source("Message.GraphicsCard"), summary + (devices.Count > 3 ? $" • +{devices.Count - 3}" : ""), devices,
            devices.Count > 0 ? ReadingState.Available : controllers.State);
    }
}
