using WinCoreManager.Localization;
using System.ComponentModel;
using System.IO;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Startup;

public enum StartupChangeStatus { Applied, Unchanged, Conflict, Protected, Cancelled, AccessDenied, Failed, Unconfirmed }
public sealed record StartupChangeResult(StartupChangeStatus Status, string Message);

public interface IStartupChanges
{
    Task<StartupChangeResult> SetEnabledAsync(StartupEntry expected, bool enabled, CancellationToken token);
}

internal sealed class StartupChanges(StartupFolderSource folders, StartupBackupStore backups,
    IStartupFileMove files, IHistoryService history, StartupRunSource? run = null) : IStartupChanges
{
    public async Task<StartupChangeResult> SetEnabledAsync(StartupEntry expected, bool enabled, CancellationToken token)
    {
        StartupChangeResult result;
        try
        {
            result = await StartupInventory.RunOnSta(() => Apply(expected, enabled, token), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(StartupChangeStatus.Cancelled, LocalizationService.Source("Message.TheChangeWasCanceledBeforeWriting")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or Win32Exception { NativeErrorCode: 5 })
        { result = new(StartupChangeStatus.AccessDenied, LocalizationService.Source("Message.YouDoNotHavePermissionToChangeThis2")); }
        catch (Exception ex) when (ex is Win32Exception { NativeErrorCode: 2 or 3 or 32 or 33 or 80 or 183 or 6800 }
            || ex is IOException { HResult: unchecked((int)0x80070020) })
        { result = Conflict(); }
        catch (Exception ex) when (StartupErrors.Expected(ex))
        { result = new(StartupChangeStatus.Failed, LocalizationService.Source("Message.CouldNotFinishChangingTheEntry")); }

        if (result.Status is StartupChangeStatus.Applied or StartupChangeStatus.Unconfirmed
            or StartupChangeStatus.AccessDenied or StartupChangeStatus.Failed)
        {
            try
            {
                await history.AddAsync(new HistoryRecord
                {
                    Type = HistoryType.Startup,
                    Target = expected.Name,
                    Details = enabled ? LocalizationService.Source("Message.RestoreStartupEntry") : LocalizationService.Source("Message.DisableStartupEntry"),
                    Status = result.Status == StartupChangeStatus.Applied ? HistoryStatus.Success : HistoryStatus.Failure,
                    Message = result.Message,
                });
            }
            catch (Exception ex) when (StartupErrors.Expected(ex) || ex is Microsoft.Data.Sqlite.SqliteException)
            {
                return result with { Message = result.Message + LocalizationService.Source("Message.CouldNotSaveToHistory") };
            }
        }
        return result;
    }

    private StartupChangeResult Apply(StartupEntry expected, bool enabled, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (expected.Source == StartupSource.UserRun && run is not null)
            return run.SetEnabled(expected, enabled, token);
        if (expected.Source != StartupSource.UserFolder || !folders.IsPersonalShortcut(expected.Location)
            || !expected.CanChange || expected.Fingerprint is null || !MatchesId(expected))
            return new(StartupChangeStatus.Protected, LocalizationService.Source("Message.ThisEntryIsProtectedAgainstChanges"));
        if (enabled == (expected.Status == StartupStatus.Enabled))
            return new(StartupChangeStatus.Unchanged, LocalizationService.Source("Message.NoChangeNeeded"));

        using var operation = backups.Lock(folders.PersonalFolder);
        if (!StartupPaths.IsSafeFile(expected.FilePath)) return Conflict();
        using var targetDirectories = new WindowsStartupFileMove.DirectoryPins();
        targetDirectories.Pin(Path.GetDirectoryName(expected.FilePath!)!);
        using var executable = WindowsStartupFileMove.OpenVerifiedFileForRead(expected.FilePath!);
        var current = FindCurrent(expected.Id, token);
        if (current is null || current.Fingerprint != expected.Fingerprint || current.Status != expected.Status
            || current.Stored != expected.Stored || !SameTarget(expected, current)) return Conflict();
        if (!current.CanChange) return new(StartupChangeStatus.Protected, current.Protection!);

        var original = current.Location;
        var parts = current.Fingerprint!.Split('/', 2);
        var digest = parts[0];
        var approval = parts[1];
        StartupBackup? backup;
        if (enabled)
        {
            backup = backups.Read([], token).SingleOrDefault(item =>
                item.Snapshot.Original.Equals(original, StringComparison.OrdinalIgnoreCase)
                && item.Snapshot.Digest == digest && item.Snapshot.Approval == approval);
            if (backup is null || StartupFolderSource.Occupied(original)) return Conflict();
        }
        else
        {
            token.ThrowIfCancellationRequested();
            backup = backups.Prepare(original, digest, approval);
        }

        var moved = false;
        try
        {
            moved = files.MoveVerified(enabled ? backup.FilePath : original, enabled ? original : backup.FilePath,
                digest, () =>
                {
                    var latest = folders.ApprovalFor(original);
                    return latest.Status == StartupStatus.Enabled && latest.Token == approval;
                }, token);
        }
        finally
        {
            if (!enabled && !moved)
            {
                RemoveEmptyBackup(backup);
            }
        }
        if (!moved) return Conflict();

        // A rename is the commit point. Always verify the result, even if cancellation arrives after it.
        try
        {
            var confirmed = FindCurrent(expected.Id, CancellationToken.None);
            if (confirmed?.Status == (enabled ? StartupStatus.Enabled : StartupStatus.Disabled)
                && confirmed.Stored != enabled && confirmed.Fingerprint == expected.Fingerprint)
            {
                if (enabled) RemoveEmptyBackup(backup);
                return new(StartupChangeStatus.Applied,
                    enabled ? LocalizationService.Source("Message.ShortcutRestoredTheChangeTakesEffectAtThe")
                        : LocalizationService.Source("Message.ShortcutDisabledAndSavedTheChangeTakesEffect"));
            }
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { }
        return new(StartupChangeStatus.Unconfirmed,
            LocalizationService.Source("Message.TheShortcutWasMovedButItsFinalState"));
    }

    private void RemoveEmptyBackup(StartupBackup backup)
    {
        try { backups.RemoveEmptyBackup(backup); }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { }
    }

    private StartupEntry? FindCurrent(string id, CancellationToken token)
    {
        List<StartupEntry> entries = [];
        folders.ReadPersonal(entries, [], token);
        return entries.SingleOrDefault(entry => entry.Id == id);
    }

    private static bool MatchesId(StartupEntry entry) =>
        StartupFolderSource.Id(entry.Location, StartupSource.UserFolder) == entry.Id;

    private static bool SameTarget(StartupEntry first, StartupEntry second) =>
        string.Equals(first.FilePath, second.FilePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(first.Arguments, second.Arguments, StringComparison.Ordinal);

    private static StartupChangeResult Conflict() =>
        new(StartupChangeStatus.Conflict, LocalizationService.Source("Message.TheEntryChangedIsInUseOrIs"));
}
