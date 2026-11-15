using ConflictStudio.Core;

namespace ConflictStudio.App;

public enum ScanHistoryChangeKind { New, Changed, NoLongerDetected, Unchanged }

public sealed record ScanHistoryEntry(ScanHistoryChangeKind ChangeKind, ConflictSurface Surface, string Target, ConflictWorkItem? Before, ConflictWorkItem? After)
{
    public string ChangeLabel => ChangeKind switch
    {
        ScanHistoryChangeKind.New => "New",
        ScanHistoryChangeKind.Changed => "Changed",
        ScanHistoryChangeKind.NoLongerDetected => "No longer detected",
        _ => "Unchanged"
    };

    public string Status => ChangeKind switch
    {
        ScanHistoryChangeKind.New => "Detected in current scan",
        ScanHistoryChangeKind.Changed => "Recorded evidence changed",
        ScanHistoryChangeKind.NoLongerDetected => "No longer detected",
        _ => "Unchanged"
    };

    public bool CanOpenCurrent => After is not null;
    public string SurfaceLabel => (After ?? Before)?.SurfaceLabel ?? string.Empty;
    public string ProviderSummary => (After ?? Before)?.ProviderSummary ?? string.Empty;
}

public sealed record ScanHistoryComparison(ProfileScanReceipt Previous, ProfileScanReceipt Current, ScanHistoryEntry[] Entries, string ToolVersionNotice, string AnalysisNotice, string CoverageNotice)
{
    public bool AnalysisMayHaveChanged => string.IsNullOrWhiteSpace(Previous.AnalysisVersion) || string.IsNullOrWhiteSpace(Current.AnalysisVersion) || !string.Equals(Previous.AnalysisVersion, Current.AnalysisVersion, StringComparison.Ordinal);

    public static ScanHistoryComparison Create(ProfileScanReceipt previous, ProfileScanReceipt current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ProfileScanDrift drift = ProfileScanDriftAnalyzer.Compare(previous, current);
        ConflictWorkItem[] before = ConflictWorkQueueBuilder.Build(previous, []);
        ConflictWorkItem[] after = ConflictWorkQueueBuilder.Build(current, []);
        IEnumerable<ScanHistoryEntry> changedEntries = drift.NewWorkItems.Select(value => Entry(ScanHistoryChangeKind.New, null, value))
            .Concat(drift.ChangedWorkItems.Select(value => Entry(ScanHistoryChangeKind.Changed, value.Before, value.After)))
            .Concat(drift.RemovedWorkItems.Select(value => Entry(ScanHistoryChangeKind.NoLongerDetected, value, null)));
        ScanHistoryEntry[] entries = changedEntries.Concat(UnchangedItems(before, after, drift))
            .OrderBy(value => value.ChangeKind)
            .ThenBy(value => value.Surface)
            .ThenBy(value => value.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ScanHistoryComparison(previous, current, entries, VersionNotice("Tool", previous.ToolVersion, current.ToolVersion, false), VersionNotice("Analyzer", previous.AnalysisVersion, current.AnalysisVersion, true), CoverageNoticeFor(previous, current));
    }

    private static IEnumerable<ScanHistoryEntry> UnchangedItems(ConflictWorkItem[] before, ConflictWorkItem[] after, ProfileScanDrift drift)
    {
        Dictionary<string, Queue<ConflictWorkItem>> remainingBefore = before.GroupBy(EvidenceIdentity, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => new Queue<ConflictWorkItem>(group), StringComparer.OrdinalIgnoreCase);
        foreach (ConflictWorkItem removed in drift.RemovedWorkItems) Dequeue(remainingBefore, EvidenceIdentity(removed));
        foreach (ConflictWorkItemChange changed in drift.ChangedWorkItems) Dequeue(remainingBefore, EvidenceIdentity(changed.Before));
        Dictionary<string, int> changedAfter = drift.NewWorkItems.Concat(drift.ChangedWorkItems.Select(value => value.After)).GroupBy(EvidenceIdentity, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (ConflictWorkItem current in after)
        {
            string identity = EvidenceIdentity(current);
            if (changedAfter.TryGetValue(identity, out int count) && count > 0)
            {
                changedAfter[identity] = count - 1;
                continue;
            }
            if (remainingBefore.TryGetValue(identity, out Queue<ConflictWorkItem>? candidates) && candidates.Count > 0) yield return Entry(ScanHistoryChangeKind.Unchanged, candidates.Dequeue(), current);
        }
    }

    private static ScanHistoryEntry Entry(ScanHistoryChangeKind kind, ConflictWorkItem? before, ConflictWorkItem? after)
    {
        ConflictWorkItem identity = after ?? before!;
        return new ScanHistoryEntry(kind, identity.Surface, identity.Target, before, after);
    }

    private static string EvidenceIdentity(ConflictWorkItem item) => item.Surface + "\0" + item.Target + "\0" + item.EvidenceSha256;

    private static void Dequeue(Dictionary<string, Queue<ConflictWorkItem>> values, string identity)
    {
        if (values.TryGetValue(identity, out Queue<ConflictWorkItem>? matches) && matches.Count > 0) matches.Dequeue();
    }

    private static string VersionNotice(string name, string? previous, string? current, bool analysis)
    {
        string boundary = analysis ? " Differences may include analyzer changes and are not attributed to installed mods." : string.Empty;
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current)) return $"{name} version was not recorded for one or both scans.{boundary}";
        if (!string.Equals(previous, current, StringComparison.Ordinal)) return $"{name} version changed from {previous} to {current}.{boundary}";
        return $"{name} version {current} was used for both scans.";
    }

