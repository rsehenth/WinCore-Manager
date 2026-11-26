using WinCoreManager.Localization;
using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace WinCoreManager.Services.Startup;

internal sealed record StartupRunValue(string Name, uint Type, byte[] Bytes)
{
    private static readonly UnicodeEncoding StrictUnicode = new(false, false, true);

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Command
    {
        get
        {
            if (Type is not (1 or 2) || Bytes.Length is < 4 or > 32768 || Bytes.Length % 2 != 0
                || Bytes[^1] != 0 || Bytes[^2] != 0) return null;
            try
            {
                var text = StrictUnicode.GetString(Bytes, 0, Bytes.Length - 2);
                return text.Contains('\0') ? null : text;
            }
            catch (DecoderFallbackException) { return null; }
        }
    }

    public bool Matches(StartupRunValue? other) =>
        other is not null && Name == other.Name && Type == other.Type && Bytes.AsSpan().SequenceEqual(other.Bytes);
}

internal interface IStartupRunRegistry
{
    IReadOnlyList<StartupRunValue> Read();
    string? WriteRestriction();
    bool CompareExchange(string name, StartupRunValue? expected, StartupRunValue? replacement,
        string approval, CancellationToken token);
}

internal sealed record StartupRunSnapshot(StartupRunValue Value, string Approval)
{
    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}

