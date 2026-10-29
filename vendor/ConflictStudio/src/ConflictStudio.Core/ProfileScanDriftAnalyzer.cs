namespace ConflictStudio.Core;

public sealed record ResourceConflictChange(ResourceConflict Before, ResourceConflict After);

public sealed record VirtualFileShadowChange(VirtualFileShadow Before, VirtualFileShadow After);

public sealed record InteractionFindingChange(InteractionFinding Before, InteractionFinding After);

public sealed record ConflictWorkItemChange(ConflictWorkItem Before, ConflictWorkItem After);

public sealed record ProfileScanDrift(
    ResourceConflict[] NewResourceConflicts,
    ResourceConflict[] RemovedResourceConflicts,
    ResourceConflictChange[] ChangedResourceConflicts,
    VirtualFileShadow[] NewVirtualShadows,
    VirtualFileShadow[] RemovedVirtualShadows,
    VirtualFileShadowChange[] ChangedVirtualShadows,
    InteractionFinding[] NewInteractionFindings,
    InteractionFinding[] RemovedInteractionFindings,
    InteractionFindingChange[] ChangedInteractionFindings,
    ConflictWorkItem[] NewWorkItems,
    ConflictWorkItem[] RemovedWorkItems,
    ConflictWorkItemChange[] ChangedWorkItems);

public static class ProfileScanDriftAnalyzer
{
    public static ProfileScanDrift Compare(ProfileScanReceipt previous, ProfileScanReceipt current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        if (!string.Equals(previous.ProfileName, current.ProfileName, StringComparison.Ordinal) || previous.ManagerKind != current.ManagerKind || string.IsNullOrWhiteSpace(previous.InstallationId) || !string.Equals(previous.InstallationId, current.InstallationId, StringComparison.Ordinal)) throw new ArgumentException("Profile scan receipts must come from the same manager installation and profile.");
        Delta<ResourceConflict> resources = Difference(previous.ResourceConflicts, current.ResourceConflicts, value => value.ResourceHash.ToString(System.Globalization.CultureInfo.InvariantCulture), ResourceEvidence);
        Delta<VirtualFileShadow> shadows = Difference(previous.VirtualFileShadows, current.VirtualFileShadows, value => value.RelativePath, ShadowEvidence);
        Delta<InteractionFinding> findings = Difference(previous.InteractionFindings, current.InteractionFindings, value => value.Target, FindingEvidence);
        Delta<ConflictWorkItem> workItems = Difference(ConflictWorkQueueBuilder.Build(previous, []), ConflictWorkQueueBuilder.Build(current, []), value => value.Surface + "|" + value.Target, value => value.EvidenceSha256);
        return new ProfileScanDrift(
            resources.Added,
            resources.Removed,
            resources.Changed.Select(value => new ResourceConflictChange(value.Before, value.After)).ToArray(),
            shadows.Added,
            shadows.Removed,
            shadows.Changed.Select(value => new VirtualFileShadowChange(value.Before, value.After)).ToArray(),
            findings.Added,
            findings.Removed,
            findings.Changed.Select(value => new InteractionFindingChange(value.Before, value.After)).ToArray(),
            workItems.Added,
            workItems.Removed,
            workItems.Changed.Select(value => new ConflictWorkItemChange(value.Before, value.After)).ToArray());
    }

    private static Delta<T> Difference<T>(IReadOnlyList<T> previous, IReadOnlyList<T> current, Func<T, string> identity, Func<T, string> evidence)
    {
        Dictionary<string, T[]> before = previous.GroupBy(identity, StringComparer.OrdinalIgnoreCase).ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, T[]> after = current.GroupBy(identity, StringComparer.OrdinalIgnoreCase).ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.OrdinalIgnoreCase);
        List<T> added = [];
        List<T> removed = [];
        List<Changed<T>> changed = [];
        foreach ((string key, T[] values) in after)
        {
            if (!before.TryGetValue(key, out T[]? oldValues)) added.AddRange(values);
            else if (values.Length == 1 && oldValues.Length == 1 && evidence(values[0]) != evidence(oldValues[0])) changed.Add(new Changed<T>(oldValues[0], values[0]));
            else
            {
                HashSet<string> oldEvidence = oldValues.Select(evidence).ToHashSet(StringComparer.Ordinal);
                added.AddRange(values.Where(value => !oldEvidence.Contains(evidence(value))));
            }
        }
        foreach ((string key, T[] values) in before)
        {
            if (!after.TryGetValue(key, out T[]? newValues)) removed.AddRange(values);
            else if (values.Length != 1 || newValues.Length != 1)
            {
                HashSet<string> newEvidence = newValues.Select(evidence).ToHashSet(StringComparer.Ordinal);
                removed.AddRange(values.Where(value => !newEvidence.Contains(evidence(value))));
            }
        }
        return new Delta<T>(added.ToArray(), removed.ToArray(), changed.ToArray());
    }

    private static string ResourceEvidence(ResourceConflict value) => value.Kind + "|" + value.EngineWinnerArchive + "|" + string.Join("|", value.Providers.Select(provider => provider.ArchiveName + ":" + provider.PayloadFingerprint));

    private static string ShadowEvidence(VirtualFileShadow value) => value.Relation + "|" + value.WinnerProvider + "|" + string.Join("|", value.Providers.Select(provider => provider.Provider + ":" + provider.Sha256));

    private static string FindingEvidence(InteractionFinding value) => value.Kind + "|" + value.Summary + "|" + string.Join("|", value.Providers.OrderBy(provider => provider, StringComparer.OrdinalIgnoreCase));

    private sealed record Changed<T>(T Before, T After);

    private sealed record Delta<T>(T[] Added, T[] Removed, Changed<T>[] Changed);
}
