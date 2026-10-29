using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ConflictStudio.Core;

public sealed record VortexOrderRequest(int SchemaVersion, string RequestId, string ContextId, string ProfileId, DateTimeOffset RequestedAtUtc, DateTimeOffset ExpiresAtUtc, string? ExpectedOrderSha256, ArchiveFingerprint[] Inventory, string[] ProposedOrder, bool RestorePrevious = false, string? RestoreBackupPath = null);

public sealed record VortexOrderResponse(int SchemaVersion, string RequestId, bool Applied, string Message, string? BackupPath, string? WrittenSha256, DateTimeOffset CompletedAtUtc, string? ContextId = null);

public sealed class VortexOrderBridgeStore
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    private readonly string _requestPath;
    private readonly string _responsePath;
    private readonly string _progressPath;
    private readonly string _leasePath;
    private readonly string _mutexName;

    private sealed record OrderProgress(int SchemaVersion, string RequestId, long Sequence);
    private sealed record OrderLease(int SchemaVersion, string RequestId, DateTimeOffset ExpiresAtUtc);

    public VortexOrderBridgeStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _requestPath = Path.Combine(root, "order-request.json");
        _responsePath = Path.Combine(root, "order-response.json");
        _progressPath = Path.Combine(root, "order-progress.json");
        _leasePath = Path.Combine(root, "order-lease.json");
        _mutexName = "Local\\CyberpunkConflictStudio.Vortex." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root))));
    }

    public void WriteRequest(VortexOrderRequest request) => Write(_requestPath, request);

    public VortexOrderRequest? ReadRequest() => Read<VortexOrderRequest>(_requestPath);

    public void WriteResponse(VortexOrderResponse response) => Write(_responsePath, response);

    public VortexOrderResponse? TryReadResponse(string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        VortexOrderResponse? response = Read<VortexOrderResponse>(_responsePath);
        return response is not null && string.Equals(response.RequestId, requestId, StringComparison.Ordinal) ? response : null;
    }

    public VortexOrderResponse Exchange(VortexOrderRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using Mutex mutex = new(false, _mutexName);
        bool ownsMutex;
        try { ownsMutex = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) throw new ArchiveOrderException("Another Conflict Studio instance is already waiting for Vortex to apply an archive order.");
        try
        {
            if (request.SchemaVersion == 2) Write(_leasePath, new OrderLease(2, request.RequestId, DateTimeOffset.UtcNow + timeout));
            WriteRequest(request);
            long lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
            long sequence = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                VortexOrderResponse? response = TryReadResponse(request.RequestId);
                if (response is not null) return response;
                if (System.Diagnostics.Stopwatch.GetElapsedTime(lastProgress) >= timeout) break;
                if (request.SchemaVersion == 2)
                {
                    OrderProgress? progress = Read<OrderProgress>(_progressPath);
                    if (progress is { SchemaVersion: 2 } && progress.RequestId == request.RequestId && progress.Sequence > sequence)
                    {
                        sequence = progress.Sequence;
                        Write(_leasePath, new OrderLease(2, request.RequestId, DateTimeOffset.UtcNow + timeout));
                        lastProgress = System.Diagnostics.Stopwatch.GetTimestamp();
                    }
                }
                Thread.Sleep(100);
            }
            throw new ArchiveOrderException(sequence == 0
                ? "Vortex did not acknowledge the archive order. Keep Vortex open with the matching Conflict Studio extension enabled, then try again."
                : "Vortex stopped reporting archive-order progress. The order was not confirmed. Scan again before retrying.");
        }
        finally
        {
            try
            {
                VortexOrderRequest? pending = ReadRequest();
                if (pending is not null && string.Equals(pending.RequestId, request.RequestId, StringComparison.Ordinal)) File.Delete(_requestPath);
                if (Read<OrderLease>(_leasePath)?.RequestId == request.RequestId) File.Delete(_leasePath);
                if (Read<OrderProgress>(_progressPath)?.RequestId == request.RequestId) File.Delete(_progressPath);
            }
            finally { mutex.ReleaseMutex(); }
        }
    }

    private static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
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

