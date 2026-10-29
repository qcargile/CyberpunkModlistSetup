using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text.Json;
using ChromeBlood.Core.Vanilla;
using ProtoBuf;
using SteamKit2;
using SteamKit2.Internal;

return Run(args);

static int Run(string[] args)
{
    if (args.Length is not (6 or 7) || args[0] != "--depot-cache" || args[2] != "--app-manifest" || args[4] != "--output"
        || (args.Length == 7 && args[6] != "--verify"))
    {
        Console.Error.WriteLine("Usage: ChromeBlood.GameIndex --depot-cache <Steam/depotcache> --app-manifest <appmanifest_1091500.acf> --output <steam-2.5.json> [--verify]");
        return 2;
    }
    string depotRoot = Path.GetFullPath(args[1]);
    string appManifest = Path.GetFullPath(args[3]);
    string output = Path.GetFullPath(args[5]);
    string steamApps = Path.GetDirectoryName(appManifest)!;
    if (output.StartsWith(Path.TrimEndingDirectorySeparator(depotRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || output.StartsWith(Path.TrimEndingDirectorySeparator(steamApps) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || output.Equals(appManifest, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("The output cannot replace a Steam source.");
    ValveKeyValues app = ValveKeyValues.Parse(System.Text.Encoding.UTF8.GetString(ReadDirectFile(appManifest, 1048576))).Object("AppState");
    if (app.Value("appid") != "1091500" || app.Value("buildid") != "26161058"
        || !ulong.TryParse(app.Value("StateFlags"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong stateFlags) || stateFlags != 4
        || app.Object("UserConfig").Value("language") != "english" || app.Object("UserConfig").OptionalValue("BetaKey") is not ("" or "public"))
        throw new InvalidDataException("The Steam installation is not the pinned English public 2.5 build.");
    if (app.OptionalObject("MountedConfig") is { } mounted
        && (mounted.Value("language") != "english" || mounted.OptionalValue("BetaKey") is not ("" or "public")))
        throw new InvalidDataException("The mounted Steam profile is not the pinned English public profile.");
    DepotSource[] sources =
    [
        new(1091501, 3524634124112875626, "e4299d31a2c8f92517139946e444cf5f65e0098d4406aecdf0ab6d527b3eb3b8"),
        new(2060310, 4793018690565322422, "3815b138a0bf3d383398c6634a554c3b25c2579c5f51d5826bb28e316a65b8dc"),
        new(2138330, 5565496786254603194, "47dcf532980b6572e9a88f5411881faf2a86ab4c1b05f6147824b6f6d9d6c2a5")
    ];
    ValveKeyValues installed = app.Object("InstalledDepots");
    if (installed.Count != sources.Length) throw new InvalidDataException("The installed depot set is not the reference depot set.");
    Dictionary<string, VanillaFile> files = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, string> directories = new(StringComparer.OrdinalIgnoreCase);
    List<DepotEvidence> evidence = [];
    foreach (DepotSource source in sources)
    {
        string manifestId = source.ManifestId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (installed.Object(source.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Value("manifest") != manifestId)
            throw new InvalidDataException("The installed depot manifest does not match the pinned source.");
        byte[] raw = ReadDirectFile(Path.Combine(depotRoot, $"{source.Id}_{source.ManifestId}.manifest"), 16777216);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(raw));
        if (sha256 != source.Sha256) throw new InvalidDataException("A depot manifest differs from its pinned source bytes.");
        uint payloadCrc = ValidateSections(raw);
        DepotManifest manifest = DepotManifest.Deserialize(raw);
        if (manifest.FilenamesEncrypted || manifest.DepotID != source.Id || manifest.ManifestGID != source.ManifestId
            || manifest.Files is null || manifest.Files.Count is 0 or > 100000)
            throw new InvalidDataException("A depot manifest has encrypted names or invalid identity or cardinality.");
        int fileCount = 0;
        int directoryCount = 0;
        long totalBytes = 0;
        HashSet<string> depotPaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (DepotManifest.FileData file in manifest.Files)
        {
            string relative = ValidatePath(file.FileName);
            if (!depotPaths.Add(relative)) throw new InvalidDataException("A depot contains duplicate or colliding paths.");
            if (!string.IsNullOrEmpty(file.LinkTarget) || file.Flags.HasFlag(EDepotFileFlag.Symlink)) throw new InvalidDataException("A depot path is a symbolic link.");
            if (file.Flags.HasFlag(EDepotFileFlag.Directory))
            {
                if (directories.TryGetValue(relative, out string? previous) && previous != relative) throw new InvalidDataException("Depot directories collide by case.");
                directories[relative] = relative;
                directoryCount++;
            }
            else
            {
                if (file.FileHash.Length != 20 || file.TotalSize > long.MaxValue) throw new InvalidDataException("A depot file has invalid size or SHA-1.");
                VanillaFile entry = new(relative, (long)file.TotalSize, Convert.ToHexStringLower(file.FileHash));
                if (files.TryGetValue(relative, out VanillaFile? previous) && previous != entry) throw new InvalidDataException("Depot files have conflicting definitions.");
                files[relative] = entry;
                totalBytes = checked(totalBytes + entry.Size);
                fileCount++;
            }
        }
        if ((ulong)totalBytes != manifest.TotalUncompressedSize) throw new InvalidDataException("A depot's file sizes do not match its manifest total.");
        evidence.Add(new(source.Id, manifestId, sha256, fileCount, directoryCount, totalBytes, payloadCrc.ToString("x8")));
    }
    foreach (string relative in files.Keys.Concat(directories.Keys).ToArray())
    {
        int separator = relative.LastIndexOf('\\');
        while (separator >= 0)
        {
            string parent = relative[..separator];
            if (files.ContainsKey(parent)) throw new InvalidDataException("A depot file is used as a parent directory.");
            if (directories.TryGetValue(parent, out string? previous) && previous != parent) throw new InvalidDataException("Parent directories collide by case.");
            directories[parent] = parent;
            separator = parent.LastIndexOf('\\');
        }
    }
    if (files.Keys.Any(directories.ContainsKey)) throw new InvalidDataException("A depot path is both a file and a directory.");
    byte[] result = JsonSerializer.SerializeToUtf8Bytes(new
    {
        SchemaVersion = 1,
        GameVersion = "2.5",
        AppId = 1091500,
        BuildId = "26161058",
        Language = "english",
        Branch = "public",
        Provenance = "Local Steam client depotcache. Depot IDs and manifest IDs match installed appmanifest_1091500.acf. Raw bytes are SHA-256 pinned and payload CRC-32 checked. No CDN download or cryptographic Steam signature verification was performed. No installed game files were used as reference content.",
        Parser = "SteamKit2 3.5.0; SteamRE/SteamKit commit 49387aec55c24104946af7a7b160148358e538b4; LGPL-2.1-only; maintainer tool dependency only",
        Depots = evidence,
        Directories = directories.Values.Order(StringComparer.Ordinal).ToArray(),
        Files = files.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray()
    });
    if (args.Length == 7)
    {
        if (!File.ReadAllBytes(output).AsSpan().SequenceEqual(result)) throw new InvalidDataException("The imported reference differs from the existing output.");
    }
    else
    {
        ValidateAncestors(Path.GetDirectoryName(output)!);
        using FileStream target = new(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        target.Write(result);
        target.Flush(true);
    }
    Console.WriteLine($"Steam build 26161058: {files.Count} files, {directories.Count} directories, {files.Values.Sum(file => file.Size)} bytes; SHA256 {Convert.ToHexStringLower(SHA256.HashData(result))}");
    foreach (DepotEvidence depot in evidence) Console.WriteLine($"Depot {depot.Id}: {depot.FileCount} files, {depot.DirectoryCount} explicit directories, {depot.TotalBytes} bytes; CRC32 {depot.PayloadCrc32}; SHA256 {depot.RawSha256}");
    return 0;
}

static uint ValidateSections(byte[] bytes)
{
    using MemoryStream stream = new(bytes, false);
    using BinaryReader reader = new(stream);
    ReadOnlyMemory<byte> payload = default;
    ContentManifestMetadata? metadata = null;
    HashSet<uint> sections = [];
    while (true)
    {
        uint magic = reader.ReadUInt32();
        if (magic == 0x32c415ab) break;
        int length = reader.ReadInt32();
        if (length < 0 || length > stream.Length - stream.Position || !sections.Add(magic)) throw new InvalidDataException("A manifest section has an invalid length or duplicate type.");
        int start = checked((int)stream.Position);
        if (magic == 0x71f617d0) payload = bytes.AsMemory(start - 4, length + 4);
        else if (magic == 0x1f4812be)
        {
            using MemoryStream section = new(bytes, start, length, false);
            metadata = Serializer.Deserialize<ContentManifestMetadata>(section);
        }
        else if (magic != 0x1b81b817) throw new InvalidDataException("A manifest section has an unexpected type.");
        stream.Position += length;
    }
    if (stream.Position != stream.Length || payload.IsEmpty || metadata is null || sections.Count != 3 || metadata.filenames_encrypted)
        throw new InvalidDataException("A manifest is incomplete, encrypted, or contains trailing data.");
    uint actual = Crc32.HashToUInt32(payload.Span);
    if (actual != metadata.crc_clear) throw new InvalidDataException("The manifest payload CRC-32 does not match.");
    return actual;
}

static byte[] ReadDirectFile(string path, int maximumBytes)
{
    ValidateAncestors(path);
    using FileStream source = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    if (source.Length > maximumBytes) throw new InvalidDataException("A Steam manifest exceeds its size limit.");
    byte[] bytes = new byte[checked((int)source.Length)];
    source.ReadExactly(bytes);
    return bytes;
}

static void ValidateAncestors(string path)
{
    for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("A source or output path contains a reparse point.");
}

static string ValidatePath(string path)
{
    if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || Path.IsPathRooted(path)) throw new InvalidDataException("A depot path is invalid.");
    string normalized = path.Replace('/', '\\');
    foreach (string part in normalized.Split('\\'))
    {
        string stem = part.Split('.')[0];
        if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
            throw new InvalidDataException("A depot contains an unsafe Windows path.");
    }
    return normalized;
}

internal sealed record DepotSource(uint Id, ulong ManifestId, string Sha256);
internal sealed record DepotEvidence(uint Id, string ManifestId, string RawSha256, int FileCount, int DirectoryCount, long TotalBytes, string PayloadCrc32);
