namespace ConflictStudio.Core;

public sealed record Mo2Profile(string Name, string ModlistPath)
{
    public override string ToString() => Name;
}

public static class Mo2ProfileDiscovery
{
    public static Mo2Profile[] Discover(string mo2Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        string profilesRoot = Mo2InstancePathResolver.Resolve(mo2Root).ProfilesRoot;
        if (!Directory.Exists(profilesRoot)) throw new DirectoryNotFoundException("The MO2 profiles directory does not exist.");
        return Directory.EnumerateDirectories(profilesRoot)
            .Select(path => new Mo2Profile(Path.GetFileName(path), Path.Combine(path, "modlist.txt")))
            .Where(profile => File.Exists(profile.ModlistPath))
            .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
