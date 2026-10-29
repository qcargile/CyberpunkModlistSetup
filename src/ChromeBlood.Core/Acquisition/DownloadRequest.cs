

namespace ChromeBlood.Core.Acquisition;

public enum SetupAcquisitionKind { Direct }

public sealed record DownloadRequest(
    Uri Source,
    string? ExpectedSha256,
    string Architecture,
    string ManifestVersion,
    SetupAcquisitionKind AcquisitionKind,
    bool RequireTrustedAuthenticode,
    string? ExpectedSigner,
    long MaximumBytes);

public sealed record DownloadedPackage(Uri FinalSource, long Length, string Sha256, string PartialPath);

internal sealed record PackageDownloadCheckpoint(string RouteHash, string Validator, bool UsesEntityTag, long TotalBytes);

public enum TransferFailureKind
{
    TransientTransport,
    RateLimited,
    AccountRequired,
    NotFound,
    IntegrityRejected,
    Storage,
    Permanent
}

public enum PackageDownloadProgressState { Transferring, WaitingToRetry }

public sealed record PackageDownloadProgress(long BytesReceived, long? TotalBytes)
{
    public PackageDownloadProgressState State { get; init; } = PackageDownloadProgressState.Transferring;
    public int Attempt { get; init; } = 1;
    public int MaximumAttempts { get; init; } = 1;
    public TimeSpan? RetryDelay { get; init; }
}

public interface ITransferFailure
{
    TransferFailureKind Failure { get; }
    TimeSpan? RetryAfter { get; }
}

public class PackageAcquisitionException : IOException, ITransferFailure
{
    public PackageAcquisitionException(string message, Exception? innerException = null)
        : this(message, TransferFailureKind.Permanent, null, innerException)
    {
    }

    public PackageAcquisitionException(string message, TransferFailureKind failure,
        TimeSpan? retryAfter = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        RetryAfter = retryAfter;
    }

    public TransferFailureKind Failure { get; }
    public TimeSpan? RetryAfter { get; }
}

internal sealed class PackageIntegrityException(string message)
    : PackageAcquisitionException(message, TransferFailureKind.IntegrityRejected);

internal sealed class TransferRecoveryPolicy
{
    public static TransferRecoveryPolicy Default { get; } = new(3, TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(30));

    public TransferRecoveryPolicy(int maximumAttempts, TimeSpan initialRetryDelay,
        TimeSpan maximumRetryDelay, TimeSpan maximumCumulativeRetryDelay, TimeSpan noProgressTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialRetryDelay, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRetryDelay, initialRetryDelay);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCumulativeRetryDelay, maximumRetryDelay);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(noProgressTimeout, TimeSpan.Zero);
        MaximumAttempts = maximumAttempts;
        InitialRetryDelay = initialRetryDelay;
        MaximumRetryDelay = maximumRetryDelay;
        MaximumCumulativeRetryDelay = maximumCumulativeRetryDelay;
        NoProgressTimeout = noProgressTimeout;
    }

    public int MaximumAttempts { get; }
    public TimeSpan InitialRetryDelay { get; }
    public TimeSpan MaximumRetryDelay { get; }
    public TimeSpan MaximumCumulativeRetryDelay { get; }
    public TimeSpan NoProgressTimeout { get; }

    public TimeSpan DelayFor(int completedAttempt, TimeSpan? retryAfter)
    {
        double factor = Math.Pow(2, Math.Max(0, completedAttempt - 1));
        TimeSpan backoff = TimeSpan.FromTicks(Math.Min(MaximumRetryDelay.Ticks,
            (long)Math.Min(long.MaxValue, InitialRetryDelay.Ticks * factor)));
        return retryAfter is TimeSpan providerDelay && providerDelay > backoff ? providerDelay : backoff;
    }
}