public sealed class VortexArchiveOrderWriter : IArchiveOrderWriter
{
    private VortexManagerContext _context;
    private readonly Func<VortexOrderRequest, VortexOrderResponse> _exchange;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IReadOnlyList<string>> _runningProcesses;
    private ArchiveFingerprint[]? _inventory;

    public VortexArchiveOrderWriter(VortexManagerContext context, Func<VortexOrderRequest, VortexOrderResponse> exchange, Func<DateTimeOffset> clock) : this(context, exchange, clock, RunningProcesses) { }

    public VortexArchiveOrderWriter(VortexManagerContext context, Func<VortexOrderRequest, VortexOrderResponse> exchange, Func<DateTimeOffset> clock, Func<IReadOnlyList<string>> runningProcesses)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _runningProcesses = runningProcesses ?? throw new ArgumentNullException(nameof(runningProcesses));
    }

    public ArchiveOrderApplyResult Apply(ArchiveOrderPreview preview, IReadOnlyList<ArchiveFingerprint> currentArchives)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(currentArchives);
        if (!_context.DeploymentFresh) throw new ArchiveOrderException("Deploy the active Vortex profile before applying an archive order.");
        if (!SameInventory(preview.Observation.Archives, currentArchives)) throw new ArchiveOrderException("The archive inventory changed after the preview. Scan again before applying.");
        if (_runningProcesses().Contains("Cyberpunk2077", StringComparer.OrdinalIgnoreCase)) throw new ArchiveOrderException("Archive order cannot be written while Cyberpunk2077 is running.");
        DateTimeOffset now = _clock().ToUniversalTime();
        string requestId = Guid.NewGuid().ToString("N");
        VortexOrderRequest request = new(2, requestId, _context.ContextId, _context.ProfileId, now, now.AddSeconds(60), preview.Observation.OrderFileSha256, currentArchives.ToArray(), preview.ProposedOrder);
        VortexOrderResponse response = _exchange(request);
        if (response.SchemaVersion != 2) throw new ArchiveOrderException("Update the Conflict Studio Vortex extension and restart Vortex before applying an archive order.");
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) || !response.Applied || response.WrittenSha256 is null) throw new ArchiveOrderException(string.IsNullOrWhiteSpace(response.Message) ? "Vortex rejected the archive order." : response.Message);
        _inventory = currentArchives.ToArray();
        if (response.ContextId is not null) _context = _context with { ContextId = response.ContextId };
        return new ArchiveOrderApplyResult(response.BackupPath, true, preview.Observation.OrderFileSha256 is not null, response.WrittenSha256);
    }

    public void RestorePrevious(ArchiveOrderApplyResult result, string targetPath)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (_runningProcesses().Contains("Cyberpunk2077", StringComparer.OrdinalIgnoreCase)) throw new ArchiveOrderException("Archive order cannot be written while Cyberpunk2077 is running.");
        if (_inventory is null) throw new ArchiveOrderException("There is no Vortex archive order change to undo.");
        string requestId = Guid.NewGuid().ToString("N");
        DateTimeOffset now = _clock().ToUniversalTime();
        VortexOrderRequest request = new(2, requestId, _context.ContextId, _context.ProfileId, now, now.AddSeconds(60), result.WrittenSha256, _inventory, [], true, result.BackupPath);
        VortexOrderResponse response = _exchange(request);
        if (response.SchemaVersion != 2) throw new ArchiveOrderException("Update the Conflict Studio Vortex extension and restart Vortex before restoring an archive order.");
        if (!string.Equals(response.RequestId, requestId, StringComparison.Ordinal) || !response.Applied) throw new ArchiveOrderException(string.IsNullOrWhiteSpace(response.Message) ? "Vortex rejected the archive order restore." : response.Message);
        _inventory = null;
    }

    private static IReadOnlyList<string> RunningProcesses() => System.Diagnostics.Process.GetProcesses().Select(process => process.ProcessName).ToArray();

    private static bool SameInventory(ArchiveFingerprint[] observed, IReadOnlyList<ArchiveFingerprint> current)
    {
        if (observed.Length != current.Count) return false;
        Dictionary<string, ArchiveFingerprint> values = current.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        return observed.All(value => values.TryGetValue(value.Name, out ArchiveFingerprint? now) && now.Size == value.Size && string.Equals(now.Sha256, value.Sha256, StringComparison.Ordinal));
    }
}
