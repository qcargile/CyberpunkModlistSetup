using ConflictStudio.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace ConflictStudio.App;

public partial class MainWindow
{
    private ProfileViewStateStore _viewStateStore = null!;
    private readonly DispatcherTimer _viewSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Task _viewSaveTask = Task.CompletedTask;
    private bool _viewDirty;
    private bool _restoringView;
    private bool _viewInitialized;
    private bool _closingForWrites;
    private bool _skipCloseFlush;
    private bool _userStateWriteFailed;
    private int _viewChangeRevision;
    internal Task PendingUserStateWrites => Task.WhenAll(_viewSaveTask, _noteSaveTask, _baselineWriteTask);
    private static readonly double[] DefaultColumnWidths = [245, 320, 150, 520, 420, 320];
    private static readonly string[] ColumnSortMembers = ["AnalysisLabel", "ProofLabel", "SurfaceLabel", "Target", "ProviderSummary", "FilesSummary"];

    private void InitializeViewPersistence(string applicationData)
    {
        _viewStateStore = new ProfileViewStateStore(applicationData);
        _viewSaveTimer.Tick += (_, _) => SaveCurrentView();
        for (int index = 0; index < WorkQueueDataGrid.Columns.Count; index++)
        {
            DataGridColumn column = WorkQueueDataGrid.Columns[index];
            column.SortMemberPath = ColumnSortMembers[index];
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn)).AddValueChanged(column, ViewGeometryChanged);
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn)).AddValueChanged(column, ViewGeometryChanged);
        }
        WorkQueueDataGrid.Sorting += (_, _) => Dispatcher.BeginInvoke(ScheduleViewSave, DispatcherPriority.Background);
        CodeDetailSplitter.DragCompleted += (_, _) => ScheduleViewSave();
        CodeSummaryExpander.Expanded += (_, _) => ScheduleViewSave();
        CodeSummaryExpander.Collapsed += (_, _) => ScheduleViewSave();
        MainTabControl.SelectionChanged += (_, e) => { if (ReferenceEquals(e.Source, MainTabControl)) ScheduleViewSave(); };
        _viewInitialized = true;
    }

    private void ViewGeometryChanged(object? sender, EventArgs e) => ScheduleViewSave();

    private void ScheduleViewSave()
    {
        if (!_viewInitialized || _restoringView || _investigationClosed || _receipt?.InstallationId is null) return;
        _viewDirty = true;
        _viewChangeRevision++;
        _viewSaveTimer.Stop();
        _viewSaveTimer.Start();
    }

    private static string SelectedTag(ComboBox comboBox, string fallback)
        => (comboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectTag(ComboBox comboBox, string tag)
        => comboBox.SelectedItem = comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.Ordinal)) ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

    private SortDescription[] CurrentCodeSort()
        => WorkQueueDataGrid.ItemsSource is null ? [] : CollectionViewSource.GetDefaultView(WorkQueueDataGrid.ItemsSource).SortDescriptions.ToArray();

    private void RestoreCodeSort(IEnumerable<SortDescription> sort)
    {
        if (WorkQueueDataGrid.ItemsSource is null) return;
        ICollectionView view = CollectionViewSource.GetDefaultView(WorkQueueDataGrid.ItemsSource);
        using (view.DeferRefresh())
        {
            view.SortDescriptions.Clear();
            foreach (DataGridColumn column in WorkQueueDataGrid.Columns) column.SortDirection = null;
            foreach (SortDescription description in sort)
            {
                DataGridColumn? column = WorkQueueDataGrid.Columns.FirstOrDefault(value => value.SortMemberPath == description.PropertyName);
                if (column is null) continue;
                column.SortDirection = description.Direction;
                view.SortDescriptions.Add(description);
            }
        }
    }

    private ProfileViewState CaptureProfileView(ProfileScanReceipt receipt)
    {
        Grid grid = (Grid)CodeDetailSplitter.Parent;
        double height = grid.RowDefinitions[3].ActualHeight + grid.RowDefinitions[5].ActualHeight;
        SortDescription[] sort = CurrentCodeSort();
        ProfileColumnState[] columns = WorkQueueDataGrid.Columns.Select((column, index) =>
        {
            int priority = Array.FindIndex(sort, value => value.PropertyName == column.SortMemberPath);
            double width = column.ActualWidth > 0 ? column.ActualWidth : column.Width.Value;
            return new ProfileColumnState(ProfileViewStateStore.CodeColumnKeys[index], width, column.Visibility == Visibility.Visible, priority >= 0 ? sort[priority].Direction.ToString() : null, Math.Max(0, priority));
        }).ToArray();
        return new ProfileViewState(receipt.ManagerKind, receipt.InstallationId!, receipt.ProfileName)
        {
            CodeSearch = QueueSearchTextBox.Text,
            CodeView = SelectedTag(QueueViewComboBox, "Actionable"),
            CodeSurface = SelectedTag(QueueSurfaceComboBox, "All"),
            CodeProvider = QueueProviderComboBox.SelectedItem as string ?? "All mods",
            CodeOtherProvider = QueueOtherProviderComboBox.SelectedItem as string ?? "All mods",
            ArchiveModFilter = ArchiveModFilterTextBox.Text,
            ArchiveFileFilter = ArchiveFileFilterTextBox.Text,
            ShowNonConflictingFiles = ShowNonConflictingFilesCheckBox.IsChecked == true,
            OnlyConflictingArchives = OnlyConflictingArchivesCheckBox.IsChecked == true,
            Columns = columns,
            CodeDetailFraction = height > 0 ? grid.RowDefinitions[5].ActualHeight / height : 0.6,
            SummaryExpanded = CodeSummaryExpander.IsExpanded,
            SelectedTab = MainTabControl.SelectedIndex,
            HistoryReference = SelectedTag(HistoryReferenceComboBox, "previous"),
            HistoryFilter = SelectedTag(HistoryFilterComboBox, "Changes")
        };
    }

    private void SaveCurrentView()
    {
        _viewSaveTimer.Stop();
        if (!_viewInitialized || !_viewDirty || _restoringView || _receipt?.InstallationId is null) return;
        ProfileScanReceipt receipt = _receipt;
        ProfileViewState state = CaptureProfileView(receipt);
        _viewDirty = false;
        Task<bool> write = _viewSaveTask.ContinueWith(_ => _viewStateStore.TrySave(state), TaskScheduler.Default);
        _viewSaveTask = write;
        _ = ReportViewWriteAsync(write, receipt);
    }

    private async Task ReportViewWriteAsync(Task<bool> write, ProfileScanReceipt receipt)
    {
        try
        {
            bool saved = await write;
            if (!saved && !_investigationClosed && ReferenceEquals(receipt, _receipt)) FooterStatusTextBlock.Text = "This view could not be saved. Your scan and reviews are unchanged.";
        }
        catch (Exception exception) { _diagnostics.TryWrite("view-preferences", exception); }
    }

    private void ApplyProfileView(ProfileViewState state)
    {
        _restoringView = true;
        try
        {
            QueueSearchTextBox.Text = state.CodeSearch;
            SelectTag(QueueViewComboBox, state.CodeView);
            SelectTag(QueueSurfaceComboBox, state.CodeSurface);
            QueueProviderComboBox.SelectedItem = QueueProviderComboBox.Items.Cast<string>().FirstOrDefault(value => value == state.CodeProvider) ?? "All mods";
            QueueOtherProviderComboBox.SelectedItem = QueueOtherProviderComboBox.Items.Cast<string>().FirstOrDefault(value => value == state.CodeOtherProvider) ?? "All mods";
            ArchiveModFilterTextBox.Text = state.ArchiveModFilter;
            ArchiveFileFilterTextBox.Text = state.ArchiveFileFilter;
            ShowNonConflictingFilesCheckBox.IsChecked = state.ShowNonConflictingFiles;
            OnlyConflictingArchivesCheckBox.IsChecked = state.OnlyConflictingArchives;
            for (int index = 0; index < WorkQueueDataGrid.Columns.Count; index++)
            {
                DataGridColumn column = WorkQueueDataGrid.Columns[index];
                ProfileColumnState? saved = state.Columns.FirstOrDefault(value => value.Key == ProfileViewStateStore.CodeColumnKeys[index]);
                column.Width = saved?.Width ?? DefaultColumnWidths[index];
                column.Visibility = saved?.Visible == false ? Visibility.Collapsed : Visibility.Visible;
            }
            if (WorkQueueDataGrid.Columns.All(column => column.Visibility != Visibility.Visible)) WorkQueueDataGrid.Columns[3].Visibility = Visibility.Visible;
            Grid grid = (Grid)CodeDetailSplitter.Parent;
            grid.RowDefinitions[3].Height = new GridLength(1 - state.CodeDetailFraction, GridUnitType.Star);
            grid.RowDefinitions[5].Height = new GridLength(state.CodeDetailFraction, GridUnitType.Star);
            CodeSummaryExpander.IsExpanded = state.SummaryExpanded;
            MainTabControl.SelectedIndex = state.SelectedTab;
            SelectTag(HistoryReferenceComboBox, state.HistoryReference);
            SelectTag(HistoryFilterComboBox, state.HistoryFilter);
            ApplyQueueFilter();
            SortDescription[] sort = state.Columns.Where(column => column.SortDirection is not null).OrderBy(column => column.SortPriority)
                .Select(column => new SortDescription(ColumnSortMembers[Array.IndexOf(ProfileViewStateStore.CodeColumnKeys, column.Key)], Enum.Parse<ListSortDirection>(column.SortDirection!))).ToArray();
            RestoreCodeSort(sort);
            ApplyArchiveTreeFilter();
        }
        finally { _restoringView = false; }
    }

    private void ColumnsClicked(object sender, RoutedEventArgs e)
    {
        ContextMenu menu = new();
        foreach (DataGridColumn column in WorkQueueDataGrid.Columns)
        {
            MenuItem item = new() { Header = column.Header, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible };
            item.Click += (_, _) =>
            {
                if (!item.IsChecked && WorkQueueDataGrid.Columns.Count(value => value.Visibility == Visibility.Visible) == 1) { item.IsChecked = true; return; }
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = ColumnsButton;
        menu.IsOpen = true;
    }

    private async void ResetViewClicked(object sender, RoutedEventArgs e)
    {
        if (_receipt?.InstallationId is not string installation) return;
        ProfileScanReceipt receipt = _receipt;
        _viewSaveTimer.Stop();
        _viewDirty = false;
        _viewChangeRevision++;
        ApplyProfileView(new ProfileViewState(receipt.ManagerKind, installation, receipt.ProfileName) { SelectedTab = MainTabControl.SelectedIndex });
        _historyTask = RefreshHistoryComparisonAsync();
        Task<bool> write = _viewSaveTask.ContinueWith(_ => _viewStateStore.TryDelete(receipt.ManagerKind, installation, receipt.ProfileName), TaskScheduler.Default);
        _viewSaveTask = write;
        await ReportViewWriteAsync(write, receipt);
    }

    private bool FlushInvestigationBeforeClose(CancelEventArgs e)
    {
        if (!_viewInitialized) return false;
        if (_skipCloseFlush) { _skipCloseFlush = false; return false; }
        if (_closingForWrites) { e.Cancel = true; return true; }
        SaveCurrentView();
        Task writes = Task.WhenAll(_viewSaveTask, _noteSaveTask, _baselineWriteTask);
        if (writes.IsCompleted) return false;
        e.Cancel = true;
        _closingForWrites = true;
        IsEnabled = false;
        _ = CloseAfterInvestigationWritesAsync(writes);
        return true;
    }

    private async Task CloseAfterInvestigationWritesAsync(Task writes)
    {
        try { await writes; }
        catch (Exception exception) { _diagnostics.TryWrite("investigation-close", exception); _userStateWriteFailed = true; }
        if (_investigationClosed) return;
        IsEnabled = true;
        _closingForWrites = false;
        if (_userStateWriteFailed) return;
        _skipCloseFlush = true;
        Close();
    }

    private void DisposeInvestigation()
    {
        if (_investigationClosed) return;
        SaveCurrentView();
        _investigationClosed = true;
        _viewSaveTimer.Stop();
        _historyRevision++;
        _historyCancellation?.Cancel();
        _comparisonRevision++;
        _previousScan = null;
        _baseline = null;
        _historyComparison = null;
        if (!_viewInitialized) return;
        foreach (DataGridColumn column in WorkQueueDataGrid.Columns)
        {
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn)).RemoveValueChanged(column, ViewGeometryChanged);
            DependencyPropertyDescriptor.FromProperty(DataGridColumn.VisibilityProperty, typeof(DataGridColumn)).RemoveValueChanged(column, ViewGeometryChanged);
        }
    }
}
