using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed record CodeCaseCounts(int ProvenConflicts, int NeedsDecision, int Reviewed, int CompatibleEvidence);

public static class CodeCaseWorkspace
{
    public static ConflictWorkItem[] Filter(IReadOnlyList<ConflictWorkItem> items, string query, string view, string surface, string provider, string? otherProvider = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        bool scanProblems = string.Equals(surface, nameof(ConflictSurface.Diagnostic), StringComparison.Ordinal);
        IEnumerable<ConflictWorkItem> filtered = scanProblems ? items : items.Where(value => value.IsCodeCase);
        filtered = view switch
        {
            "Proven" => filtered.Where(value => value.CaseKind == ConflictCaseKind.ProvenConflict && value.State != ConflictWorkState.Reviewed),
            "NeedsDecision" => filtered.Where(value => value.IsActionable && value.CaseKind != ConflictCaseKind.ProvenConflict && value.State != ConflictWorkState.Reviewed),
            "Reviewed" => filtered.Where(value => value.State == ConflictWorkState.Reviewed),
            "Compatible" => filtered.Where(value => !value.IsActionable && value.State != ConflictWorkState.Reviewed),
            "All" => filtered,
            _ => filtered.Where(value => IsPlayerTriage(value, scanProblems))
        };
        if (surface != "All" && Enum.TryParse(surface, out ConflictSurface parsedSurface)) filtered = filtered.Where(value => value.Surface == parsedSurface);
        if (!string.IsNullOrWhiteSpace(provider) && provider != "All mods") filtered = filtered.Where(value => value.Providers.Contains(provider, StringComparer.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(otherProvider) && otherProvider != "All mods") filtered = filtered.Where(value => value.Providers.Contains(otherProvider, StringComparer.OrdinalIgnoreCase));
        string search = query?.Trim() ?? string.Empty;
        if (search.Length > 0) filtered = filtered.Where(value => value.Target.Contains(search, StringComparison.OrdinalIgnoreCase) || value.Summary.Contains(search, StringComparison.OrdinalIgnoreCase) || value.Providers.Any(name => name.Contains(search, StringComparison.OrdinalIgnoreCase)) || value.SourceFiles.Any(file => file.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase)));
        return filtered.OrderBy(CaseOrder).ThenBy(value => value.Surface).ThenBy(value => value.Target, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static CodeCaseCounts Counts(IReadOnlyList<ConflictWorkItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        IEnumerable<ConflictWorkItem> codeItems = items.Where(value => value.IsCodeCase);
        return new CodeCaseCounts(
            codeItems.Count(value => value.CaseKind == ConflictCaseKind.ProvenConflict && value.State != ConflictWorkState.Reviewed),
            codeItems.Count(value => value.IsActionable && value.CaseKind != ConflictCaseKind.ProvenConflict && value.State != ConflictWorkState.Reviewed),
            codeItems.Count(value => value.State == ConflictWorkState.Reviewed),
            codeItems.Count(value => !value.IsActionable && value.State != ConflictWorkState.Reviewed));
    }

    public static string[] Providers(IReadOnlyList<ConflictWorkItem> items)
        => ["All mods", .. items.SelectMany(value => value.Providers).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)];

    public static string ReviewRationale(string outcome, string notes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        string trimmed = notes?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? outcome : outcome + ": " + trimmed;
    }

    public static string ReviewNotes(string rationale, string outcome)
    {
        if (string.IsNullOrWhiteSpace(rationale) || string.IsNullOrWhiteSpace(outcome)) return string.Empty;
        string prefix = outcome + ": ";
        return rationale.StartsWith(prefix, StringComparison.Ordinal) ? rationale[prefix.Length..] : string.Empty;
    }

    private static int CaseOrder(ConflictWorkItem item) => item.CaseKind switch
    {
        ConflictCaseKind.ProvenConflict => 0,
        ConflictCaseKind.CompetingDeclaration => 1,
        ConflictCaseKind.FileOverride => 2,
        ConflictCaseKind.OrderSensitive => 3,
        ConflictCaseKind.CompilerEvidence => 4,
        ConflictCaseKind.RuntimeCheck => 5,
        ConflictCaseKind.Unknown => 6,
        ConflictCaseKind.Reviewed => 7,
        ConflictCaseKind.Composes => 8,
        ConflictCaseKind.SameEvidence => 9,
        _ => 10
    };

    private static bool IsPlayerTriage(ConflictWorkItem item, bool scanProblems)
        => item.State != ConflictWorkState.Reviewed
            && (scanProblems || item.Surface != ConflictSurface.Diagnostic)
            && (item.CaseKind is ConflictCaseKind.ProvenConflict
                or ConflictCaseKind.CompetingDeclaration
                or ConflictCaseKind.FileOverride
                or ConflictCaseKind.OrderSensitive
                or ConflictCaseKind.CompilerEvidence
                or ConflictCaseKind.RuntimeCheck
                or ConflictCaseKind.Unknown);
}
