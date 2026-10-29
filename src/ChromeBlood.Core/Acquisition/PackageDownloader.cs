using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace ChromeBlood.Core.Acquisition;

public sealed class PackageDownloader : IDisposable
{
    private const int DefaultMaximumRedirects = 5;
    private const int CopyBufferSize = 81920;
    private readonly HttpClient _client;
    private readonly int _maximumRedirects;
    private readonly TimeSpan _noProgressTimeout;

    public PackageDownloader()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false }, DefaultMaximumRedirects,
            TransferRecoveryPolicy.Default.NoProgressTimeout)
    {
    }

    internal PackageDownloader(HttpMessageHandler handler, int maximumRedirects)
        : this(handler, maximumRedirects, TransferRecoveryPolicy.Default.NoProgressTimeout)
    {
    }

    internal PackageDownloader(HttpMessageHandler handler, int maximumRedirects, TimeSpan noProgressTimeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRedirects);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(noProgressTimeout, TimeSpan.Zero);

        _client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
        _maximumRedirects = maximumRedirects;
        _noProgressTimeout = noProgressTimeout;
    }

    internal async Task<DownloadedPackage> DownloadAsync(DownloadRequest request, string partialPath, CancellationToken cancellationToken)
        => await DownloadAsync(
            request,
            partialPath,
            () => new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough),
            true,
            cancellationToken).ConfigureAwait(false);

    internal async Task<DownloadedPackage> DownloadAsync(
        DownloadRequest request,
        string partialPath,
        Func<FileStream> createPartial,
        bool deleteOwnedPartialOnFailure,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadAsync(request, partialPath, createPartial, null, null, null, null, request.Source,
                deleteOwnedPartialOnFailure, cancellationToken).ConfigureAwait(false);
        }
        catch (PackageIntegrityException exception)
        {
            throw new PackageAcquisitionException(exception.Message, exception.Failure, null, exception);
        }
    }

    internal async Task<DownloadedPackage> DownloadAsync(
        DownloadRequest request,
        string partialPath,
        Func<FileStream> createPartial,
        Func<FileStream?>? openPartial,
        PackageDownloadCheckpoint? checkpoint,
        Action<PackageDownloadCheckpoint>? saveCheckpoint,
        IProgress<PackageDownloadProgress>? progress,
        Uri checkpointSource,
        bool deleteOwnedPartialOnFailure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(partialPath);
        ArgumentNullException.ThrowIfNull(createPartial);
        ArgumentNullException.ThrowIfNull(checkpointSource);
        ValidateTransportRequest(request);

        Uri current = request.Source;
        HttpResponseMessage? response = null;
        bool ownsPartial = false;
        bool completed = false;
        long resumeOffset = 0;
        try
        {
            if (checkpoint is not null && openPartial is not null)
            {
                try
                {
                    resumeOffset = new FileInfo(partialPath).Length;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw StorageFailure(exception);
                }
            }
            if (resumeOffset <= 0 || checkpoint is null || resumeOffset >= checkpoint.TotalBytes) resumeOffset = 0;
            for (int redirects = 0; ; redirects++)
            {
                RequireHttps(current);
                using HttpRequestMessage message = new(HttpMethod.Get, current);
                if (resumeOffset > 0)
                {
                    message.Headers.Range = new RangeHeaderValue(resumeOffset, null);
                    message.Headers.IfRange = checkpoint!.UsesEntityTag
                        ? new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(checkpoint.Validator))
                        : new RangeConditionHeaderValue(DateTimeOffset.Parse(checkpoint.Validator, System.Globalization.CultureInfo.InvariantCulture));
                }
                response = await SendAsync(message, cancellationToken).ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                {
                    break;
                }

                if (redirects >= _maximumRedirects)
                {
                    throw new PackageAcquisitionException("The package download exceeded the redirect limit.");
                }

                Uri location = response.Headers.Location
                    ?? throw new PackageAcquisitionException("The package download returned a redirect without a location.");

                Uri next = location.IsAbsoluteUri ? location : new Uri(current, location);
                RequireHttps(next);
                response.Dispose();
                response = null;
                current = next;
            }

            if (!response.IsSuccessStatusCode)
            {
                if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                    throw new PackageIntegrityException("The retained partial package cannot be resumed from this server.");
                throw HttpFailure(response);
            }

            if (resumeOffset > 0 && response.StatusCode == HttpStatusCode.PartialContent)
            {
                ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
                if (range?.From != resumeOffset || range.Length != checkpoint!.TotalBytes
                    || response.Content.Headers.ContentLength is long segmentLength && segmentLength != checkpoint.TotalBytes - resumeOffset
                    || !ValidatorMatches(response, checkpoint))
                    throw new PackageIntegrityException("The resumed package response did not match its stored validator and byte range.");
            }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                resumeOffset = 0;
            }
            else
            {
                throw new PackageIntegrityException("The package server returned an unsupported partial response.");
            }

            long? declaredLength = response.Content.Headers.ContentLength;
            if (declaredLength is < 0 || declaredLength > request.MaximumBytes - resumeOffset)
            {
                throw new PackageAcquisitionException("The declared package size exceeds the allowed size.");
            }

            FileStream opened;
            try
            {
                opened = resumeOffset > 0 ? openPartial!() ?? throw new PackageIntegrityException("The retained partial package changed.") : createPartial();
            }
            catch (PackageIntegrityException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw StorageFailure(exception);
            }
            await using FileStream output = opened;
            ownsPartial = true;
            if (resumeOffset > 0)
            {
                try
                {
                    if (output.Length != resumeOffset)
                        throw new PackageIntegrityException("The retained partial package changed.");
                }
                catch (PackageIntegrityException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw StorageFailure(exception);
                }
            }
            if (resumeOffset == 0 && saveCheckpoint is not null)
            {
                PackageDownloadCheckpoint? newCheckpoint = CreateCheckpoint(response, request, checkpointSource);
                if (newCheckpoint is not null)
                {
                    try
                    {
                        saveCheckpoint(newCheckpoint);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        throw StorageFailure(exception);
                    }
                }
            }
            await using Stream input = await ReadResponseStreamAsync(response, cancellationToken).ConfigureAwait(false);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[CopyBufferSize];
            long total = resumeOffset;
            if (resumeOffset > 0)
            {
                output.Position = 0;
                int previous;
                try
                {
                    while ((previous = await output.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                        hash.AppendData(buffer, 0, previous);
                    output.Position = resumeOffset;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw StorageFailure(exception);
                }
            }
            progress?.Report(new PackageDownloadProgress(total, resumeOffset > 0 ? checkpoint!.TotalBytes : declaredLength));
            while (true)
            {
                int read = await ReadNetworkAsync(input, buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > request.MaximumBytes || declaredLength.HasValue && total - resumeOffset > declaredLength.Value)
                {
                    throw new PackageAcquisitionException("The package body exceeds its allowed or declared size.");
                }

                hash.AppendData(buffer, 0, read);
                try
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw StorageFailure(exception);
                }
                progress?.Report(new PackageDownloadProgress(total, resumeOffset > 0 ? checkpoint!.TotalBytes : declaredLength));
            }

            if (declaredLength.HasValue && total - resumeOffset != declaredLength.Value)
            {
                throw new PackageAcquisitionException("The package body length does not match its declared size.");
            }

            try
            {
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw StorageFailure(exception);
            }
            string sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (request.ExpectedSha256 is not null && !string.Equals(sha256, request.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageIntegrityException("The package SHA-256 digest does not match the declared digest.");
            }

            completed = true;
            return new DownloadedPackage(current, total, sha256, partialPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PackageAcquisitionException)
        {
            throw;
        }
        catch (HttpRequestException exception)
        {
            throw new PackageAcquisitionException("The package connection was interrupted.",
                TransferFailureKind.TransientTransport, null, exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw StorageFailure(exception);
        }
        catch (IOException exception)
        {
            throw StorageFailure(exception);
        }
        finally
        {
            response?.Dispose();
            if (ownsPartial && !completed && deleteOwnedPartialOnFailure)
            {
                DeleteOwnedPartial(partialPath);
            }
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using CancellationTokenSource progress = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress.CancelAfter(_noProgressTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, progress.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new PackageAcquisitionException("The package connection made no progress before the timeout.",
                TransferFailureKind.TransientTransport, null, exception);
        }
    }

    private async Task<Stream> ReadResponseStreamAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using CancellationTokenSource progress = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress.CancelAfter(_noProgressTimeout);
        try
        {
            return await response.Content.ReadAsStreamAsync(progress.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new PackageAcquisitionException("The package response made no progress before the timeout.",
                TransferFailureKind.TransientTransport, null, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new PackageAcquisitionException("The package connection was interrupted.",
                TransferFailureKind.TransientTransport, null, exception);
        }
    }

    private async Task<int> ReadNetworkAsync(Stream input, byte[] buffer, CancellationToken cancellationToken)
    {
        using CancellationTokenSource progress = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        progress.CancelAfter(_noProgressTimeout);
        try
        {
            return await input.ReadAsync(buffer, progress.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            throw new PackageAcquisitionException("The package response made no progress before the timeout.",
                TransferFailureKind.TransientTransport, null, exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            throw new PackageAcquisitionException("The package connection was interrupted.",
                TransferFailureKind.TransientTransport, null, exception);
        }
    }

    private static PackageAcquisitionException HttpFailure(HttpResponseMessage response)
    {
        TransferFailureKind failure = response.StatusCode switch
        {
            HttpStatusCode.RequestTimeout => TransferFailureKind.TransientTransport,
            HttpStatusCode.TooManyRequests => TransferFailureKind.RateLimited,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => TransferFailureKind.AccountRequired,
            HttpStatusCode.NotFound or HttpStatusCode.Gone => TransferFailureKind.NotFound,
            HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => TransferFailureKind.TransientTransport,
            _ => TransferFailureKind.Permanent
        };
        return new PackageAcquisitionException($"The package server returned HTTP {(int)response.StatusCode}.",
            failure, RetryAfter(response));
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? retry = response.Headers.RetryAfter;
        if (retry?.Delta is TimeSpan delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (retry?.Date is DateTimeOffset date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }
        return null;
    }

    private static PackageAcquisitionException StorageFailure(Exception exception)
        => new("The package cache could not write the downloaded bytes.", TransferFailureKind.Storage, null, exception);

    private static PackageDownloadCheckpoint? CreateCheckpoint(HttpResponseMessage response, DownloadRequest request,
        Uri checkpointSource)
    {
        long? length = response.Content.Headers.ContentLength;
        if (request.ExpectedSha256 is null || response.StatusCode != HttpStatusCode.OK
            || length is not > 0 || length > request.MaximumBytes
            || !response.Headers.AcceptRanges.Contains("bytes")) return null;
        string? entityTag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        DateTimeOffset? modified = response.Content.Headers.LastModified;
        if (entityTag is null && modified is null) return null;
        string routeHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(checkpointSource.AbsoluteUri)));
        return new PackageDownloadCheckpoint(routeHash, entityTag ?? modified!.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            entityTag is not null, length.Value);
    }

    private static bool ValidatorMatches(HttpResponseMessage response, PackageDownloadCheckpoint checkpoint)
        => checkpoint.UsesEntityTag
            ? string.Equals(response.Headers.ETag?.ToString(), checkpoint.Validator, StringComparison.Ordinal)
            : response.Content.Headers.LastModified is DateTimeOffset modified
                && string.Equals(modified.ToString("R", System.Globalization.CultureInfo.InvariantCulture), checkpoint.Validator, StringComparison.Ordinal);

    private static void ValidateTransportRequest(DownloadRequest request)
    {
        RequireHttps(request.Source);
        if (request.MaximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "The maximum package size must be positive.");
        }

        if (request.ExpectedSha256 is not null && !IsSha256(request.ExpectedSha256))
        {
            throw new ArgumentException("The expected SHA-256 digest is invalid.", nameof(request));
        }
    }

    private static void RequireHttps(Uri uri)
    {
        if (!uri.IsAbsoluteUri
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new PackageAcquisitionException("Package acquisition requires an absolute HTTPS URL without user information.");
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MultipleChoices
            or HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    private static void DeleteOwnedPartial(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
