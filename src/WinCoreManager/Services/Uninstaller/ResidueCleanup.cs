/*
 * Planejamento e execução da limpeza de resíduos.
 * Conceitos adaptados de Bulk Crap Uninstaller (JunkManager/CleanUpResults para bloqueios e
 * RegistryKeyJunk.Backup/Delete para chaves)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Alterações: plano revalidado executando a detecção novamente (uma única fonte de verdade);
 * backup obrigatório — falha de backup impede a remoção; execução sequencial; cancelamento
 * preserva itens concluídos e impede os seguintes; sem shell e sem tomada de posse de arquivos.
 */

using WinCoreManager.Localization;

using System.Diagnostics;
using System.IO;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>
/// Monta o plano de limpeza revalidando cada item selecionado contra uma nova detecção:
/// itens que mudaram de caminho, identidade ou classificação desde a prévia são bloqueados.
/// </summary>
public sealed class ResidueCleanupPlanner(IResidueAnalysisService analysisService, IProgramInventoryService? inventory = null)
{
    /// <summary>
    /// selectedItems deve conter apenas itens vindos da prévia apresentada ao usuário.
    /// </summary>
    public async Task<ResidueCleanupPlan> BuildPlanAsync(
        InstalledProgram program,
        IReadOnlyList<InstalledProgram> allPrograms,
        IReadOnlyList<ResidueItem> selectedItems,
        CancellationToken ct)
    {
        var operationId = Guid.NewGuid();
        var approved = new List<ResidueItem>();
        var blocked = new List<ResidueCleanupItemResult>();

        if (selectedItems.Count == 0)
        {
            return new ResidueCleanupPlan
            {
                OperationId = operationId,
                Program = program,
                Approved = approved,
                Blocked = blocked,
            };
        }

        ResidueScanResult revalidated;
        try
        {
            // A lista da abertura da janela pode estar velha: um programa instalado depois também protege seus itens.
            var currentPrograms = inventory is null
                ? allPrograms
                : [.. allPrograms, .. await inventory.GetInstalledProgramsAsync(ct).ConfigureAwait(true)];
            revalidated = await analysisService.DetectAsync(program, currentPrograms, ct).ConfigureAwait(true);
            if (revalidated.ScanFailed)
            {
                throw new IOException(revalidated.Error ?? LocalizationService.Source("Message.TheScanFailed"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or InvalidOperationException or System.Security.SecurityException)
        {
            return new ResidueCleanupPlan
            {
                OperationId = operationId,
                Program = program,
                Blocked = selectedItems.Select(item => new ResidueCleanupItemResult
                {
                    Item = item,
                    Outcome = ResidueCleanupItemOutcome.SkippedInvalid,
                    Message = LocalizationService.Source("Message.CouldNotValidateTheItemsAgain0", ex.Message),
                }).ToList(),
            };
        }

        var current = revalidated.Items
            .ToDictionary(i => $"{i.Category}|{i.Location}|{i.Is64BitView}|{i.ValueName}", i => i, StringComparer.OrdinalIgnoreCase);

        foreach (var item in selectedItems)
        {
            if (!current.TryGetValue($"{item.Category}|{item.Location}|{item.Is64BitView}|{item.ValueName}", out var revalidatedItem))
            {
                blocked.Add(new ResidueCleanupItemResult
                {
                    Item = item,
                    Outcome = ResidueCleanupItemOutcome.SkippedInvalid,
                    Message = LocalizationService.Source("Message.TheItemChangedOrDisappearedSinceThePreview"),
                });
                continue;
            }

            if (revalidatedItem.Confidence != ResidueConfidence.High)
            {
                blocked.Add(new ResidueCleanupItemResult
                {
                    Item = revalidatedItem,
                    Outcome = ResidueCleanupItemOutcome.SkippedInvalid,
                    Message = LocalizationService.Source("Message.TheClassificationChangedTo0SinceThePreview", revalidatedItem.ConfidenceLabel),
                });
                continue;
            }

            if (revalidatedItem.Fingerprint is null
                || !string.Equals(item.Fingerprint, revalidatedItem.Fingerprint, StringComparison.Ordinal))
            {
                blocked.Add(new ResidueCleanupItemResult
                {
                    Item = revalidatedItem,
                    Outcome = ResidueCleanupItemOutcome.SkippedInvalid,
                    Message = LocalizationService.Source("Message.TheItemSContentsOrIdentityChangedSince"),
                });
                continue;
            }

            approved.Add(revalidatedItem);
        }

        return new ResidueCleanupPlan
        {
            OperationId = operationId,
            Program = program,
            Approved = approved,
            Blocked = blocked,
        };
    }
}

/// <summary>
/// Executor sequencial da limpeza: para cada item, backup obrigatório (quarentena para
/// arquivos/pastas, exportação .reg + snapshot para chaves) e só então a remoção.
/// Falha de backup NUNCA remove o item. Cancelamento preserva o que já foi concluído.
/// </summary>
public sealed class ResidueCleanupExecutor(
    IResidueQuarantineService quarantine,
    IResidueRegistryReader registryReader,
    IResidueRegistryWriter registryWriter,
    IHistoryService history)
{
    public async Task<ResidueCleanupReport> ExecuteAsync(
        ResidueCleanupPlan plan,
        Action<string>? onLog = null,
        Action<ResidueCleanupItemResult>? onItemCompleted = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<ResidueCleanupItemResult>(plan.Approved.Count + plan.Blocked.Count);
        results.AddRange(plan.Blocked);
        var cancelled = false;

        foreach (var item in plan.Approved)
        {
            if (cancelled || cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                var skipped = new ResidueCleanupItemResult
                {
                    Item = item,
                    Outcome = ResidueCleanupItemOutcome.SkippedCancelled,
                    Message = LocalizationService.Source("Message.SkippedBecauseTheUserCanceledTheRemainingOperations"),
                };
                results.Add(skipped);
                onItemCompleted?.Invoke(skipped);
                continue;
            }

            ResidueCleanupItemResult result;
            try
            {
                result = item.Category switch
                {
                    ResidueCategory.RegistryKey when registryWriter is IAtomicResidueRegistryWriter && !plan.RegistryConsentGranted =>
                        Blocked(item, LocalizationService.Source("Message.ReviewRequired")),
                    ResidueCategory.RegistryKey => CleanRegistryKey(item, plan.Program.Name, cancellationToken),
                    ResidueCategory.RegistryValue => Blocked(item, LocalizationService.Source("ResidueAdvanced.EvidenceOnly")),
                    _ => CleanFileSystemItem(item, plan.Program.Name, cancellationToken),
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                result = new ResidueCleanupItemResult
                {
                    Item = item,
                    Outcome = ResidueCleanupItemOutcome.SkippedCancelled,
                    Message = LocalizationService.Source("Message.SkippedBecauseTheUserCanceled"),
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                result = new ResidueCleanupItemResult
                {
                    Item = item,
                    Outcome = ResidueCleanupItemOutcome.Failed,
                    Message = ex.Message,
                };
            }

            results.Add(result);
            onLog?.Invoke($"{item.CategoryLabel} {item.Location}: {DescribeOutcome(result)}");
            onItemCompleted?.Invoke(result);
        }

        var report = new ResidueCleanupReport
        {
            OperationId = plan.OperationId,
            ProgramName = plan.Program.Name,
            Results = results,
            WasCancelledByUser = cancelled,
        };

        await RecordHistoryAsync(report).ConfigureAwait(true);
        return report;
    }

    private ResidueCleanupItemResult CleanFileSystemItem(ResidueItem item, string programName, CancellationToken ct)
    {
        if (item.Fingerprint is null)
        {
            return Blocked(item, LocalizationService.Source("Message.TheItemSIdentityCouldNotBeVerified"));
        }

        // A quarentena É a remoção: o item sai do local original. Falha de backup = nada é removido.
        QuarantineEntry entry;
        try
        {
            entry = quarantine.MoveToQuarantine(item.Location, item.Category, programName, ct, item.Fingerprint);
        }
        catch (QuarantineIncompleteException ex)
        {
            return new ResidueCleanupItemResult
            {
                Item = item,
                Outcome = ResidueCleanupItemOutcome.Failed,
                Message = ex.Message,
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ResidueCleanupItemResult
            {
                Item = item,
                Outcome = ResidueCleanupItemOutcome.BackupFailed,
                Message = LocalizationService.Source("Message.QuarantineDidNotFinish0TheItemWas", ex.Message),
            };
        }

        return new ResidueCleanupItemResult
        {
            Item = item,
            Outcome = ResidueCleanupItemOutcome.Success,
            Message = item.Category == ResidueCategory.Folder
                ? LocalizationService.Source("Message.FolderMovedToQuarantine")
                : LocalizationService.Source("Message.FileMovedToQuarantine"),
            QuarantineEntryId = entry.Id,
        };
    }

    private ResidueCleanupItemResult CleanRegistryKey(ResidueItem item, string programName, CancellationToken ct)
    {
        if (!registryWriter.SupportsMutation)
        {
            return Blocked(item, LocalizationService.Source("Message.Safety.RegistryMutationUnavailable"));
        }

        if (item.Fingerprint is null)
        {
            return Blocked(item, LocalizationService.Source("Message.TheRegistryKeySIdentityCouldNotBe"));
        }

        if (!ResiduePathRules.TryParseRegistryPath(item.Location, out var hive, out var subKeyPath))
        {
            return Blocked(item, LocalizationService.Source("Message.TheRegistryKeyPathIsInvalidTheOperation"));
        }

        if (!registryWriter.CanWriteKey(hive, subKeyPath))
            return Blocked(item, LocalizationService.Source("ResidueAdvanced.RegistryReadOnly"));

        if (!registryReader.KeyExists(hive, subKeyPath, item.Is64BitView))
        {
            return Blocked(item, LocalizationService.Source("Message.TheRegistryKeyNoLongerExistsThereIs"));
        }

        // Backup obrigatório: exportação .reg + snapshot antes de qualquer exclusão.
        QuarantineEntry backup;
        try
        {
            backup = quarantine.ExportRegistryKey(hive, subKeyPath, item.Is64BitView, item.Location, programName, ct, item.Fingerprint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return new ResidueCleanupItemResult
            {
                Item = item,
                Outcome = ResidueCleanupItemOutcome.BackupFailed,
                Message = LocalizationService.Source("Message.TheRequiredBackupFailed0TheRegistryKey", ex.Message),
            };
        }

        // Só remove o que o backup contém: qualquer mudança depois da exportação bloqueia a exclusão.
        RegistryKeyData current;
        try
        {
            current = RegistrySnapshot.Capture(registryReader, hive, subKeyPath, item.Is64BitView);
        }
        catch (InvalidOperationException ex)
        {
            return RegistryNotRemoved(item, backup, LocalizationService.Source("Message.CouldNotVerifyTheRegistryKeyAfterBackup", ex.Message));
        }

        if (RegistrySnapshot.ComputeHash(current) != backup.Fingerprint)
        {
            return RegistryNotRemoved(item, backup, LocalizationService.Source("Message.TheRegistryKeyChangedAfterBackup"));
        }

        ct.ThrowIfCancellationRequested();
        if (registryWriter is IAtomicResidueRegistryWriter atomic)
        {
            var outcome = atomic.DeleteVerifiedOutcome(hive, subKeyPath, item.Is64BitView, current, ct);
            if (!outcome.Succeeded)
            {
                return outcome.RemovedKeys > 0
                    ? new ResidueCleanupItemResult
                    {
                        Item = item,
                        Outcome = ResidueCleanupItemOutcome.Failed,
                        Message = LocalizationService.Source("Message.PARTIALREMOVAL0Of1KeysRemoved2",
                            outcome.RemovedKeys, outcome.TotalKeys, outcome.Error),
                        QuarantineEntryId = backup.Id,
                        RegistryBackupFile = backup.RegistryBackupFile,
                    }
                    : RegistryNotRemoved(item, backup, outcome.Error);
            }
            return RegistryRemoved(item, backup, outcome.Mode);
        }
        var paths = RegistrySnapshot.PostOrderPaths(subKeyPath, current).ToList();
        for (var i = 0; i < paths.Count; i++)
        {
            var separator = paths[i].LastIndexOf('\\');
            if (registryWriter.DeleteEmptySubKey(hive, paths[i][..separator], paths[i][(separator + 1)..], item.Is64BitView, out var deleteError))
            {
                continue;
            }

            return new ResidueCleanupItemResult
            {
                Item = item,
                Outcome = ResidueCleanupItemOutcome.Failed,
                Message = i == 0
                    ? LocalizationService.Source("Message.CouldNotRemoveTheRegistryKey0Backup", deleteError)
                    : LocalizationService.Source("Message.PARTIALREMOVAL0Of1KeysRemoved2", i, paths.Count, deleteError),
                QuarantineEntryId = backup.Id,
                RegistryBackupFile = backup.RegistryBackupFile,
            };
        }

        return RegistryRemoved(item, backup);
    }

    private static ResidueCleanupItemResult RegistryRemoved(ResidueItem item, QuarantineEntry backup,
        RegistryMutationMode mode = RegistryMutationMode.Transactional) => new()
    {
        Item = item,
        Outcome = ResidueCleanupItemOutcome.Success,
        Message = LocalizationService.Source(mode == RegistryMutationMode.SupervisedNonTransactional
            ? "ResidueAdvanced.NonTransactionalRemoved"
            : "Message.RegistryKeyRemovedRegBackupAndSnapshotAvailable"),
        QuarantineEntryId = backup.Id,
        RegistryBackupFile = backup.RegistryBackupFile,
    };

    private static ResidueCleanupItemResult Blocked(ResidueItem item, string message) => new()
    {
        Item = item,
        Outcome = ResidueCleanupItemOutcome.SkippedInvalid,
        Message = message,
    };

    private static ResidueCleanupItemResult RegistryNotRemoved(ResidueItem item, QuarantineEntry backup, string reason) => new()
    {
        Item = item,
        Outcome = ResidueCleanupItemOutcome.Failed,
        Message = LocalizationService.Source("Message.0TheRegistryKeyWasKeptBackupPreserved", reason),
        QuarantineEntryId = backup.Id,
        RegistryBackupFile = backup.RegistryBackupFile,
    };

    private static string DescribeOutcome(ResidueCleanupItemResult result) => result.Outcome switch
    {
        ResidueCleanupItemOutcome.Success => LocalizationService.Source("Message.RemovedAfterBackup"),
        ResidueCleanupItemOutcome.BackupFailed => LocalizationService.Source("Message.KeptBackupFailed2"),
        ResidueCleanupItemOutcome.Failed => LocalizationService.Source("Message.ResidueOutcomeFailed", result.Message),
        ResidueCleanupItemOutcome.SkippedInvalid => LocalizationService.Source("Message.ResidueOutcomeBlocked", result.Message),
        _ => LocalizationService.Source("Message.SkippedCanceled"),
    };

    private async Task RecordHistoryAsync(ResidueCleanupReport report)
    {
        try
        {
            var backupReferences = report.Results
                .Select(r => r.RegistryBackupFile ?? r.QuarantineEntryId?.ToString("N"))
                .Where(reference => reference is not null)
                .Cast<string>()
                .ToList();

            var byOutcome = report.Results.GroupBy(r => r.Outcome)
                .Select(group => $"{group.Key}: {group.Count()}")
                .ToList();

            await history.AddAsync(new HistoryRecord
            {
                Timestamp = DateTimeOffset.Now,
                Type = HistoryType.ResidueCleanup,
                Target = report.ProgramName,
                Details = LocalizationService.Source("Message.ResidueHistoryDetails", report.OperationId.ToString("N"), report.Results.Count,
                    backupReferences.Count > 0 ? string.Join(", ", backupReferences) : LocalizationService.Source("Message.ResidueHistoryNoBackups")),
                Status = report.IsFullSuccess
                    ? HistoryStatus.Success
                    : report.WasCancelledByUser && report.FailureCount == 0 ? HistoryStatus.Cancelled : HistoryStatus.Failure,
                Message = (report.IsFullSuccess || report.SuccessCount == 0 ? string.Empty : LocalizationService.Source("Message.ResidueHistoryPartial") + " · ")
                          + (byOutcome.Count > 0 ? string.Join(" · ", byOutcome) : LocalizationService.Source("Message.NoItemsProcessed")),
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Falha de histórico não deve afetar o resultado da limpeza.
            Debug.WriteLine(LocalizationService.Source("Message.CouldNotRecordLeftoverCleanupInHistory0", ex.Message));
        }
    }
}
