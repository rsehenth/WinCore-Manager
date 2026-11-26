using WinCoreManager.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.ViewModels;

public partial class CleanupRow(CleanupCategory category) : ObservableObject
{
    public CleanupCategory Category { get; } = category;
    public string Name => Category.Name;
    public string Description => Category.Description;
    public string Size => CleanupService.FormatBytes(Category.Bytes);
    public bool Selectable => Category.Selectable;
    public string Availability => $"{Category.Availability} · {Category.Elevation}";
    public string Attention => LocalizationService.Source("Message.Attention0", Category.Attention.ToLowerInvariant());

    [ObservableProperty] private bool _isSelected;
}

public partial class CleanupViewModel : ViewModelBase
{
    private readonly CleanupService _service;
    private readonly IWindowsFeatureConfirmation _confirmation;
    private CleanupSnapshot _snapshot = new(null, [], false, "");

    [ObservableProperty] private IReadOnlyList<CleanupRow> _categories = [];
    [ObservableProperty] private string _volumeText = LocalizationService.Source("Message.WindowsVolume");
    [ObservableProperty] private string _capacityText = LocalizationService.Source("Message.CapacityUsedSpaceAndFreeSpaceWillAppear");
    [ObservableProperty] private string _estimateText = "";
    [ObservableProperty] private double _usedPercent;
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private string _selectionText = LocalizationService.Source("Message.NoCategorySelected");
    [ObservableProperty] private LocalizedMessages _status = new();
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(EmptyCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenNativeCommand))]
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
    private bool CanEmpty() => !IsBusy && Categories.Count(row => row.Selectable && row.IsSelected) == 1;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Message = "";
        Status = new("Message.CheckingTheWindowsVolume");
        try { await LoadAsync(); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanOpenNative))]
    private void OpenNative() => Message = _service.OpenNative(_snapshot.Volume).Message;

    [RelayCommand(CanExecute = nameof(CanEmpty))]
    private async Task EmptyAsync()
    {
        if (!CanEmpty() || _snapshot.Volume is not { } volume) return;
        var row = Categories.Single(item => item.Selectable && item.IsSelected);
        var confirmed = _confirmation.Confirm(LocalizationService.Source("Message.EmptyRecycleBin"),
            LocalizationService.Source("Message.PermanentlyDeleteItemsFromTheRecycleBinOn", volume.Root, row.Size));
        if (!confirmed) return;

        IsBusy = true;
        try
        {
            var result = await _service.EmptyAsync(_snapshot, row.Category, confirmed, _confirmation.Owner);
            await LoadAsync();
            Message = result.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadAsync()
    {
        _snapshot = await _service.ReadAsync();
        foreach (var previous in Categories) previous.PropertyChanged -= OnSelectionChanged;
        Categories = _snapshot.Categories.Select(category => new CleanupRow(category)).ToArray();
        foreach (var row in Categories) row.PropertyChanged += OnSelectionChanged;

        var volume = _snapshot.Volume;
        VolumeText = volume is null ? LocalizationService.Source("Message.WindowsVolumeUnavailable") : LocalizationService.Source("Message.WindowsVolume0", volume.Root);
        CapacityText = volume is null ? "" : LocalizationService.Source("Message.Capacity0Used1Free2", CleanupService.FormatBytes(volume.Capacity), CleanupService.FormatBytes(volume.Used), CleanupService.FormatBytes(volume.Free));
        HasUsage = volume is { Capacity: > 0 };
        UsedPercent = HasUsage ? Math.Clamp(100.0 * volume!.Used / volume.Capacity, 0, 100) : 0;
        EstimateText = _snapshot.KnownCount == 0 ? LocalizationService.Source("Message.SpaceEstimateUnavailable")
            : LocalizationService.Source("Message.PotentialSavings0SizesAvailableFor1Of", CleanupService.FormatBytes(_snapshot.KnownBytes), _snapshot.KnownCount, Categories.Count);
        List<string> warnings = [_snapshot.StatusKey];
        if (volume is not null && !_snapshot.NativeAvailable) warnings.Add("Message.WindowsDiskCleanupIsUnavailable");
        if (volume is not null && !WindowsVolume.IsDriveRoot(volume.Root))
            warnings.Add("Message.CleanupRequiresAWindowsVolumeWithADrive");
        Status = new(warnings.ToArray());
        UpdateSelection();
        OpenNativeCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CleanupRow.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        var selected = Categories.Where(row => row.Selectable && row.IsSelected).ToArray();
        SelectionText = selected.Length == 0 ? LocalizationService.Source("Message.NoCategorySelected")
            : LocalizationService.Source("Message.0CategorySelectedEstimatedSize1", selected.Length, CleanupService.FormatBytes(selected[0].Category.Bytes));
        EmptyCommand.NotifyCanExecuteChanged();
    }
}

