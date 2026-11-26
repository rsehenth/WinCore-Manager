/*
 * Quarentena, backup e recuperação de resíduos.
 * Conceito de backup adaptado de Bulk Crap Uninstaller (Junk/Containers/RegistryKeyJunk.Backup exporta
 * .reg antes da exclusão)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Alterações: arquivos/pastas são MOVIDOS (não excluídos) para uma área controlada pelo app,
 * com manifest persistido e recuperação sem sobrescrever; exportação .reg feita pelo próprio app
 * (sem reg.exe) e snapshot JSON para recuperação supervisionada.
 */

using WinCoreManager.Localization;

using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Área de quarentena: movimentação de arquivos/pastas, exportação de chaves e recuperação.</summary>
public interface IResidueQuarantineService
{
    /// <summary>Move o item para a quarentena e registra no manifest. Lança InvalidOperationException com motivo em caso de falha.</summary>
    QuarantineEntry MoveToQuarantine(string originalPath, ResidueCategory category, string programName, CancellationToken ct, string? expectedFingerprint = null);

    /// <summary>Exporta a chave (com subchaves) para .reg + snapshot JSON dentro da quarentena; retorna o arquivo .reg.</summary>
    QuarantineEntry ExportRegistryKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, string fullKeyPath, string programName, CancellationToken ct, string? expectedFingerprint = null);

    IReadOnlyList<QuarantineEntry> GetEntries();

    QuarantineRestoreResult Restore(Guid entryId, bool overwriteConfirmed, CancellationToken ct);

    /// <summary>Problema atual do manifesto; enquanto houver, nenhuma operação altera a quarentena.</summary>
    string? ManifestProblem => null;
    bool CanRestoreRegistry => true;
    string? DescribeUnsupportedItem(ResidueItem item) => null;
    string? AlternateStorage(ResidueItem item) => null;
}

public enum QuarantineRestoreStatus
{
    Restored = 0,
    ConflictDestinationExists = 1,
    EntryNotFound = 2,
    SourceMissing = 3,
    Failed = 4,
}

public sealed record QuarantineRestoreResult(QuarantineRestoreStatus Status, string Message, QuarantineEntry? Entry);

/// <summary>O item saiu do local original, mas o registro final não foi concluído nem desfeito.</summary>
public sealed class QuarantineIncompleteException(string message, Exception inner) : InvalidOperationException(message, inner);

