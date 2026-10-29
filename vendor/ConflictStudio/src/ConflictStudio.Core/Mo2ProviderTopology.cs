namespace ConflictStudio.Core;

public sealed record DeploymentProvider(string Name, string RootPath, int? Mo2Priority = null, string? ManagerId = null);

public static class Mo2ProviderTopology
{
    public static DeploymentProvider[] Discover(string mo2Root, IReadOnlyList<string> activeProviders)
        => Discover(mo2Root, activeProviders.Select(value => new Mo2ActiveProvider(value, -1)).ToArray());

    public static DeploymentProvider[] Discover(string mo2Root, IReadOnlyList<Mo2ActiveProvider> activeProviders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        ArgumentNullException.ThrowIfNull(activeProviders);
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(mo2Root);
        List<DeploymentProvider> providers = [];
        if (Directory.Exists(paths.OverwriteRoot)) providers.Add(new DeploymentProvider("Overwrite", paths.OverwriteRoot, int.MaxValue));
        foreach (Mo2ActiveProvider provider in activeProviders)
        {
            string root = Path.Combine(paths.ModsRoot, provider.Name);
            if (Directory.Exists(root)) providers.Add(new DeploymentProvider(provider.Name, root, provider.Priority));
        }
        if (paths.GameRoot is not null && Directory.Exists(paths.GameRoot)) providers.Add(new DeploymentProvider("Game directory", paths.GameRoot, -1));
        if (paths.GameRoot is not null)
        {
            string redmods = Path.Combine(paths.GameRoot, "mods");
            if (Directory.Exists(redmods)) foreach (string redmod in Directory.EnumerateDirectories(redmods).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)) providers.Add(new DeploymentProvider("REDmod: " + Path.GetFileName(redmod), redmod));
        }
        return providers.ToArray();
    }
}
