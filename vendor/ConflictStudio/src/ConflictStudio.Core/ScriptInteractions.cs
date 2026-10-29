using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ConflictStudio.Core;

public sealed record RedScriptSource(string Provider, string FilePath, string Text);

public enum RedScriptHookKind { Wrap, Replace, AddMethod, AddField }

public enum RedScriptOverlapKind { ExclusiveReplacement, CompositionReview, AddedMemberCollision, AddedMemberInteraction, RedundantReplacement }

public sealed record RedScriptHook(string Provider, string FilePath, RedScriptHookKind Kind, string Target, string? BodySha256 = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RedScriptFieldDeclaration? Declaration { get; init; }
}

public sealed record RedScriptOverlap(string Target, RedScriptOverlapKind Kind, RedScriptHook[] Hooks);

public static class RedScriptInteractionAnalyzer
{
    private static readonly Regex Annotation = new("^[ \\t]*@(?<kind>wrapMethod|replaceMethod|addMethod|addField)\\((?<class>[^)]+)\\)", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex Method = new("\\bfunc\\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\\s*\\((?<parameters>[^)]*)\\)", RegexOptions.Compiled);
    private static readonly Regex Field = new("\\blet\\s+(?<field>[A-Za-z_][A-Za-z0-9_]*)\\s*:\\s*(?<type>[^;=]+)", RegexOptions.Compiled);

    public static RedScriptOverlap[] Analyze(IReadOnlyList<RedScriptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        Dictionary<(string Provider, string FilePath, string Target), Queue<string?>> bodies = RedScriptFlowEvidenceAnalyzer.Analyze(sources)
            .GroupBy(value => (value.Provider, value.FilePath, value.Target))
            .ToDictionary(group => group.Key, group => new Queue<string?>(group.OrderBy(value => value.Line).Select(value => value.BodySha256)));
        List<RedScriptHook> hooks = [];
        foreach (RedScriptSource source in RedScriptConditionalSourceFilter.Filter(sources))
        {
            string code = SourceTextMask.RedScript(source.Text, false);
            foreach (Match annotation in Annotation.Matches(code))
            {
                RedScriptHookKind kind = annotation.Groups["kind"].Value switch
                {
                    "wrapMethod" => RedScriptHookKind.Wrap,
                    "replaceMethod" => RedScriptHookKind.Replace,
                    "addMethod" => RedScriptHookKind.AddMethod,
                    _ => RedScriptHookKind.AddField
                };
                Match declaration = kind == RedScriptHookKind.AddField ? Field.Match(code, annotation.Index + annotation.Length) : Method.Match(code, annotation.Index + annotation.Length);
                Match nextAnnotation = annotation.NextMatch();
                if (!declaration.Success || nextAnnotation.Success && nextAnnotation.Index < declaration.Index) continue;
                string className = annotation.Groups["class"].Value.Trim();
                string target = kind == RedScriptHookKind.AddField
                    ? className + "." + declaration.Groups["field"].Value
                    : RedScriptTarget.Normalize(className, declaration.Groups["method"].Value, declaration.Groups["parameters"].Value);
                string? bodySha256 = bodies.TryGetValue((source.Provider, source.FilePath, target), out Queue<string?>? bodyQueue) && bodyQueue.Count > 0 ? bodyQueue.Dequeue() : null;
                hooks.Add(new RedScriptHook(source.Provider, source.FilePath, kind, target, bodySha256)
                {
                    Declaration = kind == RedScriptHookKind.AddField
                        ? new RedScriptFieldDeclaration(source.Provider, source.FilePath, RedScriptFlowEvidenceAnalyzer.LineAt(source.Text, declaration.Index), declaration.Groups["type"].Value.Trim())
                        : null
                });
            }
        }

        Dictionary<string, int> fieldOccurrences = new(StringComparer.Ordinal);
        for (int index = 0; index < hooks.Count; index++)
        {
            RedScriptHook hook = hooks[index];
            if (hook.Declaration is not { } declaration) continue;
            string identity = CodeOperationIdentity.FieldBase(hook.Target, declaration);
            int occurrence = fieldOccurrences.GetValueOrDefault(identity) + 1;
            fieldOccurrences[identity] = occurrence;
            hooks[index] = hook with { Declaration = declaration with { OperationOccurrence = occurrence, OperationId = CodeOperationIdentity.FieldId(hook.Target, declaration, occurrence) } };
        }

        return hooks.GroupBy(value => value.Target, StringComparer.Ordinal)
            .Select(group => new RedScriptOverlap(group.Key, Classify(group), group.ToArray()))
            .Where(ShouldReport)
            .OrderBy(value => value.Target, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ShouldReport(RedScriptOverlap overlap)
        => overlap.Hooks.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || overlap.Kind is RedScriptOverlapKind.ExclusiveReplacement or RedScriptOverlapKind.RedundantReplacement or RedScriptOverlapKind.AddedMemberCollision;

    private static RedScriptOverlapKind Classify(IEnumerable<RedScriptHook> hooks)
    {
        RedScriptHook[] values = hooks.ToArray();
        if (values.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
        {
            if (values.Count(value => value.Kind is RedScriptHookKind.AddMethod or RedScriptHookKind.AddField) > 1) return RedScriptOverlapKind.AddedMemberCollision;
            RedScriptHook[] internalReplacements = values.Where(value => value.Kind == RedScriptHookKind.Replace).ToArray();
            if (internalReplacements.Length > 1) values = internalReplacements;
            else return RedScriptOverlapKind.CompositionReview;
        }
        if (values.All(value => value.Kind is RedScriptHookKind.AddMethod or RedScriptHookKind.AddField)) return RedScriptOverlapKind.AddedMemberCollision;
        if (values.Any(value => value.Kind is RedScriptHookKind.AddMethod or RedScriptHookKind.AddField)) return RedScriptOverlapKind.AddedMemberInteraction;
        RedScriptHook[] replacements = values.Where(value => value.Kind == RedScriptHookKind.Replace).ToArray();
        if (replacements.Length <= 1) return RedScriptOverlapKind.CompositionReview;
        bool identicalReplacements = replacements.All(value => value.BodySha256 is not null) && replacements.Select(value => value.BodySha256).Distinct(StringComparer.Ordinal).Count() == 1;
        if (!identicalReplacements) return RedScriptOverlapKind.ExclusiveReplacement;
        return replacements.Length == values.Length ? RedScriptOverlapKind.RedundantReplacement : RedScriptOverlapKind.CompositionReview;
    }
}

internal static class RedScriptTarget
{
    public static string Normalize(string className, string methodName, string parameters)
    {
        string[] types = Split(parameters)
            .Select(ParameterType)
            .Where(value => value.Length > 0)
            .ToArray();
        return className.Trim() + "." + methodName + "(" + string.Join(", ", types) + ")";
    }

    private static IEnumerable<string> Split(string parameters)
    {
        int start = 0;
        int depth = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            char value = parameters[index];
            if (value is '<' or '[' or '(') depth++;
            else if (value is '>' or ']' or ')') depth--;
            else if (value == ',' && depth == 0)
            {
                yield return parameters[start..index];
                start = index + 1;
            }
        }
        if (start < parameters.Length) yield return parameters[start..];
    }

    private static string ParameterType(string parameter)
    {
        int colon = parameter.IndexOf(':');
        string type = colon < 0 ? parameter.Trim() : parameter[(colon + 1)..].Trim();
        int assignment = type.IndexOf('=');
        if (assignment >= 0) type = type[..assignment].Trim();
        return NormalizeType(type);
    }

    public static string NormalizeType(string type)
    {
        type = Regex.Replace(type, "\\s+", string.Empty);
        string previous;
        do
        {
            previous = type;
            type = Regex.Replace(type, "\\[(?<inner>[^\\[\\]]+)\\]", "array<${inner}>");
        }
        while (type != previous);
        return type;
    }
}

public sealed record LuaSource(string Provider, string FilePath, string Text);

public enum LuaHookKind { Observe, Override }

public enum LuaOverlapKind { ObserverComposition, OverrideWithObservers, OverrideReview, RedundantOverride }

public sealed record LuaHook(string Provider, string FilePath, LuaHookKind Kind, string Target);

public sealed record LuaOverlap(string Target, LuaOverlapKind Kind, LuaHook[] Hooks);

public static class LuaInteractionAnalyzer
{
    private static readonly Regex ReturnOnlyCallback = new("""\Afunction\s*\([^()]*\)\s*return\s+(?:true|false|nil|-?[0-9]+(?:\.[0-9]+)?|"(?:[^"\\]|\\.)*"|'(?:[^'\\]|\\.)*'|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)+)\s*;?\s*end\z""", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LuaOverlap[] Analyze(IReadOnlyList<LuaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        List<(LuaHook Hook, string? CallbackEvidence)> hooks = [];
        foreach (LuaSource source in sources)
        {
            foreach (LuaHookRegistration registration in LuaHookRegistrationAnalyzer.Analyze(source.Text).Where(value => value.Confidence == EvidenceConfidence.Literal))
            {
                LuaHookKind kind = registration.Kind == LuaCallbackEvidenceKind.Override ? LuaHookKind.Override : LuaHookKind.Observe;
                hooks.Add((new LuaHook(source.Provider, source.FilePath, kind, registration.Target), registration.CallbackEvidence));
            }
        }

        return hooks.GroupBy(value => value.Hook.Target, StringComparer.Ordinal)
            .Select(group =>
            {
                int overrides = group.Count(value => value.Hook.Kind == LuaHookKind.Override);
                LuaOverlapKind kind = overrides > 1 ? LuaOverlapKind.OverrideReview : overrides == 1 ? LuaOverlapKind.OverrideWithObservers : LuaOverlapKind.ObserverComposition;
                if (overrides > 1 && overrides == group.Count()
                    && group.Select(value => value.Hook.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    && group.Select(value => value.Hook.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    && group.Select(value => value.CallbackEvidence).Distinct(StringComparer.Ordinal).Count() == 1
                    && ReturnOnlyCallback.IsMatch(group.First().CallbackEvidence ?? string.Empty))
                    kind = LuaOverlapKind.RedundantOverride;
                return new LuaOverlap(group.Key, kind, group.Select(value => value.Hook).ToArray());
            })
            .Where(ShouldReport)
            .OrderBy(value => value.Target, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool ShouldReport(LuaOverlap overlap)
        => overlap.Hooks.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || overlap.Kind is LuaOverlapKind.OverrideReview or LuaOverlapKind.RedundantOverride;
}
