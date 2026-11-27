using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class UpdatesPage : UserControl
{
    private UpdatesViewModel? _viewModel;
    private bool _logScrollScheduled;
    private bool _detailsVisible;
    private bool _compact;
    private bool _compactTable;
    private double _controlsShare = 0.5;
    private readonly Thickness _pagePadding;

    public UpdatesPage()
    {
        InitializeComponent();
        _pagePadding = PageGrid.Margin;
        DataContextChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) =>
        {
            UpdateSubscription();
            _viewModel?.LoadCommand.Execute(null);
        };
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) =>
        {
            SummaryCards.Visibility = ActualHeight < 820 || ActualWidth < 720 ? Visibility.Collapsed : Visibility.Visible;
            UpdatePadding();
            FitDetailsRow();
        };
        DetailsArea.SizeChanged += (_, _) => FitUpdateControls();
        UpdatesSplitter.DragCompleted += (_, e) => { if (!e.Canceled) SaveSplit(); };
        UpdatesSplitter.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Handled && e.Key is Key.Up or Key.Down) SaveSplit();
        }), handledEventsToo: true);
        LogSplitter.DragCompleted += (_, e) => { if (!e.Canceled) SaveLogSplit(); };
        LogSplitter.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Handled && e.Key is Key.Up or Key.Down) SaveLogSplit();
        }), handledEventsToo: true);
    }

    private async void ShowAttemptHistory(object sender, RoutedEventArgs args)
    {
        if (_viewModel?.LoadAttemptHistoryCommand.CanExecute(null) != true) return;
        var load = _viewModel.LoadAttemptHistoryCommand.ExecuteAsync(null);
        UpdateDetailsTabs.SelectedItem = AttemptHistoryTab;
        await load;
    }

    private void UpdateSubscription()
    {
        if (ReferenceEquals(_viewModel, DataContext))
        {
            return;
        }

        Detach();
        _viewModel = DataContext as UpdatesViewModel;
        if (_viewModel is not null)
        {
            _viewModel.AttachFilter(CollectionViewSource.GetDefaultView(_viewModel.Rows));
            _viewModel.LogLines.CollectionChanged += OnLogChanged;
            _viewModel.PropertyChanged += OnViewModelChanged;
        }
        ApplySplit();
        FitUpdateControls();
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
            _viewModel.LogLines.CollectionChanged -= OnLogChanged;
            _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = null;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdatesViewModel.ShowUpdateDetails) &&
            _detailsVisible != _viewModel?.ShowUpdateDetails)
            ApplySplit();
        if (e.PropertyName is nameof(UpdatesViewModel.ShowLog) or nameof(UpdatesViewModel.HasUpdateControls)) FitUpdateControls();
        if (e.PropertyName is nameof(UpdatesViewModel.HasUpdateControls) or nameof(UpdatesViewModel.HasInterruptedUpdate))
            FitDetailsRow();
        if (_viewModel is { } updates)
        {
            if (e.PropertyName == nameof(UpdatesViewModel.IsUpdating))
                UpdateDetailsTabs.SelectedItem = !updates.IsUpdating && updates.Results.Count > 0 ? UpdateResultsTab : SessionLogTab;
            if (e.PropertyName == nameof(UpdatesViewModel.PendingBatch) && updates.PendingBatch.Count == 0 &&
                UpdateDetailsTabs.SelectedItem == PendingUpdatesTab)
                UpdateDetailsTabs.SelectedItem = updates.Results.Count > 0 ? UpdateResultsTab : SessionLogTab;
        }
        var announcement = e.PropertyName switch
        {
            nameof(UpdatesViewModel.OperationStatus) => UpdateOperationStatus,
            nameof(UpdatesViewModel.BatchSummary) => UpdateBatchSummary,
            nameof(UpdatesViewModel.ResumeReview) => ResumeReviewStatus,
            nameof(UpdatesViewModel.RecoveryActionStatus) => RecoveryActionText,
            nameof(UpdatesViewModel.RecoveryCompletion) => RecoveryCompletionText,
            _ => null,
        };
        if (announcement is not null && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            Dispatcher.InvokeAsync(() =>
            {
                if (announcement.IsVisible && announcement.Text.Length > 0)
                    UIElementAutomationPeer.CreatePeerForElement(announcement)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }, DispatcherPriority.Background);
    }

    private void ApplySplit()
    {
        _detailsVisible = _viewModel?.ShowUpdateDetails == true;
        var ratio = _viewModel?.ListRatio ?? 0.76;
        ListRow.Height = new(_detailsVisible ? ratio : 1, GridUnitType.Star);
        FitDetailsRow();
        DetailsRow.Height = _detailsVisible ? new(1 - ratio, GridUnitType.Star) : new(0);
    }

    private void FitDetailsRow()
    {
        var compactRecovery = _compact && _viewModel?.HasInterruptedUpdate == true;
        var operationShown = _viewModel?.HasOperation == true;
        ListRow.MinHeight = _compact && (compactRecovery || operationShown) ? 84 : 110;
        DetailsRow.MinHeight = !_detailsVisible ? 0
            : operationShown && !compactRecovery ? Math.Clamp(ActualHeight * 0.43, 146, 250)
            : _viewModel?.HasUpdateControls == true ? Math.Clamp(ActualHeight * 0.29, 116, 200)
            : compactRecovery ? 74 : 116;
    }

    private void SaveSplit()
    {
        if (!_detailsVisible || !ListRow.Height.IsStar || !DetailsRow.Height.IsStar) return;
        _viewModel?.SaveListRatio(ListRow.Height.Value / (ListRow.Height.Value + DetailsRow.Height.Value));
    }

    private void FitUpdateControls()
    {
        var showLog = _viewModel?.ShowLog == true;
        LogSplitter.Visibility = showLog && _viewModel?.HasUpdateControls == true ? Visibility.Visible : Visibility.Collapsed;
        var available = DetailsArea.ActualHeight;
        var reservedForTabs = _compact && _viewModel?.HasOperation == true ? 62 : 96;
        // Keep both the operation controls and the selected detail tab usable in shorter windows.
        UpdateControlsScroll.MaxHeight = showLog
            ? Math.Max(0, Math.Min(available * _controlsShare, available - reservedForTabs - LogSplitter.Height))
            : available;
    }

    private void SaveLogSplit()
    {
        // The splitter sets a pixel height; keep it as a share and return the row to Auto so a short card leaves no gap.
        // Layout has not run yet when the drag completes; the row's new pixel height is the reliable value.
        var height = ControlsRow.Height.IsAbsolute ? ControlsRow.Height.Value : ControlsRow.ActualHeight;
        if (DetailsArea.ActualHeight > 0)
            _controlsShare = Math.Clamp(height / DetailsArea.ActualHeight, 0.1, 0.9);
        ControlsRow.Height = GridLength.Auto;
        FitUpdateControls();
    }

    private void UpdatePadding()
    {
        var compact = ActualWidth < 680 || ActualHeight < 500;
        if (compact != _compact)
        {
            _compact = compact;
            PageGrid.Margin = compact ? new Thickness(16, 12, 16, 12) : _pagePadding;
            PageHeading.FontSize = compact ? 20 : 30;
            Header.Tag = compact ? null : FindResource("Icon.Update");
            PackagesGrid.RowHeight = compact ? 38 : 46;
            FitUpdateControls();
        }

        var compactTable = ActualWidth < 790;
        if (_compactTable == compactTable) return;
        _compactTable = compactTable;
        PackageIdColumn.Visibility = PackageSourceColumn.Visibility = compactTable ? Visibility.Collapsed : Visibility.Visible;
        InstalledColumn.Width = new(compactTable ? 88 : 128);
        AvailableColumn.Width = new(compactTable ? 90 : 104);
        PackageStatusColumn.Width = new(compactTable ? 106 : 124);
    }

    private void HideUpdateFromMenu(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null || sender is not MenuItem { CommandParameter: UpdateItemRow row }) return;
        _viewModel.HideUpdateCommand.Execute(row);
        if (!PackagesGrid.Items.Contains(row))
            Dispatcher.InvokeAsync(() => (PackagesGrid.HasItems ? (Control)PackagesGrid : ManageHiddenButton).Focus(),
                DispatcherPriority.Input);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_logScrollScheduled && e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            var followsTail = LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 20;
            _logScrollScheduled = true;
            Dispatcher.InvokeAsync(() =>
            {
                _logScrollScheduled = false;
                if (IsLoaded && followsTail) LogScroll.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }
}
