using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using WinCoreManager.Models;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class MaintenancePage : UserControl
{
    private MaintenanceViewModel? _viewModel;

    public MaintenancePage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
    }

    private void UpdateSubscription()
    {
        if (!IsLoaded || ReferenceEquals(_viewModel, DataContext))
        {
            return;
        }

        Detach();
        _viewModel = DataContext as MaintenanceViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ConfirmHandler = ConfirmAsync;
            _viewModel.LogLines.CollectionChanged += OnLogChanged;
        }
    }

    private Task<bool> ConfirmAsync(MaintenanceConfirmationRequest request) =>
        Task.FromResult(Window.GetWindow(this) is { } owner && MaintenanceConfirmDialog.Show(owner, request));

    private void Detach()
    {
        if (_viewModel is not null)
        {
            if (_viewModel.ConfirmHandler == ConfirmAsync)
            {
                _viewModel.ConfirmHandler = null;
            }

            _viewModel.LogLines.CollectionChanged -= OnLogChanged;
            _viewModel = null;
        }
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Reset)
        {
            LogScroll.ScrollToEnd();
        }
    }
}
