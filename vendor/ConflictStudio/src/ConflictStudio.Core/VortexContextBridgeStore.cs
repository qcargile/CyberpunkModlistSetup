using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ConflictStudio.Core;

public sealed record VortexContextRefreshRequest(int SchemaVersion, string RequestId, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc);

public sealed record VortexContextRefreshResponse(int SchemaVersion, string RequestId, bool Refreshed, string Message, string? ContextId, DateTimeOffset CompletedAtUtc);

public sealed class VortexContextBridgeStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _requestPath;
    private readonly string _responsePath;
    private readonly string _mutexName;

    public VortexContextBridgeStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _requestPath = Path.Combine(root, "context-request.json");
        _responsePath = Path.Combine(root, "context-response.json");
        _mutexName = "Local\\CyberpunkConflictStudio.Vortex.Context." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))));
    }

    public void WriteRequest(VortexContextRefreshRequest request) => Write(_requestPath, request);

    public VortexContextRefreshRequest? ReadRequest() => Read<VortexContextRefreshRequest>(_requestPath);

    public void WriteResponse(VortexContextRefreshResponse response) => Write(_responsePath, response);

    public VortexContextRefreshResponse? TryReadResponse(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        VortexContextRefreshResponse? response = Read<VortexContextRefreshResponse>(_responsePath);
        return response is not null && string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ? response : null;
    }

    public VortexContextRefreshResponse Exchange(VortexContextRefreshRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using Mutex mutex = new(false, _mutexName);
        bool ownsMutex;
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) throw new InvalidOperationException("Another Conflict Studio instance is already refreshing the Vortex profile.");
        try
        {
            WriteRequest(request);
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VortexContextRefreshResponse? response = TryReadResponse(request.RequestId);
                if (response is not null) return response;
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Vortex did not export the active profile. Keep Vortex open with the Conflict Studio bridge enabled, then try again.");
        }
        finally
        {
            VortexContextRefreshRequest? pending = ReadRequest();
            if (pending is not null && string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal)) File.Delete(_requestPath);
            mutex.ReleaseMutex();
        }
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));
            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
