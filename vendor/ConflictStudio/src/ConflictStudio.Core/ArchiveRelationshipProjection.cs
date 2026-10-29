namespace ConflictStudio.Core;

public static class ArchiveRelationshipProjection
{
    public static string[] ComposeCombinedOrder(IReadOnlyList<string> proposedLegacyOrder, IReadOnlyList<string> currentCombinedOrder)
    {
        ArgumentNullException.ThrowIfNull(proposedLegacyOrder);
        ArgumentNullException.ThrowIfNull(currentCombinedOrder);
        HashSet<string> legacy = proposedLegacyOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return proposedLegacyOrder.Concat(currentCombinedOrder.Where(value => !legacy.Contains(value))).ToArray();
    }

    public static ArchiveRelationship[] BuildRelationships(IReadOnlyList<string> effectiveOrder, IReadOnlyList<string> displayOrder, IReadOnlyList<ResourceProvider> resources, IReadOnlyList<string> selectedArchives, ArchiveOrderProblemLane unresolvedLane, IReadOnlyList<string>? unlistedArchives = null)
    {
        ArgumentNullException.ThrowIfNull(effectiveOrder);
        ArgumentNullException.ThrowIfNull(displayOrder);
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(selectedArchives);
        if (selectedArchives.Count == 0) return [];
        Dictionary<string, int> effectivePositions = effectiveOrder.Select((name, position) => (name, position)).ToDictionary(value => value.name, value => value.position, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> displayPositions = displayOrder.Select((name, position) => (name, position)).ToDictionary(value => value.name, value => value.position, StringComparer.OrdinalIgnoreCase);
        HashSet<string> selected = selectedArchives.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> unlisted = (unlistedArchives ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, RelationshipState> relationships = new(StringComparer.OrdinalIgnoreCase);
        foreach (string archive in selected.Where(displayPositions.ContainsKey)) relationships[archive] = new RelationshipState();
        foreach (IGrouping<ulong, ResourceProvider> resource in resources.GroupBy(value => value.ResourceHash))
        {
            ResourceProvider[] providers = resource.GroupBy(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase).Select(value => value.First()).ToArray();
            ResourceProvider[] selectedProviders = providers.Where(value => selected.Contains(value.ArchiveName)).ToArray();
            if (selectedProviders.Length == 0) continue;
            foreach (ResourceProvider selectedProvider in selectedProviders)
            {
                foreach (ResourceProvider other in providers.Where(value => !selected.Contains(value.ArchiveName) && displayPositions.ContainsKey(value.ArchiveName)))
                {
                    if (!relationships.TryGetValue(other.ArchiveName, out RelationshipState? relationship)) relationships[other.ArchiveName] = relationship = new RelationshipState();
                    if (UnlistedRelationshipIsUnresolved(selectedProvider.ArchiveName, other.ArchiveName, unlisted))
                    {
                        relationship.Unknown = true;
                        continue;
                    }
                    if (ResourcePayloadIdentity.Compare(selectedProvider, other) == ArchivePayloadRelation.Identical)
                    {
                        relationship.Same = true;
                        continue;
                    }
                    bool sameRedmodLane = selectedProvider.ArchiveName.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase) && other.ArchiveName.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase);
                    bool ambiguous = sameRedmodLane && (RedmodArchiveIdentity.IsAmbiguousProvider(selectedProvider, providers) && RedmodArchiveIdentity.ProviderPayloadsDiffer(selectedProvider, providers)
                        || RedmodArchiveIdentity.IsAmbiguousProvider(other, providers) && RedmodArchiveIdentity.ProviderPayloadsDiffer(other, providers));
                    if (RelationshipOrderIsUnresolved(selectedProvider.ArchiveName, other.ArchiveName, unresolvedLane, unlisted) || ambiguous || !effectivePositions.TryGetValue(selectedProvider.ArchiveName, out int selectedPosition) || !effectivePositions.TryGetValue(other.ArchiveName, out int otherPosition))
                    {
                        relationship.Unknown = true;
                        continue;
                    }
                    if (selectedPosition < otherPosition) relationship.SelectedWins = true;
                    else if (selectedPosition > otherPosition) relationship.SelectedLoses = true;
                }
            }
        }
        return relationships.Select(value => new ArchiveRelationship(value.Key, value.Value.SelectedWins && !value.Value.Unknown, value.Value.SelectedLoses && !value.Value.Unknown, value.Value.Same && !value.Value.Unknown, value.Value.Unknown, displayPositions[value.Key], displayOrder.Count))
            .OrderBy(value => value.Position)
            .ToArray();
    }

    private sealed class RelationshipState
    {
        public bool SelectedWins { get; set; }
        public bool SelectedLoses { get; set; }
        public bool Same { get; set; }
        public bool Unknown { get; set; }
    }

    private static bool RelationshipOrderIsUnresolved(string selectedArchive, string otherArchive, ArchiveOrderProblemLane lane, HashSet<string> unlistedArchives)
    {
        bool selectedRedmod = selectedArchive.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase);
        bool otherRedmod = otherArchive.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase);
        if (selectedRedmod != otherRedmod) return false;
        return lane == ArchiveOrderProblemLane.Combined
            || lane == ArchiveOrderProblemLane.Legacy && !selectedRedmod && !otherRedmod
            || lane == ArchiveOrderProblemLane.Redmod && selectedRedmod && otherRedmod;
    }

    private static bool UnlistedRelationshipIsUnresolved(string selectedArchive, string otherArchive, HashSet<string> unlistedArchives)
        => !selectedArchive.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase)
            && !otherArchive.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase)
            && unlistedArchives.Contains(selectedArchive)
            && unlistedArchives.Contains(otherArchive);
}
