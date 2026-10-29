using System.Diagnostics.CodeAnalysis;

namespace ChromeBlood.Core.Detection;

public enum ProbeStatus
{
    Available,
    Missing,
    AccessDenied,
    Locked,
    ReparsePoint,
    CleanupFailed,
    Unavailable,
    NotAllowed
}

[SuppressMessage("Design", "CA1000:Do not declare static members on generic types")]
public sealed record ProbeObservation<T>(ProbeStatus Status, T? Value)
{
    public static ProbeObservation<T> Available(T value) => new(ProbeStatus.Available, value);

    public static ProbeObservation<T> Missing() => new(ProbeStatus.Missing, default);

    public static ProbeObservation<T> AccessDenied() => new(ProbeStatus.AccessDenied, default);

    public static ProbeObservation<T> Locked() => new(ProbeStatus.Locked, default);

    public static ProbeObservation<T> ReparsePoint() => new(ProbeStatus.ReparsePoint, default);

    public static ProbeObservation<T> CleanupFailed() => new(ProbeStatus.CleanupFailed, default);

    public static ProbeObservation<T> Unavailable() => new(ProbeStatus.Unavailable, default);
}

public enum FileSystemEntryKind
{
    File,
    Directory,
    Other
}

public sealed record FileSystemEntryObservation(
    string Path,
    FileSystemEntryKind Kind,
    ProbeStatus Status,
    FileAttributes? Attributes);

public interface IFileProbe
{
    ProbeObservation<FileSystemEntryKind> GetExistence(string path);

    ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> Enumerate(string directoryPath);

    ProbeObservation<string> ReadText(string path);

    ProbeObservation<FileAttributes> GetAttributes(string path);

    ProbeObservation<string> GetFinalPath(string path);

    ProbeObservation<bool> IsWritable(string path);
}

public interface IExecutableProductVersionProbe
{
    ProbeObservation<string> GetProductVersion(string executablePath);
}

public interface IBoundedDirectoryProbe
{
    ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> EnumerateDirectories(
        string directoryPath,
        int maximumAcceptedEntries,
        Func<string, bool> includeDirectoryName);
}
