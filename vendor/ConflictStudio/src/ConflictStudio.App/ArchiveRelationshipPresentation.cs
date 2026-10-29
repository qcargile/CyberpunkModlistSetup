using System.ComponentModel;
using ConflictStudio.Core;

namespace ConflictStudio.App;

public enum ArchiveRelationshipTone { None, Wins, Loses, Mixed, Same, Unknown }
public enum ArchiveDropCue { None, Before, After }

public sealed class ArchiveRailItem : INotifyPropertyChanged
{
    private ArchiveRelationshipTone _relationshipTone;
    private ArchiveDropCue _dropCue;

    public ArchiveRailItem(string archiveName) => ArchiveName = archiveName;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ArchiveName { get; }

    public ArchiveRelationshipTone RelationshipTone
    {
        get => _relationshipTone;
        set
        {
            if (_relationshipTone == value) return;
            _relationshipTone = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RelationshipTone)));
        }
    }

    public ArchiveDropCue DropCue
    {
        get => _dropCue;
        set
        {
            if (_dropCue == value) return;
            _dropCue = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DropCue)));
        }
    }
}

public static class ArchiveDropCueProjection
{
    public static ArchiveDropCue For(IReadOnlyList<string> order, IReadOnlyList<string> moving, string target)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(moving);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        HashSet<string> selected = moving.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || selected.Contains(target)) return ArchiveDropCue.None;
        int targetIndex = IndexOf(order, target);
        int[] positions = order.Select((value, index) => (value, index)).Where(value => selected.Contains(value.value)).Select(value => value.index).ToArray();
        if (targetIndex < 0 || positions.Length == 0) return ArchiveDropCue.None;
        if (positions.All(value => value < targetIndex)) return ArchiveDropCue.After;
        if (positions.All(value => value > targetIndex)) return ArchiveDropCue.Before;
        return ArchiveDropCue.None;
    }

    private static int IndexOf(IReadOnlyList<string> values, string target)
    {
        for (int index = 0; index < values.Count; index++) if (string.Equals(values[index], target, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }
}

public static class ArchiveRelationshipPresentation
{
    public static void Apply(IReadOnlyList<ArchiveRailItem> rail, IReadOnlyList<ArchiveConflictNode> tree, IReadOnlyList<ArchiveOverviewEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(rail);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(entries);
        Dictionary<string, ArchiveRelationshipTone> tones = entries.ToDictionary(value => value.ArchiveName, Tone, StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveRailItem row in rail) row.RelationshipTone = tones.GetValueOrDefault(row.ArchiveName);
        foreach (ArchiveConflictNode row in tree) row.RelationshipTone = tones.GetValueOrDefault(row.ArchiveName);
    }

    public static ArchiveRelationshipTone Tone(ArchiveOverviewEntry entry)
        => (ArchiveRelationshipTone)ArchiveRelationship.Classify(entry.SelectedWins, entry.SelectedLoses, entry.HasSame, entry.HasUnknown);
}
