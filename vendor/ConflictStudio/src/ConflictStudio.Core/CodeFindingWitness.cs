using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace ConflictStudio.Core;

public enum CodeFindingWitnessKind
{
    OpposingArrayMutation,
    ArrayInsertionOrder,
    DuplicateArrayEntry,
    ArrayReplacement,
    ScalarValue,
    RecordDeclaration,
    DuplicateMemberDeclaration,
    ExclusiveReplacement,
    FlowInterruption,
    DeclarationRuntimeValue,
    RuntimeValue
}

public enum CodeFindingParticipantRole
{
    ArrayAddition,
    ArrayRemoval,
    PlainArrayAddition,
    UniqueArrayAddition,
    ValueDeclaration,
    RecordDeclaration,
    AddedMethod,
    AddedField,
    Replacement,
    StopsContinuation,
    SharedFlow,
    RuntimeWrite
}

public enum CodeEvidenceOperationKind
{
    Unknown,
    TweakTypeDeclaration,
    TweakBaseDeclaration,
    TweakScalarAssignment,
    TweakArrayReplacement,
    TweakArrayAppend,
    TweakArrayAppendOnce,
    TweakArrayPrepend,
    TweakArrayPrependOnce,
    TweakArrayRemove,
    TweakInlineRecord,
    TweakArrayAppendFrom,
    TweakArrayPrependFrom,
    RedScriptWrap,
    RedScriptReplace,
    RedScriptAddMethod,
    RedScriptAddField,
    LuaObserve,
    LuaObserveBefore,
    LuaObserveAfter,
    LuaOverride,
    LuaLifecycle,
    RuntimeTweakDbWrite,
    RuntimeBlackboardWrite,
    RuntimeStatusEffectWrite,
    RuntimeStatPoolWrite,
    RuntimePersistenceWrite
}

public sealed record CodeFindingParticipant(
    string OperationId,
    CodeFindingParticipantRole Role,
    string RoleLabel,
    CodeEvidenceOperationKind Kind,
    string Provider,
    string FilePath,
    int Line,
    string? Member,
    string? NormalizedValue,
    string? ValueType,
    CodeSourceEvidence[] Sources);

