using System.Globalization;
using System.IO;
using System.Text;
using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed class DiagnosticLog
{
    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _activityPath;
    private readonly string _startupFailurePath;
    private readonly long _maximumFileBytes;

    public string DirectoryPath { get; }

    public DiagnosticLog(string root, long maximumFileBytes = 2 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        DirectoryPath = root;
        _path = Path.Combine(root, "diagnostics.log");
        _activityPath = Path.Combine(root, "activity.log");
        _startupFailurePath = Path.Combine(root, "startup-failure.log");
        _maximumFileBytes = maximumFileBytes;
    }

    public void Write(string operation, Exception exception)
    {
        TryWrite(operation, exception);
    }

    public bool TryWrite(string operation, Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(exception);
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O}\t{operation}\t{exception.GetType().FullName}\t{exception.Message}{Environment.NewLine}{exception.StackTrace}{Environment.NewLine}");
        try
        {
            lock (_gate)
            {
                AppendBounded(_path, line);
            }
            return true;
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException) { return false; }
    }

    public bool TryWriteStartupFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string version = typeof(DiagnosticLog).Assembly.GetName().Version?.ToString(3) ?? "unknown";
        string executable = Environment.ProcessPath ?? AppContext.BaseDirectory;
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O}\tstartup-failure\tversion={version}\tos={Environment.OSVersion.VersionString}\texecutable={executable}\tcommandLine={Environment.CommandLine}{Environment.NewLine}{exception}{Environment.NewLine}");
        try
        {
            lock (_gate)
            {
                AppendBounded(_startupFailurePath, line);
            }
            return true;
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException) { return false; }
    }

    public string ReadRecent(int maximumCharacters = 60000)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        try
        {
            lock (_gate)
            {
                if (!File.Exists(_path)) return "No application errors have been recorded.";
                using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int bytesToRead = (int)Math.Min(stream.Length, (long)maximumCharacters * 4);
                stream.Seek(-bytesToRead, SeekOrigin.End);
                byte[] buffer = new byte[bytesToRead];
                int bytesRead = stream.Read(buffer, 0, buffer.Length);
                string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                return text.Length <= maximumCharacters ? text : text[^maximumCharacters..];
            }
        }
        catch (Exception readException) when (readException is IOException or UnauthorizedAccessException) { return "The application error log is currently unavailable."; }
    }

    public bool TryWriteAction(string operation, string outcome, string detail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        ArgumentNullException.ThrowIfNull(detail);
        string safeDetail = PrivatePathRedactor.Redact(detail).Replace('\r', ' ').Replace('\n', ' ');
        string line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O}\t{operation}\t{outcome}\t{safeDetail}{Environment.NewLine}");
        try
        {
            lock (_gate)
            {
                AppendBounded(_activityPath, line);
            }
            return true;
        }
        catch (Exception writeException) when (writeException is IOException or UnauthorizedAccessException) { return false; }
    }

    private void AppendBounded(string path, string value)
    {
        Directory.CreateDirectory(DirectoryPath);
        string bounded = Encoding.UTF8.GetByteCount(value) <= _maximumFileBytes ? value : value[^Math.Min(value.Length, (int)Math.Max(1, _maximumFileBytes / 4))..];
        long incomingBytes = Encoding.UTF8.GetByteCount(bounded);
        if (File.Exists(path) && new FileInfo(path).Length + incomingBytes > _maximumFileBytes)
        {
            string previous = Path.Combine(DirectoryPath, Path.GetFileNameWithoutExtension(path) + ".previous" + Path.GetExtension(path));
            File.Move(path, previous, true);
        }
        File.AppendAllText(path, bounded, Encoding.UTF8);
    }
}
