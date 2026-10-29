using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ChromeBlood.Contracts.Paths;

namespace ChromeBlood.Core.Detection;

internal interface IWindowsFinalPathApi
{
    SafeFileHandle Open(string path);

    uint GetFinalPath(SafeFileHandle file, char[] buffer);

    int GetLastError();
}

internal interface IWindowsWriteProbe
{
    void WriteAndFlush(string path);

    void Delete(string path);
}

public sealed class WindowsFileProbe : IFileProbe, IBoundedDirectoryProbe, IExecutableProductVersionProbe
{
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int InitialFinalPathCapacity = 512;
    private const int MaximumFinalPathCapacity = 32768;
    private readonly IWindowsFinalPathApi _finalPathApi;
    private readonly IWindowsWriteProbe _writeProbe;

    public WindowsFileProbe()
        : this(new WindowsFinalPathApi(), new WindowsWriteProbe())
    {
    }

    internal WindowsFileProbe(IWindowsFinalPathApi finalPathApi)
        : this(finalPathApi, new WindowsWriteProbe())
    {
    }

    internal WindowsFileProbe(IWindowsWriteProbe writeProbe)
        : this(new WindowsFinalPathApi(), writeProbe)
    {
    }

    private WindowsFileProbe(IWindowsFinalPathApi finalPathApi, IWindowsWriteProbe writeProbe)
    {
        ArgumentNullException.ThrowIfNull(finalPathApi);
        ArgumentNullException.ThrowIfNull(writeProbe);
        _finalPathApi = finalPathApi;
        _writeProbe = writeProbe;
    }

    public ProbeObservation<FileSystemEntryKind> GetExistence(string path)
    {
        string normalized = Normalize(path);
        try
        {
            FileAttributes attributes = File.GetAttributes(normalized);
            return ProbeObservation<FileSystemEntryKind>.Available(GetKind(attributes));
        }
        catch (FileNotFoundException)
        {
            return ProbeObservation<FileSystemEntryKind>.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeObservation<FileSystemEntryKind>.Missing();
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<FileSystemEntryKind>.AccessDenied();
        }
        catch (IOException)
        {
            return ProbeObservation<FileSystemEntryKind>.Unavailable();
        }
    }

    public ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> Enumerate(string directoryPath)
    {
        string normalized = Normalize(directoryPath);
        ProbeObservation<FileSystemEntryKind> existence = GetExistence(normalized);
        if (existence.Status != ProbeStatus.Available)
        {
            return FromStatus<IReadOnlyList<FileSystemEntryObservation>>(existence.Status);
        }

        if (existence.Value != FileSystemEntryKind.Directory)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Unavailable();
        }

