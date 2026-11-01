using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Globalization;

using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Acquisition;

public sealed record PackageCacheEntry(
    string CacheId,
    string Path,
    string Sha256,
    Uri Source,
    AuthenticodeVerdict Authenticode,
    bool FromCache);

public sealed class PackageCache : IDisposable
{
    private const int LockRetryMilliseconds = 25;
    private const int LockWaitMilliseconds = 10000;
    private readonly SecureCacheRoot _root;
    private readonly PackageDownloader _downloader;
    private readonly IAuthenticodeVerifier _authenticode;
    private readonly IFileHandleHasher _hasher;
    private readonly TransferRecoveryPolicy _recovery;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PackageCache(string root, PackageDownloader downloader, IAuthenticodeVerifier authenticode)
        : this(new SecureCacheRoot(root), downloader, authenticode, new FileHandleHasher(),
            TransferRecoveryPolicy.Default, Task.Delay)
    {
    }

    internal PackageCache(SecureCacheRoot root, PackageDownloader downloader, IAuthenticodeVerifier authenticode, IFileHandleHasher hasher)
        : this(root, downloader, authenticode, hasher, TransferRecoveryPolicy.Default, Task.Delay)
    {
    }

    internal PackageCache(SecureCacheRoot root, PackageDownloader downloader, IAuthenticodeVerifier authenticode,
        IFileHandleHasher hasher, TransferRecoveryPolicy recovery,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(authenticode);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(recovery);
        ArgumentNullException.ThrowIfNull(delay);
        _root = root;
        _downloader = downloader;
        _authenticode = authenticode;
        _hasher = hasher;
        _recovery = recovery;
        _delay = delay;
    }

    public async Task<PackageCacheEntry> AcquireAsync(DownloadRequest request, CancellationToken cancellationToken)
        => await AcquireAsync(request, null, null, cancellationToken).ConfigureAwait(false);

