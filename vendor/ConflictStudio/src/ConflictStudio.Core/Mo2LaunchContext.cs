namespace ConflictStudio.Core;

public enum Mo2LaunchEvidence { ExplicitRoot, WorkingDirectory, ExecutableDirectory, SavedRoot }

public sealed record Mo2LaunchContext(string Root, string? SelectedProfile, Mo2LaunchEvidence Evidence);

public static class Mo2LaunchContextResolver
{
    public static Mo2LaunchContext? TryResolve(string workingDirectory, string executablePath, string? explicitRoot = null, string? savedRoot = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            string? explicitInstance = InstanceRoot(explicitRoot, false);
            return explicitInstance is null ? null : Context(explicitInstance, Mo2LaunchEvidence.ExplicitRoot);
        }
        string? root = null;
        root = InstanceRoot(workingDirectory);
        if (root is not null) return Context(root, Mo2LaunchEvidence.WorkingDirectory);
        root = InstanceRoot(Path.GetDirectoryName(Path.GetFullPath(executablePath)));
        if (root is not null) return Context(root, Mo2LaunchEvidence.ExecutableDirectory);
        root = InstanceRoot(savedRoot);
        return root is null ? null : Context(root, Mo2LaunchEvidence.SavedRoot);
    }

    private static Mo2LaunchContext Context(string root, Mo2LaunchEvidence evidence)
    {
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(root);
        return new Mo2LaunchContext(paths.Root, paths.SelectedProfile, evidence);
    }

    private static string? InstanceRoot(string? candidate, bool searchParents = true)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        string fullPath;
        try { fullPath = Path.GetFullPath(candidate); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        DirectoryInfo? directory = new(File.Exists(fullPath) ? Path.GetDirectoryName(fullPath)! : fullPath);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ModOrganizer.ini"))) return directory.FullName;
            if (!searchParents) return null;
            directory = directory.Parent;
        }
        return null;
    }
}
