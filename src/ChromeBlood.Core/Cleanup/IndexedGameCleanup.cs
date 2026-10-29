using System.Text;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Detection;
using ChromeBlood.Core.Vanilla;

namespace ChromeBlood.Core.Cleanup;

internal static class IndexedGameCleanup
{
    internal static string ReadSteamManifest(string gameRoot)
    {
        DirectoryInfo game = new(Path.GetFullPath(gameRoot));
        DirectoryInfo? common = game.Parent;
        DirectoryInfo? steamApps = common?.Parent;
        if (common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) != true
            || steamApps?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) != true) return "";
        using SecureCacheRoot root = new(steamApps.FullName, new WindowsSecureFileApi(), false);
        using SecureFileLease? file = root.TryOpenRead("appmanifest_1091500.acf");
        if (file is null || file.Length > 256 * 1024) return "";
        byte[] bytes = new byte[(int)file.Length];
        long offset = 0;
        while (offset < bytes.Length)
        {
            int read = RandomAccess.Read(file.Handle, bytes.AsSpan((int)offset), offset);
            if (read == 0) throw new IOException("The Steam manifest changed while reading it. Check preparation again.");
            offset += read;
        }
        string text = Encoding.UTF8.GetString(bytes);
        return SteamManifestParser.GetValue(text, "appid") == "1091500"
            && game.Name.Equals(SteamManifestParser.GetValue(text, "installdir"), StringComparison.OrdinalIgnoreCase) ? text : "";
    }

    internal static CleanupPolicy CreatePolicy(VanillaScanResult scan)
    {
        if (!scan.ProfileMatch.Matched || !scan.Complete)
            throw new CleanupPlanException("A complete matching game-folder scan is required before indexed cleanup. Resolve unreadable paths or use the supported game build, then check again.");
        HashSet<string> changed = scan.Entries.Where(entry => entry.Status == VanillaEntryStatus.Changed)
            .Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> protectedContainers = scan.Entries.Where(entry => IsManagerOwned(entry.RelativePath))
            .SelectMany(entry => Ancestors(entry.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        VanillaScanEntry[] candidates = scan.Entries.Where(entry => entry.Status == VanillaEntryStatus.Extra
            && !IsManagerOwned(entry.RelativePath)
            && !changed.Contains(entry.RelativePath) && !Ancestors(entry.RelativePath).Any(changed.Contains)
            && !(entry.IsDirectory && protectedContainers.Contains(entry.RelativePath))).ToArray();
        HashSet<string> directories = candidates.Where(entry => entry.IsDirectory).Select(entry => entry.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        CleanupPolicyTarget[] targets = candidates.Where(entry => !Ancestors(entry.RelativePath).Any(directories.Contains))
            .OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new CleanupPolicyTarget(entry.RelativePath, entry.IsDirectory,
                "Not included in the matching Steam 2.5 game-file reference", CleanupTargetExpansion.Exact)).ToArray();
        return CleanupPolicy.FromIndexedTargets(scan.ReferenceIdentity, targets);
    }

    internal static bool IsManagerOwned(string relative)
    {
        string[] parts = relative.Replace('/', '\\').Split('\\');
        return parts[0].Equals("V2077", StringComparison.OrdinalIgnoreCase)
            || parts.Any(part => part.Equals("__folder_managed_by_vortex", StringComparison.OrdinalIgnoreCase)
                || part.Equals("__delete_if_empty", StringComparison.OrdinalIgnoreCase))
            || parts.Length == 1 && parts[0].StartsWith("vortex.deployment.", StringComparison.OrdinalIgnoreCase)
                && parts[0].EndsWith("json", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        int separator = path.LastIndexOf('\\');
        while (separator > 0)
        {
            path = path[..separator];
            yield return path;
            separator = path.LastIndexOf('\\');
        }
    }
}
