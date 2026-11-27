using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public partial class UpdatesViewModel
{
    private IReadOnlyList<WinGetPackage> _suspendedQueue = [];
    private Guid? _suspendedReceiptId;

    public ObservableCollection<UpdatePackageResult> Results { get; } = [];
    [ObservableProperty] private IReadOnlyList<WinGetPackage> _pendingBatch = [];
    internal Func<IReadOnlyList<WinGetPackage>, bool>? ReviewBatch { get; set; }

    [ObservableProperty] private int _remainingUpdates;
    [ObservableProperty] private string _batchSummary = string.Empty;
    [ObservableProperty] private string _resumeReview = string.Empty;
    [ObservableProperty] private IReadOnlyList<string> _resumeDifferences = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuspendedQueue))]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    [NotifyCanExecuteChangedFor(nameof(ResumeUpdatesCommand))]
    private int _suspendedUpdateCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResumeUpdates))]
    [NotifyPropertyChangedFor(nameof(UpdateSelectedDisabledReason))]
    [NotifyPropertyChangedFor(nameof(RefreshDisabledReason))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResumeUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(UpdateSelectedCommand))]
    private bool _isPreparingResume;

    public bool HasSuspendedQueue => SuspendedUpdateCount > 0;

    public bool ShowResumeUpdates => HasSuspendedQueue && !IsUpdating && !IsCheckingInterruption &&
        !IsPreparingResume && PendingPackageChangeHint is null && !UninstallRunning && !MaintenanceRunning;

    private bool CanResumeUpdates() => ShowResumeUpdates && !IsScanning;

    private void SetSuspendedQueue(IReadOnlyList<WinGetPackage> queue, Guid? receiptId = null)
    {
        _suspendedQueue = queue;
        _suspendedReceiptId = receiptId;
        SuspendedUpdateCount = queue.Count;
    }

    private bool ConfirmBatch(IReadOnlyList<UpdateItemRow> rows)
    {
        if (rows.Count == 0) return false;
        if (ReviewBatch is null || ReviewBatch(rows.Select(row => row.Package).ToArray())) return true;
        StatusText = L("UpdateOperation.ReviewCancelled");
        return false;
    }

    internal static bool ShowBatchReview(IReadOnlyList<WinGetPackage> packages)
    {
        var language = LocalizationService.Current;
        var title = language.Format("UpdateOperation.ReviewTitle", packages.Count);
        var lines = packages.Select(package => language.Format("UpdateOperation.ReviewPackage", package.Name, package.Id,
            package.Source, DisplayVersion(package.InstalledVersion), DisplayVersion(package.AvailableVersion)));
        return LocalizedDialog.Confirm(Application.Current?.MainWindow, title,
            string.Join(Environment.NewLine, lines), "UpdateOperation.ConfirmBatch");

        string DisplayVersion(string version) =>
            PresentationText.Translate(UpdateItemRow.VersionForDisplay(version), language.Culture);
    }

    private void SaveSuspendedQueue(Guid? receiptId, WinGetPackage[] packages)
    {
        if (packages.Length == 0) return;
        Guid? savedId = null;
        if (receiptId is { } id && _interruptionStore is not null)
        {
            try
            {
                if (_interruptionStore.SaveSuspendedQueue(id, packages)) savedId = id;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            if (savedId is null) AppendLog(LocalizationService.Source("UpdateOperation.SuspendedQueueNotSaved"));
        }
        SetSuspendedQueue(packages, savedId);
    }

    private void RestoreSuspendedQueue()
    {
        try
        {
            if (_interruptionStore?.LoadSuspendedQueue() is not { } saved) return;
            SetSuspendedQueue(saved.Packages, saved.ReceiptId);
            PendingBatch = saved.Packages;
            OperationStatus = LocalizationService.Source(HasInterruptedUpdate ? "UpdateOperation.QueueNeedsReview" : "UpdateOperation.QueueReadyToResume");
            HasOperation = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog(LocalizationService.Source("UpdateOperation.SuspendedQueueUnavailable"));
        }
    }

    private bool TryConsumeSuspendedQueue()
    {
        try
        {
            return _suspendedReceiptId is not { } id || _interruptionStore!.ConsumeSuspendedQueue(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    partial void OnHasInterruptedUpdateChanged(bool value)
    {
        if (value) RecoveryFeedback = string.Empty;
        if (!value && OperationStatus == LocalizationService.Source("UpdateOperation.QueueNeedsReview"))
        {
            OperationStatus = LocalizationService.Source(HasSuspendedQueue ? "UpdateOperation.QueueReadyToResume" : "UpdateOperation.QueueReviewResolved");
            OperationResult = string.Empty;
        }
    }

    [RelayCommand(CanExecute = nameof(CanResumeUpdates))]
    private async Task ResumeUpdatesAsync()
    {
        RefreshInterruptionState();
        if (!CanResumeUpdates()) return;

        bool scanned;
        IsPreparingResume = true;
        try
        {
            scanned = await DetectAndScanAsync(preserveSelection: true);
        }
        finally
        {
            IsPreparingResume = false;
        }

        RefreshInterruptionState();
        if (!scanned || !CanResumeUpdates()) return;
        var candidates = Rows.Where(row => row.CanSelect && !IsHidden(row) &&
            !string.Equals(row.InstalledVersion, row.AvailableVersion, StringComparison.OrdinalIgnoreCase))
            .ToLookup(row => (row.Source.ToUpperInvariant(), row.Id.ToUpperInvariant()));
        var remaining = new List<UpdateItemRow>();
        var changes = new List<string>();
        foreach (var previous in _suspendedQueue)
        {
            var matches = candidates[(previous.Source.ToUpperInvariant(), previous.Id.ToUpperInvariant())].ToArray();
            if (matches is not [var current])
            {
                if (_scanIncomplete)
                {
                    ResumeReview = L("UpdateOperation.ResumeScanIncomplete");
                    return;
                }
                changes.Add(LocalizationService.Source("UpdateOperation.ResumeUnavailable", previous.Name, previous.AvailableVersion));
                continue;
            }

            remaining.Add(current);
            if (!string.Equals(previous.AvailableVersion, current.AvailableVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(previous.InstalledVersion, current.InstalledVersion, StringComparison.OrdinalIgnoreCase))
                changes.Add(LocalizationService.Source("UpdateOperation.ResumeVersionChanged", previous.Name,
                    previous.InstalledVersion, previous.AvailableVersion, current.InstalledVersion, current.AvailableVersion));
        }

        if (changes.Count == 0 && (!ConfirmBatch(remaining) || !CanResumeUpdates())) return;
        if (!TryConsumeSuspendedQueue())
        {
            RefreshInterruptionState();
            ResumeReview = LocalizationService.Source("UpdateOperation.SuspendedQueueUnavailable");
            return;
        }
        if (changes.Count == 0)
        {
            await RunBatchAsync(remaining);
            return;
        }

        SearchText = string.Empty;
        var selection = remaining.ToHashSet();
        foreach (var row in Rows) row.IsSelected = selection.Contains(row);
        ResumeReview = LocalizationService.Source("UpdateOperation.ResumeReview");
        ResumeDifferences = changes;
        OperationStatus = string.Empty;
        SetSuspendedQueue([]);
        PendingBatch = [];
    }
}

public sealed record UpdatePackageResult(string Name, string Id, string Status, string Message, string Log,
    string? Installed = null, string? TargetVersion = null, int? ExitCode = null, DateTimeOffset? Timestamp = null,
    bool HistoryExcerpt = false, bool LogTruncated = false);
