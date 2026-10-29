using System.Buffers.Binary;
using System.Text;

namespace ConflictStudio.Core;

public enum ResourcePathIndexState { Resolved, Unavailable, Failed }

public sealed record ResourcePathIndexEvidence(ResourcePathIndexState State, string? Provider, string? SourcePath, int RequestedHashes, int ResolvedHashes, string Message);

public sealed record ResourcePathIndexResult(Dictionary<ulong, string> Paths, ResourcePathIndexEvidence Evidence);

public static class ResourcePathHash
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Compute(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Compute(Encoding.UTF8.GetBytes(value));
    }

    internal static ulong Compute(ReadOnlySpan<byte> value)
    {
        ulong hash = Offset;
        foreach (byte current in value)
        {
            hash ^= current;
            hash *= Prime;
        }
        return hash;
    }
}

public static class ResourcePathIndex
{
    private const uint KarkMagic = 0x4B52414B;
    private const int MaximumOutputBytes = 512 * 1024 * 1024;
    private const string RelativePath = "bin\\x64\\plugins\\cyber_engine_tweaks\\tweakdb\\usedhashes.kark";

    public static ResourcePathIndexResult Resolve(Mo2InstancePaths paths, IReadOnlyList<DeploymentProvider> providers, IReadOnlySet<ulong> neededHashes, CancellationToken cancellationToken = default)
        => Resolve(paths, providers, neededHashes, null, cancellationToken);

    public static ResourcePathIndexResult Resolve(Mo2InstancePaths paths, IReadOnlyList<DeploymentProvider> providers, IReadOnlySet<ulong> neededHashes, IReadOnlyDictionary<string, string>? deployedWinners, CancellationToken cancellationToken = default)
        => Resolve(paths, providers, neededHashes, deployedWinners, null, cancellationToken);

