namespace ConflictStudio.Core;

public static class Mo2ProfileArchiveOrder
{
    public static bool MappingEnabled(string instanceRoot) => MappingBlockedReason(instanceRoot) is null;

    internal static string? MappingBlockedReason(string instanceRoot)
    {
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(instanceRoot);
        if (!File.Exists(Path.Combine(paths.Root, "plugins", "chrome_blood_archives", "__init__.py"))) return "Install Chrome & Blood Archives in this MO2 instance before saving a profile archive order.";
        if (paths.EnforcesArchiveOrder) return "Disable 'enforce archive load order' in MO2's Cyberpunk plugin before using a profile archive order.";
        Dictionary<string, string> settings = ReadPersistence(Path.Combine(paths.Root, "ModOrganizer.ini"));
        if (!PluginEnabled(settings, "Chrome & Blood Archives")) return "Enable Chrome & Blood Archives in MO2 before saving a profile archive order.";
        if (!PluginEnabled(settings, "Python Proxy")) return "Enable Python Proxy in MO2 before saving a profile archive order.";
        return null;
    }

    public static string Resolve(string instanceRoot, string profileModlistPath)
    {
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(instanceRoot);
        string profile = Path.GetFullPath(profileModlistPath);
        string profilesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.ProfilesRoot));
        string? directory = Path.GetDirectoryName(profile);
        if (!string.Equals(Path.GetFileName(profile), "modlist.txt", StringComparison.OrdinalIgnoreCase)
            || directory is null
            || !string.Equals(Path.GetDirectoryName(directory), profilesRoot, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(profile)) throw new ArchiveOrderException("Choose an existing profile inside this MO2 instance's configured profiles directory.");
        string target = Path.Combine(directory, "archive-order.txt");
        RequireNoRedirect(target);
        return target;
    }

    internal static string? Find(string instanceRoot, string profileModlistPath)
    {
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(instanceRoot);
        string profile = Path.GetFullPath(profileModlistPath);
        string? directory = Path.GetDirectoryName(profile);
        return string.Equals(Path.GetFileName(profile), "modlist.txt", StringComparison.OrdinalIgnoreCase)
            && directory is not null
            && string.Equals(Path.GetDirectoryName(directory), Path.TrimEndingDirectorySeparator(paths.ProfilesRoot), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, "archive-order.txt") : null;
    }

    internal static string? ResolveOverride(string instanceRoot, string path)
    {
        string full = Path.GetFullPath(path);
        string? orderDirectory = Path.GetDirectoryName(full);
        string? profileDirectory = string.Equals(Path.GetFileName(full), "archive-order.txt", StringComparison.OrdinalIgnoreCase) ? orderDirectory
            : string.Equals(Path.GetFileName(orderDirectory), "archive-order", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetFileName(full), "modlist.txt", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(orderDirectory) : null;
        if (profileDirectory is null) return null;
        string? expected = Find(instanceRoot, Path.Combine(profileDirectory, "modlist.txt"));
        return expected is null ? null : Resolve(instanceRoot, Path.Combine(profileDirectory, "modlist.txt"));
    }

    internal static bool IsProfilePath(string profileModlistPath, string orderPath)
    {
        string profile = Path.GetFullPath(profileModlistPath);
        string directory = Path.GetDirectoryName(profile)!;
        string full = Path.GetFullPath(orderPath);
        return string.Equals(Path.GetFileName(profile), "modlist.txt", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(full, Path.Combine(directory, "archive-order.txt"), StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, Path.Combine(directory, "archive-order", "modlist.txt"), StringComparison.OrdinalIgnoreCase));
    }

    internal static void RequireNoRedirect(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArchiveOrderException("The profile archive-order path passes through a filesystem redirect. Use a regular profile directory before saving.");
    }

    private static Dictionary<string, string> ReadPersistence(string path)
    {
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;
        bool persistence = false;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                persistence = string.Equals(line[1..^1], "PluginPersistance", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int separator = line.IndexOf('=');
            if (!persistence || separator < 0) continue;
            string key = Uri.UnescapeDataString(line[..separator].Trim()).Replace('\\', '/');
            values[key] = line[(separator + 1)..].Trim().Trim('"');
        }
        return values;
    }

    private static bool PluginEnabled(Dictionary<string, string> settings, string name)
        => !settings.TryGetValue(name + "/enabled", out string? value) || value.Length > 0 && value != "0" && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
