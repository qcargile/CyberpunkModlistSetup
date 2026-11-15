using ConflictStudio.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConflictStudio.App;

public sealed record CodeSourceChoice(CodeFindingParticipant Participant, CodeSourceEvidence? Evidence)
{
    public override string ToString() => $"{Path.GetFileName(Evidence?.FilePath ?? Participant.FilePath)}:{Evidence?.FocusStartLine ?? Participant.Line} ({Participant.Provider}) | {Describe(Participant, 48)}" + (Evidence?.OperationOccurrence > 1 ? $" / occurrence {Evidence.OperationOccurrence}" : string.Empty);

    internal static string Describe(CodeFindingParticipant participant, int limit)
    {
        string? value = participant.Role is CodeFindingParticipantRole.Replacement or CodeFindingParticipantRole.AddedMethod or CodeFindingParticipantRole.StopsContinuation or CodeFindingParticipantRole.SharedFlow
            ? null : participant.NormalizedValue ?? participant.ValueType ?? participant.Member;
        if (string.IsNullOrWhiteSpace(value)) return participant.RoleLabel;
        if (value.Length > limit)
        {
            if (char.IsHighSurrogate(value[limit - 1])) limit--;
            value = value[..limit] + "…";
        }
        return participant.RoleLabel + ": " + value;
    }
}

public sealed record CodeWitnessChoice(CodeFindingWitness Witness)
{
    public override string ToString() => Witness.Title;
}

public partial class CodeComparisonWindow : Window
{
    private const int InitialLineLimit = 160;
    private const int MaximumLineLimit = 400;
    private static readonly SemaphoreSlim SourceReadSlots = new(2, 2);
    private readonly Func<CodeSourceEvidence, CancellationToken, Task<CodeSourceDocument>> _reader;
    private readonly string _findingSummary;
    private CodeFindingWitness? _witness;
    private Dictionary<string, CodeSourceDocument> _documents = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _loadCancellation;
    private CodeSourceDocument? _leftDocument;
    private CodeSourceDocument? _rightDocument;
    private string? _leftError;
    private string? _rightError;
    private bool _initializing = true;
    private bool _closed;
    private bool _syncingScroll;
    private int _revision;
    private int _contextLines = 3;
    private int _lineLimit = InitialLineLimit;

    public CodeComparisonWindow(Window owner, ConflictWorkItem item, IReadOnlyList<CodeFindingWitness> comparisons, IReadOnlyList<CodeSourceEvidence>? contributors = null, TweakReferenceIndex? references = null, SourceEditorPreferenceStore? editorPreferences = null)
        : this(item, comparisons, CodeSourceReader.ReadAsync, contributors, references, editorPreferences)
    {
        Owner = owner;
        Resources.MergedDictionaries.Add(owner.Resources);
    }

