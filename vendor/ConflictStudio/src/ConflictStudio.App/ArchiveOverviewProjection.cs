using ConflictStudio.Core;

namespace ConflictStudio.App;

public static class ArchiveOverviewProjection
{
    public static string[] ComposeCombinedOrder(IReadOnlyList<string> proposedLegacyOrder, IReadOnlyList<string> currentCombinedOrder)
        => ArchiveRelationshipProjection.ComposeCombinedOrder(proposedLegacyOrder, currentCombinedOrder);

    public static ArchiveOverviewEntry[] BuildRelationships(IReadOnlyList<string> effectiveOrder, IReadOnlyList<string> displayOrder, IReadOnlyList<ResourceProvider> resources, IReadOnlyList<string> selectedArchives, ArchiveOrderProblemLane unresolvedLane, IReadOnlyList<string>? unlistedArchives = null)
        => ArchiveRelationshipProjection.BuildRelationships(effectiveOrder, displayOrder, resources, selectedArchives, unresolvedLane, unlistedArchives)
            .Select(value => new ArchiveOverviewEntry(value.ArchiveName, value.SelectedWins, value.SelectedLoses, value.HasSame, value.HasUnknown, value.Position, value.TotalCount)).ToArray();
}
