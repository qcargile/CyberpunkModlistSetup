using System.Collections.Concurrent;
using System.Text;

namespace ConflictStudio.Core;

public sealed record DeploymentFileEntry(DeploymentProvider Provider, int ProviderPosition, string RelativePath, string PhysicalPath, string Lane, bool ArchiveXlFallbackRoot);

public sealed record DeploymentFileEnumerationFailure(string Provider, string Path, string Lane, string Message);

public sealed class DeploymentFileManifest
{
    private static readonly string[] Lanes = ["archive\\pc\\mod", "bin\\x64\\plugins", "engine\\config", "r6\\input", "r6\\scripts", "r6\\tweaks", "red4ext\\plugins"];
    private static readonly HashSet<string> TextExtensions = new([".reds", ".lua", ".tweak", ".yaml", ".yml", ".xl", ".json"], StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CapturedContent> _content = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProfileFileSnapshot> _fingerprints = new(StringComparer.OrdinalIgnoreCase);

    private DeploymentFileManifest(DeploymentProvider[] providers, DeploymentFileEntry[] files, DeploymentFileEnumerationFailure[] failures)
    {
        Providers = providers;
        Files = files;
        Failures = failures;
    }

    public DeploymentProvider[] Providers { get; }
    public DeploymentFileEntry[] Files { get; }
    public DeploymentFileEnumerationFailure[] Failures { get; }

    public DeploymentFileManifest WithMovedFiles(VortexMovedFiles movedFiles)
    {
        ArgumentNullException.ThrowIfNull(movedFiles);
        Dictionary<string, (DeploymentProvider Provider, int Position)> providers = Providers.Select((value, position) => (value, position)).Where(value => value.value.ManagerId is not null).ToDictionary(value => value.value.ManagerId!, value => (value.value, value.position), StringComparer.OrdinalIgnoreCase);
        HashSet<string> deployed = movedFiles.Files.Select(value => value.PhysicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<DeploymentFileEntry> files = Files.Where(value => !value.PhysicalPath.EndsWith(".vortex_lnk", StringComparison.OrdinalIgnoreCase) && !deployed.Contains(value.PhysicalPath)).ToList();
        foreach (VortexMovedFile moved in movedFiles.Files)
        {
            if (!providers.TryGetValue(moved.Provider.Id, out var provider) || string.Equals(Path.GetExtension(moved.RelativePath), ".archive", StringComparison.OrdinalIgnoreCase)) continue;
            string? lane = Lanes.FirstOrDefault(value => moved.RelativePath.StartsWith(value + "\\", StringComparison.OrdinalIgnoreCase));
            bool archiveXlFallbackRoot = !Directory.Exists(Path.Combine(provider.Provider.RootPath, "archive", "pc", "mod"));
            if (lane is null && !(archiveXlFallbackRoot && string.Equals(Path.GetExtension(moved.RelativePath), ".xl", StringComparison.OrdinalIgnoreCase))) continue;
            files.Add(new DeploymentFileEntry(provider.Provider, provider.Position, moved.RelativePath, moved.PhysicalPath, lane ?? string.Empty, archiveXlFallbackRoot));
        }
        return new DeploymentFileManifest(Providers, files.ToArray(), Failures);
    }

    public static DeploymentFileManifest Build(IReadOnlyList<DeploymentProvider> providers, CancellationToken cancellationToken = default)
        => Build(providers, (IProgress<ScanProgress>?)null, cancellationToken);

    public static DeploymentFileManifest Build(IReadOnlyList<DeploymentProvider> providers, IProgress<ScanProgress>? progress, CancellationToken cancellationToken = default)
        => Build(providers, (root, pattern) => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories), progress, cancellationToken);

    internal static DeploymentFileManifest Build(IReadOnlyList<DeploymentProvider> providers, Func<string, string, IEnumerable<string>> enumerateFiles, CancellationToken cancellationToken)
        => Build(providers, enumerateFiles, null, cancellationToken);

    private static DeploymentFileManifest Build(IReadOnlyList<DeploymentProvider> providers, Func<string, string, IEnumerable<string>> enumerateFiles, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(enumerateFiles);
        List<DeploymentFileEntry> files = [];
        List<DeploymentFileEnumerationFailure> failures = [];
        HashSet<string> physicalPaths = new(StringComparer.OrdinalIgnoreCase);
        for (int position = 0; position < providers.Count; position++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeploymentProvider provider = providers[position];
            progress?.Report(new ScanProgress("deployment · discovering active files", position, Math.Max(1, providers.Count), provider.Name));
            string archiveLane = Path.Combine(provider.RootPath, "archive", "pc", "mod");
            bool archiveXlFallbackRoot = !Directory.Exists(archiveLane);
            foreach (string lane in Lanes)
            {
                string root = Path.Combine(provider.RootPath, lane);
                if (!Directory.Exists(root)) continue;
                AddFiles(provider, position, lane, root, "*", archiveXlFallbackRoot, enumerateFiles, files, failures, physicalPaths, cancellationToken);
            }
            if (archiveXlFallbackRoot && Directory.Exists(provider.RootPath)) AddFiles(provider, position, string.Empty, provider.RootPath, "*.xl", true, enumerateFiles, files, failures, physicalPaths, cancellationToken);
            progress?.Report(new ScanProgress("deployment · discovering active files", position + 1, Math.Max(1, providers.Count), provider.Name));
        }
        return new DeploymentFileManifest(providers.ToArray(), files.ToArray(), failures.ToArray());
    }

    public ProfileFileSnapshot Capture(DeploymentFileEntry file, bool hashContent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!hashContent) return ProfileInputGuard.CaptureFile(file.PhysicalPath, false);
        if (TextExtensions.Contains(Path.GetExtension(file.PhysicalPath))) return Content(file, cancellationToken).Snapshot;
        return _fingerprints.GetOrAdd(file.PhysicalPath, path =>
        {
            FileSha256Result fingerprint = FileSha256.Fingerprint(path, cancellationToken: cancellationToken);
            return new ProfileFileSnapshot(Path.GetFullPath(path), fingerprint.Length, fingerprint.LastWriteTimeUtc, fingerprint.Sha256, FingerprintSource.Fresh);
        });
    }

