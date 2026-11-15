using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

public enum EvidenceConfidence { ExactToken, Literal, Dynamic }

public enum EvidenceImpact { None, Review }

public enum RedScriptFlowKind { Wrap, Replace, Add }

public enum RedScriptContinuationEvidence { Continues, EarlyReturnBeforeContinuation, Missing, NotApplicable }

public sealed record RedScriptFlowEvidence(
    string Provider,
    string FilePath,
    string Target,
    RedScriptFlowKind Kind,
    RedScriptContinuationEvidence Continuation,
    EvidenceConfidence Confidence,
    EvidenceImpact Impact,
    int Line,
    string SourceHash,
    string? BodySha256 = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string OperationId { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public int OperationOccurrence { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal int SourceStartIndex { get; init; } = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int SourceEndIndex { get; init; } = -1;
}

public static class RedScriptFlowEvidenceAnalyzer
{
    private static readonly Regex Annotation = new("@(?<kind>wrapMethod|replaceMethod|addMethod)\\s*\\(\\s*(?<class>[^)]+?)\\s*\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Method = new("\\bfunc\\s+(?<method>[A-Za-z_][A-Za-z0-9_]*)\\s*\\((?<parameters>[^)]*)\\)[^{]*\\{", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Continuation = new("\\bwrappedMethod\\s*\\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Return = new("\\breturn\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RedScriptFlowEvidence[] Analyze(IReadOnlyList<RedScriptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        List<RedScriptFlowEvidence> evidence = [];
        foreach (RedScriptSource source in RedScriptConditionalSourceFilter.Filter(sources))
        {
            string code = SourceTextMask.RedScript(source.Text, true);
            string semanticCode = SourceTextMask.RedScript(source.Text, false);
            foreach (Match annotation in Annotation.Matches(code))
            {
                Match method = Method.Match(code, annotation.Index + annotation.Length);
                Match nextAnnotation = annotation.NextMatch();
                if (!method.Success || nextAnnotation.Success && nextAnnotation.Index < method.Index) continue;
                int openingBrace = method.Index + method.Length - 1;
                int closingBrace = ClosingBrace(code, openingBrace);
                if (closingBrace < 0) continue;
                string body = code[(openingBrace + 1)..closingBrace];
                RedScriptFlowKind kind = ParseKind(annotation.Groups["kind"].Value);
                RedScriptContinuationEvidence continuation = ContinuationFor(kind, body);
                string target = RedScriptTarget.Normalize(annotation.Groups["class"].Value, method.Groups["method"].Value, method.Groups["parameters"].Value);
                string declarationAndBody = semanticCode[method.Index..(closingBrace + 1)];
                evidence.Add(new RedScriptFlowEvidence(
                    source.Provider,
                    source.FilePath,
                    target,
                    kind,
                    continuation,
                    EvidenceConfidence.ExactToken,
                    ImpactFor(kind, continuation),
                    LineAt(source.Text, method.Index),
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text))),
                    SemanticEvidence.Sha256(target + "|" + declarationAndBody))
                {
                    SourceStartIndex = annotation.Index,
                    SourceEndIndex = closingBrace
                });
            }
        }

        return CodeOperationIdentity.NumberFlows(evidence.OrderBy(value => value.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Line)
            .ToArray()).ToArray();
    }

    private static int ClosingBrace(string text, int openingBrace)
    {
        int depth = 0;
        for (int index = openingBrace; index < text.Length; index++)
        {
            if (text[index] == '{') depth++;
            if (text[index] == '}' && --depth == 0) return index;
        }

        return -1;
    }

    private static RedScriptFlowKind ParseKind(string value) => value switch
    {
        "wrapMethod" => RedScriptFlowKind.Wrap,
        "replaceMethod" => RedScriptFlowKind.Replace,
        _ => RedScriptFlowKind.Add
    };

    private static RedScriptContinuationEvidence ContinuationFor(RedScriptFlowKind kind, string body)
    {
        if (kind != RedScriptFlowKind.Wrap) return RedScriptContinuationEvidence.NotApplicable;
        Match[] continuations = Continuation.Matches(body).Cast<Match>().ToArray();
        if (continuations.Length == 0) return RedScriptContinuationEvidence.Missing;
        Match? topLevelContinuation = continuations.FirstOrDefault(value => BraceDepthAt(body, value.Index) == 0 && StatementAlwaysContinues(body, value));
        if (topLevelContinuation is null) return EveryBranchContinues(body) ? RedScriptContinuationEvidence.Continues : RedScriptContinuationEvidence.EarlyReturnBeforeContinuation;
        foreach (Match returnToken in Return.Matches(body).Cast<Match>().Where(value => value.Index < topLevelContinuation.Index))
        {
            if (!ReturnPathContinues(body, returnToken)) return RedScriptContinuationEvidence.EarlyReturnBeforeContinuation;
        }
        return RedScriptContinuationEvidence.Continues;
    }

    private static bool EveryBranchContinues(string body)
    {
        Match branch = Regex.Match(body, "\\bif\\b", RegexOptions.CultureInvariant);
        while (branch.Success)
        {
            if (BraceDepthAt(body, branch.Index) == 0)
            {
                if (HasSuppressingReturn(body[..branch.Index])) return false;
                int thenOpen = body.IndexOf('{', branch.Index);
                if (thenOpen >= 0)
                {
                    int thenClose = ClosingBrace(body, thenOpen);
                    if (thenClose >= 0)
                    {
                        Match elseToken = Regex.Match(body[(thenClose + 1)..], "^\\s*;?\\s*else\\s*\\{", RegexOptions.CultureInvariant);
                        if (elseToken.Success)
                        {
                            int elseOpen = body.IndexOf('{', thenClose + 1 + elseToken.Index);
                            int elseClose = ClosingBrace(body, elseOpen);
                            if (elseClose >= 0
                                && BranchContinues(body[(thenOpen + 1)..thenClose])
                                && BranchContinues(body[(elseOpen + 1)..elseClose])) return true;
                        }
                    }
                }
            }
            branch = branch.NextMatch();
        }
        return false;
    }

    private static bool HasSuppressingReturn(string body)
    {
        foreach (Match returnToken in Return.Matches(body))
        {
            if (!ReturnPathContinues(body, returnToken)) return true;
        }
        return false;
    }

    private static int EnclosingBlockStart(string body, int position)
    {
        Stack<int> blocks = [];
        for (int index = 0; index < position; index++)
        {
            if (body[index] == '{') blocks.Push(index + 1);
            else if (body[index] == '}' && blocks.Count > 0) blocks.Pop();
        }
        return blocks.Count == 0 ? 0 : blocks.Peek();
    }

    private static bool BranchContinues(string body)
    {
        Match? topLevelContinuation = Continuation.Matches(body).Cast<Match>().FirstOrDefault(value => BraceDepthAt(body, value.Index) == 0 && StatementAlwaysContinues(body, value));
        if (topLevelContinuation is null) return EveryBranchContinues(body);
        foreach (Match returnToken in Return.Matches(body).Cast<Match>().Where(value => value.Index < topLevelContinuation.Index))
        {
            if (!ReturnPathContinues(body, returnToken)) return false;
        }
        return true;
    }

    private static bool ReturnPathContinues(string body, Match returnToken)
    {
        int statementEnd = body.IndexOf(';', returnToken.Index);
        string expression = statementEnd < 0 ? body[(returnToken.Index + returnToken.Length)..] : body[(returnToken.Index + returnToken.Length)..statementEnd];
        if (ExpressionAlwaysContinues(expression)) return true;
        int pathStart = EnclosingBlockStart(body, returnToken.Index);
        string prefix = body[pathStart..returnToken.Index];
        return Continuation.Matches(prefix).Cast<Match>().Any(value => BraceDepthAt(prefix, value.Index) == 0 && StatementAlwaysContinues(prefix, value)) || EveryBranchContinues(prefix);
    }

    private static bool StatementAlwaysContinues(string body, Match continuation)
    {
        int start = 0;
        int depth = 0;
        for (int index = 0; index < continuation.Index; index++)
        {
            if (body[index] == '{') depth++;
            else if (body[index] == '}')
            {
                depth--;
                if (depth == 0) start = index + 1;
            }
            else if (body[index] == ';' && depth == 0) start = index + 1;
        }
        int end = body.Length;
        for (int index = continuation.Index; index < body.Length; index++)
        {
            if (body[index] == ';' && BraceDepthAt(body, index) == 0)
            {
                end = index;
                break;
            }
        }
        return ExpressionAlwaysContinues(body[start..end]);
    }

    private static bool ExpressionAlwaysContinues(string expression)
    {
        string value = TrimOuterParentheses(expression.Trim());
        if (!Continuation.IsMatch(value)) return false;
        string[] sequence = SplitTopLevel(value, ',');
        if (sequence.Length > 1) return sequence.Any(ExpressionAlwaysContinues);
        if (TrySplitTernary(value, out string condition, out string whenTrue, out string whenFalse))
        {
            return ExpressionAlwaysContinues(condition) || ExpressionAlwaysContinues(whenTrue) && ExpressionAlwaysContinues(whenFalse);
        }
        int shortCircuit = FindTopLevelOperator(value, "||");
        if (shortCircuit < 0) shortCircuit = FindTopLevelOperator(value, "&&");
        if (shortCircuit >= 0) return ExpressionAlwaysContinues(value[..shortCircuit]);
        if (Continuation.Matches(value).Cast<Match>().Any(match => ParenthesisDepthAt(value, match.Index) == 0)) return true;
        foreach ((int start, int end) in ParenthesizedGroups(value))
        {
            if (ExpressionAlwaysContinues(value[(start + 1)..end])) return true;
        }
        return false;
    }

    private static string TrimOuterParentheses(string value)
    {
        while (value.Length >= 2 && value[0] == '(' && ClosingParenthesis(value, 0) == value.Length - 1) value = value[1..^1].Trim();
        return value;
    }

    private static bool TrySplitTernary(string value, out string condition, out string whenTrue, out string whenFalse)
    {
        int question = FindTopLevel(value, '?');
        if (question < 0)
        {
            condition = whenTrue = whenFalse = string.Empty;
            return false;
        }
        int nested = 0;
        for (int index = question + 1; index < value.Length; index++)
        {
            if (ParenthesisDepthAt(value, index) != 0) continue;
            if (value[index] == '?') nested++;
            else if (value[index] == ':' && nested-- == 0)
            {
                condition = value[..question];
                whenTrue = value[(question + 1)..index];
                whenFalse = value[(index + 1)..];
                return true;
            }
        }
        condition = whenTrue = whenFalse = string.Empty;
        return false;
    }

    private static int FindTopLevelOperator(string value, string operation)
    {
        for (int index = 0; index <= value.Length - operation.Length; index++)
        {
            if (ParenthesisDepthAt(value, index) == 0 && value.AsSpan(index, operation.Length).SequenceEqual(operation)) return index;
        }
        return -1;
    }

    private static int FindTopLevel(string value, char token)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] == token && ParenthesisDepthAt(value, index) == 0) return index;
        }
        return -1;
    }

    private static string[] SplitTopLevel(string value, char token)
    {
        List<string> segments = [];
        int start = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != token || ParenthesisDepthAt(value, index) != 0) continue;
            segments.Add(value[start..index]);
            start = index + 1;
        }
        if (segments.Count == 0) return [value];
        segments.Add(value[start..]);
        return segments.ToArray();
    }

    private static int ParenthesisDepthAt(string value, int position)
    {
        int depth = 0;
        for (int index = 0; index < position; index++)
        {
            if (value[index] == '(') depth++;
            else if (value[index] == ')') depth--;
        }
        return depth;
    }

    private static int ClosingParenthesis(string value, int opening)
    {
        int depth = 0;
        for (int index = opening; index < value.Length; index++)
        {
            if (value[index] == '(') depth++;
            else if (value[index] == ')' && --depth == 0) return index;
        }
        return -1;
    }

    private static IEnumerable<(int Start, int End)> ParenthesizedGroups(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (value[index] != '(') continue;
            int end = ClosingParenthesis(value, index);
            if (end < 0) yield break;
            yield return (index, end);
            index = end;
        }
    }

    private static int BraceDepthAt(string text, int position)
    {
        int depth = 0;
        for (int index = 0; index < position; index++)
        {
            if (text[index] == '{') depth++;
            else if (text[index] == '}') depth--;
        }
        return depth;
    }

    private static EvidenceImpact ImpactFor(RedScriptFlowKind kind, RedScriptContinuationEvidence continuation)
        => kind == RedScriptFlowKind.Replace || continuation is RedScriptContinuationEvidence.EarlyReturnBeforeContinuation or RedScriptContinuationEvidence.Missing
            ? EvidenceImpact.Review
            : EvidenceImpact.None;

    internal static int LineAt(string text, int index)
    {
        int line = 1;
        for (int position = 0; position < index; position++)
        {
            if (text[position] == '\r')
            {
                line++;
                if (position + 1 < index && text[position + 1] == '\n') position++;
            }
            else if (text[position] == '\n') line++;
        }

        return line;
    }
}

