using WinCoreManager.Localization;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace WinCoreManager.Services.WindowsFeatures;

internal sealed class WindowsCleanupPlatform : ICleanupPlatform
{
    private const string CatalogKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";

    public WindowsVolume ReadWindowsVolume()
    {
        var windows = new StringBuilder(32768);
        var length = GetSystemWindowsDirectoryW(windows, (uint)windows.Capacity);
        if (length == 0 || length >= windows.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var root = new StringBuilder(32768);
        if (!GetVolumePathNameW(windows.ToString(), root, (uint)root.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var identity = new StringBuilder(64);
        var path = root.ToString();
        if (!GetVolumeNameForVolumeMountPointW(path, identity, (uint)identity.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        var volumePath = identity.ToString();
        var result = GetDiskSpaceInformationW(WindowsVolume.IsDriveRoot(path) ? path : volumePath, out var info);
        if (result != 0) throw new COMException(LocalizationService.Source("Message.WindowsDidNotReportVolumeUsage"), result);
        var units = checked((ulong)info.SectorsPerUnit * info.BytesPerSector);
        return new(path, volumePath, checked((long)(info.Total * units)), checked((long)(info.Available * units)),
            checked((long)(info.Used * units)));
    }

    public IReadOnlyList<CleanupCategory> ReadCatalog()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var catalog = machine.OpenSubKey(CatalogKey);
        if (catalog is null) return [];

        List<CleanupCategory> categories = [];
        foreach (var id in catalog.GetSubKeyNames())
        {
            using var registration = catalog.OpenSubKey(id);
            if (registration is null || registration.GetValue(null) is not string clsid || !Guid.TryParse(clsid, out _))
                continue;
            var known = Descriptions.GetValueOrDefault(id);
            var label = known.Name ?? ReadLabel(registration, "Display") ?? id;
            var description = known.Description ?? ReadLabel(registration, "Description") ?? LocalizationService.Source("Message.SeeTheDescriptionInWindowsDiskCleanup");
            var admin = known.Admin ? LocalizationService.Source("Message.WindowsToolRequestsAdministratorApproval") : LocalizationService.Source("Message.CheckPermissionsInWindows");
            if (id.Equals(CleanupService.RecycleBin, StringComparison.OrdinalIgnoreCase)) admin = LocalizationService.Source("Message.AdministratorApprovalNotRequired");
            categories.Add(new(id, label, description, null, known.Attention ?? "Revisar", admin, false, LocalizationService.Source("Message.RegisteredCheckInWindows")));
        }
        return categories.OrderByDescending(item => item.Id.Equals(CleanupService.RecycleBin, StringComparison.OrdinalIgnoreCase))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string? ReadLabel(RegistryKey key, string field) =>
        key.GetValue(field, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string { Length: > 0 and <= 600 } value
        && !value.StartsWith('@') && !value.Any(char.IsControl) ? value : null;

    public RecycleEstimate? QueryRecycleBin(string root)
    {
        if (!WindowsVolume.IsDriveRoot(root)) return null;
        var info = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
        return SHQueryRecycleBinW(root, ref info) == 0 && info.Bytes >= 0 && info.Items >= 0 ? new(info.Bytes, info.Items) : null;
    }

    public WindowsActionResult EmptyRecycleBin(string root, nint owner)
    {
        if (!WindowsVolume.IsDriveRoot(root) || new DriveInfo(root).DriveType != DriveType.Fixed)
            return new(false, LocalizationService.Source("Message.WindowsDidNotIdentifyAValidFixedDrive"));
        var result = SHEmptyRecycleBinW(owner, root, 0);
        return result switch
        {
            0 => new(true, LocalizationService.Source("Message.WindowsCompletedTheOperation")),
            unchecked((int)0x800704C7) or unchecked((int)0x80004004) => new(false, LocalizationService.Source("Message.CleanupCanceledInWindows")),
            unchecked((int)0x80070005) or unchecked((int)0x800702E4) => new(false, LocalizationService.Source("Message.YouDoNotHavePermissionToEmptyThe")),
            _ => new(false, LocalizationService.Source("Message.CouldNotEmptyTheRecycleBin0x0", result)),
        };
    }

    private static readonly Dictionary<string, (string Name, string Description, string Attention, bool Admin)> Descriptions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Recycle Bin"] = (LocalizationService.Source("Message.RecycleBin"), LocalizationService.Source("Message.EmptyingTheRecycleBinPermanentlyDeletesFilesYou"), "Alta", false),
            ["Internet Cache Files"] = (LocalizationService.Source("Message.TemporaryInternetFiles"), LocalizationService.Source("Message.TemporaryCacheOfInternetContent"), "Baixa", false),
            ["D3D Shader Cache"] = (LocalizationService.Source("Message.DirectXShaderCache"), LocalizationService.Source("Message.WindowsCanRebuildShadersAsNeeded"), "Baixa", false),
            ["Temporary Files"] = (LocalizationService.Source("Message.TemporaryFiles"), LocalizationService.Source("Message.TemporaryFilesWindowsConsidersEligibleForCleanup"), "Revisar", false),
            ["Thumbnail Cache"] = (LocalizationService.Source("Message.Thumbnails"), LocalizationService.Source("Message.FileExplorerRebuildsThumbnailsAsNeeded"), "Baixa", false),
            ["Delivery Optimization Files"] = (LocalizationService.Source("Message.DeliveryOptimizationFiles"), LocalizationService.Source("Message.CacheUsedToDistributeUpdates"), "Baixa", false),
            ["Update Cleanup"] = (LocalizationService.Source("Message.WindowsUpdateCleanup"), LocalizationService.Source("Message.RemovesSupersededVersionsOfComponentsReviewTheAvailable"), "Alta", true),
            ["Windows Upgrade Log Files"] = (LocalizationService.Source("Message.WindowsUpdateLogFiles"), LocalizationService.Source("Message.TheseLogsCanHelpDiagnoseUpdateProblems"), "Alta", false),
            ["Previous Installations"] = (LocalizationService.Source("Message.PreviousWindowsInstallations"), LocalizationService.Source("Message.RemovingTheseFilesCanPreventAReturnTo"), "Alta", true),
            ["Temporary Setup Files"] = (LocalizationService.Source("Message.TemporaryInstallationFiles"), LocalizationService.Source("Message.InstallationFilesFoundByWindowsDiskCleanup"), "Revisar", true),
            ["DownloadsFolder"] = (LocalizationService.Source("Message.Downloads"), LocalizationService.Source("Message.MayContainDocumentsAndOtherPersonalFiles"), "Alta", false),
            ["System error memory dump files"] = (LocalizationService.Source("Message.SystemErrorMemoryDumpFiles"), LocalizationService.Source("Message.TheseFilesMayBeNeededToDiagnoseSystem"), "Alta", true),
            ["System error minidump files"] = (LocalizationService.Source("Message.SystemErrorMinidumpFiles"), LocalizationService.Source("Message.TheseFilesMayBeNeededToDiagnoseSystem"), "Alta", true),
            ["Windows Error Reporting Files"] = (LocalizationService.Source("Message.WindowsErrorReports"), LocalizationService.Source("Message.TheseFilesCanHelpDiagnoseProblems"), "Alta", false),
            ["Windows ESD installation files"] = (LocalizationService.Source("Message.WindowsESDInstallationFiles"), LocalizationService.Source("Message.TheseFilesMayBeRequiredByWindowsRecovery"), "Alta", true),
            ["Windows Defender"] = ("Microsoft Defender", LocalizationService.Source("Message.FilesIdentifiedByWindowsDiskCleanupReviewThem"), "Alta", true),
            ["Device Driver Packages"] = (LocalizationService.Source("Message.DeviceDriverPackages"), LocalizationService.Source("Message.RemovingTheseFilesCanLimitRollbackToOlder"), "Alta", true),
            ["Setup Log Files"] = (LocalizationService.Source("Message.SetupLogFiles"), LocalizationService.Source("Message.TheseLogsMayBeNeededToInvestigateSetup"), "Alta", false),
            ["Windows Reset Log Files"] = (LocalizationService.Source("Message.ResetLogFiles"), LocalizationService.Source("Message.TheseLogsCanHelpInvestigateAWindowsReset"), "Alta", false),
            ["User file versions"] = (LocalizationService.Source("Message.UserFileVersions"), LocalizationService.Source("Message.RemovingTheseFilesMayDiscardVersionsYouNeed"), "Alta", false),
            ["Old ChkDsk Files"] = (LocalizationService.Source("Message.FilesRecoveredByCHKDSK"), LocalizationService.Source("Message.MayContainFragmentsOfRecoveredData"), "Alta", false),
            ["Upgrade Discarded Files"] = (LocalizationService.Source("Message.FilesDiscardedByTheUpdate"), LocalizationService.Source("Message.ReviewTheFilesBeforeConfirmingInWindows"), "Alta", true),
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct RecycleInfo { public uint Size; public long Bytes, Items; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DiskSpaceInfo
    {
        public ulong Total, Available, PoolUnavailable, CallerTotal, CallerAvailable, CallerPoolUnavailable,
            Used, TotalReserved, StorageReserve, AvailableCommitted, PoolAvailable;
        public uint SectorsPerUnit, BytesPerSector;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSystemWindowsDirectoryW(StringBuilder path, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string path, StringBuilder root, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDiskSpaceInformationW(string root, out DiskSpaceInfo space);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHQueryRecycleBinW(string root, ref RecycleInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHEmptyRecycleBinW(nint hwnd, string root, uint flags);
}

