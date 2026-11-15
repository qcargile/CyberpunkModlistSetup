using ConflictStudio.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ConflictStudio.App;

public partial class CodeComparisonWindow
{
    private ConflictWorkItem _item = null!;
    private CodeSourceEvidence[] _contributors = [];
    private TweakReferenceIndex _references = TweakReferenceIndex.Empty;
    private SourceEditorPreferenceStore _editorPreferences = null!;
    private SourceInspectionWindow? _sourceInspector;
    private bool _compactSources;

    private void ComparisonLayoutChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Height < 680;
        if (compact && !_compactSources) ContributorsExpander.IsExpanded = false;
        _compactSources = compact;
    }

    private void InitializeSourceNavigation(ConflictWorkItem item, IReadOnlyList<CodeSourceEvidence> sources, TweakReferenceIndex? references, SourceEditorPreferenceStore? editorPreferences)
    {
        _item = item;
        HashSet<string> targets = item.RelatedTargets.Append(item.Target).ToHashSet(StringComparer.Ordinal);
        _contributors = sources.Where(source => source.Surface == item.Surface && targets.Contains(source.Target)).ToArray();
        _references = references ?? TweakReferenceIndex.Empty;
        _editorPreferences = editorPreferences ?? new SourceEditorPreferenceStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio"));
        ContributorsExpander.IsExpanded = _contributors.Length > 2;
    }

    private void RefreshContributors()
    {
        if (_item is null) return;
        CodeContributorRow? selected = ContributorsDataGrid.SelectedItem as CodeContributorRow;
        CodeContributorRow[] rows = CodeContributorOverview.Create(_item, _contributors, _witness);
        ContributorsDataGrid.ItemsSource = rows;
        ContributorsDataGrid.SelectedItem = rows.FirstOrDefault(row => row.OperationId == selected?.OperationId && row.Source == selected?.Source) ?? rows.FirstOrDefault();
        ContributorsTitleTextBlock.Text = $"Contributors and source context ({rows.Length:N0})";
        PreviousIssueButton.IsEnabled = WitnessComboBox.SelectedIndex > 0;
        NextIssueButton.IsEnabled = WitnessComboBox.SelectedIndex >= 0 && WitnessComboBox.SelectedIndex < WitnessComboBox.Items.Count - 1;
        IssueNavigationPanel.Visibility = WitnessComboBox.Items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateContributorActions();
    }

    private void ContributorSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateContributorActions();

    private void UpdateContributorActions()
    {
        if (InspectContributorButton is null) return;
        CodeContributorRow? row = ContributorsDataGrid.SelectedItem as CodeContributorRow;
        InspectContributorButton.IsEnabled = row?.Source is not null;
        CompareContributorButton.IsEnabled = row?.SupportsSelectedIssue == true && LeftSourceComboBox.Items.OfType<CodeSourceChoice>().Any(choice => choice.Participant.OperationId == row.OperationId);
        TweakReferenceResolution? reference = row?.Source is { } source ? _references.Resolve(source) : null;
        ContributorReferenceTextBlock.Text = reference is null || reference.State == TweakReferenceState.NotReference ? string.Empty : reference.Message;
    }

    private void CompareContributorClicked(object sender, RoutedEventArgs e)
    {
        if (ContributorsDataGrid.SelectedItem is not CodeContributorRow { SupportsSelectedIssue: true } row) return;
        CodeSourceChoice? selected = LeftSourceComboBox.Items.OfType<CodeSourceChoice>().FirstOrDefault(choice => choice.Participant.OperationId == row.OperationId && (row.Source is null || choice.Evidence == row.Source))
            ?? LeftSourceComboBox.Items.OfType<CodeSourceChoice>().FirstOrDefault(choice => choice.Participant.OperationId == row.OperationId);
        if (selected is not null) LeftSourceComboBox.SelectedItem = selected;
        if (_compactSources) ContributorsExpander.IsExpanded = false;
    }

    private void InspectContributorClicked(object sender, RoutedEventArgs e)
    {
        if (ContributorsDataGrid.SelectedItem is CodeContributorRow { Source: { } source }) InspectSource(source);
    }

    private void InspectPaneClicked(object sender, RoutedEventArgs e)
    {
        if (SourceFor(sender) is { } source) InspectSource(source);
    }

    private void InspectSource(CodeSourceEvidence source)
    {
        _sourceInspector?.Close();
        SourceInspectionWindow inspector = new(this, source, _references, _editorPreferences, LeftCodeBox.FontSize);
        _sourceInspector = inspector;
        inspector.Closed += (_, _) => { if (ReferenceEquals(_sourceInspector, inspector)) _sourceInspector = null; };
        inspector.Show();
    }

    private void PreviousIssueClicked(object sender, RoutedEventArgs e)
    {
        if (WitnessComboBox.SelectedIndex > 0) WitnessComboBox.SelectedIndex--;
    }

    private void NextIssueClicked(object sender, RoutedEventArgs e)
    {
        if (WitnessComboBox.SelectedIndex < WitnessComboBox.Items.Count - 1) WitnessComboBox.SelectedIndex++;
    }

    private void ComparisonFontChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LeftCodeBox is null || RightCodeBox is null || ComparisonFontComboBox.SelectedItem is not ComboBoxItem selected) return;
        double size = double.Parse((string)selected.Tag, CultureInfo.InvariantCulture);
        LeftCodeBox.FontSize = RightCodeBox.FontSize = size;
        if (!_initializing && !_closed) RenderSources();
    }

    private void ComparisonEditorSettingsClicked(object sender, RoutedEventArgs e) => new SourceEditorSettingsWindow(this, _editorPreferences).ShowDialog();
}
