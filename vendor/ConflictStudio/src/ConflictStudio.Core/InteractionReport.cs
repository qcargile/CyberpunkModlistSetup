using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

public enum InteractionFindingKind { Exclusive, Review, Composable, Informational }

public sealed record RedScriptFieldDeclaration(string Provider, string FilePath, int Line, string Type)
{
    [JsonIgnore]
    public string OperationId { get; init; } = string.Empty;

    [JsonIgnore]
    public int OperationOccurrence { get; init; }
}

public sealed record TweakRuntimeEvidence(TweakOperation[] Declarations, SharedStateWrite[] Writes)
{
    private static readonly Regex LiteralArgument = new(@"\(\s*[tn]?(?<quote>['""])[^'""]+\k<quote>\s*,\s*(?<value>(?<empty>\{\s*\}|\[\s*\])|true|false|[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?)\s*\)$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal IEnumerable<(TweakOperation Declaration, SharedStateWrite Write, string Value)> CompetingValues()
    {
        foreach (SharedStateWrite write in Writes)
        {
            Match literal = LiteralArgument.Match(write.Evidence);
            if (!literal.Success) continue;
            string value = literal.Groups["value"].Value;
            foreach (TweakOperation declaration in Declarations)
            {
                if (declaration.Target != write.Target
                    || string.Equals(declaration.Provider, write.Provider, StringComparison.OrdinalIgnoreCase)) continue;
                bool different = literal.Groups["empty"].Success
                    ? declaration.Kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayPrepend or TweakOperationKind.ArrayPrependOnce
                        || declaration.Kind == TweakOperationKind.ArrayReplacement && declaration.Value != "[]"
                    : declaration.Kind == TweakOperationKind.ScalarAssignment && ScalarsDiffer(declaration.Value, value);
                if (different) yield return (declaration, write, value);
            }
        }
    }

    internal static string? ScalarLiteral(SharedStateWrite write)
    {
        Match literal = LiteralArgument.Match(write.Evidence);
        return literal.Success && !literal.Groups["empty"].Success ? literal.Groups["value"].Value : null;
    }

    internal static bool ScalarsDiffer(string first, string second)
        => decimal.TryParse(first, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal initial)
            && decimal.TryParse(second, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal updated)
            ? initial != updated
            : bool.TryParse(first, out bool initialFlag) && bool.TryParse(second, out bool updatedFlag) && initialFlag != updatedFlag;
}

public sealed record InteractionFinding(string Target, InteractionFindingKind Kind, string Summary, string[] Providers)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RedScriptFieldDeclaration[]? DeclarationEvidence { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TweakRuntimeEvidence? TweakRuntimeEvidence { get; init; }
}

public static class InteractionReportBuilder
{
    public static InteractionFinding[] Build(ModSourceInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        RedScriptFlowEvidence[] redScriptFlows = RedScriptFlowEvidenceAnalyzer.Analyze(inventory.RedScripts);
        LuaCallbackEvidence[] luaCallbacks = LuaCallbackEvidenceAnalyzer.Analyze(inventory.LuaSources);
        TweakAnalysisResult tweaks = TweakInteractionAnalyzer.AnalyzeDetailed(inventory.TweakSources);
        return Build(inventory, redScriptFlows, luaCallbacks, tweaks.Overlaps, tweaks.Operations, SharedStateWriteAnalyzer.Collect(inventory.RedScripts, inventory.LuaSources));
    }

    public static InteractionFinding[] Build(ModSourceInventory inventory, IReadOnlyList<RedScriptFlowEvidence> redScriptFlows, IReadOnlyList<LuaCallbackEvidence> luaCallbacks, IReadOnlyList<TweakOverlap> tweakOverlaps)
        => Build(inventory, redScriptFlows, luaCallbacks, tweakOverlaps, TweakInteractionAnalyzer.AnalyzeDetailed(inventory.TweakSources).Operations, SharedStateWriteAnalyzer.Collect(inventory.RedScripts, inventory.LuaSources));

