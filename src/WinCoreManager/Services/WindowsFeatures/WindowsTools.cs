using WinCoreManager.Localization;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.WindowsFeatures;

public enum WindowsTool { DiskCleanup, Performance, SystemProtection, StartupApps, TaskScheduler, InstalledApps, RecycleBin }

public sealed record WindowsActionResult(bool Success, string Message);

public interface IWindowsTools
{
    bool IsAvailable(WindowsTool tool);
    WindowsActionResult Open(WindowsTool tool, string? driveRoot = null);
}

internal sealed class WindowsTools : IWindowsTools
{
    public bool IsAvailable(WindowsTool tool) => GetPath(tool) is not null;

    public WindowsActionResult Open(WindowsTool tool, string? driveRoot = null)
    {
        var path = GetPath(tool);
        if (path is null) return new(false, LocalizationService.Source("Message.TheWindowsToolIsUnavailable"));
        if (tool == WindowsTool.DiskCleanup ? !WindowsVolume.IsDriveRoot(driveRoot) : driveRoot is not null)
            return new(false, LocalizationService.Source("Message.CouldNotVerifyTheToolSDestination"));

        var info = new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Environment.SystemDirectory };
        if (tool == WindowsTool.DiskCleanup)
        {
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add(driveRoot![..1]);
        }
        try
        {
            using var process = Process.Start(info);
            if (tool is WindowsTool.StartupApps or WindowsTool.InstalledApps or WindowsTool.RecycleBin)
                return new(true, LocalizationService.Source("Message.WindowsToolOpened"));
            return process is null ? new(false, LocalizationService.Source("Message.WindowsDidNotConfirmThatTheToolOpened"))
                : new(true, LocalizationService.Source("Message.WindowsToolOpened"));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is 5 or 740 or 1223)
        { return new(false, LocalizationService.Source("Message.OpeningTheWindowsToolWasCanceledOrPermission")); }
        catch (Exception ex) when (StartupErrors.Expected(ex) || ex is InvalidOperationException)
        { return new(false, LocalizationService.Source("Message.CouldNotOpenTheWindowsTool")); }
    }

    private static string? GetPath(WindowsTool tool)
    {
        var shellTarget = tool switch
        {
            WindowsTool.StartupApps => "ms-settings:startupapps",
            WindowsTool.InstalledApps => "ms-settings:appsfeatures",
            WindowsTool.RecycleBin => "shell:RecycleBinFolder",
            _ => null,
        };
        if (shellTarget is not null) return shellTarget;
        var executable = tool switch
        {
            WindowsTool.DiskCleanup => "cleanmgr.exe",
            WindowsTool.Performance => "SystemPropertiesPerformance.exe",
            WindowsTool.SystemProtection => "SystemPropertiesProtection.exe",
            WindowsTool.TaskScheduler => "taskschd.msc",
            _ => null,
        };
        if (executable is null) return null;
        var path = Path.Combine(Environment.SystemDirectory, executable);
        return StartupPaths.IsSafeFile(path) ? path : null;
    }
}

