/*
 * Adaptado de Bulk Crap Uninstaller (source/UninstallTools/Uninstaller/UninstallManager.cs)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * http://www.apache.org/licenses/LICENSE-2.0
 * Alterações: execução sempre com UseShellExecute=false e sem shell intermediário; cancelamento
 * NUNCA encerra um desinstalador ativo (aguarda a saída natural); mapeamento de códigos MSI;
 * lote estritamente sequencial com resultados preservados e histórico integrado.
 */

using WinCoreManager.Localization;

using System.Diagnostics;
using System.IO;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Executa um plano de desinstalação aprovado pelo usuário.</summary>
public interface IUninstallExecutor
{
    Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine, CancellationToken cancellationToken);
}

/// <summary>Executor real: inicia o desinstalador oficial e aguarda a saída natural.</summary>
public sealed class ProcessUninstallExecutor : IUninstallExecutor
{
    public Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
        ExecuteAsync(plan, onOutputLine, cancellationToken, null);

    internal async Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine,
        CancellationToken cancellationToken, PackageChangeReservation? reservation)
    {
        if (Environment.IsPrivilegedProcess || IsMachineExecutable(plan))
        {
            return new UninstallExecutionResult
            {
                Program = plan.Program,
                Outcome = UninstallOutcome.NotExecutable,
                Message = LocalizationService.Source("Message.Safety.PrivilegedUninstallUnavailable"),
            };
        }

        var policy = ProgramClassifier.Evaluate(plan.Program).Policy;
        var planner = new UninstallPlanner();
        if (!plan.IsExecutable || policy == RemovalPolicy.Blocked
            || (policy == RemovalPolicy.RequiresReview && !plan.ReviewConfirmed)
            || (!Matches(planner.CreatePlan(plan.Program, silentPreferred: false), plan)
                && (!plan.Program.HasQuietUninstall || !Matches(planner.CreatePlan(plan.Program, silentPreferred: true), plan))))
        {
            return new UninstallExecutionResult
            {
                Program = plan.Program,
                Outcome = UninstallOutcome.NotExecutable,
                Message = plan.NotExecutableReason ?? LocalizationService.Source("Message.UninstallIsUnsupportedForThisEntry"),
            };
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            Arguments = plan.Arguments,
            // Nunca interpretado por shell (ProcessRunner também força UseShellExecute=false);
            // o ProcessRunner decide CreateNoWindow e o redirecionamento de saída.
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(plan.ExecutablePath) ?? Environment.SystemDirectory,
        };

        ProcessRunResult result;
        using var directoryPins = new WindowsStartupFileMove.DirectoryPins();
        try
        {
            directoryPins.Pin(startInfo.WorkingDirectory);
            using var executable = WindowsStartupFileMove.OpenVerifiedFileForRead(startInfo.FileName);
            result = await ProcessRunner.RunObservedAsync(startInfo, onOutputLine, onOutputLine, cancellationToken,
                terminateOnCancellation: false, observation: ProcessObservationPolicy.Uninstall, onStarted: () =>
                {
                    executable.Dispose();
                    directoryPins.Dispose();
                }, control: reservation?.Control).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            result = new ProcessRunResult { StartFailed = reservation is null, Interrupted = reservation is not null,
                HasExitCode = false, StartError = ex.Message };
        }

        if (result.StartFailed)
        {
            return new UninstallExecutionResult
            {
                Program = plan.Program,
                Outcome = UninstallOutcome.Failure,
                DurationMs = result.DurationMs,
                Message = result.StartErrorCode == 740
                    ? LocalizationService.Source("Message.Safety.PrivilegedUninstallUnavailable")
                    : LocalizationService.Source("Message.CouldNotStartTheUninstaller0", result.StartError),
            };
        }

        if (result.Interrupted || !result.HasExitCode || reservation is not null && !reservation.CanAdvance(result))
            return new UninstallExecutionResult
            {
                Program = plan.Program,
                Outcome = UninstallOutcome.Interrupted,
                DurationMs = result.DurationMs,
                ExitCode = result.HasExitCode ? result.ExitCode : null,
                Message = LocalizationService.Source("UpdateOperation.PendingEvidence"),
            };

        var (outcome, message) = DescribeExit(result.ExitCode, plan.Method);
        return new UninstallExecutionResult
        {
            Program = plan.Program,
            Outcome = outcome,
            ExitCode = result.ExitCode,
            DurationMs = result.DurationMs,
            Message = message,
        };
    }

    internal static bool IsMachineExecutable(UninstallPlan plan) => plan.Method == UninstallMethod.Executable
        && ResiduePathRules.TryParseRegistryPath(plan.Program.RegistryPath, out var hive, out _)
        && hive == ResidueRegistryHive.LocalMachine;

    private static bool Matches(UninstallPlan validated, UninstallPlan requested) =>
        validated.IsExecutable && validated.Method == requested.Method
        && string.Equals(validated.ExecutablePath, requested.ExecutablePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(validated.Arguments, requested.Arguments, StringComparison.Ordinal);

    private static (UninstallOutcome, string) DescribeExit(int exitCode, UninstallMethod method)
    {
        // Códigos MSI (winerror.h / msi.h) também usados por instaladores em cadeia.
        return exitCode switch
        {
            0 => (UninstallOutcome.Success, LocalizationService.Source("Message.Complete")),
            3010 => (UninstallOutcome.SuccessRebootRequired, LocalizationService.Source("Message.CompleteRestartingWindowsIsRecommended")),
            1641 => (UninstallOutcome.SuccessRebootRequired, LocalizationService.Source("Message.CompleteTheInstallerRequestedARestart")),
            1602 => (UninstallOutcome.Cancelled, LocalizationService.Source("Message.TheUserCanceledTheUninstallInTheInstaller")),
            1605 => (UninstallOutcome.Failure, LocalizationService.Source("Message.WindowsInstallerCouldNotFindTheRegisteredProduct")),
            1618 => (UninstallOutcome.Failure, LocalizationService.Source("Message.AnotherWindowsInstallerOperationIsInProgressTry")),
            1622 => (UninstallOutcome.Failure, LocalizationService.Source("Message.CouldNotOpenTheInstallationLog")),
            1638 => (UninstallOutcome.Failure, LocalizationService.Source("Message.AnotherVersionOfThisProductIsAlreadyInstalled")),
            _ => (UninstallOutcome.Failure, method == UninstallMethod.Msi
                ? LocalizationService.Source("Message.MsiexecReturnedCode0", exitCode)
                : LocalizationService.Source("Message.TheUninstallerReturnedCode0", exitCode)),
        };
    }
}