    internal CodeComparisonWindow(ConflictWorkItem item, IReadOnlyList<CodeFindingWitness> comparisons, Func<CodeSourceEvidence, CancellationToken, Task<CodeSourceDocument>> reader, IReadOnlyList<CodeSourceEvidence>? contributors = null, TweakReferenceIndex? references = null, SourceEditorPreferenceStore? editorPreferences = null)
    {
        _reader = reader;
        _findingSummary = item.Summary;
        InitializeComponent();
        InitializeSourceNavigation(item, contributors ?? comparisons.SelectMany(value => value.Participants).SelectMany(value => value.Sources).ToArray(), references, editorPreferences);
        TargetTextBlock.Text = item.Target;
        WitnessComboBox.ItemsSource = comparisons.Select(value => new CodeWitnessChoice(value)).ToArray();
        WitnessComboBox.Visibility = comparisons.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        WitnessComboBox.SelectedIndex = comparisons.Count > 0 ? 0 : -1;
        SelectWitness();
        _initializing = false;
        Loaded += ComparisonLoaded;
        LeftCodeBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(LeftCodeScrolled));
        RightCodeBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(RightCodeScrolled));
    }

    private async void ComparisonLoaded(object sender, RoutedEventArgs e) => await LoadSelectionAsync();

    private static CodeSourceChoice[] Choices(IEnumerable<CodeFindingParticipant> participants)
    {
        return participants.SelectMany(participant => participant.Sources.Length == 0
            ? [new CodeSourceChoice(participant, null)]
            : participant.Sources.OrderByDescending(value => value.IsCompleteBlock).ThenBy(value => value.StartLine).Select(value => new CodeSourceChoice(participant, value))).ToArray();
    }

    private void SelectWitness()
    {
        _witness = (WitnessComboBox.SelectedItem as CodeWitnessChoice)?.Witness;
        WitnessTitleTextBlock.Text = _witness?.Title ?? "No supporting comparison is available";
        ExplanationTextBlock.Text = _witness?.Description ?? _findingSummary;
        ExplanationTextBlock.ToolTip = _findingSummary;
        BoundaryTextBlock.Text = _witness?.Boundary ?? "Run a fresh scan or open the finding's files.";
        SourcesTextBlock.Text = _witness is null ? string.Empty : $"{_witness.Participants.Length:N0} operations supporting this disagreement";
        LeftSourceComboBox.ItemsSource = Choices(_witness?.Participants ?? []);
        LeftSourceComboBox.SelectedIndex = LeftSourceComboBox.Items.Count > 0 ? 0 : -1;
        SelectOpponents();
        RefreshContributors();
    }

    private void SelectOpponents()
    {
        CodeSourceChoice? previous = RightSourceComboBox.SelectedItem as CodeSourceChoice;
        CodeSourceChoice? left = LeftSourceComboBox.SelectedItem as CodeSourceChoice;
        CodeSourceChoice[] choices = left is null || _witness is null ? [] : Choices(_witness.OpponentsFor(left.Participant));
        RightSourceComboBox.ItemsSource = choices;
        RightSourceComboBox.SelectedItem = choices.FirstOrDefault(value => value.Participant.OperationId == previous?.Participant.OperationId && value.Evidence == previous?.Evidence) ?? choices.FirstOrDefault();
    }

    private async void WitnessSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _closed) return;
        _initializing = true;
        SelectWitness();
        ContextDifferencesCheckBox.IsChecked = false;
        _initializing = false;
        _contextLines = 3;
        _lineLimit = InitialLineLimit;
        await LoadSelectionAsync();
    }

    private void ContextHighlightChanged(object sender, RoutedEventArgs e)
    {
        if (!_initializing && !_closed) RenderSources();
    }

    private async void SourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _closed) return;
        if (ReferenceEquals(sender, LeftSourceComboBox))
        {
            _initializing = true;
            SelectOpponents();
            _initializing = false;
        }
        _contextLines = 3;
        _lineLimit = InitialLineLimit;
        await LoadSelectionAsync();
    }

    internal async Task LoadSelectionAsync()
    {
        if (_closed) return;
        _loadCancellation?.Cancel();
        CancellationTokenSource cancellation = new();
        _loadCancellation = cancellation;
        int revision = ++_revision;
        CodeSourceChoice? leftChoice = LeftSourceComboBox.SelectedItem as CodeSourceChoice;
        CodeSourceChoice? rightChoice = RightSourceComboBox.SelectedItem as CodeSourceChoice;
        CodeSourceEvidence? left = leftChoice?.Evidence;
        CodeSourceEvidence? right = rightChoice?.Evidence;
        LeftRoleTextBlock.Text = ParticipantHeading(leftChoice?.Participant);
        RightRoleTextBlock.Text = ParticipantHeading(rightChoice?.Participant);
        LeftSourceComboBox.ToolTip = leftChoice?.ToString();
        RightSourceComboBox.ToolTip = rightChoice?.ToString();
        ScopeTextBlock.Text = leftChoice is not null && rightChoice is not null && string.Equals(leftChoice.Participant.Provider, rightChoice.Participant.Provider, StringComparison.OrdinalIgnoreCase)
            ? $"Both operations are within {leftChoice.Participant.Provider}. Their presence alone does not establish an unintended problem."
            : string.Empty;
        _leftDocument = null;
        _rightDocument = null;
        _leftError = null;
        _rightError = null;
        LeftCodeBox.Document = new FlowDocument();
        RightCodeBox.Document = new FlowDocument();
        LeftPathTextBox.Text = left?.PhysicalPath ?? string.Empty;
        RightPathTextBox.Text = right?.PhysicalPath ?? string.Empty;
        LeftStatusTextBlock.Text = left is null ? MissingSource(leftChoice) : "Verifying source against the scan…";
        RightStatusTextBlock.Text = right is null ? MissingSource(rightChoice) : "Verifying source against the scan…";
        MoreContextButton.IsEnabled = false;
        LeftOpenButton.IsEnabled = LeftFolderButton.IsEnabled = LeftCopyButton.IsEnabled = left is not null;
        RightOpenButton.IsEnabled = RightFolderButton.IsEnabled = RightCopyButton.IsEnabled = right is not null;
        ComparisonStatusTextBlock.Text = "Reading only the selected source files.";
        Dictionary<string, Task<(CodeSourceDocument? Document, string? Error)>> reads = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CodeSourceDocument> previous = _documents;
        _documents = new Dictionary<string, CodeSourceDocument>(StringComparer.OrdinalIgnoreCase);
        Task<(CodeSourceDocument? Document, string? Error)> Read(CodeSourceEvidence? source)
        {
            if (source is null) return Task.FromResult<(CodeSourceDocument?, string?)>((null, null));
            string key = SourceKey(source);
            if (reads.TryGetValue(key, out Task<(CodeSourceDocument? Document, string? Error)>? pending)) return pending;
            Task<(CodeSourceDocument? Document, string? Error)> task = previous.TryGetValue(key, out CodeSourceDocument? cached)
                ? Task.FromResult<(CodeSourceDocument?, string?)>((cached, null)) : ReadSourceAsync(source, cancellation.Token);
            reads.Add(key, task);
            return task;
        }
        try
        {
            Task<(CodeSourceDocument? Document, string? Error)> leftRead = Read(left);
            Task<(CodeSourceDocument? Document, string? Error)> rightRead = Read(right);
            await Task.WhenAll(leftRead, rightRead);
            if (_closed || revision != _revision || cancellation.IsCancellationRequested) return;
            (_leftDocument, _leftError) = await leftRead;
            (_rightDocument, _rightError) = await rightRead;
            if (left is null && leftChoice is not null) _leftError = MissingSource(leftChoice);
            if (right is null && rightChoice is not null) _rightError = MissingSource(rightChoice);
            if (left is not null && _leftDocument is not null) _documents[SourceKey(left)] = _leftDocument;
            if (right is not null && _rightDocument is not null) _documents[SourceKey(right)] = _rightDocument;
            RenderSources();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<(CodeSourceDocument? Document, string? Error)> ReadSourceAsync(CodeSourceEvidence evidence, CancellationToken cancellationToken)
    {
        try { return (await ReadWithLimitAsync(_reader, evidence, cancellationToken), null); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, exception.Message);
        }
    }

    internal static async Task<CodeSourceDocument> ReadWithLimitAsync(Func<CodeSourceEvidence, CancellationToken, Task<CodeSourceDocument>> reader, CodeSourceEvidence evidence, CancellationToken cancellationToken)
    {
        await SourceReadSlots.WaitAsync(cancellationToken);
        try { return await Task.Run(() => reader(evidence, cancellationToken), cancellationToken); }
        finally { SourceReadSlots.Release(); }
    }

    private static string MissingSource(CodeSourceChoice? choice)
        => choice is null ? "No supporting counterpart was recorded." : "Exact source was not recorded for this operation. Run a fresh scan or use the main window's file actions.";

    private static string ParticipantHeading(CodeFindingParticipant? participant)
        => participant is null ? "Source unavailable" : CodeSourceChoice.Describe(participant, 160);

    private void MoreContextClicked(object sender, RoutedEventArgs e)
    {
        _contextLines += 10;
        _lineLimit = Math.Min(MaximumLineLimit, _lineLimit + 80);
        RenderSources();
    }

    private void ResetContextClicked(object sender, RoutedEventArgs e)
    {
        if (_loadCancellation is not null) return;
        _contextLines = 3;
        _lineLimit = InitialLineLimit;
        RenderSources();
    }

    private void RenderSources()
    {
        CodeSourceEvidence? left = (LeftSourceComboBox.SelectedItem as CodeSourceChoice)?.Evidence;
        CodeSourceEvidence? right = (RightSourceComboBox.SelectedItem as CodeSourceChoice)?.Evidence;
        CodeComparisonExcerpt? leftExcerpt = _leftDocument is not null && left is not null ? CodeComparisonExcerpt.Create(_leftDocument, left, _contextLines, _lineLimit) : null;
        CodeComparisonExcerpt? rightExcerpt = _rightDocument is not null && right is not null ? CodeComparisonExcerpt.Create(_rightDocument, right, _contextLines, _lineLimit) : null;
        CodeComparisonLines comparison = leftExcerpt is not null && rightExcerpt is not null && left is not null && right is not null
            ? CodeComparisonDiff.Compare(leftExcerpt.Lines, leftExcerpt.StartLine, rightExcerpt.Lines, rightExcerpt.StartLine, left.FocusStartLine, left.FocusEndLine, right.FocusStartLine, right.FocusEndLine)
            : new CodeComparisonLines(UnpairedLines(leftExcerpt), UnpairedLines(rightExcerpt), false);
        bool showContextDifferences = ContextDifferencesCheckBox.IsChecked == true;
        bool declarationEvidence = _witness?.IsDuplicateDeclaration == true || _witness?.Kind == CodeFindingWitnessKind.ExclusiveReplacement;
        bool operationEvidence = _witness?.Kind is CodeFindingWitnessKind.OpposingArrayMutation or CodeFindingWitnessKind.ArrayInsertionOrder;
        RenderPane(LeftCodeBox, LeftStatusTextBlock, comparison.Left, left, leftExcerpt, _leftError, showContextDifferences, declarationEvidence, operationEvidence);
        RenderPane(RightCodeBox, RightStatusTextBlock, comparison.Right, right, rightExcerpt, _rightError, showContextDifferences, declarationEvidence, operationEvidence);
        MoreContextButton.IsEnabled = _lineLimit < MaximumLineLimit && (HasMore(_leftDocument, leftExcerpt) || HasMore(_rightDocument, rightExcerpt));
        List<string> notes = ["Read-only source, verified when loaded. No files are modified."];
        if (showContextDifferences) notes.Add("Blue marks surrounding text differences, not additional conflicts.");
        if (_leftError is not null || _rightError is not null) notes[0] = "One or more sources could not be verified. Available sources remain visible.";
        if (leftExcerpt is not null && rightExcerpt is not null && !comparison.Left.Any(value => value.IsDifferent) && !comparison.Right.Any(value => value.IsDifferent)) notes.Add(_witness?.IsDuplicateDeclaration == true
            ? "Both locations contain the repeated declaration or addition; identical text does not remove that duplication."
            : "No text difference is visible in this preview. The operation and reported value are identified above each pane.");
        if (comparison.LineAlignmentLimited) notes.Add("Long excerpts are compared line by line without automatic alignment.");
        if (leftExcerpt?.RangeLimited == true || rightExcerpt?.RangeLimited == true) notes.Add("Only part of the source block is displayed.");
        if (leftExcerpt?.LinesShortened == true || rightExcerpt?.LinesShortened == true) notes.Add("Long lines are shortened in this preview.");
        if (_lineLimit == MaximumLineLimit) notes.Add("Preview limit reached. Open the file to inspect further context.");
        ComparisonStatusTextBlock.Text = string.Join(" ", notes);
    }

    private static bool HasMore(CodeSourceDocument? document, CodeComparisonExcerpt? excerpt)
        => document is not null && excerpt is not null && (excerpt.StartLine > 1 || excerpt.EndLine < document.Lines.Length);

    private static CodeComparisonLine[] UnpairedLines(CodeComparisonExcerpt? excerpt)
        => excerpt?.Lines.Select((line, index) => new CodeComparisonLine(excerpt.StartLine + index, line, 0, 0, false)).ToArray() ?? [];

    internal static void RenderPane(RichTextBox editor, TextBlock status, CodeComparisonLine[] lines, CodeSourceEvidence? evidence, CodeComparisonExcerpt? excerpt, string? error, bool showContextDifferences, bool declarationEvidence, bool operationEvidence)
    {
        double textSize = Math.Clamp(editor.FontSize, 10, 24);
        FlowDocument document = new() { PagePadding = new Thickness(8), FontFamily = new FontFamily("Consolas"), FontSize = textSize };
        double width = Math.Max(300, editor.ActualWidth - 20);
        foreach (CodeComparisonLine line in lines)
        {
            Paragraph paragraph = new() { Margin = new Thickness(0), LineHeight = textSize + 7 };
            string number = line.LineNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            bool focus = line.LineNumber is int value && evidence is not null && value >= evidence.FocusStartLine && value <= evidence.FocusEndLine;
            paragraph.Inlines.Add(new Run(number.PadLeft(6) + "  ") { Foreground = focus ? Brushes.Cyan : Brushes.SlateGray });
            if (line.LineNumber is null)
            {
                if (showContextDifferences) paragraph.Background = new SolidColorBrush(Color.FromRgb(17, 27, 34));
            }
            else if (declarationEvidence && evidence is not null && line.LineNumber >= evidence.StartLine && line.LineNumber <= evidence.FocusStartLine)
            {
                paragraph.Inlines.Add(new Run(line.Text) { Background = new SolidColorBrush(Color.FromRgb(13, 61, 70)), Foreground = Brushes.White });
            }
            else if (operationEvidence && focus)
            {
                paragraph.Inlines.Add(new Run(line.Text) { Background = new SolidColorBrush(Color.FromRgb(88, 61, 14)), Foreground = new SolidColorBrush(Color.FromRgb(255, 216, 132)) });
            }
            else if (line.IsDifferent && line.DifferenceLength > 0 && (showContextDifferences || focus && !declarationEvidence))
            {
                bool context = !focus || declarationEvidence;
                paragraph.Inlines.Add(new Run(line.Text[..line.DifferenceStart]));
                paragraph.Inlines.Add(new Run(line.Text.Substring(line.DifferenceStart, line.DifferenceLength)) { Background = new SolidColorBrush(context ? Color.FromRgb(32, 59, 88) : Color.FromRgb(88, 61, 14)), Foreground = new SolidColorBrush(context ? Color.FromRgb(202, 233, 255) : Color.FromRgb(255, 216, 132)) });
                paragraph.Inlines.Add(new Run(line.Text[(line.DifferenceStart + line.DifferenceLength)..]));
            }
            else paragraph.Inlines.Add(new Run(line.Text));
            document.Blocks.Add(paragraph);
            FormattedText measured = new(number.PadLeft(6) + "  " + line.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Consolas"), textSize, Brushes.White, VisualTreeHelper.GetDpi(editor).PixelsPerDip);
            width = Math.Max(width, measured.WidthIncludingTrailingWhitespace + 24);
        }
        document.PageWidth = width;
        editor.Document = document;
        status.Text = error ?? (evidence is null ? "No source selected." : excerpt is null ? "Source is not available." : $"Lines {excerpt.StartLine}-{excerpt.EndLine}. {evidence.Description} {(evidence.IsCompleteBlock ? "" : "Context excerpt; a complete block was not established.")}");
        status.Foreground = error is null ? new SolidColorBrush(Color.FromRgb(142, 162, 173)) : new SolidColorBrush(Color.FromRgb(255, 209, 102));
    }

    private void LeftCodeScrolled(object sender, ScrollChangedEventArgs e) => SyncScroll(LeftCodeBox, RightCodeBox, e);
    private void RightCodeScrolled(object sender, ScrollChangedEventArgs e) => SyncScroll(RightCodeBox, LeftCodeBox, e);

    private void SyncScroll(RichTextBox source, RichTextBox other, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || e.VerticalChange == 0) return;
        _syncingScroll = true;
        try { other.ScrollToVerticalOffset(source.VerticalOffset); }
        finally { _syncingScroll = false; }
    }

    private CodeSourceEvidence? SourceFor(object sender)
        => (((Button)sender).Tag as string == "Left" ? LeftSourceComboBox : RightSourceComboBox).SelectedItem is CodeSourceChoice choice ? choice.Evidence : null;

    private void OpenFileClicked(object sender, RoutedEventArgs e)
    {
        CodeSourceEvidence? source = SourceFor(sender);
        if (source is null) return;
        FileAction(sender, file => ComparisonStatusTextBlock.Text = SourceFileActions.OpenAtLine(file, source.FocusStartLine, _editorPreferences.Load()).Status);
    }
    private void ShowFolderClicked(object sender, RoutedEventArgs e) => FileAction(sender, SourceFileActions.ShowInFolder);
    private void CopyPathClicked(object sender, RoutedEventArgs e) => FileAction(sender, file => Clipboard.SetText(file.PhysicalPath!));

    private void FileAction(object sender, Action<SourceFileLocation> action)
    {
        CodeSourceEvidence? source = SourceFor(sender);
        if (source is null) return;
        try { action(new SourceFileLocation(source.Provider, source.FilePath, source.PhysicalPath)); }
        catch (Exception exception) { ComparisonStatusTextBlock.Text = exception.Message; }
    }

    private static string SourceKey(CodeSourceEvidence evidence) => evidence.PhysicalPath + "\0" + evidence.SourceSha256;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _revision++;
        _loadCancellation?.Cancel();
        _sourceInspector?.Close();
        _documents.Clear();
        _leftDocument = null;
        _rightDocument = null;
        LeftCodeBox.Document = new FlowDocument();
        RightCodeBox.Document = new FlowDocument();
        base.OnClosed(e);
    }
}
