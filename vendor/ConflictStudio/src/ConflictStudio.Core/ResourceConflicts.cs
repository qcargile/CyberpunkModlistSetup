namespace ConflictStudio.Core;

public enum ResourcePathConfidence { Unresolved, ResolvedIndex, ArchiveCustomData }

public sealed record ResourceSegmentMetadata(uint InlineBufferSegmentCount, uint Start, uint End);

public sealed record ResourceProvider(
    string ArchiveName,
    ulong ResourceHash,
    string? ResourcePath,
    string? PayloadSha1,
    ResourceSegmentMetadata? SegmentMetadata = null,
    string? ResourceType = null,
    ResourcePathConfidence PathConfidence = ResourcePathConfidence.Unresolved,
    string? ProviderName = null,
    string? CookedPayloadSha256 = null)
{
    public string Provider => string.IsNullOrWhiteSpace(ProviderName) ? ArchiveName : ProviderName;
    public string? PayloadFingerprint => CookedPayloadSha256 ?? PayloadSha1;
}

public static class ResourcePayloadIdentity
{
    public static string?[] ComparableFingerprints(IReadOnlyList<ResourceProvider> providers)
    {
        if (providers.All(value => !string.IsNullOrWhiteSpace(value.CookedPayloadSha256))) return providers.Select(value => value.CookedPayloadSha256).ToArray();
        if (providers.All(value => !string.IsNullOrWhiteSpace(value.PayloadSha1))) return providers.Select(value => value.PayloadSha1).ToArray();
        return new string?[providers.Count];
    }

    public static ArchivePayloadRelation Compare(ResourceProvider first, ResourceProvider second)
    {
        string?[] fingerprints = ComparableFingerprints([first, second]);
        if (fingerprints.Any(string.IsNullOrWhiteSpace)) return ArchivePayloadRelation.Unknown;
        return string.Equals(fingerprints[0], fingerprints[1], StringComparison.OrdinalIgnoreCase) ? ArchivePayloadRelation.Identical : ArchivePayloadRelation.Different;
    }

    public static bool ProviderPayloadsDiffer(ResourceProvider provider, IReadOnlyList<ResourceProvider> providers)
    {
        ResourceProvider[] providerGroup = providers.Where(value => string.Equals(value.Provider, provider.Provider, StringComparison.OrdinalIgnoreCase)).ToArray();
        string?[] fingerprints = ComparableFingerprints(providerGroup);
        return fingerprints.All(value => !string.IsNullOrWhiteSpace(value)) && fingerprints.Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
    }
}

internal static class ArchiveUncertainty
{
    public static bool Crosses(IReadOnlyList<ResourceProvider> providers, IReadOnlyDictionary<string, int> positions, IReadOnlyList<RdarArchiveFailure>? failures, ArchiveOrderProblemLane unresolvedLane, ArchiveOrderProblemLane incompleteLane, IReadOnlySet<string> unlistedArchives)
    {
        if (providers.Any(value => !positions.ContainsKey(value.ArchiveName)) || OrderIsUnresolved(providers, unresolvedLane) || IncompleteSetAffects(providers, incompleteLane) || UnlistedOrderIsUnresolved(providers, unlistedArchives)) return true;
        if (failures is not { Count: > 0 }) return false;
        if (failures.Any(value => !positions.ContainsKey(value.ArchiveName))) return true;
        int lowestKnownPosition = providers.Max(value => positions[value.ArchiveName]);
        return failures.Any(value => positions[value.ArchiveName] <= lowestKnownPosition);
    }

