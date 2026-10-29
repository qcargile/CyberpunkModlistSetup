using ConflictStudio.Core;
using System.ComponentModel;

namespace ConflictStudio.App;

public enum ArchiveTreeTone { Winning, Losing, Same, Unknown, Neutral }

public sealed record ArchiveResourceNode(ArchiveResourceOutcome Outcome, ArchiveTreeTone Tone, string ArchiveName = "")
{
    public string Path => Outcome.DisplayName;
    public string[] OtherArchives => (Outcome.OtherArchives ?? []).Where(value => !string.Equals(value, ArchiveName, StringComparison.OrdinalIgnoreCase)).ToArray();
    public string ProviderContext => Tone switch
    {
        ArchiveTreeTone.Winning => OtherArchives.Length == 0 ? "Effective file" : "Overrides: " + string.Join(", ", OtherArchives),
        ArchiveTreeTone.Losing => "Winner: " + (Outcome.WinnerArchive ?? "can't determine"),
        ArchiveTreeTone.Same => "Identical copy in: " + string.Join(", ", OtherArchives),
        ArchiveTreeTone.Unknown => "Winner cannot be determined",
        _ => "Only in this archive"
    };
    public string PlainMeaning => Outcome.PayloadRelation == ArchivePayloadRelation.Identical ? "This file has exactly the same content as the winning copy. Copies in other archives may still differ." : Outcome.Disposition switch
    {
        ArchiveResourceDisposition.Winning => "Cyberpunk uses this archive's file. Lower archives are ignored for this resource.",
        ArchiveResourceDisposition.Losing => $"Cyberpunk uses {Outcome.WinnerArchive ?? "a higher archive"} instead of this archive's file.",
        ArchiveResourceDisposition.WinningAndLosing => $"{Outcome.WinnerArchive ?? "A higher archive"} wins overall. This archive still overrides lower archives.",
        ArchiveResourceDisposition.Unresolved when Outcome.WinnerArchive is not null => $"The winning file comes from {Outcome.WinnerArchive}, but Conflict Studio cannot identify which archive inside it contains that file.",
        ArchiveResourceDisposition.Unresolved => "The winner cannot be determined until the named archive problem is fixed.",
        _ => "Only this archive contains the resource, so there is no conflict."
    };
    public string TechnicalEvidence => $"Cooked SHA-256: {Outcome.CookedPayloadSha256 ?? "unavailable"}\nRDAR SHA-1: {Outcome.RdarPayloadSha1 ?? "unavailable"}\nResource type: {Outcome.ResourceType ?? "unknown"}\nPath evidence: {Outcome.PathConfidence}";
}

public sealed record ArchiveConflictGroupNode(string Header, ArchiveTreeTone Tone, IReadOnlyList<ArchiveResourceNode> Children);

public sealed class ArchiveConflictNode : INotifyPropertyChanged
{
    private ArchiveRelationshipTone _relationshipTone;

    public ArchiveConflictNode(ArchiveConflictSummary summary, IReadOnlyList<ArchiveConflictGroupNode> children)
    {
        Summary = summary;
        Children = children;
    }

    public ArchiveConflictSummary Summary { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<ArchiveConflictGroupNode> Children { get; }
    public string ArchiveName => Summary.ArchiveName;
    public string Provider => Summary.Provider;
    public int OrderPosition => (Summary.OrderPosition ?? -1) + 1;
    public int WinningCount => Children.Where(value => value.Tone == ArchiveTreeTone.Winning).Sum(value => value.Children.Count);
    public int LosingCount => Children.Where(value => value.Tone == ArchiveTreeTone.Losing).Sum(value => value.Children.Count);
    public int SameCount => Children.Where(value => value.Tone == ArchiveTreeTone.Same).Sum(value => value.Children.Count);
    public int UnknownCount => Children.Where(value => value.Tone == ArchiveTreeTone.Unknown).Sum(value => value.Children.Count);
    public int UniqueCount => Children.Where(value => value.Tone == ArchiveTreeTone.Neutral).Sum(value => value.Children.Count);
    public string CountSummary
    {
        get
        {
            List<string> counts = [];
            if (WinningCount > 0) counts.Add(WinningCount == 1 ? "1 win" : $"{WinningCount} wins");
            if (LosingCount > 0) counts.Add(LosingCount == 1 ? "1 loss" : $"{LosingCount} losses");
            if (SameCount > 0) counts.Add($"{SameCount} same");
            if (UnknownCount > 0) counts.Add(UnknownCount == 1 ? "1 can't determine" : $"{UnknownCount} can't determine");
            if (UniqueCount > 0) counts.Add($"{UniqueCount} unique");
            return counts.Count == 0 ? "No conflicts" : string.Join(" · ", counts);
        }
    }
    public string? PhysicalPath => Summary.PhysicalPath;
    public bool HasWinning => WinningCount > 0;
    public bool HasLosing => LosingCount > 0;
    public bool HasSame => SameCount > 0;
    public bool HasUnknown => UnknownCount > 0;
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
}

public sealed class ArchiveConflictTreeViewModel
{
    private ArchiveConflictSummary[] _summaries = [];

