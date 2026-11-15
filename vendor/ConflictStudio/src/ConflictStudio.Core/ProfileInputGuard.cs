using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Diagnostics;

namespace ConflictStudio.Core;

public sealed record ProfileInputSnapshot(string Path, string Sha256, byte[] Content);
public enum FingerprintSource { None, Fresh, MemoryCache, PersistentCache }
public sealed record ProfileFileSnapshot(string Path, long Length, DateTime LastWriteTimeUtc, string? Sha256, FingerprintSource FingerprintSource = FingerprintSource.None);
public sealed record ProfileFileValidationProgress(int Completed, int Total, string CurrentItem, long BytesCompleted, long BytesTotal);

public static class ProfileInputGuard
{
    public static ProfileInputSnapshot Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] content = File.ReadAllBytes(path);
        return new ProfileInputSnapshot(Path.GetFullPath(path), Convert.ToHexStringLower(SHA256.HashData(content)), content);
    }

    public static void RequireUnchanged(ProfileInputSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ProfileInputSnapshot current = Capture(snapshot.Path);
        if (!string.Equals(current.Sha256, snapshot.Sha256, StringComparison.Ordinal)) throw new ProfileInputChangedException("The active profile changed during the scan. Run the scan again to produce one consistent result.");
    }

    public static void RequireStillAbsent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (File.Exists(path)) throw new ProfileInputChangedException("An archive order source appeared during the scan. Run the scan again to produce one consistent result.");
    }

    public static ProfileFileSnapshot CaptureFile(string path, bool hashContent)
    {
        FileInfo info = new(Path.GetFullPath(path));
        if (!info.Exists) throw new ProfileInputChangedException("An active evidence file disappeared during the scan.");
        if (!hashContent) return new ProfileFileSnapshot(info.FullName, info.Length, info.LastWriteTimeUtc, null);
        FileSha256Result fingerprint = FileSha256.Fingerprint(info.FullName);
        return new ProfileFileSnapshot(info.FullName, fingerprint.Length, fingerprint.LastWriteTimeUtc, fingerprint.Sha256, FingerprintSource.Fresh);
    }

    public static ProfileFileSnapshot CaptureFile(string path, string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        FileInfo info = new(Path.GetFullPath(path));
        if (!info.Exists) throw new ProfileInputChangedException("An active evidence file disappeared during the scan.");
        return CaptureFile(path, expectedSha256, FingerprintSource.PersistentCache);
    }

    public static ProfileFileSnapshot CaptureFile(string path, string expectedSha256, FingerprintSource fingerprintSource)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        FileInfo info = new(Path.GetFullPath(path));
        if (!info.Exists) throw new ProfileInputChangedException("An active evidence file disappeared during the scan.");
        return new ProfileFileSnapshot(info.FullName, info.Length, info.LastWriteTimeUtc, expectedSha256, fingerprintSource);
    }

    public static void RequireUnchanged(ProfileFileSnapshot snapshot, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        FileInfo current = new(snapshot.Path);
        if (!current.Exists) throw new ProfileInputChangedException($"An active file disappeared during the scan: {snapshot.Path}");
        if (snapshot.Sha256 is null)
        {
            bool metadataChanged = current.Length != snapshot.Length || current.LastWriteTimeUtc != snapshot.LastWriteTimeUtc;
            if (metadataChanged) throw new ProfileInputChangedException($"A file's size or last-modified time changed during the scan: {snapshot.Path}. Start length={snapshot.Length}, last write UTC={snapshot.LastWriteTimeUtc:O}; final length={current.Length}, last write UTC={current.LastWriteTimeUtc:O}.");
            return;
        }
        FileSha256Result fingerprint = FileSha256.Fingerprint(snapshot.Path, progress, cancellationToken);
        bool contentMetadataChanged = fingerprint.Length != snapshot.Length || fingerprint.LastWriteTimeUtc != snapshot.LastWriteTimeUtc;
        if (!string.Equals(fingerprint.Sha256, snapshot.Sha256, StringComparison.Ordinal))
        {
            string evidence = contentMetadataChanged
                ? $"{snapshot.Path}. Expected SHA-256={snapshot.Sha256}; actual SHA-256={fingerprint.Sha256}. Start length={snapshot.Length}, last write UTC={snapshot.LastWriteTimeUtc:O}; final length={fingerprint.Length}, last write UTC={fingerprint.LastWriteTimeUtc:O}."
                : $"{snapshot.Path}. Expected SHA-256={snapshot.Sha256}; actual SHA-256={fingerprint.Sha256}; length={fingerprint.Length}; last write UTC={fingerprint.LastWriteTimeUtc:O}.";
            if (snapshot.FingerprintSource is FingerprintSource.MemoryCache or FingerprintSource.PersistentCache) throw new CachedFingerprintMismatchException($"The cached fingerprint no longer matches the active file: {evidence}", snapshot.Path, snapshot.FingerprintSource);
            throw new ProfileInputChangedException($"An active file no longer matches the fresh fingerprint captured at scan start: {evidence}");
        }
    }

    public static void RequireAllUnchanged(IReadOnlyList<ProfileFileSnapshot> snapshots, CancellationToken cancellationToken, Action<ProfileFileValidationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        Exception? failure = null;
        object progressLock = new();
        Stopwatch elapsed = Stopwatch.StartNew();
        int completed = 0;
        long bytesCompleted = 0;
        long bytesTotal = snapshots.Where(value => value.Sha256 is not null).Sum(value => value.Length);
        long lastReportedBytes = 0;
        long lastReportedMilliseconds = 0;
        Parallel.ForEach(snapshots, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 }, snapshot =>
        {
            if (Volatile.Read(ref failure) is not null) return;
            long fileBytesCompleted = 0;
            try
            {
                RequireUnchanged(snapshot, value =>
                {
                    long delta = value - fileBytesCompleted;
                    fileBytesCompleted = value;
                    lock (progressLock)
                    {
                        bytesCompleted += delta;
                        long milliseconds = elapsed.ElapsedMilliseconds;
                        if (bytesCompleted - lastReportedBytes < 64L * 1024 * 1024 && milliseconds - lastReportedMilliseconds < 250) return;
                        lastReportedBytes = bytesCompleted;
                        lastReportedMilliseconds = milliseconds;
                        progress?.Invoke(new ProfileFileValidationProgress(completed, snapshots.Count, Path.GetFileName(snapshot.Path), bytesCompleted, bytesTotal));
                    }
                }, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProfileInputChangedException) { Interlocked.CompareExchange(ref failure, exception, null); }
            lock (progressLock)
            {
                completed++;
                lastReportedBytes = bytesCompleted;
                lastReportedMilliseconds = elapsed.ElapsedMilliseconds;
                progress?.Invoke(new ProfileFileValidationProgress(completed, snapshots.Count, Path.GetFileName(snapshot.Path), bytesCompleted, bytesTotal));
            }
        });
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

public class ProfileInputChangedException(string message) : Exception(message);
public sealed class CachedFingerprintMismatchException(string message, string path, FingerprintSource fingerprintSource) : ProfileInputChangedException(message)
{
    public string Path { get; } = path;
    public FingerprintSource FingerprintSource { get; } = fingerprintSource;
}
