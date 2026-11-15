namespace ConflictStudio.App;

public sealed record CodeSourceMatch(int Line, int Column, int Length);
public sealed record CodeSourceSearchResult(CodeSourceMatch[] Matches, bool IsLimited);

public static class CodeSourceSearch
{
    public const int MaximumMatches = 500;
    public const int MaximumQueryLength = 512;

    public static CodeSourceSearchResult Find(IReadOnlyList<string> lines, string query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Length > MaximumQueryLength) throw new ArgumentException("Search text must be 512 characters or fewer.", nameof(query));
        if (query.Length == 0) return new([], false);
        List<CodeSourceMatch> matches = [];
        for (int line = 0; line < lines.Count; line++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int start = 0;
            while (start < lines[line].Length)
            {
                int column = lines[line].IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
                if (column < 0) break;
                if (matches.Count == MaximumMatches) return new(matches.ToArray(), true);
                matches.Add(new(line + 1, column + 1, query.Length));
                start = column + query.Length;
            }
        }
        return new(matches.ToArray(), false);
    }
}
