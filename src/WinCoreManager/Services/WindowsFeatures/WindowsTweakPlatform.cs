using WinCoreManager.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WinCoreManager.Services.WindowsFeatures;

internal sealed class WindowsTweakPlatform : ITweakPlatform
{
    public Guid ReadActivePlan() => WindowsPowerSettings.ReadActivePlan();

    public bool HasBattery() => WindowsPowerSettings.HasBattery();

    public TweakReading Read(TweakAddress address)
    {
        if (!address.Valid) throw new ArgumentException(nameof(address));
        if (address.Setting == TweakSetting.ActivePlan || address.IsTimeout) return WindowsPowerSettings.Read(address);

        var value = IsShell(address.Setting) ? ReadShell(address.Setting) : ReadEffect(address.Setting);
        var restricted = IsShell(address.Setting) && ShellManaged(address.Setting);
        return new(address, TweakService.Label(address.Setting), new(value ? 1u : 0u), TweakService.ToggleChoices,
            restricted ? LocalizationService.Source("Message.ManagedByFileExplorerPolicy") : null);
    }

    public void Write(TweakAddress address, TweakValue before, TweakValue value)
    {
        if (!address.Valid || !value.ValidFor(address)) throw new ArgumentException(nameof(value));
        if (address.Setting == TweakSetting.ActivePlan || address.IsTimeout)
        {
            WindowsPowerSettings.Write(address, before, value);
            return;
        }

        var live = Read(address);
        if (live.Restriction is not null) throw new Win32Exception(1260);
        if (live.Current != before) throw new TweakConflictException();
        var enabled = value.Number == 1;
        if (IsShell(address.Setting)) WriteShell(address.Setting, enabled);
        else WriteEffect(address.Setting, enabled);
    }

    private static bool IsShell(TweakSetting setting) => setting >= TweakSetting.Thumbnails;

    private static readonly Dictionary<TweakSetting, (uint Get, uint Set, bool UseUiParam)> Effects = new()
    {
        [TweakSetting.ControlAnimation] = (0x1042, 0x1043, false),
        [TweakSetting.MenuAnimation] = (0x1002, 0x1003, false),
        [TweakSetting.PointerShadow] = (0x101A, 0x101B, false),
        [TweakSetting.FontSmoothing] = (0x004A, 0x004B, true),
        [TweakSetting.FullWindowDrag] = (0x0026, 0x0025, true),
    };

    private static bool ReadEffect(TweakSetting setting)
    {
        if (setting == TweakSetting.WindowAnimation)
        {
            var data = new AnimationInfo { Size = 8 };
            Check(SystemParametersAnimation(0x0048, 8, ref data, 0));
            return data.MinAnimate != 0;
        }
        Check(SystemParametersRead(Effects[setting].Get, 0, out var enabled, 0));
        return enabled != 0;
    }

    private static void WriteEffect(TweakSetting setting, bool enabled)
    {
        const uint persistAndBroadcast = 3;
        var value = enabled ? 1u : 0u;
        if (setting == TweakSetting.WindowAnimation)
        {
            var data = new AnimationInfo { Size = 8, MinAnimate = (int)value };
            Check(SystemParametersAnimation(0x0049, 8, ref data, persistAndBroadcast));
            return;
        }
        var effect = Effects[setting];
        Check(SystemParametersWrite(effect.Set, effect.UseUiParam ? value : 0,
            effect.UseUiParam ? 0 : (nint)value, persistAndBroadcast));
    }

    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

    private static (string Name, uint Mask, uint Bit, bool Tail, uint On, uint Off) ShellSetting(TweakSetting setting) => setting switch
    {
        TweakSetting.FileExtensions => ("HideFileExt", 0x00000002, 1u << 1, false, 0, 1),
        TweakSetting.HiddenFiles => ("Hidden", 0x00000001, 1u, false, 1, 2),
        TweakSetting.Thumbnails => ("IconsOnly", 0x01000000, 1u << 4, true, 0, 1),
        _ => throw new ArgumentException(nameof(setting)),
    };

    private static bool ReadShell(TweakSetting setting)
    {
        var definition = ShellSetting(setting);
        using var advanced = Registry.CurrentUser.OpenSubKey(ExplorerAdvanced);
        var value = advanced?.GetValue(definition.Name);
        if (value is int dword)
        {
            if ((uint)dword == definition.On) return true;
            if ((uint)dword == definition.Off) return false;
            throw new Win32Exception(13);
        }
        if (value is not null) throw new Win32Exception(13);

        var state = new ShellState();
        SHGetSetSettings(ref state, definition.Mask, false);
        var bit = ((definition.Tail ? state.Tail : state.Flags) & definition.Bit) != 0;
        return setting == TweakSetting.Thumbnails ? !bit : bit;
    }

    private static void WriteShell(TweakSetting setting, bool enabled)
    {
        var definition = ShellSetting(setting);
        var state = new ShellState();
        var bit = (setting == TweakSetting.Thumbnails ? !enabled : enabled) ? definition.Bit : 0;
        if (definition.Tail) state.Tail = bit;
        else state.Flags = bit;
        SHGetSetSettings(ref state, definition.Mask, true);
        using var advanced = Registry.CurrentUser.OpenSubKey(ExplorerAdvanced);
        var actual = advanced?.GetValue(definition.Name);
        if (actual is not int value || (uint)value != (enabled ? definition.On : definition.Off))
            throw new UnauthorizedAccessException(LocalizationService.Source("Message.FileExplorerDidNotSaveThePreference"));
        // Let existing Explorer windows refresh the preference while leaving their processes running.
        _ = SendMessageTimeoutW(0xFFFF, 0x001A, 0, ExplorerAdvanced, 0x0002, 250, out _);
    }

    private static bool ShellManaged(TweakSetting setting)
    {
        string[] paths = [@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", @"Software\Policies\Microsoft\Windows\Explorer"];
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        foreach (var path in paths)
        {
            using var key = root.OpenSubKey(path);
            if (key?.GetValue(ShellSetting(setting).Name) is not null
                || (setting != TweakSetting.Thumbnails && key?.GetValue("NoFolderOptions") is 1)
                || (setting == TweakSetting.Thumbnails && key?.GetValue("DisableThumbnails") is 1))
                return true;
        }
        return false;
    }

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AnimationInfo { public uint Size; public int MinAnimate; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShellState { public uint Flags, Win95Long, Win95Int, SortParam, SortDirection, Version, NotUsed, Tail; }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersAnimation(uint action, uint uiParam, ref AnimationInfo data, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersRead(uint action, uint uiParam, out int value, uint flags);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersWrite(uint action, uint uiParam, nint value, uint flags);

    [DllImport("shell32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void SHGetSetSettings(ref ShellState state, uint mask, [MarshalAs(UnmanagedType.Bool)] bool set);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint SendMessageTimeoutW(nint destination, uint message, nuint wParam,
        string area, uint flags, uint timeout, out nuint result);
}