    public static InteractionFinding[] Build(ModSourceInventory inventory, IReadOnlyList<RedScriptFlowEvidence> redScriptFlows, IReadOnlyList<LuaCallbackEvidence> luaCallbacks, IReadOnlyList<TweakOverlap> tweakOverlaps, IReadOnlyList<TweakOperation> tweakOperations, IReadOnlyList<SharedStateWrite> runtimeWrites)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(redScriptFlows);
        ArgumentNullException.ThrowIfNull(luaCallbacks);
        ArgumentNullException.ThrowIfNull(tweakOverlaps);
        ArgumentNullException.ThrowIfNull(tweakOperations);
        ArgumentNullException.ThrowIfNull(runtimeWrites);
        ILookup<string, LuaCallbackEvidence> callbacksByTarget = luaCallbacks.ToLookup(value => value.Target, StringComparer.Ordinal);
        List<InteractionFinding> findings = [];
        findings.AddRange(RedScriptInteractionAnalyzer.Analyze(inventory.RedScripts).Select(RedScriptFinding));
        findings.AddRange(LuaInteractionAnalyzer.Analyze(inventory.LuaSources).Select(LuaFinding));
        findings.AddRange(CrossLanguageFindings(redScriptFlows, callbacksByTarget));
        findings.AddRange(tweakOverlaps.Select(TweakFinding));
        findings.AddRange(TweakRuntimeFindings(tweakOperations, runtimeWrites));
        return findings.GroupBy(value => value.Target, StringComparer.Ordinal)
            .Select(group =>
            {
                RedScriptFieldDeclaration[] declarations = group.SelectMany(value => value.DeclarationEvidence ?? []).ToArray();
                InteractionFinding? runtime = group.FirstOrDefault(value => value.TweakRuntimeEvidence is not null);
                return new InteractionFinding(group.Key, group.Min(value => value.Kind), string.Join(" ", group.Select(value => value.Summary).Distinct(StringComparer.Ordinal)), group.OrderBy(value => value.Providers.Length == 1).SelectMany(value => value.Providers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
                {
                    DeclarationEvidence = declarations.Length == 0 ? null : declarations,
                    TweakRuntimeEvidence = runtime?.TweakRuntimeEvidence
                };
            })
            .OrderBy(value => value.Kind)
            .ThenBy(value => value.Target, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<InteractionFinding> TweakRuntimeFindings(IReadOnlyList<TweakOperation> operations, IReadOnlyList<SharedStateWrite> writes)
    {
        ILookup<string, SharedStateWrite> writesByTarget = writes.Where(value => value.Surface == SharedStateSurface.TweakDb).ToLookup(value => value.Target, StringComparer.Ordinal);
        foreach (IGrouping<string, TweakOperation> group in operations.ToLookup(value => value.Target, StringComparer.Ordinal))
        {
            SharedStateWrite[] runtime = writesByTarget[group.Key].ToArray();
            if (runtime.Length == 0) continue;
            TweakOperation[] declarations = group.ToArray();
            string[] providers = declarations.Select(value => value.Provider).Concat(runtime.Select(value => value.Provider)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            TweakRuntimeEvidence evidence = new(declarations, runtime);
            bool competing = evidence.CompetingValues().Any();
            yield return new InteractionFinding(group.Key, competing ? InteractionFindingKind.Review : InteractionFindingKind.Informational,
                competing ? "Different providers request different literal values for this TweakDB field. The runtime writer can replace the declared value; no in-game outcome is claimed."
                    : "Declarations and runtime writes refer to this TweakDB field. No competing value is established by this relationship alone.", providers)
            {
                TweakRuntimeEvidence = evidence
            };
        }
    }

    private static IEnumerable<InteractionFinding> CrossLanguageFindings(IReadOnlyList<RedScriptFlowEvidence> flows, ILookup<string, LuaCallbackEvidence> callbacksByTarget)
    {
        foreach (RedScriptFlowEvidence flow in flows.Where(value => value.Kind == RedScriptFlowKind.Add))
        {
            string target = MethodBase(flow.Target);
            LuaCallbackEvidence[] overrides = callbacksByTarget[target].Where(value => value.Kind == LuaCallbackEvidenceKind.Override && value.Confidence == EvidenceConfidence.Literal).ToArray();
            string[] providers = overrides.SelectMany(value => value.Copies.Select(copy => copy.Provider)).Append(flow.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (overrides.Length == 0 || providers.Length < 2) continue;
            yield return new InteractionFinding(flow.Target, InteractionFindingKind.Informational,
                "A CET override targets a RedScript method added by another provider. This relationship alone does not establish a conflict.", providers);
        }
    }

    internal static string MethodBase(string target)
    {
        int parameters = target.IndexOf('(');
        return parameters < 0 ? target : target[..parameters];
    }

    private static InteractionFinding RedScriptFinding(RedScriptOverlap overlap)
    {
        string[] providers = overlap.Hooks.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (overlap.Kind == RedScriptOverlapKind.ExclusiveReplacement) return new InteractionFinding(overlap.Target, InteractionFindingKind.Exclusive, "Multiple active replacements declare exclusive source ownership of this method.", providers);
        if (overlap.Kind == RedScriptOverlapKind.RedundantReplacement) return new InteractionFinding(overlap.Target, InteractionFindingKind.Composable, providers.Length == 1 ? "Multiple declarations contain the same replacement body in analyzed source. This is redundant related source evidence, not a competing source outcome." : "Multiple active providers contain the same replacement body in analyzed source. This is redundant related source evidence, not a competing source outcome.", providers);
        if (overlap.Hooks.Count(value => value.Kind is RedScriptHookKind.AddMethod or RedScriptHookKind.AddField) > 1)
        {
            RedScriptFieldDeclaration[] fields = overlap.Hooks.Where(value => value.Declaration is not null).Select(value => value.Declaration!).ToArray();
            return new InteractionFinding(overlap.Target, InteractionFindingKind.Review, providers.Length == 1 ? "Multiple declarations add the same RedScript member. This is a source overlap; no compiler outcome is established by this report." : "Multiple active providers add the same RedScript member. This is a source overlap; no compiler outcome is established by this report.", providers)
            {
                DeclarationEvidence = fields.Length == 0 ? null : fields
            };
        }
        return new InteractionFinding(overlap.Target, InteractionFindingKind.Informational,
            "These declarations target the same method. Continuation markers alone do not establish conflicting behavior or compatibility.", providers);
    }

    private static InteractionFinding LuaFinding(LuaOverlap overlap)
    {
        string[] providers = overlap.Hooks.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new InteractionFinding(overlap.Target, InteractionFindingKind.Informational,
            "These CET callbacks target the same method. Their registrations alone do not establish conflicting behavior or compatibility.", providers);
    }

    private static InteractionFinding TweakFinding(TweakOverlap overlap)
    {
        string[] providers = overlap.Operations.Select(operation => operation.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string multiple = providers.Length == 1 ? "Multiple declarations" : "Multiple providers";
        string active = providers.Length == 1 ? "Active declarations" : "Active providers";
        InteractionFindingKind kind = overlap.Kind is TweakOverlapKind.BaseRecordDependency or TweakOverlapKind.InternalContext ? InteractionFindingKind.Informational
            : overlap.Kind is TweakOverlapKind.ComposableMutation or TweakOverlapKind.AssignmentThenMutation or TweakOverlapKind.Redundant ? InteractionFindingKind.Composable : InteractionFindingKind.Review;
        string summary = overlap.Kind == TweakOverlapKind.InternalContext ? "This provider contains differing source definitions. The source locations are context, not evidence of competing active components."
            : overlap.Kind == TweakOverlapKind.OpposingMutation ? "Separate source components add and remove the same array entries. Known operation order does not satisfy both requested outcomes."
            : overlap.Kind == TweakOverlapKind.Redundant ? "All active providers declare the same value."
            : overlap.Kind == TweakOverlapKind.AssignmentThenMutation ? "TweakXL commits the array assignment before applying removals, prepends, and appends."
            : overlap.Kind == TweakOverlapKind.ComposableMutation ? "TweakXL applies these mutations in documented phases; no competing assignment or duplicate insertion is present."
            : overlap.Kind == TweakOverlapKind.DuplicateMutation ? $"{multiple} append the same array element without a uniqueness guard, so the final array may contain duplicates."
            : overlap.Kind == TweakOverlapKind.RecordDefinitionCollision ? $"{multiple} construct the same record differently. TweakXL keeps the first construction it encounters, but normal-file discovery order is not reproduced here."
            : overlap.Kind == TweakOverlapKind.SourceArrayDependency ? "A producer-consumer dependency exists because a provider copies from an array another provider changes. The copied final contents are not resolved statically."
            : overlap.Kind == TweakOverlapKind.BaseRecordDependency ? "A direct $base declaration names a record whose properties another provider changes. This is dependency provenance, not a resolved inherited value or a conflict verdict."
            : overlap.Kind == TweakOverlapKind.ScalarOverwrite ? $"{active} declare competing scalar values. This report does not infer a final winner."
            : $"{active} declare competing array operations. This report preserves the documented operations without resolving a final result.";
        return new InteractionFinding(overlap.Target, kind, summary, providers);
    }
}