    public async Task<PackageCacheEntry?> TryGetAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        string cacheId = GetCacheId(request);
        using SafeFileHandle cacheLock = await AcquireLockAsync(cacheId + ".lock", cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await ValidateEntryAsync(request, cacheId, cacheId + ".package", true, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PackageCacheEntry?> TryInspectAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        string cacheId = GetCacheId(request);
        try
        {
            return await ValidateEntryAsync(request, cacheId, cacheId + ".package", true, null, cancellationToken,
                false).ConfigureAwait(false);
        }
        catch (PackageAcquisitionException)
        {
            return null;
        }
    }

    public async Task<PackageCacheEntry> AcquireAsync(
        DownloadRequest request,
        Func<CancellationToken, Task<Uri>>? resolveDownloadSource,
        CancellationToken cancellationToken)
        => await AcquireAsync(request, resolveDownloadSource, null, cancellationToken).ConfigureAwait(false);

    public async Task<PackageCacheEntry> AcquireAsync(
        DownloadRequest request,
        Func<CancellationToken, Task<Uri>>? resolveDownloadSource,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string cacheId = GetCacheId(request);
        string packageName = cacheId + ".package";
        string partialName = cacheId + ".partial";
        string checkpointName = cacheId + ".resume";
        string lockName = cacheId + ".lock";
        using SafeFileHandle cacheLock = await AcquireLockAsync(lockName, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        PackageCacheEntry? cached = await ValidateEntryAsync(request, cacheId, packageName, true, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (cached is not null)
        {
            _root.DeleteIfPresent(partialName);
            _root.DeleteIfPresent(checkpointName);
            return cached;
        }

        bool transferComplete = false;
        try
        {
            string partialPath = Path.Combine(_root.Path, partialName);
            DownloadedPackage downloaded = await DownloadWithRecoveryAsync(request, resolveDownloadSource,
                partialPath, partialName, checkpointName, progress, cancellationToken).ConfigureAwait(false);
            transferComplete = true;
            cancellationToken.ThrowIfCancellationRequested();
            Uri reportedSource = resolveDownloadSource is null ? downloaded.FinalSource : request.Source;
            PackageCacheEntry? staged = await ValidateEntryAsync(request, cacheId, partialName, false, reportedSource, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (staged is null)
            {
                throw new PackageAcquisitionException("The downloaded package did not satisfy its trust policy.");
            }

            try
            {
                File.Move(partialPath, Path.Combine(_root.Path, packageName), false);
            }
            catch (IOException exception)
            {
                throw new PackageAcquisitionException("The package cache commit lost an atomic no-overwrite race.", exception);
            }

            PackageCacheEntry? committed = await ValidateEntryAsync(request, cacheId, packageName, false, reportedSource, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (committed is null)
            {
                throw new PackageAcquisitionException("The committed package did not satisfy its trust policy.");
            }

            _root.DeleteIfPresent(checkpointName);
            return committed;
        }
        catch (Exception exception)
        {
            if (transferComplete || !MayResume(partialName, checkpointName, exception))
            {
                _root.DeleteIfPresent(partialName);
                _root.DeleteIfPresent(checkpointName);
            }
            throw;
        }
    }

    private async Task<DownloadedPackage> DownloadWithRecoveryAsync(
        DownloadRequest request,
        Func<CancellationToken, Task<Uri>>? resolveDownloadSource,
        string partialPath,
        string partialName,
        string checkpointName,
        IProgress<PackageDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        bool refreshedRejectedLink = false;
        TimeSpan cumulativeDelay = TimeSpan.Zero;
        for (int attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool transferStarted = false;
            try
            {
                DownloadRequest downloadRequest = resolveDownloadSource is null
                    ? request
                    : request with { Source = await resolveDownloadSource(cancellationToken).ConfigureAwait(false) };
                _ = GetCacheId(downloadRequest);
                PackageDownloadCheckpoint? checkpoint = LoadCheckpoint(checkpointName, partialName, request);
                transferStarted = true;
                return await _downloader.DownloadAsync(
                    downloadRequest,
                    partialPath,
                    () =>
                    {
                        _root.DeleteIfPresent(partialName);
                        _root.DeleteIfPresent(checkpointName);
                        return new FileStream(_root.CreatePartial(partialName), FileAccess.Write, 81920, true);
                    },
                    () =>
                    {
                        SecureFileLease? lease = _root.TryOpenPartialForResume(partialName);
                        return lease is null ? null : new FileStream(lease.Handle, FileAccess.ReadWrite, 81920, true);
                    },
                    checkpoint,
                    value => SaveCheckpoint(checkpointName, value),
                    progress is null ? null : new AttemptProgress(progress, attempt, _recovery.MaximumAttempts),
                    request.Source,
                    false,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ITransferFailure)
            {
                ITransferFailure failure = (ITransferFailure)exception;
                bool refreshRejectedLink = transferStarted && resolveDownloadSource is not null
                    && !refreshedRejectedLink && failure.Failure == TransferFailureKind.AccountRequired;
                bool retry = attempt < _recovery.MaximumAttempts
                    && (failure.Failure is TransferFailureKind.TransientTransport or TransferFailureKind.RateLimited
                        || refreshRejectedLink);
                if (!retry) throw;
                refreshedRejectedLink |= refreshRejectedLink;
                TimeSpan delay = refreshRejectedLink ? TimeSpan.Zero : _recovery.DelayFor(attempt, failure.RetryAfter);
                if (delay > _recovery.MaximumCumulativeRetryDelay - cumulativeDelay) throw;
                cumulativeDelay += delay;
                PackageDownloadCheckpoint? checkpoint = LoadCheckpoint(checkpointName, partialName, request);
                long received = PartialLength(partialName);
                progress?.Report(new PackageDownloadProgress(received, checkpoint?.TotalBytes)
                {
                    State = PackageDownloadProgressState.WaitingToRetry,
                    Attempt = attempt + 1,
                    MaximumAttempts = _recovery.MaximumAttempts,
                    RetryDelay = delay
                });
                if (delay > TimeSpan.Zero) await _delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private long PartialLength(string partialName)
    {
        using SecureFileLease? partial = _root.TryOpenReadDelete(partialName);
        return partial?.Length ?? 0;
    }

    private sealed class AttemptProgress(IProgress<PackageDownloadProgress> progress, int attempt,
        int maximumAttempts) : IProgress<PackageDownloadProgress>
    {
        public void Report(PackageDownloadProgress value)
            => progress.Report(value with { Attempt = attempt, MaximumAttempts = maximumAttempts });
    }

    private PackageDownloadCheckpoint? LoadCheckpoint(string checkpointName, string partialName, DownloadRequest request)
    {
        try
        {
            using SecureFileLease? metadata = _root.TryOpenReadDelete(checkpointName);
            using SecureFileLease? partial = _root.TryOpenReadDelete(partialName);
            if (metadata is null || partial is null || metadata.Length is <= 0 or > 4096
                || partial.Length <= 0 || partial.Length >= request.MaximumBytes) return null;
            byte[] bytes = new byte[checked((int)metadata.Length)];
            if (RandomAccess.Read(metadata.Handle, bytes, 0) != bytes.Length) return null;
            PackageDownloadCheckpoint? checkpoint = JsonSerializer.Deserialize<PackageDownloadCheckpoint>(bytes);
            return checkpoint is { TotalBytes: > 0 }
                && checkpoint.TotalBytes <= request.MaximumBytes
                && partial.Length < checkpoint.TotalBytes
                && checkpoint.RouteHash is { Length: 64 } routeHash
                && routeHash.All(char.IsAsciiHexDigit)
                && HasUsableValidator(checkpoint)
                ? checkpoint : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool HasUsableValidator(PackageDownloadCheckpoint checkpoint)
    {
        if (checkpoint.Validator is not { Length: > 0 and <= 256 }) return false;
        return checkpoint.UsesEntityTag
            ? EntityTagHeaderValue.TryParse(checkpoint.Validator, out EntityTagHeaderValue? tag) && tag is { IsWeak: false }
            : DateTimeOffset.TryParseExact(checkpoint.Validator, "R", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out _);
    }

    private void SaveCheckpoint(string name, PackageDownloadCheckpoint checkpoint)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        if (bytes.Length > 4096) return;
        _root.DeleteIfPresent(name);
        using SafeFileHandle handle = _root.CreatePartial(name);
        RandomAccess.Write(handle, bytes, 0);
        RandomAccess.FlushToDisk(handle);
    }

    private bool MayResume(string partialName, string checkpointName, Exception exception)
    {
        if (exception is PackageIntegrityException) return false;
        using SecureFileLease? metadata = _root.TryOpenReadDelete(checkpointName);
        using SecureFileLease? partial = _root.TryOpenReadDelete(partialName);
        return metadata is not null && partial is { Length: > 0 };
    }

    public async Task<PackageCacheEntry> ImportAsync(
        DownloadRequest request,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        string source = Path.GetFullPath(sourcePath);
        string? parentPath = Path.GetDirectoryName(source);
        if (parentPath is null) throw new PackageAcquisitionException("The selected archive has no parent directory.");
        using SecureCacheRoot sourceRoot = new(parentPath, new WindowsSecureFileApi(), false);
        using SecureFileLease sourceLease = sourceRoot.TryOpenRead(Path.GetFileName(source))
            ?? throw new PackageAcquisitionException("The selected archive is missing.");
        if (!string.Equals(sourceLease.FinalPath, source, StringComparison.OrdinalIgnoreCase))
            throw new PackageAcquisitionException("The selected archive path changed during import.");
        FileHashResult sourceHash = await _hasher.HashAsync(sourceLease.Handle, request.MaximumBytes, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceHash.Length != sourceLease.Length
            || request.ExpectedSha256 is not null && !string.Equals(sourceHash.Sha256, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new PackageAcquisitionException("The selected archive does not match the declared size or SHA-256 digest.");

        string cacheId = GetCacheId(request);
        string packageName = cacheId + ".package";
        string partialName = cacheId + ".partial";
        using SafeFileHandle cacheLock = await AcquireLockAsync(cacheId + ".lock", cancellationToken).ConfigureAwait(false);
        PackageCacheEntry? cached = await ValidateEntryAsync(request, cacheId, packageName, true, null, cancellationToken).ConfigureAwait(false);
        if (cached is not null) return cached;

        _root.DeleteIfPresent(partialName);
        string partialPath = Path.Combine(_root.Path, partialName);
        try
        {
            using (SafeFileHandle partial = _root.CreatePartial(partialName))
            {
                byte[] buffer = new byte[81920];
                long offset = 0;
                while (offset < sourceLease.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = RandomAccess.Read(sourceLease.Handle, buffer, offset);
                    if (read == 0) throw new PackageAcquisitionException("The selected archive changed during import.");
                    RandomAccess.Write(partial, buffer.AsSpan(0, read), offset);
                    offset = checked(offset + read);
                }
                if (RandomAccess.Read(sourceLease.Handle, buffer.AsSpan(0, 1), offset) != 0)
                    throw new PackageAcquisitionException("The selected archive changed during import.");
                RandomAccess.FlushToDisk(partial);
            }

            PackageCacheEntry? staged = await ValidateEntryAsync(request, cacheId, partialName, false, request.Source, cancellationToken).ConfigureAwait(false);
            if (staged is null) throw new PackageAcquisitionException("The selected archive did not satisfy its trust policy.");
            File.Move(partialPath, Path.Combine(_root.Path, packageName), false);
            PackageCacheEntry? result = await ValidateEntryAsync(request, cacheId, packageName, false, request.Source, cancellationToken).ConfigureAwait(false);
            return result ?? throw new PackageAcquisitionException("The imported archive did not satisfy its trust policy.");
        }
        catch
        {
            _root.DeleteIfPresent(partialName);
            throw;
        }
    }

    public static string GetCacheId(DownloadRequest request)
    {
        ValidateRequest(request);
        StringBuilder identity = new();
        AddField(identity, CanonicalSource(request.Source));
        AddField(identity, request.ExpectedSha256?.ToLowerInvariant());
        AddField(identity, request.Architecture.ToLowerInvariant());
        AddField(identity, request.ManifestVersion);
        AddField(identity, ((int)request.AcquisitionKind).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddField(identity, request.RequireTrustedAuthenticode ? "1" : "0");
        AddField(identity, request.ExpectedSigner is null ? null : AuthenticodeVerifier.NormalizeSignerIdentity(request.ExpectedSigner).ToUpperInvariant());
        AddField(identity, request.MaximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }

    public void Dispose() => _root.Dispose();

    private async Task<PackageCacheEntry?> ValidateEntryAsync(
        DownloadRequest request,
        string cacheId,
        string name,
        bool fromCache,
        Uri? resolvedSource,
        CancellationToken cancellationToken,
        bool deleteInvalid = true)
    {
        using SecureFileLease? lease = deleteInvalid ? _root.TryOpenReadDelete(name) : _root.TryOpenRead(name);
        if (lease is null)
        {
            return null;
        }

        FileHashResult hash;
        try
        {
            hash = await _hasher.HashAsync(lease.Handle, request.MaximumBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (PackageAcquisitionException)
        {
            if (deleteInvalid) lease.Delete();
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (hash.Length != lease.Length
            || request.ExpectedSha256 is not null && !string.Equals(hash.Sha256, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (deleteInvalid) lease.Delete();
            return null;
        }

        AuthenticodeVerdict verdict;
        if (request.RequireTrustedAuthenticode || request.ExpectedSigner is not null)
        {
            verdict = _authenticode.Verify(lease.Handle, lease.FinalPath, request.ExpectedSigner, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (verdict.Status != AuthenticodeStatus.Trusted)
            {
                if (deleteInvalid) lease.Delete();
                return null;
            }
        }
        else
        {
            verdict = new AuthenticodeVerdict(AuthenticodeStatus.NotRequired, null, 0);
        }

        return new PackageCacheEntry(cacheId, Path.Combine(_root.Path, name), hash.Sha256, resolvedSource ?? request.Source, verdict, fromCache);
    }

    private async Task<SafeFileHandle> AcquireLockAsync(string name, CancellationToken cancellationToken)
    {
        long started = Environment.TickCount64;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return _root.OpenLock(name);
            }
            catch (SecureFileOpenException exception) when (exception.NativeError is 32 or 33)
            {
                if (Environment.TickCount64 - started >= LockWaitMilliseconds)
                    throw new PackageAcquisitionException("Another process is keeping the package cache busy: " + Path.Combine(_root.Path, name)
                        + ". Close other Setup windows or wait for the active process to finish, then retry. Other preparation can continue.", exception);
                await Task.Delay(LockRetryMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException exception)
            {
                throw new PackageAcquisitionException("The package cache lock could not be acquired.", exception);
            }
        }
    }

    private static void ValidateRequest(DownloadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Source.IsAbsoluteUri
            || !string.Equals(request.Source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(request.Source.Host)
            || !string.IsNullOrEmpty(request.Source.UserInfo))
        {
            throw new ArgumentException("The package source must be an absolute HTTPS URL without user information.", nameof(request));
        }

        if (request.ExpectedSha256 is not null && (request.ExpectedSha256.Length != 64 || !request.ExpectedSha256.All(char.IsAsciiHexDigit)))
        {
            throw new ArgumentException("The package SHA-256 digest is invalid.", nameof(request));
        }

        if (!IsToken(request.Architecture) || !IsVersion(request.ManifestVersion))
        {
            throw new ArgumentException("The package architecture or manifest version is invalid.", nameof(request));
        }

        if (!Enum.IsDefined(request.AcquisitionKind))
        {
            throw new ArgumentException("The package acquisition kind is invalid.", nameof(request));
        }

        if (request.ExpectedSigner is not null && string.IsNullOrWhiteSpace(request.ExpectedSigner))
        {
            throw new ArgumentException("The package signer identity must not be blank.", nameof(request));
        }

        if (request.MaximumBytes <= 0)
        {
            throw new ArgumentException("The maximum package size must be positive.", nameof(request));
        }

        if (request.ExpectedSha256 is null && !request.RequireTrustedAuthenticode && request.ExpectedSigner is null)
        {
            throw new ArgumentException("A package without a declared digest requires Authenticode trust.", nameof(request));
        }
    }

    private static bool IsToken(string value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= 64
            && char.IsAsciiLetterOrDigit(value[0])
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsVersion(string value)
        => !string.IsNullOrEmpty(value)
            && value.Length <= 128
            && char.IsAsciiLetterOrDigit(value[0])
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string CanonicalSource(Uri source)
        => source.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);

    private static void AddField(StringBuilder builder, string? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        builder.Append(value.Length).Append(':').Append(value);
    }
}

