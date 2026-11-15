using ConflictStudio.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace ConflictStudio.App;

public sealed record SourceDefinitionChoice(CodeSourceEvidence Source)
{
    public override string ToString() => $"{Source.Provider} · {Source.FilePath}:{Source.StartLine}";
}

public partial class SourceInspectionWindow : Window
{
    private readonly TweakReferenceIndex _references;
    private readonly SourceEditorPreferenceStore _editorPreferences;
    private readonly Func<CodeSourceEvidence, CancellationToken, Task<CodeSourceDocument>> _reader;
    private readonly List<CodeSourceEvidence> _back = [];
    private CodeSourceEvidence _source;
    private CodeSourceDocument? _document;
    private CancellationTokenSource? _readCancellation;
    private CancellationTokenSource? _searchCancellation;
    private CodeSourceSearchResult _search = new([], false);
    private int _matchIndex = -1;
    private int? _displayLine;
    private int _revision;
    private int _lineLimit = 200;
    private int _contextLines = 3;
    private int _recordedContextLines = 3;
    private bool _closed;
    private Task _navigationTask = Task.CompletedTask;
    private Task _searchTask = Task.CompletedTask;

    internal Task NavigationReady => _navigationTask;
    internal Task SearchReady => _searchTask;

    public SourceInspectionWindow(Window owner, CodeSourceEvidence source, TweakReferenceIndex references, SourceEditorPreferenceStore editorPreferences, double textSize)
        : this(source, references, editorPreferences, CodeSourceReader.ReadAsync, textSize)
    {
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
    }

    internal SourceInspectionWindow(CodeSourceEvidence source, TweakReferenceIndex references, SourceEditorPreferenceStore editorPreferences, Func<CodeSourceEvidence, CancellationToken, Task<CodeSourceDocument>> reader, double textSize = 13)
    {
        _source = source;
        _references = references;
        _editorPreferences = editorPreferences;
        _reader = reader;
        InitializeComponent();
        SourceFontSizeComboBox.SelectedItem = SourceFontSizeComboBox.Items.Cast<ComboBoxItem>().FirstOrDefault(item => double.Parse((string)item.Tag, CultureInfo.InvariantCulture) == textSize) ?? SourceFontSizeComboBox.Items[1];
        Loaded += (_, _) => _navigationTask = LoadSourceAsync();
    }

    internal Task LoadSourceAsync() => NavigateAsync(_source, false);