    private static bool UnlistedOrderIsUnresolved(IReadOnlyList<ResourceProvider> providers, IReadOnlySet<string> unlistedArchives)
    {
        ResourceProvider[] legacy = providers.Where(value => !value.ArchiveName.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase)).ToArray();
        return legacy.Length > 1 && legacy.All(value => unlistedArchives.Contains(value.ArchiveName));
    }

    private static bool IncompleteSetAffects(IReadOnlyList<ResourceProvider> providers, ArchiveOrderProblemLane lane)
    {
        if (lane is ArchiveOrderProblemLane.Legacy or ArchiveOrderProblemLane.Combined) return true;
        return lane == ArchiveOrderProblemLane.Redmod && providers.All(value => value.ArchiveName.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase));
    }

    private static bool OrderIsUnresolved(IReadOnlyList<ResourceProvider> providers, ArchiveOrderProblemLane lane)
    {
        int redmods = providers.Count(value => value.ArchiveName.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase));
        int legacy = providers.Count - redmods;
        return lane == ArchiveOrderProblemLane.Legacy && legacy > 1
            || lane == ArchiveOrderProblemLane.Redmod && legacy == 0 && redmods > 1
            || lane == ArchiveOrderProblemLane.Combined && (legacy > 1 || legacy == 0 && redmods > 1);
    }
}

public enum ResourceConflictKind { Redundant, Divergent, Unresolved, OrderedOverlap }

public sealed record ResourceConflict(ulong ResourceHash, string DisplayName, ResourceConflictKind Kind, string EngineWinnerArchive, ResourceProvider[] Providers);

public static class ResourceConflictAnalyzer
{
    public static ResourceConflict[] Analyze(IReadOnlyList<ResourceProvider> providers, IReadOnlyList<string> archiveOrder, IReadOnlyList<RdarArchiveFailure>? failures = null, ArchiveOrderProblemLane unresolvedLane = ArchiveOrderProblemLane.None, ArchiveOrderProblemLane incompleteLane = ArchiveOrderProblemLane.None, IReadOnlyList<string>? unlistedArchives = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(archiveOrder);
        Dictionary<string, int> positions = archiveOrder.Select((name, index) => new { name, index }).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
        HashSet<string> unlisted = (unlistedArchives ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return providers.GroupBy(value => value.ResourceHash)
            .Where(group => group.Select(value => value.ArchiveName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => Create(group.Key, group.ToArray(), positions, failures, unresolvedLane, incompleteLane, unlisted))
            .OrderBy(value => value.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    private static ResourceConflict Create(ulong resourceHash, ResourceProvider[] providers, Dictionary<string, int> positions, IReadOnlyList<RdarArchiveFailure>? failures, ArchiveOrderProblemLane unresolvedLane, ArchiveOrderProblemLane incompleteLane, IReadOnlySet<string> unlistedArchives)
    {
        bool orderIncomplete = ArchiveUncertainty.Crosses(providers, positions, failures, unresolvedLane, incompleteLane, unlistedArchives);
        ResourceProvider[] ordered = providers.OrderBy(value => positions.TryGetValue(value.ArchiveName, out int position) ? position : int.MaxValue).ToArray();
        string? path = ordered.Select(value => value.ResourcePath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        string?[] payloadValues = ResourcePayloadIdentity.ComparableFingerprints(ordered);
        string[] payloads = payloadValues.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool ambiguousWinner = !orderIncomplete && RedmodArchiveIdentity.IsAmbiguousProvider(ordered[0], providers);
        ResourceProvider[] winnerGroup = ambiguousWinner ? ordered.Where(value => string.Equals(value.Provider, ordered[0].Provider, StringComparison.OrdinalIgnoreCase)).ToArray() : [ordered[0]];
        string?[] winnerPayloads = ResourcePayloadIdentity.ComparableFingerprints(winnerGroup);
        bool winnerPayloadUnknown = ambiguousWinner && (winnerPayloads.Any(string.IsNullOrWhiteSpace) || ResourcePayloadIdentity.ProviderPayloadsDiffer(ordered[0], providers));
        ResourceConflictKind kind = orderIncomplete || winnerPayloadUnknown ? ResourceConflictKind.Unresolved : payloadValues.Any(string.IsNullOrWhiteSpace) ? ResourceConflictKind.OrderedOverlap : payloads.Length == 1 ? ResourceConflictKind.Redundant : ResourceConflictKind.Divergent;
        string winner = kind == ResourceConflictKind.Unresolved ? "unresolved" : ambiguousWinner ? ordered[0].Provider : ordered[0].ArchiveName;
        return new ResourceConflict(resourceHash, path ?? $"resource hash {resourceHash}", kind, winner, ordered);
    }
}
