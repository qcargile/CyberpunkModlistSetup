using System.Diagnostics;

namespace ConflictStudio.Core;

internal sealed class ArchiveFingerprintProgress
{
    private const long ReportIntervalBytes = 64L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _phase;
    private readonly IProgress<ScanProgress>? _progress;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _activeBytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _total;
    private readonly long _totalBytes;
    private int _completed;
    private long _completedBytes;
    private long _bytesRead;
    private long _lastReportedBytes;
    private long _lastReportedMilliseconds;
    private string? _currentItem;

    public ArchiveFingerprintProgress(IProgress<ScanProgress>? progress, IReadOnlyList<string> paths, string phase = "deployment · indexing archives")
    {
        _progress = progress;
        _phase = phase;
        _total = paths.Count;
        _totalBytes = paths.Sum(TryLength);
        ReportUnsafe(true);
    }

    public void Start(string path)
    {
        lock (_gate)
        {
            _currentItem = Path.GetFileName(path);
            _activeBytes[path] = 0;
            ReportUnsafe(true);
        }
    }

    public void Read(string path, long bytesRead)
    {
        lock (_gate)
        {
            _currentItem = Path.GetFileName(path);
            _activeBytes[path] = bytesRead;
            ReportUnsafe(false);
        }
    }

    public void Complete(string path, long size)
    {
        lock (_gate)
        {
            _completed++;
            _completedBytes += size;
            if (_activeBytes.Remove(path, out long read)) _bytesRead += read;
            _currentItem = Path.GetFileName(path);
            ReportUnsafe(true);
        }
    }

    public void Skip(string path)
    {
        lock (_gate)
        {
            _completed++;
            _completedBytes += TryLength(path);
            if (_activeBytes.Remove(path, out long read)) _bytesRead += read;
            _currentItem = Path.GetFileName(path);
            ReportUnsafe(true);
        }
    }

    private void ReportUnsafe(bool force)
    {
        long activeBytes = _activeBytes.Values.Sum();
        long readBytes = _bytesRead + activeBytes;
        long milliseconds = _elapsed.ElapsedMilliseconds;
        if (!force && readBytes - _lastReportedBytes < ReportIntervalBytes && milliseconds - _lastReportedMilliseconds < 250) return;
        _lastReportedBytes = readBytes;
        _lastReportedMilliseconds = milliseconds;
        _progress?.Report(new ScanProgress(_phase, _completed, _total, _currentItem, _completedBytes + activeBytes, _totalBytes, readBytes, milliseconds));
    }

    private static long TryLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return 0; }
    }
}
