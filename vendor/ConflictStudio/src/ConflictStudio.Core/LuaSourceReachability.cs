using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

internal static class LuaSourceReachability
{
    private static readonly Regex Loader = new("\\b(?:require|dofile|loadfile|loadstring|load|_G|_ENV)\\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Literal = new("^\\s*(?<paren>\\()?\\s*(?<quote>[\"'])(?<path>[^\"'\\\\\\r\\n]+)\\k<quote>\\s*(?(paren)\\))", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static LuaSource[] Select(LuaSource[] sources, IReadOnlySet<string> effectivePaths, List<SourceAnalysisFailure> failures, CancellationToken cancellationToken)
    {
        List<LuaSource> selected = [];
        foreach (IGrouping<string, LuaSource> group in sources.GroupBy(value => ModSourceScanner.CetLuaRoot(value.FilePath)!, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, LuaSource> files = group.ToDictionary(value => value.FilePath, StringComparer.OrdinalIgnoreCase);
            string entry = group.Key + "\\init.lua";
            HashSet<string> reached = new(StringComparer.OrdinalIgnoreCase);
            Queue<string> pending = new();
            pending.Enqueue(entry);
            bool unresolved = !files.ContainsKey(entry);
            while (pending.Count > 0 && !unresolved)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = pending.Dequeue();
                if (!reached.Add(path)) continue;
                LuaSource source = files[path];
                string code = SourceTextMask.Lua(source.Text);
                string structure = SourceTextMask.Lua(source.Text, true);
                foreach (Match loader in Loader.Matches(structure))
                {
                    int previous = loader.Index - 1;
                    while (previous >= 0 && char.IsWhiteSpace(code[previous])) previous--;
                    if (previous >= 0 && (code[previous] == ':' || code[previous] == '.' && (previous == 0 || code[previous - 1] != '.'))) continue;
                    Match literal = Literal.Match(code[(loader.Index + loader.Length)..]);
                    if (loader.Value is "load" or "loadstring" or "_G" or "_ENV" || !literal.Success)
                    {
                        unresolved = true;
                        break;
                    }
                    string module = literal.Groups["path"].Value;
                    string? relative = RelativeModule(module);
                    if (relative is null)
                    {
                        unresolved = true;
                        break;
                    }
                    string target = group.Key + "\\" + relative;
                    string[] candidates = loader.Value == "require" ? [target, target + ".lua", target + "\\init.lua"] : [target, target + ".lua"];
                    string? found = candidates.FirstOrDefault(effectivePaths.Contains);
                    if (found is not null && files.ContainsKey(found)) pending.Enqueue(found);
                    else failures.Add(new SourceAnalysisFailure(source.Provider, source.FilePath, "CET Lua import", $"Line {RedScriptFlowEvidenceAnalyzer.LineAt(source.Text, loader.Index)}: {loader.Value}('{module}') has no readable effective Lua source target. This may be optional; no runtime failure is inferred."));
                }
            }
            if (unresolved)
            {
                selected.AddRange(group);
                LuaSource origin = files.GetValueOrDefault(entry) ?? group.First();
                failures.Add(new SourceAnalysisFailure(origin.Provider, group.Key, "CET Lua reachability", "The entry source or a loading expression could not be resolved statically. This mod's Lua files remain candidates; their execution is not established."));
            }
            else
            {
                selected.AddRange(group.Where(value => reached.Contains(value.FilePath)));
                int omitted = files.Count - reached.Count;
                if (omitted > 0) failures.Add(new SourceAnalysisFailure(files[entry].Provider, group.Key, "CET Lua reachability", $"{omitted} Lua files without an identified literal import path from init.lua were excluded. This is source reachability, not proof of which functions execute."));
            }
        }
        return selected.ToArray();
    }

    private static string? RelativeModule(string module)
    {
        if (module.StartsWith('/') || module.StartsWith('\\') || module.Contains(':')) return null;
        List<string> parts = [];
        foreach (string part in module.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        return parts.Count == 0 ? null : string.Join('\\', parts);
    }
}
