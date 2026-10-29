using System.Buffers;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Acquisition;

[Flags]
internal enum SecureFileAccess
{
    Read = 1,
    Write = 2,
    Delete = 4,
    Attributes = 8,
    ReadControl = 16,
    WriteDac = 32,
    WriteOwner = 64,
    WriteAttributes = 128
}

internal enum SecureFileDisposition
{
    CreateNew,
    OpenExisting,
    OpenAlways
}

[Flags]
internal enum SecureFileOpenOptions
{
    None = 0,
    OpenReparsePoint = 1,
    Directory = 2,
    Asynchronous = 4,
    WriteThrough = 8,
    Sequential = 16,
    BackupSemantics = 32
}

internal sealed record SecureFileOpenRequest(
    string Path,
    SecureFileAccess Access,
    FileShare Share,
    SecureFileDisposition Disposition,
    SecureFileOpenOptions Options);

internal sealed record SecureFileMetadata(bool IsDirectory, bool IsReparsePoint, long Length);

internal interface ISecureFileApi
{
    SafeFileHandle? Open(SecureFileOpenRequest request);

    void CreateDirectory(string path);

    SecureFileMetadata GetMetadata(SafeFileHandle handle);

    string GetFinalPath(SafeFileHandle handle);

    void Delete(SafeFileHandle handle);

    void Rename(SafeFileHandle handle, string destination, bool replace);
}

internal sealed class SecureFileOpenException(string message, int nativeError, Exception innerException)
    : IOException(message, innerException)
{
    public int NativeError { get; } = nativeError;
}

internal sealed class SecureFileLease(SafeFileHandle handle, string finalPath, SecureFileMetadata metadata, ISecureFileApi api) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;
    public string FinalPath { get; } = finalPath;
    public long Length => metadata.Length;

    public void Delete() => api.Delete(Handle);

    public void Rename(string destination, bool replace = false) => api.Rename(Handle, destination, replace);

    public void Dispose() => Handle.Dispose();
}

internal sealed class SecureCacheRoot : IDisposable
{
    private readonly ISecureFileApi _api;
    private readonly List<SafeFileHandle> _directoryHandles = [];
    private readonly string _physicalRoot;
    private readonly bool _createMissing;
    private bool _disposed;

    public SecureCacheRoot(string root)
        : this(root, new WindowsSecureFileApi(), true)
    {
    }

    internal SecureCacheRoot(string root, ISecureFileApi api)
        : this(root, api, true)
    {
    }

    internal SecureCacheRoot(string root, ISecureFileApi api, bool createMissing)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(api);
        Path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        if (!System.IO.Path.IsPathFullyQualified(Path))
        {
            throw new ArgumentException("The package cache root must be fully qualified.", nameof(root));
        }