public sealed record CodeFindingWitness(
    string StableId,
    CodeFindingWitnessKind Kind,
    string Title,
    string Description,
    string Boundary,
    string? Member,
    CodeFindingParticipant[] Participants)
{
    public bool IsDuplicateDeclaration => Kind is CodeFindingWitnessKind.DuplicateArrayEntry or CodeFindingWitnessKind.DuplicateMemberDeclaration;

    public bool CanCompare(CodeFindingParticipant left, CodeFindingParticipant right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.OperationId == right.OperationId || !Contains(left) || !Contains(right) || !SameMember(left, right)) return false;
        return CanCompareKnown(left, right);
    }

    public CodeFindingParticipant[] OpponentsFor(CodeFindingParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!Contains(participant)) return [];
        return Participants.Where(value => value.OperationId != participant.OperationId && SameMember(participant, value) && CanCompareKnown(participant, value)).ToArray();
    }

    internal bool HasOpponent(CodeFindingParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        return Contains(participant) && Participants.Any(value => value.OperationId != participant.OperationId && SameMember(participant, value) && CanCompareKnown(participant, value));
    }

    internal bool HasComparison()
    {
        for (int left = 0; left < Participants.Length; left++)
            for (int right = left + 1; right < Participants.Length; right++)
                if (Participants[left].OperationId != Participants[right].OperationId && SameMember(Participants[left], Participants[right]) && CanCompareKnown(Participants[left], Participants[right])) return true;
        return false;
    }

    private bool CanCompareKnown(CodeFindingParticipant left, CodeFindingParticipant right)
        => Kind switch
        {
            CodeFindingWitnessKind.OpposingArrayMutation => Opposite(left, right, CodeFindingParticipantRole.ArrayAddition, CodeFindingParticipantRole.ArrayRemoval) && !SameComponent(left, right),
            CodeFindingWitnessKind.ArrayInsertionOrder => Opposite(left, right, CodeFindingParticipantRole.PlainArrayAddition, CodeFindingParticipantRole.UniqueArrayAddition) && !SameProvider(left, right),
            CodeFindingWitnessKind.DuplicateArrayEntry => left.Role == CodeFindingParticipantRole.PlainArrayAddition && right.Role == CodeFindingParticipantRole.PlainArrayAddition && !SameProvider(left, right),
            CodeFindingWitnessKind.ArrayReplacement => ArrayOperationsDiffer(left, right),
            CodeFindingWitnessKind.ScalarValue or CodeFindingWitnessKind.RecordDeclaration => DifferentValues(left, right),
            CodeFindingWitnessKind.DuplicateMemberDeclaration => left.Role == right.Role && left.Kind == right.Kind,
            CodeFindingWitnessKind.ExclusiveReplacement => left.Role == CodeFindingParticipantRole.Replacement && right.Role == CodeFindingParticipantRole.Replacement && ReplacementBodiesDiffer(left, right),
            CodeFindingWitnessKind.FlowInterruption => Opposite(left, right, CodeFindingParticipantRole.StopsContinuation, CodeFindingParticipantRole.SharedFlow),
            CodeFindingWitnessKind.DeclarationRuntimeValue => DeclarationAndRuntimeDiffer(left, right),
            CodeFindingWitnessKind.RuntimeValue => left.Role == CodeFindingParticipantRole.RuntimeWrite && right.Role == CodeFindingParticipantRole.RuntimeWrite && !SameProvider(left, right) && ScalarValuesDiffer(left.NormalizedValue, right.NormalizedValue),
            _ => false
        };

    private bool Contains(CodeFindingParticipant participant) => Participants.Any(value => value.OperationId == participant.OperationId);

    private static bool SameMember(CodeFindingParticipant left, CodeFindingParticipant right)
        => string.Equals(left.Member, right.Member, StringComparison.Ordinal);

    private static bool SameProvider(CodeFindingParticipant left, CodeFindingParticipant right)
        => string.Equals(left.Provider, right.Provider, StringComparison.OrdinalIgnoreCase);

    private static bool SameComponent(CodeFindingParticipant left, CodeFindingParticipant right)
        => SameProvider(left, right) && string.Equals(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);

    private static bool Opposite(CodeFindingParticipant left, CodeFindingParticipant right, CodeFindingParticipantRole first, CodeFindingParticipantRole second)
        => left.Role == first && right.Role == second || left.Role == second && right.Role == first;

    private static bool DifferentValues(CodeFindingParticipant left, CodeFindingParticipant right)
        => left.NormalizedValue is not null && right.NormalizedValue is not null && !string.Equals(left.NormalizedValue, right.NormalizedValue, StringComparison.Ordinal);

    private static bool ArrayOperationsDiffer(CodeFindingParticipant left, CodeFindingParticipant right)
    {
        bool leftReplacement = left.Kind == CodeEvidenceOperationKind.TweakArrayReplacement;
        bool rightReplacement = right.Kind == CodeEvidenceOperationKind.TweakArrayReplacement;
        return leftReplacement && rightReplacement ? DifferentValues(left, right) : leftReplacement || rightReplacement;
    }

    private static bool ReplacementBodiesDiffer(CodeFindingParticipant left, CodeFindingParticipant right)
        => left.NormalizedValue is null || right.NormalizedValue is null || !string.Equals(left.NormalizedValue, right.NormalizedValue, StringComparison.Ordinal);

    private static bool DeclarationAndRuntimeDiffer(CodeFindingParticipant left, CodeFindingParticipant right)
    {
        CodeFindingParticipant declaration;
        CodeFindingParticipant runtime;
        if (left.Role == CodeFindingParticipantRole.RuntimeWrite && right.Role is CodeFindingParticipantRole.ValueDeclaration or CodeFindingParticipantRole.ArrayAddition)
        {
            runtime = left;
            declaration = right;
        }
        else if (right.Role == CodeFindingParticipantRole.RuntimeWrite && left.Role is CodeFindingParticipantRole.ValueDeclaration or CodeFindingParticipantRole.ArrayAddition)
        {
            runtime = right;
            declaration = left;
        }
        else return false;
        if (SameProvider(declaration, runtime)) return false;
        if (runtime.ValueType == "empty collection")
            return declaration.Kind is CodeEvidenceOperationKind.TweakArrayAppend or CodeEvidenceOperationKind.TweakArrayAppendOnce or CodeEvidenceOperationKind.TweakArrayPrepend or CodeEvidenceOperationKind.TweakArrayPrependOnce
                || declaration.Kind == CodeEvidenceOperationKind.TweakArrayReplacement && declaration.NormalizedValue != "[]";
        return declaration.Kind == CodeEvidenceOperationKind.TweakScalarAssignment && ScalarValuesDiffer(declaration.NormalizedValue, runtime.NormalizedValue);
    }

    private static bool ScalarValuesDiffer(string? first, string? second)
        => first is not null && second is not null && TweakRuntimeEvidence.ScalarsDiffer(first, second);
}

