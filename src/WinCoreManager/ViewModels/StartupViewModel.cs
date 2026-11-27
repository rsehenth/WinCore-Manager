using WinCoreManager.Localization;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinCoreManager.Services;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.WindowsFeatures;
using WinCoreManager.Views;

namespace WinCoreManager.ViewModels;

public interface IStartupConfirmation
{
    bool Confirm(string name, bool enable);
}

internal sealed class WindowsStartupConfirmation : IStartupConfirmation
{
    public bool Confirm(string name, bool enable)
    {
        var owner = System.Windows.Application.Current.MainWindow;
        var action = enable ? LocalizationService.Source("Message.Enable") : LocalizationService.Source("Message.Disable");
        var description = enable
            ? LocalizationService.Source("Message.TheOriginalSettingWillBeRestoredIfIt")
            : LocalizationService.Source("Message.TheSettingWillBeSavedSoYouCan");
        return LocalizedDialog.Confirm(owner, LocalizationService.Source("Message.Startup"),
            LocalizationService.Source("Message.0StartupFor12TheChangeTakes", action, name, description));
    }
}

public enum StartupFilter { All, Enabled, Disabled, Protected }
public sealed record StartupStatusOption(string Label, StartupFilter Value);
public sealed record StartupSourceOption(string Label, StartupSource? Value);

public sealed record StartupRow(StartupEntry Entry)
{
    public string Name => Entry.Name;
    public string Publisher => Entry.Publisher ?? "—";
    public string Source => Entry.Source.Label();
    public string Status => Entry.Status switch
    {
        StartupStatus.Enabled => LocalizationService.Source("Message.Enabled"),
        StartupStatus.Disabled => Entry.Stored ? LocalizationService.Source("Message.DisabledByWinCore") : LocalizationService.Source("Message.DisabledInWindows"),
        _ => LocalizationService.Source("Message.Unknown2"),
    };
    public bool Enabled => Entry.Status == StartupStatus.Enabled;
    public bool Disabled => Entry.Status == StartupStatus.Disabled;
    public string FilePath => Entry.FilePath ?? LocalizationService.Source("Message.Unknown");
    public string Location => Entry.Location;
    public string Arguments => Entry.Arguments ?? "—";
    public string OriginalCommand => Entry.OriginalCommand is { } command
        ? LocalizationService.Source("Message.OriginalCommand") + (command.Length <= 4096 ? command : command[..4096] + "…") : "";
    public string Details => Entry.Protection is null
        ? Entry.Resolution ?? LocalizationService.Source("Message.ChangesTakeEffectAtTheNextSignIn")
        : Entry.Resolution is null ? Entry.Protection : $"{Entry.Protection} {Entry.Resolution}";
    public bool CanManageInWindows => !Entry.CanChange &&
        (Entry.Protection == LocalizationService.Source("Startup.MachineWriteRequired") ||
         Entry.Protection == LocalizationService.Source("Message.RegistryEntriesAreReadOnly") ||
         Entry.Protection == LocalizationService.Source("Message.StartupEntriesForAllUsersAreReadOnly") ||
         Entry.Protection == LocalizationService.Source("Message.ScheduledTasksAreReadOnly") ||
         Entry.Protection == LocalizationService.Source("Message.WindowsManagesThisSettingOutsideTheApp") ||
         Entry.Protection == LocalizationService.Source("Message.StartupIsDisabledInWindowsOrItsStatus") &&
            Entry.Status == StartupStatus.Disabled && !Entry.Stored);
    public string Action => Entry.CanChange
        ? Enabled ? LocalizationService.Source("Message.Disable") : LocalizationService.Source("Message.Enable")
        : CanManageInWindows ? LocalizationService.Source("Message.StartupManagedInWindows")
        : LocalizationService.Source("Message.Protected");
}

public partial class StartupViewModel : ViewModelBase
{
    private readonly IStartupInventory _inventory;
    private readonly IStartupChanges _changes;
    private readonly IStartupConfirmation _confirmation;
    private readonly IExternalNavigation _external;
    private readonly IWindowsTools? _tools;
    private StartupInventoryResult _current = StartupInventoryResult.Empty;

    public IReadOnlyList<StartupStatusOption> StatusOptions { get; } =
    [
        new(LocalizationService.Source("Message.All"), StartupFilter.All), new(LocalizationService.Source("Message.Enabled2"), StartupFilter.Enabled),
        new(LocalizationService.Source("Message.Disabled2"), StartupFilter.Disabled), new(LocalizationService.Source("Message.Protected2"), StartupFilter.Protected),
    ];

