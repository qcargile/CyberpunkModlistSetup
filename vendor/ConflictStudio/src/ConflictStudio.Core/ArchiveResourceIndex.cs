using System.Text.Json.Serialization;

namespace ConflictStudio.Core;

public enum ArchiveResourceDisposition { Winning, Losing, WinningAndLosing, Redundant, Unresolved, Unique }

public enum ArchivePayloadRelation { Identical, Different, Unknown, NotApplicable }

public sealed record ArchiveResourceOutcome(
    ulong ResourceHash,
    string DisplayName,
    ArchiveResourceDisposition Disposition,
    string? WinnerArchive,
    string? PayloadFingerprint,
    string? ResourceType,
    ResourcePathConfidence PathConfidence,
    [property: JsonIgnore] string[]? OtherArchives,
    ArchivePayloadRelation PayloadRelation = ArchivePayloadRelation.Unknown,
    string? RdarPayloadSha1 = null,
    string? CookedPayloadSha256 = null);

public sealed record ArchiveConflictSummary(
    string ArchiveName,
    string Provider,
    int? OrderPosition,
    ArchiveResourceOutcome[] Winning,
    ArchiveResourceOutcome[] Losing,
    ArchiveResourceOutcome[] Redundant,
    ArchiveResourceOutcome[] Unresolved,
    ArchiveResourceOutcome[] Unique,
    string? PhysicalPath = null)
{
    public int ConflictCount => Winning.Concat(Losing).Concat(Redundant).Concat(Unresolved).Select(value => value.ResourceHash).Distinct().Count();
}

