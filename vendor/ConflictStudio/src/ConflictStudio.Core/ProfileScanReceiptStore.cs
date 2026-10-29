using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ConflictStudio.Core;

public static class ProfileScanReceiptStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };

    public static void Write(string path, ProfileScanReceipt receipt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Validate(receipt);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, fullPath, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static ProfileScanReceipt Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            JsonNode document = JsonNode.Parse(File.ReadAllText(path)) ?? throw new ProfileScanReceiptException("The profile scan receipt is empty.");
            if (document["schemaVersion"]?.GetValue<int>() == 1) MigrateSchemaOne(document);
            MigrateArchiveXlFailureKinds(document);
            ProfileScanReceipt receipt = document.Deserialize<ProfileScanReceipt>(Options) ?? throw new ProfileScanReceiptException("The profile scan receipt is empty.");
            Validate(receipt);
            return receipt;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ProfileScanReceiptException("The profile scan receipt is invalid.", exception);
        }
    }

    private static void Validate(ProfileScanReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (receipt.SchemaVersion != 2 || receipt.ScannedAtUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(receipt.ProfileName)) throw new ProfileScanReceiptException("The profile scan receipt has an unsupported header.");
        if (receipt.ActiveProviders is null || receipt.ArchiveOrder is null || receipt.ArchiveFailures is null || receipt.ResourceConflicts is null || receipt.VirtualFileShadows is null || receipt.InteractionFindings is null || receipt.RedScriptFlows is null || receipt.SharedStateWrites is null || receipt.LuaCallbacks is null || receipt.TweakOverlaps is null || receipt.ArchiveXlChains is null || receipt.ArchiveXlFailures is null) throw new ProfileScanReceiptException("The profile scan receipt is incomplete.");
    }

    private static void MigrateSchemaOne(JsonNode document)
    {
        document["schemaVersion"] = 2;
        if (document["archiveSummaries"] is not JsonArray summaries) return;
        foreach (JsonObject summary in summaries.OfType<JsonObject>())
        {
            foreach (string collection in new[] { "winning", "losing", "redundant", "unresolved", "unique" })
            {
                if (summary[collection] is not JsonArray outcomes) continue;
                foreach (JsonObject outcome in outcomes.OfType<JsonObject>())
                {
                    if (!outcome.Remove("payloadSha1", out JsonNode? value)) continue;
                    outcome["payloadFingerprint"] = value?.GetValue<string>() == "da39a3ee5e6b4b0d3255bfef95601890afd80709" ? null : value;
                }
            }
        }
    }

    private static void MigrateArchiveXlFailureKinds(JsonNode document)
    {
        if (document["archiveXlFailures"] is not JsonArray failures) return;
        foreach (JsonObject failure in failures.OfType<JsonObject>())
        {
            if (failure.ContainsKey("kind")) continue;
            string message = failure["message"]?.GetValue<string>() ?? string.Empty;
            ArchiveXlFailureKind kind = message.StartsWith("Unsupported ArchiveXL ", StringComparison.Ordinal)
                ? ArchiveXlFailureKind.Coverage
                : message.StartsWith("ArchiveXL ", StringComparison.Ordinal)
                    ? ArchiveXlFailureKind.Malformed
                    : ArchiveXlFailureKind.Operational;
            failure["kind"] = (int)kind;
        }
    }
}

public sealed class ProfileScanReceiptException(string message, Exception? innerException = null) : Exception(message, innerException);
