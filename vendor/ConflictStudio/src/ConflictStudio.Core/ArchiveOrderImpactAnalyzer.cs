namespace ConflictStudio.Core;

public sealed record ArchiveWinnerDelta(ulong ResourceHash, string DisplayName, string BeforeWinner, string AfterWinner);

public static class ArchiveOrderImpactAnalyzer
{
    public static ArchiveWinnerDelta[] Analyze(IReadOnlyList<ResourceProvider> providers, IReadOnlyList<string> beforeOrder, IReadOnlyList<string> afterOrder)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Dictionary<string, int> before = Positions(beforeOrder);
        Dictionary<string, int> after = Positions(afterOrder);
        return providers.GroupBy(value => value.ResourceHash)
            .Where(group => group.Select(value => value.ArchiveName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => Create(group.Key, group.ToArray(), before, after))
            .Where(value => value.BeforeWinner != value.AfterWinner)
            .OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ArchiveWinnerDelta Create(ulong hash, ResourceProvider[] providers, Dictionary<string, int> before, Dictionary<string, int> after)
    {
        ResourceProvider beforeWinner = providers.OrderBy(value => before.TryGetValue(value.ArchiveName, out int position) ? position : int.MaxValue).First();
        ResourceProvider afterWinner = providers.OrderBy(value => after.TryGetValue(value.ArchiveName, out int position) ? position : int.MaxValue).First();
        string display = providers.Select(value => value.ResourcePath).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? $"resource hash {hash}";
        return new ArchiveWinnerDelta(hash, display, beforeWinner.ArchiveName, afterWinner.ArchiveName);
    }

    private static Dictionary<string, int> Positions(IReadOnlyList<string> order) => order.Select((name, index) => new { name, index }).ToDictionary(value => value.name, value => value.index, StringComparer.OrdinalIgnoreCase);
}
