using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

internal sealed record LuaHookRegistration(
    LuaCallbackEvidenceKind Kind,
    string Target,
    EvidenceConfidence Confidence,
    LuaContinuationEvidence Continuation,
    int Line,
    bool DefinitionSite = false,
    bool ResolvedHelper = false,
    string? CallbackEvidence = null,
    int InvocationIndex = 0,
    int RegistrationIndex = 0,
    int EvidenceEndIndex = -1,
    bool InlineCallback = false,
    int CallbackStartIndex = -1,
    int CallbackEndIndex = -1);

internal static class LuaHookRegistrationAnalyzer
{
    private sealed record FunctionDefinition(string Name, string[] Parameters, int DeclarationStart, int BodyStart, int BodyEnd, int DeclarationEnd);
    private sealed record Call(string Name, string[] Arguments, int Index, int EndIndex);
    private sealed record Expression(string? Literal, int ParameterIndex = -1);
    private sealed record CallbackExpression(string? Inline, int ParameterIndex = -1);
    private sealed record Template(LuaCallbackEvidenceKind Kind, Expression Class, Expression Method, CallbackExpression Callback, int RegistrationIndex);

    private static readonly string[] HookNames = ["Observe", "ObserveBefore", "ObserveAfter", "Override"];
    private static readonly Regex Function = new("\\b(?:local\\s+)?function\\s+(?<name>[A-Za-z_][A-Za-z0-9_.:]*)\\s*\\((?<parameters>[^)]*)\\)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Keyword = new("\\b(function|if|do|repeat|end|until)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Literal = new("^[\"'](?<value>[^\"']+)[\"']$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LuaHookRegistration[] Analyze(string text)
    {
        string code = SourceTextMask.Lua(text);
        string structure = SourceTextMask.Lua(text, true);
        FunctionDefinition[] definitions = Definitions(code, structure);
        Dictionary<string, List<Template>> templates = BuildTemplates(code, definitions);
        List<LuaHookRegistration> registrations = [];
        HashSet<string> invokedDefinitions = new(StringComparer.Ordinal);

        foreach (Call call in Calls(code, HookNames))
        {
            if (ContainingDefinition(definitions, call.Index) is not null) continue;
            if (call.Arguments.Length < 2) continue;
            (string className, bool classLiteral) = Target(call.Arguments[0]);
            (string methodName, bool methodLiteral) = Target(call.Arguments[1]);
            LuaCallbackEvidenceKind kind = Enum.Parse<LuaCallbackEvidenceKind>(call.Name);
            string callback = call.Arguments.Length > 2 ? call.Arguments[2] : string.Empty;
            string? callbackEvidence = ResolveCallbackEvidence(callback, code, definitions);
            FunctionDefinition? callbackDefinition = CallbackDefinition(callback, definitions);
            registrations.Add(new LuaHookRegistration(kind, className + "." + methodName, classLiteral && methodLiteral ? EvidenceConfidence.Literal : EvidenceConfidence.Dynamic, Continuation(callbackEvidence ?? callback, kind), RedScriptFlowEvidenceAnalyzer.LineAt(text, call.Index), definitions.Any(value => call.Index >= value.BodyStart && call.Index < value.BodyEnd), false, callbackEvidence, call.Index, call.Index, call.EndIndex, callback.TrimStart().StartsWith("function", StringComparison.Ordinal), callbackDefinition?.DeclarationStart ?? -1, callbackDefinition?.DeclarationEnd ?? -1));
        }

        foreach ((string name, List<Template> values) in templates)
        {
            foreach (Call call in Calls(code, [name]))
            {
                if (IsDeclarationCall(code, call.Index)) continue;
                FunctionDefinition? containing = ContainingDefinition(definitions, call.Index);
                if (containing is not null) continue;
                foreach (Template template in values)
                {
                    string? className = Resolve(template.Class, call.Arguments);
                    string? methodName = Resolve(template.Method, call.Arguments);
                    if (className is null || methodName is null) continue;
                    string callback = Resolve(template.Callback, call.Arguments) ?? string.Empty;
                    string? callbackEvidence = ResolveCallbackEvidence(callback, code, definitions);
                    FunctionDefinition? callbackDefinition = CallbackDefinition(callback, definitions);
                    registrations.Add(new LuaHookRegistration(template.Kind, className + "." + methodName, EvidenceConfidence.Literal, Continuation(callbackEvidence ?? callback, template.Kind), RedScriptFlowEvidenceAnalyzer.LineAt(text, call.Index), false, true, callbackEvidence, call.Index, template.RegistrationIndex, call.EndIndex, false, callbackDefinition?.DeclarationStart ?? -1, callbackDefinition?.DeclarationEnd ?? -1));
                    invokedDefinitions.Add(name);
                }
            }
        }

        foreach (FunctionDefinition definition in RootDefinitions(code, definitions, templates.Keys.ToArray()))
        {
            if (invokedDefinitions.Contains(definition.Name)) continue;
            if (!templates.TryGetValue(definition.Name, out List<Template>? values)) continue;
            foreach (Template template in values)
            {
                if (template.Class.Literal is null || template.Method.Literal is null) continue;
                string callback = template.Callback.Inline ?? string.Empty;
                string? callbackEvidence = ResolveCallbackEvidence(callback, code, definitions);
                registrations.Add(new LuaHookRegistration(template.Kind, template.Class.Literal + "." + template.Method.Literal, EvidenceConfidence.Literal, Continuation(callbackEvidence ?? callback, template.Kind), RedScriptFlowEvidenceAnalyzer.LineAt(text, template.RegistrationIndex), false, true, callbackEvidence, template.RegistrationIndex, template.RegistrationIndex));
            }
        }

        HashSet<(LuaCallbackEvidenceKind Kind, string Target)> resolvedTargets = registrations.Where(value => value.ResolvedHelper).Select(value => (value.Kind, value.Target)).ToHashSet();
        return registrations
            .Where(value => !value.DefinitionSite || !resolvedTargets.Contains((value.Kind, value.Target)))
            .GroupBy(value => (value.Kind, value.Target, value.InvocationIndex, value.RegistrationIndex))
            .Select(group => group.OrderBy(value => value.Confidence).First())
            .OrderBy(value => value.Line)
            .ToArray();
    }

    private static Dictionary<string, List<Template>> BuildTemplates(string code, IReadOnlyList<FunctionDefinition> definitions)
    {
        Dictionary<string, List<Template>> templates = new(StringComparer.Ordinal);
        for (int pass = 0; pass <= definitions.Count; pass++)
        {
            bool changed = false;
            foreach (FunctionDefinition definition in definitions)
            {
                string body = code[definition.BodyStart..definition.BodyEnd];
                IEnumerable<Call> calls = Calls(body, [.. HookNames, .. templates.Keys]).Select(value => value with { Index = value.Index + definition.BodyStart });
                foreach (Call call in calls)
                {
                    if (HookNames.Contains(call.Name, StringComparer.Ordinal))
                    {
                        if (call.Arguments.Length < 2) continue;
                        Template template = new(
                            Enum.Parse<LuaCallbackEvidenceKind>(call.Name),
                            TemplateValue(call.Arguments[0], definition.Parameters),
                            TemplateValue(call.Arguments[1], definition.Parameters),
                            TemplateCallback(call.Arguments.Length > 2 ? call.Arguments[2] : string.Empty, definition.Parameters),
                            call.Index);
                        changed |= AddTemplate(templates, definition.Name, template);
                        continue;
                    }

                    foreach (Template nested in templates[call.Name])
                    {
                        Expression mappedClass = Map(nested.Class, call.Arguments, definition.Parameters);
                        Expression mappedMethod = Map(nested.Method, call.Arguments, definition.Parameters);
                        int registrationIndex = (nested.Class.Literal is null || nested.Method.Literal is null) && mappedClass.Literal is not null && mappedMethod.Literal is not null ? call.Index : nested.RegistrationIndex;
                        Template template = new(
                            nested.Kind,
                            mappedClass,
                            mappedMethod,
                            Map(nested.Callback, call.Arguments, definition.Parameters),
                            registrationIndex);
                        changed |= AddTemplate(templates, definition.Name, template);
                    }
                }
            }
            if (!changed) break;
        }
        return templates;
    }

    private static bool AddTemplate(Dictionary<string, List<Template>> templates, string name, Template template)
    {
        if (!templates.TryGetValue(name, out List<Template>? values)) templates[name] = values = [];
        if (values.Contains(template)) return false;
        values.Add(template);
        return true;
    }

    private static Expression Map(Expression expression, string[] arguments, string[] callerParameters)
    {
        if (expression.Literal is not null) return expression;
        if (expression.ParameterIndex < 0 || expression.ParameterIndex >= arguments.Length) return new Expression(null);
        return TemplateValue(arguments[expression.ParameterIndex], callerParameters);
    }

    private static CallbackExpression Map(CallbackExpression expression, string[] arguments, string[] callerParameters)
    {
        if (expression.Inline is not null) return expression;
        if (expression.ParameterIndex < 0 || expression.ParameterIndex >= arguments.Length) return new CallbackExpression(null);
        return TemplateCallback(arguments[expression.ParameterIndex], callerParameters);
    }

    private static Expression TemplateValue(string expression, IReadOnlyList<string> parameters)
    {
        Match literal = Literal.Match(expression.Trim());
        if (literal.Success) return new Expression(literal.Groups["value"].Value);
        int parameter = Array.FindIndex(parameters.ToArray(), value => value == expression.Trim());
        return new Expression(null, parameter);
    }

    private static CallbackExpression TemplateCallback(string expression, IReadOnlyList<string> parameters)
    {
        string trimmed = expression.Trim();
        if (trimmed.StartsWith("function", StringComparison.Ordinal)) return new CallbackExpression(trimmed);
        int parameter = Array.FindIndex(parameters.ToArray(), value => value == trimmed);
        return new CallbackExpression(null, parameter);
    }

    private static string? Resolve(Expression expression, string[] arguments)
    {
        if (expression.Literal is not null) return expression.Literal;
        if (expression.ParameterIndex < 0 || expression.ParameterIndex >= arguments.Length) return null;
        Match literal = Literal.Match(arguments[expression.ParameterIndex].Trim());
        return literal.Success ? literal.Groups["value"].Value : null;
    }

    private static string? Resolve(CallbackExpression expression, string[] arguments)
    {
        if (expression.Inline is not null) return expression.Inline;
        return expression.ParameterIndex >= 0 && expression.ParameterIndex < arguments.Length ? arguments[expression.ParameterIndex] : null;
    }

    private static string? ResolveCallbackEvidence(string callback, string code, IReadOnlyList<FunctionDefinition> definitions)
    {
        string trimmed = callback.Trim();
        if (trimmed.StartsWith("function", StringComparison.Ordinal)) return SemanticEvidence.Normalize(trimmed);
        FunctionDefinition? definition = definitions.FirstOrDefault(value => value.Name == trimmed);
        return definition is null ? null : SemanticEvidence.Normalize("function(" + string.Join(",", definition.Parameters) + ")" + code[definition.BodyStart..definition.BodyEnd] + " end");
    }

    private static FunctionDefinition? CallbackDefinition(string callback, IReadOnlyList<FunctionDefinition> definitions)
    {
        string name = callback.Trim();
        if (name.StartsWith("function", StringComparison.Ordinal)) return null;
        FunctionDefinition[] matches = definitions.Where(value => value.Name == name).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static FunctionDefinition[] Definitions(string code, string structure)
    {
        List<FunctionDefinition> definitions = [];
        foreach (Match match in Function.Matches(code))
        {
            if (structure[match.Index] != code[match.Index]) continue;
            int end = FunctionEnd(structure, match.Index + match.Length);
            if (end < 0) continue;
            string[] parameters = match.Groups["parameters"].Value.Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
            definitions.Add(new FunctionDefinition(match.Groups["name"].Value, parameters, match.Index, match.Index + match.Length, end, end + "end".Length - 1));
        }
        return definitions.ToArray();
    }

    private static int FunctionEnd(string structure, int start)
    {
        int depth = 1;
        foreach (Match token in Keyword.Matches(structure, start))
        {
            if (token.Value is "function" or "if" or "do" or "repeat") depth++;
            else depth--;
            if (depth == 0) return token.Index;
        }
        return -1;
    }

    private static IEnumerable<Call> Calls(string code, string[] names)
    {
        if (names.Length == 0) yield break;
        Regex callPattern = new("\\b(?<name>" + string.Join("|", names.Select(Regex.Escape).OrderByDescending(value => value.Length)) + ")\\s*\\(", RegexOptions.CultureInvariant);
        string structure = SourceTextMask.Lua(code, true);
        foreach (Match match in callPattern.Matches(code))
        {
            if (structure[match.Index] != code[match.Index]) continue;
            int open = code.IndexOf('(', match.Index + match.Groups["name"].Length);
            string[]? arguments = Arguments(code, open, out int endIndex);
            if (arguments is not null) yield return new Call(match.Groups["name"].Value, arguments, match.Index, endIndex);
        }
    }

    private static string[]? Arguments(string code, int open, out int endIndex)
    {
        endIndex = -1;
        if (open < 0) return null;
        List<string> arguments = [];
        int start = open + 1;
        int round = 1;
        int square = 0;
        int curly = 0;
        char quote = '\0';
        for (int index = open + 1; index < code.Length; index++)
        {
            char value = code[index];
            if (quote != '\0')
            {
                if (value == '\\') index++;
                else if (value == quote) quote = '\0';
                continue;
            }
            if (SourceTextMask.LuaLongStringEnd(code, index) is int longEnd)
            {
                index = longEnd - 1;
                continue;
            }
            if (value is '\'' or '"') quote = value;
            else if (value == '(') round++;
            else if (value == ')' && --round == 0)
            {
                string final = code[start..index].Trim();
                if (final.Length > 0 || arguments.Count > 0) arguments.Add(final);
                endIndex = index;
                return arguments.ToArray();
            }
            else if (value == '[') square++;
            else if (value == ']') square--;
            else if (value == '{') curly++;
            else if (value == '}') curly--;
            else if (value == ',' && round == 1 && square == 0 && curly == 0)
            {
                arguments.Add(code[start..index].Trim());
                start = index + 1;
            }
        }
        return null;
    }

    private static bool IsDeclarationCall(string code, int index)
    {
        int start = Math.Max(0, index - 32);
        return Regex.IsMatch(code[start..index], "function\\s+$", RegexOptions.CultureInvariant);
    }

    private static FunctionDefinition? ContainingDefinition(IEnumerable<FunctionDefinition> definitions, int index)
        => definitions.Where(value => index >= value.BodyStart && index < value.BodyEnd).OrderBy(value => value.BodyEnd - value.BodyStart).FirstOrDefault();

    private static IEnumerable<FunctionDefinition> RootDefinitions(string code, IReadOnlyList<FunctionDefinition> definitions, string[] names)
    {
        HashSet<string> calledInsideDefinitions = [];
        foreach (FunctionDefinition definition in definitions)
        {
            string body = code[definition.BodyStart..definition.BodyEnd];
            foreach (string name in Calls(body, names).Select(value => value.Name)) calledInsideDefinitions.Add(name);
        }
        return definitions.Where(value => (value.Name.Contains('.') || value.Name.Contains(':')) && !calledInsideDefinitions.Contains(value.Name));
    }

    private static (string Target, bool Literal) Target(string expression)
    {
        string trimmed = expression.Trim();
        Match literal = Literal.Match(trimmed);
        return literal.Success ? (literal.Groups["value"].Value, true) : (Regex.Replace(trimmed, "\\s+", string.Empty), false);
    }

    private static LuaContinuationEvidence Continuation(string callback, LuaCallbackEvidenceKind kind)
    {
        if (kind != LuaCallbackEvidenceKind.Override) return LuaContinuationEvidence.NotApplicable;
        callback = SourceTextMask.Lua(callback, true);
        Match declaration = Regex.Match(callback, "^\\s*function\\s*\\((?<parameters>[^)]*)\\)", RegexOptions.CultureInvariant);
        if (!declaration.Success) return LuaContinuationEvidence.Unknown;
        string[] parameters = declaration.Groups["parameters"].Value.Split(',').Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();
        if (parameters.Length == 0) return LuaContinuationEvidence.Missing;
        string wrapped = parameters[^1];
        Regex continuation = new("\\b" + Regex.Escape(wrapped) + "\\s*\\(", RegexOptions.CultureInvariant);
        if (!continuation.IsMatch(callback)) return LuaContinuationEvidence.Missing;
        foreach (Match returnToken in Regex.Matches(callback, "\\breturn\\b", RegexOptions.CultureInvariant))
        {
            int end = callback.IndexOfAny(['\r', '\n', ';'], returnToken.Index);
            string afterReturn = callback[(returnToken.Index + returnToken.Length)..];
            Match blockEnd = Regex.Match(afterReturn, "\\bend\\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            int blockEndIndex = blockEnd.Success ? returnToken.Index + returnToken.Length + blockEnd.Index : -1;
            if (blockEndIndex >= 0 && (end < 0 || blockEndIndex < end)) end = blockEndIndex;
            string statement = end < 0 ? callback[returnToken.Index..] : callback[returnToken.Index..end];
            if (continuation.IsMatch(statement)) continue;
            string beforeReturn = callback[..returnToken.Index];
            Match immediatelyBefore = Regex.Match(beforeReturn, "\\b" + Regex.Escape(wrapped) + "\\s*\\([^;\\r\\n]*\\)\\s*;?\\s*$", RegexOptions.CultureInvariant);
            if (!immediatelyBefore.Success) return LuaContinuationEvidence.Conditional;
        }
        return LuaContinuationEvidence.Continues;
    }
}