    public IReadOnlyList<ArchiveConflictNode> VisibleArchives { get; private set; } = [];
    public string ResultSummary { get; private set; } = "Run a profile scan to find archive conflicts.";

    public void Load(IReadOnlyList<ArchiveConflictSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        _summaries = summaries.ToArray();
        VisibleArchives = [];
        ResultSummary = "Run a profile scan to find archive conflicts.";
    }

    public void Filter(string modQuery, string fileQuery, bool showNoConflicts, bool onlyConflictingArchives = false)
    {
        string modSearch = modQuery?.Trim() ?? string.Empty;
        string fileSearch = fileQuery?.Trim() ?? string.Empty;
        List<ArchiveConflictNode> archives = [];
        foreach (ArchiveConflictSummary summary in _summaries.OrderBy(value => value.OrderPosition ?? int.MaxValue))
        {
            if (onlyConflictingArchives && summary.Winning.Length == 0 && summary.Losing.Length == 0 && summary.Redundant.Length == 0 && summary.Unresolved.Length == 0) continue;
            if (modSearch.Length > 0 && !summary.ArchiveName.Contains(modSearch, StringComparison.OrdinalIgnoreCase) && !summary.Provider.Contains(modSearch, StringComparison.OrdinalIgnoreCase)) continue;
            ArchiveConflictGroupNode[] groups = Groups(summary, fileSearch, showNoConflicts);
            if (groups.Length == 0) continue;
            archives.Add(new ArchiveConflictNode(summary, groups));
        }
        VisibleArchives = archives;
        int resources = archives.SelectMany(value => value.Children).SelectMany(value => value.Children).Select(value => value.Outcome.ResourceHash).Distinct().Count();
        ResultSummary = $"Found {resources:N0} matching file{(resources == 1 ? string.Empty : "s")} across {archives.Count:N0} archive{(archives.Count == 1 ? string.Empty : "s")}.";
    }

    public ArchiveConflictNode? Find(string archiveName) => VisibleArchives.FirstOrDefault(value => string.Equals(value.ArchiveName, archiveName, StringComparison.OrdinalIgnoreCase));

    private static ArchiveConflictGroupNode[] Groups(ArchiveConflictSummary summary, string search, bool showNoConflicts)
    {
        HashSet<ulong> same = summary.Redundant.Select(value => value.ResourceHash).ToHashSet();
        List<ArchiveConflictGroupNode> groups = [];
        Add(groups, summary.ArchiveName, "Winning", ArchiveTreeTone.Winning, summary.Winning.Where(value => !same.Contains(value.ResourceHash)), search);
        Add(groups, summary.ArchiveName, "Losing", ArchiveTreeTone.Losing, summary.Losing.Where(value => !same.Contains(value.ResourceHash)), search);
        Add(groups, summary.ArchiveName, "Same content", ArchiveTreeTone.Same, summary.Redundant, search);
        Add(groups, summary.ArchiveName, "Can't determine", ArchiveTreeTone.Unknown, summary.Unresolved, search);
        if (showNoConflicts) Add(groups, summary.ArchiveName, "No conflicts", ArchiveTreeTone.Neutral, summary.Unique, search);
        return groups.ToArray();
    }

    private static void Add(List<ArchiveConflictGroupNode> groups, string archiveName, string label, ArchiveTreeTone tone, IEnumerable<ArchiveResourceOutcome> outcomes, string search)
    {
        ArchiveResourceNode[] resources = outcomes.GroupBy(value => value.ResourceHash).Select(value => value.First())
            .Where(value => search.Length == 0 || value.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) || (value.OtherArchives ?? []).Any(archive => !string.Equals(archive, archiveName, StringComparison.OrdinalIgnoreCase) && archive.Contains(search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(value => new ArchiveResourceNode(value, tone, archiveName)).ToArray();
        if (resources.Length > 0) groups.Add(new ArchiveConflictGroupNode($"{label} ({resources.Length:N0})", tone, resources));
    }
}
