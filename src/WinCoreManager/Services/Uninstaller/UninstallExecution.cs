/*
 * Adaptado de Bulk Crap Uninstaller (source/UninstallTools/Uninstaller/UninstallManager.cs)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * http://www.apache.org/licenses/LICENSE-2.0
 * Alterações: execução direta do executável validado. HKLM usa o verbo runas e o handle retornado
 * pelo Windows, sem interpretador intermediário. Cancelamento
 * NUNCA encerra um desinstalador ativo (aguarda a saída natural); mapeamento de códigos MSI;
 * lote estritamente sequencial com resultados preservados e histórico integrado.
 */

using WinCoreManager.Localization;

using System.Diagnostics;
using System.ComponentModel;
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
    private readonly Func<UninstallPlan, bool> _matchesRegistration;
    private readonly Func<string, bool> _protectedImagePath;
    private readonly Func<ProcessStartInfo, Process?> _startElevated;
    private readonly Func<InstalledProgram, bool?> _entryExists;
    private readonly (string Image, bool IsPrivileged)? _fixture;

    public ProcessUninstallExecutor() : this(MachineUninstallPolicy.MatchesRegistration,
        MachineUninstallPolicy.IsProtectedImagePath, Process.Start, MachineUninstallPolicy.EntryExists) { }

    private ProcessUninstallExecutor(Func<UninstallPlan, bool> matchesRegistration, Func<string, bool> protectedImagePath,
        Func<ProcessStartInfo, Process?> startElevated, Func<InstalledProgram, bool?> entryExists)
    {
        _matchesRegistration = matchesRegistration;
        _protectedImagePath = protectedImagePath;
        _startElevated = startElevated;
        _entryExists = entryExists;
    }

    internal ProcessUninstallExecutor(string fixtureImage, string fixtureArguments, bool isPrivileged,
        Func<UninstallPlan, bool> matchesRegistration, Action<ProcessStartInfo> requestElevation,
        Func<InstalledProgram, bool?> entryExists)
        : this(matchesRegistration, path => path == fixtureImage, request =>
        {
            requestElevation(request);
            return Process.Start(new ProcessStartInfo(fixtureImage, fixtureArguments)
            {
                UseShellExecute = true,
                Verb = "open",
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetDirectoryName(fixtureImage)!,
            });
        }, entryExists)
    {
        _fixture = (fixtureImage, isPrivileged);
    }

    public Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine, CancellationToken cancellationToken) =>
        ExecuteAsync(plan, onOutputLine, cancellationToken, null);

    internal async Task<UninstallExecutionResult> ExecuteAsync(UninstallPlan plan, Action<string>? onOutputLine,
        CancellationToken cancellationToken, PackageChangeReservation? reservation)
    {
        var machine = MachineUninstallPolicy.IsMachine(plan.Program);
        var policy = ProgramClassifier.Evaluate(plan.Program).Policy;
        var planner = new UninstallPlanner();
        if (!plan.IsExecutable || policy == RemovalPolicy.Blocked
            || (policy == RemovalPolicy.RequiresReview && !plan.ReviewConfirmed)
            || (machine && (!plan.PreparedForConsent || !plan.ConsentGranted))
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

        if (GetLaunchRestriction(plan) is { } restriction)
            return Unavailable(plan, restriction);

        if (plan.Method == UninstallMethod.CurrentUserAppx)
        {
            if (!plan.ConsentGranted || reservation is null) return Unavailable(plan, LocalizationService.Source("Message.ReviewRequired"));
            return await ExecuteAppxAsync(plan, onOutputLine, cancellationToken, reservation).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = plan.ExecutablePath,
            Arguments = plan.Arguments,
            UseShellExecute = machine,
            Verb = machine ? "runas" : string.Empty,
            WorkingDirectory = Path.GetDirectoryName(plan.ExecutablePath) ?? Environment.SystemDirectory,
        };

        ProcessRunResult result;
        var launched = false;
        WinGetProcessControl? control = null;
        using var directoryPins = new WindowsStartupFileMove.DirectoryPins();
        try
        {
            directoryPins.Pin(startInfo.WorkingDirectory);
            using var executable = WindowsStartupFileMove.OpenVerifiedFileForRead(startInfo.FileName);
            if (plan.OptionalWin32 && (!plan.ConsentGranted || !OptionalWindowsApps.IsOneDrive(plan.Program)
                || (!machine && !MachineUninstallPolicy.MatchesCurrentUserRegistration(plan))
                || StartupSignature.VerifiedPublisher(startInfo.FileName) != "Microsoft Corporation"))
                return Unavailable(plan, LocalizationService.Source("Optional.Win32Unverified"));
            if (IsMachineExecutable(plan) && (plan.ApprovedImage is null ||
                MachineUninstallPolicy.ReadImage(executable, startInfo.FileName) != plan.ApprovedImage))
                return Unavailable(plan, LocalizationService.Source("Message.Uninstaller.ImageChanged"));

            void OnStarted()
            {
                launched = true;
                executable.Dispose();
                directoryPins.Dispose();
                onOutputLine?.Invoke(LocalizationService.Source("Message.Uninstaller.Running", plan.Program.Name));
            }

            control = reservation?.Control ?? new WinGetProcessControl();
            cancellationToken.ThrowIfCancellationRequested();
            reservation?.MarkUninstallLaunchPending(plan.Program.Name);
            if (machine)
            {
                onOutputLine?.Invoke(LocalizationService.Source("Message.Uninstaller.AwaitingUac", plan.Program.Name));
                result = await LaunchElevatedAsync(startInfo, plan, OnStarted, control, cancellationToken).ConfigureAwait(false);
            }
            else
                result = await ProcessRunner.RunObservedAsync(startInfo, onOutputLine, onOutputLine, cancellationToken,
                    terminateOnCancellation: false, observation: ProcessObservationPolicy.Uninstall,
                    onStarted: OnStarted, control: control).ConfigureAwait(false);
        }
        catch (Exception ex) when (MachineUninstallPolicy.IsValidationError(ex) || ex is InvalidOperationException)
        {
            result = new ProcessRunResult { StartFailed = !launched, Interrupted = launched, HasExitCode = false, StartError = ex.Message };
        }

        if (result.StartFailed)
        {
            var refused = result.StartErrorCode == 1223;
            return new UninstallExecutionResult
            {
                Program = plan.Program,
                Outcome = refused || result.Cancelled ? UninstallOutcome.Cancelled : UninstallOutcome.Failure,
                DurationMs = result.DurationMs,
                Message = result.StartErrorCode == 740
                    ? LocalizationService.Source("Message.Safety.PrivilegedUninstallUnavailable")
                    : refused ? LocalizationService.Source("Message.AdministratorApprovalWasCanceled")
                    : LocalizationService.Source("Message.CouldNotStartTheUninstaller0", result.StartError),
            };
        }

        // Inno and NSIS hand the work to a second-phase copy that can linger (or hang with no window) after it
        // removed the registration. Root exit 0 plus the entry gone is the uninstall's own completion evidence, so a
        // process still open after the settle window is reported instead of holding every package change.
        var lingering = false;
        if (!cancellationToken.IsCancellationRequested && control?.RootIdentity is not null &&
            result is { HasExitCode: true, ExitCode: 0, Observation: { ControlledProcesses: ControlledProcessState.Exited,
                Delegation: DelegatedProcessState.KnownActive } } && !WinGetNative.WindowsInstallerBusy() && EntryRemoved(plan))
        {
            lingering = true;
            result = new ProcessRunResult
            {
                ExitCode = 0, HasExitCode = true, DurationMs = result.DurationMs, Output = result.Output, Errors = result.Errors,
                Observation = new(ControlledProcessState.Exited, DelegatedProcessState.Possible),
            };
            onOutputLine?.Invoke(LocalizationService.Source("Message.Uninstaller.Lingering", plan.Program.Name));
        }

        var outcomePersisted = reservation?.RecordUninstallOutcome(result) ?? true;
        if (!outcomePersisted || result.Interrupted || !result.HasExitCode || !result.Observation.IsConclusive ||
            reservation is not null && !reservation.CanAdvance(result))
            return new UninstallExecutionResult
            {
                Program = plan.Program,
                Outcome = UninstallOutcome.Interrupted,
                DurationMs = result.DurationMs,
                ExitCode = result.HasExitCode ? result.ExitCode : null,
                Message = result.HasExitCode && WingetExitCodes.RequiresReboot(result.ExitCode)
                    ? DescribeExit(result.ExitCode, plan.Method).Item2
                    : LocalizationService.Source("UpdateOperation.PendingEvidence"),
            };

        var (outcome, message) = DescribeExit(result.ExitCode, plan.Method);
        if (outcome == UninstallOutcome.Success)
        {
            bool? stillInstalled;
            try { stillInstalled = _entryExists(plan.Program); }
            catch (Exception ex) when (MachineUninstallPolicy.IsValidationError(ex)) { stillInstalled = null; }
            (outcome, message) = stillInstalled switch
            {
                true => (UninstallOutcome.Failure, LocalizationService.Source("Message.Uninstaller.StillListed", plan.Program.Name)),
                false => (outcome, message),
                null => (UninstallOutcome.Interrupted, LocalizationService.Source("Message.Uninstaller.InventoryUnverified", plan.Program.Name)),
            };
        }
        return new UninstallExecutionResult
        {
            Program = plan.Program,
            Outcome = outcome,
            ExitCode = result.ExitCode,
            DurationMs = result.DurationMs,
            Message = lingering ? $"{message} {LocalizationService.Source("Message.Uninstaller.Lingering", plan.Program.Name)}" : message,
        };
    }

    private static async Task<UninstallExecutionResult> ExecuteAppxAsync(UninstallPlan plan, Action<string>? onOutput,
        CancellationToken token, PackageChangeReservation reservation)
    {
        token.ThrowIfCancellationRequested();
        var start = OfficialAppx.Removal(plan.Program);
        reservation.MarkUninstallLaunchPending(plan.Program.Name);
        var run = await ProcessRunner.RunObservedAsync(start, null, null, token,
            terminateOnCancellation: false, observation: ProcessObservationPolicy.Uninstall,
            onStarted: () => onOutput?.Invoke(LocalizationService.Source("Message.Uninstaller.Running", plan.Program.Name)),
            control: reservation.Control).ConfigureAwait(false);
        var persisted = reservation.RecordUninstallOutcome(run);
        var exit = run.HasExitCode ? run.ExitCode : -1;
        var outcome = run.StartFailed && persisted ? (run.Cancelled ? UninstallOutcome.Cancelled : UninstallOutcome.Failure)
            : !persisted || run.Interrupted || !run.Observation.IsConclusive ||
                !reservation.CanAdvance(run) ? UninstallOutcome.Interrupted
            : exit switch
            {
                0 => UninstallOutcome.Success,
                10 => UninstallOutcome.NotExecutable,
                20 => UninstallOutcome.Failure,
                _ => UninstallOutcome.Interrupted,
            };
        return new()
        {
            Program = plan.Program,
            Outcome = outcome,
            ExitCode = run.HasExitCode && !run.StartFailed ? exit : null,
            DurationMs = run.DurationMs,
            Message = LocalizationService.Source($"Optional.Outcome.{outcome}"),
        };
    }

    private bool EntryRemoved(UninstallPlan plan)
    {
        try { return _entryExists(plan.Program) == false; }
        catch (Exception ex) when (MachineUninstallPolicy.IsValidationError(ex)) { return false; }
    }

    internal string? GetLaunchRestriction(UninstallPlan plan)
    {
        if (_fixture?.IsPrivileged ?? Environment.IsPrivilegedProcess)
            return LocalizationService.Source("Message.Uninstaller.ElevatedSession");
        // The fixture substitutes its image for machine MSI and uses only that image for executable plans.
        if (_fixture is { } fixture &&
            !(plan.Method == UninstallMethod.Executable && plan.ExecutablePath == fixture.Image ||
              plan.Method == UninstallMethod.Msi && MachineUninstallPolicy.IsMachine(plan.Program)))
            return LocalizationService.Source("Message.UninstallIsUnsupportedForThisEntry");
        if (!MachineUninstallPolicy.IsMachine(plan.Program)) return null;
        try
        {
            if (_matchesRegistration(plan) && (!IsMachineExecutable(plan) || _protectedImagePath(plan.ExecutablePath)))
                return null;
        }
        catch (Exception ex) when (MachineUninstallPolicy.IsValidationError(ex)) { }
        return LocalizationService.Source("Message.Uninstaller.UntrustedMachineCommand");
    }

    internal static bool IsMachineExecutable(UninstallPlan plan) => plan.Method == UninstallMethod.Executable
        && MachineUninstallPolicy.IsMachine(plan.Program);

    internal string? PrepareForConsent(UninstallPlan plan)
    {
        if (!MachineUninstallPolicy.IsMachine(plan.Program)) return GetLaunchRestriction(plan);
        if (GetLaunchRestriction(plan) is { } restriction) return restriction;
        try
        {
            if (IsMachineExecutable(plan))
            {
                using var pins = new WindowsStartupFileMove.DirectoryPins();
                pins.Pin(Path.GetDirectoryName(plan.ExecutablePath)!);
                using var executable = WindowsStartupFileMove.OpenVerifiedFileForRead(plan.ExecutablePath);
                plan.ApprovedImage = MachineUninstallPolicy.ReadImage(executable, plan.ExecutablePath);
                if (plan.ApprovedImage is null) return LocalizationService.Source("Message.Uninstaller.UntrustedMachineCommand");
            }
            plan.PreparedForConsent = true;
            return null;
        }
        catch (Exception ex) when (MachineUninstallPolicy.IsValidationError(ex))
        {
            return LocalizationService.Source("Message.Uninstaller.UntrustedMachineCommand");
        }
    }

    private async Task<ProcessRunResult> LaunchElevatedAsync(ProcessStartInfo startInfo, UninstallPlan plan, Action onStarted,
        WinGetProcessControl control, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        Process? started;
        try
        {
            // UAC can keep ShellExecuteEx pending. Keep the pins and reservation until
            // Windows answers, since abandoning this call could allow a late launch.
            started = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                if (!_matchesRegistration(plan) ||
                    IsMachineExecutable(plan) && !_protectedImagePath(startInfo.FileName))
                    throw new IOException(LocalizationService.Source("Message.Uninstaller.UntrustedMachineCommand"));
                token.ThrowIfCancellationRequested();
                return _startElevated(startInfo);
            }).ConfigureAwait(false);
        }
        catch (Win32Exception ex)
        {
            return new ProcessRunResult { StartFailed = true, HasExitCode = false, StartErrorCode = ex.NativeErrorCode,
                StartError = ex.Message, DurationMs = clock.ElapsedMilliseconds };
        }

        if (started is null)
            return new ProcessRunResult { Interrupted = true, HasExitCode = false, DurationMs = clock.ElapsedMilliseconds };
        using var process = started;
        onStarted();
        control.CaptureRoot(process);
        var observed = await ProcessRunner.ObserveExitAsync(process, ProcessObservationPolicy.Uninstall, token,
            control: control, settleInstallerWork: true).ConfigureAwait(false);
        return new ProcessRunResult
        {
            ExitCode = observed.Exited ? process.ExitCode : -1,
            HasExitCode = observed.Exited,
            Interrupted = !observed.Exited || control.RootIdentity is null || observed.DelegateActive ||
                WinGetNative.WindowsInstallerBusy(),
            Observation = new(observed.Exited ? ControlledProcessState.Exited : ControlledProcessState.Active,
                observed.DelegateActive ? DelegatedProcessState.KnownActive : DelegatedProcessState.Possible),
            DurationMs = clock.ElapsedMilliseconds,
        };
    }

    private static UninstallExecutionResult Unavailable(UninstallPlan plan, string reason) => new()
    {
        Program = plan.Program,
        Outcome = UninstallOutcome.NotExecutable,
        Message = reason,
    };

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
    internal string? GetLaunchRestriction(UninstallPlan plan) =>
        executor is ProcessUninstallExecutor process ? process.GetLaunchRestriction(plan) : null;

    internal Task<List<(string Name, string? Reason)>> CheckConsentPlansAsync(IReadOnlyList<UninstallPlan> plans)
    {
        List<(string, string?)> CheckPlans() => plans.Select(plan => (plan.Program.Name, plan.IsExecutable
                ? (executor as ProcessUninstallExecutor)?.PrepareForConsent(plan)
                : plan.NotExecutableReason ?? LocalizationService.Source("Message.Unsupported")))
            .Where(entry => entry.Item2 is not null).ToList();

        return executor is ProcessUninstallExecutor && plans.Any(plan => MachineUninstallPolicy.IsMachine(plan.Program))
            ? Task.Run(CheckPlans) : Task.FromResult(CheckPlans());
    }

    public Task<UninstallBatchReport> RunAsync(
        IReadOnlyList<InstalledProgram> programs,
        Action<string>? onLog = null,
        Action<UninstallExecutionResult>? onItemCompleted = null,
        CancellationToken cancellationToken = default,
        bool reviewConfirmed = false) =>
        RunAsync(programs, onLog, onItemCompleted, cancellationToken, reviewConfirmed, null);

    internal async Task<UninstallBatchReport> RunAsync(IReadOnlyList<InstalledProgram> programs, Action<string>? onLog,
        Action<UninstallExecutionResult>? onItemCompleted, CancellationToken cancellationToken, bool reviewConfirmed,
        PackageChangeReservation? reservation, IReadOnlyList<UninstallPlan>? confirmedPlans = null)
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
                    var plan = confirmedPlans?[results.Count] ?? planner.CreatePlan(program, silentPreferred: false);
                    if (!ReferenceEquals(plan.Program, program) || !plan.IsExecutable)
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
