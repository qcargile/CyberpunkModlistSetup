using System.Text.RegularExpressions;

namespace ConflictStudio.Core;

public sealed record Mo2InstancePaths(string Root, string ModsRoot, string ProfilesRoot, string OverwriteRoot, string? GameRoot, string? SelectedProfile = null, bool EnforcesArchiveOrder = false);

public static class Mo2InstancePathResolver
{
    public static Mo2InstancePaths Resolve(string mo2Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        string root = Path.GetFullPath(mo2Root);
        string iniPath = Path.Combine(root, "ModOrganizer.ini");
        Dictionary<string, string> values = File.Exists(iniPath) ? Values(File.ReadAllText(iniPath)) : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string baseRoot = ResolveValue(root, root, values.GetValueOrDefault("base_directory"));
        string mods = ResolveValue(root, baseRoot, values.GetValueOrDefault("mod_directory") ?? values.GetValueOrDefault("mods_directory") ?? "mods");
        string profiles = ResolveValue(root, baseRoot, values.GetValueOrDefault("profiles_directory") ?? "profiles");
        string overwrite = ResolveValue(root, baseRoot, values.GetValueOrDefault("overwrite_directory") ?? "overwrite");
        string? game = values.TryGetValue("gamePath", out string? gameValue) ? ResolveValue(root, baseRoot, gameValue) : null;
        bool enforcesArchiveOrder = bool.TryParse(values.GetValueOrDefault("Cyberpunk%202077%20Support%20Plugin\\enforce_archive_load_order"), out bool enforced) && enforced;
        return new Mo2InstancePaths(root, mods, profiles, overwrite, game, values.GetValueOrDefault("selected_profile"), enforcesArchiveOrder);
    }

    private static Dictionary<string, string> Values(string text)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text, "(?m)^(?<key>selected_profile|base_directory|mod_directory|mods_directory|profiles_directory|overwrite_directory|gamePath|Cyberpunk%202077%20Support%20Plugin\\\\enforce_archive_load_order)=(?<value>.+)$")) values[match.Groups["key"].Value] = Unwrap(match.Groups["value"].Value.Trim());
        return values;
    }

    private static string Unwrap(string value)
    {
        string result = value.StartsWith("@ByteArray(", StringComparison.Ordinal) && value.EndsWith(')') ? value[11..^1] : value;
        return result.Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    private static string ResolveValue(string instanceRoot, string baseRoot, string? value)
    {
        string expanded = (value ?? instanceRoot).Replace("%BASE_DIR%", baseRoot, StringComparison.OrdinalIgnoreCase);
        return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseRoot, expanded));
    }
}