        _api = api;
        _createMissing = createMissing;
        try
        {
            _physicalRoot = GuardRootChain(Path);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public string Path { get; }

    public SafeFileHandle OpenLock(string name)
        => OpenRequired(name, new SecureFileOpenRequest(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.Write,
            FileShare.None,
            SecureFileDisposition.OpenAlways,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.WriteThrough));

    public SafeFileHandle OpenPrivilegedLock(string name)
        => OpenRequired(name, new SecureFileOpenRequest(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.Write | SecureFileAccess.ReadControl | SecureFileAccess.WriteDac | SecureFileAccess.WriteOwner,
            FileShare.None,
            SecureFileDisposition.OpenAlways,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.WriteThrough));

    public SafeFileHandle CreatePartial(string name)
        => OpenRequired(name, new SecureFileOpenRequest(
            EntryPath(name),
            SecureFileAccess.Write,
            FileShare.None,
            SecureFileDisposition.CreateNew,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Asynchronous | SecureFileOpenOptions.WriteThrough));

    public SafeFileHandle CreateAtomicPartial(string name)
        => OpenRequired(name, new SecureFileOpenRequest(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.Write | SecureFileAccess.Delete,
            FileShare.None,
            SecureFileDisposition.CreateNew,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.WriteThrough));

    public SafeFileHandle CreatePrivilegedPartial(string name)
        => OpenRequired(name, new SecureFileOpenRequest(
            EntryPath(name),
            SecureFileAccess.Write | SecureFileAccess.Delete | SecureFileAccess.ReadControl | SecureFileAccess.WriteDac | SecureFileAccess.WriteOwner,
            FileShare.None,
            SecureFileDisposition.CreateNew,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Asynchronous | SecureFileOpenOptions.WriteThrough));

    public SecureFileLease? TryOpenReadDelete(string name)
    {
        ThrowIfDisposed();
        SecureFileOpenRequest request = new(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.Delete,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential);
        SafeFileHandle? handle = _api.Open(request);
        if (handle is null)
        {
            return null;
        }

        try
        {
            (SecureFileMetadata metadata, string finalPath) = ValidatePhysicalEntry(handle, name);
            if (metadata.IsDirectory)
            {
                throw new PackageAcquisitionException("The package cache entry is a directory.");
            }

            if (metadata.IsReparsePoint)
            {
                _api.Delete(handle);
                handle.Dispose();
                return null;
            }

            return new SecureFileLease(handle, finalPath, metadata, _api);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public SecureFileLease? TryOpenPartialForResume(string name)
    {
        ThrowIfDisposed();
        SecureFileOpenRequest request = new(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.Write | SecureFileAccess.Delete,
            FileShare.None,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Asynchronous | SecureFileOpenOptions.WriteThrough);
        SafeFileHandle? handle = _api.Open(request);
        if (handle is null) return null;
        try
        {
            (SecureFileMetadata metadata, string finalPath) = ValidatePhysicalEntry(handle, name);
            if (metadata.IsDirectory || metadata.IsReparsePoint)
            {
                _api.Delete(handle);
                handle.Dispose();
                return null;
            }
            return new SecureFileLease(handle, finalPath, metadata, _api);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public SecureFileLease? TryOpenRead(string name)
    {
        ThrowIfDisposed();
        SecureFileOpenRequest request = new(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.ReadControl,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential);
        SafeFileHandle? handle = _api.Open(request);
        if (handle is null)
        {
            return null;
        }

        try
        {
            (SecureFileMetadata metadata, string finalPath) = ValidatePhysicalEntry(handle, name);
            if (metadata.IsDirectory || metadata.IsReparsePoint)
            {
                throw new PackageAcquisitionException("The secure file entry is a directory or reparse point.");
            }

            return new SecureFileLease(handle, finalPath, metadata, _api);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public SecureFileLease? TryOpenReadReplaceable(string name)
    {
        ThrowIfDisposed();
        SecureFileOpenRequest request = new(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.ReadControl,
            FileShare.Read | FileShare.Delete,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential);
        SafeFileHandle? handle = _api.Open(request);
        if (handle is null)
        {
            return null;
        }

        try
        {
            (SecureFileMetadata metadata, string finalPath) = ValidatePhysicalEntry(handle, name);
            if (metadata.IsDirectory || metadata.IsReparsePoint)
            {
                throw new PackageAcquisitionException("The replaceable secure file entry is a directory or reparse point.");
            }

            return new SecureFileLease(handle, finalPath, metadata, _api);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public SecureFileLease? TryOpenPrivilegedClaim(string name)
    {
        ThrowIfDisposed();
        SecureFileOpenRequest request = new(
            EntryPath(name),
            SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.ReadControl | SecureFileAccess.WriteDac | SecureFileAccess.WriteOwner,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential);
        SafeFileHandle? handle = _api.Open(request);
        if (handle is null)
        {
            return null;
        }

        try
        {
            (SecureFileMetadata metadata, string finalPath) = ValidatePhysicalEntry(handle, name);
            if (metadata.IsDirectory || metadata.IsReparsePoint)
            {
                throw new PackageAcquisitionException("The privileged claim is a directory or reparse point.");
            }

            return new SecureFileLease(handle, finalPath, metadata, _api);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void DeleteIfPresent(string name)
    {
        using SecureFileLease? lease = TryOpenReadDelete(name);
        lease?.Delete();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int index = _directoryHandles.Count - 1; index >= 0; index--)
        {
            _directoryHandles[index].Dispose();
        }

        _directoryHandles.Clear();
    }

    internal void RevalidateRoot()
    {
        ThrowIfDisposed();
        SafeFileHandle handle = _directoryHandles[^1];
        SecureFileMetadata metadata = _api.GetMetadata(handle);
        string physical = NormalizeFinalPath(_api.GetFinalPath(handle));
        if (!metadata.IsDirectory || metadata.IsReparsePoint || !string.Equals(physical, _physicalRoot, StringComparison.OrdinalIgnoreCase))
            throw new PackageAcquisitionException("The held secure root identity or final path changed.");
    }

    private string GuardRootChain(string root)
    {
        string pathRoot = System.IO.Path.GetPathRoot(root)
            ?? throw new ArgumentException("The package cache root has no filesystem root.", nameof(root));
        string relative = System.IO.Path.GetRelativePath(pathRoot, root);
        List<string> paths = [System.IO.Path.TrimEndingDirectorySeparator(pathRoot)];
        if (relative != ".")
        {
            string current = pathRoot;
            foreach (string component in relative.Split(System.IO.Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                if (component is "." or "..")
                {
                    throw new PackageAcquisitionException("The package cache root contains an invalid component.");
                }

                current = System.IO.Path.Combine(current, component);
                paths.Add(current);
            }
        }

        string finalRoot = string.Empty;
        foreach (string current in paths)
        {
            SecureFileOpenRequest request = new(
                current,
                SecureFileAccess.Attributes,
                FileShare.Read,
                SecureFileDisposition.OpenExisting,
                SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Directory);
            SafeFileHandle? handle = _api.Open(request);
            if (handle is null)
            {
                if (!_createMissing)
                {
                    throw new PackageAcquisitionException("The secure root does not exist.");
                }

                try { _api.CreateDirectory(current); }
                catch (Win32Exception exception) when (exception.NativeErrorCode == 183) { }
                handle = _api.Open(request)
                    ?? throw new PackageAcquisitionException("The package cache directory could not be opened after creation.");
            }

            try
            {
                SecureFileMetadata metadata = _api.GetMetadata(handle);
                string physical = NormalizeFinalPath(_api.GetFinalPath(handle));
                if (!metadata.IsDirectory || metadata.IsReparsePoint)
                {
                    throw new PackageAcquisitionException("The package cache root chain contains a reparse point or non-directory.");
                }

                if (!string.Equals(physical, NormalizeFinalPath(current), StringComparison.OrdinalIgnoreCase))
                {
                    throw new PackageAcquisitionException($"The package cache root '{current}' resolves to '{physical}' instead of its direct physical path.");
                }

                _directoryHandles.Add(handle);
                finalRoot = physical;
                handle = null;
            }
            finally
            {
                handle?.Dispose();
            }
        }

        return finalRoot;
    }

    private SafeFileHandle OpenRequired(string name, SecureFileOpenRequest request)
    {
        ThrowIfDisposed();
        SafeFileHandle handle = _api.Open(request)
            ?? throw new PackageAcquisitionException("The package cache entry could not be opened.");
        try
        {
            (SecureFileMetadata metadata, _) = ValidatePhysicalEntry(handle, name);
            if (metadata.IsDirectory || metadata.IsReparsePoint)
            {
                throw new PackageAcquisitionException("The package cache entry is a directory or reparse point.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private (SecureFileMetadata Metadata, string FinalPath) ValidatePhysicalEntry(SafeFileHandle handle, string name)
    {
        SecureFileMetadata metadata = _api.GetMetadata(handle);
        string finalPath = NormalizeFinalPath(_api.GetFinalPath(handle));
        if (!string.Equals(System.IO.Path.GetDirectoryName(finalPath), _physicalRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(System.IO.Path.GetFileName(finalPath), name, StringComparison.OrdinalIgnoreCase))
        {
            throw new PackageAcquisitionException("The package cache entry resolves outside the held cache root.");
        }

        return (metadata, finalPath);
    }

    private string EntryPath(string name)
    {
        ThrowIfDisposed();
        if (string.IsNullOrWhiteSpace(name)
            || !string.Equals(System.IO.Path.GetFileName(name), name, StringComparison.Ordinal)
            || name.IndexOfAny([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new PackageAcquisitionException("The package cache entry name is invalid.");
        }

        return System.IO.Path.Combine(Path, name);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal static string NormalizeFinalPath(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string drivePrefix = @"\\?\";
        string normalized = path.StartsWith(uncPrefix, StringComparison.OrdinalIgnoreCase)
            ? @"\\" + path[uncPrefix.Length..]
            : path.StartsWith(drivePrefix, StringComparison.OrdinalIgnoreCase)
                ? path[drivePrefix.Length..]
                : path;
        return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(normalized));
    }
}

internal sealed record FileHashResult(long Length, string Sha256);

internal interface IFileHandleHasher
{
    Task<FileHashResult> HashAsync(SafeFileHandle handle, long maximumBytes, CancellationToken cancellationToken);
}

internal sealed class FileHandleHasher : IFileHandleHasher
{
    private const int BufferSize = 81920;

    public async Task<FileHashResult> HashAsync(SafeFileHandle handle, long maximumBytes, CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(handle);
        if (length > maximumBytes)
        {
            throw new PackageAcquisitionException("The cached package exceeds the allowed size.");
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(BufferSize, length - offset)), offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new PackageAcquisitionException("The cached package changed length while hashing.");
                }

                hash.AppendData(buffer, 0, read);
                offset += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new FileHashResult(length, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }
}

internal sealed class WindowsSecureFileApi : ISecureFileApi
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const uint WriteAttributes = 0x00000100;
    private const uint ReadControl = 0x00020000;
    private const uint WriteDac = 0x00040000;
    private const uint WriteOwner = 0x00080000;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint AttributeNormal = 0x00000080;
    private const uint FlagBackupSemantics = 0x02000000;
    internal const uint FlagOpenReparsePoint = 0x00200000;
    internal const uint FlagOverlapped = 0x40000000;
    internal const uint FlagWriteThrough = 0x80000000;
    private const uint FlagSequentialScan = 0x08000000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int InitialFinalPathCapacity = 512;
    private const int MaximumFinalPathCapacity = 32768;

    public SafeFileHandle? Open(SecureFileOpenRequest request)
    {
        uint desiredAccess = 0;
        if (request.Access.HasFlag(SecureFileAccess.Read))
        {
            desiredAccess |= GenericRead;
        }

        if (request.Access.HasFlag(SecureFileAccess.Write))
        {
            desiredAccess |= GenericWrite;
        }

        if (request.Access.HasFlag(SecureFileAccess.Delete))
        {
            desiredAccess |= DeleteAccess;
        }

        if (request.Access.HasFlag(SecureFileAccess.Attributes))
        {
            desiredAccess |= ReadAttributes;
        }

        if (request.Access.HasFlag(SecureFileAccess.WriteAttributes))
        {
            desiredAccess |= WriteAttributes;
        }

        if (request.Access.HasFlag(SecureFileAccess.ReadControl))
        {
            desiredAccess |= ReadControl;
        }

        if (request.Access.HasFlag(SecureFileAccess.WriteDac))
        {
            desiredAccess |= WriteDac;
        }

        if (request.Access.HasFlag(SecureFileAccess.WriteOwner))
        {
            desiredAccess |= WriteOwner;
        }

        uint flags = NativeFlags(request.Options);
        uint disposition = request.Disposition switch
        {
            SecureFileDisposition.CreateNew => CreateNew,
            SecureFileDisposition.OpenAlways => OpenAlways,
            _ => OpenExisting
        };
        SafeFileHandle handle = CreateFile(ExtendedPath(request.Path), desiredAccess, (uint)request.Share, 0, disposition, flags, 0);
        if (!handle.IsInvalid)
        {
            return handle;
        }

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        if (request.Disposition == SecureFileDisposition.OpenExisting && error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return null;
        }

        Win32Exception inner = new(error);
        throw new SecureFileOpenException($"The secure file open failed for '{request.Path}'.", error, inner);
    }

    internal static string ExtendedPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    internal static uint NativeFlags(SecureFileOpenOptions options)
    {
        uint flags = AttributeNormal;
        if (options.HasFlag(SecureFileOpenOptions.Directory) || options.HasFlag(SecureFileOpenOptions.BackupSemantics))
        {
            flags |= FlagBackupSemantics;
        }

        if (options.HasFlag(SecureFileOpenOptions.OpenReparsePoint))
        {
            flags |= FlagOpenReparsePoint;
        }

        if (options.HasFlag(SecureFileOpenOptions.Asynchronous))
        {
            flags |= FlagOverlapped;
        }

        if (options.HasFlag(SecureFileOpenOptions.WriteThrough))
        {
            flags |= FlagWriteThrough;
        }

        if (options.HasFlag(SecureFileOpenOptions.Sequential))
        {
            flags |= FlagSequentialScan;
        }

        return flags;
    }

    public void CreateDirectory(string path)
    {
        if (!CreateDirectoryNative(ExtendedPath(path), 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    public static void CreateDirectory(string path, DirectorySecurity security)
    {
        ArgumentNullException.ThrowIfNull(security);
        byte[] descriptor = security.GetSecurityDescriptorBinaryForm();
        GCHandle pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            SecurityAttributes attributes = new()
            {
                Length = checked((uint)Marshal.SizeOf<SecurityAttributes>()),
                SecurityDescriptor = pinned.AddrOfPinnedObject(),
                InheritHandle = false
            };
            if (!CreateDirectorySecure(ExtendedPath(path), ref attributes))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            pinned.Free();
        }
    }

    public SecureFileMetadata GetMetadata(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        FileAttributes attributes = (FileAttributes)information.FileAttributes;
        long length = ((long)information.FileSizeHigh << 32) | information.FileSizeLow;
        return new SecureFileMetadata(
            attributes.HasFlag(FileAttributes.Directory),
            attributes.HasFlag(FileAttributes.ReparsePoint),
            length);
    }

    public string GetFinalPath(SafeFileHandle handle)
    {
        int capacity = InitialFinalPathCapacity;
        while (capacity <= MaximumFinalPathCapacity)
        {
            char[] buffer = new char[capacity];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, checked((int)length));
            }

            if (length > MaximumFinalPathCapacity)
            {
                break;
            }

            capacity = Math.Max(capacity + 1, checked((int)length));
        }

        throw new IOException("The secure file final path exceeds the supported length.");
    }

    public void Delete(SafeFileHandle handle)
    {
        FileDispositionInformation disposition = new() { DeleteFile = true };
        if (!SetFileInformationByHandle(handle, 4, ref disposition, (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    public void Rename(SafeFileHandle handle, string destination, bool replace)
    {
        byte[] name = System.Text.Encoding.Unicode.GetBytes(ExtendedPath(destination));
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        int bufferLength = nameOffset + name.Length + sizeof(char);
        nint buffer = Marshal.AllocHGlobal(bufferLength);
        try
        {
            for (int index = 0; index < bufferLength; index++)
            {
                Marshal.WriteByte(buffer, index, 0);
            }

            Marshal.WriteByte(buffer, 0, replace ? (byte)1 : (byte)0);
            Marshal.WriteIntPtr(buffer, rootOffset, 0);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)bufferLength))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FileTime CreationTime;
        public FileTime LastAccessTime;
        public FileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryNative(string path, nint securityAttributes);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectorySecure(string path,
        ref SecurityAttributes securityAttributes);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] filePath, uint filePathLength, uint flags);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, ref FileDispositionInformation information, uint bufferSize);

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, nint information, uint bufferSize);
}
