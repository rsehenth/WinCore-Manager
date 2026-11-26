using WinCoreManager.Localization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WinCoreManager.Services.Hardware;

internal sealed class WindowsComplementaryProvider(IHardwareWmi wmi, IDesktopDisplays displays) : IComplementaryProvider
{
    public IEnumerable<HardwareSection> Read(CancellationToken token)
    {
        var time = DateTimeOffset.Now;
        yield return ReadNetwork(time, token);
        yield return ReadDisplays(time, token);
        yield return Simple(HardwareKind.Audio, LocalizationService.Source("Message.Audio"), "Win32_SoundDevice", "Name,Manufacturer,Status", time, token,
            (LocalizationService.Source("Message.Manufacturer"), "Manufacturer"), (LocalizationService.Source("Message.ReportedStatus"), "Status"));
        yield return Simple(HardwareKind.Battery, LocalizationService.Source("Message.Battery"), "Win32_Battery", "Name,EstimatedChargeRemaining", time, token);
    }

    private HardwareSection ReadNetwork(DateTimeOffset time, CancellationToken token)
    {
        const string source = "WMI Win32_NetworkAdapter";
        var reports = wmi.Read(HardwareWmi.Cim, "Win32_NetworkAdapter", "Name,Manufacturer,NetEnabled,GUID,AdapterType", token, "PhysicalAdapter = TRUE");
        Dictionary<Guid, NetworkInterfaceType> types;
        try
        {
            types = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => Guid.TryParse(n.Id, out _)).GroupBy(n => Guid.Parse(n.Id))
                .Where(group => group.Count() == 1).ToDictionary(group => group.Key, group => group.Single().NetworkInterfaceType);
        }
        catch (NetworkInformationException) { types = []; }

        var devices = reports.Rows.Select((row, index) =>
        {
            var name = row.Text("Name");
            var nativeType = Guid.TryParse(row.Get("GUID") as string, out var id) && types.TryGetValue(id, out var type) ? type : default;
            var link = nativeType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Ethernet",
                _ => row.Text("AdapterType")
            };
            return new InventoryDevice($"network/{index}", HardwareKind.Network, name ?? LocalizationService.Source("Message.NetworkAdapter"), time, name is null)
                .Add(LocalizationService.Source("Message.Manufacturer"), row.Text("Manufacturer"), source)
                .Add(LocalizationService.Source("Message.Type"), link, nativeType != default ? LocalizationService.Source("Message.WindowsInterfaceType") : source)
                .Add(LocalizationService.Source("Message.InterfaceEnabled"), row.Flag("NetEnabled") is { } value ? value ? LocalizationService.Source("Message.Yes") : LocalizationService.Source("Message.No") : null, source, localizeValue: true).Build();
        }).ToArray();
        return InventoryText.Section(LocalizationService.Source("Message.Network"), devices, reports.State);
    }

    private HardwareSection ReadDisplays(DateTimeOffset time, CancellationToken token)
    {
        const string monitorSource = "WMI WmiMonitorID · EDID";
        var displaySource = LocalizationService.Source("Message.WindowsEnumDisplaySettingsWDesktopMode");
        var info = wmi.Read(HardwareWmi.Monitor, "WmiMonitorID", "InstanceName,ManufacturerName,UserFriendlyName", token, "Active = TRUE");
        var screens = displays.Read(token);
        var devices = screens.Select((screen, index) =>
        {
            var matches = info.Rows.Where(row => MatchesDisplay(screen.Instance, row.Get("InstanceName") as string)).ToArray();
            var edid = matches is [var match] ? match : null;
            var name = DecodeEdid(edid?.Get("UserFriendlyName")) ?? InventoryText.Clean(screen.Name);
            return new InventoryDevice($"display/{index}", HardwareKind.Display, name ?? $"Monitor {index + 1}", time)
                .Add(LocalizationService.Source("Message.Name"), name, DecodeEdid(edid?.Get("UserFriendlyName")) is null ? "Windows EnumDisplayDevicesW" : monitorSource)
                .Add(LocalizationService.Source("Message.ManufacturerCode"), DecodeEdid(edid?.Get("ManufacturerName")), monitorSource)
                .Add(LocalizationService.Source("Message.DesktopResolution"), screen.Width > 0 && screen.Height > 0 ? $"{screen.Width} × {screen.Height}" : null, displaySource, "px")
                .Add(LocalizationService.Source("Message.RefreshRate"), screen.Hertz > 1 ? screen.Hertz : null, displaySource, "Hz").Build();
        }).ToList();
        if (devices.Count == 0)
        {
            devices.AddRange(info.Rows.Select((edid, index) => new InventoryDevice($"display/{index}", HardwareKind.Display,
                    DecodeEdid(edid.Get("UserFriendlyName")) ?? $"Monitor {index + 1}", time)
                .Add(LocalizationService.Source("Message.ManufacturerCode"), DecodeEdid(edid.Get("ManufacturerName")), monitorSource)
                .Add(LocalizationService.Source("Message.DesktopResolution"), null, displaySource)
                .Add(LocalizationService.Source("Message.RefreshRate"), null, displaySource, "Hz").Build()));
        }
        return InventoryText.Section(LocalizationService.Source("Message.Displays"), devices, info.State);
    }

    private static string? DecodeEdid(object? value) => value is ushort[] chars
        ? InventoryText.Clean(new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray())) : null;

    internal static bool MatchesDisplay(string? path, string? edid)
    {
        if (path is null || edid is null || !path.StartsWith(@"\\?\DISPLAY#", StringComparison.OrdinalIgnoreCase)) return false;
        var parts = path[4..].Split('#');
        var suffix = edid.LastIndexOf('_');
        return parts.Length == 4 && suffix > 0 && int.TryParse(edid[(suffix + 1)..], out _)
            && string.Equals(string.Join('\\', parts.Take(3)), edid[..suffix], StringComparison.OrdinalIgnoreCase);
    }

    private HardwareSection Simple(HardwareKind kind, string title, string className, string columns, DateTimeOffset time,
        CancellationToken token, params (string Label, string Column)[] fields)
    {
        var source = "WMI " + className;
        var result = wmi.Read(HardwareWmi.Cim, className, columns, token);
        var devices = result.Rows.Select((r, index) =>
        {
            var name = r.Text("Name");
            var device = new InventoryDevice($"{kind}/{index}", kind, name ?? title, time, name is null);
            foreach (var (label, column) in fields) device.Add(label, r.Text(column), source);
            if (kind == HardwareKind.Battery)
                device.Add(LocalizationService.Source("Message.EstimatedRemainingCharge"), r.UInt("EstimatedChargeRemaining") is <= 100 and { } charge ? (double)charge : null, source, "%");
            return device.Build();
        }).ToArray();
        return InventoryText.Section(title, devices, result.State);
    }
}

