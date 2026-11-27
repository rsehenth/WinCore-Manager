using Microsoft.Extensions.DependencyInjection;
using WinCoreManager.Services.Uninstaller;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinCoreManager.Models;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class UninstallersPage : UserControl
{
    private UninstallersViewModel? _viewModel;

    public UninstallersPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateSubscription();
        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => Detach();
        SizeChanged += (_, _) => SummaryCards.Visibility = ActualHeight < 800 || ActualWidth < 720
            ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSubscription()
    {
        if (!IsLoaded || ReferenceEquals(_viewModel, DataContext))
        {
            return;
        }

        Detach();
        _viewModel = DataContext as UninstallersViewModel;
        if (_viewModel is not null)
        {
            ApplyCatalogSort(_viewModel.CatalogSort);
            _viewModel.LogLines.CollectionChanged += OnLogChanged;
            _viewModel.ConfirmHandler = RequestConfirmation;
            _viewModel.ShowResidueScanHandler = ShowResidueScan;
            _viewModel.OfferResidueScanHandler = name => Window.GetWindow(this) is { } owner && LocalizedDialog.Confirm(owner,
                Localization.LocalizationService.Source("Message.Uninstaller.OfferResidueTitle"),
                Localization.LocalizationService.Source("Message.Uninstaller.OfferResidue", name), "Message.Uninstaller.OfferResidueTitle");
            _viewModel.LoadCommand.Execute(null);
        }
    }

    private void OnOptionalApps(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { CanManageOptionalApps: true } model && Window.GetWindow(this) is { } owner &&
            App.Services?.GetService<OptionalAppInventory>() is { } inventory)
            new OptionalWindowsAppsWindow(inventory, model) { Owner = owner }.ShowDialog();
    }

    private void OnProgramsSorting(object sender, DataGridSortingEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        e.Handled = true;
        var member = e.Column.SortMemberPath;
        var direction = e.Column.SortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;

        List<SortDescription> sort = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
            ? [.. _viewModel.CatalogSort]
            : [];
        var index = sort.FindIndex(item => item.PropertyName == member);
        if (index < 0)
        {
            sort.Add(new SortDescription(member, direction));
        }
        else
        {
            sort[index] = new SortDescription(member, direction);
        }

        _viewModel.CatalogSort = sort;
        ApplyCatalogSort(sort);
    }

    private void OnProgramsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // A tecla Espaço também funciona quando o foco está na célula, antes de entrar no checkbox.
        if (e.Key == Key.Space && e.KeyboardDevice.Modifiers == ModifierKeys.None &&
            e.OriginalSource is DataGridCell && ProgramsGrid.CurrentColumn == ProgramsGrid.Columns[0] &&
            ProgramsGrid.CurrentItem is UninstallerItemRow { CanSelect: true } row)
        {
            if (!e.IsRepeat)
                row.IsSelected = !row.IsSelected;
            e.Handled = true;
        }
    }

    private void ApplyCatalogSort(IReadOnlyList<SortDescription> columns)
    {
        using (ProgramsGrid.Items.DeferRefresh())
        {
            ProgramsGrid.Items.SortDescriptions.Clear();
            foreach (var sort in GetCatalogSortDescriptions(columns))
            {
                ProgramsGrid.Items.SortDescriptions.Add(sort);
            }
        }

        foreach (var column in ProgramsGrid.Columns)
        {
            var sort = columns.FirstOrDefault(item => item.PropertyName == column.SortMemberPath);
            column.SortDirection = sort.PropertyName is null ? null : sort.Direction;
        }
    }

    internal static IEnumerable<SortDescription> GetCatalogSortDescriptions(IReadOnlyList<SortDescription> columns)
    {
        foreach (var column in columns)
        {
            if (column.PropertyName == nameof(UninstallerItemRow.InstallDate))
            {
                yield return new SortDescription(nameof(UninstallerItemRow.HasInstallDate), ListSortDirection.Descending);
            }

            yield return column;
        }

        if (columns.Any(column => column.PropertyName == nameof(UninstallerItemRow.InstallDate)))
        {
            yield return new SortDescription(nameof(UninstallerItemRow.Name), ListSortDirection.Ascending);
            yield return new SortDescription(
                $"{nameof(UninstallerItemRow.Program)}.{nameof(InstalledProgram.RegistryPath)}", ListSortDirection.Ascending);
            yield return new SortDescription(
                $"{nameof(UninstallerItemRow.Program)}.{nameof(InstalledProgram.Is64Bit)}", ListSortDirection.Descending);
        }
    }

    private Task<bool> RequestConfirmation(Models.UninstallConfirmationRequest request) =>
        Task.FromResult(Window.GetWindow(this) is { } owner && UninstallConfirmDialog.Show(owner, request));

    private Task ShowResidueScan(Models.InstalledProgram program)
    {
        if (Window.GetWindow(this) is not { } owner || _viewModel is null)
        {
            return Task.CompletedTask;
        }

        var allPrograms = _viewModel.Rows.Select(row => row.Program).ToList();
        ResidueScanWindow.Show(owner, program, allPrograms);
        return Task.CompletedTask;
    }

    private void OnManualResidueScanClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.CanOpenManualResidue != true || Window.GetWindow(this) is not { } owner)
        {
            return;
        }

        var program = ManualResidueDialog.Show(owner);
        if (program is not null)
        {
            ShowResidueScan(program);
        }
    }

    private void Detach()
    {
        if (_viewModel is not null)
        {
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
