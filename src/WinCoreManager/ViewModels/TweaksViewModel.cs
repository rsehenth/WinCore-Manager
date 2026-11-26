using WinCoreManager.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.ViewModels;

public partial class TweakRow : ObservableObject
{
    public TweakReading Reading { get; }
    public string Label => Reading.Label;
    public IReadOnlyList<TweakChoice> Choices => Reading.Choices;
    public string CurrentText => Reading.Choices.FirstOrDefault(choice => choice.Value == Reading.Current)?.Label
        ?? (Reading.Current is null ? LocalizationService.Source("Message.Unavailable") : Reading.Current.Plan?.ToString() ?? $"{Reading.Current.Number} s");
    public string Details => Reading.Restriction is { } restriction ? $"{CurrentText} · {restriction}" : LocalizationService.Source("Message.Current0", CurrentText);
    public bool CanEdit => Reading.Current is not null && Reading.Restriction is null && Choices.Count > 1;
    public bool Changed => Choice is not null && Choice.Value != Reading.Current;

    [ObservableProperty] private TweakChoice? _choice;

    public TweakRow(TweakReading reading)
    {
        Reading = reading;
        _choice = reading.Choices.FirstOrDefault(choice => choice.Value == reading.Current);
    }
}

public partial class TweaksViewModel : ViewModelBase
{
    private readonly TweakService _service;
    private readonly RestorePointService _restore;
    private readonly IWindowsFeatureConfirmation _confirmation;
    private TweakSnapshot? _snapshot;

    [ObservableProperty] private IReadOnlyList<TweakRow> _power = [];
    [ObservableProperty] private IReadOnlyList<TweakRow> _effects = [];
    [ObservableProperty] private IReadOnlyList<TweakRow> _folderOptions = [];
    [ObservableProperty] private LocalizedMessages _status = new();
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _restoreStatus = LocalizationService.Source("Message.WaitingForScan");
    [ObservableProperty] private string _restoreDescription = "";
    [ObservableProperty] private string _undoText = LocalizationService.Source("Message.NoChangeToUndo");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenPerformanceCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProtectionCommand))]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool _isBusy;

    public bool CanInteract => !IsBusy;
    public IReadOnlyList<string> VisualProfiles { get; } =
        [LocalizationService.Source("Message.LetWindowsChoose"), LocalizationService.Source("Message.AdjustForBestAppearance"), LocalizationService.Source("Message.AdjustForBestPerformance"), LocalizationService.Source("Message.Custom")];

    public TweaksViewModel(TweakService service, RestorePointService restore, IWindowsFeatureConfirmation confirmation)
    {
        Title = LocalizationService.Source("Message.Tweaks");
        _service = service;
        _restore = restore;
        _confirmation = confirmation;
    }

    private bool CanRefresh() => !IsBusy;
    private bool CanApply(TweakRow? row) => !IsBusy && row is { CanEdit: true, Changed: true } && Rows().Contains(row);
    private bool CanUndo() => !IsBusy && _snapshot?.Undo is not null;
    private bool CanOpenPerformance() => !IsBusy && _snapshot?.PerformanceAvailable == true;
    private bool CanOpenProtection() => !IsBusy && _snapshot?.ProtectionAvailable == true;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        Status = new("Message.ReadingWindowsSettings");
        RestoreStatus = LocalizationService.Source("Message.CheckingRestorePoints");
        RestoreDescription = "";
        Message = "";
        try
        {
            var restore = _restore.ReadAsync();
            await LoadSettingsAsync();
            ShowRestore(await restore);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync(TweakRow? row)
    {
        if (!CanApply(row) || row?.Choice is not { } choice) return;
        if (!_confirmation.Confirm(LocalizationService.Source("Message.ApplyChange"), LocalizationService.Source("Message.0Current1New2", row.Label, row.CurrentText, choice.Label))) return;

        IsBusy = true;
        try
        {
            var result = await _service.ApplyAsync(row.Reading, choice.Value, confirmed: true);
            await LoadSettingsAsync();
            Message = result.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        if (!CanUndo() || _snapshot?.Undo is not { } undo) return;
        var row = Rows().FirstOrDefault(item => item.Reading.Address == undo.Address);
        var previous = row?.Choices.FirstOrDefault(choice => choice.Value == undo.Before)?.Label
            ?? LocalizationService.Source("Message.PreviousStateSaved");
        if (!_confirmation.Confirm(LocalizationService.Source("Message.UndoChange"), LocalizationService.Source("Message.0Restore1", TweakService.Label(undo.Address.Setting), previous))) return;

        IsBusy = true;
        try
        {
            var result = await _service.UndoAsync(undo, confirmed: true);
            await LoadSettingsAsync();
            Message = result.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanOpenPerformance))]
    private void OpenPerformance() => Message = _service.OpenTool(WindowsTool.Performance).Message;

    [RelayCommand(CanExecute = nameof(CanOpenProtection))]
    private void OpenProtection() => Message = _service.OpenTool(WindowsTool.SystemProtection).Message;

    private async Task LoadSettingsAsync()
    {
        _snapshot = await _service.ReadAsync();
        foreach (var row in Rows()) row.PropertyChanged -= OnChoiceChanged;
        var rows = _snapshot.Settings.Select(reading => new TweakRow(reading)).ToArray();
        Power = rows.Where(row => row.Reading.Address.Setting <= TweakSetting.SleepDc).ToArray();
        Effects = rows.Where(row => row.Reading.Address.Setting is >= TweakSetting.WindowAnimation and <= TweakSetting.Thumbnails).ToArray();
        FolderOptions = rows.Where(row => row.Reading.Address.Setting >= TweakSetting.FileExtensions).ToArray();
        foreach (var row in rows) row.PropertyChanged += OnChoiceChanged;
        List<string> warnings = [_snapshot.StatusKey];
        if (!_snapshot.PerformanceAvailable) warnings.Add("Message.PerformanceOptionsUnavailable");
        if (!_snapshot.ProtectionAvailable) warnings.Add("Message.SystemProtectionUnavailable");
        Status = new(warnings.ToArray());
        UndoText = _snapshot.Undo is { } undo ? TweakService.Label(undo.Address.Setting) : LocalizationService.Source("Message.NoChangeToUndo");
    }

    private IEnumerable<TweakRow> Rows() => Power.Concat(Effects).Concat(FolderOptions);

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TweakRow.Choice)) ApplyCommand.NotifyCanExecuteChanged();
    }

    private void ShowRestore(RestorePointInfo info)
    {
        var protection = LocalizationService.Source("Message.CheckTheProtectionStatusOfEachVolumeIn");
        RestoreStatus = info.State switch
        {
            RestoreQueryState.Found when info.HasInvalidDates => LocalizationService.Source("Message.LatestVerifiedDate0GSomeRestorePoints", info.Created!.Value.ToLocalTime()),
            RestoreQueryState.Found => LocalizationService.Source("Message.LatestRestorePoint0G", info.Created!.Value.ToLocalTime()),
            RestoreQueryState.Empty => LocalizationService.Source("Message.NoRestorePointsFound0", protection),
            RestoreQueryState.Disabled => LocalizationService.Source("Message.SystemRestoreIsDisabledByPolicy"),
            RestoreQueryState.AccessDenied => LocalizationService.Source("Message.YouDoNotHavePermissionToCheckRestore"),
            RestoreQueryState.InvalidDate => LocalizationService.Source("Message.WindowsReturnedRestorePointsWithAnInvalidDate"),
            _ => LocalizationService.Source("Message.CouldNotCheckRestorePoints0", protection),
        };
        RestoreDescription = info.Description ?? "";
    }
}

