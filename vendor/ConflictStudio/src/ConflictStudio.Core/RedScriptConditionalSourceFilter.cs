using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

internal static class RedScriptConditionalSourceFilter
{
    private static readonly Regex Module = new("^\\s*module\\s+(?<name>[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex Term = new("^\\s*(?<not>!)?\\s*ModuleExists\\s*\\(\\s*[\"'](?<name>[^\"']+)[\"']\\s*\\)\\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Condition = new("@if(?:Not)?\\s*\\(", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Import = new("import\\b[^;\\r\\n]*;?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DeclarationStart = new("^[ \\t]*(?:@|(?:[A-Za-z_][A-Za-z0-9_]*[ \\t]+)*(?:func|class|struct|enum|let|import)\\b)", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    public static RedScriptSource[] Filter(IReadOnlyList<RedScriptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        HashSet<string> modules = sources.SelectMany(source => Module.Matches(SourceTextMask.RedScript(source.Text, false)).Select(match => match.Groups["name"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sources.Select(source => source with { Text = FilterBodies(source.Text, modules) }).ToArray();
    }

    private static string FilterBodies(string text, HashSet<string> modules)
    {
        string semantic = SourceTextMask.RedScript(text, false);
        string syntax = SourceTextMask.RedScript(text, true);
        char[] result = text.ToCharArray();
        foreach ((int start, int conditionEnd, bool? active) in Conditions(semantic, syntax, modules))
        {
            int end = active == true ? conditionEnd : DeclarationEnd(syntax, conditionEnd);
            for (int index = start; index < end; index++)
                if (result[index] is not '\r' and not '\n') result[index] = ' ';
        }
        return new string(result);
    }

    private static int DeclarationEnd(string syntax, int start)
    {
        int index = start;
        while (index < syntax.Length)
        {
            if (char.IsWhiteSpace(syntax[index])) { index++; continue; }
            if (syntax[index] != '@') break;
            while (index < syntax.Length && syntax[index] != '(') index++;
            int parentheses = 0;
            while (index < syntax.Length)
            {
                char token = syntax[index++];
                if (token == '(') parentheses++;
                else if (token == ')' && --parentheses == 0) break;
            }
        }
        Match import = Import.Match(syntax, index);
        if (import.Success && import.Index == index) return index + import.Length;
        Match declaration = Regex.Match(syntax[index..], "\\b(?:func|class|struct|enum|let)\\b", RegexOptions.CultureInvariant);
        int nextLine = syntax.IndexOf('\n', index + (declaration.Success ? declaration.Index + declaration.Length : 0));
        Match nextDeclaration = nextLine < 0 ? Match.Empty : DeclarationStart.Match(syntax, nextLine + 1);
        int declarationLimit = nextDeclaration.Success ? nextDeclaration.Index : syntax.Length;
        while (index < declarationLimit && syntax[index] is not '{' and not ';') index++;
        if (index == declarationLimit) return index;
        if (syntax[index] == ';') return index + 1;
        int braces = 0;
        while (index < syntax.Length)
        {
            char token = syntax[index++];
            if (token == '{') braces++;
            else if (token == '}' && --braces == 0) return index;
        }
        return index;
    }

    public static SourceAnalysisFailure[] Failures(IReadOnlyList<RedScriptSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        HashSet<string> modules = sources.SelectMany(source => Module.Matches(SourceTextMask.RedScript(source.Text, false)).Select(match => match.Groups["name"].Value)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return sources.Where(source => Conditions(SourceTextMask.RedScript(source.Text, false), SourceTextMask.RedScript(source.Text, true), modules).Any(condition => condition.Active is null))
            .Select(source => new SourceAnalysisFailure(source.Provider, source.FilePath, "RedScript condition", "At least one RedScript @if condition could not be evaluated, so guarded declarations in this file were excluded from conflict claims."))
            .ToArray();
    }

    private static IEnumerable<(int Start, int End, bool? Active)> Conditions(string semantic, string syntax, HashSet<string> modules)
    {
        foreach (Match condition in Condition.Matches(semantic))
        {
            if (syntax[condition.Index] == '@' && TryCondition(semantic[condition.Index..], modules, out bool? active, out int conditionEnd))
                yield return (condition.Index, condition.Index + conditionEnd, active);
        }
    }

    private static bool TryCondition(string line, HashSet<string> modules, out bool? active, out int end)
    {
        active = null;
        end = 0;
        int marker = line.IndexOf("@if", StringComparison.Ordinal);
        if (marker < 0 || !string.IsNullOrWhiteSpace(line[..marker])) return false;
        bool negateAll = line.AsSpan(marker).StartsWith("@ifNot", StringComparison.Ordinal);
        int opening = line.IndexOf('(', marker);
        if (opening < 0) return false;
        int depth = 0;
        char quote = '\0';
        int closing = -1;
        for (int index = opening; index < line.Length; index++)
        {
            char value = line[index];
            if (quote != '\0')
            {
                if (value == quote && line[index - 1] != '\\') quote = '\0';
                continue;
            }
            if (value is '\'' or '"') quote = value;
            else if (value == '(') depth++;
            else if (value == ')' && --depth == 0)
            {
                closing = index;
                break;
            }
        }
        if (closing < 0) return false;
        string expression = line[(opening + 1)..closing];
        string separator = expression.Contains("||", StringComparison.Ordinal) ? "||" : "&&";
        string[] terms = expression.Split(separator, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool?[] evaluated = terms.Select(term => EvaluateTerm(term, modules)).ToArray();
        if (evaluated.All(value => value.HasValue))
        {
            bool[] values = evaluated.Select(value => value!.Value).ToArray();
            active = separator == "||" ? values.Any(value => value) : values.All(value => value);
        }
        if (negateAll && active.HasValue) active = !active.Value;
        end = closing + 1;
        return true;
    }

    private static bool? EvaluateTerm(string expression, HashSet<string> modules)
    {
        Match match = Term.Match(expression);
        if (!match.Success) return null;
        bool exists = modules.Contains(match.Groups["name"].Value);
        return match.Groups["not"].Success ? !exists : exists;
    }
}
