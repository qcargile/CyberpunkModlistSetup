using ChromeBlood.Contracts.Paths;
using ChromeBlood.Contracts.Serialization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChromeBlood.Core.Parity;

public sealed class LegacyParityCorpus
{
    private static readonly Regex CommitPattern = new("^[0-9a-f]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    private LegacyParityCorpus(string root, LegacyParityIndex index, IReadOnlyList<LegacyParityFixture> fixtures)
    {
        Root = root;
        Index = index;
        Fixtures = fixtures;
    }

    public string Root { get; }

    public LegacyParityIndex Index { get; }

    public IReadOnlyList<LegacyParityFixture> Fixtures { get; }

    public static LegacyParityCorpus LoadEmbedded()
    {
        static byte[] Read(string name)
        {
            using Stream resource = typeof(LegacyParityCorpus).Assembly.GetManifestResourceStream("ChromeBlood.Cleanup." + name)
                ?? throw new InvalidOperationException("The setup cleanup policy is missing.");
            using MemoryStream bytes = new();
            resource.CopyTo(bytes);
            return bytes.ToArray();
        }
        LegacyParityIndex index = ContractJson.Deserialize<LegacyParityIndex>(Read("cases.json"));
        ValidateIndex(index);
        if (index.Cases.Length != 1 || index.Cases[0].Id != "full-clean" || index.Cases[0].Command != LegacyParityCommand.FullClean)
            throw Invalid("parity.policy.invalid", "cases", "The setup cleanup policy is invalid.");
        LegacyParityCase policy = index.Cases[0];
        ValidateCase(policy, "full-clean");
        byte[] payload = Read(policy.ExpectedFile);
        if (Convert.ToHexStringLower(SHA256.HashData(payload)) != policy.Sha256)
            throw Invalid("parity.policy.sha256", policy.ExpectedFile, "The embedded cleanup policy did not match its recorded hash.");
        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement expected = document.RootElement.Clone();
        if (ContainsAbsolutePath(expected))
            throw Invalid("parity.policy.root", policy.ExpectedFile, "The cleanup policy contains an absolute path.");
        return new LegacyParityCorpus("embedded", index, [new LegacyParityFixture(policy, expected)]);
    }

    public static LegacyParityCorpus Load(string corpusRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusRoot);

        string root = Path.GetFullPath(corpusRoot);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Corpus root does not exist: {root}");
        }
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid("parity.corpus.reparse", "root", "Corpus root cannot be a reparse point.");
        }

        string indexPath = Path.Combine(root, "cases.json");
        FileAttributes indexAttributes;
        try
        {
            indexAttributes = File.GetAttributes(indexPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            throw new LegacyParityCorpusException(
                "parity.index.topology",
                "cases.json",
                "The parity index must be a direct regular file.",
                exception);
        }
        if ((indexAttributes & FileAttributes.Directory) != 0)
        {
            throw Invalid("parity.index.topology", "cases.json", "The parity index must be a direct regular file.");
        }
        if ((indexAttributes & FileAttributes.ReparsePoint) != 0)
        {
            throw Invalid("parity.index.reparse", "cases.json", "The parity index cannot be a reparse point.");
        }

        string indexJson = File.ReadAllText(indexPath);
        LegacyParityIndex index;
        try
        {
            index = ContractJson.Deserialize<LegacyParityIndex>(indexJson);
        }
        catch (ContractFormatException exception)
        {
            throw new LegacyParityCorpusException(
                "parity.index.invalid",
                "cases.json",
                exception.Message,
                exception);
        }

        ValidateIndex(index);

        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> expectedFiles = new(StringComparer.OrdinalIgnoreCase);
        List<LegacyParityFixture> fixtures = new(index.Cases.Length);
        for (int i = 0; i < index.Cases.Length; i++)
        {
            LegacyParityCase parityCase = index.Cases[i]
                ?? throw Invalid("parity.case.invalid", $"cases[{i}]", "Parity cases cannot be null.");
            string casePath = $"cases[{i}]";
            ValidateCase(parityCase, casePath);

            if (!ids.Add(parityCase.Id))
            {
                throw Invalid("parity.case.id.duplicate", $"{casePath}.id", $"Duplicate parity case ID: {parityCase.Id}");
            }
            if (!expectedFiles.Add(parityCase.ExpectedFile))
            {
                throw Invalid(
                    "parity.expected.duplicate",
                    $"{casePath}.expectedFile",
                    $"Expected parity files must be unique: {parityCase.ExpectedFile}");
            }

            string expectedPath = ResolveExpectedPath(root, parityCase.ExpectedFile, casePath);
            if (!File.Exists(expectedPath))
            {
                throw Invalid(
                    "parity.expected.missing",
                    $"{casePath}.expectedFile",
                    $"Expected parity file does not exist: {parityCase.ExpectedFile}");
            }
            if ((File.GetAttributes(expectedPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw Invalid(
                    "parity.expected.reparse",
                    $"{casePath}.expectedFile",
                    $"Expected parity file cannot be a reparse point: {parityCase.ExpectedFile}");
            }

            byte[] expectedBytes = File.ReadAllBytes(expectedPath);
            string actualHash = Convert.ToHexStringLower(SHA256.HashData(expectedBytes));
            if (!actualHash.Equals(parityCase.Sha256, StringComparison.Ordinal))
            {
                throw Invalid(
                    "parity.expected.sha256",
                    $"{casePath}.sha256",
                    $"Expected SHA-256 {parityCase.Sha256}, found {actualHash}.");
            }

            JsonElement expected;
            try
            {
                using JsonDocument document = JsonDocument.Parse(expectedBytes);
                expected = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new LegacyParityCorpusException(
                    "parity.expected.invalidJson",
                    parityCase.ExpectedFile,
                    exception.Message,
                    exception);
            }

            if (ContainsAbsolutePath(expected))
            {
                throw Invalid(
                    "parity.expected.root.unnormalized",
                    parityCase.ExpectedFile,
                    "Expected parity JSON contains an absolute path instead of <GAME_ROOT>.");
            }

            fixtures.Add(new LegacyParityFixture(parityCase, expected));
        }

        ValidateTopology(root, expectedFiles);

        return new LegacyParityCorpus(root, index, fixtures);
    }

    private static void ValidateIndex(LegacyParityIndex index)
    {
        if (index.SchemaVersion != 1)
        {
            throw Invalid("parity.index.schemaVersion", "schemaVersion", "Legacy parity schemaVersion must be 1.");
        }

        if (index.LegacyCommit is null || !CommitPattern.IsMatch(index.LegacyCommit))
        {
            throw Invalid("parity.index.legacyCommit", "legacyCommit", "Legacy commit must be a lowercase 40-character SHA-1.");
        }

        if (index.Cases is null || index.Cases.Length == 0)
        {
            throw Invalid("parity.index.cases", "cases", "Legacy parity index must contain at least one case.");
        }
    }

    private static void ValidateCase(LegacyParityCase parityCase, string path)
    {
        if (string.IsNullOrWhiteSpace(parityCase.Id))
        {
            throw Invalid("parity.case.id", $"{path}.id", "Parity case ID is required.");
        }

        if (!IsExpectedFileName(parityCase.ExpectedFile)
            || parityCase.ExpectedFile.Equals("cases.json", StringComparison.OrdinalIgnoreCase)
            || parityCase.ExpectedFile.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("parity.expected.path", $"{path}.expectedFile", "Expected file must be a simple filename beneath the corpus root.");
        }

        if (parityCase.Sha256 is null || !HashPattern.IsMatch(parityCase.Sha256))
        {
            throw Invalid("parity.expected.sha256.format", $"{path}.sha256", "SHA-256 must contain 64 lowercase hexadecimal characters.");
        }
    }

    private static string ResolveExpectedPath(string root, string expectedFile, string casePath)
    {
        if (!IsExpectedFileName(expectedFile))
        {
            throw Invalid("parity.expected.path", $"{casePath}.expectedFile", "Expected file must be relative to the corpus root.");
        }

        string resolved = Path.GetFullPath(Path.Combine(root, expectedFile));
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("parity.expected.path", $"{casePath}.expectedFile", "Expected file escapes the corpus root.");
        }

        return resolved;
    }

    private static bool IsExpectedFileName(string? value)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            ContractWindowsPath.ValidateSegment(value);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateTopology(string root, HashSet<string> expectedFiles)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(entry);
            FileAttributes attributes = File.GetAttributes(entry);
            bool allowedFile = name.Equals("cases.json", StringComparison.Ordinal)
                || name.Equals(".gitattributes", StringComparison.Ordinal)
                || expectedFiles.Contains(name);
            if (!allowedFile
                || (attributes & FileAttributes.Directory) != 0
                || (attributes & FileAttributes.ReparsePoint) != 0 && !expectedFiles.Contains(name))
            {
                throw Invalid(
                    "parity.corpus.unexpected",
                    name,
                    $"The parity corpus contains unexpected topology: {name}");
            }
        }
    }

    private static bool ContainsAbsolutePath(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            string? value = element.GetString();
            return value is not null
                && !value.Equals("<GAME_ROOT>", StringComparison.Ordinal)
                && (ContractWindowsPath.LooksRootedOrNamespace(value)
                    || ContractWindowsPath.TryNormalizeAbsolute(value, out _));
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(ContainsAbsolutePath);
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            return element.EnumerateObject().Any(property => ContainsAbsolutePath(property.Value));
        }

        return false;
    }

    private static LegacyParityCorpusException Invalid(string code, string path, string message)
        => new(code, path, message);
}

public sealed record LegacyParityFixture(LegacyParityCase Case, JsonElement Expected);

public sealed class LegacyParityCorpusException : Exception
{
    public LegacyParityCorpusException(string code, string path, string message)
        : base(message)
    {
        Code = code;
        Path = path;
    }

    public LegacyParityCorpusException(string code, string path, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
        Path = path;
    }

    public string Code { get; }

    public string Path { get; }
}
