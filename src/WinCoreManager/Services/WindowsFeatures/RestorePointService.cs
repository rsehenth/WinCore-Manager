using System.ComponentModel;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.WindowsFeatures;

public enum RestoreQueryState { Found, Empty, Disabled, AccessDenied, Unavailable, InvalidDate }

public sealed record RestorePointInfo(RestoreQueryState State, DateTimeOffset? Created = null,
    string? Description = null, bool HasInvalidDates = false);

public sealed record RawRestorePoint(string? Created, string? Description);

public interface IRestorePointReader
{
    bool DisabledByPolicy();
    IReadOnlyList<RawRestorePoint> Read();
}

public sealed class RestorePointService(IRestorePointReader reader)
{
    public Task<RestorePointInfo> ReadAsync() => Task.Run(() =>
    {
        try
        {
            if (reader.DisabledByPolicy()) return new RestorePointInfo(RestoreQueryState.Disabled);
            var rows = reader.Read();
            if (rows.Count == 0) return new RestorePointInfo(RestoreQueryState.Empty);
            var dates = rows.Select(row => (Created: ParseDate(row.Created), row.Description)).ToArray();
            var latest = dates.OrderByDescending(row => row.Created).First();
            return latest.Created is { } date ? new(RestoreQueryState.Found, date, latest.Description,
                    dates.Any(row => row.Created is null))
                : new(RestoreQueryState.InvalidDate);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or ManagementException { ErrorCode: ManagementStatus.AccessDenied }
            or COMException { HResult: unchecked((int)0x80070005) or unchecked((int)0x80041003) }
            or Win32Exception { NativeErrorCode: 5 })
        { return new(RestoreQueryState.AccessDenied); }
        catch (Exception ex) when (ex is ManagementException || StartupErrors.Expected(ex))
        { return new(RestoreQueryState.Unavailable); }
    });

    internal static DateTimeOffset? ParseDate(string? dmtf)
    {
        if (dmtf is not { Length: 25 } || dmtf[21] is not ('+' or '-')
            || !DateTime.TryParseExact(dmtf[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)
            || !int.TryParse(dmtf.AsSpan(22), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
            || offset > 840) return null;
        try { return new DateTimeOffset(date, TimeSpan.FromMinutes(dmtf[21] == '-' ? -offset : offset)); }
        catch (ArgumentException) { return null; }
    }
}

internal sealed class WindowsRestorePointReader : IRestorePointReader
{
    public bool DisabledByPolicy()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Microsoft\Windows NT\SystemRestore");
        return key?.GetValue("DisableSR") is 1;
    }

    public IReadOnlyList<RawRestorePoint> Read()
    {
        var scope = new ManagementScope(@"\\.\root\default");
        using var query = new ManagementObjectSearcher(scope,
            new ObjectQuery("SELECT CreationTime, Description FROM SystemRestore"),
            new EnumerationOptions { Timeout = TimeSpan.FromSeconds(8), ReturnImmediately = true, Rewindable = false });
        using var results = query.Get();
        List<RawRestorePoint> rows = [];
        foreach (ManagementBaseObject row in results)
        {
            using (row) rows.Add(new(row["CreationTime"] as string, row["Description"] as string));
        }
        return rows;
    }
}

