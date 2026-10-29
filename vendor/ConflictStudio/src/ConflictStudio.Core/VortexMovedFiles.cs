using System.Text.Json;

namespace ConflictStudio.Core;

public sealed record VortexMovedFile(VortexProviderContext Provider, string RelativePath, string SourcePath, string PhysicalPath);

public sealed class VortexMovedFiles
{
    private readonly Dictionary<string, string> _paths;
    private readonly Dictionary<string, VortexMovedFile[]> _directories;

    private VortexMovedFiles(VortexMovedFile[] files, ProfileInputSnapshot[] inputs)
    {
        Files = files;
        Inputs = inputs;
        _paths = files.ToDictionary(value => value.SourcePath, value => value.PhysicalPath, StringComparer.OrdinalIgnoreCase);
        _directories = files.GroupBy(value => Path.GetDirectoryName(value.SourcePath)!, StringComparer.OrdinalIgnoreCase).ToDictionary(value => value.Key, value => value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<VortexMovedFile> Files { get; }
    public ProfileInputSnapshot[] Inputs { get; }

    public string Resolve(string sourcePath) => _paths.GetValueOrDefault(Path.GetFullPath(sourcePath), sourcePath);

    public IReadOnlyList<VortexMovedFile> InDirectory(string sourceDirectory) => _directories.GetValueOrDefault(Path.GetFullPath(sourceDirectory)) ?? [];

    public static VortexMovedFiles Capture(VortexManagerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        Dictionary<string, VortexProviderContext> providers = context.Providers.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);
        List<VortexMovedFile> files = [];
        List<ProfileInputSnapshot> inputs = [];
        foreach ((string relative, string winnerId) in context.DeployedWinners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!providers.TryGetValue(winnerId, out VortexProviderContext? provider)) continue;
            string source = Path.GetFullPath(Path.Combine(provider.RootPath, relative));
            string deployed = Path.GetFullPath(Path.Combine(context.GameRoot, relative));
            if (!source.StartsWith(Path.GetFullPath(provider.RootPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !deployed.StartsWith(Path.GetFullPath(context.GameRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || File.Exists(source) || !File.Exists(source + ".vortex_lnk")) continue;
            try
            {
                ProfileInputSnapshot marker = ProfileInputGuard.Capture(source + ".vortex_lnk");
                using JsonDocument document = JsonDocument.Parse(marker.Content);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("target", out JsonElement target)
                    || target.ValueKind != JsonValueKind.String
                    || !Path.IsPathFullyQualified(target.GetString()!)) continue;
                string targetPath = Path.GetFullPath(target.GetString()!);
                if (!string.Equals(targetPath, deployed, StringComparison.OrdinalIgnoreCase) || !File.Exists(deployed)) continue;
                files.Add(new VortexMovedFile(provider, relative, source, deployed));
                inputs.Add(marker);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            {
            }
        }
        return new VortexMovedFiles(files.ToArray(), inputs.ToArray());
    }
}
