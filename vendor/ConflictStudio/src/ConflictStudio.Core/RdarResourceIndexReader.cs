using System.Text;

namespace ConflictStudio.Core;

public static class RdarResourceIndexReader
{
    private const uint SupportedVersion = 12;
    private const int HeaderSize = 44;
    private const int ExtendedHeaderSize = 0xAC;
    private const int IndexHeaderSize = 28;
    private const int FileRecordSize = 56;
    private const int CustomDataHeaderSize = 20;
    private const uint MaximumEntries = 1_000_000;
    private const uint MaximumCustomPathBytes = 64 * 1024 * 1024;
    private const string EmptySha1 = "da39a3ee5e6b4b0d3255bfef95601890afd80709";

    public static ResourceProvider[] Read(string archivePath, IReadOnlyDictionary<ulong, string>? resolvedPaths = null, string? provider = null, string? oodlePath = null)
        => ReadDetailed(archivePath, resolvedPaths, provider, oodlePath).Resources;

    public static RdarResourceReadResult ReadDetailed(string archivePath, IReadOnlyDictionary<ulong, string>? resolvedPaths = null, string? provider = null, string? oodlePath = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using FileStream stream = File.OpenRead(archivePath);
        if (stream.Length < HeaderSize) throw new RdarResourceIndexException("The archive is too short for an RDAR header.");
        using BinaryReader reader = new(stream, Encoding.ASCII, true);
        string magic = Encoding.ASCII.GetString(reader.ReadBytes(4));
        uint version = reader.ReadUInt32();
        ulong indexPosition = reader.ReadUInt64();
        uint indexSize = reader.ReadUInt32();
        reader.ReadUInt64();
        reader.ReadUInt32();
        reader.ReadUInt64();
        uint customDataLength = reader.ReadUInt32();
        if (magic != "RDAR" || version != SupportedVersion) throw new RdarResourceIndexException("The archive is not a supported RDAR version 12 file.");
        if (indexPosition > (ulong)stream.Length || indexSize < IndexHeaderSize || indexPosition + indexSize > (ulong)stream.Length) throw new RdarResourceIndexException("The archive index lies outside the file.");

        Dictionary<ulong, string> customPaths;
        string? customPathWarning = null;
        try { customPaths = ReadCustomPaths(stream, customDataLength, indexPosition, oodlePath, cancellationToken); }
        catch (Exception exception) when (exception is IOException or InvalidDataException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            customPaths = [];
            customPathWarning = $"Archive-local resource paths could not be decoded: {exception.Message}";
        }

        stream.Position = checked((long)indexPosition);
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt64();
        uint fileEntryCount = reader.ReadUInt32();
        uint fileSegmentCount = reader.ReadUInt32();
        reader.ReadUInt32();
        if (fileEntryCount > MaximumEntries || fileSegmentCount > MaximumEntries || IndexHeaderSize + (ulong)fileEntryCount * FileRecordSize + (ulong)fileSegmentCount * 16 > indexSize) throw new RdarResourceIndexException("The archive file table is invalid.");

        string archiveName = Path.GetFileName(archivePath);
        ResourceProvider[] resources = new ResourceProvider[fileEntryCount];
        Dictionary<ulong, RdarResourceStorage> storage = [];
        for (int index = 0; index < fileEntryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ulong resourceHash = reader.ReadUInt64();
            reader.ReadInt64();
            uint inlineBufferSegmentCount = reader.ReadUInt32();
            uint segmentStart = reader.ReadUInt32();
            uint segmentEnd = reader.ReadUInt32();
            reader.ReadUInt32();
            reader.ReadUInt32();
            string sha1Value = Convert.ToHexStringLower(reader.ReadBytes(20));
            string? sha1 = sha1Value == EmptySha1 || sha1Value.All(value => value == '0') ? null : sha1Value;
            storage[resourceHash] = new RdarResourceStorage(segmentStart, segmentEnd);
            string? resourcePath = null;
            ResourcePathConfidence pathConfidence = ResourcePathConfidence.Unresolved;
            if (customPaths.TryGetValue(resourceHash, out string? customPath))
            {
                resourcePath = customPath;
                pathConfidence = ResourcePathConfidence.ArchiveCustomData;
            }
            else if (resolvedPaths?.TryGetValue(resourceHash, out string? resolvedPath) == true)
            {
                resourcePath = resolvedPath;
                pathConfidence = ResourcePathConfidence.ResolvedIndex;
            }
            string? resourceType = string.IsNullOrWhiteSpace(resourcePath) ? null : Path.GetExtension(resourcePath).TrimStart('.').ToLowerInvariant();
            resources[index] = new ResourceProvider(archiveName, resourceHash, resourcePath, sha1, new ResourceSegmentMetadata(inlineBufferSegmentCount, segmentStart, segmentEnd), resourceType, pathConfidence, provider);
        }

        RdarStorageSegment[] segments = new RdarStorageSegment[fileSegmentCount];
        for (int index = 0; index < segments.Length; index++) segments[index] = new RdarStorageSegment(reader.ReadUInt64(), reader.ReadUInt32(), reader.ReadUInt32());
        return new RdarResourceReadResult(resources, customPathWarning, new RdarArchivePayloadIndex(archiveName, archivePath, storage, segments));
    }

    private static Dictionary<ulong, string> ReadCustomPaths(FileStream stream, uint customDataLength, ulong indexPosition, string? oodlePath, CancellationToken cancellationToken)
    {
        if (customDataLength < CustomDataHeaderSize || ExtendedHeaderSize + customDataLength > indexPosition) return new Dictionary<ulong, string>();
        stream.Position = ExtendedHeaderSize;
        using BinaryReader reader = new(stream, Encoding.UTF8, true);
        if (reader.ReadUInt32() != 0x4C585253U || reader.ReadUInt32() != 1) return new Dictionary<ulong, string>();
        uint uncompressedSize = reader.ReadUInt32();
        uint compressedSize = reader.ReadUInt32();
        uint pathCount = reader.ReadUInt32();
        if (compressedSize > MaximumCustomPathBytes || uncompressedSize > MaximumCustomPathBytes || pathCount > MaximumEntries || CustomDataHeaderSize + compressedSize > customDataLength || uncompressedSize < compressedSize) throw new InvalidDataException("Archive-local resource path data exceeds the supported safety limit.");
        cancellationToken.ThrowIfCancellationRequested();
        byte[] compressed = reader.ReadBytes(checked((int)compressedSize));
        byte[] payload = uncompressedSize == compressedSize ? compressed : oodlePath is null ? [] : OodleDecoder.Decompress(compressed, checked((int)uncompressedSize), oodlePath);
        if (payload.Length == 0) return [];
        Dictionary<ulong, string> paths = [];
        int start = 0;
        for (uint index = 0; index < pathCount && start < payload.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Array.IndexOf(payload, (byte)0, start);
            if (end < 0) break;
            string path = Encoding.Latin1.GetString(payload, start, end - start);
            if (!string.IsNullOrWhiteSpace(path)) paths[HashResourcePath(path)] = path;
            start = end + 1;
        }
        return paths;
    }

    private static ulong HashResourcePath(string path)
    {
        string normalized = path.Trim().Trim('"').TrimStart('\\', '/').Replace('/', '\\').ToLowerInvariant();
        ulong hash = 14695981039346656037UL;
        foreach (byte value in Encoding.UTF8.GetBytes(normalized))
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return hash;
    }
}

public sealed record RdarResourceReadResult(ResourceProvider[] Resources, string? Warning, RdarArchivePayloadIndex PayloadIndex);

public sealed class RdarResourceIndexException(string message) : Exception(message);