        try
        {
            FileSystemEntryObservation[] entries = Directory
                .EnumerateFileSystemEntries(normalized)
                .Select(ObserveEntry)
                .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Available(entries);
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.AccessDenied();
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Missing();
        }
        catch (IOException)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Unavailable();
        }
    }

    public ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> EnumerateDirectories(
        string directoryPath,
        int maximumAcceptedEntries,
        Func<string, bool> includeDirectoryName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumAcceptedEntries);
        ArgumentNullException.ThrowIfNull(includeDirectoryName);
        string normalized = Normalize(directoryPath);
        ProbeObservation<FileSystemEntryKind> existence = GetExistence(normalized);
        if (existence.Status != ProbeStatus.Available)
        {
            return FromStatus<IReadOnlyList<FileSystemEntryObservation>>(existence.Status);
        }

        if (existence.Value != FileSystemEntryKind.Directory)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Unavailable();
        }

        try
        {
            FileSystemEntryObservation[] entries = Directory
                .EnumerateDirectories(normalized, "*", SearchOption.TopDirectoryOnly)
                .Where(path => includeDirectoryName(Path.GetFileName(path)))
                .Take(checked(maximumAcceptedEntries + 1))
                .Select(ObserveEntry)
                .ToArray();
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Available(entries);
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.AccessDenied();
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Missing();
        }
        catch (IOException)
        {
            return ProbeObservation<IReadOnlyList<FileSystemEntryObservation>>.Unavailable();
        }
    }

    public ProbeObservation<string> ReadText(string path)
    {
        string normalized = Normalize(path);
        try
        {
            return ProbeObservation<string>.Available(File.ReadAllText(normalized));
        }
        catch (FileNotFoundException)
        {
            return ProbeObservation<string>.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeObservation<string>.Missing();
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<string>.AccessDenied();
        }
        catch (IOException exception)
        {
            return FromStatus<string>(ClassifyIOException(exception));
        }
    }

    public ProbeObservation<string> GetProductVersion(string executablePath)
    {
        string normalized = Normalize(executablePath);
        try
        {
            string? productVersion = FileVersionInfo.GetVersionInfo(normalized).ProductVersion;
            return string.IsNullOrWhiteSpace(productVersion)
                ? ProbeObservation<string>.Unavailable()
                : ProbeObservation<string>.Available(productVersion);
        }
        catch (FileNotFoundException)
        {
            return ProbeObservation<string>.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeObservation<string>.Missing();
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<string>.AccessDenied();
        }
        catch (Win32Exception exception)
        {
            return FromWin32Error<string>(exception.NativeErrorCode);
        }
        catch (IOException exception)
        {
            return FromStatus<string>(ClassifyIOException(exception));
        }
    }

    public ProbeObservation<FileAttributes> GetAttributes(string path)
    {
        string normalized = Normalize(path);
        try
        {
            return ProbeObservation<FileAttributes>.Available(File.GetAttributes(normalized));
        }
        catch (FileNotFoundException)
        {
            return ProbeObservation<FileAttributes>.Missing();
        }
        catch (DirectoryNotFoundException)
        {
            return ProbeObservation<FileAttributes>.Missing();
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<FileAttributes>.AccessDenied();
        }
        catch (IOException)
        {
            return ProbeObservation<FileAttributes>.Unavailable();
        }
    }

    public ProbeObservation<string> GetFinalPath(string path)
    {
        string normalized = Normalize(path);
        using SafeFileHandle handle = _finalPathApi.Open(normalized);
        if (handle.IsInvalid)
        {
            return FromWin32Error<string>(_finalPathApi.GetLastError());
        }

        int capacity = InitialFinalPathCapacity;
        while (capacity <= MaximumFinalPathCapacity)
        {
            char[] buffer = new char[capacity];
            uint length = _finalPathApi.GetFinalPath(handle, buffer);
            if (length == 0)
            {
                return FromWin32Error<string>(_finalPathApi.GetLastError());
            }

            if (length < buffer.Length)
            {
                string finalPath = RemoveNamespacePrefix(new string(buffer, 0, (int)length));
                return ProbeObservation<string>.Available(Normalize(finalPath));
            }

            if (length > MaximumFinalPathCapacity)
            {
                return ProbeObservation<string>.Unavailable();
            }

            capacity = Math.Max(checked(capacity + 1), checked((int)length));
        }

        return ProbeObservation<string>.Unavailable();
    }

    public ProbeObservation<bool> IsWritable(string path)
    {
        string normalized = Normalize(path);
        ProbeObservation<FileAttributes> attributes = GetAttributes(normalized);
        if (attributes.Status != ProbeStatus.Available)
        {
            return FromStatus<bool>(attributes.Status);
        }

        if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
        {
            return ProbeObservation<bool>.ReparsePoint();
        }

        try
        {
            if (attributes.Value.HasFlag(FileAttributes.Directory))
            {
                ProbeDirectoryWrite(normalized);
            }
            else
            {
                using FileStream stream = new(normalized, FileMode.Open, FileAccess.Write, FileShare.Read);
            }

            return ProbeObservation<bool>.Available(true);
        }
        catch (UnauthorizedAccessException)
        {
            return ProbeObservation<bool>.AccessDenied();
        }
        catch (WriteProbeCleanupException)
        {
            return ProbeObservation<bool>.CleanupFailed();
        }
        catch (IOException exception)
        {
            return FromStatus<bool>(ClassifyIOException(exception));
        }
    }

    internal static ProbeStatus ClassifyIOException(IOException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        int win32Error = exception.HResult & 0xFFFF;
        return win32Error is ErrorSharingViolation or ErrorLockViolation
            ? ProbeStatus.Locked
            : ProbeStatus.Unavailable;
    }

    private static FileSystemEntryObservation ObserveEntry(string path)
    {
        string normalized = Normalize(path);
        try
        {
            FileAttributes attributes = File.GetAttributes(normalized);
            ProbeStatus status = attributes.HasFlag(FileAttributes.ReparsePoint)
                ? ProbeStatus.ReparsePoint
                : ProbeStatus.Available;
            return new FileSystemEntryObservation(normalized, GetKind(attributes), status, attributes);
        }
        catch (UnauthorizedAccessException)
        {
            return new FileSystemEntryObservation(normalized, FileSystemEntryKind.Other, ProbeStatus.AccessDenied, null);
        }
        catch (FileNotFoundException)
        {
            return new FileSystemEntryObservation(normalized, FileSystemEntryKind.Other, ProbeStatus.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new FileSystemEntryObservation(normalized, FileSystemEntryKind.Other, ProbeStatus.Missing, null);
        }
        catch (IOException)
        {
            return new FileSystemEntryObservation(normalized, FileSystemEntryKind.Other, ProbeStatus.Unavailable, null);
        }
    }

    private static FileSystemEntryKind GetKind(FileAttributes attributes)
        => attributes.HasFlag(FileAttributes.Directory)
            ? FileSystemEntryKind.Directory
            : FileSystemEntryKind.File;

    private void ProbeDirectoryWrite(string directoryPath)
    {
        string probePath = Path.Combine(directoryPath, $".chromeblood-write-{Guid.NewGuid():N}.tmp");
        Exception? primaryFailure = null;
        try
        {
            _writeProbe.WriteAndFlush(probePath);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        try
        {
            _writeProbe.Delete(probePath);
        }
        catch (FileNotFoundException)
        {
        }
        catch (Exception exception)
        {
            throw new WriteProbeCleanupException(exception);
        }

        if (primaryFailure is not null)
        {
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }
    }

    private static ProbeObservation<T> FromStatus<T>(ProbeStatus status)
        => status switch
        {
            ProbeStatus.Missing => ProbeObservation<T>.Missing(),
            ProbeStatus.AccessDenied => ProbeObservation<T>.AccessDenied(),
            ProbeStatus.Locked => ProbeObservation<T>.Locked(),
            ProbeStatus.ReparsePoint => ProbeObservation<T>.ReparsePoint(),
            ProbeStatus.CleanupFailed => ProbeObservation<T>.CleanupFailed(),
            _ => ProbeObservation<T>.Unavailable()
        };

    private static ProbeObservation<T> FromWin32Error<T>(int error)
        => error switch
        {
            ErrorFileNotFound or ErrorPathNotFound => ProbeObservation<T>.Missing(),
            ErrorAccessDenied => ProbeObservation<T>.AccessDenied(),
            ErrorSharingViolation or ErrorLockViolation => ProbeObservation<T>.Locked(),
            _ => ProbeObservation<T>.Unavailable()
        };

    private static string RemoveNamespacePrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string drivePrefix = @"\\?\";
        if (path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[uncPrefix.Length..];
        }

        return path.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase)
            ? path[drivePrefix.Length..]
            : path;
    }

    private static string Normalize(string path) => ContractWindowsPath.NormalizeAbsolute(path);

    private sealed class WindowsFinalPathApi : IWindowsFinalPathApi
    {
        public SafeFileHandle Open(string path)
            => CreateFile(
                path,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics,
                0);

        public uint GetFinalPath(SafeFileHandle file, char[] buffer)
            => GetFinalPathNameByHandle(file, buffer, (uint)buffer.Length, 0);

        public int GetLastError() => Marshal.GetLastPInvokeError();

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
        [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            [Out] char[] filePath,
            uint filePathLength,
            uint flags);
    }

    private sealed class WindowsWriteProbe : IWindowsWriteProbe
    {
        public void WriteAndFlush(string path)
        {
            using FileStream stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            stream.WriteByte(0);
            stream.Flush(true);
        }

        public void Delete(string path) => File.Delete(path);
    }

    private sealed class WriteProbeCleanupException(Exception innerException)
        : IOException("The temporary write probe could not be removed.", innerException);
}
