using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using WinCoreManager.Helpers;
using WinCoreManager.Localization;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Views;

public partial class OptionalWindowsAppsWindow : Window
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<OptionalApp>>> inventory;
    private readonly UninstallersViewModel model;
    private OptionalAppRow[] rows = [];
    private OptionalAppRow[] visibleRows = [];
    private CancellationTokenSource? operation;
    private bool removing;
    private bool loaded;

    internal OptionalWindowsAppsWindow(OptionalAppInventory source, UninstallersViewModel model)
        : this(source.ReadAsync, model) { }

    internal OptionalWindowsAppsWindow(Func<CancellationToken, Task<IReadOnlyList<OptionalApp>>> inventory, UninstallersViewModel model)
    {
        this.inventory = inventory;
        this.model = model;
        InitializeComponent();
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
        Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            Search.Focus();
            await RefreshAsync();
        };
    }

    private static LocalizationService Loc => LocalizationService.Current;

    private async Task RefreshAsync()
    {
        if (operation is not null) return;
        using var scan = new CancellationTokenSource();
        operation = scan;
        Controls.IsEnabled = AppList.IsEnabled = Remove.IsEnabled = false;
        Status.Text = Loc["Optional.Loading"];
        try
        {
            var apps = await inventory(scan.Token);
            scan.Token.ThrowIfCancellationRequested();
            foreach (var row in rows) row.PropertyChanged -= OnSelectionChanged;
            rows = apps.Select(app => new OptionalAppRow(app)).ToArray();
            foreach (var row in rows) row.PropertyChanged += OnSelectionChanged;
            Status.Text = rows.Any(row => row.App.State == OptionalAppState.Unverified) ? Loc["Optional.Partial"] : "";
        }
        catch (OperationCanceledException) when (scan.IsCancellationRequested) { }
        finally
        {
            operation = null;
            Controls.IsEnabled = AppList.IsEnabled = true;
            ApplyFilter();
        }
    }

    private void OnFilter(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (Category is null || ShowAbsent is null || Empty is null) return;
        var search = Search.Text.Trim();
        visibleRows = rows.Where(row => row.App.State != OptionalAppState.NotInstalled || ShowAbsent.IsChecked == true)
            .Where(row => Category.SelectedIndex switch { 1 => row.App.Definition.Suggested, 2 => !row.App.Definition.Suggested, _ => true })
            .Where(row => search.Length == 0 || row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                row.App.Definition.Family.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        var suggested = visibleRows.Where(row => row.App.Definition.Suggested).ToArray();
        var others = visibleRows.Where(row => !row.App.Definition.Suggested).ToArray();
        Suggested.ItemsSource = suggested;
        Others.ItemsSource = others;
        SuggestedTitle.Visibility = suggested.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OthersTitle.Visibility = others.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Empty.Visibility = visibleRows.Length == 0 && operation is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelection();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void OnClear(object sender, RoutedEventArgs e) => SetSelection(null);
    private void OnSelectSuggested(object sender, RoutedEventArgs e) => SetSelection(row => row.App.Definition.CanSuggest);
    private void OnSelectEligible(object sender, RoutedEventArgs e) => SetSelection(_ => true);

    private void SetSelection(Func<OptionalAppRow, bool>? matches)
    {
        if (operation is not null) return;
        var visible = matches is null ? null : visibleRows.ToHashSet();
        foreach (var row in rows)
            row.IsSelected = visible is not null && visible.Contains(row) && row.Eligible && matches!(row);
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var count = rows.Count(row => row.IsSelected && row.Eligible);
        Remove.IsEnabled = operation is null && count > 0;
        Remove.Content = count == 0 ? Loc["Optional.Remove"] : Loc.Format("Optional.RemoveCount", count);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        var selected = rows.Where(row => row.Eligible && row.IsSelected).Select(row => row.App).ToArray();
        if (selected.Length == 0 || operation is not null) return;
        using var cancellation = new CancellationTokenSource();
        operation = cancellation;
        removing = true;
        Controls.IsEnabled = AppList.IsEnabled = CloseButton.IsEnabled = Remove.IsEnabled = false;
        CancelBatch.IsEnabled = true;
        CancelBatch.Visibility = Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Progress.Maximum = selected.Length;
        string? summary = null;
        try
        {
            var report = await model.RemoveOptionalAppsAsync(selected, request => UninstallConfirmDialog.Show(this, request),
                result =>
                {
                    Progress.Value++;
                    Status.Text = $"{result.Program.Name} — {Translate(result.OutcomeLabel)}. {Translate(result.Message)}";
                    if (result.Outcome == UninstallOutcome.Interrupted) summary = Status.Text;
                }, cancellation.Token, name => LocalizedDialog.Confirm(this,
                    Loc["Message.Uninstaller.OfferResidueTitle"], Loc.Format("Message.Uninstaller.OfferResidue", name),
                    "Message.Uninstaller.OfferResidueTitle"));
            if (report is not null && report.Results.All(result => result.Outcome != UninstallOutcome.Interrupted))
                summary = Loc.Format("Optional.Summary", report.SuccessCount, report.FailureCount,
                    report.CancelledCount, report.NotExecutableCount);
            else if (report is null && Progress.Value > 0) summary = Status.Text;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { summary = Loc["Optional.Stop"]; }
        catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        { summary = Loc["Optional.OperationError"]; }
        finally
        {
            operation = null;
            removing = false;
            CancelBatch.Visibility = Progress.Visibility = Visibility.Collapsed;
            Controls.IsEnabled = AppList.IsEnabled = CloseButton.IsEnabled = true;
            await RefreshAsync();
            if (summary is not null) Status.Text = summary;
        }
    }

    private static string Translate(string value) => PresentationText.Translate(value, Loc.Culture);

    private void OnCancelBatch(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        CancelBatch.IsEnabled = false;
        Status.Text = Loc["Optional.Stop"];
    }

    private void OnStoreLink(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: OptionalAppRow row } || row.App.Definition.StorePage is not { } page) return;
        try { Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Win32Exception) { Status.Text = Loc["Optional.LinkError"]; }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (removing) { e.Cancel = true; return; }
        operation?.Cancel();
        base.OnClosing(e);
    }
}

