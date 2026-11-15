namespace ConflictStudio.Core;

public enum PrecedenceStatus { Predicted, Unresolved }

public sealed record PrecedenceResult<T>(PrecedenceStatus Status, T? Winner, T[] Overridden, string Basis) where T : class;

internal static class SourcePrecedence
{
    private static readonly HashSet<string> PluginFolders = new(["ArchiveXL", "Codeware", "TweakXL"], StringComparer.OrdinalIgnoreCase);
    private const string Prediction = "Predicted from standard Windows ASCII directory traversal; filesystem order and runtime outcome were not observed.";
    private const string Unresolved = "Source ordering cannot be predicted from the available paths and declarations.";

    public static PrecedenceResult<RedScriptHook> Scripts(RedScriptHook[] hooks)
    {
        RedScriptHook[] replacements = hooks.Where(value => value.Kind == RedScriptHookKind.Replace).ToArray();
        return Order(replacements, value => value.FilePath, value => value.Provider, value => value.LineNumber, false);
    }

    public static PrecedenceResult<TweakOperation> Tweaks(TweakOperation[] operations)
    {
        if (operations.Any(value => value.Kind != TweakOperationKind.ScalarAssignment || value.IsGeneratedSource || value.IsAliasSource))
            return new(PrecedenceStatus.Unresolved, null, [], Unresolved);
        return Order(operations, value => value.FilePath, value => value.Provider, value => value.LineNumber, true);
    }

    private static PrecedenceResult<T> Order<T>(T[] values, Func<T, string> path, Func<T, string> provider, Func<T, int> line, bool tweak) where T : class
    {
        List<(T Value, string[] Parts, bool Plugin, int Priority)> parsed = [];
        foreach (T value in values)
        {
            if (!TryPath(path(value), tweak, out string[] parts, out bool plugin) || line(value) <= 0)
                return new(PrecedenceStatus.Unresolved, null, [], Unresolved);
            int priority = tweak ? parts[^1][0] switch { '_' or '#' or '$' or '!' => 0, '^' => 2, _ => 1 } : 1;
            parsed.Add((value, parts, plugin, priority));
        }
        if (parsed.Count < 2 || parsed.Count(value => value.Plugin) > 1)
            return new(PrecedenceStatus.Unresolved, null, [], Unresolved);
        for (int i = 0; i < parsed.Count; i++)
            for (int j = i + 1; j < parsed.Count; j++)
            {
                if (parsed[i].Parts.Length != parsed[j].Parts.Length && parsed[i].Parts.Zip(parsed[j].Parts).All(pair => string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase)))
                    return new(PrecedenceStatus.Unresolved, null, [], Unresolved);
                if (CompareParts(parsed[i].Parts, parsed[j].Parts) == 0
                    && (!string.Equals(provider(parsed[i].Value), provider(parsed[j].Value), StringComparison.OrdinalIgnoreCase) || line(parsed[i].Value) == line(parsed[j].Value)))
                    return new(PrecedenceStatus.Unresolved, null, [], Unresolved);
            }
        parsed.Sort((a, b) =>
        {
            int result = a.Plugin.CompareTo(b.Plugin);
            if (result == 0) result = a.Priority.CompareTo(b.Priority);
            if (result == 0) result = CompareParts(a.Parts, b.Parts);
            if (result == 0) result = line(a.Value).CompareTo(line(b.Value));
            return result;
        });
        return new(PrecedenceStatus.Predicted, parsed[^1].Value, parsed.Take(parsed.Count - 1).Select(value => value.Value).ToArray(),
            parsed[^1].Plugin ? Prediction + " Registered plugin scripts follow r6 scripts; plugin registration order is unavailable." : Prediction);
    }

    private static bool TryPath(string path, bool tweak, out string[] parts, out bool plugin)
    {
        parts = path.Replace('/', '\\').Split('\\');
        plugin = false;
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || c > 126 || "<>:\"|?*".Contains(c)) || IsReservedName(part))) return false;
        if (parts.Length >= 3 && parts[0].Equals("r6", StringComparison.OrdinalIgnoreCase)
            && parts[1].Equals(tweak ? "tweaks" : "scripts", StringComparison.OrdinalIgnoreCase))
        {
            string extension = Path.GetExtension(parts[^1]);
            return tweak ? extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase) || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase) : extension.Equals(".reds", StringComparison.OrdinalIgnoreCase);
        }
        if (tweak || parts.Length < 4 || !parts[0].Equals("red4ext", StringComparison.OrdinalIgnoreCase) || !parts[1].Equals("plugins", StringComparison.OrdinalIgnoreCase) || !parts[^1].EndsWith(".reds", StringComparison.OrdinalIgnoreCase)) return false;
        plugin = parts.Length >= 5 && PluginFolders.Contains(parts[2]) && parts[3].Equals("Scripts", StringComparison.OrdinalIgnoreCase)
            || parts.Length == 4 && parts[2].Equals("mod_settings", StringComparison.OrdinalIgnoreCase) && (parts[3].Equals("packed.reds", StringComparison.OrdinalIgnoreCase) || parts[3].Equals("module.reds", StringComparison.OrdinalIgnoreCase));
        return plugin;
    }

    private static bool IsReservedName(string part)
    {
        string name = part.Split('.')[0];
        return name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9';
    }

    private static int CompareParts(string[] first, string[] second)
    {
        for (int i = 0; i < Math.Min(first.Length, second.Length); i++)
        {
            int result = StringComparer.OrdinalIgnoreCase.Compare(first[i], second[i]);
            if (result != 0) return result;
        }
        return first.Length.CompareTo(second.Length);
    }
}
