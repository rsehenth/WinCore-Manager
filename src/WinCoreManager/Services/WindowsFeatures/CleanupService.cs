using WinCoreManager.Localization;
using System.Globalization;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.WindowsFeatures;

public sealed record WindowsVolume(string Root, string Identity, long Capacity, long Free, long? Allocated = null)
{
    public long Used => Allocated ?? Capacity - Free;
    public static bool IsDriveRoot(string? root) => root is { Length: 3 }
        && char.IsAsciiLetter(root[0]) && root[1] == ':' && root[2] == '\\';
}

public sealed record CleanupCategory(string Id, string Name, string Description, long? Bytes,
    string Attention, string Elevation, bool Selectable, string Availability, long? Items = null);

public sealed record CleanupSnapshot(WindowsVolume? Volume, IReadOnlyList<CleanupCategory> Categories,
    bool NativeAvailable, string StatusKey)
{
    public string Status => StatusKey.Length == 0 ? "" : LocalizationService.Source(StatusKey);
    public int KnownCount => Categories.Count(category => category.Bytes.HasValue);
    public long KnownBytes => Categories.Sum(category => category.Bytes.GetValueOrDefault());
}

public sealed record RecycleEstimate(long Bytes, long Items);

public interface ICleanupPlatform
{
    WindowsVolume ReadWindowsVolume();
    IReadOnlyList<CleanupCategory> ReadCatalog();
    RecycleEstimate? QueryRecycleBin(string root);
    WindowsActionResult EmptyRecycleBin(string root, nint owner);
}

public sealed class CleanupService(ICleanupPlatform platform, IWindowsTools tools, IHistoryService history)
{
    internal const string RecycleBin = "Recycle Bin";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<CleanupSnapshot> ReadAsync() => Task.Run(Read);

    private CleanupSnapshot Read()
    {
        WindowsVolume volume;
        try { volume = platform.ReadWindowsVolume(); }
        catch (Exception ex) when (StartupErrors.Expected(ex) || ex is EntryPointNotFoundException or OverflowException)
        { return new(null, [], false, "Message.TheWindowsVolumeCouldNotBeIdentified"); }

        IReadOnlyList<CleanupCategory> catalog;
        var status = "";
        try { catalog = platform.ReadCatalog(); }
        catch (Exception ex) when (StartupErrors.Expected(ex))
        {
            catalog = [];
            status = "Message.TheDiskCleanupCatalogIsUnavailable";
        }

        var rows = catalog.Select(category =>
        {
            if (!category.Id.Equals(RecycleBin, StringComparison.OrdinalIgnoreCase)) return category;
            var estimate = QueryRecycleBin(volume.Root);
            return category with
            {
                Bytes = estimate?.Bytes,
                Items = estimate?.Items,
                Selectable = estimate is { Items: > 0 },
                Availability = estimate switch
                {
                    { Items: > 0 } => LocalizationService.Source("Message.AvailableOnThisVolume"),
                    { Items: 0 } => LocalizationService.Source("Message.EmptyOnThisVolume"),
                    _ => LocalizationService.Source("Message.CheckAvailabilityInWindows"),
                },
            };
        }).ToArray();
        if (status.Length == 0 && rows.Length == 0) status = "Message.NoRegisteredCategoriesWereFound";
        return new(volume, rows, tools.IsAvailable(WindowsTool.DiskCleanup), status);
    }

    public WindowsActionResult OpenNative(WindowsVolume? expected)
    {
        if (expected is null || !WindowsVolume.IsDriveRoot(expected.Root)) return InvalidVolume();
        try
        {
            var live = platform.ReadWindowsVolume();
            return SameVolume(expected, live) ? tools.Open(WindowsTool.DiskCleanup, live.Root) : InvalidVolume();
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return InvalidVolume(); }
    }

    public async Task<WindowsActionResult> EmptyAsync(CleanupSnapshot snapshot, CleanupCategory selection,
        bool confirmed, nint owner)
    {
        if (!confirmed) return new(false, LocalizationService.Source("Message.CleanupCanceled"));
        if (snapshot.Volume is not { } expected || !WindowsVolume.IsDriveRoot(expected.Root)
            || !snapshot.Categories.Contains(selection) || !selection.Selectable
            || !selection.Id.Equals(RecycleBin, StringComparison.OrdinalIgnoreCase))
            return new(false, LocalizationService.Source("Message.TheSelectedCategoryIsAvailableInWindowsDisk"));

        await _gate.WaitAsync();
        try
        {
            var result = await StartupInventory.RunOnSta(() =>
            {
                try
                {
                    var live = platform.ReadWindowsVolume();
                    if (!SameVolume(expected, live)) return InvalidVolume();
                    var before = QueryRecycleBin(live.Root);
                    if (before is not { Items: > 0 } || before.Bytes != selection.Bytes || before.Items != selection.Items)
                        return new(false, LocalizationService.Source("Message.TheRecycleBinChangedRefreshTheListAnd"));
                    // The Shell also confirms the current contents using its own dialog.
                    var action = platform.EmptyRecycleBin(live.Root, owner);
                    if (!action.Success) return action;
                    return QueryRecycleBin(live.Root) is { Items: 0 }
                        ? new(true, LocalizationService.Source("Message.TheRecycleBinOn0WasEmptiedAnd", live.Root))
                        : new(false, LocalizationService.Source("Message.WindowsCompletedTheOperationButTheRecycleBin"));
                }
                catch (Exception ex) when (StartupErrors.Expected(ex))
                { return new WindowsActionResult(false, LocalizationService.Source("Message.CouldNotFinishEmptyingTheRecycleBin")); }
            }, CancellationToken.None);
            try
            {
                await history.AddAsync(new HistoryRecord
                {
                    Type = HistoryType.Cleanup, Target = LocalizationService.Source("Message.RecycleBin"), Details = expected.Root,
                    Status = result.Success ? HistoryStatus.Success : HistoryStatus.Failure, Message = result.Message,
                });
            }
            catch (Exception ex) when (StartupErrors.Expected(ex) || ex is Microsoft.Data.Sqlite.SqliteException)
            { return result with { Message = result.Message + LocalizationService.Source("Message.HistoryUnavailable") }; }
            return result;
        }
        finally { _gate.Release(); }
    }

    private RecycleEstimate? QueryRecycleBin(string root)
    {
        try
        {
            if (!WindowsVolume.IsDriveRoot(root)) return null;
            return platform.QueryRecycleBin(root) is { Items: >= 0, Bytes: >= 0 } estimate ? estimate : null;
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { return null; }
    }

    private static bool SameVolume(WindowsVolume first, WindowsVolume second) =>
        WindowsVolume.IsDriveRoot(second.Root) && !string.IsNullOrWhiteSpace(first.Identity)
        && first.Root.Equals(second.Root, StringComparison.OrdinalIgnoreCase)
        && first.Identity.Equals(second.Identity, StringComparison.OrdinalIgnoreCase);

    private static WindowsActionResult InvalidVolume() => new(false, LocalizationService.Source("Message.TheWindowsVolumeChangedOrIsUnavailableRefresh"));

    public static string FormatBytes(long? bytes)
    {
        if (bytes is null) return LocalizationService.Source("Message.SizeUnavailable");
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        decimal value = bytes.Value;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }
        return $"{value.ToString("0.##", LocalizationService.Current.Culture)} {units[index]}";
    }
}

