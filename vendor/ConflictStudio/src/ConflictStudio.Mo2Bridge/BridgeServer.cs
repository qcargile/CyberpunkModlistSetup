using ConflictStudio.Core;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConflictStudio.Mo2Bridge;

public sealed class BridgeServer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() }, MaxDepth = 32 };
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly object _outputLock = new();
    private readonly Mo2ArchiveWorkspace _workspace;
    private long _generation;

    public BridgeServer(TextReader input, TextWriter output) : this(input, output, new Mo2ArchiveWorkspace()) { }

    public BridgeServer(TextReader input, TextWriter output, Mo2ArchiveWorkspace workspace)
    {
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    public async Task RunAsync()
    {
        Task active = Task.CompletedTask;
        CancellationTokenSource? activeCancellation = null;
        BoundedLineReader reader = new(_input);
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is string line)
            {
                BridgeRequest? request = null;
                try
                {
                    request = JsonSerializer.Deserialize<BridgeRequest>(line, JsonOptions) ?? throw new InvalidDataException("The bridge request is empty.");
                    ArgumentException.ThrowIfNullOrWhiteSpace(request.Id);
                    ArgumentException.ThrowIfNullOrWhiteSpace(request.Operation);
                    if (request.Operation == "cancel")
                    {
                        Cancel(activeCancellation);
                        Write(new { request.Id, type = "result", ok = true, request.Operation, data = new { cancelled = true } });
                        continue;
                    }
                    if (request.Operation != "scan" && !active.IsCompleted)
                    {
                        WriteError(request, "An archive operation is already running.", false);
                        continue;
                    }
                    if (request.Operation == "scan")
                    {
                        Cancel(activeCancellation);
                    }
                    CancellationTokenSource cancellation = new();
                    CancellationTokenSource? previousCancellation = activeCancellation;
                    activeCancellation = cancellation;
                    Task previous = active;
                    long generation = Volatile.Read(ref _generation);
                    BridgeRequest current = request;
                    active = Task.Run(async () =>
                    {
                        object? result = null;
                        Exception? failure = null;
                        try
                        {
                            await previous.ConfigureAwait(false);
                            previousCancellation?.Dispose();
                            cancellation.Token.ThrowIfCancellationRequested();
                            result = Execute(current, generation, cancellation.Token);
                        }
                        catch (Exception exception) { failure = exception; }
                        finally { WriteTerminal(current, generation, result, failure, cancellation.Token); }
                    });
                }
                catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
                {
                    WriteError(request, exception.Message, false);
                }
            }
        }
        finally
        {
            Cancel(activeCancellation);
            await active.ConfigureAwait(false);
            activeCancellation?.Dispose();
        }
    }

    private object Execute(BridgeRequest request, long generation, CancellationToken cancellationToken)
    {
        return request.Operation switch
        {
            "scan" => _workspace.Scan(Required(request.InstanceRoot, "instanceRoot"), Required(request.ProfileModlistPath, "profileModlistPath"), request.ActiveProviders, new BridgeProgress(value => WriteCurrent(generation, new { request.Id, type = "progress", ok = true, request.Operation, value.Phase, value.Completed, value.Total, value.CurrentItem, value.BytesCompleted, value.BytesTotal })), cancellationToken),
            "select" => _workspace.Select(Required(request.Token, "token"), request.SelectedArchives ?? [], request.Order ?? request.ProposedOrder, request.Offset, request.Limit),
            "preview" => _workspace.Preview(Required(request.Token, "token"), request.Order ?? throw new ArgumentException("order is required."), request.UnknownImpactAcknowledged),
            "apply" => _workspace.Apply(Required(request.Token, "token"), request.Order ?? throw new ArgumentException("order is required."), cancellationToken),
            "undo" => _workspace.Undo(Required(request.Token, "token"), cancellationToken),
            _ => throw new ArgumentException("Unknown bridge operation.")
        };
    }

    private void Cancel(CancellationTokenSource? cancellation)
    {
        lock (_outputLock) Interlocked.Increment(ref _generation);
        cancellation?.Cancel();
    }

    private void WriteTerminal(BridgeRequest request, long generation, object? result, Exception? failure, CancellationToken cancellationToken)
    {
        lock (_outputLock)
        {
            if (cancellationToken.IsCancellationRequested || generation != Volatile.Read(ref _generation) || failure is OperationCanceledException)
                WriteError(request, "The archive operation was cancelled.", true);
            else if (failure is not null) WriteError(request, failure.Message, false);
            else
            {
                try { Write(new { request.Id, type = "result", ok = true, request.Operation, data = result }); }
                catch (InvalidDataException exception) { WriteError(request, exception.Message, false); }
            }
        }
    }

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException(name + " is required.");

    private void WriteCurrent(long generation, object value)
    {
        lock (_outputLock) if (generation == Volatile.Read(ref _generation)) Write(value);
    }

    private void WriteError(BridgeRequest? request, string message, bool cancelled)
        => Write(new { id = request?.Id, type = "error", ok = false, operation = request?.Operation, message, cancelled });

    private void Write(object value)
    {
        string text = JsonSerializer.Serialize(value, JsonOptions);
        if (Encoding.UTF8.GetByteCount(text) > 32 * 1024 * 1024) throw new InvalidDataException("The archive response exceeds 32 MiB. Narrow the selection or request fewer detail rows.");
        lock (_outputLock)
        {
            _output.WriteLine(text);
            _output.Flush();
        }
    }

    private sealed record BridgeRequest(string Id, string Operation, string? InstanceRoot = null, string? ProfileModlistPath = null, Mo2ActiveProvider[]? ActiveProviders = null, string? Token = null, string[]? SelectedArchives = null, string[]? Order = null, string[]? ProposedOrder = null, bool UnknownImpactAcknowledged = false, int Offset = 0, int Limit = 1000);
    private sealed class BridgeProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }

    private sealed class BoundedLineReader(TextReader input)
    {
        private const int MaximumCharacters = 32 * 1024 * 1024;
        private readonly char[] _buffer = new char[4096];
        private int _offset;
        private int _length;

        public async Task<string?> ReadLineAsync()
        {
            StringBuilder line = new();
            while (true)
            {
                if (_offset == _length)
                {
                    _length = await input.ReadAsync(_buffer.AsMemory()).ConfigureAwait(false);
                    _offset = 0;
                    if (_length == 0) return line.Length == 0 ? null : line.ToString();
                }
                int end = Array.IndexOf(_buffer, '\n', _offset, _length - _offset);
                int count = (end < 0 ? _length : end) - _offset;
                if (line.Length + count > MaximumCharacters) throw new InvalidDataException("The bridge request exceeds 32 MiB of text.");
                line.Append(_buffer, _offset, count);
                _offset += count;
                if (end >= 0)
                {
                    _offset++;
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    return line.ToString();
                }
            }
        }
    }
}
