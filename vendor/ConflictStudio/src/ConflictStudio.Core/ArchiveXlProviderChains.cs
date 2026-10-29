namespace ConflictStudio.Core;

public sealed record ArchiveXlOperationChain(ArchiveXlOperationKind Kind, string Target, ArchiveXlOperation[] Operations);

public static class ArchiveXlProviderChainAnalyzer
{
    public static ArchiveXlOperationChain[] Group(IReadOnlyList<ArchiveXlOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        return operations.GroupBy(operation => (operation.Kind, operation.Target), new OperationKeyComparer())
            .Select(group => new ArchiveXlOperationChain(
                group.Key.Kind,
                group.Key.Target,
                group.ToArray()))
            .OrderBy(chain => chain.Kind)
            .ThenBy(chain => chain.Target, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class OperationKeyComparer : IEqualityComparer<(ArchiveXlOperationKind Kind, string Target)>
    {
        public bool Equals((ArchiveXlOperationKind Kind, string Target) left, (ArchiveXlOperationKind Kind, string Target) right) => left.Kind == right.Kind && StringComparer.OrdinalIgnoreCase.Equals(left.Target, right.Target);

        public int GetHashCode((ArchiveXlOperationKind Kind, string Target) value) => HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Target));
    }
}
