using ConflictStudio.Core;
using System.IO;
using System.Windows;

namespace ConflictStudio.App;

public partial class MainWindow
{
    private EvidenceNoteStore _noteStore = null!;
    private EvidenceNote[] _notes = [];
    private string? _noteReadError;
    private Task _noteSaveTask = Task.CompletedTask;

    private (EvidenceNote[] Notes, string? Error) ReadOpenNotes()
    {
        try
        {
            EvidenceNote[] notes = _noteStore.Load();
            return (notes, _noteStore.LastRecoveryPath is string preserved ? $"Unreadable notes were preserved as {Path.GetFileName(preserved)}." : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or EvidenceNoteException)
        {
            _diagnostics.TryWrite("open-notes", exception);
            return ([], "Open notes could not be read. Existing notes were not replaced.");
        }
    }

    private void PresentReviewContext(ConflictWorkItem[] selected)
    {
        if (SaveOpenNoteButton is null) return;
        SaveOpenNoteButton.IsEnabled = !_historyBusy && _noteSaveTask.IsCompleted && selected.Length > 0 && selected.All(item => item.State != ConflictWorkState.Reviewed);
        List<string> context = [];
        if (_noteReadError is not null) context.Add(_noteReadError);
        if (selected.Length == 1)
        {
            ConflictWorkItem item = selected[0];
            if (item.ReviewStatusLabel is not null) context.Add(item.ReviewStatusLabel);
            if (item.PreviousReview is { } previous)
            {
                context.Add($"Previous review ({previous.ReviewedAtUtc.LocalDateTime:g}): {previous.Rationale}");
                context.Add(item.PreviousReviewReason ?? "The previous decision no longer matches this evidence.");
            }
            if (item.OpenNote is { } note && item.State != ConflictWorkState.Reviewed)
            {
                ReviewRationaleTextBox.Text = note.Text;
                context.Add(item.NoteIsStale ? "This open note was written for earlier evidence. It has not accepted the current finding." : "Open note saved. This finding remains open.");
            }
        }
        else if (selected.Length > 1) context.Add("Saving an open note applies this text to each selected finding without reviewing it.");
        ReviewContextTextBlock.Text = string.Join(Environment.NewLine, context);
        ReviewContextTextBlock.Visibility = context.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SaveOpenNoteClicked(object sender, RoutedEventArgs e) => _noteSaveTask = SaveOpenNoteAsync();

    private async Task SaveOpenNoteAsync()
    {
        ProfileScanReceipt? receipt = _receipt;
        ConflictWorkItem[] selected = WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().ToArray();
        if (receipt?.InstallationId is not string installation || selected.Length == 0 || selected.Any(item => item.State == ConflictWorkState.Reviewed) || _historyBusy || !_noteSaveTask.IsCompleted) return;
        string text = ReviewRationaleTextBox.Text;
        _userStateWriteFailed = false;
        SaveOpenNoteButton.IsEnabled = false;
        SaveReviewButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        ReviewRationaleTextBox.IsEnabled = false;
        try
        {
            EvidenceNote[] notes = await Task.Run(() => _noteStore.SaveMany(installation, receipt.ProfileName, selected, text, DateTimeOffset.UtcNow));
            if (_investigationClosed || !ReferenceEquals(receipt, _receipt)) return;
            _notes = notes;
            ConflictWorkItem current = WorkQueueDataGrid.SelectedItem as ConflictWorkItem ?? selected[0];
            ReloadQueue(current);
            WorkspaceStatusTextBlock.Text = string.IsNullOrWhiteSpace(text) ? "Open note cleared. No findings were marked reviewed." : "Note saved without marking any findings reviewed.";
        }
        catch (Exception exception)
        {
            _userStateWriteFailed = true;
            if (!_investigationClosed) ShowError("save-open-note", exception);
        }
        finally
        {
            if (!_investigationClosed && ReferenceEquals(receipt, _receipt) && !_historyBusy)
            {
                ReviewRationaleTextBox.IsEnabled = true;
                ExportButton.IsEnabled = true;
                SaveOpenNoteButton.IsEnabled = WorkQueueDataGrid.SelectedItems.Count > 0 && WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().All(item => item.State != ConflictWorkState.Reviewed);
                SaveReviewButton.IsEnabled = WorkQueueDataGrid.SelectedItems.Count > 0 && WorkQueueDataGrid.SelectedItems.Cast<ConflictWorkItem>().All(item => item.Classification != EvidenceClassification.Unresolved);
            }
        }
    }
}