internal sealed partial class OptionalAppRow(OptionalApp app) : ObservableObject
{
    internal OptionalApp App => app;
    public bool Eligible => app.State == OptionalAppState.Available;
    public string Name => app.Definition.DisplayName;
    public string Impact => app.Definition.Impact;
    public string Identity => string.Join(" · ", new[]
    {
        app.Program?.OptionalPackageFullName ?? app.Program?.RegistryPath ?? app.Definition.Family,
        app.Program?.Publisher,
    }.Where(text => !string.IsNullOrEmpty(text)));
    public string Summary => string.Join(" · ", new[]
    {
        app.Program?.DisplayVersion,
        LocalizationService.Current[app.Definition == OptionalWindowsApps.OneDrive ? "Optional.Win32" : "Optional.Appx"],
        LocalizationService.Current[app.Definition.CanSuggest ? "Optional.LowImpact" : "Optional.ReviewImpact"],
        LocalizationService.Current[$"Optional.State.{app.State}"],
    }.Where(text => !string.IsNullOrEmpty(text)));
    public string Method => app.Definition == OptionalWindowsApps.OneDrive
        ? (app.Program is { } program ? TranslateScope(program.InstallationScope) + " · " : "") + LocalizationService.Current["Optional.Win32Method"]
        : LocalizationService.Current["Optional.AppxMethod"];
    public Visibility StoreLinkVisibility => app.Definition.StorePage is null ? Visibility.Collapsed : Visibility.Visible;

    private static string TranslateScope(string value) => PresentationText.Translate(value, LocalizationService.Current.Culture);

    [ObservableProperty]
    private bool _isSelected;
}