public sealed class ResidueQuarantineService(
    string quarantineRoot,
    IResidueRegistryReader registryReader,
    IResidueRegistryWriter registryWriter,
    ResiduePathRules rules,
    bool isElevatedProcess = false,
    string? sourceVolumeRoot = null,
    bool sourceVolumeFilesOnly = false) : IResidueQuarantineService
{
    internal const int MaxTreeEntries = 20000;

    private static readonly string ElevatedMessage = LocalizationService.Source("Message.WinCoreManagerIsRunningAsAnAdministratorQuarantine");
    private static readonly string CrossVolumeMessage = LocalizationService.Source("Message.QuarantineCrossVolume");

    private static readonly object ManifestLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private volatile string? _manifestProblem;
    private readonly ResidueQuarantineService? _sourceVolume = sourceVolumeRoot is not null
        && !string.Equals(Path.GetFullPath(quarantineRoot), Path.GetFullPath(sourceVolumeRoot), StringComparison.OrdinalIgnoreCase)
        ? new(sourceVolumeRoot, registryReader, registryWriter, rules, isElevatedProcess, sourceVolumeFilesOnly: true)
        : null;

    private string ManifestPath => Path.Combine(quarantineRoot, "manifest.json");

    public string? ManifestProblem => _manifestProblem ?? _sourceVolume?.ManifestProblem;
    public bool CanRestoreRegistry => registryWriter.SupportsMutation && !isElevatedProcess;

    public string? AlternateStorage(ResidueItem item) =>
        _sourceVolume is not null && (item.Category == ResidueCategory.File ||
            (item.Category == ResidueCategory.Folder && item.IsEligibleForCleanup && item.Fingerprint is not null))
        && ResiduePathRules.TryNormalize(item.Location, out var path)
        && RequiresCrossVolume(path, quarantineRoot)
        && !RequiresCrossVolume(path, sourceVolumeRoot!)
        && (item.Category == ResidueCategory.File || ResidueRecoveryIntegrity.Supports(path)) ? sourceVolumeRoot : null;

    public string? DescribeUnsupportedItem(ResidueItem item)
    {
        if (isElevatedProcess) return ElevatedMessage;
        if (item.Category == ResidueCategory.RegistryKey)
            return CanRestoreRegistry ? null : LocalizationService.Source("Message.Safety.RegistryMutationUnavailable");
        if (ResiduePathRules.TryNormalize(item.Location, out var path)
            && RequiresCrossVolume(path, quarantineRoot) && AlternateStorage(item) is null)
            return CrossVolumeMessage;
        return null;
    }

    /// <summary>Ponto de falha para testes: executa entre o movimento e o registro final.</summary>
    internal Action? AfterMoveForTest { get; set; }
    internal Action? AfterRestoreForTest { get; set; }

    public QuarantineEntry MoveToQuarantine(string originalPath, ResidueCategory category, string programName, CancellationToken ct, string? expectedFingerprint = null)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfElevated();
        if (category == ResidueCategory.RegistryKey)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.ThisCategoryDoesNotSupportFileMoves"));
        }

        var original = RequireSafeOriginal(originalPath);
        if (_sourceVolume is not null && (category == ResidueCategory.File || expectedFingerprint is not null)
            && RequiresCrossVolume(original, quarantineRoot))
        {
            return _sourceVolume.MoveToQuarantine(original, category, programName, ct, expectedFingerprint);
        }

        var sourceDirectory = sourceVolumeFilesOnly && category == ResidueCategory.Folder;
        if (RequiresCrossVolume(original, quarantineRoot) ||
            (sourceDirectory && (expectedFingerprint is null || !ResidueRecoveryIntegrity.Supports(original))))
        {
            throw new InvalidOperationException(CrossVolumeMessage);
        }

        if (!File.Exists(original) && !Directory.Exists(original))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheItemNoLongerExistsAndCannotBe"));
        }

        using var directoryPins = sourceVolumeFilesOnly ? new WindowsStartupFileMove.DirectoryPins() : null;
        if (directoryPins is not null)
        {
            directoryPins.Pin(Path.GetDirectoryName(original)!);
            directoryPins.Pin(quarantineRoot, create: true);
        }

        var root = PrepareRoot();
        using var guarded = ResidueFileGuard.OpenForMove(original, category);
        using var content = sourceVolumeFilesOnly && category == ResidueCategory.File
            ? File.Open(original, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)
            : null;
        if (guarded.VolumeSerial != ResidueFileGuard.GetVolumeSerial(root))
        {
            throw new InvalidOperationException(CrossVolumeMessage);
        }

        var inspection = ResidueFileGuard.Inspect(original, MaxTreeEntries, ct)
                         ?? throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotInspectTheItem"));
        if (!inspection.Complete || inspection.HasReparsePoint || inspection.HasHardLinks ||
            (sourceDirectory && inspection.HasExecutable))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.SomeContentCouldNotBeVerifiedOrContains2"));
        }

        if (expectedFingerprint is not null && !expectedFingerprint.Equals(inspection.Fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheItemChangedAfterTheScanTheOperation"));
        }

        ct.ThrowIfCancellationRequested();
        var contentHash = content is null ? null : Convert.ToHexString(SHA256.HashData(content));
        var recoveryFingerprint = (sourceDirectory || category == ResidueCategory.File) && ResidueRecoveryIntegrity.Supports(original)
            ? ResidueRecoveryIntegrity.Capture(original, guarded.IsDirectory, ct)
            : null;
        if (sourceDirectory && ResidueFileGuard.Inspect(original, MaxTreeEntries, ct)?.Fingerprint != inspection.Fingerprint)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheItemChangedAfterTheScanTheOperation"));
        }
        ct.ThrowIfCancellationRequested();
        var id = Guid.NewGuid();
        var target = Path.Combine(root, $"{id:N}_{SanitizeName(Path.GetFileName(original))}");
        var entry = new QuarantineEntry
        {
            Id = id,
            State = QuarantineEntryState.Pending,
            Fingerprint = inspection.Fingerprint,
            ContentSha256 = contentHash,
            RecoveryFingerprint = recoveryFingerprint,
            OriginalPath = original,
            QuarantinedPath = target,
            Category = category,
            ProgramName = programName,
            Timestamp = DateTimeOffset.Now,
            SizeBytes = inspection.TotalBytes,
        };

        lock (ManifestLock)
        {
            using var gate = AcquireManifestGate();
            var entries = ReadManifestLocked();

            // Registro antes do movimento: uma queda de energia não deixa item sem rastro.
            SaveManifestLocked([.. entries, entry]);
            try
            {
                ResidueFileGuard.RenameNoReplace(guarded.Handle, target);
            }
            catch
            {
                TrySaveManifestLocked(entries);
                throw;
            }

            try
            {
                AfterMoveForTest?.Invoke();
                if (recoveryFingerprint is not null &&
                    ResidueRecoveryIntegrity.Capture(target, guarded.IsDirectory, CancellationToken.None) != recoveryFingerprint)
                {
                    throw new IOException(LocalizationService.Source("Message.TheQuarantinedContentChangedOrIsIncompleteRestore"));
                }
                entry.State = QuarantineEntryState.Complete;
                SaveManifestLocked([.. entries, entry]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                entry.State = QuarantineEntryState.Pending;
                try
                {
                    ResidueFileGuard.RenameNoReplace(guarded.Handle, original);
                }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    throw new QuarantineIncompleteException(
                        LocalizationService.Source("Message.TheItemWasMovedTo0ButIts", target), ex);
                }

                TrySaveManifestLocked(entries);
                throw new InvalidOperationException(LocalizationService.Source("Message.TheManifestWriteFailedAndTheItemWas", ex.Message), ex);
            }
        }

        return entry;
    }

    /// <summary>Verdadeiro quando a movimentação atravessaria unidades (não é feita automaticamente).</summary>
    public static bool RequiresCrossVolume(string path, string quarantineRoot)
    {
        var pathRoot = Path.GetPathRoot(Path.GetFullPath(path));
        var quarantineDrive = Path.GetPathRoot(Path.GetFullPath(quarantineRoot));
        return !string.Equals(pathRoot, quarantineDrive, StringComparison.OrdinalIgnoreCase);
    }

    public QuarantineEntry ExportRegistryKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, string fullKeyPath, string programName, CancellationToken ct, string? expectedFingerprint = null)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfElevated();

        if (!ResiduePathRules.TryParseRegistryPath(fullKeyPath, out var parsedHive, out var parsedSubKey)
            || parsedHive != hive
            || !parsedSubKey.Equals(subKeyPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheRegistryKeyPathIsInconsistent"));
        }

        var protection = DescribeRegistryProtection(fullKeyPath);
        if (protection is not null)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.ProtectedRegistryKey0", protection));
        }

        if (!registryReader.KeyExists(hive, subKeyPath, is64BitView))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheRegistryKeyNoLongerExistsAndCannot"));
        }

        var snapshot = RegistrySnapshot.Capture(registryReader, hive, subKeyPath, is64BitView);
        var hash = RegistrySnapshot.ComputeHash(snapshot);
        if (expectedFingerprint is not null && !expectedFingerprint.Equals(hash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheRegistryKeyChangedAfterTheScanThe"));
        }

        var root = PrepareRoot();
        var id = Guid.NewGuid();
        var regPath = Path.Combine(root, $"{id:N}_{SanitizeName(subKeyPath.Split('\\').Last())}.reg");
        var snapshotPath = Path.Combine(root, $"{id:N}_snapshot.json");
        var entry = new QuarantineEntry
        {
            Id = id,
            Fingerprint = hash,
            OriginalPath = fullKeyPath,
            QuarantinedPath = snapshotPath,
            Category = ResidueCategory.RegistryKey,
            ProgramName = programName,
            Timestamp = DateTimeOffset.Now,
            RegistryBackupFile = regPath,
            Is64BitView = is64BitView,
        };

        try
        {
            WriteDurable(regPath, BuildRegFile(hive, subKeyPath, is64BitView, snapshot));
            WriteDurable(snapshotPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, JsonOptions)));

            var stored = JsonSerializer.Deserialize<RegistryKeyData>(File.ReadAllText(snapshotPath), JsonOptions);
            if (stored is null || RegistrySnapshot.ComputeHash(stored) != hash)
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotVerifyTheSavedSnapshot"));
            }

            lock (ManifestLock)
            {
                using var gate = AcquireManifestGate();
                SaveManifestLocked([.. ReadManifestLocked(), entry]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            TryDelete(regPath);
            TryDelete(snapshotPath);
            throw new InvalidOperationException(LocalizationService.Source("Message.CouldNotCompleteTheBackup0", ex.Message), ex);
        }

        return entry;
    }

    public IReadOnlyList<QuarantineEntry> GetEntries()
    {
        lock (ManifestLock)
        {
            try
            {
                using var gate = AcquireManifestGate();
                var primary = ReconcileLocked(ReadManifestLocked());
                return _sourceVolume is null ? primary : [.. primary, .. _sourceVolume.GetEntries()];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _manifestProblem ??= ex.Message;
                return [];
            }
        }
    }

    public QuarantineRestoreResult Restore(Guid entryId, bool overwriteConfirmed, CancellationToken ct)
    {
        if (isElevatedProcess)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed, ElevatedMessage, null);
        }

        QuarantineEntry? entry;
        try
        {
            lock (ManifestLock)
            {
                using var gate = AcquireManifestGate();
                entry = ReconcileLocked(ReadManifestLocked()).FirstOrDefault(e => e.Id == entryId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed, ex.Message, null);
        }

        if (entry is null)
        {
            if (_sourceVolume is not null)
            {
                return _sourceVolume.Restore(entryId, overwriteConfirmed, ct);
            }

            return new QuarantineRestoreResult(QuarantineRestoreStatus.EntryNotFound, LocalizationService.Source("Message.QuarantineEntryNotFound"), null);
        }

        ct.ThrowIfCancellationRequested();

        try
        {
            return entry.Category == ResidueCategory.RegistryKey
                ? RestoreRegistryKey(entry)
                : RestoreFileSystemItem(entry, overwriteConfirmed, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed, LocalizationService.Source("Message.RestoreFailed0", ex.Message), entry);
        }
    }

    private QuarantineRestoreResult RestoreFileSystemItem(QuarantineEntry entry, bool overwriteConfirmed, CancellationToken ct)
    {
        if (!IsDirectChildOfQuarantine(entry.QuarantinedPath))
        {
            return Failed(entry, LocalizationService.Source("Message.TheManifestPointsOutsideQuarantineRestoreWasBlocked"));
        }

        if (!ResiduePathRules.TryNormalize(entry.OriginalPath, out var destination))
        {
            return Failed(entry, LocalizationService.Source("Message.TheRecordedOriginalPathIsInvalid"));
        }

        if (sourceVolumeFilesOnly && (RequiresCrossVolume(destination, quarantineRoot) ||
            (entry.Category == ResidueCategory.File ? entry.ContentSha256 is null : entry.RecoveryFingerprint is null)))
        {
            return Failed(entry, CrossVolumeMessage);
        }

        var protection = rules.DescribeFileSystemProtection(destination, isReparsePoint: false);
        if (protection is not null)
        {
            return Failed(entry, LocalizationService.Source("Message.TheDestinationIsProtected0", protection));
        }

        if (!File.Exists(entry.QuarantinedPath) && !Directory.Exists(entry.QuarantinedPath))
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.SourceMissing,
                LocalizationService.Source("Message.TheQuarantinedItemNoLongerExists"), entry);
        }

        using var directoryPins = sourceVolumeFilesOnly ? new WindowsStartupFileMove.DirectoryPins() : null;
        if (directoryPins is not null)
        {
            directoryPins.Pin(quarantineRoot);
            directoryPins.Pin(Path.GetDirectoryName(destination)!, create: true);
        }

        PrepareRoot();
        using var guarded = ResidueFileGuard.OpenForMove(entry.QuarantinedPath, entry.Category);
        using var content = entry.ContentSha256 is null ? null
            : File.Open(entry.QuarantinedPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var inspection = ResidueFileGuard.Inspect(entry.QuarantinedPath, MaxTreeEntries, ct);
        if (inspection is null || !inspection.Complete || inspection.HasReparsePoint || inspection.HasHardLinks
            || (content is not null && entry.ContentSha256 != Convert.ToHexString(SHA256.HashData(content)))
            || (entry.RecoveryFingerprint is not null
                ? ResidueRecoveryIntegrity.Capture(entry.QuarantinedPath, guarded.IsDirectory, ct) != entry.RecoveryFingerprint
                : entry.Fingerprint is not null && entry.Fingerprint != inspection.Fingerprint))
        {
            return Failed(entry, LocalizationService.Source("Message.TheQuarantinedContentChangedOrIsIncompleteRestore"));
        }

        QuarantineEntry? displaced = null;
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            if (!overwriteConfirmed)
            {
                return new QuarantineRestoreResult(QuarantineRestoreStatus.ConflictDestinationExists,
                    LocalizationService.Source("Message.AnItemAlreadyExistsAtTheOriginalLocation"), entry);
            }

            // O item atual nunca é apagado: vai para a quarentena como uma nova entrada.
            displaced = MoveToQuarantine(
                destination,
                Directory.Exists(destination) ? ResidueCategory.Folder : ResidueCategory.File,
                LocalizationService.Source("Message.0ReplacedDuringRestore", entry.ProgramName),
                ct);
        }

        try
        {
            var parent = Path.GetDirectoryName(destination)!;
            var nearest = ResidueFileGuard.NearestExisting(parent);
            if (nearest is null || !ResidueFileGuard.IsPlainDirectory(nearest))
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheDestinationCrossesALinkJunctionOrMount"));
            }

            Directory.CreateDirectory(parent);
            if (!ResidueFileGuard.IsPlainDirectory(parent))
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheDestinationCrossesALinkJunctionOrMount"));
            }

            if (entry.RecoveryFingerprint is not null) MarkRestoring(entry);
            ResidueFileGuard.RenameNoReplace(guarded.Handle, destination);
            if (entry.RecoveryFingerprint is not null)
            {
                try
                {
                    if (ResidueRecoveryIntegrity.Capture(destination, guarded.IsDirectory, CancellationToken.None) != entry.RecoveryFingerprint)
                        throw new IOException(LocalizationService.Source("Message.TheQuarantinedContentChangedOrIsIncompleteRestore"));
                }
                catch
                {
                    ResidueFileGuard.RenameNoReplace(guarded.Handle, entry.QuarantinedPath);
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            var rollback = displaced is null ? null : Restore(displaced.Id, overwriteConfirmed: false, CancellationToken.None);
            var suffix = rollback is { Status: not QuarantineRestoreStatus.Restored }
                ? LocalizationService.Source("Message.DisplacedItemInQuarantine", displaced!.QuarantinedPath)
                : string.Empty;
            return Failed(entry, LocalizationService.Source("Message.RestoreFailed01", ex.Message, suffix));
        }

        try
        {
            AfterRestoreForTest?.Invoke();
            RemoveEntry(entry);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Restored,
                LocalizationService.Source("Message.ItemRestoredButTheManifestCouldNotBe", ex.Message), entry);
        }

        return new QuarantineRestoreResult(QuarantineRestoreStatus.Restored,
            displaced is null
                ? LocalizationService.Source("Message.ItemRestoredToItsOriginalLocation")
                : LocalizationService.Source("Message.ItemRestoredTheItemPreviouslyAtTheDestination"),
            entry);
    }

    private void MarkRestoring(QuarantineEntry entry)
    {
        lock (ManifestLock)
        {
            using var gate = AcquireManifestGate();
            var entries = ReadManifestLocked();
            var recorded = entries.SingleOrDefault(item => item.Id == entry.Id && item.RecoveryFingerprint == entry.RecoveryFingerprint &&
                item.Fingerprint == entry.Fingerprint &&
                item.OriginalPath == entry.OriginalPath && item.QuarantinedPath == entry.QuarantinedPath);
            if (recorded is null) throw new IOException();
            recorded.State = QuarantineEntryState.Restoring;
            SaveManifestLocked(entries);
        }
    }

    private QuarantineRestoreResult RestoreRegistryKey(QuarantineEntry entry)
    {
        if (!registryWriter.SupportsMutation)
        {
            return Failed(entry, LocalizationService.Source("Message.Safety.RegistryMutationUnavailable"));
        }

        if (!IsDirectChildOfQuarantine(entry.QuarantinedPath))
        {
            return Failed(entry, LocalizationService.Source("Message.TheManifestPointsOutsideQuarantineRestoreWasBlocked"));
        }

        if (!File.Exists(entry.QuarantinedPath))
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.SourceMissing,
                LocalizationService.Source("Message.TheRegistrySnapshotNoLongerExists"), entry);
        }

        RegistryKeyData? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RegistryKeyData>(File.ReadAllText(entry.QuarantinedPath), JsonOptions);
        }
        catch (JsonException ex)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed,
                LocalizationService.Source("Message.InvalidSnapshot0YouCanManuallyImportThe", ex.Message, entry.RegistryBackupFile), entry);
        }

        if (snapshot is null)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed, LocalizationService.Source("Message.TheSnapshotIsEmpty"), entry);
        }

        if (entry.Fingerprint is not null && entry.Fingerprint != RegistrySnapshot.ComputeHash(snapshot))
        {
            return Failed(entry, LocalizationService.Source("Message.TheSnapshotChangedAfterBackupRestoreWasBlocked"));
        }

        if (!ResiduePathRules.TryParseRegistryPath(entry.OriginalPath, out var hive, out var subKeyPath))
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed, LocalizationService.Source("Message.TheOriginalRegistryKeyPathIsInvalid"), entry);
        }

        var protection = DescribeRegistryProtection(entry.OriginalPath);
        if (protection is not null)
        {
            return Failed(entry, LocalizationService.Source("Message.TheDestinationRegistryKeyIsProtected0", protection));
        }

        if (registryReader.KeyExists(hive, subKeyPath, entry.Is64BitView))
        {
            if (entry.State == QuarantineEntryState.Restoring && entry.Fingerprint is not null &&
                entry.Fingerprint == RegistrySnapshot.ComputeHash(RegistrySnapshot.Capture(registryReader, hive, subKeyPath, entry.Is64BitView)))
            {
                RemoveEntry(entry);
                return new QuarantineRestoreResult(QuarantineRestoreStatus.Restored,
                    LocalizationService.Source("Message.RegistryKeyRecreatedFromTheSnapshotReviewPermissions"), entry);
            }
            return new QuarantineRestoreResult(QuarantineRestoreStatus.ConflictDestinationExists,
                LocalizationService.Source("Message.TheRegistryKeyAlreadyExistsAndWasLeft", entry.RegistryBackupFile), entry);
        }

        if (entry.Fingerprint is not null) MarkRestoring(entry);
        var errors = new List<string>();
        RecreateKey(hive, subKeyPath, snapshot, entry.Is64BitView, errors);

        if (errors.Count > 0)
        {
            return new QuarantineRestoreResult(QuarantineRestoreStatus.Failed,
                LocalizationService.Source("Message.PartialRestore") + string.Join(" ", errors)
                + " " + LocalizationService.Source("Message.RegistryBackupSavedAt0", entry.RegistryBackupFile), entry);
        }

        AfterRestoreForTest?.Invoke();
        RemoveEntry(entry);
        return new QuarantineRestoreResult(QuarantineRestoreStatus.Restored,
            LocalizationService.Source("Message.RegistryKeyRecreatedFromTheSnapshotReviewPermissions"), entry);
    }

    private void RecreateKey(ResidueRegistryHive hive, string subKeyPath, RegistryKeyData snapshot, bool is64BitView, List<string> errors)
    {
        if (!registryWriter.CreateKey(hive, subKeyPath, is64BitView, out var error))
        {
            errors.Add(LocalizationService.Source("Message.CouldNotRecreate01", subKeyPath, error));
            return;
        }

        foreach (var value in snapshot.Values)
        {
            var data = value.ToRegistryData();
            if (data is null
                || !registryWriter.SetValue(hive, subKeyPath, value.Name, value.Kind, data, is64BitView, out error))
            {
                errors.Add(LocalizationService.Source("Message.CouldNotRestoreValue01", value.Name, error));
            }
        }

        foreach (var child in snapshot.SubKeys)
        {
            RecreateKey(hive, $@"{subKeyPath}\{child.SubKeyPath}", child, is64BitView, errors);
        }
    }

    private static QuarantineRestoreResult Failed(QuarantineEntry entry, string message) =>
        new(QuarantineRestoreStatus.Failed, message, entry);

    private void ThrowIfElevated()
    {
        if (isElevatedProcess)
        {
            throw new InvalidOperationException(ElevatedMessage);
        }
    }

    private string? DescribeRegistryProtection(string fullKeyPath) => rules.DescribeRegistryProtection(
        fullKeyPath,
        ResiduePathRules.IsUninstallEntryPath(fullKeyPath) ? [fullKeyPath] : []);

    private string RequireSafeOriginal(string path)
    {
        if (!ResiduePathRules.TryNormalize(path, out var normalized))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.InvalidRelativeOrAmbiguousPath"));
        }

        var protection = rules.DescribeFileSystemProtection(normalized, isReparsePoint: false);
        if (protection is not null)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.ProtectedItem0", protection));
        }

        if (ResiduePathRules.IsSameOrUnder(normalized, quarantineRoot) || ResiduePathRules.IsSameOrUnder(quarantineRoot, normalized))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheQuarantineFolderCannotBeMovedIntoQuarantine"));
        }

        return normalized;
    }

    private string PrepareRoot()
    {
        if (!ResiduePathRules.TryNormalize(quarantineRoot, out var root))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheQuarantineLocationIsInvalidOrIsNot"));
        }

        var nearest = ResidueFileGuard.NearestExisting(root);
        if (nearest is null || !ResidueFileGuard.IsPlainDirectory(nearest))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheQuarantineLocationCrossesALinkJunctionOr"));
        }

        Directory.CreateDirectory(root);
        if (!ResidueFileGuard.IsPlainDirectory(root))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheQuarantineLocationCrossesALinkJunctionOr"));
        }

        return root;
    }

    private bool IsDirectChildOfQuarantine(string path) =>
        ResiduePathRules.TryNormalize(path, out var normalized)
        && ResiduePathRules.TryNormalize(quarantineRoot, out var root)
        && string.Equals(Path.GetDirectoryName(normalized), root, StringComparison.OrdinalIgnoreCase);

    private void RemoveEntry(QuarantineEntry entry)
    {
        lock (ManifestLock)
        {
            using var gate = AcquireManifestGate();
            SaveManifestLocked(ReadManifestLocked().Where(e => e.Id != entry.Id).ToList());
        }
    }

    // Duas instâncias do app não podem intercalar leitura e gravação do manifesto.
    private FileStream? AcquireManifestGate()
    {
        if (!Directory.Exists(quarantineRoot))
        {
            return null;
        }

        if (sourceVolumeFilesOnly && !ResidueFileGuard.IsPlainDirectory(quarantineRoot))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheQuarantineLocationCrossesALinkJunctionOr"));
        }

        var path = Path.Combine(quarantineRoot, "manifest.lock");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(100);
            }
        }
    }

    private List<QuarantineEntry> ReadManifestLocked()
    {
        if (!File.Exists(ManifestPath))
        {
            _manifestProblem = null;
            return [];
        }

        try
        {
            var entries = JsonSerializer.Deserialize<List<QuarantineEntry>>(File.ReadAllText(ManifestPath), JsonOptions)
                          ?? throw new JsonException(LocalizationService.Source("Message.TheManifestIsEmpty"));
            _manifestProblem = null;
            return entries;
        }
        catch (JsonException ex)
        {
            // Nunca recomeça vazio: isso apagaria o caminho de volta de todos os itens guardados.
            var copy = PreserveCorruptManifest();
            var message = LocalizationService.Source("Message.TheQuarantineManifestIsCorrupt0NoChanges", ex.Message, copy);
            _manifestProblem = message;
            throw new InvalidOperationException(message, ex);
        }
    }

    private string PreserveCorruptManifest()
    {
        var bytes = File.ReadAllBytes(ManifestPath);
        var copy = Path.Combine(quarantineRoot, $"manifest.corrupt-{Convert.ToHexString(SHA256.HashData(bytes))[..12]}.json");
        if (!File.Exists(copy))
        {
            File.WriteAllBytes(copy, bytes);
        }

        return copy;
    }

    /// <summary>Resolve entradas Pending deixadas por uma interrupção, sem mover nada.</summary>
    private List<QuarantineEntry> ReconcileLocked(List<QuarantineEntry> entries)
    {
        var changed = false;
        var result = new List<QuarantineEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.State == QuarantineEntryState.Restoring && entry.RecoveryFingerprint is { } fingerprint &&
                entry.Category is ResidueCategory.File or ResidueCategory.Folder &&
                IsDirectChildOfQuarantine(entry.QuarantinedPath) &&
                !File.Exists(entry.QuarantinedPath) && !Directory.Exists(entry.QuarantinedPath) &&
                ResiduePathRules.TryNormalize(entry.OriginalPath, out var original) &&
                rules.DescribeFileSystemProtection(original, isReparsePoint: false) is null)
            {
                try
                {
                    if (ResidueRecoveryIntegrity.Capture(original, entry.Category == ResidueCategory.Folder, CancellationToken.None) == fingerprint)
                    {
                        changed = true;
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
            }
            if (entry.State == QuarantineEntryState.Pending && entry.Category != ResidueCategory.RegistryKey)
            {
                if (File.Exists(entry.QuarantinedPath) || Directory.Exists(entry.QuarantinedPath))
                {
                    entry.State = QuarantineEntryState.Complete;
                    changed = true;
                }
                else if (File.Exists(entry.OriginalPath) || Directory.Exists(entry.OriginalPath))
                {
                    changed = true;
                    continue;
                }
            }

            result.Add(entry);
        }

        if (changed)
        {
            TrySaveManifestLocked(result);
        }

        return result;
    }

    private void SaveManifestLocked(IReadOnlyList<QuarantineEntry> entries)
    {
        var temporaryPath = ManifestPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteDurable(temporaryPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(entries, JsonOptions)));
            File.Move(temporaryPath, ManifestPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void TrySaveManifestLocked(IReadOnlyList<QuarantineEntry> entries)
    {
        try
        {
            SaveManifestLocked(entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Entradas Pending são reconciliadas na próxima leitura.
            System.Diagnostics.Debug.WriteLine(LocalizationService.Source("Message.CouldNotUpdateTheManifest0", ex.Message));
        }
    }

    private static void WriteDurable(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine(LocalizationService.Source("Message.CouldNotRemoveTheTemporaryFile0", ex.Message));
        }
    }

    // ------------------------------------------------------------- Exportação .reg

    private static byte[] BuildRegFile(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, RegistryKeyData snapshot)
    {
        var builder = new StringBuilder();
        builder.Append("Windows Registry Editor Version 5.00\r\n\r\n");
        WriteRegKey(builder, HiveName(hive) + @"\" + PhysicalSubKeyPath(hive, subKeyPath, is64BitView), snapshot);
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        try
        {
            return [.. encoding.GetPreamble(), .. encoding.GetBytes(builder.ToString())];
        }
        catch (EncoderFallbackException ex)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheRegistryKeyPathIsInconsistent"), ex);
        }
    }

    // O .reg é importado na visão de 64 bits: a chave de 32 bits precisa do caminho físico.
    private static string PhysicalSubKeyPath(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) =>
        hive == ResidueRegistryHive.LocalMachine
        && !is64BitView
        && subKeyPath.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
        && !subKeyPath.StartsWith(@"SOFTWARE\Wow6432Node\", StringComparison.OrdinalIgnoreCase)
            ? @"SOFTWARE\Wow6432Node\" + subKeyPath[@"SOFTWARE\".Length..]
            : subKeyPath;

    private static void WriteRegKey(StringBuilder builder, string fullPath, RegistryKeyData snapshot)
    {
        builder.Append('[').Append(fullPath).Append("]\r\n");
        foreach (var value in snapshot.Values)
        {
            builder.Append(value.ToRegLine()).Append("\r\n");
        }

        builder.Append("\r\n");
        foreach (var child in snapshot.SubKeys)
        {
            WriteRegKey(builder, $@"{fullPath}\{child.SubKeyPath}", child);
        }
    }

    private static string HiveName(ResidueRegistryHive hive) => hive switch
    {
        ResidueRegistryHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        ResidueRegistryHive.CurrentUser => "HKEY_CURRENT_USER",
        _ => throw new InvalidOperationException(LocalizationService.Source("Message.UnsupportedRegistryHive")),
    };

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        var result = builder.ToString();
        return result.Length > 60 ? result[..60] : result;
    }
}

