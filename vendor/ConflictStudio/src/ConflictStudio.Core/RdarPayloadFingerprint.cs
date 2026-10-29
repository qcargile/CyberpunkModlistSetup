using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ConflictStudio.Core;

public static class RdarPayloadFingerprint
{
    private const uint Kark = 1263681867;
    private const uint MaximumSegmentBytes = 128 * 1024 * 1024;

    public static ResourceProvider[] Apply(IReadOnlyList<ResourceProvider> resources, IReadOnlyList<RdarArchivePayloadIndex> indexes, string? oodlePath, CancellationToken cancellationToken = default)
    {
        using OodleDecoderSession? decoder = OodleDecoderSession.TryOpen(oodlePath);
        return ApplyWithDecoder(resources, indexes, decoder is null ? null : decoder.Decompress, cancellationToken);
    }

    internal static ResourceProvider[] ApplyWithDecoder(IReadOnlyList<ResourceProvider> resources, IReadOnlyList<RdarArchivePayloadIndex> indexes, Func<byte[], int, byte[]>? decode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(indexes);
        HashSet<ulong> collisions = resources.GroupBy(value => value.ResourceHash).Where(value => value.Select(provider => provider.ArchiveName).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any()).Select(value => value.Key).ToHashSet();
        Dictionary<string, RdarArchivePayloadIndex> byArchive = indexes.ToDictionary(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase);
        Dictionary<(string Archive, ulong Hash), string?> fingerprints = [];
        foreach (IGrouping<string, ResourceProvider> archive in resources.Where(value => collisions.Contains(value.ResourceHash)).GroupBy(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byArchive.TryGetValue(archive.Key, out RdarArchivePayloadIndex? index)) continue;
            try
            {
                using FileStream stream = File.Open(index.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                foreach (ResourceProvider provider in archive)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        fingerprints[(provider.ArchiveName, provider.ResourceHash)] = Fingerprint(stream, index, provider.ResourceHash, decode, cancellationToken);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                    {
                        fingerprints[(provider.ArchiveName, provider.ResourceHash)] = null;
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                foreach (ResourceProvider provider in archive) fingerprints[(provider.ArchiveName, provider.ResourceHash)] = null;
            }
        }
        return resources.Select(value => fingerprints.TryGetValue((value.ArchiveName, value.ResourceHash), out string? fingerprint) ? value with { CookedPayloadSha256 = fingerprint } : value).ToArray();
    }

    private static string? Fingerprint(FileStream stream, RdarArchivePayloadIndex index, ulong resourceHash, Func<byte[], int, byte[]>? decode, CancellationToken cancellationToken)
    {
        if (!index.Resources.TryGetValue(resourceHash, out RdarResourceStorage? resource) || resource.SegmentStart >= resource.SegmentEnd || resource.SegmentEnd > index.Segments.Length) return null;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (uint segmentIndex = resource.SegmentStart; segmentIndex < resource.SegmentEnd; segmentIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RdarStorageSegment segment = index.Segments[segmentIndex];
            if (segment.CompressedSize > MaximumSegmentBytes || segment.Size > MaximumSegmentBytes || segment.Offset > (ulong)stream.Length || segment.CompressedSize > (ulong)stream.Length - segment.Offset) return null;
            byte[] stored = new byte[checked((int)segment.CompressedSize)];
            stream.Position = checked((long)segment.Offset);
            stream.ReadExactly(stored);
            if (segmentIndex == resource.SegmentStart && segment.CompressedSize != segment.Size && stored.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(stored) == Kark)
            {
                if (stored.Length < 8 || decode is null) return null;
                uint declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(4));
                if (declaredSize == 0 || declaredSize > MaximumSegmentBytes) return null;
                int outputSize = (int)declaredSize;
                hash.AppendData(decode(stored[8..], outputSize));
            }
            else hash.AppendData(stored);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