internal static class CodeOperationIdentity
{
    public static List<TweakOperation> NumberTweaks(IEnumerable<TweakOperation> operations)
        => Number(operations, TweakBase, (value, occurrence, id) => value with { OperationOccurrence = occurrence, OperationId = id });

    public static List<RedScriptFlowEvidence> NumberFlows(IEnumerable<RedScriptFlowEvidence> flows)
        => Number(flows, FlowBase, (value, occurrence, id) => value with { OperationOccurrence = occurrence, OperationId = id });

    public static List<SharedStateWrite> NumberWrites(IEnumerable<SharedStateWrite> writes)
        => Number(writes, WriteBase, (value, occurrence, id) => value with { OperationOccurrence = occurrence, OperationId = id });

    public static List<LuaCallbackEvidence> NumberCallbacks(IEnumerable<LuaCallbackEvidence> callbacks)
        => Number(callbacks, CallbackBase, (value, occurrence, id) => value with { OperationOccurrence = occurrence, OperationId = id });

    public static string FieldBase(string target, RedScriptFieldDeclaration field)
        => Join("field", field.Provider, NormalizePath(field.FilePath), target, field.Type, field.Line.ToString(CultureInfo.InvariantCulture));

    public static string FieldId(string target, RedScriptFieldDeclaration field, int occurrence)
        => Id(FieldBase(target, field), occurrence);

    public static string TweakBase(TweakOperation operation)
        => Join("tweak", operation.Provider, NormalizePath(operation.FilePath), operation.Target, operation.Kind.ToString(), operation.Value, operation.LineNumber.ToString(CultureInfo.InvariantCulture));

    public static string FlowBase(RedScriptFlowEvidence flow)
        => Join("redscript", flow.Provider, NormalizePath(flow.FilePath), flow.Target, flow.Kind.ToString(), flow.Line.ToString(CultureInfo.InvariantCulture), flow.BodySha256 ?? string.Empty);

    public static string WriteBase(SharedStateWrite write)
        => Join("write", write.Provider, NormalizePath(write.FilePath), write.Surface.ToString(), write.Target, write.Operation, write.Evidence, write.Line.ToString(CultureInfo.InvariantCulture), write.CallSha256 ?? string.Empty);

    public static string CallbackBase(LuaCallbackEvidence callback)
        => Join("lua", callback.Target, callback.Kind.ToString(), callback.Line.ToString(CultureInfo.InvariantCulture), callback.SourceHash, callback.CallbackSha256 ?? string.Empty);

