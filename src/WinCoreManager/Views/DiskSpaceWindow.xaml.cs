using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WinCoreManager.Localization;
using WinCoreManager.Services.DiskSpace;
using WinCoreManager.Services.Hardware;

namespace WinCoreManager.Views;

public partial class DiskSpaceWindow : Window
{
    private const int FilePageSize = 100;
    private const int FolderPageSize = 200;
    private readonly Func<DiskVolume[]> listVolumes;
    private readonly Func<DiskVolume, Action<DiskScanProgress>?, CancellationToken, DiskScanResult> scan;
    private readonly Func<DiskVolume, DiskDirectory, DiskFile?, bool> reveal;
    private readonly Action<string> copy;
    private CancellationTokenSource? scanCancellation;
    private DiskAnalysisOptions? options;
    private DiskAnalysisResult? analysis;
    private Func<DiskAnalysisOptions, Action<DiskAnalysisProgress>, CancellationToken, DiskAnalysisResult>? analyze;
    private DiskVolume? duplicateVolume;
    private int duplicateLimit = FilePageSize;
    private string folderSort = "Bytes";
    private ListSortDirection folderDirection = ListSortDirection.Descending;
    private DiskScanResult? result;
    private DiskDirectory? currentFolder;
    private FolderBranch? treeRoot;
    private FolderBranch? treeSelection;
    private object? selection;
    private DiskMapItem[] mapItems = [];
    private bool ready, loading, hasStarted, updatingSelection, closeRequested, closed;
    private int fileLimit = FilePageSize;
    private int folderLimit = FolderPageSize;
    private string fileSort = "LogicalBytes";
    private ListSortDirection fileDirection = ListSortDirection.Descending;

    public DiskSpaceWindow() : this(new DiskVolumeCatalog().List, new DiskSpaceScanner().Scan,
        DiskExplorer.Show, Clipboard.SetText)
    {
    }

    internal DiskSpaceWindow(Func<DiskVolume[]> listVolumes,
        Func<DiskVolume, Action<DiskScanProgress>?, CancellationToken, DiskScanResult> scan,
        Func<DiskVolume, DiskDirectory, DiskFile?, bool>? reveal = null, Action<string>? copy = null)
    {
        this.listVolumes = listVolumes;
        this.scan = scan;
        this.reveal = reveal ?? DiskExplorer.Show;
        this.copy = copy ?? Clipboard.SetText;

        InitializeComponent();
        ready = true;
        Closed += (_, _) =>
        {
            closed = true;
            scanCancellation?.Cancel();
        };
        UpdateActions();
    }

    internal DiskSpaceWindow(DiskAnalysisOptions options,
        Func<DiskAnalysisOptions, Action<DiskAnalysisProgress>, CancellationToken, DiskAnalysisResult>? analyze = null)
        : this(new DiskVolumeCatalog().List, new DiskSpaceScanner().Scan)
    {
        this.options = options;
        this.analyze = analyze ?? new DiskAnalysis().Scan;
        StartButton.Content = T("ConfigureAgain");
        RefreshButton.Content = T("Rerun");
        FolderRankingControls.Visibility = Visibility.Visible;
        AnalysisSummary.Visibility = Visibility.Visible;
        UpdateActions();
    }