internal sealed record DesktopDisplay(string? Name, string? Instance, uint Width, uint Height, uint Hertz);
internal interface IDesktopDisplays { IReadOnlyList<DesktopDisplay> Read(CancellationToken token); }

internal sealed class WindowsDesktopDisplays : IDesktopDisplays
{
    public IReadOnlyList<DesktopDisplay> Read(CancellationToken token)
    {
        var result = new List<DesktopDisplay>();
        for (uint index = 0; index < 64; index++)
        {
            token.ThrowIfCancellationRequested();
            var adapter = DisplayDevice.Create();
            if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;
            if ((adapter.Flags & 1) == 0 || (adapter.Flags & 8) != 0) continue;
            var mode = new DisplayMode { Size = 220 };
            if (!EnumDisplaySettings(adapter.Name, -1, ref mode)) mode = default;
            for (uint number = 0; number < 32; number++)
            {
                var monitor = DisplayDevice.Create();
                if (!EnumDisplayDevices(adapter.Name, number, ref monitor, 1)) break;
                if ((monitor.Flags & 1) == 0) continue;
                result.Add(new(monitor.Description, monitor.Id, mode.Width, mode.Height, mode.Hertz));
            }
        }
        return result;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
        public static DisplayDevice Create() => new() { Size = (uint)Marshal.SizeOf<DisplayDevice>() };
    }

    // DEVMODEW fixed ABI. Only the fields returned for the current desktop mode are read.
    [StructLayout(LayoutKind.Explicit, Size = 220)]
    private struct DisplayMode
    {
        [FieldOffset(68)] public ushort Size;
        [FieldOffset(172)] public uint Width;
        [FieldOffset(176)] public uint Height;
        [FieldOffset(184)] public uint Hertz;
    }

    [DllImport("user32.dll", EntryPoint = "EnumDisplayDevicesW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint number, ref DisplayDevice display, uint flags);

    [DllImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
}
