namespace ConflictStudio.Core;

public static class SourcePrecedencePresentation
{
    public static PrecedenceResult<T>? Restrict<T>(PrecedenceResult<T>? result, IEnumerable<SourceAnalysisFailure> failures, string surface) where T : class
    {
        if (result is null || !failures.Any(value => value.Surface.StartsWith(surface, StringComparison.OrdinalIgnoreCase))) return result;
        return new PrecedenceResult<T>(PrecedenceStatus.Unresolved, default, [], "The scan has unreadable, unsupported, or unevaluated sources on this surface; another writer may be missing.");
    }

    public static string Describe(PrecedenceResult<TweakOperation>? result)
        => Describe(result, value => $"{value.Provider}: {value.FilePath}:{value.LineNumber} = {value.Value}", "TweakXL assignment", "Later runtime writes and additional imports can change the value; this is not an observed in-game value.");

    public static string Describe(PrecedenceResult<RedScriptHook>? result)
        => Describe(result, value => $"{value.Provider}: {value.FilePath}:{value.LineNumber}", "method replacement", "This applies to this method's replacement body only. Wrappers remain separate; their nesting and runtime behavior are not established.");

    private static string Describe<T>(PrecedenceResult<T>? result, Func<T, string> describe, string kind, string boundary) where T : class
    {
        if (result is null) return "Source order was not recorded. Run a fresh scan to determine whether a winner can be predicted.";
        if (result.Status != PrecedenceStatus.Predicted || result.Winner is not { } winner) return $"Winner unresolved. {result.Basis}";
        return $"Predicted winning {kind}: {describe(winner)}. Predicted overridden declarations: {string.Join("; ", result.Overridden.Select(describe))}. {result.Basis} {boundary}";
    }
}
