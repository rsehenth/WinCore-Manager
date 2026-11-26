using WinCoreManager.Localization;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WinCoreManager.Services.WindowsFeatures;

internal static class WindowsPowerSettings
{
    private static readonly Guid Video = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");
    private static readonly Guid Sleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SleepTimeout = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private const uint NoMoreItems = 259;
    private const uint Policy = 1260;

    public static Guid ReadActivePlan()
    {
        Check(PowerGetActiveScheme(0, out var pointer));
        try { return Marshal.PtrToStructure<Guid>(pointer); }
        finally { _ = LocalFree(pointer); }
    }

    public static bool HasBattery() => GetSystemPowerStatus(out var status) && status.BatteryFlag != 255
        && (status.BatteryFlag & 128) == 0;

    public static TweakReading Read(TweakAddress address)
    {
        var label = TweakService.Label(address.Setting);
        if (address.Setting == TweakSetting.ActivePlan)
        {
            var active = ReadActivePlan();
            return new(address, label, TweakValue.ForPlan(active), Plans(),
                Restriction(PowerSettingAccessCheck(19, 0)));
        }

        var plan = address.Plan!.Value;
        var (group, setting, monitor) = Parameters(address);
        var result = address.IsBattery
            ? PowerReadDCValueIndex(0, in plan, in group, in setting, out var value)
            : PowerReadACValueIndex(0, in plan, in group, in setting, out value);
        Check(result);
        var access = Restriction(PowerSettingAccessCheckForGuid(address.IsBattery ? 1u : 0u, in setting));
        if (address.IsBattery && !HasBattery()) access = LocalizationService.Source("Message.ThisComputerHasNoAvailableBattery");

        var minResult = PowerReadValueMin(0, in group, in setting, out var min);
        var maxResult = PowerReadValueMax(0, in group, in setting, out var max);
        var stepResult = PowerReadValueIncrement(0, in group, in setting, out var step);
        if (minResult != 0 || maxResult != 0 || stepResult != 0 || min > max || step == 0)
            return new(address, label, new(value), [], LocalizationService.Source("Message.WindowsDidNotConfirmTheLimitsForThis"));

        uint[] standard = monitor ? [0, 60, 300, 600, 900, 1800, 3600] : [0, 300, 600, 900, 1800, 3600, 7200];
        var options = standard.Where(seconds => InRange(seconds, min, max, step)).Append(value).Distinct().Order()
            .Select(seconds => new TweakChoice(new(seconds), FormatTime(seconds))).ToArray();
        return new(address, label, new(value), options, access);
    }

    public static void Write(TweakAddress address, TweakValue before, TweakValue after)
    {
        if (address.Setting == TweakSetting.ActivePlan)
        {
            var plan = after.Plan!.Value;
            Check(PowerSettingAccessCheck(19, 0));
            Check(PowerSettingAccessCheckForGuid(16, in plan));
            if (!Plans().Any(option => option.Value == after) || ReadActivePlan() != before.Plan)
                throw new TweakConflictException();
            Check(PowerSetActiveScheme(0, in plan));
            return;
        }

        var scheme = address.Plan!.Value;
        var (group, setting, _) = Parameters(address);
        Check(PowerSettingAccessCheckForGuid(address.IsBattery ? 1u : 0u, in setting));
        Check(PowerReadValueMin(0, in group, in setting, out var min));
        Check(PowerReadValueMax(0, in group, in setting, out var max));
        Check(PowerReadValueIncrement(0, in group, in setting, out var step));
        var seconds = after.Number!.Value;
        if (!InRange(seconds, min, max, step) || Read(address).Current != before || ReadActivePlan() != scheme)
            throw new TweakConflictException();
        Check(address.IsBattery ? PowerWriteDCValueIndex(0, in scheme, in group, in setting, seconds)
            : PowerWriteACValueIndex(0, in scheme, in group, in setting, seconds));
        // Reapply only the plan still active. An external switch takes precedence.
        if (ReadActivePlan() != scheme) throw new TweakConflictException();
        Check(PowerSetActiveScheme(0, in scheme));
    }

    private static bool InRange(uint seconds, uint min, uint max, uint step) =>
        step != 0 && seconds >= min && seconds <= max && (seconds - min) % step == 0;

    private static (Guid Group, Guid Setting, bool Monitor) Parameters(TweakAddress address) =>
        address.Setting is TweakSetting.MonitorAc or TweakSetting.MonitorDc ? (Video, VideoTimeout, true) : (Sleep, SleepTimeout, false);

    private static string? Restriction(uint result) => result switch
    {
        0 => null,
        Policy => LocalizationService.Source("Message.ManagedByWindowsPolicy"),
        _ => LocalizationService.Source("Message.PermissionToChangeThisSettingIsUnavailable"),
    };

    private static IReadOnlyList<TweakChoice> Plans()
    {
        List<TweakChoice> plans = [];
        var buffer = new byte[16];
        for (uint index = 0; ; index++)
        {
            uint size = (uint)buffer.Length;
            var result = PowerEnumerate(0, 0, 0, 16, index, buffer, ref size);
            if (result == NoMoreItems) return plans;
            Check(result);
            if (size != 16 || index >= 1024) throw new Win32Exception(13);
            var plan = new Guid(buffer);
            plans.Add(new(TweakValue.ForPlan(plan), FriendlyName(plan)));
        }
    }

    private static string FriendlyName(Guid plan)
    {
        uint size = 0;
        var result = PowerReadFriendlyName(0, in plan, 0, 0, null, ref size);
        if (result is not (0 or 234) || size is < 2 or > 4096) return plan.ToString();
        var buffer = new byte[size];
        return PowerReadFriendlyName(0, in plan, 0, 0, buffer, ref size) == 0
            ? Encoding.Unicode.GetString(buffer).TrimEnd('\0') : plan.ToString();
    }

    private static string FormatTime(uint seconds)
    {
        if (seconds == 0) return LocalizationService.Source("Message.Never");
        var time = TimeSpan.FromSeconds(seconds);
        if (seconds % 3600 == 0) return seconds == 3600 ? LocalizationService.Source("Message.1Hour") : LocalizationService.Source("Message.0Hours", seconds / 3600);
        if (seconds < 3600 && seconds % 60 == 0) return seconds == 60 ? LocalizationService.Source("Message.1Minute") : LocalizationService.Source("Message.0Minutes", seconds / 60);
        return seconds < 60 ? $"{seconds} s" : time.ToString("c");
    }

    private static void Check(uint error)
    {
        if (error != 0) throw new Win32Exception((int)error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
    [DllImport("kernel32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint pointer);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerGetActiveScheme(nint root, out nint guid);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerEnumerate(nint root, nint scheme, nint group, uint accessor, uint index,
        [Out] byte[] buffer, ref uint size);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadFriendlyName(nint root, in Guid plan, nint group, nint setting,
        [Out] byte[]? buffer, ref uint size);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadACValueIndex(nint root, in Guid plan, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadDCValueIndex(nint root, in Guid plan, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerWriteACValueIndex(nint root, in Guid plan, in Guid group, in Guid setting, uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerWriteDCValueIndex(nint root, in Guid plan, in Guid group, in Guid setting, uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSetActiveScheme(nint root, in Guid plan);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadValueMin(nint root, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadValueMax(nint root, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerReadValueIncrement(nint root, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingAccessCheck(uint accessor, nint guid);
    [DllImport("powrprof.dll", EntryPoint = "PowerSettingAccessCheck"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint PowerSettingAccessCheckForGuid(uint accessor, in Guid guid);
}