    private async Task NavigateAsync(CodeSourceEvidence source, bool remember, int contextLines = 3)
    {
        if (_closed) return;
        _readCancellation?.Cancel();
        _searchCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _readCancellation = cancellation;
        int revision = ++_revision;
        if (remember)
        {
            if (_back.Count == 32) _back.RemoveAt(0);
            _back.Add(_source);
        }
        _source = source;
        _document = null;
        _displayLine = null;
        _lineLimit = 200;
        _contextLines = _recordedContextLines = contextLines;
        _search = new([], false);
        _matchIndex = -1;
        BackButton.IsEnabled = _back.Count > 0;
        FindSourceButton.IsEnabled = InspectorOpenButton.IsEnabled = InspectorContextButton.IsEnabled = OriginalLocationButton.IsEnabled = false;
        NextMatchButton.IsEnabled = PreviousMatchButton.IsEnabled = false;
        SearchStatusTextBlock.Text = string.Empty;
        SourceCodeBox.Document = new FlowDocument();
        SourceTitleTextBlock.Text = source.Target;
        SourcePathTextBox.Text = source.PhysicalPath;
        InspectorStatusTextBlock.Text = "Verifying source against the scan…";
        TweakReferenceResolution reference = _references.Resolve(source);
        ReferencePanel.Visibility = reference.State == TweakReferenceState.NotReference ? Visibility.Collapsed : Visibility.Visible;
        DefinitionChoicesPanel.Visibility = reference.Definitions.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReferenceStatusTextBlock.Text = reference.Message + (reference.Definitions.Length > 1 ? " Choose a definition; display order does not establish priority." : string.Empty);
        DefinitionComboBox.ItemsSource = reference.Definitions.Select(definition => new SourceDefinitionChoice(definition)).ToArray();
        DefinitionComboBox.SelectedIndex = reference.Definitions.Length > 0 ? 0 : -1;
        FollowReferenceButton.IsEnabled = false;
        try
        {
            CodeSourceDocument document = await CodeComparisonWindow.ReadWithLimitAsync(_reader, source, cancellation.Token);
            if (_closed || revision != _revision || cancellation.IsCancellationRequested) return;
            _document = document;
            FollowReferenceButton.IsEnabled = reference.Definitions.Length > 0;
            FindSourceButton.IsEnabled = InspectorOpenButton.IsEnabled = OriginalLocationButton.IsEnabled = true;
            RenderSource();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!_closed && revision == _revision) InspectorStatusTextBlock.Text = exception.Message;
        }
        finally
        {
            if (ReferenceEquals(_readCancellation, cancellation)) _readCancellation = null;
            cancellation.Dispose();
        }
    }

    private void FollowReferenceClicked(object sender, RoutedEventArgs e)
    {
        if (_document is not null && DefinitionComboBox.SelectedItem is SourceDefinitionChoice selected) _navigationTask = NavigateAsync(selected.Source, true, 0);
    }

    private void BackClicked(object sender, RoutedEventArgs e)
    {
        if (_back.Count == 0) return;
        CodeSourceEvidence source = _back[^1];
        _back.RemoveAt(_back.Count - 1);
        _navigationTask = NavigateAsync(source, false);
    }

    private void FindClicked(object sender, RoutedEventArgs e) => _searchTask = FindAsync();
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _searchTask = FindAsync();
    }

    private async Task FindAsync()
    {
        if (_closed || _document is not { } document) return;
        _searchCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _searchCancellation = cancellation;
        int revision = _revision;
        string query = SourceSearchTextBox.Text;
        SearchStatusTextBlock.Text = "Searching verified source…";
        NextMatchButton.IsEnabled = PreviousMatchButton.IsEnabled = false;
        try
        {
            CodeSourceSearchResult result = await Task.Run(() => CodeSourceSearch.Find(document.Lines, query, cancellation.Token), cancellation.Token);
            if (_closed || revision != _revision || cancellation.IsCancellationRequested) return;
            _search = result;
            _matchIndex = result.Matches.Length > 0 ? 0 : -1;
            NextMatchButton.IsEnabled = PreviousMatchButton.IsEnabled = result.Matches.Length > 1;
            if (_matchIndex >= 0) ShowMatch();
            else SearchStatusTextBlock.Text = query.Length == 0 ? "Enter text to search this source file." : "No matches in this verified source file.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ArgumentException exception) { if (!_closed && revision == _revision) SearchStatusTextBlock.Text = exception.Message; }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation)) _searchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PreviousMatchClicked(object sender, RoutedEventArgs e) => MoveMatch(-1);
    private void NextMatchClicked(object sender, RoutedEventArgs e) => MoveMatch(1);
    private void MoveMatch(int direction)
    {
        if (_search.Matches.Length == 0) return;
        _matchIndex = (_matchIndex + direction + _search.Matches.Length) % _search.Matches.Length;
        ShowMatch();
    }

    private void ShowMatch()
    {
        CodeSourceMatch match = _search.Matches[_matchIndex];
        _displayLine = match.Line;
        _contextLines = 3;
        SearchStatusTextBlock.Text = $"Match {_matchIndex + 1:N0} of {_search.Matches.Length:N0}{(_search.IsLimited ? "+ (search limit)" : string.Empty)} · line {match.Line:N0}, column {match.Column:N0}. Search results are source context.";
        if (match.Column + match.Length - 1 > CodeComparisonExcerpt.MaximumLineCharacters) SearchStatusTextBlock.Text += " The match is beyond this shortened preview; open the file at this line.";
        RenderSource();
    }

    private void RenderSource()
    {
        if (_document is null || _closed) return;
        CodeSourceEvidence shown = _displayLine is int line ? _source with { StartLine = line, EndLine = line, FocusStartLine = line, FocusEndLine = line, IsCompleteBlock = false, Description = "Search result" } : _source;
        CodeComparisonExcerpt excerpt = CodeComparisonExcerpt.Create(_document, shown, _contextLines, _lineLimit);
        CodeComparisonLine[] lines = excerpt.Lines.Select((text, index) => new CodeComparisonLine(excerpt.StartLine + index, text, 0, 0, false)).ToArray();
        CodeComparisonWindow.RenderPane(SourceCodeBox, InspectorStatusTextBlock, lines, shown, excerpt, null, false, false, false);
        if (excerpt.RangeLimited || excerpt.LinesShortened) InspectorStatusTextBlock.Text += " Preview is limited; open the file for the full source.";
        InspectorContextButton.IsEnabled = _lineLimit < 400 && (excerpt.StartLine > 1 || excerpt.EndLine < _document.Lines.Length);
        SourceCodeBox.ScrollToHome();
    }

    private void MoreContextClicked(object sender, RoutedEventArgs e)
    {
        _contextLines += 20;
        _lineLimit = Math.Min(400, _lineLimit + 80);
        RenderSource();
    }

    private void OriginalLocationClicked(object sender, RoutedEventArgs e)
    {
        _displayLine = null;
        _contextLines = _recordedContextLines;
        _lineLimit = 200;
        SearchStatusTextBlock.Text = "At the recorded source location.";
        RenderSource();
    }

    private void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SourceCodeBox is null || SourceFontSizeComboBox.SelectedItem is not ComboBoxItem selected) return;
        SourceCodeBox.FontSize = double.Parse((string)selected.Tag, CultureInfo.InvariantCulture);
        RenderSource();
    }

    private void OpenAtLineClicked(object sender, RoutedEventArgs e)
    {
        if (_document is null) return;
        try { InspectorStatusTextBlock.Text = SourceFileActions.OpenAtLine(new(_source.Provider, _source.FilePath, _source.PhysicalPath), _displayLine ?? _source.FocusStartLine, _editorPreferences.Load()).Status; }
        catch (Exception exception) { InspectorStatusTextBlock.Text = exception.Message; }
    }

    private void EditorSettingsClicked(object sender, RoutedEventArgs e) => new SourceEditorSettingsWindow(this, _editorPreferences).ShowDialog();

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _revision++;
        _readCancellation?.Cancel();
        _searchCancellation?.Cancel();
        _document = null;
        _back.Clear();
        _search = new([], false);
        SourceCodeBox.Document = new FlowDocument();
        base.OnClosed(e);
    }
}
