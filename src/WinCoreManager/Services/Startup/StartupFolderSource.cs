using WinCoreManager.Localization;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace WinCoreManager.Services.Startup;

internal sealed class StartupFolderSource(
    string personal, string common, IStartupRegistry registry, IStartupShortcutReader shortcuts,
    IStartupProductReader products, StartupPolicy policy, StartupBackupStore backups) : IStartupSource
{
    private static readonly HashSet<string> StartupExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".lnk", ".exe", ".bat", ".cmd", ".vbs", ".js", ".ps1", ".url", ".wsf", ".com", ".msi", ".scr", ".pif", ".hta" };

    public string Label => LocalizationService.Source("Message.StartupFolders");
    internal string PersonalFolder => personal;

    public void Read(List<StartupEntry> entries, List<string> warnings, CancellationToken token)
    {
        ReadPersonal(entries, warnings, token);
        ReadFolder(common, StartupSource.CommonFolder, entries, warnings, [], token);
    }

    internal void ReadPersonal(List<StartupEntry> entries, List<string> warnings, CancellationToken token)
    {
        IReadOnlyList<StartupBackup> stored = [];
        var warningsBefore = warnings.Count;
        try { stored = backups.Read(warnings, token); }
        catch (Exception ex) when (StartupErrors.Expected(ex))
        { warnings.Add(LocalizationService.Source("Message.StartupBackupsCouldNotReadAllEntries")); }
        var backupAvailable = warnings.Count == warningsBefore;

        ReadFolder(personal, StartupSource.UserFolder, entries, warnings, stored, token, backupAvailable);
        foreach (var backup in stored)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = backup.Snapshot;
            try
            {
                if (!IsPersonalShortcut(snapshot.Original))
                {
                    warnings.Add(LocalizationService.Source("Message.ABackupIsLinkedToAnotherStartupFolder"));
                    continue;
                }

                var approval = ApprovalFor(snapshot.Original);
                var conflict = Occupied(snapshot.Original)
                    || stored.Count(item => SamePath(item.Snapshot.Original, snapshot.Original)) != 1;
                var entry = Inspect(snapshot.Original, backup.FilePath, StartupSource.UserFolder, approval, stored: true);
                if (!backupAvailable || conflict || entry.Fingerprint != $"{snapshot.Digest}/{snapshot.Approval}"
                    || approval.Status != StartupStatus.Enabled)
                {
                    entry = entry with
                    {
                        Id = conflict ? $"{entry.Id}:{snapshot.Id:N}" : entry.Id,
                        Protection = !backupAvailable ? LocalizationService.Source("Message.CouldNotReadAllBackups")
                            : conflict ? LocalizationService.Source("Message.TheBackupConflictsWithAnotherShortcutOrBackup")
                            : LocalizationService.Source("Message.TheBackupOrWindowsStateChangedAfterStartup"),
                    };
                }
                entries.Add(entry);
            }
            catch (Exception ex) when (StartupErrors.Expected(ex))
            { warnings.Add(LocalizationService.Source("Message.CouldNotReadAStartupBackup")); }
        }
    }

    private void ReadFolder(string folder, StartupSource source, List<StartupEntry> entries,
        List<string> warnings, IReadOnlyList<StartupBackup> stored, CancellationToken token, bool backupAvailable = true)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        try
        {
            try { _ = File.GetAttributes(folder); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
            if (!StartupPaths.IsSafeDirectory(folder))
            {
                warnings.Add(LocalizationService.Source("Message.0CouldNotVerifyThePath", source.Label()));
                return;
            }

            var userApproval = StartupApproval.Read(registry, RegistryHive.CurrentUser, "StartupFolder");
            var machineApproval = StartupApproval.Read(registry, RegistryHive.LocalMachine, "StartupFolder");
            if (userApproval is null || machineApproval is null) warnings.Add(LocalizationService.Source("Message.0PartialStatus", source.Label()));
            foreach (var path in Directory.EnumerateFiles(folder))
            {
                token.ThrowIfCancellationRequested();
                if (!StartupExtensions.Contains(Path.GetExtension(path))) continue;
                try
                {
                    var approval = StartupApproval.For(Path.GetFileName(path), userApproval, machineApproval);
                    var entry = Inspect(path, path, source, approval, stored: false);
                    if (!backupAvailable)
                        entry = entry with { Protection = LocalizationService.Source("Message.CouldNotReadAllBackups") };
                    else if (stored.Any(item => SamePath(item.Snapshot.Original, path)))
                        entry = entry with { Protection = LocalizationService.Source("Message.ThisShortcutAlreadyHasABackup") };
                    entries.Add(entry);
                }
                catch (Exception ex) when (StartupErrors.Expected(ex))
                {
                    warnings.Add(LocalizationService.Source("Message.0AFileCouldNotBeRead", source.Label()));
                    entries.Add(Unreadable(path, source));
                }
            }
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { warnings.Add(LocalizationService.Source("Message.0IncompleteScan", source.Label())); }
    }

    private StartupEntry Inspect(string original, string content, StartupSource source, StartupApproval approval, bool stored)
    {
        var name = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);
        var isShortcut = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        string? digest = null;
        StartupTarget target;
        if (isShortcut && StartupPaths.IsSafeFile(content))
        {
            using var stream = File.Open(content, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is > 0 and <= 4 * 1024 * 1024)
            {
                target = shortcuts.Read(content);
                digest = Convert.ToHexString(SHA256.HashData(stream));
            }
            else target = StartupTarget.Unresolved(LocalizationService.Source("Message.TheShortcutIsEmptyOrExceedsTheAllowed"));
        }
        else if (!isShortcut && extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            target = StartupPaths.Executable(original, null, expand: false);
        }
        else
        {
            target = StartupTarget.Unresolved(isShortcut ? LocalizationService.Source("Message.TheShortcutIsUnavailableOrItsPathCould")
                : LocalizationService.Source("Message.TheAssociatedFileHasNoDirectExecutable"));
        }

        var product = products.Read(target.Path);
        var protection = source != StartupSource.UserFolder ? LocalizationService.Source("Message.StartupEntriesForAllUsersAreReadOnly")
            : !IsPersonalShortcut(original) || digest is null ? LocalizationService.Source("Message.OnlyVerifiablePersonalShortcutsCanBeChanged")
            : approval.Status != StartupStatus.Enabled ? LocalizationService.Source("Message.WindowsManagesThisSettingOutsideTheApp")
            : !backups.CanStore(personal, common) ? LocalizationService.Source("Message.ASafeBackupLocationIsUnavailableForThis")
            : policy.PersonalShortcutProtection(target, product, name);
        var status = stored ? StartupStatus.Disabled : approval.Status;
        return new(Id(original, source), name, product?.Publisher, source, status, original,
            target.Path ?? (!isShortcut && StartupPaths.IsSafeFile(original) ? original : null),
            target.Arguments, target.Problem, policy.Recognized(name, product), protection,
            digest is null ? null : $"{digest}/{approval.Token}", stored);
    }

    internal StartupApproval ApprovalFor(string path) => StartupApproval.For(Path.GetFileName(path),
        StartupApproval.Read(registry, RegistryHive.CurrentUser, "StartupFolder"),
        StartupApproval.Read(registry, RegistryHive.LocalMachine, "StartupFolder"));

    internal bool IsPersonalShortcut(string? path) =>
        StartupPaths.TryLocalPath(path, out var normalized) && path == normalized
        && SamePath(Path.GetDirectoryName(normalized), personal)
        && !SamePath(personal, common) && !StartupPaths.IsWithin(personal, common)
        && Path.GetExtension(normalized).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    internal static string Id(string path, StartupSource source) => $"{source}:{path.ToUpperInvariant()}";

    internal static bool Occupied(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
    }

    private static bool SamePath(string? first, string? second) =>
        string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

    private static StartupEntry Unreadable(string path, StartupSource source) =>
        new(Id(path, source), Path.GetFileNameWithoutExtension(path), null, source, StartupStatus.Unknown, path,
            null, null, LocalizationService.Source("Message.CouldNotReadTheFile"), Protection: LocalizationService.Source("Message.ThisEntryCannotBeChanged"));
}

public static class StartupSourceLabels
{
    public static string Label(this StartupSource source) => source switch
    {
        StartupSource.UserRun => LocalizationService.Source("Message.UserRegistry"),
        StartupSource.MachineRun64 => LocalizationService.Source("Message.ComputerRegistry"),
        StartupSource.MachineRun32 => LocalizationService.Source("Message.ComputerRegistry32Bit"),
        StartupSource.UserFolder => LocalizationService.Source("Message.PersonalStartupFolder"),
        StartupSource.CommonFolder => LocalizationService.Source("Message.SharedStartupFolder"),
        StartupSource.ScheduledTask => LocalizationService.Source("Message.ScheduledTask"),
        _ => LocalizationService.Source("Message.UnknownSource"),
    };
}
