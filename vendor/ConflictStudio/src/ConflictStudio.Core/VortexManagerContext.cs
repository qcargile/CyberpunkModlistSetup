using System.Text.Json;
using System.Security.Cryptography;
using System.Text.Encodings.Web;

namespace ConflictStudio.Core;

public enum ModManagerKind { Mo2, Vortex, Manual }

public sealed record VortexProviderContext(string Id, string Name, string RootPath, int Order);

public sealed record VortexManagerContext(
    int SchemaVersion,
    string ContextId,
    DateTimeOffset CapturedAtUtc,
    string ProfileId,
    string ProfileName,
    string GameRoot,
    string StagingRoot,
    bool DeploymentFresh,
    VortexProviderContext[] Providers,
    Dictionary<string, string> DeployedWinners,
    string[] ArchiveOrder,
    string? ArchiveOrderSha256,
    DateTimeOffset? HeartbeatAtUtc = null,
    bool DeploymentInventoryComplete = false,
    int DeploymentFileCount = 0,
    int RelevantDeploymentFileCount = 0,
    int UnmappedRelevantFileCount = 0,
    long BridgeRefreshMilliseconds = 0,
    int TargetRelocatedFileCount = 0);

public sealed record VortexBridgeHeartbeat(int SchemaVersion, string ContextId, string ProfileId, DateTimeOffset HeartbeatAtUtc);

public static class VortexBridgeHeartbeatStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string PathForContext(string contextPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(contextPath))!, "heartbeat.json");
    }

    public static VortexBridgeHeartbeat Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(path));
    }

    public static VortexBridgeHeartbeat Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        VortexBridgeHeartbeat heartbeat;
        try { heartbeat = JsonSerializer.Deserialize<VortexBridgeHeartbeat>(content, Options) ?? throw new InvalidDataException("The Vortex bridge heartbeat is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("The Vortex bridge heartbeat is invalid.", exception); }
        if (heartbeat.SchemaVersion != 1 || !IsSha256(heartbeat.ContextId) || string.IsNullOrWhiteSpace(heartbeat.ProfileId) || heartbeat.HeartbeatAtUtc == default || heartbeat.HeartbeatAtUtc.Offset != TimeSpan.Zero) throw new InvalidDataException("The Vortex bridge heartbeat header is invalid.");
        return heartbeat;
    }

    public static void Write(string path, VortexBridgeHeartbeat heartbeat)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(heartbeat);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllBytes(fullPath, JsonSerializer.SerializeToUtf8Bytes(heartbeat));
    }

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class VortexDeploymentFiles
{
    public static bool IsDeployedPath(string? managerId, string relativePath, IReadOnlyDictionary<string, string>? deployedWinners)
        => deployedWinners is null || deployedWinners.ContainsKey(Normalize(relativePath)) || string.Equals(managerId, "game-directory", StringComparison.OrdinalIgnoreCase);

    public static bool IsEffective(string? managerId, string relativePath, IReadOnlyDictionary<string, string>? deployedWinners)
    {
        if (deployedWinners is null) return true;
        return deployedWinners.TryGetValue(Normalize(relativePath), out string? winnerId)
            ? string.Equals(managerId, winnerId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(managerId, "game-directory", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Replace('/', '\\').TrimStart('\\');
}

public static class VortexManagerContextStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static VortexManagerContext Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllBytes(path));
    }

    public static VortexManagerContext Read(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        VortexManagerContext context;
        try { context = JsonSerializer.Deserialize<VortexManagerContext>(content, Options) ?? throw new InvalidDataException("The saved Vortex profile information is empty."); }
        catch (JsonException exception) { throw new InvalidDataException("The saved Vortex profile information could not be read.", exception); }
        Validate(context);
        string gameRoot = Path.GetFullPath(context.GameRoot);
        string stagingRoot = Path.GetFullPath(context.StagingRoot);
        VortexProviderContext[] resolvedProviders = context.Providers.OrderBy(value => value.Order).Select(value => value with { RootPath = Path.GetFullPath(value.RootPath) }).ToArray();
        Dictionary<string, int> providerNameCounts = resolvedProviders.GroupBy(value => value.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(value => value.Key, value => value.Count(), StringComparer.OrdinalIgnoreCase);
        VortexProviderContext[] providers = resolvedProviders.Select(value => providerNameCounts[value.Name] > 1 ? value with { Name = $"{value.Name} [{value.Id}]" } : value).ToArray();
        Dictionary<string, string> winners = new(context.DeployedWinners.Select(value => new KeyValuePair<string, string>(Normalize(value.Key), value.Value)), StringComparer.OrdinalIgnoreCase);
        return context with { GameRoot = gameRoot, StagingRoot = stagingRoot, Providers = providers, DeployedWinners = winners };
    }

    private static void Validate(VortexManagerContext context)
    {
        if (context.SchemaVersion != 1 || !IsSha256(context.ContextId) || context.CapturedAtUtc == default || context.CapturedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(context.ProfileId) || string.IsNullOrWhiteSpace(context.ProfileName)) throw new InvalidDataException("The saved Vortex profile information has an unsupported format or missing or invalid profile details.");
        if (context.HeartbeatAtUtc is DateTimeOffset heartbeat && (heartbeat == default || heartbeat.Offset != TimeSpan.Zero)) throw new InvalidDataException("The Vortex heartbeat timestamp is invalid.");
        if (!Path.IsPathRooted(context.GameRoot) || !Path.IsPathRooted(context.StagingRoot) || !Directory.Exists(context.GameRoot) || !Directory.Exists(context.StagingRoot)) throw new InvalidDataException("The saved Vortex game folder or mod staging folder has an invalid path or does not exist.");
        if (context.Providers is null || context.DeployedWinners is null || context.ArchiveOrder is null) throw new InvalidDataException("The saved Vortex profile information is missing its mod list, installed-file selections, or archive order.");
        if (!string.Equals(context.ContextId, ComputeContextId(context), StringComparison.Ordinal)) throw new InvalidDataException("The saved Vortex profile information does not match its verification code. Refresh it through the Conflict Studio extension in Vortex.");
        if (context.DeploymentFileCount < 0 || context.RelevantDeploymentFileCount < 0 || context.UnmappedRelevantFileCount < 0 || context.BridgeRefreshMilliseconds < 0 || context.TargetRelocatedFileCount < 0) throw new InvalidDataException("The Vortex deployment metrics are invalid.");
        if (context.DeploymentInventoryComplete && (context.RelevantDeploymentFileCount > context.DeploymentFileCount || context.UnmappedRelevantFileCount > context.RelevantDeploymentFileCount || context.DeployedWinners.Count > context.RelevantDeploymentFileCount || context.TargetRelocatedFileCount > context.UnmappedRelevantFileCount)) throw new InvalidDataException("The Vortex deployment counts are inconsistent.");
        HashSet<string> providerIds = new(StringComparer.OrdinalIgnoreCase);
        HashSet<int> orders = [];
        string stagingRoot = Path.GetFullPath(context.StagingRoot);
        foreach (VortexProviderContext provider in context.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Id)) throw new InvalidDataException("A Vortex provider has no id.");
            if (string.IsNullOrWhiteSpace(provider.Name)) throw new InvalidDataException($"Vortex provider '{provider.Id}' has no display name.");
            if (!Path.IsPathRooted(provider.RootPath)) throw new InvalidDataException($"Vortex provider '{provider.Name}' has a relative staging path: {provider.RootPath}");
            if (!providerIds.Add(provider.Id)) throw new InvalidDataException($"The Vortex provider id is duplicated: {provider.Id}");
            if (!orders.Add(provider.Order)) throw new InvalidDataException($"The Vortex provider order is duplicated at position {provider.Order}.");
            if (!IsWithin(stagingRoot, provider.RootPath)) throw new InvalidDataException($"Vortex provider '{provider.Name}' is outside the staging directory: {provider.RootPath}");
        }
        foreach ((string relativePath, string providerId) in context.DeployedWinners)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath) || !providerIds.Contains(providerId)) throw new InvalidDataException("The Vortex deployment winner context is invalid.");
        }
        if (context.ArchiveOrder.Any(string.IsNullOrWhiteSpace) || context.ArchiveOrderSha256 is not null && !IsSha256(context.ArchiveOrderSha256)) throw new InvalidDataException("The Vortex archive order context is invalid.");
    }

    internal static string ComputeContextId(VortexManagerContext context)
    {
        using MemoryStream content = new();
        using (Utf8JsonWriter writer = new(content, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("profileId", context.ProfileId);
            writer.WriteString("profileName", context.ProfileName);
            writer.WriteString("gameRoot", Path.GetFullPath(context.GameRoot));
            writer.WriteString("stagingRoot", Path.GetFullPath(context.StagingRoot));
            writer.WriteBoolean("deploymentFresh", context.DeploymentFresh);
            writer.WriteStartArray("providers");
            foreach (VortexProviderContext provider in context.Providers.OrderBy(value => value.Order))
            {
                writer.WriteStartObject();
                writer.WriteString("id", provider.Id);
                writer.WriteString("name", provider.Name);
                writer.WriteString("rootPath", Path.GetFullPath(provider.RootPath));
                writer.WriteNumber("order", provider.Order);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartObject("deployedWinners");
            foreach ((string relativePath, string providerId) in context.DeployedWinners.OrderBy(value => value.Key, StringComparer.Ordinal)) writer.WriteString(relativePath, providerId);
            writer.WriteEndObject();
            writer.WriteStartArray("archiveOrder");
            foreach (string archive in context.ArchiveOrder) writer.WriteStringValue(archive);
            writer.WriteEndArray();
            writer.WriteString("archiveOrderSha256", context.ArchiveOrderSha256);
            writer.WriteBoolean("deploymentInventoryComplete", context.DeploymentInventoryComplete);
            writer.WriteNumber("deploymentFileCount", context.DeploymentFileCount);
            writer.WriteNumber("relevantDeploymentFileCount", context.RelevantDeploymentFileCount);
            writer.WriteNumber("unmappedRelevantFileCount", context.UnmappedRelevantFileCount);
            writer.WriteNumber("targetRelocatedFileCount", context.TargetRelocatedFileCount);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(content.ToArray()));
    }

    private static bool IsWithin(string root, string path)
    {
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Normalize(string value) => value.Replace('/', '\\').TrimStart('\\');

    private static bool IsSha256(string? value) => value is not null && value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public static class VortexDeploymentGuard
{
    public static string DefaultContextPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio", "vortex", "context.json");

    public static void RequireNoDeployment(string gameRoot, string contextPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        if (!File.Exists(contextPath)) return;
        VortexManagerContext context;
        try { context = VortexManagerContextStore.Read(contextPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            string? contextGameRoot = TryReadGameRoot(contextPath);
            if (contextGameRoot is null || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot)), Path.TrimEndingDirectorySeparator(contextGameRoot), StringComparison.OrdinalIgnoreCase)) return;
            throw new CrossManagerDeploymentException($"The saved Vortex profile information could not be read, so Conflict Studio cannot confirm that Vortex's deployed files have been removed from this Cyberpunk installation: {exception.Message}");
        }
        string root = Path.GetFullPath(gameRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(root, context.GameRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return;
        string prefix = root + Path.DirectorySeparatorChar;
        Dictionary<string, VortexProviderContext> providers = context.Providers.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase);
        bool ownershipUnresolved = false;
        bool deployedFile = context.DeployedWinners.Any(entry =>
        {
            string relativePath = entry.Key;
            string path = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
            if (!providers.TryGetValue(entry.Value, out VortexProviderContext? provider))
            {
                ownershipUnresolved = true;
                return false;
            }
            string source = Path.GetFullPath(Path.Combine(provider.RootPath, relativePath));
            if (!File.Exists(source))
            {
                ownershipUnresolved = true;
                return false;
            }
            try
            {
                FileInfo deployed = new(path);
                FileInfo staged = new(source);
                if (deployed.Length != staged.Length) return false;
                using FileStream deployedStream = File.OpenRead(path);
                using FileStream stagedStream = File.OpenRead(source);
                return SHA256.HashData(deployedStream).AsSpan().SequenceEqual(SHA256.HashData(stagedStream));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                ownershipUnresolved = true;
                return false;
            }
        });
        if (!deployedFile && !ownershipUnresolved) return;
        if (!deployedFile) throw new CrossManagerDeploymentException($"Vortex profile '{context.ProfileName}' still lists files in this Cyberpunk installation, but their original copies in Vortex's mod folders are unavailable for comparison. Conflict Studio cannot confirm that Vortex's deployed files have been removed.");
        throw new CrossManagerDeploymentException($"Vortex profile '{context.ProfileName}' still has at least 1 deployed file with matching content in this Cyberpunk installation. Purge the Vortex deployment before scanning an MO2 profile, or switch Conflict Studio to Vortex mode.");
    }

    private static string? TryReadGameRoot(string contextPath)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(contextPath));
            if (!document.RootElement.TryGetProperty("gameRoot", out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) return null;
            string path = value.GetString()!;
            return Path.IsPathRooted(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

public sealed class CrossManagerDeploymentException(string message) : Exception(message);
