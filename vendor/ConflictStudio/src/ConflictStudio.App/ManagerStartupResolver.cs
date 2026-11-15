using ConflictStudio.Core;
using System.IO;

namespace ConflictStudio.App;

public sealed record ApplicationLaunchOptions(string? Manager, string? Root, string? Profile, string? VortexContext)
{
    public static ApplicationLaunchOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        string? manager = null;
        string? root = null;
        string? profile = null;
        string? vortexContext = null;
        for (int index = 1; index < arguments.Count; index++)
        {
            string name = arguments[index];
            if (name is not ("--manager" or "--root" or "--profile" or "--vortex-context")) continue;
            if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index])) throw new ArgumentException($"{name} requires a value.", nameof(arguments));
            string value = arguments[index];
            if (name == "--manager") manager = value;
            else if (name == "--root") root = value;
            else if (name == "--profile") profile = value;
            else vortexContext = value;
        }
        return new ApplicationLaunchOptions(manager, root, profile, vortexContext);
    }
}

public sealed record ManagerStartupSelection(ModManagerKind ManagerKind, string Root, string ProfileName, string? ContextPath, Mo2LaunchEvidence? Evidence);

public sealed record VortexProfileOption(string Name, string ContextPath)
{
    public override string ToString() => Name;
}

public sealed record ManualProfileOption(string Name, string GameRoot)
{
    public override string ToString() => Name;
}

public static class ManagerStartupResolver
{
    public static ManagerStartupSelection? ResolveMo2(ApplicationLaunchOptions options, string workingDirectory, string executablePath, WorkspacePreference? preference)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Manager is not null && !string.Equals(options.Manager, "mo2", StringComparison.OrdinalIgnoreCase)) return null;
        Mo2LaunchContext? context = Mo2LaunchContextResolver.TryResolve(workingDirectory, executablePath, options.Root, preference?.Mo2Root);
        if (context is null) return null;
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(context.Root);
        string executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath))!;
        if (IsWithin(paths.ModsRoot, executableDirectory)) throw new InvalidOperationException("Conflict Studio cannot run from the resolved MO2 mods directory. Install it as an MO2 tool outside mods.");
        Mo2Profile[] profiles = Mo2ProfileDiscovery.Discover(context.Root);
        string? requested = options.Profile is not null && !string.Equals(options.Profile, "current", StringComparison.OrdinalIgnoreCase) ? options.Profile : context.SelectedProfile;
        if (requested is null && preference is not null && string.Equals(Path.GetFullPath(preference.Mo2Root), context.Root, StringComparison.OrdinalIgnoreCase)) requested = preference.ProfileName;
        Mo2Profile? profile = requested is null ? profiles.Length == 1 ? profiles[0] : null : profiles.FirstOrDefault(value => string.Equals(value.Name, requested, StringComparison.OrdinalIgnoreCase));
        if (profile is null && requested is not null) throw new InvalidOperationException($"The MO2 profile does not exist: {requested}");
        return profile is null ? null : new ManagerStartupSelection(ModManagerKind.Mo2, context.Root, profile.Name, null, context.Evidence);
    }

    public static ManagerStartupSelection? ResolveVortex(ApplicationLaunchOptions options, string defaultContextPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultContextPath);
        if (options.Manager is not null && !string.Equals(options.Manager, "vortex", StringComparison.OrdinalIgnoreCase)) return null;
        string path = Path.GetFullPath(options.VortexContext ?? defaultContextPath);
        if (!File.Exists(path))
        {
            if (string.Equals(options.Manager, "vortex", StringComparison.OrdinalIgnoreCase)) throw new FileNotFoundException("The Vortex profile information is missing. Enable the Conflict Studio extension in Vortex, open Vortex, then try again.", path);
            return null;
        }
        VortexManagerContext context = VortexManagerContextStore.Read(path);
        return new ManagerStartupSelection(ModManagerKind.Vortex, context.GameRoot, context.ProfileName, path, null);
    }

    public static ManagerStartupSelection? ResolveManual(ApplicationLaunchOptions options, WorkspacePreference? preference)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Manager is not null && !string.Equals(options.Manager, "manual", StringComparison.OrdinalIgnoreCase)) return null;
        string? root = options.Root ?? (preference?.ManagerKind == ModManagerKind.Manual ? preference.Mo2Root : null);
        if (string.IsNullOrWhiteSpace(root)) return null;
        string fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(Path.Combine(fullRoot, "archive", "pc", "content")) && !File.Exists(Path.Combine(fullRoot, "bin", "x64", "Cyberpunk2077.exe"))) return null;
        return new ManagerStartupSelection(ModManagerKind.Manual, fullRoot, "Deployed game", null, null);
    }

    private static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