public static class ArchiveResourceIndexBuilder
{
    public static ArchiveConflictSummary[] Build(IReadOnlyList<ResourceProvider> resources, IReadOnlyList<Mo2Archive> archives, IReadOnlyList<string> archiveOrder, IReadOnlyList<RdarArchiveFailure>? failures = null, ArchiveOrderProblemLane unresolvedLane = ArchiveOrderProblemLane.None, ArchiveOrderProblemLane incompleteLane = ArchiveOrderProblemLane.None, IReadOnlyList<string>? unlistedArchives = null)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(archiveOrder);
        Dictionary<string, int> positions = archiveOrder.Select((name, index) => (name, index)).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
        HashSet<string> unlisted = (unlistedArchives ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Mo2Archive> archiveMetadata = archives.ToDictionary(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Buckets> buckets = new(StringComparer.OrdinalIgnoreCase);
        foreach (Mo2Archive archive in archives) buckets.TryAdd(archive.ArchiveName, new Buckets());
        foreach (RdarArchiveFailure failure in failures ?? [])
        {
            if (!buckets.TryGetValue(failure.ArchiveName, out Buckets? bucket)) buckets[failure.ArchiveName] = bucket = new Buckets();
            bucket.Unresolved.Add(new ArchiveResourceOutcome(0, $"Archive unreadable: {failure.Message}", ArchiveResourceDisposition.Unresolved, null, null, null, ResourcePathConfidence.Unresolved, [], ArchivePayloadRelation.Unknown));
        }

        foreach (IGrouping<ulong, ResourceProvider> hashGroup in resources.GroupBy(value => value.ResourceHash))
        {
            ResourceProvider[] providers = hashGroup.GroupBy(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase).Select(value => value.First()).ToArray();
            bool chainUnresolved = ArchiveUncertainty.Crosses(providers, positions, failures, unresolvedLane, incompleteLane, unlisted);
            if (providers.Length == 1)
            {
                bool lowerArchiveUnreadable = !chainUnresolved && failures is { Count: > 0 };
                ArchiveResourceDisposition disposition = chainUnresolved ? ArchiveResourceDisposition.Unresolved : lowerArchiveUnreadable ? ArchiveResourceDisposition.Winning : ArchiveResourceDisposition.Unique;
                Add(buckets, providers[0], disposition, chainUnresolved ? null : providers[0].ArchiveName, lowerArchiveUnreadable ? failures!.Select(value => value.ArchiveName).ToArray() : [], chainUnresolved ? ArchivePayloadRelation.Unknown : ArchivePayloadRelation.NotApplicable);
                continue;
            }
            string[] archiveNames = providers.Select(value => value.ArchiveName).ToArray();
            ResourceProvider[] ordered = providers.OrderBy(value => positions.TryGetValue(value.ArchiveName, out int position) ? position : int.MaxValue).ToArray();
            bool evidenceMissing = chainUnresolved;
            bool ambiguousWinner = !evidenceMissing && RedmodArchiveIdentity.IsAmbiguousProvider(ordered[0], providers);
            ResourceProvider[] winnerGroup = ambiguousWinner ? ordered.Where(value => string.Equals(value.Provider, ordered[0].Provider, StringComparison.OrdinalIgnoreCase)).ToArray() : [ordered[0]];
            string?[] winnerPayloads = ResourcePayloadIdentity.ComparableFingerprints(winnerGroup);
            bool winnerPayloadUnknown = ambiguousWinner && (winnerPayloads.Any(string.IsNullOrWhiteSpace) || winnerPayloads.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
            bool unresolved = evidenceMissing || winnerPayloadUnknown;
            string? winner = unresolved ? null : ambiguousWinner ? ordered[0].Provider : ordered[0].ArchiveName;
            for (int index = 0; index < ordered.Length; index++)
            {
                ResourceProvider provider = ordered[index];
                bool providerAmbiguous = RedmodArchiveIdentity.IsAmbiguousProvider(provider, providers);
                bool belongsToAmbiguousWinner = ambiguousWinner && string.Equals(provider.Provider, ordered[0].Provider, StringComparison.OrdinalIgnoreCase);
                ArchivePayloadRelation comparison = ResourcePayloadIdentity.Compare(ordered[0], provider);
                bool matchesWinner = index > 0 && comparison == ArchivePayloadRelation.Identical;
                ArchiveResourceDisposition disposition = evidenceMissing ? ArchiveResourceDisposition.Unresolved
                    : winnerPayloadUnknown && belongsToAmbiguousWinner ? ArchiveResourceDisposition.Unresolved
                    : winnerPayloadUnknown ? ArchiveResourceDisposition.Losing
                    : belongsToAmbiguousWinner ? ArchiveResourceDisposition.Unresolved
                    : providerAmbiguous ? ArchiveResourceDisposition.Losing
                    : index == 0 ? ArchiveResourceDisposition.Winning
                    : index == ordered.Length - 1 ? ArchiveResourceDisposition.Losing
                    : ArchiveResourceDisposition.WinningAndLosing;
                ArchivePayloadRelation relation = matchesWinner || belongsToAmbiguousWinner && !winnerPayloadUnknown ? ArchivePayloadRelation.Identical
                    : unresolved || comparison == ArchivePayloadRelation.Unknown ? ArchivePayloadRelation.Unknown
                    : index == 0 ? ArchivePayloadRelation.NotApplicable
                    : comparison;
                Add(buckets, provider, disposition, winner, archiveNames, relation);
            }
        }

        return buckets.Select(value => Summary(value.Key, archiveMetadata.GetValueOrDefault(value.Key), positions.TryGetValue(value.Key, out int position) ? position : null, value.Value))
            .OrderBy(value => value.OrderPosition ?? int.MaxValue)
            .ThenBy(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Add(Dictionary<string, Buckets> buckets, ResourceProvider provider, ArchiveResourceDisposition disposition, string? winner, string[] otherArchives, ArchivePayloadRelation relation)
    {
        if (!buckets.TryGetValue(provider.ArchiveName, out Buckets? bucket)) buckets[provider.ArchiveName] = bucket = new Buckets();
        string display = provider.ResourcePath ?? $"resource hash {provider.ResourceHash}";
        ArchiveResourceOutcome outcome = new(provider.ResourceHash, display, disposition, winner, provider.PayloadFingerprint, provider.ResourceType, provider.PathConfidence, otherArchives, relation, provider.PayloadSha1, provider.CookedPayloadSha256);
        if (disposition == ArchiveResourceDisposition.WinningAndLosing)
        {
            bucket.Winning.Add(outcome);
            bucket.Losing.Add(outcome);
        }
        else bucket.For(disposition).Add(outcome);
    }

    private static ArchiveConflictSummary Summary(string archive, Mo2Archive? metadata, int? position, Buckets buckets)
    {
        ArchiveResourceOutcome[] winning = Sort(buckets.Winning);
        ArchiveResourceOutcome[] losing = Sort(buckets.Losing);
        ArchiveResourceOutcome[] unresolved = Sort(buckets.Unresolved);
        ArchiveResourceOutcome[] redundant = winning.Concat(losing).Where(value => value.PayloadRelation == ArchivePayloadRelation.Identical).GroupBy(value => value.ResourceHash).Select(value => value.First()).OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ArchiveConflictSummary(archive, metadata?.Provider ?? "Unknown provider", position, winning, losing, redundant, unresolved, Sort(buckets.Unique), metadata?.PhysicalPath);
    }

    private static ArchiveResourceOutcome[] Sort(List<ArchiveResourceOutcome> values) => values.OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed class Buckets
    {
        public List<ArchiveResourceOutcome> Winning { get; } = [];
        public List<ArchiveResourceOutcome> Losing { get; } = [];
        public List<ArchiveResourceOutcome> Redundant { get; } = [];
        public List<ArchiveResourceOutcome> Unresolved { get; } = [];
        public List<ArchiveResourceOutcome> Unique { get; } = [];

        public List<ArchiveResourceOutcome> For(ArchiveResourceDisposition disposition) => disposition switch
        {
            ArchiveResourceDisposition.Winning => Winning,
            ArchiveResourceDisposition.Losing => Losing,
            ArchiveResourceDisposition.Redundant => Redundant,
            ArchiveResourceDisposition.Unresolved => Unresolved,
            _ => Unique
        };
    }
}
