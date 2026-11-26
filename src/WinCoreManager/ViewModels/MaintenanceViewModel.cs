using WinCoreManager.Localization;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.ViewModels;

public partial class MaintenanceViewModel : ViewModelBase
{
    private const int MaxLogLines = 600;

    private readonly MaintenanceService _maintenance;
    private readonly SynchronizationContext _ui;
    private CancellationTokenSource? _cts;
    private string? _selectedDrive;

    public MaintenanceViewModel(MaintenanceService maintenance)
    {
        Title = LocalizationService.Source("Message.Maintenance");
        _maintenance = maintenance;
        _ui = new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);

        foreach (var drive in MaintenanceService.GetFixedDriveLetters())
        {
            FixedDrives.Add(drive);
        }

        _selectedDrive = FixedDrives.Contains("C") ? "C" : FixedDrives.FirstOrDefault();
        _maintenance.StatusChanged += OnServiceStatusChanged;
    }

    public Func<MaintenanceConfirmationRequest, Task<bool>>? ConfirmHandler { get; set; }

    public IReadOnlyList<MaintenanceToolDef> Tools => MaintenanceService.Tools;

    public ObservableCollection<string> FixedDrives { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    public bool ShowLog => IsRunning || LogLines.Count > 0;

    public bool IsElevatedSession => MaintenanceService.IsCurrentProcessElevated();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFullRepairCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(ShowLog))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelRequested;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFullRepairCommand))]
    private bool _applicationUpdateRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunToolCommand))]
    [NotifyCanExecuteChangedFor(nameof(RunFullRepairCommand))]
    private bool _applicationUninstallRunning;

    [ObservableProperty]
    private bool _packageConflictRunning;

    [ObservableProperty]
    private string _currentOperation = string.Empty;

    [ObservableProperty]
    private string _statusText = LocalizationService.Source("Message.NoMaintenanceOperationInProgress");

    internal Func<bool>? PackageChangesBlocked { get; set; }
    internal TryReserveOperation? TryReservePackageChange { get; set; }

    private bool TryReserve(bool conflicts, string target, out PackageChangeReservation? reservation)
    {
        reservation = null;
        if (!conflicts || TryReservePackageChange is null || TryReservePackageChange(PackageChangeKind.Maintenance, target, out reservation)) return true;
        StatusText = LocalizationService.Source("UpdateOperation.PackageConflict");
        return false;
    }

    [RelayCommand(CanExecute = nameof(CanRunTool))]
    private async Task RunToolAsync(MaintenanceToolDef? tool)
    {
        if (tool is null || !CanRunTool(tool))
        {
            return;
        }

        var request = BuildRequest(tool);
        if (!await ConfirmAsync(request).ConfigureAwait(true) || !CanRunTool(tool))
        {
            return;
        }

        if (!TryReserve(ConflictsWithPackageChanges(tool), tool.Key, out var reservation)) return;
        using var packageLease = reservation;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;

        IsCancelRequested = false;
        PackageConflictRunning = ConflictsWithPackageChanges(tool);
        IsRunning = true;
        CurrentOperation = LocalizationService.Source("Message.Running0", tool.DisplayName);
        StatusText = LocalizationService.Source("Message.Running0", tool.DisplayName);
        AddLog(LocalizationService.Source("Message.0Started1HHMmSs", tool.DisplayName, DateTime.Now));
        var arguments = tool.Key == "chkdsk_scan" ? $"{_selectedDrive}: {tool.Arguments}" : tool.Arguments;
        AddLog(LocalizationService.Source("Message.MaintenanceCommandLine", tool.ExecutableName, arguments));

        try
        {
            var outcome = await _maintenance.RunAsync(
                tool,
                tool.Key == "chkdsk_scan" ? _selectedDrive : null,
                AddLog,
                cancellation.Token, reservation).ConfigureAwait(true);

            if (outcome.Status != MaintenanceStatus.Interrupted && reservation is not null)
                await reservation.CompleteAsync();
            StatusText = $"{tool.DisplayName}: {outcome.Message}";

            if (outcome.Status == MaintenanceStatus.Cancelled)
            {
                AddLog(LocalizationService.Source("Message.0Canceled", tool.DisplayName));
            }
            else
            {
                AddLog(LocalizationService.Source("Message.0Finished1HHMmSs2", tool.DisplayName, DateTime.Now, outcome.Message));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Source("Message.0OperationCanceled", tool.DisplayName);
            AddLog(LocalizationService.Source("Message.0Canceled", tool.DisplayName));
        }
        catch (Exception ex)
        {
            StatusText = $"{tool.DisplayName}: {ex.Message}";
            AddLog(StatusText);
        }
        finally
        {
            _cts = null;
            CurrentOperation = string.Empty;
            IsRunning = false;
            PackageConflictRunning = false;
        }
    }

    private static bool ConflictsWithPackageChanges(MaintenanceToolDef tool) =>
        tool.Key is not ("sfc_verifyonly" or "dism_checkhealth" or "dism_scanhealth" or "flushdns");

    private bool CanRunTool(MaintenanceToolDef? tool) => tool is not null && !IsRunning && !ApplicationUninstallRunning &&
        (!(ApplicationUpdateRunning || PackageChangesBlocked?.Invoke() == true) || !ConflictsWithPackageChanges(tool));

    private bool CanRun() => !IsRunning && !ApplicationUpdateRunning && PackageChangesBlocked?.Invoke() != true && !ApplicationUninstallRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunFullRepairAsync()
    {
        if (!CanRun())
        {
            return;
        }

        var dism = Tools.First(t => t.Key == "dism_restorehealth");
        var sfc = Tools.First(t => t.Key == "sfc_scannow");

        var request = new MaintenanceConfirmationRequest
        {
            Title = LocalizationService.Source("Message.FullRepair"),
            Description = LocalizationService.Source("Message.RunsDISMRestoreHealthToRepairTheWindowsImage"),
            CommandText = $"{dism.ExecutableName} {dism.Arguments}\n{sfc.ExecutableName} {sfc.Arguments}",
            Effects = LocalizationService.Source("Message.ChangesComponentsInTheWindowsImageAndMay"),
            TypicalDuration = LocalizationService.Source("Message.0DISM1SFC", dism.TypicalDuration, sfc.TypicalDuration),
            RequiresElevation = !IsElevatedSession,
        };

        if (!await ConfirmAsync(request).ConfigureAwait(true) || !CanRun())
        {
            return;
        }

        if (!TryReserve(true, "dism_restorehealth / sfc_scannow", out var reservation)) return;
        using var packageLease = reservation;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        IsCancelRequested = false;

        PackageConflictRunning = IsRunning = true;
        StatusText = LocalizationService.Source("Message.RunningFullRepairDISMRestoreHealthSFCScannow");
        AddLog(LocalizationService.Source("Message.FullRepairStarted0HHMmSs", DateTime.Now));

        try
        {
            var outcomes = await _maintenance.RunFullRepairAsync(AddLog, cancellation.Token, reservation).ConfigureAwait(true);
            foreach (var outcome in outcomes)
            {
                AddLog($"{outcome.Tool.DisplayName}: {outcome.Message}");
            }

            if (outcomes.All(o => o.Status != MaintenanceStatus.Interrupted) && reservation is not null)
                await reservation.CompleteAsync();
            var failed = outcomes.Count(o => o.Status == MaintenanceStatus.Failed);
            var cancelled = outcomes.Any(o => o.Status == MaintenanceStatus.Cancelled);

            StatusText = outcomes.Any(o => o.Status == MaintenanceStatus.Interrupted)
                ? LocalizationService.Source("Message.ExternalProcessObservationEnded")
                : cancelled
                ? LocalizationService.Source("Message.FullRepairCanceled")
                : failed == 0
                    ? LocalizationService.Source("Message.FullRepairCompletedSuccessfullyDISMAndSFC")
                    : outcomes.Count == 1
                        ? LocalizationService.Source("Message.DISMFailedTheSFCStepWasSkipped")
                        : LocalizationService.Source("Message.SFCFailedSeeTheLogForDetails");

            AddLog(LocalizationService.Source("Message.FullRepairFinished0HHMmSs", DateTime.Now));
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationService.Source("Message.FullRepairCanceled");
            AddLog(LocalizationService.Source("Message.FullRepairCanceled2"));
        }
        catch (Exception ex)
        {
            StatusText = LocalizationService.Source("Message.CompleteRepairFailed", ex.Message);
            AddLog(StatusText);
        }
        finally
        {
            _cts = null;
            CurrentOperation = string.Empty;
            IsRunning = false;
            PackageConflictRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelRequested = true;
        _cts?.Cancel();
        StatusText = LocalizationService.Source("Message.CancellationRequestedTheCurrentStepMayNeedTo");
    }

    private bool CanCancel() => IsRunning && !IsCancelRequested;

    private MaintenanceConfirmationRequest BuildRequest(MaintenanceToolDef tool)
    {
        return new MaintenanceConfirmationRequest
        {
            Title = tool.DisplayName,
            Description = tool.Description,
            CommandText = tool.DisplayCommand,
            DriveCommandPreview = tool.Key == "chkdsk_scan"
                ? drive => $"{tool.ExecutableName} {drive?.ToUpperInvariant() ?? "?"}: {tool.Arguments}"
                : null,
            Effects = tool.PossibleEffects,
            TypicalDuration = tool.TypicalDuration,
            RequiresElevation = tool.RequiresElevation && !IsElevatedSession,
            DriveSelection = tool.Key == "chkdsk_scan",
            DriveLetters = FixedDrives,
            SelectedDrive = _selectedDrive,
        };
    }

    private async Task<bool> ConfirmAsync(MaintenanceConfirmationRequest request)
    {
        if (ConfirmHandler is null)
        {
            return false;
        }

        var confirmed = await ConfirmHandler(request).ConfigureAwait(true);

        if (confirmed && request.DriveSelection && !string.IsNullOrWhiteSpace(request.SelectedDrive))
        {
            _selectedDrive = request.SelectedDrive;
        }

        return confirmed;
    }

    private void OnServiceStatusChanged(MaintenanceStatus status, string toolName)
    {
        _ui.Post(_ =>
        {
            CurrentOperation = status == MaintenanceStatus.Running
                ? LocalizationService.Source("Message.Running0", toolName)
                : string.Empty;
        }, null);
    }

    private void AddLog(string line)
    {
        _ui.Post(_ =>
        {
            LogLines.Add(line);
            while (LogLines.Count > MaxLogLines)
            {
                LogLines.RemoveAt(0);
            }

            OnPropertyChanged(nameof(ShowLog));
        }, null);
    }
}
