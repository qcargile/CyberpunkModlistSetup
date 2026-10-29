using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConflictStudio.Core;

internal sealed class ContentAddressedAnalysisCache
{
    private const int RetainedEntriesPerLane = 4;
    private readonly string _root;

    public ContentAddressedAnalysisCache(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public static ContentAddressedAnalysisCache Default()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio", "cache", "analysis-v1"));

    public bool TryRead<T>(string lane, string key, out T? value)
    {
        string path = PathFor(lane, key);
        if (!File.Exists(path))
        {
            value = default;
            return false;
        }
        try
        {
            using FileStream file = File.OpenRead(path);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            value = JsonSerializer.Deserialize<T>(gzip);
            return value is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            value = default;
            return false;
        }
    }

    public void Write<T>(string lane, string key, T value)
    {
        string path = PathFor(lane, key);
        string directory = Path.GetDirectoryName(path)!;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(directory);
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (GZipStream gzip = new(file, CompressionLevel.Fastest))
            {
                JsonSerializer.Serialize(gzip, value);
            }
            File.Move(temporary, path, true);
            Trim(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    public static string Key(IEnumerable<string> values)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string value in values)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private string PathFor(string lane, string key)
    {
        if (lane.Length == 0 || lane.Any(value => !char.IsAsciiLetterOrDigit(value) && value != '-')) throw new ArgumentException("The cache lane is invalid.", nameof(lane));
        if (key.Length != 64 || key.Any(value => value is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) throw new ArgumentException("The cache key is invalid.", nameof(key));
        return Path.Combine(_root, lane, key + ".json.gz");
    }

    private static void Trim(string directory)
    {
        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*.json.gz").OrderByDescending(value => value.LastWriteTimeUtc).Skip(RetainedEntriesPerLane))
        {
            try { file.Delete(); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
