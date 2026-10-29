using ConflictStudio.Core;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ConflictStudio.App;

public partial class MainWindow
{
    private string _applicationDataDirectory = string.Empty;
    private readonly SemaphoreSlim _historyGate = new(1, 1);
    private CancellationTokenSource? _historyCancellation;
    private ProfileScanReceipt? _previousScan;
    private ScanBaselineLoadResult? _baseline;
    private ScanHistoryComparison? _historyComparison;
    private int _historyRevision;
    private int _comparisonRevision;
    private bool _historyBusy;
    private bool _investigationClosed;
    private Task _historyTask = Task.CompletedTask;
    private Task _baselineWriteTask = Task.CompletedTask;

    internal Task InvestigationReady => _historyTask;

    private void InitializeInvestigation(string applicationData)
    {
        _applicationDataDirectory = applicationData;
        _noteStore = new EvidenceNoteStore(_decisionDirectory);
        InitializeViewPersistence(applicationData);
    }

    private string HistoryDirectory(ProfileScanReceipt receipt)
    {
        string profile = string.Concat(receipt.ProfileName.Select(value => Path.GetInvalidFileNameChars().Contains(value) ? '_' : value));
        return Path.Combine(_applicationDataDirectory, "receipts", receipt.InstallationId!, profile);
    }

    private void BeginInvestigation(ProfileScanReceipt receipt, bool restoreView)
    {
        ScanCoveragePresentation coverage = ScanCoveragePresentation.Create(receipt);
        CoverageSummaryTextBlock.Text = coverage.Summary;
        CoverageDetailsTextBlock.Text = coverage.Details;
        if (receipt.InstallationId is null)
        {
            HistorySummaryTextBlock.Text = "This scan has no installation identity. Run a fresh scan before retaining a baseline.";
            return;
        }
        _historyBusy = true;
        ExportButton.IsEnabled = false;
        SaveOpenNoteButton.IsEnabled = SaveReviewButton.IsEnabled = false;
        ReviewRationaleTextBox.IsEnabled = false;
        _previousScan = null;
        _baseline = null;
        _historyComparison = null;
        HistoryDataGrid.ItemsSource = null;
        HistorySummaryTextBlock.Text = "Reading scan history…";
        PinBaselineButton.IsEnabled = ClearBaselineButton.IsEnabled = false;
        HistoryReferenceComboBox.IsEnabled = HistoryFilterComboBox.IsEnabled = false;
        _historyCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _historyCancellation = cancellation;
        _historyTask = LoadInvestigationAsync(receipt, ++_historyRevision, restoreView, _viewChangeRevision, cancellation);
    }

    private async Task LoadInvestigationAsync(ProfileScanReceipt receipt, int revision, bool restoreView, int viewRevision, CancellationTokenSource cancellation)
    {
        try
        {
            await PendingUserStateWrites.WaitAsync(cancellation.Token);
            await _historyGate.WaitAsync(cancellation.Token);
            ProfileScanReceiptPersistenceResult result;
            ScanBaselineLoadResult baseline;
            ProfileViewState view;
            (EvidenceNote[] Notes, string? Error) notes;
            try
            {
                (result, baseline, view, notes) = await Task.Run(() =>
                {
                    string directory = HistoryDirectory(receipt);
                    return (ProfileScanReceiptPersistence.Save(directory, receipt, cancellationToken: cancellation.Token), new ScanBaselineStore(directory).Load(receipt), _viewStateStore.Load(receipt.ManagerKind, receipt.InstallationId!, receipt.ProfileName), ReadOpenNotes());
                }, cancellation.Token);
            }
            finally { _historyGate.Release(); }
            if (_investigationClosed || revision != _historyRevision || !ReferenceEquals(_receipt, receipt)) return;
            _previousScan = result.PreviousReceipt;
            _baseline = baseline;
            _historyBusy = false;
            _notes = notes.Notes;
            _noteReadError = notes.Error;
            ExportButton.IsEnabled = true;
            ReviewRationaleTextBox.IsEnabled = true;
            ReloadQueue(WorkQueueDataGrid.SelectedItem as ConflictWorkItem);
            HistoryReferenceComboBox.IsEnabled = HistoryFilterComboBox.IsEnabled = true;
            if (restoreView && viewRevision == _viewChangeRevision) ApplyProfileView(view);
            PinBaselineButton.IsEnabled = true;
            PinBaselineButton.Content = baseline.State == ScanBaselineState.Missing ? "Pin current scan" : "Replace baseline";
            ClearBaselineButton.IsEnabled = baseline.State != ScanBaselineState.Missing;
            BaselineStatusTextBlock.Text = baseline.Message;
            if (result.InvalidHistory)
            {
                string recovery = result.PreservedInvalidPath is null ? "Previous history could not be preserved and was not replaced." : "Incompatible or unreadable history was preserved as " + Path.GetFileName(result.PreservedInvalidPath) + ".";
                BaselineStatusTextBlock.Text += " " + recovery;
                RecordAction("receipt-history", result.PreservedInvalidPath is null ? "blocked" : "recovered", recovery);
            }
            await RefreshHistoryComparisonAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _diagnostics.TryWrite("receipt-history", exception);
            if (_investigationClosed || revision != _historyRevision || !ReferenceEquals(_receipt, receipt)) return;
            var recovery = await Task.Run(() => (Notes: ReadOpenNotes(), Baseline: new ScanBaselineStore(HistoryDirectory(receipt)).Load(receipt)));
            if (_investigationClosed || revision != _historyRevision || !ReferenceEquals(_receipt, receipt)) return;
            _notes = recovery.Notes.Notes;
            _noteReadError = recovery.Notes.Error;
            _baseline = recovery.Baseline;
            _historyBusy = false;
            ExportButton.IsEnabled = true;
            ReviewRationaleTextBox.IsEnabled = true;
            ReloadQueue(WorkQueueDataGrid.SelectedItem as ConflictWorkItem);
            HistoryReferenceComboBox.IsEnabled = HistoryFilterComboBox.IsEnabled = true;
            BaselineStatusTextBlock.Text = recovery.Baseline.Message + " Rolling scan history could not be saved: " + exception.Message;
            PinBaselineButton.IsEnabled = true;
            ClearBaselineButton.IsEnabled = recovery.Baseline.State != ScanBaselineState.Missing;
            await RefreshHistoryComparisonAsync();
        }
        finally
        {
            if (ReferenceEquals(_historyCancellation, cancellation)) _historyCancellation = null;
            cancellation.Dispose();
        }
    }

