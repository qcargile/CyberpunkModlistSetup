namespace ConflictStudio.Core;

public sealed record RdarArchiveInput(string Provider, string ArchivePath, string? ArchiveName = null, string? LogicalProvider = null);

public sealed record RdarStorageSegment(ulong Offset, uint CompressedSize, uint Size);

public sealed record RdarResourceStorage(uint SegmentStart, uint SegmentEnd);

public sealed record RdarArchivePayloadIndex(string ArchiveName, string ArchivePath, Dictionary<ulong, RdarResourceStorage> Resources, RdarStorageSegment[] Segments);

public sealed record RdarArchiveFailure(string Provider, string ArchiveName, string Message);
public sealed record RdarArchiveWarning(string Provider, string ArchiveName, string Message);

public sealed record RdarResourceScanResult(ResourceProvider[] Resources, RdarArchiveFailure[] Failures, RdarArchiveWarning[]? Warnings = null, RdarArchivePayloadIndex[]? PayloadIndexes = null);

public static class RdarResourceScanner
{
    public static ResourceProvider[] Scan(string archiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        if (!Directory.Exists(archiveDirectory)) throw new DirectoryNotFoundException("The archive directory does not exist.");
        List<ResourceProvider> resources = [];
        foreach (string archive in FindArchives(archiveDirectory)) resources.AddRange(RdarResourceIndexReader.Read(archive));
        return resources.ToArray();
    }

    public static RdarResourceScanResult ScanResilient(string archiveDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveDirectory);
        if (!Directory.Exists(archiveDirectory)) throw new DirectoryNotFoundException("The archive directory does not exist.");
        return ScanResilient(FindArchives(archiveDirectory).Select(path => new RdarArchiveInput(Path.GetFileName(path), path)).ToArray());
    }

    public static RdarResourceScanResult ScanResilient(IReadOnlyList<RdarArchiveInput> archives, IReadOnlyDictionary<ulong, string>? resolvedPaths = null, string? oodlePath = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(archives);
        List<ResourceProvider> resources = [];
        List<RdarArchiveFailure> failures = [];
        List<RdarArchiveWarning> warnings = [];
        List<RdarArchivePayloadIndex> payloadIndexes = [];
        for (int index = 0; index < archives.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RdarArchiveInput archive = archives[index];
            try
            {
                RdarResourceReadResult result = RdarResourceIndexReader.ReadDetailed(archive.ArchivePath, resolvedPaths, archive.LogicalProvider ?? archive.Provider, oodlePath, cancellationToken);
                resources.AddRange(archive.ArchiveName is null ? result.Resources : result.Resources.Select(value => value with { ArchiveName = archive.ArchiveName }));
                payloadIndexes.Add(result.PayloadIndex with { ArchiveName = archive.ArchiveName ?? result.PayloadIndex.ArchiveName });
                if (result.Warning is not null) warnings.Add(new RdarArchiveWarning(archive.Provider, archive.ArchiveName ?? Path.GetFileName(archive.ArchivePath), result.Warning));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException or RdarResourceIndexException) { failures.Add(new RdarArchiveFailure(archive.Provider, archive.ArchiveName ?? Path.GetFileName(archive.ArchivePath), exception.Message)); }
            progress?.Report(new ScanProgress("packed resources", index + 1, archives.Count));
        }
        return new RdarResourceScanResult(resources.ToArray(), failures.ToArray(), warnings.ToArray(), payloadIndexes.ToArray());
    }

    private static string[] FindArchives(string archiveDirectory) => Directory.EnumerateFiles(archiveDirectory, "*", SearchOption.TopDirectoryOnly)
        .Where(path => string.Equals(Path.GetExtension(path), ".archive", StringComparison.OrdinalIgnoreCase))
        .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