    public string ReadText(DeploymentFileEntry file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Content(file, cancellationToken).Text;
    }

    public ReadOnlyMemory<byte> ReadBytes(DeploymentFileEntry file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        return Content(file, cancellationToken).Bytes;
    }

    public ArchiveFingerprint Fingerprint(DeploymentFileEntry file, CancellationToken cancellationToken = default)
    {
        ProfileFileSnapshot snapshot = Capture(file, true, cancellationToken);
        return new ArchiveFingerprint(Path.GetFileName(file.PhysicalPath), snapshot.Length, snapshot.Sha256!);
    }

    private CapturedContent Content(DeploymentFileEntry file, CancellationToken cancellationToken)
        => _content.GetOrAdd(file.PhysicalPath, path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileContentSha256Result capture = FileSha256.ReadAll(path, cancellationToken);
            using StreamReader reader = new(new MemoryStream(capture.Content), Encoding.UTF8, true);
            ProfileFileSnapshot snapshot = new(Path.GetFullPath(path), capture.Length, capture.LastWriteTimeUtc, capture.Sha256, FingerprintSource.Fresh);
            _fingerprints[path] = snapshot;
            return new CapturedContent(snapshot, capture.Content, reader.ReadToEnd());
        });

    private static void AddFiles(DeploymentProvider provider, int position, string lane, string root, string pattern, bool archiveXlFallbackRoot, Func<string, string, IEnumerable<string>> enumerateFiles, List<DeploymentFileEntry> files, List<DeploymentFileEnumerationFailure> failures, HashSet<string> physicalPaths, CancellationToken cancellationToken)
    {
        try
        {
            foreach (string path in enumerateFiles(root, pattern).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string fullPath = Path.GetFullPath(path);
                if (string.Equals(Path.GetExtension(fullPath), ".archive", StringComparison.OrdinalIgnoreCase) || !physicalPaths.Add(fullPath)) continue;
                string relative = Path.GetRelativePath(provider.RootPath, fullPath).Replace('/', '\\');
                files.Add(new DeploymentFileEntry(provider, position, relative, fullPath, lane, archiveXlFallbackRoot));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new DeploymentFileEnumerationFailure(provider.Name, root, lane, exception.Message));
        }
    }

    private sealed record CapturedContent(ProfileFileSnapshot Snapshot, byte[] Bytes, string Text);
}