    private void HistoryReferenceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringView || _historyBusy || _receipt is null || _investigationClosed) return;
        ScheduleViewSave();
        _historyTask = RefreshHistoryComparisonAsync();
    }

    private async Task RefreshHistoryComparisonAsync()
    {
        ProfileScanReceipt? current = _receipt;
        ProfileScanReceipt? previous = SelectedTag(HistoryReferenceComboBox, "previous") == "baseline" ? _baseline?.Receipt : _previousScan;
        int revision = ++_comparisonRevision;
        _historyComparison = null;
        HistoryDataGrid.ItemsSource = null;
        HistoryAnalysisNoticeTextBlock.Text = string.Empty;
        OpenHistoryFindingButton.IsEnabled = false;
        if (previous is null || current is null)
        {
            HistorySummaryTextBlock.Text = SelectedTag(HistoryReferenceComboBox, "previous") == "baseline" ? "No usable pinned baseline. Pin the current scan to compare after your next update." : "No earlier scan is available for comparison. Run another scan after making changes.";
            HistoryNoticeTextBlock.Text = _baseline?.State is ScanBaselineState.Foreign or ScanBaselineState.Unreadable ? _baseline.Message : string.Empty;
            return;
        }
        HistorySummaryTextBlock.Text = "Comparing recorded findings…";
        try
        {
            ScanHistoryComparison comparison = await Task.Run(() => ScanHistoryComparison.Create(previous, current));
            if (_investigationClosed || revision != _comparisonRevision || !ReferenceEquals(current, _receipt)) return;
            _historyComparison = comparison;
            HistoryAnalysisNoticeTextBlock.Text = comparison.AnalysisMayHaveChanged ? comparison.AnalysisNotice : string.Empty;
            HistoryNoticeTextBlock.Text = string.Join(Environment.NewLine, comparison.ToolVersionNotice, comparison.AnalysisNotice, comparison.CoverageNotice);
            FilterHistoryRows();
        }
        catch (Exception exception)
        {
            _diagnostics.TryWrite("history-comparison", exception);
            if (_investigationClosed || revision != _comparisonRevision) return;
            HistorySummaryTextBlock.Text = "These saved scans could not be compared. Current results are still available.";
            HistoryNoticeTextBlock.Text = exception.Message;
        }
    }

    private void HistoryFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringView || HistoryDataGrid is null) return;
        FilterHistoryRows();
        ScheduleViewSave();
    }

    private void FilterHistoryRows()
    {
        if (_historyComparison is not { } comparison) return;
        string filter = SelectedTag(HistoryFilterComboBox, "Changes");
        ScanHistoryEntry[] rows = comparison.Entries.Where(entry => filter == "All" || filter == "Changes" && entry.ChangeKind != ScanHistoryChangeKind.Unchanged || entry.ChangeKind.ToString() == filter).ToArray();
        HistoryDataGrid.ItemsSource = rows;
        HistoryDataGrid.SelectedItem = rows.FirstOrDefault();
        HistorySummaryTextBlock.Text = $"{comparison.Previous.ScannedAtUtc.LocalDateTime:g} → {comparison.Current.ScannedAtUtc.LocalDateTime:g} · {rows.Length:N0} shown of {comparison.Entries.Length:N0} recorded findings";
    }

    private void HistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryBeforeTextBox is null) return;
        ScanHistoryEntry? row = HistoryDataGrid.SelectedItem as ScanHistoryEntry;
        if (row is null)
        {
            HistoryBeforeTextBox.Text = HistoryAfterTextBox.Text = string.Empty;
            OpenHistoryFindingButton.IsEnabled = false;
            return;
        }
        HistoryBeforeTextBox.Text = DescribeHistoricalFinding(row?.Before, "Not detected in the earlier scan.");
        HistoryAfterTextBox.Text = DescribeHistoricalFinding(row?.After, "Not detected in the current scan. This does not prove it was fixed; removed mods or incomplete coverage can also change the result.");
        OpenHistoryFindingButton.IsEnabled = row?.CanOpenCurrent == true;
    }

    private static string DescribeHistoricalFinding(ConflictWorkItem? item, string missing)
        => item is null ? missing : string.Join(Environment.NewLine, string.IsNullOrWhiteSpace(item.AnalysisLabel) ? item.ClassificationLabel : item.AnalysisLabel, item.ProviderSummary, item.Summary, item.BoundaryLabel);

    private void OpenHistoryFindingClicked(object sender, RoutedEventArgs e)
    {
        if (HistoryDataGrid.SelectedItem is not ScanHistoryEntry { After: { } after }) return;
        ConflictWorkItem? current = _workItems.FirstOrDefault(item => item.Surface == after.Surface && item.Target == after.Target && item.EvidenceSha256 == after.EvidenceSha256);
        if (current is null) { HistoryNoticeTextBlock.Text = "This finding is no longer part of the current scan. Scan again before opening it."; return; }
        if (current.Surface == ConflictSurface.PackedResource)
        {
            MainTabControl.SelectedIndex = 0;
            ArchiveModFilterTextBox.Text = string.Empty;
            ArchiveFileFilterTextBox.Text = current.Target;
            ApplyArchiveTreeFilter();
            return;
        }
        MainTabControl.SelectedIndex = 1;
        QueueSearchTextBox.Text = string.Empty;
        SelectTag(QueueViewComboBox, "All");
        SelectTag(QueueSurfaceComboBox, current.Surface == ConflictSurface.Diagnostic ? "Diagnostic" : "All");
        QueueProviderComboBox.SelectedIndex = 0;
        QueueOtherProviderComboBox.SelectedIndex = 0;
        ApplyQueueFilter();
        WorkQueueDataGrid.SelectedItem = current;
        WorkQueueDataGrid.ScrollIntoView(current);
    }

    private void PinBaselineClicked(object sender, RoutedEventArgs e) => _baselineWriteTask = ChangeBaselineAsync(false);
    private void ClearBaselineClicked(object sender, RoutedEventArgs e) => _baselineWriteTask = ChangeBaselineAsync(true);

    private async Task ChangeBaselineAsync(bool clear)
    {
        ProfileScanReceipt? receipt = _receipt;
        if (receipt?.InstallationId is null || _historyBusy || !_baselineWriteTask.IsCompleted) return;
        _userStateWriteFailed = false;
        PinBaselineButton.IsEnabled = ClearBaselineButton.IsEnabled = false;
        try
        {
            ScanBaselineLoadResult result = await Task.Run(() =>
            {
                ScanBaselineStore store = new(HistoryDirectory(receipt));
                if (clear) store.Clear(); else store.Pin(receipt);
                return store.Load(receipt);
            });
            if (_investigationClosed || !ReferenceEquals(receipt, _receipt)) return;
            _baseline = result;
            HistoryReferenceComboBox.IsEnabled = HistoryFilterComboBox.IsEnabled = true;
            BaselineStatusTextBlock.Text = result.Message;
            bool restoring = _restoringView;
            _restoringView = true;
            try { SelectTag(HistoryReferenceComboBox, clear ? "previous" : "baseline"); }
            finally { _restoringView = restoring; }
            ScheduleViewSave();
            await RefreshHistoryComparisonAsync();
        }
        catch (Exception exception) { _userStateWriteFailed = true; if (!_investigationClosed) ShowError("scan-baseline", exception); }
        finally
        {
            if (!_investigationClosed && ReferenceEquals(receipt, _receipt))
            {
                PinBaselineButton.IsEnabled = true;
                PinBaselineButton.Content = _baseline?.State == ScanBaselineState.Missing ? "Pin current scan" : "Replace baseline";
                ClearBaselineButton.IsEnabled = _baseline?.State is not null and not ScanBaselineState.Missing;
            }
        }
    }

    private void ClearInvestigation()
    {
        _historyRevision++;
        _historyCancellation?.Cancel();
        _comparisonRevision++;
        _previousScan = null;
        _baseline = null;
        _historyComparison = null;
        _historyBusy = false;
        if (HistoryDataGrid is null) return;
        HistoryDataGrid.ItemsSource = null;
        PinBaselineButton.IsEnabled = ClearBaselineButton.IsEnabled = OpenHistoryFindingButton.IsEnabled = false;
        HistoryReferenceComboBox.IsEnabled = HistoryFilterComboBox.IsEnabled = false;
        HistorySummaryTextBlock.Text = "Run a scan to compare recorded findings.";
        HistoryNoticeTextBlock.Text = BaselineStatusTextBlock.Text = string.Empty;
        HistoryAnalysisNoticeTextBlock.Text = string.Empty;
        CoverageSummaryTextBlock.Text = "Run a scan to see its coverage.";
        CoverageDetailsTextBlock.Text = string.Empty;
    }
}
