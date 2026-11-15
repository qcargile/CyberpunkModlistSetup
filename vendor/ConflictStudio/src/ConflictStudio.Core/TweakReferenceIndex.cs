namespace ConflictStudio.Core;

public enum TweakReferenceState
{
    NotReference,
    Available,
    Ambiguous,
    Missing,
    Dynamic,
    AliasSource,
    GeneratedSource,
    Limited,
    Unavailable
}

public sealed record TweakReferenceOperation(string OperationId, string Reference);

public sealed record TweakReferenceDefinitionSet(string Reference, CodeSourceEvidence[] Definitions, int TotalDefinitions, bool IsLimited);

public sealed record TweakReferenceResolution(
    string? Reference,
    TweakReferenceState State,
    CodeSourceEvidence[] Definitions,
    string Message,
    bool IsLimited = false);

public sealed record TweakReferenceIndex(
    TweakReferenceOperation[] Operations,
    TweakReferenceDefinitionSet[] References,
    bool IsLimited,
    int TotalOperations)
{
    public const int MaximumOperations = 4096;
    public const int MaximumDefinitionsPerReference = 16;
    public const int MaximumDefinitions = 2048;

    public static TweakReferenceIndex Empty { get; } = new([], [], false, 0);

    public TweakReferenceResolution Resolve(CodeSourceEvidence source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string? reference = source.NormalizedValue;
        if (!IsReferenceKind(source.OperationKind) || string.IsNullOrWhiteSpace(reference)) return Result(reference, TweakReferenceState.NotReference);
        if (source.IsAliasSource) return Result(reference, TweakReferenceState.AliasSource);
        if (source.IsGeneratedSource) return Result(reference, TweakReferenceState.GeneratedSource);
        if (reference.Contains("$(", StringComparison.Ordinal) || reference.Contains("${", StringComparison.Ordinal)) return Result(reference, TweakReferenceState.Dynamic);
        if (!IsLiteral(reference)) return Result(reference, TweakReferenceState.NotReference);
        TweakReferenceOperation? operation = Operations.FirstOrDefault(value => string.Equals(value.OperationId, source.OperationId, StringComparison.Ordinal));
        if (operation is null && TotalOperations > Operations.Length)
            return new TweakReferenceResolution(reference, TweakReferenceState.Limited, [], $"Reference capture retained {Operations.Length:N0} of {TotalOperations:N0} source operations; {reference} was outside that bound.", true);
        if (operation is null) return Result(reference, TweakReferenceState.NotReference);
        if (!string.Equals(operation.Reference, reference, StringComparison.Ordinal)) return Result(reference, TweakReferenceState.Unavailable);
        TweakReferenceDefinitionSet? definitions = References.FirstOrDefault(value => string.Equals(value.Reference, operation.Reference, StringComparison.Ordinal));
        if (definitions is null) return Result(operation.Reference, TweakReferenceState.Missing);
        TweakReferenceState state = definitions.TotalDefinitions == 0
            ? TweakReferenceState.Missing
            : definitions.Definitions.Length == 0
                ? TweakReferenceState.Limited
                : definitions.TotalDefinitions == 1 ? TweakReferenceState.Available : TweakReferenceState.Ambiguous;
        return Result(operation.Reference, state, definitions.IsLimited, definitions.Definitions, definitions.TotalDefinitions);
    }

    private static TweakReferenceResolution Result(string? reference, TweakReferenceState state, bool limited = false, CodeSourceEvidence[]? definitions = null, int totalDefinitions = 0)
    {
        string name = string.IsNullOrWhiteSpace(reference) ? "This value" : reference;
        string message = state switch
        {
            TweakReferenceState.Available => $"{name} has one exact local definition.",
            TweakReferenceState.Ambiguous when limited => $"{name} has {totalDefinitions:N0} exact local definitions; only the captured definitions are available.",
            TweakReferenceState.Ambiguous => $"{name} has {totalDefinitions:N0} exact local definitions.",
            TweakReferenceState.Missing => $"No exact local definition was captured for {name}.",
            TweakReferenceState.Dynamic => $"{name} is dynamic source text and cannot be matched to a literal record definition.",
            TweakReferenceState.AliasSource => $"{name} comes from a YAML alias and cannot be matched to a literal definition at this use.",
            TweakReferenceState.GeneratedSource => $"{name} comes from a generated TweakXL instance and cannot be matched to a literal definition at this use.",
            TweakReferenceState.Limited => $"Reference capture reached its limit before {name} could be resolved.",
            TweakReferenceState.Unavailable => $"{name} does not match its captured reference metadata. Scan again.",
            _ => $"{name} is not a captured literal record reference."
        };
        return new TweakReferenceResolution(reference, state, definitions ?? [], message, limited || state == TweakReferenceState.Limited);
    }

    internal static bool IsReferenceKind(CodeEvidenceOperationKind kind)
        => kind is CodeEvidenceOperationKind.TweakBaseDeclaration
            or CodeEvidenceOperationKind.TweakArrayAppend
            or CodeEvidenceOperationKind.TweakArrayAppendOnce
            or CodeEvidenceOperationKind.TweakArrayPrepend
            or CodeEvidenceOperationKind.TweakArrayPrependOnce
            or CodeEvidenceOperationKind.TweakArrayRemove;

    internal static bool IsLiteral(string value)
    {
        int separator = value.IndexOf('.');
        if (separator <= 0 || separator == value.Length - 1) return false;
        return value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '$' or '-' or '.');
    }

    internal bool IsValid(IReadOnlyList<CodeSourceEvidence>? sources = null)
    {
        if (Operations is null || References is null || TotalOperations < Operations.Length || Operations.Length > MaximumOperations) return false;
        bool operationLimited = TotalOperations > Operations.Length;
        if (operationLimited && Operations.Length != MaximumOperations) return false;
        if (Operations.Any(value => value is null || value.OperationId is null || value.Reference is null || value.OperationId.Length != 64 || value.OperationId.Any(character => !Uri.IsHexDigit(character)) || !IsLiteral(value.Reference))) return false;
        if (Operations.Select(value => value.OperationId).Distinct(StringComparer.Ordinal).Count() != Operations.Length) return false;
        if (References.Any(value => value is null || value.Reference is null || value.Definitions is null || !IsLiteral(value.Reference))) return false;
        HashSet<string> referenceNames = References.Select(value => value.Reference).ToHashSet(StringComparer.Ordinal);
        if (referenceNames.Count != References.Length) return false;
        HashSet<string> operationReferences = Operations.Select(value => value.Reference).ToHashSet(StringComparer.Ordinal);
        if (!referenceNames.SetEquals(operationReferences)) return false;
        if (sources is not null)
        {
            Dictionary<string, string> referencesByOperation = Operations.ToDictionary(value => value.OperationId, value => value.Reference, StringComparer.Ordinal);
            if (sources.Any(source => source is null || source.OperationId is null || referencesByOperation.TryGetValue(source.OperationId, out string? reference) && !string.Equals(reference, source.NormalizedValue, StringComparison.Ordinal))) return false;
        }
        int capturedDefinitions = 0;
        foreach (TweakReferenceDefinitionSet reference in References)
        {
            if (reference.TotalDefinitions < reference.Definitions.Length || reference.Definitions.Length > MaximumDefinitionsPerReference || reference.IsLimited != (reference.TotalDefinitions > reference.Definitions.Length)) return false;
            if (reference.Definitions.Any(value => value is null || value.Target is null || value.Provider is null || value.FilePath is null || value.PhysicalPath is null || value.SourceSha256 is null
                    || value.Target != reference.Reference || string.IsNullOrWhiteSpace(value.Provider) || string.IsNullOrWhiteSpace(value.FilePath) || string.IsNullOrWhiteSpace(value.PhysicalPath)
                    || value.SourceSha256.Length != 64 || value.SourceSha256.Any(character => !Uri.IsHexDigit(character))
                    || value.StartLine < 1 || value.EndLine < value.StartLine || value.FocusStartLine < value.StartLine || value.FocusEndLine < value.FocusStartLine || value.FocusEndLine > value.EndLine)) return false;
            capturedDefinitions += reference.Definitions.Length;
        }
        if (capturedDefinitions > MaximumDefinitions) return false;
        return IsLimited == (operationLimited || References.Any(value => value.IsLimited));
    }
}
