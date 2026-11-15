using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed record CodeContributorRow(string OperationId, string Provider, string Operation, string Value, string FilePath, int Line, bool SupportsSelectedIssue, CodeSourceEvidence? Source)
{
    public string Relationship => SupportsSelectedIssue ? "Supports this issue" : "Source context";
    public string Location => FilePath + ":" + Line;
}

public static class CodeContributorOverview
{
    public static CodeContributorRow[] Create(ConflictWorkItem item, IReadOnlyList<CodeSourceEvidence> sources, CodeFindingWitness? witness)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sources);
        CodeFindingParticipant[] participants = witness?.Participants ?? [];
        HashSet<string> targets = item.RelatedTargets.Append(item.Target).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, CodeFindingParticipant> byOperation = participants.Where(value => value.OperationId.Length > 0).DistinctBy(value => value.OperationId).ToDictionary(value => value.OperationId, StringComparer.Ordinal);
        Dictionary<CodeSourceEvidence, CodeFindingParticipant> bySource = participants.SelectMany(value => value.Sources.Select(source => (Source: source, Participant: value))).DistinctBy(value => value.Source).ToDictionary(value => value.Source, value => value.Participant);
        CodeSourceEvidence[] relevant = sources.Where(source => source.Surface == item.Surface && targets.Contains(source.Target)).Concat(participants.SelectMany(value => value.Sources))
            .GroupBy(source => (source.OperationId, source.Provider, source.FilePath, source.FocusStartLine, source.FocusEndLine, source.SourceSha256))
            .Select(group => group.OrderByDescending(source => source.IsCompleteBlock).ThenBy(source => source.StartLine).First()).ToArray();
        List<CodeContributorRow> rows = [];
        foreach (CodeSourceEvidence source in relevant)
        {
            CodeFindingParticipant? participant = byOperation.GetValueOrDefault(source.OperationId) ?? bySource.GetValueOrDefault(source);
            bool sourceBody = source.OperationKind is CodeEvidenceOperationKind.RedScriptWrap or CodeEvidenceOperationKind.RedScriptReplace or CodeEvidenceOperationKind.RedScriptAddMethod or CodeEvidenceOperationKind.LuaObserve or CodeEvidenceOperationKind.LuaObserveBefore or CodeEvidenceOperationKind.LuaObserveAfter or CodeEvidenceOperationKind.LuaOverride or CodeEvidenceOperationKind.LuaLifecycle;
            string value = sourceBody ? source.ValueType ?? string.Empty : participant?.NormalizedValue ?? source.NormalizedValue ?? string.Empty;
            if (value.Length > 240) value = value[..(char.IsHighSurrogate(value[239]) ? 239 : 240)] + "…";
            rows.Add(new(participant?.OperationId ?? source.OperationId, source.Provider, participant?.RoleLabel ?? OperationLabel(source.OperationKind), value, source.FilePath, source.FocusStartLine, participant is not null, source));
        }
        rows.AddRange(participants.Where(participant => participant.Sources.Length == 0).Select(participant => new CodeContributorRow(participant.OperationId, participant.Provider, participant.RoleLabel, participant.NormalizedValue ?? string.Empty, participant.FilePath, participant.Line, true, null)));
        return rows.OrderByDescending(row => row.SupportsSelectedIssue).ThenBy(row => row.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(row => row.Line).ToArray();
    }

    private static string OperationLabel(CodeEvidenceOperationKind kind) => kind switch
    {
        CodeEvidenceOperationKind.TweakTypeDeclaration => "Declares record type",
        CodeEvidenceOperationKind.TweakBaseDeclaration => "Inherits record",
        CodeEvidenceOperationKind.TweakScalarAssignment => "Assigns value",
        CodeEvidenceOperationKind.TweakArrayReplacement => "Replaces list",
        CodeEvidenceOperationKind.TweakArrayAppend or CodeEvidenceOperationKind.TweakArrayAppendOnce => "Appends entry",
        CodeEvidenceOperationKind.TweakArrayPrepend or CodeEvidenceOperationKind.TweakArrayPrependOnce => "Prepends entry",
        CodeEvidenceOperationKind.TweakArrayRemove => "Removes entry",
        CodeEvidenceOperationKind.TweakInlineRecord => "Declares inline record",
        CodeEvidenceOperationKind.TweakArrayAppendFrom or CodeEvidenceOperationKind.TweakArrayPrependFrom => "Copies list entries",
        CodeEvidenceOperationKind.RedScriptWrap => "Wraps method",
        CodeEvidenceOperationKind.RedScriptReplace => "Replaces method",
        CodeEvidenceOperationKind.RedScriptAddMethod => "Adds method",
        CodeEvidenceOperationKind.RedScriptAddField => "Adds field",
        CodeEvidenceOperationKind.LuaObserve or CodeEvidenceOperationKind.LuaObserveBefore or CodeEvidenceOperationKind.LuaObserveAfter => "Observes method",
        CodeEvidenceOperationKind.LuaOverride => "Overrides method",
        CodeEvidenceOperationKind.LuaLifecycle => "Registers callback",
        CodeEvidenceOperationKind.RuntimeTweakDbWrite or CodeEvidenceOperationKind.RuntimeBlackboardWrite or CodeEvidenceOperationKind.RuntimeStatusEffectWrite or CodeEvidenceOperationKind.RuntimeStatPoolWrite or CodeEvidenceOperationKind.RuntimePersistenceWrite => "Requests runtime change",
        _ => "Recorded source"
    };
}