    public IReadOnlyList<StartupSourceOption> SourceOptions { get; } = new[] { new StartupSourceOption(LocalizationService.Source("Message.AllSources"), null) }
        .Concat(Enum.GetValues<StartupSource>().Select(source => new StartupSourceOption(source.Label(), source))).ToArray();

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private StartupStatusOption _selectedStatus;
    [ObservableProperty] private StartupSourceOption _selectedSource;
    [ObservableProperty] private IReadOnlyList<StartupRow> _rows = [];
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private StartupRow? _selectedRow;
    [ObservableProperty] private string _countText = LocalizationService.Source("Message.NoEntriesChecked");
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _warning = "";
    public bool HasSelection => SelectedRow is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyCanExecuteChangedFor(nameof(ChangeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isBusy;

    public StartupViewModel(IStartupInventory inventory, IStartupChanges changes,
        IStartupConfirmation confirmation, IExternalNavigation external, IWindowsTools? tools = null)
    {
        Title = LocalizationService.Source("Message.Startup");
        _inventory = inventory;
        _changes = changes;
        _confirmation = confirmation;
        _external = external;
        _tools = tools;
        _selectedStatus = StatusOptions[0];
        _selectedSource = SourceOptions[0];
    }

    partial void OnSearchTextChanged(string value) => Filter();
    partial void OnSelectedStatusChanged(StartupStatusOption value) => Filter();
    partial void OnSelectedSourceChanged(StartupSourceOption value) => Filter();
    partial void OnSelectedRowChanged(StartupRow? value) => OpenLocationCommand.NotifyCanExecuteChanged();

    private bool CanRefresh() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync(CancellationToken token)
    {
        IsBusy = true;
        Message = LocalizationService.Source("Message.ScanningStartupEntries");
        try
        {
            await ReadAsync(token);
            Message = Rows.Count == 0 ? LocalizationService.Source("Message.NoEntriesMatchTheSearch") : "";
        }
        catch (OperationCanceledException) { Message = LocalizationService.Source("Message.ScanCanceled"); }
        catch (Exception ex) when (StartupErrors.Expected(ex)) { Message = LocalizationService.Source("Message.CouldNotRefreshTheList"); }
        finally { IsBusy = false; }
    }

    private bool CanChange(StartupRow? row) => !IsBusy && row?.Entry.CanChange == true
        && _current.Entries.Contains(row.Entry);

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ChangeAsync(StartupRow? row, CancellationToken token)
    {
        if (row is null || !CanChange(row) || !_confirmation.Confirm(row.Name, !row.Enabled)) return;

        IsBusy = true;
        Message = LocalizationService.Source("Message.VerifyingTheChange");
        try
        {
            var result = await _changes.SetEnabledAsync(row.Entry, !row.Enabled, token);
            Message = result.Message;
            if (result.Status is not (StartupChangeStatus.Cancelled or StartupChangeStatus.Unchanged))
                await ReadAsync(CancellationToken.None);
        }
        catch (OperationCanceledException) { Message = LocalizationService.Source("Message.ChangeCanceled"); }
        catch (Exception ex) when (StartupErrors.Expected(ex))
        { Message = LocalizationService.Source("Message.CouldNotRefreshTheListCheckHistoryAnd"); }
        finally { IsBusy = false; }
    }

    private bool CanCancel() => IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        RefreshCommand.Cancel();
        ChangeCommand.Cancel();
    }

    private bool CanOpenLocation() => SelectedRow?.Entry.FilePath is { } path
        && (SelectedRow.Entry.Resolution is null || path == SelectedRow.Entry.Location);

    [RelayCommand(CanExecute = nameof(CanOpenLocation))]
    private void OpenLocation()
    {
        if (SelectedRow?.Entry.FilePath is { } path && !_external.OpenFileLocation(path))
            Message = LocalizationService.Source("Message.CouldNotOpenTheFileLocationRefreshThe");
    }

    private static WindowsTool NativeTool(StartupRow row) => row.Entry.Source == StartupSource.ScheduledTask
        ? WindowsTool.TaskScheduler : WindowsTool.StartupApps;

    private bool CanOpenNative(StartupRow? row) => row?.CanManageInWindows == true &&
        _tools?.IsAvailable(NativeTool(row)) == true;

    [RelayCommand(CanExecute = nameof(CanOpenNative))]
    private void OpenNative(StartupRow? row)
    {
        if (row is not null && CanOpenNative(row)) Message = _tools!.Open(NativeTool(row)).Message;
    }

    private async Task ReadAsync(CancellationToken token)
    {
        _current = await _inventory.LoadAsync(token);
        Warning = string.Join(" ", _current.Warnings);
        Filter();
        ChangeCommand.NotifyCanExecuteChanged();
    }

    private void Filter()
    {
        var selectedId = SelectedRow?.Entry.Id;
        var search = SearchText.Trim();
        var compare = LocalizationService.Current.Culture.CompareInfo;
        Rows = _current.Entries.Where(entry =>
            (SelectedSource.Value is null || SelectedSource.Value == entry.Source)
            && (SelectedStatus.Value switch
            {
                StartupFilter.Enabled => entry.Status == StartupStatus.Enabled,
                StartupFilter.Disabled => entry.Status == StartupStatus.Disabled,
                StartupFilter.Protected => !entry.CanChange,
                _ => true,
            })
            && (search.Length == 0 || Matches(entry.Name) || Matches(entry.Publisher)
                || Matches(entry.FilePath) || Matches(entry.Location)))
            .Select(entry => new StartupRow(entry)).ToArray();
        SelectedRow = Rows.FirstOrDefault(row => row.Entry.Id == selectedId);
        var total = _current.Entries.Count;
        CountText = total == 1 ? LocalizationService.Source("Message.1EntryFound") : LocalizationService.Source("Message.0EntriesFound", total);
        if (Rows.Count != total) CountText += LocalizationService.Source("Message.0Shown", Rows.Count);

        bool Matches(string? value) => value is not null
            && compare.IndexOf(value, search, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
    }
}