    public static ResourcePathIndexResult Resolve(Mo2InstancePaths paths, IReadOnlyList<DeploymentProvider> providers, IReadOnlySet<ulong> neededHashes, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
        => Resolve(paths, providers, neededHashes, deployedWinners, excludedPhysicalPaths, null, cancellationToken);

    internal static ResourcePathIndexResult Resolve(Mo2InstancePaths paths, IReadOnlyList<DeploymentProvider> providers, IReadOnlySet<ulong> neededHashes, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, DeploymentFileManifest? manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(neededHashes);
        string[] exclusions = PhysicalPathExclusions.Normalize(excludedPhysicalPaths);
        PhysicalPathReservation[] reservations = PhysicalPathExclusions.Reservations(providers.Select(value => value.RootPath).ToArray(), exclusions, relative => string.Equals(relative, RelativePath, StringComparison.OrdinalIgnoreCase));
        (string Provider, string Path) source;
        if (deployedWinners is not null && deployedWinners.TryGetValue(RelativePath, out string? winnerId))
        {
            DeploymentProvider? winner = providers.FirstOrDefault(value => string.Equals(value.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
            source = winner is null ? default : (winner.Name, Path.Combine(winner.RootPath, RelativePath));
            DeploymentFileEntry? captured = manifest?.Files.FirstOrDefault(value => string.Equals(value.RelativePath, RelativePath, StringComparison.OrdinalIgnoreCase) && string.Equals(value.Provider.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
            if (captured is not null) source = (captured.Provider.Name, captured.PhysicalPath);
        }
        else
        {
            if (deployedWinners is not null)
            {
                source = providers.Where(value => VortexDeploymentFiles.IsEffective(value.ManagerId, RelativePath, deployedWinners)).Select(value => (value.Name, Path.Combine(value.RootPath, RelativePath))).FirstOrDefault(value => File.Exists(value.Item2));
            }
            else
            {
                int existingPosition = providers.Select((value, position) => (position, path: Path.Combine(value.RootPath, RelativePath))).Where(value => File.Exists(value.path)).Select(value => value.position).DefaultIfEmpty(int.MaxValue).First();
                PhysicalPathReservation? reservation = PhysicalPathExclusions.First(reservations, RelativePath);
                if (reservation is not null && reservation.ProviderPosition < existingPosition)
                {
                    DeploymentProvider provider = providers[reservation.ProviderPosition];
                    source = (provider.Name, Path.Combine(provider.RootPath, RelativePath));
                }
                else source = providers.Select(value => (value.Name, Path.Combine(value.RootPath, RelativePath))).FirstOrDefault(value => File.Exists(value.Item2));
            }
        }
        if (!string.IsNullOrWhiteSpace(source.Path) && PhysicalPathExclusions.Contains(exclusions, source.Path)) return new ResourcePathIndexResult([], new ResourcePathIndexEvidence(ResourcePathIndexState.Unavailable, source.Provider, source.Path, neededHashes.Count, 0, "The active usedhashes.kark resource path index was excluded because its evidence snapshot could not be captured."));
        if (string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path)) return new ResourcePathIndexResult([], new ResourcePathIndexEvidence(ResourcePathIndexState.Unavailable, string.IsNullOrWhiteSpace(source.Provider) ? null : source.Provider, string.IsNullOrWhiteSpace(source.Path) ? null : source.Path, neededHashes.Count, 0, "No active usedhashes.kark resource path index was found."));
        if (paths.GameRoot is null) return new ResourcePathIndexResult([], new ResourcePathIndexEvidence(ResourcePathIndexState.Unavailable, source.Provider, source.Path, neededHashes.Count, 0, "The Cyberpunk game root is unavailable, so the Oodle decoder cannot be resolved."));
        string oodlePath = Path.Combine(paths.GameRoot, "bin", "x64", "oo2ext_7_win64.dll");
        if (!File.Exists(oodlePath)) return new ResourcePathIndexResult([], new ResourcePathIndexEvidence(ResourcePathIndexState.Unavailable, source.Provider, source.Path, neededHashes.Count, 0, "Cyberpunk's Oodle decoder was not found."));
        if (PhysicalPathExclusions.Contains(exclusions, oodlePath)) return new ResourcePathIndexResult([], new ResourcePathIndexEvidence(ResourcePathIndexState.Unavailable, source.Provider, source.Path, neededHashes.Count, 0, "Cyberpunk's Oodle decoder was excluded because its evidence snapshot could not be captured."));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] decompressed = ReadKark(source.Path, oodlePath);
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<ulong, string> resolved = ResolveLines(decompressed, neededHashes, cancellationToken);
            return new ResourcePathIndexResult(resolved, new ResourcePathIndexEvidence(ResourcePathIndexState.Resolved, source.Provider, source.Path, neededHashes.Count, resolved.Count, $"Resolved {resolved.Count:N0} of {neededHashes.Count:N0} active resource hashes from {source.Provider}."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return new ResourcePathIndexResult([], new ResourcePathIndexEvidence(ResourcePathIndexState.Failed, source.Provider, source.Path, neededHashes.Count, 0, $"The resource path index could not be read: {exception.Message}"));
        }
    }

    public static Dictionary<ulong, string> ResolveLines(ReadOnlySpan<byte> lines, IReadOnlySet<ulong> neededHashes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(neededHashes);
        Dictionary<ulong, string> resolved = [];
        int start = lines.Length >= 3 && lines[0] == 0xEF && lines[1] == 0xBB && lines[2] == 0xBF ? 3 : 0;
        while (start < lines.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int relativeEnd = lines[start..].IndexOf((byte)'\n');
            int end = relativeEnd < 0 ? lines.Length : start + relativeEnd;
            int contentEnd = end > start && lines[end - 1] == (byte)'\r' ? end - 1 : end;
            ReadOnlySpan<byte> line = lines[start..contentEnd];
            if (line.Length > 0)
            {
                ulong hash = ResourcePathHash.Compute(line);
                if (neededHashes.Contains(hash) && !resolved.ContainsKey(hash)) resolved.Add(hash, Encoding.UTF8.GetString(line));
            }
            if (relativeEnd < 0) break;
            start = end + 1;
        }
        return resolved;
    }

    public static ResourceProvider[] Apply(IReadOnlyList<ResourceProvider> resources, IReadOnlyDictionary<ulong, string> paths)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(paths);
        return resources.Select(value => value.ResourcePath is not null || !paths.TryGetValue(value.ResourceHash, out string? path) ? value : value with { ResourcePath = path, ResourceType = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(), PathConfidence = ResourcePathConfidence.ResolvedIndex }).ToArray();
    }

    private static byte[] ReadKark(string karkPath, string oodlePath)
    {
        byte[] file = File.ReadAllBytes(karkPath);
        if (file.Length < 9 || BinaryPrimitives.ReadUInt32LittleEndian(file) != KarkMagic) throw new InvalidDataException("The resource path index has an invalid KARK header.");
        uint outputSize = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4));
        if (outputSize == 0 || outputSize > MaximumOutputBytes) throw new InvalidDataException("The resource path index declares an unsafe output size.");
        return OodleDecoder.Decompress(file[8..], (int)outputSize, oodlePath);
    }
}
