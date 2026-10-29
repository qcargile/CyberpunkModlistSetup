using System.Buffers;
using System.Security.Cryptography;

namespace ConflictStudio.Core;

public sealed record FileSha256Result(long Length, DateTime LastWriteTimeUtc, string Sha256);

public sealed record FileContentSha256Result(byte[] Content, long Length, DateTime LastWriteTimeUtc, string Sha256);

public static class FileSha256
{
    private const int BufferSize = 1024 * 1024;

    public static string Hash(string path, Action<long>? progress = null, CancellationToken cancellationToken = default)
        => Fingerprint(path, progress, cancellationToken).Sha256;

    public static FileSha256Result Fingerprint(string path, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        long length = stream.Length;
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        long completed = 0;
        try
        {
            for (; ; )
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, BufferSize);
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                completed += read;
                progress?.Invoke(completed);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FileSha256Result(length, lastWriteTimeUtc, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static FileContentSha256Result ReadAll(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        long length = stream.Length;
        if (length > int.MaxValue) throw new IOException("The source file is too large to analyze as text.");
        DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        using MemoryStream content = new(checked((int)length));
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            for (; ; )
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, BufferSize);
                if (read == 0) break;
                content.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new FileContentSha256Result(content.ToArray(), length, lastWriteTimeUtc, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