public enum SharedStateSurface { TweakDb, Blackboard, StatusEffect, StatPool, Persistence }

public sealed record SharedStateWrite(string Provider, string FilePath, SharedStateSurface Surface, string Target, int Line, string Operation = "", string Evidence = "", string SourceHash = "", string? CallSha256 = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string OperationId { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public int OperationOccurrence { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    internal int SourceStartIndex { get; init; } = -1;

    [System.Text.Json.Serialization.JsonIgnore]
    internal int SourceEndIndex { get; init; } = -1;
}

public sealed record SharedStateWriteFinding(
    SharedStateSurface Surface,
    string Target,
    EvidenceConfidence Confidence,
    EvidenceImpact Impact,
    SharedStateWrite[] Writes)
{
    internal IEnumerable<(SharedStateWrite First, SharedStateWrite Second, string FirstValue, string SecondValue)> CompetingValues()
    {
        if (Surface != SharedStateSurface.TweakDb) yield break;
        for (int firstIndex = 0; firstIndex < Writes.Length; firstIndex++)
        {
            SharedStateWrite first = Writes[firstIndex];
            string? firstValue = TweakRuntimeEvidence.ScalarLiteral(first);
            if (firstValue is null) continue;
            for (int secondIndex = firstIndex + 1; secondIndex < Writes.Length; secondIndex++)
            {
                SharedStateWrite second = Writes[secondIndex];
                if (first.Target != second.Target || string.Equals(first.Provider, second.Provider, StringComparison.OrdinalIgnoreCase)) continue;
                string? secondValue = TweakRuntimeEvidence.ScalarLiteral(second);
                if (secondValue is not null && TweakRuntimeEvidence.ScalarsDiffer(firstValue, secondValue))
                    yield return (first, second, firstValue, secondValue);
            }
        }
    }
}

public static class SharedStateWriteAnalyzer
{
    private static readonly Regex TweakDb = new("\\b(?<receiver>TweakDBInterface|TweakDBManager|TweakDB)(?:\\.|:)(?<operation>SetFlat|SetFlatNoUpdate)\\s*\\(\\s*(?<prefix>[tn])?(?<quote>[\"'])(?<target>[^\"'\\\\\\r\\n]+)\\k<quote>(?=\\s*,)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Blackboard = new("\\b[A-Za-z_][A-Za-z0-9_]*(?:\\.|:)(?:SetBool|SetInt|SetFloat|SetName|SetString|SetVariant|SetEntity|SetVector|SetUint|SetUInt)\\s*\\(\\s*(?<target>GetAllBlackboardDefs\\(\\)\\.[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StatusEffect = new("\\b(?:StatusEffectHelper|[A-Za-z_][A-Za-z0-9_]*)(?:\\.|:)(?:ApplyStatusEffect|RemoveStatusEffect)\\s*\\(\\s*[^,\\r\\n]+,\\s*t?[\"'](?<target>[^\"']+)[\"']", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex StatPool = new("\\b[A-Za-z_][A-Za-z0-9_]*(?:\\.|:)(?:RequestChangingStatPoolValue|RequestSettingStatPoolValue)\\s*\\(\\s*[^,\\r\\n]+,\\s*gamedataStatPoolType\\.(?<target>[A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Persistence = new("\\b[A-Za-z_][A-Za-z0-9_]*(?:\\.|:)(?:SetFactStr|SetFactValue|SetQuestFact)\\s*\\(\\s*n?[\"'](?<target>[^\"']+)[\"']", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SharedStateWriteFinding[] Analyze(IReadOnlyList<RedScriptSource> redScripts, IReadOnlyList<LuaSource> luaSources)
        => Analyze(Collect(redScripts, luaSources));

    public static SharedStateWrite[] Collect(IReadOnlyList<RedScriptSource> redScripts, IReadOnlyList<LuaSource> luaSources)
    {
        ArgumentNullException.ThrowIfNull(redScripts);
        ArgumentNullException.ThrowIfNull(luaSources);
        List<SharedStateWrite> writes = [];
        foreach (RedScriptSource source in RedScriptConditionalSourceFilter.Filter(redScripts)) Extract(source.Provider, source.FilePath, SourceTextMask.RedScript(source.Text, false), SourceTextMask.RedScript(source.Text, true), writes);
        foreach (LuaSource source in luaSources) Extract(source.Provider, source.FilePath, SourceTextMask.Lua(source.Text), SourceTextMask.Lua(source.Text, true), writes, lua: true);
        return CodeOperationIdentity.NumberWrites(writes).ToArray();
    }

    public static SharedStateWriteFinding[] Analyze(IReadOnlyList<SharedStateWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        return writes.GroupBy(value => (value.Surface, value.Target))
            .Where(group => group.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Select(group => new SharedStateWriteFinding(group.Key.Surface, group.Key.Target, EvidenceConfidence.Literal, EvidenceImpact.Review, group.ToArray()))
            .OrderBy(value => value.Surface)
            .ThenBy(value => value.Target, StringComparer.Ordinal)
            .ToArray();
    }

    private static void Extract(string provider, string filePath, string text, string syntax, List<SharedStateWrite> writes, bool lua = false)
    {
        string sourceHash = SemanticEvidence.Sha256(text);
        Extract(TweakDb, SharedStateSurface.TweakDb, provider, filePath, text, syntax, sourceHash, writes, lua);
        Extract(Blackboard, SharedStateSurface.Blackboard, provider, filePath, text, syntax, sourceHash, writes);
        Extract(StatusEffect, SharedStateSurface.StatusEffect, provider, filePath, text, syntax, sourceHash, writes);
        Extract(StatPool, SharedStateSurface.StatPool, provider, filePath, text, syntax, sourceHash, writes);
        Extract(Persistence, SharedStateSurface.Persistence, provider, filePath, text, syntax, sourceHash, writes);
    }

    private static void Extract(Regex pattern, SharedStateSurface surface, string provider, string filePath, string text, string syntax, string sourceHash, List<SharedStateWrite> writes, bool lua = false)
    {
        foreach (Match match in pattern.Matches(text))
        {
            if (syntax[match.Index] != text[match.Index]) continue;
            if (surface == SharedStateSurface.TweakDb)
            {
                if (lua && match.Groups["prefix"].Success) continue;
                if (match.Groups["prefix"].Value == "n" && (match.Groups["receiver"].Value != "TweakDBManager" || match.Groups["operation"].Value != "SetFlat")) continue;
                int receiver = match.Index - 1;
                while (receiver >= 0 && char.IsWhiteSpace(syntax[receiver])) receiver--;
                if (receiver >= 0 && syntax[receiver] is '.' or ':') continue;
            }
            string operation = Regex.Match(match.Value, "(?:\\.|:)(?<operation>[A-Za-z_][A-Za-z0-9_]*)\\s*\\(").Groups["operation"].Value;
            int end = surface == SharedStateSurface.TweakDb ? CallEnd(syntax, syntax.IndexOf('(', match.Index)) : -1;
            string evidence = surface == SharedStateSurface.TweakDb ? SemanticEvidence.Normalize(end < 0 ? match.Value : text[match.Index..(end + 1)]) : Regex.Replace(match.Value, "\\s+", " ").Trim();
            writes.Add(new SharedStateWrite(provider, filePath, surface, match.Groups["target"].Value, RedScriptFlowEvidenceAnalyzer.LineAt(text, match.Index), operation, evidence, sourceHash, end < 0 ? null : SemanticEvidence.Sha256(evidence))
            {
                SourceStartIndex = match.Index,
                SourceEndIndex = end
            });
        }
    }

    private static int CallEnd(string syntax, int opening)
    {
        if (opening < 0) return -1;
        int depth = 0;
        for (int index = opening; index < syntax.Length; index++)
        {
            if (syntax[index] == '(') depth++;
            else if (syntax[index] == ')' && --depth == 0) return index;
        }
        return -1;
    }
}

public enum LuaCallbackEvidenceKind { Observe, ObserveBefore, ObserveAfter, Override, Lifecycle }

public enum LuaContinuationEvidence { Continues, Missing, NotApplicable, Unknown, Conditional }

public sealed record LuaSourceCopy(string Provider, string FilePath);

public sealed record LuaCallbackEvidence(
    LuaCallbackEvidenceKind Kind,
    string Target,
    EvidenceConfidence Confidence,
    EvidenceImpact Impact,
    LuaContinuationEvidence Continuation,
    int Line,
    string SourceHash,
    LuaSourceCopy[] Copies,
    string? CallbackSha256 = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string OperationId { get; init; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public int OperationOccurrence { get; init; }
}

public static class LuaCallbackEvidenceAnalyzer
{
    private static readonly Regex Lifecycle = new("\\bregisterForEvent\\s*\\(\\s*(?<event>[^,\\r\\n]+)\\s*,", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Literal = new("^[\"'](?<value>[^\"']+)[\"']$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LuaCallbackEvidence[] Analyze(IReadOnlyList<LuaSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        List<LuaCallbackEvidence> evidence = [];
        foreach (IGrouping<string, LuaSource> group in sources.GroupBy(SourceHash, StringComparer.Ordinal))
        {
            LuaSource source = group.First();
            string code = SourceTextMask.Lua(source.Text);
            string structure = SourceTextMask.Lua(source.Text, true);
            LuaSourceCopy[] copies = group.Select(value => new LuaSourceCopy(value.Provider, value.FilePath)).ToArray();
            foreach (Match match in Lifecycle.Matches(code))
            {
                if (structure[match.Index] != code[match.Index]) continue;
                (string target, bool literal) = Target(match.Groups["event"].Value);
                evidence.Add(new LuaCallbackEvidence(LuaCallbackEvidenceKind.Lifecycle, target, literal ? EvidenceConfidence.Literal : EvidenceConfidence.Dynamic, EvidenceImpact.None, LuaContinuationEvidence.NotApplicable, RedScriptFlowEvidenceAnalyzer.LineAt(source.Text, match.Index), group.Key, copies));
            }

            foreach (LuaHookRegistration registration in LuaHookRegistrationAnalyzer.Analyze(source.Text))
            {
                string? callbackSha256 = registration.CallbackEvidence is null ? null : SemanticEvidence.Sha256(registration.CallbackEvidence);
                evidence.Add(new LuaCallbackEvidence(registration.Kind, registration.Target, registration.Confidence, registration.Kind == LuaCallbackEvidenceKind.Override ? EvidenceImpact.Review : EvidenceImpact.None, registration.Continuation, registration.Line, group.Key, copies, callbackSha256));
            }
        }

        return CodeOperationIdentity.NumberCallbacks(evidence.OrderBy(value => value.Target, StringComparer.Ordinal)
            .ThenBy(value => value.Kind)
            .ToArray()).ToArray();
    }

    private static (string Target, bool Literal) Target(string expression)
    {
        string trimmed = expression.Trim();
        Match literal = Literal.Match(trimmed);
        return literal.Success ? (literal.Groups["value"].Value, true) : (Regex.Replace(trimmed, "\\s+", string.Empty), false);
    }

    private static string SourceHash(LuaSource source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text)));
}

internal static class SemanticEvidence
{
    public static string Sha256(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(value))));

    public static string Normalize(string value)
    {
        StringBuilder result = new();
        char quote = '\0';
        bool whitespace = false;
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (quote != '\0')
            {
                result.Append(current);
                if (current == '\\' && index + 1 < value.Length) result.Append(value[++index]);
                else if (current == quote) quote = '\0';
                continue;
            }
            if (SourceTextMask.LuaLongStringEnd(value, index) is int longEnd)
            {
                if (whitespace && result.Length > 0) result.Append(' ');
                whitespace = false;
                result.Append(value.AsSpan(index, longEnd - index));
                index = longEnd - 1;
                continue;
            }
            if (current is '\'' or '"')
            {
                if (whitespace && result.Length > 0) result.Append(' ');
                whitespace = false;
                quote = current;
                result.Append(current);
            }
            else if (char.IsWhiteSpace(current)) whitespace = true;
            else
            {
                if (whitespace && result.Length > 0) result.Append(' ');
                whitespace = false;
                result.Append(current);
            }
        }
        return result.ToString();
    }
}

internal static class SourceTextMask
{
    internal static int? LuaLongStringEnd(string text, int start)
    {
        if (start >= text.Length || text[start] != '[') return null;
        int openingEnd = start + 1;
        while (openingEnd < text.Length && text[openingEnd] == '=') openingEnd++;
        if (openingEnd >= text.Length || text[openingEnd] != '[') return null;
        string closing = "]" + text[(start + 1)..openingEnd] + "]";
        int closingStart = text.IndexOf(closing, openingEnd + 1, StringComparison.Ordinal);
        return closingStart < 0 ? text.Length : closingStart + closing.Length;
    }

    public static string RedScript(string text, bool maskStrings) => Mask(text, false, maskStrings);

    public static string Lua(string text) => Mask(text, true, false);

    public static string Lua(string text, bool maskStrings) => Mask(text, true, maskStrings);

    private static string Mask(string text, bool lua, bool maskStrings)
    {
        char[] result = text.ToCharArray();
        char quote = '\0';
        bool lineComment = false;
        bool blockComment = false;
        for (int index = 0; index < result.Length; index++)
        {
            char current = text[index];
            char next = index + 1 < text.Length ? text[index + 1] : '\0';
            if (lineComment)
            {
                if (current == '\n') lineComment = false;
                else result[index] = ' ';
                continue;
            }

            if (blockComment)
            {
                bool closes = lua ? current == ']' && next == ']' : current == '*' && next == '/';
                if (closes)
                {
                    result[index] = ' ';
                    result[index + 1] = ' ';
                    index++;
                    blockComment = false;
                }
                else if (current != '\n') result[index] = ' ';
                continue;
            }

            if (quote != '\0')
            {
                if (maskStrings && current != '\n') result[index] = ' ';
                if (current == '\\')
                {
                    if (maskStrings && index + 1 < result.Length && text[index + 1] != '\n') result[index + 1] = ' ';
                    index++;
                }
                else if (current == quote) quote = '\0';
                continue;
            }

            if (current is '\'' or '"')
            {
                quote = current;
                if (maskStrings) result[index] = ' ';
                continue;
            }

            bool longComment = lua && current == '-' && next == '-';
            if (lua && LuaLongStringEnd(text, longComment ? index + 2 : index) is int longEnd)
            {
                if (longComment || maskStrings)
                    for (int position = index; position < longEnd; position++)
                        if (text[position] is not '\r' and not '\n') result[position] = ' ';
                index = longEnd - 1;
                continue;
            }

            bool startsLine = lua ? current == '-' && next == '-' : current == '/' && next == '/';
            bool startsBlock = lua
                ? startsLine && index + 3 < text.Length && text[index + 2] == '[' && text[index + 3] == '['
                : current == '/' && next == '*';
            if (startsBlock)
            {
                int width = lua ? 4 : 2;
                for (int offset = 0; offset < width; offset++) result[index + offset] = ' ';
                index += width - 1;
                blockComment = true;
            }
            else if (startsLine)
            {
                result[index] = ' ';
                result[index + 1] = ' ';
                index++;
                lineComment = true;
            }
        }

        return new string(result);
    }
}