    private static string CoverageNoticeFor(ProfileScanReceipt previous, ProfileScanReceipt current)
    {
        string before = CoverageSignature(previous);
        string after = CoverageSignature(current);
        string prefix = string.Equals(before, after, StringComparison.Ordinal) ? "Recorded coverage counts and read issues are unchanged." : "Recorded coverage counts or read issues changed between scans.";
        return $"{prefix} Before: {CoverageSummary(previous)}. Current: {CoverageSummary(current)}.";
    }

    private static string CoverageSignature(ProfileScanReceipt receipt)
    {
        CodeCoverageReceipt? coverage = receipt.CodeCoverage;
        string archiveFailures = string.Join(";", receipt.ArchiveFailures.Select(value => value.Provider + "\0" + value.ArchiveName).Order(StringComparer.OrdinalIgnoreCase));
        string archiveXlFailures = string.Join(";", receipt.ArchiveXlFailures.Select(value => value.Provider + "\0" + value.FilePath).Order(StringComparer.OrdinalIgnoreCase));
        string sourceFailures = string.Join(";", (receipt.SourceFailures ?? []).Select(value => value.Provider + "\0" + value.FilePath + "\0" + value.Surface).Order(StringComparer.OrdinalIgnoreCase));
        return string.Join("|", receipt.ArchiveFailures.Length, archiveFailures, receipt.ArchiveXlFailures.Length, archiveXlFailures, receipt.SourceFailures?.Length ?? 0, sourceFailures, coverage?.UnsupportedTweakFiles ?? -1, coverage?.UnreadableInputs ?? -1, coverage?.LiteralCallbacks ?? -1, coverage?.DynamicCallbacks ?? -1, coverage is null ? "not-recorded" : string.Join(";", coverage.Sources.Select(value => value.Surface + ":" + value.AnalyzedFiles).Order(StringComparer.OrdinalIgnoreCase)));
    }

    private static string CoverageSummary(ProfileScanReceipt receipt)
    {
        CodeCoverageReceipt? coverage = receipt.CodeCoverage;
        if (coverage is null) return $"code coverage not recorded; archive failures={receipt.ArchiveFailures.Length}; ArchiveXL failures={receipt.ArchiveXlFailures.Length}; source failures={receipt.SourceFailures?.Length ?? 0}";
        return $"analyzed={string.Join(", ", coverage.Sources.Select(value => value.Surface + " " + value.AnalyzedFiles))}; unsupported={coverage.UnsupportedTweakFiles}; unreadable={coverage.UnreadableInputs}; dynamic={coverage.DynamicCallbacks}; archive failures={receipt.ArchiveFailures.Length}; ArchiveXL failures={receipt.ArchiveXlFailures.Length}; source failures={receipt.SourceFailures?.Length ?? 0}";
    }
}