/// <summary>Captura integral de uma chave: qualquer item que não possa ser lido falha o backup.</summary>
internal static class RegistrySnapshot
{
    private const int MaxNodes = 5000;
    private const int MaxDepth = 32;
    private const int MaxValuesPerKey = 16383;

    private static readonly JsonSerializerOptions HashOptions = new() { WriteIndented = false };

    public static RegistryKeyData Capture(IResidueRegistryReader reader, ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        if (subKeyPath.Split('\\').Any(name => !IsSafeKeyName(name)))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheRegistryKeyPathIsInconsistent"));
        }

        var remaining = MaxNodes;
        return CaptureNode(reader, hive, subKeyPath, is64BitView, 0, ref remaining);
    }

    public static string ComputeHash(RegistryKeyData snapshot) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, HashOptions)));

    /// <summary>Todos os nomes de subchaves da árvore, para detectar componentes compartilhados.</summary>
    public static IEnumerable<string> SubKeyNames(RegistryKeyData snapshot) =>
        snapshot.SubKeys.SelectMany(child => SubKeyNames(child).Prepend(child.SubKeyPath));

    /// <summary>Subchaves em pós-ordem: filhos antes dos pais, terminando na própria chave.</summary>
    public static IEnumerable<string> PostOrderPaths(string subKeyPath, RegistryKeyData snapshot) =>
        snapshot.SubKeys
            .SelectMany(child => PostOrderPaths($@"{subKeyPath}\{child.SubKeyPath}", child))
            .Append(subKeyPath);

    private static RegistryKeyData CaptureNode(
        IResidueRegistryReader reader, ResidueRegistryHive hive, string subKeyPath, bool is64BitView, int depth, ref int remaining)
    {
        if (depth > MaxDepth || --remaining < 0)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheKeyIsTooLargeToBackUp"));
        }

        var contents = reader.ReadKeyStrict(hive, subKeyPath, is64BitView);
        if (contents.IsSymbolicLink)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.0IsARegistryLinkRemovalWasBlocked", subKeyPath));
        }

        if (contents.Values.Count > MaxValuesPerKey)
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheKeyHasTooManyValuesToBack"));
        }

        var values = contents.Values
            .OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .Select(value => RegistryValueData.From(value.Name, value.Kind, value.Data))
            .ToList();

        var children = new List<RegistryKeyData>(contents.SubKeyNames.Count);
        foreach (var child in contents.SubKeyNames.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!IsSafeKeyName(child))
            {
                throw new InvalidOperationException(LocalizationService.Source("Message.TheRegistryKeyPathIsInconsistent"));
            }

            children.Add(CaptureNode(reader, hive, $@"{subKeyPath}\{child}", is64BitView, depth + 1, ref remaining));
        }

        return new RegistryKeyData
        {
            SubKeyPath = subKeyPath.Split('\\').Last(),
            Values = values,
            SubKeys = children,
        };
    }

    private static bool IsSafeKeyName(string name) =>
        name.Length > 0 && !name.Any(character => char.IsControl(character) || character is '[' or ']' or '\\');
}

