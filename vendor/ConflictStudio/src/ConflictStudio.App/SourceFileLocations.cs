using ConflictStudio.Core;
using System.IO;

namespace ConflictStudio.App;

public sealed record SourceFileLocation(string Provider, string RelativePath, string? PhysicalPath)
{
    public string Label => Path.GetFileName(RelativePath) + " (" + Provider + ")";
    public override string ToString() => Label;
}

public sealed class SourceFileLocations
{
    private readonly Dictionary<string, string> _providerRoots;
    private readonly Dictionary<string, string> _virtualFiles;

    public SourceFileLocations(ProfileScanReceipt receipt)
    {
        _providerRoots = receipt.SourceProviders.ToDictionary(value => value.Name, value => Path.GetFullPath(value.RootPath), StringComparer.OrdinalIgnoreCase);
        _virtualFiles = receipt.VirtualFileShadows.SelectMany(shadow => shadow.Providers.Select(provider => (Key: Key(provider.Provider, shadow.RelativePath), provider.PhysicalPath)))
            .Where(value => Path.IsPathFullyQualified(value.PhysicalPath)).DistinctBy(value => value.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(value => value.Key, value => value.PhysicalPath, StringComparer.OrdinalIgnoreCase);
    }

    public SourceFileLocation[] ForItems(IEnumerable<ConflictWorkItem> items)
        => items.SelectMany(item => item.SourceFiles).Distinct()
            .OrderBy(value => value.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(file => new SourceFileLocation(file.Provider, file.FilePath, Resolve(file))).ToArray();

    public static string ExistingPath(SourceFileLocation file)
    {
        if (file.PhysicalPath is not string path || !File.Exists(path)) throw new FileNotFoundException("This file is no longer available at its scanned location. Scan the profile again.");
        return path;
    }

    private string? Resolve(ConflictSourceFile file)
    {
        if (_virtualFiles.TryGetValue(Key(file.Provider, file.FilePath), out string? physical)) return physical;
        if (!_providerRoots.TryGetValue(file.Provider, out string? root) || Path.IsPathRooted(file.FilePath)) return null;
        string path = Path.GetFullPath(Path.Combine(root, file.FilePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static string Key(string provider, string path) => provider + "\0" + path.Replace('/', '\\');
}