internal sealed class StartupRunSource(IStartupRunRegistry values, IStartupRegistry approvals,
    IStartupProductReader products, StartupPolicy policy, StartupRunBackups backups) : IStartupSource
{
    public string Label => "HKCU Run";

    public void Read(List<StartupEntry> entries, List<string> warnings, CancellationToken token)
    {
        var live = values.Read();
        Dictionary<string, StartupRunSnapshot?> stored = new(StringComparer.OrdinalIgnoreCase);
        var backupAvailable = backups.IsAvailable();
        if (backupAvailable)
        {
            try { stored = backups.Read(warnings); }
            catch (Exception ex) when (StartupErrors.Expected(ex)) { backupAvailable = false; }
        }
        var approved = StartupApproval.Read(approvals, RegistryHive.CurrentUser, "Run");
        var restriction = values.WriteRestriction();
        if (approved is null) warnings.Add(LocalizationService.Source("Message.HKCURunWindowsStatusUnavailable"));
        if (!backupAvailable) warnings.Add(LocalizationService.Source("Message.HKCURunBackupLocationUnavailable"));
        foreach (var value in live)
        {
            token.ThrowIfCancellationRequested();
            if (!ValidName(value.Name)) continue;
            var approval = StartupApproval.For(value.Name, approved, StartupApproval.Empty);
            var entry = Inspect(value, approval, backupAvailable, restriction, stored: false);
            if (backups.HasCorruption(value.Name, stored) || (stored.TryGetValue(value.Name, out var snapshot)
                && (snapshot is null || !snapshot.Value.Matches(value) || snapshot.Approval != approval.Token)))
                entry = entry with { Protection = LocalizationService.Source("Message.ThisValueHasAConflictingOrInvalidBackup") };
            entries.Add(entry);
        }

        foreach (var (name, backup) in stored)
        {
            token.ThrowIfCancellationRequested();
            if (backup is null) continue;
            var current = live.FirstOrDefault(value => value.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            var approval = StartupApproval.For(name, approved, StartupApproval.Empty);
            if (backup.Value.Matches(current) && backup.Approval == approval.Token) continue;
            var entry = Inspect(backup.Value, approval, backupAvailable, restriction, stored: true);
            if (current is not null)
                entry = entry with { Id = entry.Id + ":backup", Protection = LocalizationService.Source("Message.AnotherValueUsesTheOriginalRegistryName") };
            else if (approval.Token != backup.Approval)
                entry = entry with { Protection = LocalizationService.Source("Message.WindowsStateChangedAfterStartupWasDisabledCheck") };
            entries.Add(entry);
        }
    }

    public StartupChangeResult SetEnabled(StartupEntry expected, bool enabled, CancellationToken token)
    {
        if (expected.Source != StartupSource.UserRun || !expected.CanChange || !ValidName(expected.Name)
            || expected.Id != Id(expected.Name))
            return new(StartupChangeStatus.Protected, LocalizationService.Source("Message.ThisRegistryEntryIsProtected"));
        if (enabled == (expected.Status == StartupStatus.Enabled))
            return new(StartupChangeStatus.Unchanged, LocalizationService.Source("Message.NoChangeNeeded"));

        using var backupLock = backups.Lock();
        if (!StartupPaths.IsSafeFile(expected.FilePath)) return Conflict();
        using var targetDirectories = new WindowsStartupFileMove.DirectoryPins();
        targetDirectories.Pin(Path.GetDirectoryName(expected.FilePath!)!);
        using var executable = WindowsStartupFileMove.OpenVerifiedFileForRead(expected.FilePath!);

        var current = Find(expected.Id, token);
        if (current is null || !current.CanChange || current.Status != expected.Status || current.Stored != expected.Stored
            || current.Fingerprint != expected.Fingerprint || current.FilePath != expected.FilePath
            || current.Arguments != expected.Arguments) return Conflict();

        var snapshots = backups.Read([]);
        snapshots.TryGetValue(expected.Name, out var snapshot);
        StartupRunSnapshot? prepared = null;
        if (!enabled)
        {
            var live = values.Read().SingleOrDefault(value => value.Name == expected.Name);
            var approval = StartupApproval.For(expected.Name,
                StartupApproval.Read(approvals, RegistryHive.CurrentUser, "Run"), StartupApproval.Empty);
            if (live is null || approval.Status != StartupStatus.Enabled) return Conflict();
            var candidate = new StartupRunSnapshot(live, approval.Token);
            if (candidate.Fingerprint() != current.Fingerprint) return Conflict();
            snapshot ??= prepared = backups.Prepare(candidate);
        }

        try
        {
            if (snapshot is null || snapshot.Fingerprint() != current.Fingerprint
                || !values.CompareExchange(snapshot.Value.Name, enabled ? null : snapshot.Value,
                    enabled ? snapshot.Value : null, snapshot.Approval, token))
            {
                DiscardPreparedBackup(prepared);
                return Conflict();
            }
        }
        catch (StartupRunCommitException) { return Unconfirmed(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            DiscardPreparedBackup(prepared);
            throw;
        }

        try
        {
            var after = Find(expected.Id, CancellationToken.None);
            if (after?.Stored == !enabled && after.Fingerprint == expected.Fingerprint && after.CanChange
                && after.Status == (enabled ? StartupStatus.Enabled : StartupStatus.Disabled))
            {
                if (enabled) backups.Complete(snapshot);
                return new(StartupChangeStatus.Applied, enabled
                    ? LocalizationService.Source("Message.OriginalValueRestoredTheChangeTakesEffectAt")
                    : LocalizationService.Source("Message.EntryDisabledAndSavedTheChangeTakesEffect"));
            }
        }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { }
        return Unconfirmed();
    }

    private void DiscardPreparedBackup(StartupRunSnapshot? prepared)
    {
        if (prepared is null) return;
        try { backups.Complete(prepared); }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { }
    }

    private StartupEntry? Find(string id, CancellationToken token)
    {
        List<StartupEntry> entries = [];
        Read(entries, [], token);
        return entries.SingleOrDefault(entry => entry.Id == id);
    }

    private StartupEntry Inspect(StartupRunValue value, StartupApproval approval, bool backupAvailable,
        string? restriction, bool stored)
    {
        var command = value.Command;
        var target = command is null ? StartupTarget.Unresolved(LocalizationService.Source("Message.TheValueTypeOrDataIsUnsupported"))
            : StartupPaths.Command(command, expand: value.Type == 2);
        var product = products.Read(target.Path);
        var protection = approval.Status != StartupStatus.Enabled
            ? LocalizationService.Source("Message.StartupIsDisabledInWindowsOrItsStatus")
            : !backupAvailable ? LocalizationService.Source("Message.ASafeBackupLocationIsUnavailable")
            : restriction ?? policy.PersonalShortcutProtection(target, product, value.Name);
        return new(Id(value.Name), value.Name, product?.Publisher, StartupSource.UserRun,
            stored ? StartupStatus.Disabled : approval.Status, $@"HKCU\{StartupRegistrySource.RunKey} · {value.Name}",
            target.Path, target.Arguments, target.Problem, policy.Recognized(value.Name, product), protection,
            command is null ? null : new StartupRunSnapshot(value, approval.Token).Fingerprint(), stored, command);
    }

    internal static bool ValidName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 512
        && !value.Any(char.IsControl);

    private static string Id(string name) => "UserRun:" + name.ToUpperInvariant();

    private static StartupChangeResult Conflict() =>
        new(StartupChangeStatus.Conflict, LocalizationService.Source("Message.TheValueTargetOrWindowsStateChangedRefresh"));

    private static StartupChangeResult Unconfirmed() =>
        new(StartupChangeStatus.Unconfirmed, LocalizationService.Source("Message.TheValueWasWrittenButItsFinalState"));
}

internal sealed class StartupRunCommitException(Exception inner)
    : IOException(LocalizationService.Source("Message.CouldNotConfirmTheRegistryWriteWasSaved"), inner);

