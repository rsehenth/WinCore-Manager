using WinCoreManager.Localization;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using WinCoreManager.Helpers;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public sealed class MaintenanceOutcome
{
    public MaintenanceToolDef Tool { get; init; } = null!;

    public MaintenanceStatus Status { get; init; }

    public int? ExitCode { get; init; }

    public long DurationMs { get; init; }

    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Executa ferramentas oficiais de manutenção do Windows a partir de uma
/// allowlist fechada: executáveis de System32 com caminho absoluto e
/// argumentos fixos. A interface nunca fornece comandos ou argumentos —
/// apenas seleciona uma definição já conhecida (e, no CHKDSK, uma letra de
/// unidade validada contra os discos fixos do sistema).
/// </summary>
public sealed class MaintenanceService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHistoryService _history;

    public MaintenanceService(IHistoryService history)
    {
        _history = history;
    }

    public event Action<MaintenanceStatus, string>? StatusChanged;

    public bool IsBusy { get; private set; }

    /// <summary>
    /// Ponto de extensão usado pelos testes automatizados para simular a
    /// execução (nenhum comando real de manutenção é executado nos testes).
    /// Recebe (caminho do executável, argumentos, token) e devolve o resultado.
    /// </summary>
    internal Func<string, string, CancellationToken, Task<ProcessRunResult>>? ExecutorOverride { get; set; }

    public static IReadOnlyList<MaintenanceToolDef> Tools { get; } = BuildCatalog();

    public IReadOnlyList<MaintenanceToolDef> AvailableTools => Tools.Where(t => File.Exists(t.SystemExecutable)).ToList();

    public static IReadOnlyList<string> GetFixedDriveLetters()
    {
        return DriveInfo.GetDrives()
            .Where(IsScanDrive)
            .Select(d => d.Name[0].ToString())
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsScanDrive(DriveInfo drive)
    {
        try
        {
            return drive.DriveType == DriveType.Fixed && drive.IsReady &&
                   drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A unidade pode desaparecer ou ficar indisponível entre a enumeração e a leitura.
            return false;
        }
    }

    public static bool IsValidDriveLetter(string? driveLetter)
    {
        if (string.IsNullOrWhiteSpace(driveLetter) || driveLetter.Length != 1 || !char.IsAsciiLetter(driveLetter[0]))
        {
            return false;
        }

        return GetFixedDriveLetters().Contains(driveLetter.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase);
    }

    public Task<MaintenanceOutcome> RunAsync(
        MaintenanceToolDef tool,
        string? driveLetter,
        Action<string>? onOutputLine,
        CancellationToken cancellationToken) => RunAsync(tool, driveLetter, onOutputLine, cancellationToken, null);

    internal async Task<MaintenanceOutcome> RunAsync(MaintenanceToolDef tool, string? driveLetter,
        Action<string>? onOutputLine, CancellationToken cancellationToken, PackageChangeReservation? reservation)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!Tools.Contains(tool))
        {
            return Rejected(tool, LocalizationService.Source("Message.TheToolIsNotInTheMaintenanceCatalog"));
        }

        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return Busy(tool);
        }

        IsBusy = true;
        try
        {
            return await RunStepAsync(tool, driveLetter, onOutputLine, cancellationToken, reservation).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Reparo completo: DISM RestoreHealth seguido de SFC Scannow, sempre
    /// sequencial e com histórico individual.
    /// </summary>
    public Task<IReadOnlyList<MaintenanceOutcome>> RunFullRepairAsync(
        Action<string>? onOutputLine,
        CancellationToken cancellationToken) => RunFullRepairAsync(onOutputLine, cancellationToken, null);

    internal async Task<IReadOnlyList<MaintenanceOutcome>> RunFullRepairAsync(Action<string>? onOutputLine,
        CancellationToken cancellationToken, PackageChangeReservation? reservation)
    {
        var dism = Tools.First(t => t.Key == "dism_restorehealth");
        var sfc = Tools.First(t => t.Key == "sfc_scannow");
        if (!await _gate.WaitAsync(0).ConfigureAwait(false))
        {
            return [Busy(dism)];
        }

        IsBusy = true;
        try
        {
            var dismOutcome = await RunStepAsync(dism, null, onOutputLine, cancellationToken, reservation).ConfigureAwait(false);
            if (dismOutcome.Status != MaintenanceStatus.Success)
            {
                return [dismOutcome];
            }

            var sfcOutcome = await RunStepAsync(sfc, null, onOutputLine, cancellationToken, reservation).ConfigureAwait(false);
            return [dismOutcome, sfcOutcome];
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }

    private async Task<MaintenanceOutcome> RunStepAsync(
        MaintenanceToolDef tool, string? driveLetter, Action<string>? onOutputLine, CancellationToken cancellationToken,
        PackageChangeReservation? reservation)
    {
        var arguments = tool.Arguments;
        MaintenanceOutcome outcome;
        if (tool.Key == "chkdsk_scan" && !IsValidDriveLetter(driveLetter))
        {
            outcome = Rejected(tool, LocalizationService.Source("Message.InvalidDriveSelectAnAvailableFixedNTFSDrive"));
        }
        else
        {
            if (tool.Key == "chkdsk_scan")
            {
                arguments = $"{driveLetter!.ToUpperInvariant()}: {arguments}";
            }

            ProcessRunResult result;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                StatusChanged?.Invoke(MaintenanceStatus.Running, tool.DisplayName);
                result = await ExecuteAsync(tool, arguments, onOutputLine, cancellationToken, reservation?.Control).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result = new ProcessRunResult { Cancelled = true, StartFailed = true, StartError = LocalizationService.Source("Message.OperationCanceledByTheUser") };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                result = new ProcessRunResult { StartFailed = reservation is null, Interrupted = reservation is not null,
                    HasExitCode = false, StartError = ex.Message };
            }

            // Fixed Windows maintenance commands have a synchronous tool-result contract.
            // A surviving observed descendant or an unverified launch overrides that result.
            outcome = reservation is not null && !reservation.CanAdvance(result, synchronousTool: true)
                ? new MaintenanceOutcome { Tool = tool, Status = MaintenanceStatus.Interrupted,
                    ExitCode = result.HasExitCode && !result.StartFailed ? result.ExitCode : null,
                    DurationMs = result.DurationMs, Message = LocalizationService.Source("UpdateOperation.PendingEvidence") }
                : MapOutcome(tool, result);
        }

        if (!await RecordHistoryAsync(tool, arguments, outcome, driveLetter).ConfigureAwait(false))
        {
            onOutputLine?.Invoke(LocalizationService.Source("Message.CouldNotSaveTheResultToHistory"));
        }

        StatusChanged?.Invoke(outcome.Status, tool.DisplayName);
        return outcome;
    }

    private async Task<ProcessRunResult> ExecuteAsync(
        MaintenanceToolDef tool, string arguments, Action<string>? onOutputLine, CancellationToken cancellationToken,
        WinGetProcessControl? control)
    {
        if (ExecutorOverride is not null)
        {
            return await ExecutorOverride(tool.SystemExecutable, arguments, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(tool.SystemExecutable))
        {
            return new ProcessRunResult
            {
                StartFailed = true,
                StartError = LocalizationService.Source("Message.TheExecutable0WasNotFoundOnThis", tool.SystemExecutable),
            };
        }

        var encoding = OutputEncoding(tool);
        if (tool.RequiresElevation && !IsCurrentProcessElevated())
        {
            var result = await ElevatedMaintenanceRunner.RunAsync(tool.SystemExecutable, arguments, encoding, onOutputLine, cancellationToken, control)
                .ConfigureAwait(false);
            foreach (var line in result.Errors)
            {
                onOutputLine?.Invoke(line);
            }

            return result;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = tool.SystemExecutable,
            WorkingDirectory = Environment.SystemDirectory,
            Arguments = arguments,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };
        return await ProcessRunner.RunObservedAsync(startInfo, onOutputLine, onOutputLine, cancellationToken,
            terminateOnCancellation: false, observation: ProcessObservationPolicy.Maintenance, control: control).ConfigureAwait(false);
    }

    internal static System.Text.Encoding OutputEncoding(MaintenanceToolDef tool) =>
        tool.Key is "sfc_scannow" or "sfc_verifyonly" ? System.Text.Encoding.Unicode : NativeMethods.OemEncoding;

    private static MaintenanceOutcome Busy(MaintenanceToolDef tool) =>
        Rejected(tool, LocalizationService.Source("Message.AMaintenanceOperationIsAlreadyRunningWaitFor"));

    private static MaintenanceOutcome Rejected(MaintenanceToolDef tool, string message) =>
        new() { Tool = tool, Status = MaintenanceStatus.Failed, Message = message };

    private static MaintenanceOutcome MapOutcome(MaintenanceToolDef tool, ProcessRunResult result)
    {
        if (result.StartFailed)
        {
            return new MaintenanceOutcome
            {
                Tool = tool,
                Status = result.Cancelled ? MaintenanceStatus.Cancelled : MaintenanceStatus.Failed,
                ExitCode = null,
                DurationMs = result.DurationMs,
                Message = result.StartError ?? LocalizationService.Source("Message.TheProcessCouldNotBeStarted"),
            };
        }

        if (result.Interrupted || !result.HasExitCode)
            return new MaintenanceOutcome
            {
                Tool = tool,
                Status = MaintenanceStatus.Interrupted,
                DurationMs = result.DurationMs,
                Message = LocalizationService.Source("Message.ExternalProcessObservationEnded"),
            };

        if (result.Cancelled)
        {
            return new MaintenanceOutcome
            {
                Tool = tool,
                Status = MaintenanceStatus.Cancelled,
                ExitCode = result.ExitCode,
                DurationMs = result.DurationMs,
                Message = LocalizationService.Source("Message.OperationCanceledByTheUser"),
            };
        }

        if (result.ExitCode == 0 || (tool.Key == "chkdsk_scan" && result.ExitCode is 1 or 2))
        {
            return new MaintenanceOutcome
            {
                Tool = tool,
                Status = MaintenanceStatus.Success,
                ExitCode = result.ExitCode,
                DurationMs = result.DurationMs,
                Message = result.ExitCode switch
                {
                    1 => LocalizationService.Source("Message.CHKDSKFoundAndRepairedErrors"),
                    2 => LocalizationService.Source("Message.CHKDSKReviewCleanupOutput"),
                    _ => LocalizationService.Source("Message.OperationCompletedSuccessfully"),
                },
            };
        }

        return new MaintenanceOutcome
        {
            Tool = tool,
            Status = MaintenanceStatus.Failed,
            ExitCode = result.ExitCode,
            DurationMs = result.DurationMs,
            Message = LocalizationService.Source("Message.TheToolReturnedCode00x1X", result.ExitCode, unchecked((uint)result.ExitCode)),
        };
    }

    private async Task<bool> RecordHistoryAsync(MaintenanceToolDef tool, string arguments, MaintenanceOutcome outcome, string? driveLetter)
    {
        try
        {
            await _history.AddAsync(new HistoryRecord
            {
                Type = HistoryType.Maintenance,
                Target = tool.DisplayName,
                Details = driveLetter is null ? $"{tool.ExecutableName} {arguments}" :
                    LocalizationService.Source("Message.MaintenanceCommandForDrive", $"{tool.ExecutableName} {arguments}", driveLetter.ToUpperInvariant()),
                Status = outcome.Status switch
                {
                    MaintenanceStatus.Success => HistoryStatus.Success,
                    MaintenanceStatus.Cancelled => HistoryStatus.Cancelled,
                    MaintenanceStatus.Interrupted => HistoryStatus.Interrupted,
                    _ => HistoryStatus.Failure,
                },
                ExitCode = outcome.ExitCode,
                Message = outcome.Message,
                DurationMs = outcome.DurationMs,
            }).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static IReadOnlyList<MaintenanceToolDef> BuildCatalog()
    {
        var systemDirectory = Environment.SystemDirectory;
        string Exe(string name) => Path.Combine(systemDirectory, name);

        return new List<MaintenanceToolDef>
        {
            new(
                "sfc_verifyonly",
                "SFC VerifyOnly",
                Exe("sfc.exe"),
                "/verifyonly",
                true,
                LocalizationService.Source("Message.ChecksTheIntegrityOfAllProtectedSystemFiles"),
                LocalizationService.Source("Message.ReadOnlyTheFullReportIsInWINDIR"),
                LocalizationService.Source("Message.10To30Minutes")),
            new(
                "sfc_scannow",
                "SFC Scannow",
                Exe("sfc.exe"),
                "/scannow",
                true,
                LocalizationService.Source("Message.ChecksForCorruptedSystemFilesAndRepairsThem"),
                LocalizationService.Source("Message.MayReplaceCorruptedSystemFilesWindowsMayAsk"),
                LocalizationService.Source("Message.10To30Minutes")),
            new(
                "dism_checkhealth",
                "DISM CheckHealth",
                Exe("Dism.exe"),
                "/Online /Cleanup-Image /CheckHealth",
                true,
                LocalizationService.Source("Message.QuicklyChecksWhetherTheWindowsComponentStoreHas"),
                LocalizationService.Source("Message.QuickReadOnlyCheck"),
                LocalizationService.Source("Message.AFewSeconds")),
            new(
                "dism_scanhealth",
                "DISM ScanHealth",
                Exe("Dism.exe"),
                "/Online /Cleanup-Image /ScanHealth",
                true,
                LocalizationService.Source("Message.ScansTheWindowsImageForCorruption"),
                LocalizationService.Source("Message.ReadOnlyScanThisCanTakeAWhile"),
                LocalizationService.Source("Message.10To20Minutes")),
            new(
                "dism_restorehealth",
                "DISM RestoreHealth",
                Exe("Dism.exe"),
                "/Online /Cleanup-Image /RestoreHealth",
                true,
                LocalizationService.Source("Message.RepairsTheWindowsComponentStoreDownloadingHealthyComponents"),
                LocalizationService.Source("Message.ReplacesWindowsImageComponentsItMayConnectTo"),
                LocalizationService.Source("Message.15To40Minutes")),
            new(
                "chkdsk_scan",
                "CHKDSK /scan",
                Exe("chkdsk.exe"),
                "/scan",
                true,
                LocalizationService.Source("Message.ChecksTheFileSystemOnAFixedNTFS"),
                LocalizationService.Source("Message.CHKDSKMayFixErrorsDuringTheOnlineScan"),
                LocalizationService.Source("Message.MinutesToHoursDependingOnDriveSize")),
            new(
                "flushdns",
                "Flush DNS",
                Exe("ipconfig.exe"),
                "/flushdns",
                true,
                LocalizationService.Source("Message.ClearsTheWindowsDNSResolverCache"),
                LocalizationService.Source("Message.ClearsTheLocalDNSCacheWithoutRemovingUser"),
                LocalizationService.Source("Message.Seconds")),
        }.AsReadOnly();
    }
}
