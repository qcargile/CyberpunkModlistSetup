using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace ChromeBlood.Core.Vanilla;

public sealed record VanillaFile(string RelativePath, long Size, string Sha1);

public sealed record VanillaProfileMatch(bool Matched, string Message);

internal sealed record VanillaDepot(uint Id, string ManifestId, string RawSha256, int FileCount, int DirectoryCount, long TotalBytes, string PayloadCrc32);

internal sealed record VanillaReferenceDocument(int SchemaVersion, string GameVersion, uint AppId, string BuildId, string Language,
    string Branch, string Provenance, string Parser, VanillaDepot[] Depots, string[] Directories, VanillaFile[] Files);

public sealed class VanillaGameIndex
{
    private const string EmbeddedSha256 = "1dd7286671f59b8c2d91bec1c21716d7181b1ddb07876c2841fce92d09e77ea0";
    private readonly VanillaReferenceDocument _document;

    internal VanillaGameIndex(VanillaReferenceDocument document, string identity)
    {
        if (document.SchemaVersion != 1 || document.AppId != 1091500 || document.GameVersion != "2.5"
            || document.BuildId != "26161058" || document.Language != "english" || document.Branch != "public"
            || document.Depots.Length != 3 || document.Files.Length is 0 or > 100000 || document.Directories.Length > 100000)
            throw new InvalidDataException("The vanilla reference profile is invalid.");
        Dictionary<uint, string> expected = new() { [1091501] = "3524634124112875626", [2060310] = "4793018690565322422", [2138330] = "5565496786254603194" };
        foreach (VanillaDepot depot in document.Depots)
            if (!expected.Remove(depot.Id, out string? manifest) || manifest != depot.ManifestId || !IsHash(depot.RawSha256, 64))
                throw new InvalidDataException("The vanilla depot reference is invalid.");
        Dictionary<string, VanillaFile> files = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        foreach (VanillaFile file in document.Files)
        {
            string path = ValidateRelativePath(file.RelativePath);
            if (file.Size < 0 || !IsHash(file.Sha1, 40) || !files.TryAdd(path, file with { RelativePath = path }))
                throw new InvalidDataException("The vanilla reference contains an invalid or colliding file.");
        }
        foreach (string directory in document.Directories)
            if (!directories.Add(ValidateRelativePath(directory))) throw new InvalidDataException("The vanilla reference contains colliding directories.");
        foreach (string path in files.Keys.Concat(directories.ToArray()))
        {
            if (files.ContainsKey(path) && directories.Contains(path)) throw new InvalidDataException("A vanilla path is both a directory and a file.");
            int separator = path.LastIndexOf('\\');
            while (separator >= 0)
            {
                string parent = path[..separator];
                if (files.ContainsKey(parent)) throw new InvalidDataException("A vanilla file is used as a directory.");
                directories.Add(parent);
                separator = parent.LastIndexOf('\\');
            }
        }
        _document = document;
        Identity = identity;
        Files = files.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        Directories = directories.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }

    public string Identity { get; }
    public string BuildId => _document.BuildId;
    public IReadOnlyDictionary<string, VanillaFile> Files { get; }
    public IReadOnlySet<string> Directories { get; }

    public static VanillaGameIndex LoadEmbedded()
    {
        using Stream stream = typeof(VanillaGameIndex).Assembly.GetManifestResourceStream("ChromeBlood.Vanilla.steam-2.5.json")
            ?? throw new InvalidDataException("The Steam 2.5 reference is missing.");
        using MemoryStream bytes = new();
        stream.CopyTo(bytes);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
        if (hash != EmbeddedSha256) throw new InvalidDataException("The Steam 2.5 reference failed its integrity check.");
        bytes.Position = 0;
        VanillaReferenceDocument document = JsonSerializer.Deserialize<VanillaReferenceDocument>(bytes)
            ?? throw new InvalidDataException("The Steam 2.5 reference is empty.");
        return new VanillaGameIndex(document, "steam-1091500-26161058-english-public-sha256:" + hash);
    }

    public VanillaProfileMatch MatchSteamManifest(string text)
    {
        try
        {
            ValveKeyValues root = ValveKeyValues.Parse(text).Object("AppState");
            if (root.Value("appid") != "1091500" || root.Value("buildid") != BuildId)
                return Unsupported();
            if (!ulong.TryParse(root.Value("StateFlags"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong stateFlags)
                || stateFlags != 4) return new(false, "The Steam manifest does not report a completed installation. Finish pending Steam updates or repair its metadata, then scan again.");
            string target = root.OptionalValue("TargetBuildID");
            if (target is not ("" or "0") && target != BuildId) return Unsupported();
            ValveKeyValues user = root.Object("UserConfig");
            if (user.Value("language") != _document.Language || user.OptionalValue("BetaKey") is not ("" or "public")) return Unsupported();
            if (root.OptionalObject("MountedConfig") is { } mounted
                && (mounted.Value("language") != _document.Language || mounted.OptionalValue("BetaKey") is not ("" or "public"))) return Unsupported();
            ValveKeyValues depots = root.Object("InstalledDepots");
            if (depots.Count != _document.Depots.Length) return Unsupported();
            foreach (VanillaDepot depot in _document.Depots)
                if (depots.Object(depot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)).Value("manifest") != depot.ManifestId) return Unsupported();
            return new(true, "Steam 2.5 English public build 26161058 with REDmod and Phantom Liberty matches the reference.");
        }
        catch (InvalidDataException)
        {
            return new(false, "The Steam installation manifest could not be read reliably. Select the matching Steam installation or repair its metadata in Steam.");
        }
    }

    internal static string ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024 || Path.IsPathRooted(value)) throw new InvalidDataException("The reference path is invalid.");
        string normalized = value.Replace('/', '\\');
        foreach (string part in normalized.Split('\\'))
        {
            string stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
                throw new InvalidDataException("The reference contains an unsafe Windows path.");
        }
        return normalized;
    }

    private static bool IsHash(string value, int length) => value.Length == length && value.All(char.IsAsciiHexDigit);
    private static VanillaProfileMatch Unsupported() => new(false, "This file reference supports Steam 2.5 English public build 26161058 with REDmod and Phantom Liberty. This installation has a different build, language, branch, or depot set; its files have not been classified.");
}