/// <summary>Valor de chave do Registro serializável para snapshot e exportação .reg.</summary>
public sealed record RegistryValueData(string Name, RegistryValueKind Kind, string? Text, long? Number, string? Hex, IReadOnlyList<string>? Multi)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeHex { get; init; }

    public static RegistryValueData From(string name, RegistryValueKind kind, object data) => (kind, data) switch
    {
        (_, NativeRegistryValue native) => new RegistryValueData(name, kind, null, null, null, null)
        {
            NativeHex = Convert.ToHexString(native.Bytes),
        },
        (RegistryValueKind.DWord, int number) => new RegistryValueData(name, kind, null, number, null, null),
        (RegistryValueKind.QWord, long number) => new RegistryValueData(name, kind, null, number, null, null),
        (RegistryValueKind.String or RegistryValueKind.ExpandString, string text) => new RegistryValueData(name, kind, text, null, null, null),
        (RegistryValueKind.MultiString, string[] multi) => new RegistryValueData(name, kind, null, null, null, multi),
        (RegistryValueKind.Binary or RegistryValueKind.None, byte[] bytes) => new RegistryValueData(name, kind, null, null, Convert.ToHexString(bytes), null),
        _ => throw new InvalidOperationException(LocalizationService.Source("Message.TheBackupDoesNotSupportTheTypeOf", name)),
    };

    public object? ToRegistryData() => NativeHex is not null
        ? new NativeRegistryValue(Convert.FromHexString(NativeHex))
        : Kind switch
    {
        RegistryValueKind.DWord => Number is >= int.MinValue and <= int.MaxValue ? (int)Number.Value : null,
        RegistryValueKind.QWord => Number,
        RegistryValueKind.Binary or RegistryValueKind.None => Hex is null ? null : Convert.FromHexString(Hex),
        RegistryValueKind.MultiString => Multi?.ToArray(),
        RegistryValueKind.ExpandString => Text,
        RegistryValueKind.String => Text,
        _ => null,
    };

    /// <summary>Linha no formato .reg (Windows Registry Editor 5.00).</summary>
    public string ToRegLine()
    {
        if (Name.Any(char.IsControl))
        {
            throw new InvalidOperationException(LocalizationService.Source("Message.TheValueNameCannotBeRepresentedInA"));
        }

        var name = Name.Length == 0 ? "@" : "\"" + Escape(Name) + "\"";
        if (NativeHex is not null)
        {
            var bytes = Convert.FromHexString(NativeHex);
            return $"{name}=hex({NativeRegistryValue.GetTypeCode(Kind):x}):{ToRegHex(bytes)}";
        }

        return Kind switch
        {
            RegistryValueKind.String when Text is not null && Text.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0
                => $"{name}=hex(1):{ToRegHex(Encoding.Unicode.GetBytes(Text + "\0"))}",
            RegistryValueKind.String => $"{name}=\"{Escape(Text ?? string.Empty)}\"",
            RegistryValueKind.DWord => $"{name}=dword:{unchecked((uint)(int)(Number ?? 0)):x8}",
            RegistryValueKind.QWord => $"{name}=hex(b):{ToRegHex(BitConverter.GetBytes(Number ?? 0))}",
            RegistryValueKind.Binary => $"{name}=hex:{ToRegHex(Convert.FromHexString(Hex ?? string.Empty))}",
            RegistryValueKind.None => $"{name}=hex(0):{ToRegHex(Convert.FromHexString(Hex ?? string.Empty))}",
            RegistryValueKind.MultiString => $"{name}=hex(7):{ToRegHex(Encoding.Unicode.GetBytes(string.Concat((Multi ?? []).Select(item => item + "\0")) + "\0"))}",
            RegistryValueKind.ExpandString => $"{name}=hex(2):{ToRegHex(Encoding.Unicode.GetBytes((Text ?? string.Empty) + "\0"))}",
            _ => throw new InvalidOperationException(LocalizationService.Source("Message.TheBackupDoesNotSupportTheTypeOf", Name)),
        };
    }

    private static string Escape(string text) => text
        .Replace("\\", "\\\\")
        .Replace("\"", "\\\"");

    private static string ToRegHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant() is { Length: > 0 } hex
        ? string.Join(',', Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)))
        : string.Empty;
}

/// <summary>Snapshot serializável de uma chave (valores + subchaves recursivas).</summary>
public sealed record RegistryKeyData
{
    public string SubKeyPath { get; init; } = string.Empty;
    public IReadOnlyList<RegistryValueData> Values { get; init; } = Array.Empty<RegistryValueData>();
    public IReadOnlyList<RegistryKeyData> SubKeys { get; init; } = Array.Empty<RegistryKeyData>();
}
