namespace ConflictStudio.Core;

public enum UnknownArchiveOrderImpact { None, RequiresAcknowledgement, BlockedCrossing }

public static class ArchiveUnknownImpactPolicy
{
    public static UnknownArchiveOrderImpact Evaluate(IReadOnlyList<string> currentOrder, IReadOnlyList<string> proposedOrder, IReadOnlyList<string> unreadableArchives)
    {
        ArgumentNullException.ThrowIfNull(currentOrder);
        ArgumentNullException.ThrowIfNull(proposedOrder);
        ArgumentNullException.ThrowIfNull(unreadableArchives);
        if (unreadableArchives.Count == 0) return UnknownArchiveOrderImpact.None;
        Dictionary<string, int> current = currentOrder.Select((name, index) => new { name, index }).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> proposed = proposedOrder.Select((name, index) => new { name, index }).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
        foreach (string unreadable in unreadableArchives)
        {
            if (!current.TryGetValue(unreadable, out int currentUnreadable) || !proposed.TryGetValue(unreadable, out int proposedUnreadable)) return UnknownArchiveOrderImpact.BlockedCrossing;
            foreach (string archive in currentOrder)
            {
                if (string.Equals(archive, unreadable, StringComparison.OrdinalIgnoreCase) || !proposed.TryGetValue(archive, out int proposedArchive)) continue;
                bool currentBefore = current[archive] < currentUnreadable;
                bool proposedBefore = proposedArchive < proposedUnreadable;
                if (currentBefore != proposedBefore) return UnknownArchiveOrderImpact.BlockedCrossing;
            }
        }
        return UnknownArchiveOrderImpact.RequiresAcknowledgement;
    }
}