    public static string Id(string value, int occurrence)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value + "|" + occurrence)));

    public static CodeEvidenceOperationKind Kind(TweakOperationKind kind) => kind switch
    {
        TweakOperationKind.TypeDeclaration => CodeEvidenceOperationKind.TweakTypeDeclaration,
        TweakOperationKind.BaseDeclaration => CodeEvidenceOperationKind.TweakBaseDeclaration,
        TweakOperationKind.ScalarAssignment => CodeEvidenceOperationKind.TweakScalarAssignment,
        TweakOperationKind.ArrayReplacement => CodeEvidenceOperationKind.TweakArrayReplacement,
        TweakOperationKind.ArrayAppend => CodeEvidenceOperationKind.TweakArrayAppend,
        TweakOperationKind.ArrayAppendOnce => CodeEvidenceOperationKind.TweakArrayAppendOnce,
        TweakOperationKind.ArrayPrepend => CodeEvidenceOperationKind.TweakArrayPrepend,
        TweakOperationKind.ArrayPrependOnce => CodeEvidenceOperationKind.TweakArrayPrependOnce,
        TweakOperationKind.ArrayRemove => CodeEvidenceOperationKind.TweakArrayRemove,
        TweakOperationKind.InlineRecord => CodeEvidenceOperationKind.TweakInlineRecord,
        TweakOperationKind.ArrayAppendFrom => CodeEvidenceOperationKind.TweakArrayAppendFrom,
        TweakOperationKind.ArrayPrependFrom => CodeEvidenceOperationKind.TweakArrayPrependFrom,
        _ => CodeEvidenceOperationKind.Unknown
    };

    public static CodeEvidenceOperationKind Kind(RedScriptFlowKind kind) => kind switch
    {
        RedScriptFlowKind.Wrap => CodeEvidenceOperationKind.RedScriptWrap,
        RedScriptFlowKind.Replace => CodeEvidenceOperationKind.RedScriptReplace,
        RedScriptFlowKind.Add => CodeEvidenceOperationKind.RedScriptAddMethod,
        _ => CodeEvidenceOperationKind.Unknown
    };

    public static CodeEvidenceOperationKind Kind(SharedStateSurface surface) => surface switch
    {
        SharedStateSurface.TweakDb => CodeEvidenceOperationKind.RuntimeTweakDbWrite,
        SharedStateSurface.Blackboard => CodeEvidenceOperationKind.RuntimeBlackboardWrite,
        SharedStateSurface.StatusEffect => CodeEvidenceOperationKind.RuntimeStatusEffectWrite,
        SharedStateSurface.StatPool => CodeEvidenceOperationKind.RuntimeStatPoolWrite,
        SharedStateSurface.Persistence => CodeEvidenceOperationKind.RuntimePersistenceWrite,
        _ => CodeEvidenceOperationKind.Unknown
    };

    public static CodeEvidenceOperationKind Kind(LuaCallbackEvidenceKind kind) => kind switch
    {
        LuaCallbackEvidenceKind.Observe => CodeEvidenceOperationKind.LuaObserve,
        LuaCallbackEvidenceKind.ObserveBefore => CodeEvidenceOperationKind.LuaObserveBefore,
        LuaCallbackEvidenceKind.ObserveAfter => CodeEvidenceOperationKind.LuaObserveAfter,
        LuaCallbackEvidenceKind.Override => CodeEvidenceOperationKind.LuaOverride,
        LuaCallbackEvidenceKind.Lifecycle => CodeEvidenceOperationKind.LuaLifecycle,
        _ => CodeEvidenceOperationKind.Unknown
    };

    private static List<T> Number<T>(IEnumerable<T> source, Func<T, string> baseIdentity, Func<T, int, string, T> assign)
    {
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        List<T> result = [];
        foreach (T value in source)
        {
            string identity = baseIdentity(value);
            int occurrence = occurrences.GetValueOrDefault(identity) + 1;
            occurrences[identity] = occurrence;
            result.Add(assign(value, occurrence, Id(identity, occurrence)));
        }
        return result;
    }

    private static string Join(params string[] values)
        => string.Join("|", values.Select(value => value.Length + ":" + value));

    private static string NormalizePath(string value) => value.Replace('/', '\\');
}

internal sealed class CodeFindingWitnessBuilder
{
    private readonly Dictionary<string, TweakOverlap> _tweaks;
    private readonly ILookup<string, RedScriptFlowEvidence> _flows;
    private readonly Dictionary<(string OperationId, ConflictSurface Surface), CodeSourceEvidence[]> _sources;

