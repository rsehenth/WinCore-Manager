using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
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
            UpdatePadding();
            FitDetailsRow();
        };
        DetailsArea.SizeChanged += (_, _) => FitUpdateControls();
        UpdatesSplitter.DragCompleted += (_, e) => { if (!e.Canceled) SaveSplit(); };
        UpdatesSplitter.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Handled && e.Key is Key.Up or Key.Down) SaveSplit();
        }), handledEventsToo: true);
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
        if (e.PropertyName == nameof(UpdatesViewModel.ShowLog)) FitUpdateControls();
        if (e.PropertyName == nameof(UpdatesViewModel.HasUpdateControls)) FitDetailsRow();
    }

    private void ApplySplit()
    {
        _detailsVisible = _viewModel?.ShowUpdateDetails == true;
        var ratio = _viewModel?.ListRatio ?? 0.76;
        ListRow.Height = new(_detailsVisible ? ratio : 1, GridUnitType.Star);
        FitDetailsRow();
        DetailsRow.Height = _detailsVisible ? new(1 - ratio, GridUnitType.Star) : new(0);
    }

    private void FitDetailsRow() => DetailsRow.MinHeight = !_detailsVisible ? 0
        : _viewModel?.HasUpdateControls == true ? Math.Clamp(ActualHeight * 0.29, 116, 200) : 116;

    private void SaveSplit()
    {
        if (!_detailsVisible || !ListRow.Height.IsStar || !DetailsRow.Height.IsStar) return;
        _viewModel?.SaveListRatio(ListRow.Height.Value / (ListRow.Height.Value + DetailsRow.Height.Value));
    }

    private void FitUpdateControls()
    {
        var logSpace = _viewModel?.ShowLog == true ? 66 : 0;
        UpdateControlsScroll.MaxHeight = Math.Max(0, DetailsArea.ActualHeight - logSpace);
    }

    private void UpdatePadding()
    {
        var compact = ActualWidth < 680 || ActualHeight < 500;
        if (compact == _compact) return;
        _compact = compact;
        PageGrid.Margin = compact ? new Thickness(16, 12, 16, 12) : _pagePadding;
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
            _logScrollScheduled = true;
            Dispatcher.InvokeAsync(() =>
            {
                _logScrollScheduled = false;
                if (IsLoaded) LogScroll.ScrollToEnd();
            }, DispatcherPriority.Background);
        }
    }
}