/// <summary>Executor simulado: não inicia nenhum processo. Para testes e homologação controlada.</summary>
public sealed class SimulatedUninstallExecutor : IUninstallExecutor
{
    public Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        onOutputLine?.Invoke($"[Simulado] {plan.DescribeCommand()}");
        return Task.FromResult(new UninstallExecutionResult
        {
            Program = plan.Program,
            Outcome = UninstallOutcome.Success,
            ExitCode = 0,
            Message = LocalizationService.Source("Message.SimulatedUninstallNoProcessWasStarted"),
        });
    }
}

/// <summary>
/// Desinstalação em lote: estritamente sequencial, erros tratados por programa,
/// resultados anteriores preservados e próximas operações canceladas sob demanda.
/// Nenhum desinstalador ativo é forçado a encerrar.
/// </summary>
public sealed class UninstallBatchRunner(IUninstallPlanner planner, IUninstallExecutor executor, IHistoryService history)
{
    public Task<UninstallBatchReport> RunAsync(
        IReadOnlyList<InstalledProgram> programs,
        Action<string>? onLog = null,
        Action<UninstallExecutionResult>? onItemCompleted = null,
        CancellationToken cancellationToken = default,
        bool reviewConfirmed = false) =>
        RunAsync(programs, onLog, onItemCompleted, cancellationToken, reviewConfirmed, null);