    public CodeFindingWitnessBuilder(ProfileScanReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        _tweaks = receipt.TweakOverlaps.GroupBy(value => value.Target, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        _flows = receipt.RedScriptFlows.ToLookup(value => value.Target, StringComparer.Ordinal);
        _sources = receipt.CodeEvidence.Where(value => !string.IsNullOrEmpty(value.OperationId))
            .GroupBy(value => (value.OperationId, value.Surface))
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.StartLine).ThenBy(value => value.Description, StringComparer.Ordinal).ToArray());
    }

    public CodeFindingWitness[] ForInteraction(string evidenceSha256, InteractionFinding finding)
    {
        List<CodeFindingWitness> witnesses = [];
        if (finding.TweakRuntimeEvidence is { } runtime) AddDeclarationRuntime(witnesses, evidenceSha256, finding.Target, runtime);
        if (_tweaks.TryGetValue(finding.Target, out TweakOverlap? tweak)) AddTweak(witnesses, evidenceSha256, tweak);
        AddMethods(witnesses, evidenceSha256, finding, _flows[finding.Target].ToArray());
        return witnesses.ToArray();
    }

    public CodeFindingWitness[] ForSharedState(string evidenceSha256, SharedStateWriteFinding finding)
    {
        SharedStateWrite[] numberedWrites = EnsureWrites(finding.Writes);
        var competing = (finding with { Writes = numberedWrites }).CompetingValues().ToArray();
        if (competing.Length == 0) return [];
        HashSet<string> operationIds = competing.SelectMany(value => new[] { value.First.OperationId, value.Second.OperationId }).ToHashSet(StringComparer.Ordinal);
        SharedStateWrite[] writes = numberedWrites.Where(value => operationIds.Contains(value.OperationId)).ToArray();
        CodeFindingParticipant[] participants = writes.Select(value => RuntimeParticipant(value, finding.Target, ConflictSurface.SharedState)).ToArray();
        return Create(evidenceSha256, CodeFindingWitnessKind.RuntimeValue, "Different runtime values", "These code paths write different literal values to the same TweakDB field.", "The comparison shows requested source values. It does not establish execution order or the value used in game.", finding.Target, participants) is { } witness ? [witness] : [];
    }

    private void AddTweak(List<CodeFindingWitness> witnesses, string evidenceSha256, TweakOverlap overlap)
    {
        TweakOperation[] operations = EnsureTweaks(overlap.Operations);
        if (overlap.Kind == TweakOverlapKind.OpposingMutation)
        {
            foreach (IGrouping<string, TweakOperation> group in operations.GroupBy(value => value.Value, StringComparer.Ordinal).Where(TweakInteractionAnalyzer.ValueHasOpposingMutations))
            {
                CodeFindingParticipant[] participants = group.Select(value => TweakParticipant(value, value.Kind == TweakOperationKind.ArrayRemove ? CodeFindingParticipantRole.ArrayRemoval : CodeFindingParticipantRole.ArrayAddition, value.Kind == TweakOperationKind.ArrayRemove ? "Removes member" : "Adds member", group.Key)).ToArray();
                Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.OpposingArrayMutation, $"Array member: {group.Key}", "One source adds this member while another source removes it.", "The operations disagree about membership. Their order in the final game database is not established here.", group.Key, participants));
            }
            return;
        }
        if (overlap.Kind == TweakOverlapKind.DuplicateMutation)
        {
            foreach (IGrouping<string, TweakOperation> group in operations.GroupBy(value => value.Value, StringComparer.Ordinal).Where(TweakInteractionAnalyzer.ValueHasDuplicatePlainAdds))
            {
                CodeFindingParticipant[] participants = group.Where(value => value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend).Select(value => TweakParticipant(value, CodeFindingParticipantRole.PlainArrayAddition, "Adds member; duplicates allowed", group.Key)).ToArray();
                Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.DuplicateArrayEntry, $"Repeated array member: {group.Key}", "Separate sources add the same member without a uniqueness guard.", "The duplicate additions are present in source. The comparison does not establish the final array or a gameplay problem.", group.Key, participants));
            }
            return;
        }
        if (overlap.Kind == TweakOverlapKind.MixedArrayOperations)
        {
            if (operations.Any(value => value.Kind == TweakOperationKind.ArrayReplacement))
            {
                CodeFindingParticipant[] participants = operations.Select(value => value.Kind == TweakOperationKind.ArrayReplacement
                    ? TweakParticipant(value, CodeFindingParticipantRole.ValueDeclaration, "Replaces array", overlap.Target)
                    : value.Kind == TweakOperationKind.ArrayRemove
                        ? TweakParticipant(value, CodeFindingParticipantRole.ArrayRemoval, "Removes member", overlap.Target)
                        : TweakParticipant(value, CodeFindingParticipantRole.ArrayAddition, "Adds member", overlap.Target)).ToArray();
                bool replacementsOnly = operations.All(value => value.Kind == TweakOperationKind.ArrayReplacement);
                Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.ArrayReplacement, replacementsOnly ? "Different array replacements" : "Array replacement and member changes", replacementsOnly ? "The sources replace the same array with different contents." : "One source replaces the array while other sources change individual members.", replacementsOnly ? "The source values differ. The comparison does not establish which replacement the game uses." : "The comparison preserves the replacement and mutation operands. The order-sensitive final array is not resolved here.", overlap.Target, participants));
                if (replacementsOnly) return;
            }
            foreach (IGrouping<string, TweakOperation> group in operations.GroupBy(value => value.Value, StringComparer.Ordinal).Where(TweakInteractionAnalyzer.ValueIsOrderSensitive))
            {
                CodeFindingParticipant[] participants = group.Where(value => value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend or TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayPrependOnce)
                    .Select(value => TweakParticipant(value, value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend ? CodeFindingParticipantRole.PlainArrayAddition : CodeFindingParticipantRole.UniqueArrayAddition, value.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayPrepend ? "Adds member; duplicates allowed" : "Adds member once", group.Key)).ToArray();
                Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.ArrayInsertionOrder, $"Array insertion rule: {group.Key}", "One source permits duplicate insertion while another requests unique insertion for the same member.", "The operations differ in duplicate handling. The final array is not resolved here.", group.Key, participants));
            }
            return;
        }
        if (overlap.Kind == TweakOverlapKind.ScalarOverwrite)
        {
            CodeFindingParticipant[] participants = operations.Where(value => value.Kind == TweakOperationKind.ScalarAssignment).Select(value => TweakParticipant(value, CodeFindingParticipantRole.ValueDeclaration, "Declares value", overlap.Target)).ToArray();
            Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.ScalarValue, "Different declared values", "The sources assign different scalar values to the same field.", "The source values differ. The comparison does not establish which value the game uses.", overlap.Target, participants));
            return;
        }
        if (overlap.Kind == TweakOverlapKind.RecordDefinitionCollision)
        {
            foreach (IGrouping<TweakOperationKind, TweakOperation> group in operations.Where(value => value.Kind is TweakOperationKind.TypeDeclaration or TweakOperationKind.BaseDeclaration or TweakOperationKind.InlineRecord).GroupBy(value => value.Kind))
            {
                string member = group.Key == TweakOperationKind.TypeDeclaration ? "$type" : group.Key == TweakOperationKind.BaseDeclaration ? "$base" : overlap.Target;
                CodeFindingParticipant[] participants = group.Select(value => TweakParticipant(value, CodeFindingParticipantRole.RecordDeclaration, group.Key == TweakOperationKind.TypeDeclaration ? "Declares record type" : group.Key == TweakOperationKind.BaseDeclaration ? "Declares base record" : "Declares record", member)).ToArray();
                Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.RecordDeclaration, $"Different record declaration: {member}", "The sources construct the same record with different declarations.", "The declarations differ. The comparison does not reproduce TweakXL discovery order or establish the record used in game.", member, participants));
            }
        }
    }

    private void AddMethods(List<CodeFindingWitness> witnesses, string evidenceSha256, InteractionFinding finding, RedScriptFlowEvidence[] rawFlows)
    {
        RedScriptFlowEvidence[] flows = EnsureFlows(rawFlows);
        RedScriptFlowEvidence[] replacements = flows.Where(value => value.Kind == RedScriptFlowKind.Replace).ToArray();
        if (finding.Kind == InteractionFindingKind.Exclusive && replacements.Length > 1)
        {
            CodeFindingParticipant[] participants = replacements.Select(value => FlowParticipant(value, CodeFindingParticipantRole.Replacement, "Replaces method")).ToArray();
            Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.ExclusiveReplacement, "Different method replacements", "These declarations replace the same RedScript method with different bodies.", "Only the source replacements are compared. This does not establish a compiler error or a gameplay symptom.", finding.Target, participants));
        }
        RedScriptFlowEvidence[] additions = flows.Where(value => value.Kind == RedScriptFlowKind.Add).ToArray();
        if (additions.Length > 1)
        {
            CodeFindingParticipant[] participants = additions.Select(value => FlowParticipant(value, CodeFindingParticipantRole.AddedMethod, "Adds method")).ToArray();
            Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.DuplicateMemberDeclaration, "Repeated added method", "Separate declarations add the same RedScript method.", "The declarations are evidence even when their text matches. RedScript startup output is still needed to establish the compiler result.", finding.Target, participants));
        }
        RedScriptFieldDeclaration[] fields = EnsureFields(finding.Target, finding.DeclarationEvidence ?? []);
        if (fields.Length > 1)
        {
            CodeFindingParticipant[] participants = fields.Select(value => FieldParticipant(value, finding.Target)).ToArray();
            Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.DuplicateMemberDeclaration, "Repeated added field", "Separate declarations add the same RedScript field.", "The declarations are evidence even when their text matches. RedScript startup output is still needed to establish the compiler result.", finding.Target, participants));
        }
        RedScriptFlowEvidence[] comparableFlows = flows.Where(value => value.Kind != RedScriptFlowKind.Add).ToArray();
        if (comparableFlows.Length > 1 && comparableFlows.Any(StopsContinuation))
        {
            CodeFindingParticipant[] participants = comparableFlows.Select(value => FlowParticipant(value, StopsContinuation(value) ? CodeFindingParticipantRole.StopsContinuation : CodeFindingParticipantRole.SharedFlow, StopsContinuation(value) ? "May stop continuation" : "Shares method")).ToArray();
            Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.FlowInterruption, "Continuation paths differ", "One implementation can stop before another implementation continues.", "This is a source control-flow relationship. It does not establish runtime callback order or a gameplay failure.", finding.Target, participants));
        }
    }

    private void AddDeclarationRuntime(List<CodeFindingWitness> witnesses, string evidenceSha256, string target, TweakRuntimeEvidence runtime)
    {
        TweakOperation[] declarations = EnsureTweaks(runtime.Declarations);
        SharedStateWrite[] writes = EnsureWrites(runtime.Writes);
        TweakRuntimeEvidence numbered = new(declarations, writes);
        var competing = numbered.CompetingValues().ToArray();
        if (competing.Length == 0) return;
        HashSet<string> declarationIds = competing.Select(value => value.Declaration.OperationId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> writeIds = competing.Select(value => value.Write.OperationId).ToHashSet(StringComparer.Ordinal);
        CodeFindingParticipant[] participants = declarations.Where(value => declarationIds.Contains(value.OperationId)).Select(value => TweakParticipant(value, value.IsMutation ? CodeFindingParticipantRole.ArrayAddition : CodeFindingParticipantRole.ValueDeclaration, value.IsMutation ? "Adds array member" : "Declares value", target))
            .Concat(writes.Where(value => writeIds.Contains(value.OperationId)).Select(value => RuntimeParticipant(value, target, ConflictSurface.ScriptAndTweak))).ToArray();
        Add(witnesses, Create(evidenceSha256, CodeFindingWitnessKind.DeclarationRuntimeValue, "Declared value and runtime write differ", "A TweakXL declaration and another provider's code request different values for the same field.", "The declaration and runtime request differ. Their execution order and the value used in game are not established here.", target, participants));
    }

    private CodeFindingParticipant TweakParticipant(TweakOperation operation, CodeFindingParticipantRole role, string roleLabel, string member)
        => new(operation.OperationId, role, roleLabel, CodeOperationIdentity.Kind(operation.Kind), operation.Provider, operation.FilePath, operation.LineNumber, member, operation.Value, TweakValueType(operation.Kind), Sources(operation.OperationId, ConflictSurface.ScriptAndTweak));

    private CodeFindingParticipant FlowParticipant(RedScriptFlowEvidence flow, CodeFindingParticipantRole role, string roleLabel)
        => new(flow.OperationId, role, roleLabel, CodeOperationIdentity.Kind(flow.Kind), flow.Provider, flow.FilePath, flow.Line, flow.Target, flow.BodySha256, "method body", Sources(flow.OperationId, ConflictSurface.ScriptAndTweak));

    private CodeFindingParticipant FieldParticipant(RedScriptFieldDeclaration field, string target)
        => new(field.OperationId, CodeFindingParticipantRole.AddedField, "Adds field", CodeEvidenceOperationKind.RedScriptAddField, field.Provider, field.FilePath, field.Line, target, field.Type, "field type", Sources(field.OperationId, ConflictSurface.ScriptAndTweak));

    private CodeFindingParticipant RuntimeParticipant(SharedStateWrite write, string target, ConflictSurface surface)
    {
        string? value = TweakRuntimeEvidence.ScalarLiteral(write);
        string valueType = value is null && (write.Evidence.Contains("{}", StringComparison.Ordinal) || write.Evidence.Contains("[]", StringComparison.Ordinal)) ? "empty collection" : "scalar";
        if (valueType == "empty collection") value = "[]";
        return new(write.OperationId, CodeFindingParticipantRole.RuntimeWrite, "Writes value at runtime", CodeOperationIdentity.Kind(write.Surface), write.Provider, write.FilePath, write.Line, target, value, valueType, Sources(write.OperationId, surface));
    }

    private CodeSourceEvidence[] Sources(string operationId, ConflictSurface surface)
        => string.IsNullOrEmpty(operationId) || !_sources.TryGetValue((operationId, surface), out CodeSourceEvidence[]? sources) ? [] : sources;

    private static CodeFindingWitness? Create(string evidenceSha256, CodeFindingWitnessKind kind, string title, string description, string boundary, string? member, CodeFindingParticipant[] rawParticipants)
    {
        CodeFindingParticipant[] participants = rawParticipants.OrderBy(value => value.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.FilePath, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.Line).ThenBy(value => value.OperationId, StringComparer.Ordinal).ToArray();
        CodeFindingWitness candidate = new(string.Empty, kind, title, description, boundary, member, participants);
        participants = participants.Where(candidate.HasOpponent).ToArray();
        string stableId = SemanticEvidence.Sha256(string.Join("|", new[] { evidenceSha256, kind.ToString(), member ?? string.Empty }.Concat(participants.Select(value => value.OperationId))));
        CodeFindingWitness witness = new(stableId, kind, title, description, boundary, member, participants);
        return witness.HasComparison() ? witness : null;
    }

    private static void Add(List<CodeFindingWitness> witnesses, CodeFindingWitness? witness)
    {
        if (witness is not null) witnesses.Add(witness);
    }

    private static TweakOperation[] EnsureTweaks(IEnumerable<TweakOperation> operations)
    {
        TweakOperation[] values = operations.ToArray();
        return values.All(value => !string.IsNullOrEmpty(value.OperationId)) ? values : CodeOperationIdentity.NumberTweaks(values).ToArray();
    }

    private static RedScriptFlowEvidence[] EnsureFlows(IEnumerable<RedScriptFlowEvidence> flows)
    {
        RedScriptFlowEvidence[] values = flows.ToArray();
        return values.All(value => !string.IsNullOrEmpty(value.OperationId)) ? values : CodeOperationIdentity.NumberFlows(values).ToArray();
    }

    private static SharedStateWrite[] EnsureWrites(IEnumerable<SharedStateWrite> writes)
    {
        SharedStateWrite[] values = writes.ToArray();
        return values.All(value => !string.IsNullOrEmpty(value.OperationId)) ? values : CodeOperationIdentity.NumberWrites(values).ToArray();
    }

    private static RedScriptFieldDeclaration[] EnsureFields(string target, IEnumerable<RedScriptFieldDeclaration> fields)
    {
        RedScriptFieldDeclaration[] values = fields.ToArray();
        if (values.All(value => !string.IsNullOrEmpty(value.OperationId))) return values;
        Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
        return values.Select(value =>
        {
            string identity = CodeOperationIdentity.FieldBase(target, value);
            int occurrence = occurrences.GetValueOrDefault(identity) + 1;
            occurrences[identity] = occurrence;
            return value with { OperationOccurrence = occurrence, OperationId = CodeOperationIdentity.FieldId(target, value, occurrence) };
        }).ToArray();
    }

    private static bool StopsContinuation(RedScriptFlowEvidence flow)
        => flow.Kind == RedScriptFlowKind.Replace || flow.Kind == RedScriptFlowKind.Wrap && flow.Continuation is RedScriptContinuationEvidence.Missing or RedScriptContinuationEvidence.EarlyReturnBeforeContinuation;

    private static string TweakValueType(TweakOperationKind kind) => kind switch
    {
        TweakOperationKind.TypeDeclaration => "record type",
        TweakOperationKind.BaseDeclaration => "base record",
        TweakOperationKind.ScalarAssignment => "scalar",
        TweakOperationKind.ArrayReplacement => "array",
        TweakOperationKind.InlineRecord => "record",
        _ => "array member"
    };
}
