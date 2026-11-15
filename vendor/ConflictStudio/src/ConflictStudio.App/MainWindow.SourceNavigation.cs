using ConflictStudio.Core;
using System.Windows;

namespace ConflictStudio.App;

public partial class MainWindow
{
    private void UpdateFindingNavigation()
    {
        if (PreviousFindingButton is null) return;
        PreviousFindingButton.IsEnabled = WorkQueueDataGrid.SelectedIndex > 0;
        NextFindingButton.IsEnabled = WorkQueueDataGrid.Items.Count > 0 && WorkQueueDataGrid.SelectedIndex < WorkQueueDataGrid.Items.Count - 1;
    }

    private void PreviousFindingClicked(object sender, RoutedEventArgs e) => MoveFinding(-1);
    private void NextFindingClicked(object sender, RoutedEventArgs e) => MoveFinding(1);

    private void MoveFinding(int direction)
    {
        int index = WorkQueueDataGrid.SelectedIndex + direction;
        if (index < 0 || index >= WorkQueueDataGrid.Items.Count) return;
        bool comparisonOpen = _codeComparisonWindow is not null;
        WorkQueueDataGrid.SelectedItems.Clear();
        WorkQueueDataGrid.SelectedIndex = index;
        WorkQueueDataGrid.ScrollIntoView(WorkQueueDataGrid.SelectedItem);
        if (!comparisonOpen) return;
        _codeComparisonWindow?.Close();
        if (WorkQueueDataGrid.SelectedItem is ConflictWorkItem { Comparisons.Length: > 0 }) ViewCodeClicked(this, new RoutedEventArgs());
    }
}