    private static LocalizationService Text => LocalizationService.Current;
    private static string T(string key) => Text["DiskSpace." + key];
    private static string F(string key, params object?[] values) => Text.Format("DiskSpace." + key, values);
    private static string Size(long? bytes) => bytes is { } count ? HardwareReading.FormatBytes((ulong)count) : T("Unknown");
    private static string Time(TimeSpan elapsed) => elapsed.ToString(@"hh\:mm\:ss", Text.Culture);
    private DiskVolume? SelectedVolume => VolumePicker.SelectedItem as DiskVolume;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, Math.Min(Width, work.Width - 24));
        Height = Math.Max(MinHeight, Math.Min(Height, work.Height - 32));
        PositionWithin(work);
        if (options is not null)
        {
            await RunConfiguredAsync();
            return;
        }
        await LoadVolumesAsync(null);
        if (!closed)
            VolumePicker.Focus();
    }

    private void PositionWithin(Rect work)
    {
        if (!double.IsNaN(Left))
            Left = Math.Max(work.Left, Math.Min(Left, work.Right - Width));
        if (!double.IsNaN(Top))
            Top = Math.Max(work.Top, Math.Min(Top, work.Bottom - Height));
    }

    private async Task<bool> LoadVolumesAsync(DiskVolume? previous)
    {
        loading = true;
        StatusText.Text = T("Loading");
        UpdateActions();
        try
        {
            var volumes = await Task.Run(listVolumes);
            if (closed)
                return false;

            var retained = volumes.FirstOrDefault(volume => volume.Root == previous?.Root
                && volume.VolumePath == previous.VolumePath);
            VolumePicker.ItemsSource = volumes;
            VolumePicker.SelectedItem = retained ?? DiskVolumeCatalog.Preferred(volumes, Environment.SystemDirectory);
            StatusText.Text = volumes.Length == 0 ? T("NoDrives") : previous is not null && retained is null
                ? T("Unavailable") : T("Ready");
            EmptyText.Text = StatusText.Text;
            return retained is not null;
        }
        catch (IOException)
        {
            if (!closed)
            {
                VolumePicker.ItemsSource = null;
                StatusText.Text = T("NoDrives");
                EmptyText.Text = StatusText.Text;
            }
            return false;
        }
        finally
        {
            loading = false;
            if (!closed)
                UpdateActions();
        }
    }

    private void OnVolumeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready)
            return;

        var volume = SelectedVolume;
        if (options is not null)
        {
            if (!loading && scanCancellation is null && analysis?.Volumes.FirstOrDefault(item => item.Volume == volume) is { } item)
                Present(item);
            return;
        }
        DriveCapacityText.Text = volume is null ? "" :
            $"{T("Capacity")} {Size(volume.TotalBytes)}  ·  {T("Used")} {Size(volume.UsedBytes)}  ·  {T("Free")} {Size(volume.FreeBytes)}";
        if (!loading)
        {
            hasStarted = false;
            ClearResult();
            StatusText.Text = T("Ready");
        }
        UpdateActions();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (options is null)
        {
            await StartAsync();
            return;
        }

        var dialog = new DiskAnalysisDialog(listVolumes, options) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Options is { } selected)
        {
            options = selected;
            await RunConfiguredAsync();
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (scanCancellation is not null || loading)
            return;

        if (options is not null)
        {
            await RunConfiguredAsync();
            return;
        }

        var previous = SelectedVolume;
        var restart = hasStarted;
        ClearResult();
        var retained = await LoadVolumesAsync(previous);
        if (restart && retained && !closed)
            await StartAsync();
        else
            hasStarted = false;
    }

    private async Task StartAsync()
    {
        if (loading || scanCancellation is not null || SelectedVolume is not { } volume)
            return;

        hasStarted = true;
        ClearResult();
        var cancel = new CancellationTokenSource();
        scanCancellation = cancel;
        var progress = new Progress<DiskScanProgress>(state =>
        {
            if (scanCancellation == cancel && !cancel.IsCancellationRequested && !closed)
                StatusText.Text = F("Scanning", volume.Root, state.Files, state.Directories, Size(state.LogicalBytes));
        });
        StatusText.Text = F("Scanning", volume.Root, 0, 0, Size(0));
        EmptyText.Visibility = Visibility.Collapsed;
        UpdateActions();
        CancelButton.Focus();

        try
        {
            var completed = await Task.Run(() => scan(volume, ((IProgress<DiskScanProgress>)progress).Report, cancel.Token));
            if (closed)
                return;

            cancel.Token.ThrowIfCancellationRequested();
            Present(completed);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!closed)
                StatusText.Text = EmptyText.Text = T("Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!closed)
                StatusText.Text = EmptyText.Text = T("Unavailable");
        }
        finally
        {
            scanCancellation = null;
            cancel.Dispose();
            if (!closed)
            {
                UpdateActions();
                if (closeRequested)
                    Close();
                else if (result is null)
                    StartButton.Focus();
                else
                    ((TabItem)ResultTabs.Items[0]).Focus();
            }
        }
    }

    private async Task RunConfiguredAsync()
    {
        if (options is not { } request || analyze is null || scanCancellation is not null)
            return;

        analysis = null;
        VolumePicker.ItemsSource = null;
        ClearResult();
        DuplicatesTab.Visibility = Visibility.Collapsed;
        DuplicatesGrid.ItemsSource = null;
        PreviewFiles.ItemsSource = PreviewFolders.ItemsSource = null;
        PreviewFilesEmpty.Visibility = PreviewFoldersEmpty.Visibility = Visibility.Visible;
        AnalysisSummary.Text = T(request.Accelerated ? "Fast" : "Full");
        using var cancel = new CancellationTokenSource();
        scanCancellation = cancel;
        StatusText.Text = T("Loading");
        UpdateActions();
        CancelButton.Focus();
        var progress = new Progress<DiskAnalysisProgress>(state =>
        {
            if (scanCancellation != cancel || cancel.IsCancellationRequested || closed)
                return;
            var speed = (long)(state.Bytes / Math.Max(1, state.Elapsed.TotalSeconds));
            var duration = Time(state.Elapsed);
            StatusText.Text = state.HashTotal is { } total
                ? F("HashProgress", state.HashChecked, total, duration)
                : F("RichProgress", state.Target.Folder ?? state.Target.Volume.Root, state.TargetNumber, state.TargetCount,
                    state.Files, state.Folders, Size(state.Bytes), duration, Size(speed));
            SetPreview(PreviewFiles, PreviewFilesEmpty, state.PreviewFiles);
            SetPreview(PreviewFolders, PreviewFoldersEmpty, state.PreviewFolders);
        });

        try
        {
            analysis = await Task.Run(() => analyze(request, ((IProgress<DiskAnalysisProgress>)progress).Report, cancel.Token));
            cancel.Token.ThrowIfCancellationRequested();
            if (closed)
                return;

            if (analysis.Volumes.Count == 0)
            {
                StatusText.Text = EmptyText.Text = T("Unavailable");
                return;
            }

            VolumePicker.ItemsSource = analysis.Volumes.Select(item => item.Volume).ToArray();
            VolumePicker.SelectedIndex = 0;
            duplicateLimit = FilePageSize;
            if (analysis.Duplicates is not null)
            {
                DuplicatesTab.Visibility = Visibility.Visible;
                PresentDuplicates();
            }
            Present(analysis.Volumes[0]);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!closed)
                StatusText.Text = EmptyText.Text = T("Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!closed)
                StatusText.Text = EmptyText.Text = T("Unavailable");
        }
        finally
        {
            scanCancellation = null;
            if (!closed)
            {
                UpdateActions();
                if (closeRequested)
                    Close();
                else if (result is null)
                    StartButton.Focus();
                else
                    ((TabItem)ResultTabs.Items[0]).Focus();
            }
        }
    }

    private static void SetPreview(ItemsControl control, TextBlock empty, IReadOnlyList<DiskPreviewEntry> entries)
    {
        control.ItemsSource = entries.Select(entry => new PreviewRow(entry.Path, Size(entry.Bytes))).ToArray();
        empty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record PreviewRow(string Path, string Size);

    private void ClearResult()
    {
        result = null;
        currentFolder = null;
        selection = null;
        treeSelection = treeRoot = null;
        FolderTree.ItemsSource = null;
        FolderGrid.ItemsSource = null;
        FileGrid.ItemsSource = null;
        SpaceMap.Children.Clear();
        mapItems = [];
        EmptyText.Text = T("Ready");
        UpdateActions();
    }

    private void Present(DiskScanResult completed)
    {
        result = completed;
        var p = completed.Progress;
        var volume = completed.Volume;
        if (analysis is { } batch)
        {
            var scope = batch.Volumes.Count == 1 ? volume.Display : F("AnalyzedVolumes", batch.Volumes.Count);
            AnalysisSummary.Text = F("AnalysisSummary", T(batch.Accelerated ? "Fast" : "Full"), scope,
                Size(batch.Volumes.Sum(item => item.Progress.LogicalBytes)), Time(batch.Duration));
            if (batch.Unavailable.Count > 0)
                AnalysisSummary.Text += " " + F("SkippedDrives", string.Join(", ", batch.Unavailable.Select(item => item.Folder ?? item.Volume.Root)));
            VolumeCoverageText.Text = string.Equals(completed.Root.Path, volume.Root, StringComparison.OrdinalIgnoreCase)
                && volume.UsedBytes > 0 && p.Files > p.MissingAllocation
                ? F("EstimatedCoverage", Math.Min(1, (double)p.AllocatedBytes / volume.UsedBytes.Value)) : "";
            if (p.OmittedFolders > 0)
                VolumeCoverageText.Text += " " + F("FastCoverage", p.OmittedFolders);
        }
        TotalText.Text = Size(volume.TotalBytes);
        UsedText.Text = Size(volume.UsedBytes);
        FreeText.Text = Size(volume.FreeBytes);
        VolumeBar.Value = volume.TotalBytes > 0 && volume.UsedBytes is { } used ? (double)used / volume.TotalBytes.Value * 100 : 0;
        VolumeBar.Visibility = volume.UsedBytes is not null ? Visibility.Visible : Visibility.Collapsed;
        LogicalText.Text = $"{T("Logical")}  {Size(p.LogicalBytes)}";
        AllocatedText.Text = $"{T("Allocated")}  {Size(p.MissingAllocation == p.Files && p.Files != 0 ? null : p.AllocatedBytes)}";
        CountsText.Text = F("Counts", p.Files, p.Directories, p.Inaccessible);
        CoverageText.Text = F("Coverage", p.Inaccessible, p.SkippedReparse, p.MissingAllocation, p.MissingIdentity);
        CoverageText.Visibility = p.Partial ? Visibility.Visible : Visibility.Collapsed;
        MeasurementText.Text = F("Measurement", p.DuplicateLinks);
        StatusText.Text = F(p.Partial ? "FinishedPartial" : "Finished", Time(p.Elapsed));

        NameFilter.Text = ExtensionFilter.Text = FolderFilter.Text = "";
        AllRankedFolders.IsChecked = options is not null;
        folderSort = "Bytes";
        folderDirection = ListSortDirection.Descending;
        fileLimit = FilePageSize;
        fileSort = "LogicalBytes";
        fileDirection = ListSortDirection.Descending;
        treeRoot = new FolderBranch(completed.Root);
        FolderTree.ItemsSource = new[] { treeRoot };
        treeRoot.IsExpanded = true;
        ResultTabs.SelectedIndex = 0;
        Navigate(completed.Root);
        ShowFiles();
    }

    private void UpdateActions()
    {
        var busy = scanCancellation is not null;
        StartButton.IsEnabled = !busy && !loading && (options is not null || SelectedVolume is not null);
        AnalysisPreview.Visibility = busy && options is not null && result is null ? Visibility.Visible : Visibility.Collapsed;
        RefreshButton.IsEnabled = !busy && !loading;
        VolumePicker.IsEnabled = !busy && !loading && (options is null || analysis?.Volumes.Count > 1);
        var showPicker = options is null || (result is not null && analysis?.Volumes.Count > 1);
        VolumeLabel.Visibility = VolumePicker.Visibility = showPicker ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = scanCancellation is { IsCancellationRequested: false };
        ScanProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DriveCapacityText.Visibility = options is null && result is null && SelectedVolume is not null ? Visibility.Visible : Visibility.Collapsed;
        ResultArea.Visibility = result is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Visibility = result is null && !busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Cancel()
    {
        if (scanCancellation is not { IsCancellationRequested: false } cancel)
            return;

        StatusText.Text = T("Cancelling");
        cancel.Cancel();
        UpdateActions();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (scanCancellation is null)
            return;

        e.Cancel = true;
        closeRequested = true;
        Cancel();
    }

    private void Navigate(DiskDirectory folder)
    {
        currentFolder = folder;
        CurrentPath.Text = folder.Path;
        UpButton.IsEnabled = folder.Parent is not null;
        folderLimit = FolderPageSize;
        UpdateFolderRows();
        SelectTreeBranch(folder);
        BuildMap(folder);
        Select(folder);
    }

    private void SelectTreeBranch(DiskDirectory folder)
    {
        if (treeRoot is null)
            return;

        var route = new Stack<DiskDirectory>();
        for (var part = folder; part.Parent is not null; part = part.Parent)
            route.Push(part);

        var branch = treeRoot;
        while (route.TryPop(out var part))
        {
            branch.IsExpanded = true;
            branch = branch.Reveal(part);
        }

        if (branch == treeSelection)
            return;

        updatingSelection = true;
        if (treeSelection is not null)
            treeSelection.IsSelected = false;
        treeSelection = branch;
        branch.IsSelected = true;
        updatingSelection = false;
    }

    private void UpdateFolderRows()
    {
        if (currentFolder is not { } folder || result is not { } completed)
            return;

        var global = AllRankedFolders.IsChecked == true;
        var query = FolderFilter.Text.Trim();
        var matches = (global ? completed.LargestFolders : folder.Children)
            .Where(node => query.Length == 0 || node.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        var sign = folderDirection == ListSortDirection.Ascending ? 1 : -1;
        var sorted = matches.ToArray();
        if (folderSort != "Bytes" || folderDirection != ListSortDirection.Descending)
        {
            Array.Sort(sorted, (left, right) =>
            {
                var compared = folderSort switch
                {
                    "Name" => StringComparer.CurrentCultureIgnoreCase.Compare(global ? left.Path : left.Name, global ? right.Path : right.Name),
                    "Count" => left.FileCount.CompareTo(right.FileCount),
                    _ => left.LogicalBytes.CompareTo(right.LogicalBytes)
                };
                return compared == 0 ? StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name) : sign * Math.Sign(compared);
            });
        }
        var rows = sorted.Take(folderLimit).Select(child => new FolderRow(child, completed.Root.LogicalBytes, global)).ToArray();
        updatingSelection = true;
        FolderGrid.ItemsSource = rows;
        FolderGrid.SelectedItem = rows.FirstOrDefault(row => row.Node == selection as DiskDirectory);
        updatingSelection = false;
        EmptyFolders.Text = T(folder.Omitted ? "FolderOmitted" : folder.Inaccessible ? "Denied" : "NoFolders");
        EmptyFolders.Visibility = sorted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        MoreFoldersButton.Visibility = sorted.Length > folderLimit ? Visibility.Visible : Visibility.Collapsed;
        foreach (var column in FolderGrid.Columns)
            column.SortDirection = column.SortMemberPath == folderSort && column.DisplayIndex != 2 ? folderDirection : null;
    }

    private void OnFolderFilter(object sender, RoutedEventArgs e)
    {
        if (!ready)
            return;
        folderLimit = FolderPageSize;
        UpdateFolderRows();
    }

    private void OnFolderSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var key = e.Column.SortMemberPath;
        folderDirection = key == folderSort && folderDirection == ListSortDirection.Descending
            ? ListSortDirection.Ascending : ListSortDirection.Descending;
        folderSort = key;
        UpdateFolderRows();
        if (key == "Bytes")
        {
            foreach (var column in FolderGrid.Columns.Where(column => column.SortMemberPath == key))
                column.SortDirection = ReferenceEquals(column, e.Column) ? folderDirection : null;
        }
    }

    private void OnMoreFolders(object sender, RoutedEventArgs e)
    {
        folderLimit += FolderPageSize;
        UpdateFolderRows();
    }

    private void OnUp(object sender, RoutedEventArgs e)
    {
        if (currentFolder?.Parent is { } folder)
            Navigate(folder);
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (updatingSelection || e.NewValue is not FolderBranch branch)
            return;

        if (branch.Node is { } folder)
        {
            if (currentFolder != folder)
                Navigate(folder);
            return;
        }

        if (branch.MoreFrom is { } parent)
        {
            parent.LoadPage();
            if (parent.Node is { } node && currentFolder == node)
            {
                SelectTreeBranch(node);
                parent.IsSelected = true;
            }
        }
    }

    private void OnFolderSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingSelection && FolderGrid.SelectedItem is FolderRow row)
            Select(row.Node);
    }

    private void OnFolderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(FolderGrid, e.OriginalSource as DependencyObject) is DataGridRow
            && FolderGrid.SelectedItem is FolderRow row)
            ExploreFolder(row.Node);
    }

    private void OnFolderKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FolderGrid.SelectedItem is FolderRow row)
        {
            e.Handled = true;
            ExploreFolder(row.Node);
        }
    }

    private void OnExplore(object sender, RoutedEventArgs e)
    {
        if (selection is DiskDirectory folder)
            ExploreFolder(folder);
    }

    private void ExploreFolder(DiskDirectory folder)
    {
        AllRankedFolders.IsChecked = false;
        FolderFilter.Text = "";
        Navigate(folder);
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || updatingSelection)
            return;

        fileLimit = FilePageSize;
        ShowFiles();
    }

    private void OnMoreFiles(object sender, RoutedEventArgs e)
    {
        fileLimit += FilePageSize;
        ShowFiles();
    }

    private void OnFileSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var key = e.Column.SortMemberPath;
        fileDirection = fileSort == key && fileDirection == ListSortDirection.Descending
            ? ListSortDirection.Ascending : ListSortDirection.Descending;
        fileSort = key;
        ShowFiles();
    }

    private void ShowFiles(DiskFile? ensure = null)
    {
        if (result is not { } completed)
            return;

        var matches = DiskSpaceLayout.FilterFiles(completed.Largest, NameFilter.Text, ExtensionFilter.Text);
        var sign = fileDirection == ListSortDirection.Ascending ? 1 : -1;
        var sorted = matches.ToArray();
        Array.Sort(sorted, (left, right) =>
        {
            var compare = fileSort switch
            {
                "Name" => StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name),
                "Path" => StringComparer.CurrentCultureIgnoreCase.Compare(left.Path, right.Path),
                "Extension" => StringComparer.CurrentCultureIgnoreCase.Compare(left.Extension, right.Extension),
                "ModifiedFileTime" => left.ModifiedFileTime.CompareTo(right.ModifiedFileTime),
                _ => left.LogicalBytes.CompareTo(right.LogicalBytes),
            };
            return compare == 0 ? StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name) : sign * Math.Sign(compare);
        });
        var ensured = ensure is null ? -1 : Array.IndexOf(sorted, ensure);
        if (ensured >= fileLimit)
            fileLimit = (ensured / FilePageSize + 1) * FilePageSize;

        var rows = sorted.Take(fileLimit).Select(file => new FileRow(file)).ToArray();
        var chosen = ensure ?? selection as DiskFile;
        updatingSelection = true;
        FileGrid.ItemsSource = rows;
        FileGrid.SelectedItem = rows.FirstOrDefault(row => row.File == chosen);
        updatingSelection = false;
        if (ensure is null && chosen is not null && FileGrid.SelectedItem is null && currentFolder is { } folder)
            Select(folder);

        foreach (var column in FileGrid.Columns)
            column.SortDirection = column.SortMemberPath == fileSort ? fileDirection : null;

        RankingText.Text = F("Ranking", rows.Length, sorted.Length, DiskSpaceScanner.DefaultRankingLimit);
        MoreFilesButton.Visibility = rows.Length < sorted.Length ? Visibility.Visible : Visibility.Collapsed;
        EmptyFiles.Visibility = sorted.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!updatingSelection && FileGrid.SelectedItem is FileRow row)
        {
            if (currentFolder != row.File.Directory)
                Navigate(row.File.Directory);
            Select(row.File);
        }
    }

    private void OnOverviewResized(object sender, SizeChangedEventArgs e)
    {
        if (!ready)
            return;

        var width = e.NewSize.Width;
        var stacked = width < 550;
        var compact = !stacked && width < 740;
        SummaryColumn.Width = stacked ? new(1, GridUnitType.Star) : new(compact ? 248 : 334);
        TotalText.FontSize = UsedText.FontSize = FreeText.FontSize = compact ? 16 : 19;
        MapColumn.Width = stacked ? new(0) : new(1, GridUnitType.Star);
        Grid.SetRow(MapPanel, stacked ? 1 : 0);
        Grid.SetColumn(MapPanel, stacked ? 0 : 1);
        MapPanel.Margin = stacked ? new(0, 10, 0, 0) : new(12, 0, 0, 0);
        MapViewport.Height = stacked ? 190 : Math.Clamp(e.NewSize.Height - 62, 120, 302);
    }

    private void OnWindowResized(object sender, SizeChangedEventArgs e)
    {
        if (ready)
            VolumePicker.Width = e.NewSize.Width < 740 ? 164 : 206;
    }

    private void BuildMap(DiskDirectory folder)
    {
        SpaceMap.Children.Clear();
        mapItems = DiskSpaceLayout.Items(folder, result!.Largest);
        MapEmptyText.Visibility = mapItems.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var item in mapItems)
        {
            var title = item.Name ?? T("Other");
            var file = item.File is not null;
            var isFolder = item.Folder is not null;
            var label = new StackPanel { Margin = new(7, 5, 7, 3) };
            label.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            label.Children.Add(new TextBlock { Text = Size(item.Bytes), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            var tooltip = new TextBlock
            {
                Text = F("SelectedSpaceShare", title, Size(item.Bytes),
                    folder.LogicalBytes > 0 ? (double)item.Bytes / folder.LogicalBytes : 0, item.Path ?? T("OtherDetails")),
                MaxWidth = 460,
                TextWrapping = TextWrapping.Wrap,
            };
            var button = new Button
            {
                Content = label, Tag = item, ToolTip = tooltip, Style = (Style)FindResource("MapTile"),
                Cursor = Cursors.Hand, ClipToBounds = true,
            };
            button.SetResourceReference(BackgroundProperty, isFolder ? "AccentSubtleBrush" : file ? "SuccessSubtleBrush" : "ButtonBgBrush");
            button.SetResourceReference(BorderBrushProperty, isFolder ? "AccentBorderBrush" : file ? "SuccessBrush" : "BorderStrongBrush");
            AutomationProperties.SetName(button, $"{title} · {Size(item.Bytes)}");
            button.Click += OnTileClick;
            SpaceMap.Children.Add(button);
        }

        ArrangeMap();
    }

    private void OnMapResized(object sender, SizeChangedEventArgs e) => ArrangeMap();

    private void ArrangeMap()
    {
        if (mapItems.Length == 0 || SpaceMap.ActualWidth <= 0 || SpaceMap.ActualHeight <= 0)
            return;

        var cells = DiskSpaceLayout.Arrange(mapItems, new(0, 0, SpaceMap.ActualWidth, SpaceMap.ActualHeight));
        for (var i = 0; i < cells.Length; i++)
        {
            var tile = (Button)SpaceMap.Children[i];
            var cell = cells[i];
            Canvas.SetLeft(tile, cell.X + 1);
            Canvas.SetTop(tile, cell.Y + 1);
            tile.Width = Math.Max(0, cell.Width - 2);
            tile.Height = Math.Max(0, cell.Height - 2);
            ((UIElement)tile.Content).Visibility = tile.Width >= 42 && tile.Height >= 25 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnTileClick(object sender, RoutedEventArgs e)
    {
        var item = (DiskMapItem)((Button)sender).Tag;
        switch (item.Target)
        {
            case DiskDirectory folder:
                Navigate(folder);
                UpButton.Focus();
                break;
            case DiskFile file:
                updatingSelection = true;
                NameFilter.Text = ExtensionFilter.Text = "";
                updatingSelection = false;
                ShowFiles(file);
                Select(file);
                break;
            default:
                Select(item);
                break;
        }
    }

    private void Select(object target)
    {
        selection = target;
        duplicateVolume = null;
        updatingSelection = true;
        FileGrid.SelectedItem = target is DiskFile fileTarget
            ? FileGrid.Items.OfType<FileRow>().FirstOrDefault(row => row.File == fileTarget) : null;
        FolderGrid.SelectedItem = target is DiskDirectory folderTarget
            ? FolderGrid.Items.OfType<FolderRow>().FirstOrDefault(row => row.Node == folderTarget) : null;
        updatingSelection = false;
        SelectionText.Text = target switch
        {
            DiskDirectory folder => folder.Path,
            DiskFile file => file.Path,
            _ => T("Other"),
        };
        CopyButton.IsEnabled = ExplorerButton.IsEnabled = target is DiskDirectory or DiskFile;
        ExploreButton.IsEnabled = target is DiskDirectory next && next != currentFolder;

        foreach (Button button in SpaceMap.Children)
        {
            var item = (DiskMapItem)button.Tag;
            var selected = ReferenceEquals(item.Target ?? item, target);
            button.BorderThickness = selected ? new(3) : new(1);
            button.SetResourceReference(BorderBrushProperty, selected ? "AccentTextBrush"
                : item.Folder is not null ? "AccentBorderBrush" : item.File is not null ? "SuccessBrush" : "BorderStrongBrush");
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (selection is not (DiskDirectory or DiskFile))
            return;

        try
        {
            copy(SelectionText.Text);
            StatusText.Text = T("Copied");
        }
        catch (ExternalException) { StatusText.Text = T("CopyFailed"); }
    }

    private void OnExplorer(object sender, RoutedEventArgs e)
    {
        if (result is not { } completed)
            return;

        var file = selection as DiskFile;
        var folder = file?.Directory ?? selection as DiskDirectory;
        if (folder is not null && !reveal(duplicateVolume ?? completed.Volume, folder, file))
            StatusText.Text = T("ExplorerFailed");
    }

    private void PresentDuplicates()
    {
        if (analysis?.Duplicates is not { } duplicates)
            return;
        DuplicatesSummary.Text = F("DuplicatesSummary", duplicates.Groups.Count, duplicates.Candidates, duplicates.Inaccessible, duplicates.Omitted);
        var rows = duplicates.Groups.SelectMany((group, index) => group.Files.Select(file => new DuplicateRow(index + 1, file)))
            .Take(duplicateLimit + 1).ToArray();
        DuplicatesGrid.ItemsSource = rows.Take(duplicateLimit).ToArray();
        MoreDuplicatesButton.Visibility = rows.Length > duplicateLimit ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnMoreDuplicates(object sender, RoutedEventArgs e)
    {
        duplicateLimit += FilePageSize;
        PresentDuplicates();
    }

    private void OnDuplicateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (DuplicatesGrid.SelectedItem is not DuplicateRow row)
            return;
        Select(row.Entry.File);
        duplicateVolume = row.Entry.Volume;
    }

    private sealed record DuplicateRow(int Group, DuplicateFile Entry)
    {
        public string Name => Entry.File.Name;
        public string Path => Entry.File.Path;
        public long Bytes => Entry.File.LogicalBytes;
        public string Size => DiskSpaceWindow.Size(Bytes);
    }

    private sealed record FolderRow(DiskDirectory Node, long RootBytes, bool ShowPath)
    {
        public string Name => Node.Name;
        public string DisplayPath => ShowPath ? Node.Path : Node.Name;
        public string Size => DiskSpaceWindow.Size(Node.LogicalBytes);
        public string Share => (RootBytes > 0 ? (double)Node.LogicalBytes / RootBytes : 0).ToString("P1", Text.Culture);
        public string FileCount => Node.FileCount.ToString("N0", Text.Culture);
    }

    private sealed record FileRow(DiskFile File)
    {
        public string Name => File.Name;
        public string Path => File.Path;
        public string Size => DiskSpaceWindow.Size(File.LogicalBytes);
        public string Allocation => DiskSpaceWindow.Size(File.AllocatedBytes);
        public string Extension => File.Extension;
        public string Modified => File.Modified?.ToString("g", Text.Culture) ?? T("Unknown");
    }

    private sealed class FolderBranch : ObservableObject
    {
        private const int PageSize = 100;
        private Dictionary<DiskDirectory, FolderBranch>? loaded;
        private int nextIndex;
        private FolderBranch? more;
        private bool expanded, selected;

        public DiskDirectory? Node { get; }
        public FolderBranch? MoreFrom { get; }
        public string Name { get; }
        public ObservableCollection<FolderBranch> Children { get; } = [];

        public FolderBranch(DiskDirectory node)
        {
            Node = node;
            Name = node.Name;
            AddMore();
        }

        private FolderBranch(FolderBranch parent)
        {
            MoreFrom = parent;
            Name = T("MoreFolders");
        }

        public bool IsExpanded
        {
            get => expanded;
            set
            {
                if (SetProperty(ref expanded, value) && value && nextIndex == 0)
                    LoadPage();
            }
        }

        public bool IsSelected
        {
            get => selected;
            set => SetProperty(ref selected, value);
        }

        public void LoadPage()
        {
            if (Node is null || more is null)
                return;

            Children.Remove(more);
            more = null;
            var end = Math.Min(nextIndex + PageSize, Node.Children.Count);
            while (nextIndex < end)
                Reveal(Node.Children[nextIndex++]);

            AddMore();
        }

        public FolderBranch Reveal(DiskDirectory node)
        {
            loaded ??= [];
            if (loaded.TryGetValue(node, out var existing))
                return existing;

            var branch = new FolderBranch(node);
            loaded.Add(node, branch);
            Children.Insert(Children.Count - (more is null ? 0 : 1), branch);
            return branch;
        }

        private void AddMore()
        {
            if (Node is { } node && nextIndex < node.Children.Count)
            {
                more = new FolderBranch(this);
                Children.Add(more);
            }
        }
    }
}
