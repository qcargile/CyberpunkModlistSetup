using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;

namespace ConflictStudio.App;

public sealed record ArchiveOverviewEntry(string ArchiveName, bool SelectedWins, bool SelectedLoses, bool HasSame, bool HasUnknown, int Position, int TotalCount, double? TrackRatio = null, double? TrackSizeRatio = null);

public sealed class ArchiveOverviewBar : FrameworkElement
{
    private static readonly Brush BackgroundBrush = CreateBrush(9, 15, 20);
    private static readonly Brush WinningBrush = ArchiveRelationshipBrushes.WinsBorder;
    private static readonly Brush LosingBrush = ArchiveRelationshipBrushes.LosesBorder;
    private static readonly Brush SameBrush = ArchiveRelationshipBrushes.SameBorder;
    private static readonly Brush UnknownBrush = ArchiveRelationshipBrushes.UnknownBorder;
    private static readonly Brush SelectionBrush = Brushes.White;
    private IReadOnlyList<ArchiveOverviewEntry> _entries = [];
    private string[] _selectedArchives = [];

    internal static Color SelectionMarkerColor => ((SolidColorBrush)SelectionBrush).Color;

    public IReadOnlyList<ArchiveOverviewEntry> Entries
    {
        get => _entries;
        set
        {
            ArchiveOverviewAutomationPeer? peer = UIElementAutomationPeer.FromElement(this) as ArchiveOverviewAutomationPeer;
            string? previousValue = peer?.Value;
            _entries = value ?? [];
            InvalidateVisual();
            if (peer is not null && previousValue is not null) peer.RaiseValueChanged(previousValue, peer.Value);
        }
    }

    public string? SelectedArchive
    {
        get => _selectedArchives.Length == 0 ? null : _selectedArchives[0];
        set => SelectedArchives = value is null ? [] : [value];
    }

    public IReadOnlyList<string> SelectedArchives
    {
        get => _selectedArchives;
        set
        {
            ArchiveOverviewAutomationPeer? peer = UIElementAutomationPeer.FromElement(this) as ArchiveOverviewAutomationPeer;
            string? previousValue = peer?.Value;
            _selectedArchives = value?.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            InvalidateVisual();
            if (peer is not null && previousValue is not null) peer.RaiseValueChanged(previousValue, peer.Value);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        drawingContext.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_entries.Count == 0) return;
        int totalCount = Math.Max(1, _entries.Max(value => value.TotalCount));
        for (int index = 0; index < _entries.Count; index++)
        {
            ArchiveOverviewEntry entry = _entries[index];
            Rect marker = MarkerBounds(entry, ActualHeight, totalCount);
            Brush[] brushes = entry.HasUnknown ? [UnknownBrush] : RelationshipBrushes(entry);
            double width = Math.Max(2, ActualWidth - 4);
            for (int brushIndex = 0; brushIndex < brushes.Length; brushIndex++) drawingContext.DrawRectangle(brushes[brushIndex], null, new Rect(2 + brushIndex * width / brushes.Length, marker.Y, width / brushes.Length, marker.Height));
            if (_selectedArchives.Contains(entry.ArchiveName, StringComparer.OrdinalIgnoreCase)) drawingContext.DrawRectangle(SelectionBrush, null, new Rect(0, Math.Max(0, marker.Y - 1), ActualWidth, 3));
        }
    }

    public static Rect MarkerBounds(ArchiveOverviewEntry entry, double trackHeight, int totalCount)
    {
        double markerHeight = Math.Max(1, entry.TrackSizeRatio is double sizeRatio ? sizeRatio * trackHeight : trackHeight / Math.Max(1, totalCount));
        double ratio = entry.TrackRatio ?? (entry.TotalCount <= 1 ? 0 : (double)entry.Position / (entry.TotalCount - 1));
        double y = entry.TrackRatio is null ? ratio * (trackHeight - markerHeight) : Math.Clamp(ratio * trackHeight, 0, Math.Max(0, trackHeight - markerHeight));
        return new Rect(0, y, 0, markerHeight);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ArchiveOverviewAutomationPeer(this);

    private static SolidColorBrush CreateBrush(byte red, byte green, byte blue)
    {
        SolidColorBrush brush = new(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static Brush[] RelationshipBrushes(ArchiveOverviewEntry entry)
    {
        List<Brush> brushes = [];
        if (entry.SelectedWins) brushes.Add(WinningBrush);
        if (entry.SelectedLoses) brushes.Add(LosingBrush);
        if (entry.HasSame) brushes.Add(SameBrush);
        return brushes.ToArray();
    }
}

internal sealed class ArchiveOverviewAutomationPeer : FrameworkElementAutomationPeer, IValueProvider
{
    private ArchiveOverviewBar OwnerBar => (ArchiveOverviewBar)Owner;

    public ArchiveOverviewAutomationPeer(ArchiveOverviewBar owner) : base(owner) { }

    public bool IsReadOnly => true;
    public string Value => Describe();

    public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

    public void SetValue(string value) => throw new InvalidOperationException("The archive overview is read-only. Use arrow keys or click to select an archive.");

    public void RaiseValueChanged(string previous, string current)
    {
        if (!string.Equals(previous, current, StringComparison.Ordinal)) RaisePropertyChangedEvent(ValuePatternIdentifiers.ValueProperty, previous, current);
    }

    private string Describe()
    {
        if (OwnerBar.SelectedArchives.Count == 0) return "No archive selected";
        List<string> parts = ["Selected: " + string.Join(", ", OwnerBar.SelectedArchives.Select(DescribePosition))];
        AddRelationships(parts, "Selection wins over", OwnerBar.Entries.Where(value => value.SelectedWins));
        AddRelationships(parts, "Selection loses to", OwnerBar.Entries.Where(value => value.SelectedLoses));
        AddRelationships(parts, "Matching file content", OwnerBar.Entries.Where(value => value.HasSame));
        AddRelationships(parts, "Cannot tell which archive wins", OwnerBar.Entries.Where(value => value.HasUnknown));
        return string.Join(". ", parts) + ".";
    }

    private string DescribePosition(string archiveName)
    {
        ArchiveOverviewEntry? entry = OwnerBar.Entries.FirstOrDefault(value => string.Equals(value.ArchiveName, archiveName, StringComparison.OrdinalIgnoreCase));
        return entry is null ? archiveName : $"{archiveName}, position {entry.Position + 1} of {entry.TotalCount}";
    }

    private static void AddRelationships(List<string> parts, string label, IEnumerable<ArchiveOverviewEntry> entries)
    {
        string[] names = entries.Where(value => !string.IsNullOrWhiteSpace(value.ArchiveName)).Select(value => value.ArchiveName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length > 0) parts.Add(label + ": " + string.Join(", ", names));
    }
}