    internal async Task<UninstallBatchReport> RunAsync(IReadOnlyList<InstalledProgram> programs, Action<string>? onLog,
        Action<UninstallExecutionResult>? onItemCompleted, CancellationToken cancellationToken, bool reviewConfirmed,
        PackageChangeReservation? reservation)
    {
        var results = new List<UninstallExecutionResult>(programs.Count);
        var cancelledByUser = false;

        foreach (var program in programs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelledByUser = true;
                break;
            }

            UninstallExecutionResult result;
            try
            {
                // Revalidação da política com o inventário atual — a interface não pode contornar.
                var classification = ProgramClassifier.Evaluate(program);
                if (classification.Policy == RemovalPolicy.Blocked)
                {
                    result = new UninstallExecutionResult
                    {
                        Program = program,
                        Outcome = UninstallOutcome.NotExecutable,
                        Message = LocalizationService.Source("Message.BlockedByProtectionPolicy")
                            + $"({ProgramClassificationLabels.Category(classification.Category)}): "
                            + string.Join(" ", classification.Reasons),
                    };
                }
                else if (classification.Policy == RemovalPolicy.RequiresReview && (programs.Count > 1 || !reviewConfirmed))
                {
                    result = new UninstallExecutionResult
                    {
                        Program = program,
                        Outcome = UninstallOutcome.NotExecutable,
                        Message = programs.Count > 1
                            ? LocalizationService.Source("Message.ProtectedItemThatRequiresReviewUninstallItIndividually")
                            : LocalizationService.Source("Message.ReviewRequired"),
                    };
                }
                else
                {
                    var plan = planner.CreatePlan(program, silentPreferred: false);
                    if (!plan.IsExecutable)
                    {
                        result = new UninstallExecutionResult
                        {
                            Program = program,
                            Outcome = UninstallOutcome.NotExecutable,
                            Message = plan.NotExecutableReason ?? LocalizationService.Source("Message.UninstallIsUnsupportedForThisEntry"),
                        };
                    }
                    else
                    {
                        plan.ReviewConfirmed = classification.Policy == RemovalPolicy.RequiresReview && reviewConfirmed;
                        onLog?.Invoke(LocalizationService.Source("Message.Uninstalling0", program.Name));
                        result = executor is ProcessUninstallExecutor processExecutor
                            ? await processExecutor.ExecuteAsync(plan, onLog, cancellationToken, reservation).ConfigureAwait(true)
                            : await executor.ExecuteAsync(plan, onLog, cancellationToken).ConfigureAwait(true);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelledByUser = true;
                result = new UninstallExecutionResult
                {
                    Program = program,
                    Outcome = UninstallOutcome.Cancelled,
                    Message = LocalizationService.Source("Message.TheUserCanceledBeforeCompletion"),
                };
            }

            results.Add(result);
            onLog?.Invoke($"'{program.Name}': {result.OutcomeLabel}. {result.Message}".TrimEnd());
            onItemCompleted?.Invoke(result);
            await RecordHistoryAsync(result).ConfigureAwait(true);

            if (result.Outcome == UninstallOutcome.Interrupted) break;
            if (result.Outcome is UninstallOutcome.Cancelled)
            {
                cancelledByUser = true;
                break;
            }
        }

        if (cancelledByUser)
        {
            foreach (var remaining in programs.Skip(results.Count))
            {
                var skipped = new UninstallExecutionResult
                {
                    Program = remaining,
                    Outcome = UninstallOutcome.Cancelled,
                    Message = LocalizationService.Source("Message.SkippedBecauseTheUserCanceledTheRemainingOperations"),
                };
                results.Add(skipped);
                onItemCompleted?.Invoke(skipped);
            }
        }

        return new UninstallBatchReport
        {
            Results = results,
            SuccessCount = results.Count(r => r.Outcome is UninstallOutcome.Success or UninstallOutcome.SuccessRebootRequired),
            FailureCount = results.Count(r => r.Outcome == UninstallOutcome.Failure),
            CancelledCount = results.Count(r => r.Outcome == UninstallOutcome.Cancelled),
            NotExecutableCount = results.Count(r => r.Outcome == UninstallOutcome.NotExecutable),
            WasCancelledByUser = cancelledByUser,
        };
    }

    private async Task RecordHistoryAsync(UninstallExecutionResult result)
    {
        try
        {
            await history.AddAsync(new HistoryRecord
            {
                Timestamp = DateTimeOffset.Now,
                Type = HistoryType.Uninstall,
                Target = result.Program.Name,
                Details = $"{result.Program.KindLabel} · {result.Program.Origin}",
                Status = result.Outcome switch
                {
                    UninstallOutcome.Success or UninstallOutcome.SuccessRebootRequired => HistoryStatus.Success,
                    UninstallOutcome.Failure => HistoryStatus.Failure,
                    UninstallOutcome.NotExecutable => HistoryStatus.NotExecutable,
                    UninstallOutcome.Interrupted => HistoryStatus.Interrupted,
                    _ => HistoryStatus.Cancelled,
                },
                ExitCode = result.ExitCode,
                Message = result.Message,
                DurationMs = result.DurationMs,
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Falha de histórico não deve interromper o lote nem afetar o resultado.
            Debug.WriteLine(LocalizationService.Source("Message.CouldNotRecordTheUninstallInHistory0", ex.Message));
        }
    }
}
