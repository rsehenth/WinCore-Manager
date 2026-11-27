using WinCoreManager.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.ViewModels;

public partial class CleanupRow(CleanupCategory category) : ObservableObject
{
    public CleanupCategory Category { get; } = category;
    public string Name => Category.Name;
    public string Group => LocalizationService.Source(Category.Id switch
    {
        "WinCore.Chrome" or "WinCore.Edge" or "WinCore.Brave" or "WinCore.Firefox" => "Cleanup.GroupBrowsers",
        "WinCore.Discord" or "WinCore.Nvidia" or "WinCore.Amd" => "Cleanup.GroupApplications",
        "WinCore.Npm" or "WinCore.Pip" or "WinCore.NuGet" => "Cleanup.GroupDevelopment",
        _ => "Cleanup.GroupWindows",
    });
    public bool NeedsAttention => Category.Bytes is null || Category.Items > 0 && !Selectable;
    public string Description => Category.Description;
    public string Size => CleanupService.FormatBytes(Category.Bytes);
    public string ItemCount => Category.Items switch
    {
        1 => LocalizationService.Source("Cleanup.SingleItem"),
        { } count => LocalizationService.Source("Message.CleanupItemCount", count),
        _ => "",
    };
    public IReadOnlyList<string> PreviewPaths => Category.FileScan?.Preview ?? [];
    public bool HasFilePreview => PreviewPaths.Count > 0;
    public string PreviewCaption => LocalizationService.Source("Cleanup.PreviewCount", PreviewPaths.Count, Category.Items);
    public bool Selectable => Category.Selectable;

    [ObservableProperty] private bool _isSelected;
}

public partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupService _service;
    private readonly IWindowsFeatureConfirmation _confirmation;
    private CleanupSnapshot _snapshot = new(null, [], false, "");

    public IReadOnlyList<CleanupRow> Categories { get; private set; } = [];
    [ObservableProperty] private IReadOnlyList<CleanupRow> _directCategories = [];
    [ObservableProperty] private IReadOnlyList<CleanupRow> _nativeCategories = [];
    [ObservableProperty] private CleanupRow? _recycle;
    [ObservableProperty] private bool _hasRecycle;
    [ObservableProperty] private string _volumeText = LocalizationService.Source("Message.WindowsVolume");
    [ObservableProperty] private string _capacityText = LocalizationService.Source("Message.CapacityUsedSpaceAndFreeSpaceWillAppear");
    [ObservableProperty] private string _estimateText = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private string _selectionText = LocalizationService.Source("Message.CleanupAnalyzeFirst");
    [ObservableProperty] private LocalizedMessages _status = new();
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _resultDetails = "";
    [ObservableProperty] private bool _hasResultDetails;
    [ObservableProperty] private int _progressValue;
    [ObservableProperty] private int _progressMaximum = 1;
    [ObservableProperty] private bool _progressIndeterminate = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyNativeCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenNativeCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviewRecycleBinCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isBusy;

    public bool CanInteract => !IsBusy;

    public CleanupViewModel(CleanupService service, IWindowsFeatureConfirmation confirmation)
    {
        Title = LocalizationService.Source("Message.Cleanup");
        _service = service;
        _confirmation = confirmation;
    }

    private bool CanRefresh() => !IsBusy;
    private bool CanOpenNative() => !IsBusy && _snapshot.NativeAvailable && WindowsVolume.IsDriveRoot(_snapshot.Volume?.Root);
    [RelayCommand]
    private void SetSelection(string action)
    {
        if (IsBusy) return;
        foreach (var row in DirectCategories.Where(row => row.Selectable))
            row.IsSelected = action == "all";
    }

    private bool CanEmpty() => !IsBusy && DirectCategories.Any(row => row.Selectable && row.IsSelected);
    private bool CanEmptyNative() => !IsBusy && Recycle is { Selectable: true, IsSelected: true };
    private bool CanPreviewRecycleBin() => !IsBusy && Recycle?.Category.Items > 0;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken token)
    {
        IsBusy = true;
        Message = "";
        HasResultDetails = false;
        try { await LoadAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { Message = LocalizationService.Source("Message.CleanupStopped"); Status = new(); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanOpenNative))]
    private void OpenNative() => Message = _service.OpenNative(_snapshot.Volume).Message;

    [RelayCommand(CanExecute = nameof(CanPreviewRecycleBin))]
    private void PreviewRecycleBin() => Message = _service.OpenRecycleBin().Message;

    [RelayCommand(CanExecute = nameof(CanEmpty))]
    private async Task EmptyAsync(CancellationToken token)
    {
        if (!CanEmpty()) return;
        var selected = DirectCategories.Where(row => row.Selectable && row.IsSelected).ToArray();
        var loc = LocalizationService.Current;
        var blocks = selected.Select(row =>
            $"{PresentationText.Translate(row.Name, loc.Culture)} · {CleanupService.FormatBytes(row.Category.Bytes)} · " +
            $"{PresentationText.Translate(row.ItemCount, loc.Culture)}{Environment.NewLine}" +
            $"{PresentationText.Translate(row.Description, loc.Culture)}{Environment.NewLine}" +
            $"{loc.Format("Cleanup.PreviewCount", Math.Min(row.PreviewPaths.Count, 4), row.Category.Items)}{Environment.NewLine}" +
            string.Join(Environment.NewLine, row.PreviewPaths.Take(4)));
        var confirmed = _confirmation.Confirm(loc["Message.CleanupConfirmTitle"],
            loc.Format("Message.CleanupConfirmItems", string.Join(Environment.NewLine + Environment.NewLine, blocks)));
        if (!confirmed) { Message = LocalizationService.Source("Message.CleanupCanceled"); return; }

        IsBusy = true;
        Status = new("Cleanup.Deleting");
        Message = "";
        HasResultDetails = false;
        var categories = selected.Select(row => row.Category).ToArray();
        try
        {
            var result = await _service.CleanFilesAsync(_snapshot, categories, confirmed, token,
                TrackProgress(categories.Sum(item => item.FileScan!.Count)));
            Message = result.Message;
            ResultDetails = result.Details;
            HasResultDetails = result.Details.Length != 0;
            SetSnapshot(_snapshot with { Categories = _snapshot.Categories.Where(item => item.FileScan is null).ToArray() });
            await LoadAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Status = new(); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanEmptyNative))]
    private async Task EmptyNativeAsync()
    {
        if (!CanEmptyNative()) return;
        var selection = Recycle!.Category;
        var loc = LocalizationService.Current;
        var confirmed = _confirmation.Confirm(loc["Message.CleanupConfirmTitle"],
            loc.Format("Message.CleanupConfirmItems", $"{PresentationText.Translate(selection.Name, loc.Culture)} · {CleanupService.FormatBytes(selection.Bytes)}"));
        if (!confirmed) { Message = LocalizationService.Source("Message.CleanupCanceled"); return; }

        IsBusy = true;
        HasResultDetails = false;
        ProgressIndeterminate = true;
        try
        {
            Message = (await _service.EmptyAsync(_snapshot, selection, confirmed, _confirmation.Owner)).Message;
            await LoadAsync();
        }
        finally
        {
            IsBusy = false;
            CancelCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCancel() => IsBusy && !EmptyNativeCommand.IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        RefreshCommand.Cancel();
        EmptyCommand.Cancel();
    }

    private async Task LoadAsync(CancellationToken token = default)
    {
        Status = new("Message.CheckingTheWindowsVolume");
        var snapshot = await _service.ReadAsync(token, TrackProgress());
        SetSnapshot(snapshot);
    }

    private IProgress<CleanupProgress> TrackProgress(int total = 0)
    {
        ProgressValue = 0;
        ProgressMaximum = Math.Max(1, total);
        ProgressIndeterminate = total == 0;
        var generation = ++_progressGeneration;
        return new Progress<CleanupProgress>(progress =>
        {
            if (generation != _progressGeneration || !IsBusy) return;
            ProgressIndeterminate = false;
            ProgressMaximum = progress.Total;
            ProgressValue = Math.Max(progress.Completed, ProgressValue);
        });
    }

    private int _progressGeneration;

    private void SetSnapshot(CleanupSnapshot snapshot)
    {
        _snapshot = snapshot;
        foreach (var previous in Categories) previous.PropertyChanged -= OnSelectionChanged;
        Categories = snapshot.Categories.Select(category => new CleanupRow(category)).ToArray();
        DirectCategories = Categories.Where(row => row.Category.FileScan is not null).ToArray();
        Recycle = Categories.SingleOrDefault(row => row.Category.Id.Equals(CleanupService.RecycleBin, StringComparison.OrdinalIgnoreCase));
        HasRecycle = Recycle is not null;
        NativeCategories = Categories.Where(row => row.Category.FileScan is null && row != Recycle).ToArray();
        foreach (var row in Categories) row.PropertyChanged += OnSelectionChanged;

        var volume = snapshot.Volume;
        VolumeText = volume is null ? LocalizationService.Source("Message.WindowsVolumeUnavailable") : LocalizationService.Source("Message.WindowsVolume0", volume.Root);
        CapacityText = volume is null ? "" : LocalizationService.Source("Message.Capacity0Used1Free2", CleanupService.FormatBytes(volume.Capacity), CleanupService.FormatBytes(volume.Used), CleanupService.FormatBytes(volume.Free));
        HasUsage = volume is { Capacity: > 0 };
        UsedPercent = HasUsage ? Math.Clamp(100.0 * volume!.Used / volume.Capacity, 0, 100) : 0;
        var measured = DirectCategories.Count(row => row.Category.Bytes.HasValue);
        var bytes = DirectCategories.Sum(row => row.Category.Bytes.GetValueOrDefault());
        EstimateText = measured == 0 ? LocalizationService.Source("Message.SpaceEstimateUnavailable")
            : LocalizationService.Source("Message.PotentialSavings0SizesAvailableFor1Of", CleanupService.FormatBytes(bytes), measured, DirectCategories.Count);
        List<string> warnings = [snapshot.StatusKey];
        if (volume is not null && !snapshot.NativeAvailable) warnings.Add("Message.WindowsDiskCleanupIsUnavailable");
        if (volume is not null && !WindowsVolume.IsDriveRoot(volume.Root))
            warnings.Add("Message.CleanupRequiresAWindowsVolumeWithADrive");
        Status = new(warnings.ToArray());
        UpdateSelection();
        OpenNativeCommand.NotifyCanExecuteChanged();
        PreviewRecycleBinCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupRow.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = DirectCategories.Where(row => row.Selectable && row.IsSelected).ToArray();
        SelectionText = selected.Length > 0
            ? LocalizationService.Source("Message.0CategorySelectedEstimatedSize1", selected.Length,
                CleanupService.FormatBytes(selected.Sum(row => row.Category.Bytes.GetValueOrDefault())))
            : LocalizationService.Source(DirectCategories.Count == 0 ? "Message.CleanupAnalyzeFirst"
                : DirectCategories.Any(row => row.Selectable) ? "Cleanup.SelectToClean" : "Cleanup.NothingToSelect");
        EmptyCommand.NotifyCanExecuteChanged();
        EmptyNativeCommand.NotifyCanExecuteChanged();
    }
}
