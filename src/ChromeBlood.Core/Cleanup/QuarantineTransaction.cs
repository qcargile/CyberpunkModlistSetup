using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using ChromeBlood.Contracts.Serialization;
using ChromeBlood.Core.Acquisition;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Cleanup;

public enum QuarantineTransactionState { Staged, InProgress, Recoverable, Failed, Committed }
public enum QuarantineEntryState { Planned, Moving, Copied, Published, Stored, Failed }
public enum QuarantineFailure { None, Missing, AccessDenied, Locked, ReparsePoint, IdentityChanged, HashChanged, Conflict, IoFailure }
public enum QuarantineRestoreEntryState { Pending, Restored, AlreadyRestored, Conflict, Failed }

public sealed record QuarantineManifestEntry(
    int Order,
    string RelativePath,
    string SourcePath,
    string DestinationPath,
    CleanupEntryKind Kind,
    long Size,
    string Sha256,
    string PreIdentity,
    string PostIdentity,
    FileAttributes Attributes,
    QuarantineEntryState State,
    QuarantineFailure Failure)
{
    public CleanupPathMetadata? Metadata { get; init; }
    public string? FailureReason { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NativeErrorCode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? FailureHResult { get; init; }
}

public sealed record QuarantineManifest(
    int SchemaVersion,
    string QuarantineId,
    string GameRoot,
    string GameRootIdentity,
    string QuarantineRoot,
    string QuarantineRootIdentity,
    QuarantineTransactionState State,
    QuarantineManifestEntry[] Entries)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; init; }
}

public sealed record QuarantineEntryResult(string RelativePath, QuarantineEntryState State, QuarantineFailure Failure, string? Reason = null)
{
    public int? NativeErrorCode { get; init; }
    public int? FailureHResult { get; init; }
}

public sealed record QuarantineResult(
    QuarantineTransactionState State,
    string ManifestPath,
    QuarantineManifest Manifest,
    QuarantineEntryResult[] Entries);

public sealed record QuarantineUndoReference(string QuarantineId, string ManifestSha256, string QuarantineRoot);
public sealed record QuarantineRestoreEntry(string RelativePath, QuarantineRestoreEntryState State, string? Reason);
public sealed record QuarantineRestoreResult(string QuarantineId, QuarantineRestoreEntry[] Entries);
public sealed record QuarantineRecoveryState(string QuarantineId, QuarantineTransactionState State, QuarantineManifestEntry[] Entries, bool HasCommittedManifest)
{
    public string GameRoot { get; init; } = string.Empty;
    public string? FailureReason { get; init; }
}
public sealed record QuarantineRestoreRecovery(int SchemaVersion, string QuarantineId, string ManifestSha256, QuarantineRestoreEntry[] Entries);

public sealed class CleanupManifestException(string message) : IOException(message);

public interface ICleanupTransactionFaults
{
    bool IsSameVolume(string source, string destination);
    void Boundary(string name);
}

internal sealed class CleanupDirectoryGuard : IDisposable
{
    private readonly string _root;
    private readonly WindowsSecureFileApi _api = new();
    private readonly SecureCacheRoot _rootLease;
    private readonly List<SafeFileHandle> _handles = [];

    internal CleanupDirectoryGuard(string root)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootLease = new SecureCacheRoot(_root);
    }

    internal void EnsureParent(string path)
    {
        string parent = Path.GetDirectoryName(path) ?? throw new CleanupManifestException("The cleanup path has no parent.");
        if (CleanupPlanner.PathEquals(parent, _root)) return;
        EnsureDirectory(parent);
    }

    internal SafeFileHandle EnsureDirectory(string path)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (!CleanupPlanner.SameOrChild(target, _root)) throw new CleanupManifestException("The cleanup directory escapes its held root.");
        string relative = Path.GetRelativePath(_root, target);
        if (relative == ".") throw new CleanupManifestException("The held cleanup root cannot be returned as a created child.");
        string current = _root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            SafeFileHandle? handle = _api.Open(new SecureFileOpenRequest(current, SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes, FileShare.Read | FileShare.Write, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Directory));
            if (handle is null)
            {
                _api.CreateDirectory(current);
                handle = _api.Open(new SecureFileOpenRequest(current, SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes, FileShare.Read | FileShare.Write, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Directory))
                    ?? throw new CleanupManifestException("A cleanup directory could not be held after creation.");
            }
            SecureFileMetadata metadata = _api.GetMetadata(handle);
            string physical = SecureCacheRoot.NormalizeFinalPath(_api.GetFinalPath(handle));
            if (!metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(physical, current))
            {
                handle.Dispose();
                throw new CleanupManifestException("A cleanup directory segment is a reparse point or physical escape.");
            }
            _handles.Add(handle);
        }
        return _handles[^1];
    }

    public void Dispose()
    {
        for (int index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
        _rootLease.Dispose();
    }
}

internal sealed class NoCleanupTransactionFaults : ICleanupTransactionFaults
{
    public bool IsSameVolume(string source, string destination)
        => string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(destination), StringComparison.OrdinalIgnoreCase);
    public void Boundary(string name) { }
}

public static class QuarantineManifestCodec
{
    public static QuarantineManifest ReadStrict(string path)
    {
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new CleanupManifestException("The quarantine manifest is missing or unsafe.");
        byte[] bytes = QuarantineTransaction.ReadBounded(path, 32 * 1024 * 1024);
        QuarantineManifest manifest;
        try { manifest = ContractJson.Deserialize<QuarantineManifest>(bytes); }
        catch (Exception exception) when (exception is ContractFormatException or ArgumentException)
        {
            throw new CleanupManifestException("The quarantine manifest is invalid.");
        }
        byte[] canonical = ContractJson.SerializeUtf8(manifest);
        if (!bytes.AsSpan().SequenceEqual(canonical)) throw new CleanupManifestException("The quarantine manifest is not canonical.");
        Validate(manifest, true);
        return manifest;
    }

    public static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(QuarantineTransaction.ReadBounded(path, 32 * 1024 * 1024)));

    internal static byte[] Write(QuarantineManifest manifest)
    {
        Validate(manifest, true);
        return ContractJson.SerializeUtf8(manifest);
    }

    internal static void Validate(QuarantineManifest manifest, bool requireCommitted)
    {
        if (manifest.SchemaVersion != 1 || !ValidId(manifest.QuarantineId) || manifest.Entries.Length > 200000)
            throw new CleanupManifestException("The quarantine manifest header is invalid.");
        if (!Path.IsPathFullyQualified(manifest.GameRoot)
            || !Path.IsPathFullyQualified(manifest.QuarantineRoot)
            || manifest.GameRootIdentity.Length == 0
            || manifest.QuarantineRootIdentity.Length == 0
            || CleanupPlanner.SameOrChild(manifest.GameRoot, manifest.QuarantineRoot)
            || CleanupPlanner.SameOrChild(manifest.QuarantineRoot, manifest.GameRoot))
            throw new CleanupManifestException("The quarantine manifest roots are invalid.");
        if (requireCommitted && manifest.State != QuarantineTransactionState.Committed)
            throw new CleanupManifestException("Only a committed quarantine manifest can be restored.");
        HashSet<string> relative = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < manifest.Entries.Length; index++)
        {
            QuarantineManifestEntry entry = manifest.Entries[index];
            if (entry is null || entry.Order != index || requireCommitted && entry.State != QuarantineEntryState.Stored || !relative.Add(entry.RelativePath))
                throw new CleanupManifestException("The quarantine manifest entry sequence is invalid.");
            string source = CleanupPlanner.ResolveUnder(manifest.GameRoot, entry.RelativePath);
            string destination = CleanupPlanner.ResolveUnder(Path.Combine(manifest.QuarantineRoot, manifest.QuarantineId, "files"), entry.RelativePath);
            if (!CleanupPlanner.PathEquals(source, entry.SourcePath) || !CleanupPlanner.PathEquals(destination, entry.DestinationPath))
                throw new CleanupManifestException("The quarantine manifest path binding is invalid.");
            if (entry.Kind == CleanupEntryKind.File && (entry.Size < 0 || entry.Sha256.Length != 64 || entry.PreIdentity.Length == 0 || requireCommitted && entry.PostIdentity.Length == 0))
                throw new CleanupManifestException("The quarantine file evidence is invalid.");
            if (entry.Kind == CleanupEntryKind.Directory && (entry.Size != 0 || entry.Sha256.Length != 0 || entry.PreIdentity.Length == 0 || requireCommitted && entry.PostIdentity.Length == 0))
                throw new CleanupManifestException("The quarantine directory evidence is invalid.");
            if (entry.Kind == CleanupEntryKind.Other || entry.Attributes.HasFlag(FileAttributes.ReparsePoint) || requireCommitted && entry.Failure != QuarantineFailure.None)
                throw new CleanupManifestException("The quarantine entry kind, attributes, or failure state is invalid.");
            if (entry.Metadata is null || entry.Metadata.Attributes != entry.Attributes)
                throw new CleanupManifestException("The quarantine entry metadata is missing or inconsistent.");
        }
    }

    internal static bool ValidId(string value)
        => value.Length is >= 1 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}

public sealed class QuarantineTransaction
{
    private const int MaximumTransactionBytes = 32 * 1024 * 1024;
    private readonly ICleanupTransactionFaults _faults;
    private readonly Func<DateTimeOffset> _clock;

    public QuarantineTransaction() : this(new NoCleanupTransactionFaults(), () => DateTimeOffset.UtcNow) { }

    public QuarantineTransaction(ICleanupTransactionFaults faults)
        : this(faults, () => DateTimeOffset.UtcNow)
    {
    }

    internal QuarantineTransaction(ICleanupTransactionFaults faults, Func<DateTimeOffset> clock)
    {
        ArgumentNullException.ThrowIfNull(faults);
        ArgumentNullException.ThrowIfNull(clock);
        _faults = faults;
        _clock = clock;
    }

    public async Task<QuarantineResult> ExecuteRecordedAsync(AuthorizedCleanupPlan authorized, string quarantineId, UndoJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorized);
        ArgumentNullException.ThrowIfNull(journal);
        (CleanupPlan plan, CleanupAuthorization authorization) = authorized.Snapshot();
        ValidateAuthorization(plan, authorization);
        return await ExecuteReservedAsync(plan, authorization.PlanSha256, quarantineId, journal, cancellationToken).ConfigureAwait(false);
    }

    internal Task<QuarantineResult> ExecuteDisposableTestAsync(CleanupPlan plan, string quarantineId, UndoJournal journal, OwnedCleanupMutationScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(scope);
        scope.Validate(plan);
        return ExecuteReservedAsync(plan, CleanupAuthorizationCodec.PlanSha256(plan), quarantineId, journal, cancellationToken);
    }

    private async Task<QuarantineResult> ExecuteReservedAsync(CleanupPlan plan, string planSha256, string quarantineId, UndoJournal journal, CancellationToken cancellationToken)
    {
        string transactionRoot = Path.Combine(plan.QuarantineRoot, quarantineId);
        string walPath = Path.Combine(transactionRoot, "transaction.wal.json");
        string manifestPath = Path.Combine(transactionRoot, "quarantine.json");
        EnsureProjectedBounds(plan, quarantineId, walPath);
        using IDisposable transactionLock = journal.AcquireTransaction(quarantineId);
        journal.Reserve(quarantineId, planSha256, plan.QuarantineRoot, walPath, manifestPath);
        QuarantineResult result = await ExecuteAsync(plan, quarantineId,
            () => journal.UpdateReservation(quarantineId, planSha256, UndoJournalState.Executing), cancellationToken).ConfigureAwait(false);
        if (result.State == QuarantineTransactionState.Committed)
        {
            journal.UpdateReservation(quarantineId, planSha256, UndoJournalState.Available, QuarantineManifestCodec.Sha256(result.ManifestPath));
        }
        else journal.UpdateReservation(quarantineId, planSha256, UndoJournalState.Executing);
        return result;
    }

    public Task<QuarantineResult> RecoverRecordedAsync(UndoJournal journal, string quarantineId, string planSha256, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        using IDisposable transactionLock = journal.AcquireTransaction(quarantineId);
        UndoJournalEntry reservation = journal.RequireReservation(quarantineId, planSha256);
        string transactionRoot = Path.Combine(reservation.QuarantineRoot, quarantineId);
        if (!CleanupPlanner.PathEquals(reservation.WalPath, Path.Combine(transactionRoot, "transaction.wal.json"))
            || !CleanupPlanner.PathEquals(reservation.ManifestPath, Path.Combine(transactionRoot, "quarantine.json")))
            throw new CleanupManifestException("The cleanup reservation does not point to its exact transaction files.");
        if (File.Exists(reservation.ManifestPath))
        {
            QuarantineManifest committed = QuarantineManifestCodec.ReadStrict(reservation.ManifestPath);
            ValidateRecoveryBinding(committed, reservation);
            string manifestSha = QuarantineManifestCodec.Sha256(reservation.ManifestPath);
            journal.UpdateReservation(quarantineId, planSha256, UndoJournalState.Available, manifestSha);
            return Task.FromResult(new QuarantineResult(QuarantineTransactionState.Committed, reservation.ManifestPath, committed, EntryResults(committed.Entries)));
        }

        QuarantineManifest wal = ReadWalStrict(reservation.WalPath, false, out string headerSha256);
        ValidateRecoveryBinding(wal, reservation);
        using SecureCacheRoot heldGameRoot = new(wal.GameRoot, new WindowsSecureFileApi(), false);
        using SecureCacheRoot heldQuarantineRoot = new(wal.QuarantineRoot, new WindowsSecureFileApi(), false);
        using SecureCacheRoot heldTransactionRoot = new(Path.Combine(wal.QuarantineRoot, quarantineId), new WindowsSecureFileApi(), false);
        if (WindowsCleanupProbe.IdentityOfDirectory(wal.GameRoot) != wal.GameRootIdentity
            || WindowsCleanupProbe.IdentityOfDirectory(wal.QuarantineRoot) != wal.QuarantineRootIdentity)
            throw new CleanupManifestException("A cleanup root identity changed since this transaction was recorded.");
        using CleanupDirectoryGuard filesGuard = new(Path.Combine(wal.QuarantineRoot, quarantineId, "files"));
        using EntryStateStore states = new(reservation.WalPath, headerSha256, wal);
        QuarantineManifestEntry[] entries = [.. wal.Entries];
        Dictionary<string, CleanupEntryKind> destinationKinds = entries.ToDictionary(value => value.DestinationPath, value => value.Kind, StringComparer.OrdinalIgnoreCase);
        HashSet<string> destinationPaths = new(StringComparer.OrdinalIgnoreCase) { Path.Combine(wal.QuarantineRoot, quarantineId, "files") };
        foreach (QuarantineManifestEntry entry in entries)
        {
            string current = entry.DestinationPath;
            while (destinationPaths.Add(current))
                current = Path.GetDirectoryName(current) ?? throw new CleanupManifestException("The transaction destination topology is invalid.");
        }
        for (int index = 0; index < entries.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuarantineManifestEntry entry = entries[index];
            try
            {
                if (entry.State == QuarantineEntryState.Stored)
                {
                    VerifyStoredEntry(entry, destinationKinds, destinationPaths, filesGuard);
                    continue;
                }
                void PersistBoundary(QuarantineEntryState state, string postIdentity)
                {
                    entry = entry with { State = state, PostIdentity = postIdentity, Failure = QuarantineFailure.None, FailureReason = null, NativeErrorCode = null, FailureHResult = null };
                    entries[index] = entry;
                    states.Persist(entries[index]);
                }
                string postIdentity = entry.Kind == CleanupEntryKind.File
                    ? RecoverFile(entry, filesGuard, PersistBoundary, cancellationToken)
                    : RecoverDirectory(entry, entries, filesGuard, PersistBoundary);
                entries[index] = entry with { State = QuarantineEntryState.Stored, PostIdentity = postIdentity, Failure = QuarantineFailure.None, FailureReason = null, NativeErrorCode = null, FailureHResult = null };
                states.Persist(entries[index]);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CleanupPlanException or System.ComponentModel.Win32Exception)
            {
                entries[index] = FailedEntry(entry, exception);
                QuarantineManifest failed = wal with { State = QuarantineTransactionState.Recoverable, Entries = entries, FailureReason = null };
                PersistWal(reservation.WalPath, failed);
                journal.UpdateReservation(quarantineId, planSha256, UndoJournalState.Executing);
                return Task.FromResult(new QuarantineResult(failed.State, reservation.ManifestPath, failed, EntryResults(entries)));
            }
        }

        QuarantineManifest final = wal with { State = QuarantineTransactionState.Committed, Entries = entries, FailureReason = null };
        byte[] bytes = QuarantineManifestCodec.Write(final);
        try { WriteAtomic(reservation.ManifestPath, bytes, false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            QuarantineManifest failed = wal with { State = QuarantineTransactionState.Recoverable, Entries = entries, FailureReason = TransactionFailure(exception) };
            PersistWal(reservation.WalPath, failed);
            return Task.FromResult(new QuarantineResult(failed.State, reservation.ManifestPath, failed, EntryResults(entries)));
        }
        string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        journal.UpdateReservation(quarantineId, planSha256, UndoJournalState.Available, sha);
        return Task.FromResult(new QuarantineResult(final.State, reservation.ManifestPath, final, EntryResults(entries)));
    }

    private static void ValidateRecoveryBinding(QuarantineManifest manifest, UndoJournalEntry reservation)
    {
        if (manifest.QuarantineId != reservation.QuarantineId || !CleanupPlanner.PathEquals(manifest.QuarantineRoot, reservation.QuarantineRoot))
            throw new CleanupManifestException("The cleanup journal is not bound to the recorded quarantine.");
    }

    private static QuarantineManifestEntry FailedEntry(QuarantineManifestEntry entry, Exception exception)
        => entry with { State = QuarantineEntryState.Failed, Failure = Classify(exception), FailureReason = ExceptionReason(exception),
            NativeErrorCode = NativeError(exception), FailureHResult = exception.HResult };

    private static string ExceptionReason(Exception exception)
        => exception.InnerException is null ? exception.Message : exception.Message + " " + exception.GetBaseException().Message;

    private static string TransactionFailure(Exception exception)
        => ExceptionReason(exception) + (NativeError(exception) is int error ? " (Windows error " + error + ")" : string.Empty)
            + " (HRESULT 0x" + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "). Keep the backup and retry cleanup after resolving this error.";

    private static QuarantineEntryResult[] EntryResults(QuarantineManifestEntry[] entries)
        => entries.Select(entry => new QuarantineEntryResult(entry.RelativePath, entry.State, entry.Failure, entry.FailureReason)
        { NativeErrorCode = entry.NativeErrorCode, FailureHResult = entry.FailureHResult }).ToArray();

    private static void VerifyStoredEntry(QuarantineManifestEntry entry, IReadOnlyDictionary<string, CleanupEntryKind> destinationKinds,
        IReadOnlySet<string> destinationPaths, CleanupDirectoryGuard destinationGuard)
    {
        destinationGuard.EnsureParent(entry.DestinationPath);
        if (entry.Kind == CleanupEntryKind.Directory)
        {
            VerifyStoredDirectory(entry, destinationKinds, destinationPaths);
            return;
        }
        WindowsSecureFileApi api = new();
        using SafeFileHandle held = api.Open(new SecureFileOpenRequest(entry.DestinationPath,
            SecureFileAccess.Read | SecureFileAccess.Attributes, FileShare.Read, SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The stored quarantine payload is missing.", entry.DestinationPath);
        SecureFileMetadata metadata = api.GetMetadata(held);
        FileEvidence evidence = FileEvidence.Read(held);
        if (metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), entry.DestinationPath))
            throw new CleanupPlanException("The stored quarantine payload is a reparse point or physical escape.");
        if (evidence.Identity != entry.PostIdentity) throw new CleanupPlanException("The stored quarantine payload identity changed.");
        if (evidence.Size != entry.Size || evidence.Sha256 != entry.Sha256) throw new CleanupPlanException("The stored quarantine payload content changed.");
        if (!MetadataMatches(entry.DestinationPath, CleanupEntryKind.File, entry.Metadata)) throw new IOException("The stored quarantine payload metadata changed.");
    }

    private static void VerifyStoredDirectory(QuarantineManifestEntry entry, IReadOnlyDictionary<string, CleanupEntryKind> destinationKinds,
        IReadOnlySet<string> destinationPaths)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle held = api.Open(new SecureFileOpenRequest(entry.DestinationPath, SecureFileAccess.Attributes,
            FileShare.Read, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Directory))
            ?? throw new DirectoryNotFoundException(entry.DestinationPath);
        SecureFileMetadata metadata = api.GetMetadata(held);
        if (!metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), entry.DestinationPath))
            throw new CleanupPlanException("The stored quarantine directory is a reparse point or physical escape.");
        if (WindowsCleanupProbe.Identity(held) != entry.PostIdentity) throw new CleanupPlanException("The stored quarantine directory identity changed.");
        if (!MetadataMatches(entry.DestinationPath, CleanupEntryKind.Directory, entry.Metadata)) throw new IOException("The stored quarantine directory metadata changed.");
        Stack<string> pending = new();
        pending.Push(entry.DestinationPath);
        while (pending.Count > 0)
        {
            foreach (string child in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                string full = Path.GetFullPath(child);
                FileAttributes attributes = File.GetAttributes(full);
                if (!destinationPaths.Contains(full) || attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new CleanupPlanException("The stored quarantine directory contains an unowned or reparse entry.");
                bool directory = attributes.HasFlag(FileAttributes.Directory);
                if (destinationKinds.TryGetValue(full, out CleanupEntryKind expected))
                {
                    if (directory != (expected == CleanupEntryKind.Directory)) throw new CleanupPlanException("A stored quarantine child kind changed.");
                }
                else if (directory) pending.Push(full);
                else throw new CleanupPlanException("The stored quarantine directory contains an unplanned file.");
            }
        }
    }

    private string RecoverFile(QuarantineManifestEntry entry, CleanupDirectoryGuard destinationGuard, Action<QuarantineEntryState, string> persistBoundary, CancellationToken cancellationToken)
    {
        destinationGuard.EnsureParent(entry.DestinationPath);
        if (File.Exists(entry.DestinationPath))
        {
            WindowsSecureFileApi api = new();
            using SafeFileHandle held = api.Open(new SecureFileOpenRequest(entry.DestinationPath, SecureFileAccess.Read | SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes, FileShare.Read, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
                ?? throw new FileNotFoundException("The published quarantine payload is missing.", entry.DestinationPath);
            SecureFileMetadata metadata = api.GetMetadata(held);
            if (metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), entry.DestinationPath))
                throw new CleanupPlanException("The published quarantine payload is not a direct regular file.");
            FileEvidence destination = FileEvidence.Read(held);
            if (entry.PostIdentity.Length > 0 && destination.Identity != entry.PostIdentity) throw new CleanupPlanException("The published quarantine payload identity changed.");
            if (destination.Size != entry.Size || destination.Sha256 != entry.Sha256) throw new IOException("The published quarantine payload content changed.");
            if (!MetadataMatches(entry.DestinationPath, CleanupEntryKind.File, entry.Metadata))
                RepairPublishedMetadata(entry, held, cancellationToken);
            else if (AttributesOrMissing(entry.SourcePath) is not null) DeleteVerifiedSource(entry);
            string partial = entry.DestinationPath + ".partial";
            if (File.Exists(partial)) DeleteVerifiedDuplicate(partial, entry.Size, entry.Sha256);
            return destination.Identity;
        }
        string pending = entry.DestinationPath + ".partial";
        if (!File.Exists(entry.SourcePath)) throw new FileNotFoundException("Neither cleanup source nor verified destination exists.", entry.SourcePath);
        if (File.Exists(pending)) DeleteRecoverablePartial(entry, pending);
        return StoreFile(entry with { Failure = QuarantineFailure.None }, destinationGuard, persistBoundary, cancellationToken);
    }

    private void RepairPublishedMetadata(QuarantineManifestEntry entry, SafeFileHandle destination, CancellationToken cancellationToken)
    {
        if (entry.PostIdentity.Length == 0) throw new CleanupPlanException("The published backup metadata changed and its recorded identity is missing. Keep the backup and inspect it before retrying.");
        WindowsSecureFileApi api = new();
        using SafeFileHandle source = api.Open(new SecureFileOpenRequest(entry.SourcePath,
            SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes, FileShare.Read,
            SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The original cleanup source is missing; the published backup metadata was retained.", entry.SourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        _faults.Boundary("entry:before-metadata");
        VerifySource(api, source, entry);
        ApplyMetadata(entry.DestinationPath, CleanupEntryKind.File, entry.Metadata);
        ApplyTimes(destination, entry.Metadata);
        VerifyMetadata(entry.DestinationPath, CleanupEntryKind.File, entry.Metadata);
        _faults.Boundary("entry:metadata-applied");
        FileEvidence published = FileEvidence.Read(destination);
        if (published.Identity != entry.PostIdentity || published.Size != entry.Size || published.Sha256 != entry.Sha256)
            throw new CleanupPlanException("The published quarantine payload identity or content changed during metadata recovery.");
        ApplyTimes(destination, entry.Metadata);
        VerifyMetadata(entry.DestinationPath, CleanupEntryKind.File, entry.Metadata);
        VerifySource(api, source, entry);
        cancellationToken.ThrowIfCancellationRequested();
        api.Delete(source);
    }

    private static void DeleteRecoverablePartial(QuarantineManifestEntry entry, string pending)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle source = api.Open(new SecureFileOpenRequest(entry.SourcePath,
            SecureFileAccess.Read | SecureFileAccess.Attributes, FileShare.Read, SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The cleanup source is missing; its partial backup was retained.", entry.SourcePath);
        SecureFileMetadata sourceMetadata = api.GetMetadata(source);
        FileEvidence evidence = FileEvidence.Read(source);
        if (sourceMetadata.IsDirectory || sourceMetadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(source), entry.SourcePath)
            || evidence.Identity != entry.PreIdentity || evidence.Size != entry.Size || evidence.Sha256 != entry.Sha256)
            throw new CleanupPlanException("The cleanup source identity or content changed; its partial backup was retained.");
        using SafeFileHandle partial = api.Open(new SecureFileOpenRequest(pending,
            SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes, FileShare.Read,
            SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The partial cleanup backup is missing.", pending);
        SecureFileMetadata metadata = api.GetMetadata(partial);
        if (metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(partial), pending))
            throw new CleanupPlanException("The partial cleanup backup is a reparse point or physical escape.");
        FileEvidence copied = FileEvidence.Read(partial);
        if (entry.PostIdentity != copied.Identity && (copied.Size != entry.Size || copied.Sha256 != entry.Sha256))
            throw new CleanupPlanException("The incomplete partial backup has no matching recorded identity. Keep the backup and inspect it before retrying.");
        api.Delete(partial);
    }

    private string RecoverDirectory(QuarantineManifestEntry entry, QuarantineManifestEntry[] transactionEntries, CleanupDirectoryGuard destinationGuard, Action<QuarantineEntryState, string> persistBoundary)
    {
        if (Directory.Exists(entry.DestinationPath))
        {
            if (Directory.Exists(entry.SourcePath)) return StoreDirectory(entry with { Failure = QuarantineFailure.None }, destinationGuard, persistBoundary);
            string identity = VerifyRecoverableDirectoryDestination(entry, transactionEntries);
            if (entry.PostIdentity.Length > 0 && identity != entry.PostIdentity) throw new CleanupPlanException("The published quarantine directory identity changed.");
            if (entry.PostIdentity.Length == 0) persistBoundary(QuarantineEntryState.Copied, identity);
            return identity;
        }
        return StoreDirectory(entry with { Failure = QuarantineFailure.None }, destinationGuard, persistBoundary);
    }

    private static string VerifyRecoverableDirectoryDestination(QuarantineManifestEntry directory, QuarantineManifestEntry[] transactionEntries)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle held = api.Open(new SecureFileOpenRequest(directory.DestinationPath, SecureFileAccess.Attributes, FileShare.Read, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Directory))
            ?? throw new DirectoryNotFoundException(directory.DestinationPath);
        SecureFileMetadata metadata = api.GetMetadata(held);
        if (!metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), directory.DestinationPath))
            throw new CleanupPlanException("The recoverable directory destination is a reparse point or physical escape.");
        if (!MetadataMatches(directory.DestinationPath, CleanupEntryKind.Directory, directory.Metadata)) throw new IOException("The recoverable directory metadata changed.");

        Dictionary<string, QuarantineManifestEntry> exactEntries = transactionEntries.ToDictionary(value => value.DestinationPath, StringComparer.OrdinalIgnoreCase);
        HashSet<string> allowed = new(StringComparer.OrdinalIgnoreCase) { directory.DestinationPath };
        foreach (QuarantineManifestEntry entry in transactionEntries.Where(value => CleanupPlanner.SameOrChild(value.DestinationPath, directory.DestinationPath)))
        {
            string current = entry.DestinationPath;
            while (CleanupPlanner.SameOrChild(current, directory.DestinationPath))
            {
                allowed.Add(current);
                if (CleanupPlanner.PathEquals(current, directory.DestinationPath)) break;
                current = Path.GetDirectoryName(current) ?? throw new CleanupPlanException("The transaction destination topology is invalid.");
            }
        }

        Stack<string> pending = new();
        pending.Push(directory.DestinationPath);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                string full = Path.GetFullPath(child);
                FileAttributes attributes = File.GetAttributes(full);
                if (!allowed.Contains(full) || attributes.HasFlag(FileAttributes.ReparsePoint)) throw new CleanupPlanException("The recoverable directory contains an unowned or reparse entry.");
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(full);
                    if (exactEntries.TryGetValue(full, out QuarantineManifestEntry? childDirectory)
                        && childDirectory.PostIdentity.Length > 0
                        && WindowsCleanupProbe.IdentityOfDirectory(full) != childDirectory.PostIdentity)
                        throw new CleanupPlanException("A recoverable child directory identity changed.");
                }
                else
                {
                    if (!exactEntries.TryGetValue(full, out QuarantineManifestEntry? file) || file.Kind != CleanupEntryKind.File) throw new CleanupPlanException("The recoverable directory contains an unplanned file.");
                    FileEvidence evidence = FileEvidence.Read(full);
                    if (evidence.Size != file.Size || evidence.Sha256 != file.Sha256 || file.PostIdentity.Length > 0 && evidence.Identity != file.PostIdentity || !MetadataMatches(full, CleanupEntryKind.File, file.Metadata))
                        throw new CleanupPlanException("A recoverable child file identity, content, or metadata changed.");
                }
            }
        }
        return WindowsCleanupProbe.Identity(held);
    }

    private static void DeleteVerifiedSource(QuarantineManifestEntry entry)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle held = api.Open(new SecureFileOpenRequest(entry.SourcePath, SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes, FileShare.Read, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The cleanup source is missing.", entry.SourcePath);
        VerifySource(api, held, entry);
        api.Delete(held);
    }

    private static void VerifySource(WindowsSecureFileApi api, SafeFileHandle held, QuarantineManifestEntry entry)
    {
        SecureFileMetadata metadata = api.GetMetadata(held);
        FileEvidence evidence = FileEvidence.Read(held);
        if (metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), entry.SourcePath)
            || evidence.Identity != entry.PreIdentity || evidence.Size != entry.Size || evidence.Sha256 != entry.Sha256)
            throw new IOException("The cleanup source identity or content changed.");
        if (!MetadataMatches(entry.SourcePath, CleanupEntryKind.File, entry.Metadata))
            throw new IOException("The cleanup source metadata changed; the published backup metadata was retained.");
    }

    private static QuarantineManifest ReadWalStrict(string path, bool requireCommitted, out string headerSha256)
    {
        using SecureCacheRoot transaction = new(Path.GetDirectoryName(path)!, new WindowsSecureFileApi(), false);
        byte[] bytes = ReadBounded(path, MaximumTransactionBytes);
        QuarantineManifest wal;
        try { wal = ContractJson.Deserialize<QuarantineManifest>(bytes); }
        catch (Exception exception) when (exception is ContractFormatException or ArgumentException)
        {
            throw new CleanupManifestException("The cleanup WAL is invalid.");
        }
        if (!bytes.AsSpan().SequenceEqual(ContractJson.SerializeUtf8(wal))) throw new CleanupManifestException("The cleanup WAL is noncanonical.");
        QuarantineManifestCodec.Validate(wal, requireCommitted);
        headerSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        return EntryStateStore.Read(path, headerSha256, wal);
    }

    private Task<QuarantineResult> ExecuteAsync(CleanupPlan plan, string quarantineId, Action staged, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != 1 || !QuarantineManifestCodec.ValidId(quarantineId)) throw new ArgumentException("Cleanup plan or quarantine ID is invalid.");
        ValidatePlanRoots(plan);
        ValidatePlanEntries(plan);
        CleanupRootObservation vaultAtExecution = new WindowsCleanupProbe().ObserveRoot(plan.QuarantineRoot, false, cancellationToken);
        if (vaultAtExecution.AvailableBytes < plan.RequiredBytes || !string.Equals(vaultAtExecution.FileIdentity, plan.QuarantineRootIdentity, StringComparison.Ordinal))
            throw new CleanupManifestException("The quarantine root capacity or physical identity changed before execution.");
        using SecureCacheRoot heldGameRoot = new(plan.GameRoot);
        using CleanupDirectoryGuard quarantineGuard = new(plan.QuarantineRoot);
        string transactionRoot = Path.Combine(plan.QuarantineRoot, quarantineId);
        string filesRoot = Path.Combine(transactionRoot, "files");
        string manifestPath = Path.Combine(transactionRoot, "quarantine.json");
        string walPath = Path.Combine(transactionRoot, "transaction.wal.json");
        if (Directory.Exists(transactionRoot)) throw new CleanupManifestException("The quarantine ID already exists.");
        quarantineGuard.EnsureDirectory(transactionRoot);
        quarantineGuard.EnsureDirectory(filesRoot);
        using SecureCacheRoot heldTransactionRoot = new(transactionRoot);
        using CleanupDirectoryGuard filesGuard = new(filesRoot);

        QuarantineManifestEntry[] manifestEntries = plan.Entries.Select((entry, index) => new QuarantineManifestEntry(
            index,
            entry.RelativePath,
            entry.SourcePath,
            CleanupPlanner.ResolveUnder(filesRoot, entry.RelativePath),
            entry.Kind,
            entry.Size,
            entry.Sha256,
            entry.FileIdentity,
            string.Empty,
            entry.Attributes,
            QuarantineEntryState.Planned,
            QuarantineFailure.None)
        { Metadata = entry.Metadata }).ToArray();
        QuarantineManifest state = new(1, quarantineId, plan.GameRoot, plan.GameRootIdentity, plan.QuarantineRoot, plan.QuarantineRootIdentity, QuarantineTransactionState.Staged, manifestEntries);
        byte[] stagedBytes = ContractJson.SerializeUtf8(state);
        if (stagedBytes.Length > MaximumTransactionBytes) throw new CleanupManifestException("The cleanup WAL exceeds its strict reader bound.");
        WriteAtomic(walPath, stagedBytes);
        staged();
        _faults.Boundary("wal:staged");
        using EntryStateStore states = new(walPath, Convert.ToHexStringLower(SHA256.HashData(stagedBytes)), state);

        bool recoverable = false;
        for (int index = 0; index < manifestEntries.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                recoverable = true;
                break;
            }
            QuarantineManifestEntry entry = manifestEntries[index];
            try
            {
                entry = entry with { State = QuarantineEntryState.Moving };
                manifestEntries[index] = entry;
                states.Persist(manifestEntries[index]);
                _faults.Boundary("entry:moving");
                void PersistBoundary(QuarantineEntryState boundaryState, string postIdentity)
                {
                    entry = entry with { State = boundaryState, PostIdentity = postIdentity };
                    manifestEntries[index] = entry;
                    states.Persist(manifestEntries[index]);
                }
                string postIdentity = entry.Kind == CleanupEntryKind.File
                    ? StoreFile(entry, filesGuard, PersistBoundary, cancellationToken)
                    : StoreDirectory(entry, filesGuard, PersistBoundary);
                entry = entry with { State = QuarantineEntryState.Stored, PostIdentity = postIdentity };
                manifestEntries[index] = entry;
                states.Persist(manifestEntries[index]);
                _faults.Boundary("entry:stored");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CleanupPlanException or System.ComponentModel.Win32Exception)
            {
                manifestEntries[index] = FailedEntry(entry, exception);
                recoverable = manifestEntries.Take(index).Any(value => value.State == QuarantineEntryState.Stored)
                    || File.Exists(entry.DestinationPath)
                    || Directory.Exists(entry.DestinationPath)
                    || File.Exists(entry.DestinationPath + ".partial");
                PersistWal(walPath, state with { State = recoverable ? QuarantineTransactionState.Recoverable : QuarantineTransactionState.Failed, Entries = manifestEntries });
                break;
            }
        }

        bool complete = manifestEntries.All(entry => entry.State == QuarantineEntryState.Stored);
        QuarantineTransactionState finalState = complete ? QuarantineTransactionState.Committed : recoverable ? QuarantineTransactionState.Recoverable : QuarantineTransactionState.Failed;
        QuarantineManifest final = state with { State = finalState, Entries = manifestEntries };
        if (complete)
        {
            byte[] committedBytes = QuarantineManifestCodec.Write(final);
            try
            {
                _faults.Boundary("manifest:before-commit");
                WriteAtomic(manifestPath, committedBytes, false);
                _faults.Boundary("manifest:committed");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                bool committed = File.Exists(manifestPath) && File.ReadAllBytes(manifestPath).AsSpan().SequenceEqual(committedBytes);
                if (!committed)
                {
                    finalState = QuarantineTransactionState.Recoverable;
                    final = final with { State = finalState, FailureReason = TransactionFailure(exception) };
                    PersistWal(walPath, final);
                }
            }
        }
        else
        {
            PersistWal(walPath, final);
        }
        return Task.FromResult(new QuarantineResult(finalState, manifestPath, final, EntryResults(manifestEntries)));
    }

    public static QuarantineRecoveryState ReadRecovery(string quarantineRoot, string quarantineId)
    {
        if (!QuarantineManifestCodec.ValidId(quarantineId)) throw new CleanupManifestException("The quarantine ID is invalid.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(quarantineRoot));
        string transactionRoot = CleanupPlanner.ResolveUnder(root, quarantineId);
        string manifestPath = Path.Combine(transactionRoot, "quarantine.json");
        if (File.Exists(manifestPath))
        {
            QuarantineManifest committed = QuarantineManifestCodec.ReadStrict(manifestPath);
            if (!string.Equals(committed.QuarantineId, quarantineId, StringComparison.Ordinal) || !CleanupPlanner.PathEquals(committed.QuarantineRoot, root))
                throw new CleanupManifestException("The committed manifest is not bound to the requested quarantine.");
            return new QuarantineRecoveryState(quarantineId, committed.State, committed.Entries, true) { GameRoot = committed.GameRoot, FailureReason = committed.FailureReason };
        }
        string walPath = Path.Combine(transactionRoot, "transaction.wal.json");
        QuarantineManifest wal = ReadWalStrict(walPath, false, out _);
        if (!string.Equals(wal.QuarantineId, quarantineId, StringComparison.Ordinal) || !CleanupPlanner.PathEquals(wal.QuarantineRoot, root))
            throw new CleanupManifestException("The WAL is not bound to the requested quarantine.");
        return new QuarantineRecoveryState(quarantineId, wal.State, wal.Entries, false) { GameRoot = wal.GameRoot, FailureReason = wal.FailureReason };
    }

    public static QuarantineRestoreRecovery ReadRestoreRecovery(QuarantineUndoReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reference.QuarantineRoot));
        string path = CleanupPlanner.ResolveUnder(root, Path.Combine(reference.QuarantineId, "restore.wal.json"));
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new CleanupManifestException("The exact restore WAL is missing or unsafe.");
        byte[] bytes = ReadBounded(path, 4 * 1024 * 1024);
        QuarantineRestoreRecovery recovery;
        try { recovery = ContractJson.Deserialize<QuarantineRestoreRecovery>(bytes); }
        catch (Exception exception) when (exception is ContractFormatException or ArgumentException)
        {
            throw new CleanupManifestException("The restore WAL is invalid.");
        }
        if (!bytes.AsSpan().SequenceEqual(ContractJson.SerializeUtf8(recovery))
            || recovery.SchemaVersion != 1
            || !string.Equals(recovery.QuarantineId, reference.QuarantineId, StringComparison.Ordinal)
            || !string.Equals(recovery.ManifestSha256, reference.ManifestSha256, StringComparison.Ordinal)
            || recovery.Entries.Length > 200000
            || recovery.Entries.Any(entry => entry is null))
            throw new CleanupManifestException("The restore WAL is noncanonical or not bound to the exact undo reference.");
        return recovery;
    }

    public async Task<QuarantineRestoreResult> RestoreRecordedAsync(
        UndoJournal journal,
        string quarantineId,
        string manifestSha256,
        string gameRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        using IDisposable transactionLock = journal.AcquireTransaction(quarantineId);
        QuarantineUndoReference reference = journal.Require(quarantineId, manifestSha256);
        UndoJournalEntry journalEntry = journal.Entries.Single(value => value.QuarantineId == quarantineId && value.ManifestSha256 == manifestSha256);
        bool terminal = journalEntry.State == UndoJournalState.Restored;
        if (!terminal) journal.Update(quarantineId, manifestSha256, UndoJournalState.Restoring);
        try
        {
            QuarantineRestoreResult result = await RestoreAsync(reference, gameRoot, cancellationToken).ConfigureAwait(false);
            if (!terminal)
            {
                UndoJournalState state = result.Entries.Any(value => value.State == QuarantineRestoreEntryState.Conflict)
                    ? UndoJournalState.Conflict
                    : result.Entries.All(value => value.State is QuarantineRestoreEntryState.Restored or QuarantineRestoreEntryState.AlreadyRestored)
                        ? UndoJournalState.Restored
                        : UndoJournalState.Failed;
                journal.Update(quarantineId, manifestSha256, state);
            }
            return result;
        }
        catch
        {
            if (!terminal) journal.Update(quarantineId, manifestSha256, UndoJournalState.Failed);
            throw;
        }
    }

    internal Task<QuarantineRestoreResult> RestoreDisposableTestAsync(QuarantineUndoReference reference, string gameRoot, UndoJournal journal, OwnedCleanupMutationScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(scope);
        scope.ValidateRoots(gameRoot, reference.QuarantineRoot);
        using IDisposable transactionLock = journal.AcquireTransaction(reference.QuarantineId);
        return RestoreAsync(reference, gameRoot, cancellationToken);
    }

    private Task<QuarantineRestoreResult> RestoreAsync(QuarantineUndoReference reference, string gameRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!QuarantineManifestCodec.ValidId(reference.QuarantineId) || reference.ManifestSha256.Length != 64) throw new CleanupManifestException("Undo reference is invalid.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reference.QuarantineRoot));
        using SecureCacheRoot heldQuarantineRoot = new(root);
        string manifestPath = CleanupPlanner.ResolveUnder(root, Path.Combine(reference.QuarantineId, "quarantine.json"));
        if (!File.Exists(manifestPath)) throw new CleanupManifestException("The exact quarantine manifest is missing.");
        if (!string.Equals(QuarantineManifestCodec.Sha256(manifestPath), reference.ManifestSha256, StringComparison.Ordinal))
            throw new CleanupManifestException("The exact quarantine manifest hash does not match.");
        QuarantineManifest manifest = QuarantineManifestCodec.ReadStrict(manifestPath);
        if (!string.Equals(manifest.QuarantineId, reference.QuarantineId, StringComparison.Ordinal)
            || !CleanupPlanner.PathEquals(manifest.QuarantineRoot, root)
            || !CleanupPlanner.PathEquals(manifest.GameRoot, gameRoot))
            throw new CleanupManifestException("The undo reference does not bind this manifest and game root.");
        using CleanupDirectoryGuard gameGuard = new(manifest.GameRoot);
        using SecureCacheRoot heldTransactionRoot = new(Path.Combine(root, reference.QuarantineId));
        using SecureCacheRoot heldFilesRoot = new(Path.Combine(root, reference.QuarantineId, "files"));
        WindowsCleanupProbe rootProbe = new();
        CleanupRootObservation gameObservation = rootProbe.ObserveRoot(manifest.GameRoot, false, cancellationToken);
        CleanupRootObservation vaultObservation = rootProbe.ObserveRoot(manifest.QuarantineRoot, false, cancellationToken);
        if (gameObservation.IsReparse || gameObservation.HasReparseAncestor || vaultObservation.IsReparse || vaultObservation.HasReparseAncestor
            || !CleanupPlanner.PathEquals(gameObservation.PhysicalPath, manifest.GameRoot)
            || !CleanupPlanner.PathEquals(vaultObservation.PhysicalPath, manifest.QuarantineRoot)
            || !string.Equals(gameObservation.FileIdentity, manifest.GameRootIdentity, StringComparison.Ordinal)
            || !string.Equals(vaultObservation.FileIdentity, manifest.QuarantineRootIdentity, StringComparison.Ordinal))
            throw new CleanupManifestException("The game root physical identity changed.");

        Dictionary<int, QuarantineRestoreEntry> outcomes = [];
        string restoreWalPath = CleanupPlanner.ResolveUnder(root, Path.Combine(reference.QuarantineId, "restore.wal.json"));
        void PersistRestore()
        {
            QuarantineRestoreEntry[] entries = manifest.Entries.Select(entry => outcomes.TryGetValue(entry.Order, out QuarantineRestoreEntry? outcome)
                ? outcome
                : new QuarantineRestoreEntry(entry.RelativePath, QuarantineRestoreEntryState.Pending, null)).ToArray();
            WriteAtomic(restoreWalPath, ContractJson.SerializeUtf8(new QuarantineRestoreRecovery(1, reference.QuarantineId, reference.ManifestSha256, entries)));
        }
        foreach (QuarantineManifestEntry entry in manifest.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Kind == CleanupEntryKind.Directory)
            {
                if (File.Exists(entry.SourcePath)) outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Conflict, "A file occupies the directory destination.");
                if (!Directory.Exists(entry.DestinationPath)
                    || File.GetAttributes(entry.DestinationPath).HasFlag(FileAttributes.ReparsePoint)
                    || WindowsCleanupProbe.IdentityOfDirectory(entry.DestinationPath) != entry.PostIdentity
                    || !MetadataMatches(entry.DestinationPath, CleanupEntryKind.Directory, entry.Metadata))
                    outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, "The quarantined directory identity or metadata changed.");
                continue;
            }
            if (!DestinationParentIsDirect(manifest.GameRoot, entry.SourcePath))
            {
                outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Conflict, "The destination parent chain contains a reparse point or incompatible entry.");
                continue;
            }
            if (File.Exists(entry.SourcePath))
            {
                if (File.GetAttributes(entry.SourcePath).HasFlag(FileAttributes.ReparsePoint))
                {
                    outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Conflict, "The destination is a reparse point.");
                    continue;
                }
                FileEvidence destination = FileEvidence.Read(entry.SourcePath);
                outcomes[entry.Order] = destination.Size == entry.Size && destination.Sha256 == entry.Sha256 && MetadataMatches(entry.SourcePath, entry.Kind, entry.Metadata)
                    ? new(entry.RelativePath, QuarantineRestoreEntryState.AlreadyRestored, null)
                    : new(entry.RelativePath, QuarantineRestoreEntryState.Conflict, "The destination differs and was not overwritten.");
            }
            else if (!File.Exists(entry.DestinationPath)) outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, "The quarantined file is missing.");
            else
            {
                if (File.GetAttributes(entry.DestinationPath).HasFlag(FileAttributes.ReparsePoint))
                {
                    outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, "The quarantined file is a reparse point.");
                    continue;
                }
                FileEvidence stored = FileEvidence.Read(entry.DestinationPath);
                if (stored.Size != entry.Size || stored.Sha256 != entry.Sha256 || !MetadataMatches(entry.DestinationPath, entry.Kind, entry.Metadata))
                    outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, "The quarantined file evidence changed.");
            }
        }

        HashSet<string> allowedDestinations = manifest.Entries.Select(value => value.SourcePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (QuarantineManifestEntry directory in manifest.Entries.Where(value => value.Kind == CleanupEntryKind.Directory))
        {
            if (!Directory.Exists(directory.SourcePath)) continue;
            bool unknown = HasUnknownOrReparseEntry(directory.SourcePath, allowedDestinations)
                || !MetadataMatches(directory.SourcePath, CleanupEntryKind.Directory, directory.Metadata);
            if (!unknown) continue;
            foreach (QuarantineManifestEntry affected in manifest.Entries.Where(value => CleanupPlanner.SameOrChild(value.SourcePath, directory.SourcePath)))
            {
                if (!outcomes.TryGetValue(affected.Order, out QuarantineRestoreEntry? current) || current.State != QuarantineRestoreEntryState.AlreadyRestored)
                    outcomes[affected.Order] = new(affected.RelativePath, QuarantineRestoreEntryState.Conflict, "The destination directory contains an unplanned entry.");
            }
        }

        PersistRestore();

        foreach (QuarantineManifestEntry entry in manifest.Entries.Where(value => value.Kind == CleanupEntryKind.File))
        {
            if (!outcomes.TryGetValue(entry.Order, out QuarantineRestoreEntry? current)
                || current.State != QuarantineRestoreEntryState.AlreadyRestored
                || !File.Exists(entry.DestinationPath)) continue;
            try
            {
                DeleteVerifiedDuplicate(entry.DestinationPath, entry.Size, entry.Sha256);
                _faults.Boundary("restore:duplicate-deleted");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, exception.Message);
            }
            PersistRestore();
        }

        foreach (QuarantineManifestEntry entry in manifest.Entries.Where(value => value.Kind == CleanupEntryKind.File))
        {
            if (outcomes.ContainsKey(entry.Order)) continue;
            try
            {
                gameGuard.EnsureParent(entry.SourcePath);
                MoveOrCopy(entry.DestinationPath, entry.SourcePath, entry.Size, entry.Sha256, entry.Metadata, cancellationToken);
                outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Restored, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, exception.Message);
            }
            PersistRestore();
        }
        foreach (QuarantineManifestEntry entry in manifest.Entries.Where(value => value.Kind == CleanupEntryKind.Directory).OrderByDescending(value => value.RelativePath.Length))
        {
            if (outcomes.ContainsKey(entry.Order)) continue;
            try
            {
                bool existed = Directory.Exists(entry.SourcePath);
                SafeFileHandle restoredDirectory = gameGuard.EnsureDirectory(entry.SourcePath);
                ApplyMetadata(entry.SourcePath, CleanupEntryKind.Directory, entry.Metadata);
                ApplyTimes(restoredDirectory, entry.Metadata);
                VerifyMetadata(entry.SourcePath, CleanupEntryKind.Directory, entry.Metadata);
                outcomes[entry.Order] = new(entry.RelativePath, existed ? QuarantineRestoreEntryState.AlreadyRestored : QuarantineRestoreEntryState.Restored, null);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                outcomes[entry.Order] = new(entry.RelativePath, QuarantineRestoreEntryState.Failed, exception.Message);
            }
            PersistRestore();
        }
        QuarantineRestoreResult result = new(reference.QuarantineId, outcomes.OrderBy(value => value.Key).Select(value => value.Value).ToArray());
        PersistRestore();
        return Task.FromResult(result);
    }

    private static void DeleteVerifiedDuplicate(string path, long size, string sha256)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle held = api.Open(new SecureFileOpenRequest(
            path,
            SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The duplicate quarantine payload is missing.", path);
        SecureFileMetadata metadata = api.GetMetadata(held);
        if (metadata.IsDirectory || metadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), path))
            throw new IOException("The duplicate quarantine payload is not a direct regular file.");
        FileEvidence evidence = FileEvidence.Read(held);
        if (evidence.Size != size || evidence.Sha256 != sha256) throw new IOException("The duplicate quarantine payload evidence changed.");
        api.Delete(held);
    }

    private static bool DestinationParentIsDirect(string root, string destination)
    {
        string? parent = Path.GetDirectoryName(destination);
        if (parent is null) return false;
        string relative = Path.GetRelativePath(root, parent);
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (!Directory.Exists(current)) return !File.Exists(current);
            FileAttributes attributes = File.GetAttributes(current);
            if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        return true;
    }

    private static bool HasUnknownOrReparseEntry(string root, HashSet<string> allowed)
    {
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            foreach (string child in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes attributes = File.GetAttributes(child);
                if (attributes.HasFlag(FileAttributes.ReparsePoint) || !allowed.Contains(Path.GetFullPath(child))) return true;
                if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(child);
            }
        }
        return false;
    }

    private string StoreFile(QuarantineManifestEntry entry, CleanupDirectoryGuard destinationGuard, Action<QuarantineEntryState, string> persistBoundary, CancellationToken cancellationToken)
    {
        destinationGuard.EnsureParent(entry.DestinationPath);
        WindowsSecureFileApi api = new();
        using SafeFileHandle source = api.Open(new SecureFileOpenRequest(
            entry.SourcePath,
            SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The planned source is missing.", entry.SourcePath);
        SecureFileMetadata metadata = api.GetMetadata(source);
        if (metadata.IsDirectory || metadata.IsReparsePoint) throw new CleanupPlanException("The planned source became a directory or reparse point.");
        if (!CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(source), entry.SourcePath)) throw new CleanupPlanException("The planned source resolves outside its direct path.");
        string identity = WindowsCleanupProbe.Identity(source);
        if (!string.Equals(identity, entry.PreIdentity, StringComparison.Ordinal)) throw new CleanupPlanException("The planned source identity changed.");
        FileEvidence evidence = FileEvidence.Read(source);
        if (evidence.Size != entry.Size || evidence.Sha256 != entry.Sha256) throw new CleanupPlanException("The planned source content changed.");

        if (_faults.IsSameVolume(entry.SourcePath, entry.DestinationPath))
        {
            ApplyTimes(source, entry.Metadata);
            api.Rename(source, entry.DestinationPath, false);
            ApplyTimes(source, entry.Metadata);
            VerifyMetadata(entry.DestinationPath, entry.Kind, entry.Metadata);
            persistBoundary(QuarantineEntryState.Published, WindowsCleanupProbe.Identity(source));
            _faults.Boundary("entry:moved");
            return WindowsCleanupProbe.Identity(source);
        }

        string partial = entry.DestinationPath + ".partial";
        if (!MetadataMatches(entry.SourcePath, CleanupEntryKind.File, entry.Metadata)) throw new CleanupPlanException("The planned source metadata changed before copying.");
        using SafeFileHandle copiedHandle = api.Open(new SecureFileOpenRequest(
            partial,
            SecureFileAccess.Read | SecureFileAccess.Write | SecureFileAccess.Delete | SecureFileAccess.Attributes,
            FileShare.Read | FileShare.Write,
            SecureFileDisposition.CreateNew,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.WriteThrough))
            ?? throw new IOException("The quarantine partial could not be created.");
        persistBoundary(QuarantineEntryState.Moving, WindowsCleanupProbe.Identity(copiedHandle));
        _faults.Boundary("entry:partial-created");
        CopyHandle(source, copiedHandle, cancellationToken);
        ApplyTimes(copiedHandle, entry.Metadata);
        FileEvidence copied = FileEvidence.Read(copiedHandle);
        if (copied.Size != entry.Size || copied.Sha256 != entry.Sha256) throw new IOException("The copied quarantine payload did not verify.");
        _faults.Boundary("entry:before-metadata");
        ApplyMetadata(partial, CleanupEntryKind.File, entry.Metadata);
        ApplyTimes(copiedHandle, entry.Metadata);
        VerifyMetadata(partial, CleanupEntryKind.File, entry.Metadata);
        _faults.Boundary("entry:metadata-applied");
        persistBoundary(QuarantineEntryState.Copied, copied.Identity);
        _faults.Boundary("entry:copied");
        api.Rename(copiedHandle, entry.DestinationPath, false);
        ApplyTimes(copiedHandle, entry.Metadata);
        if (!CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(copiedHandle), entry.DestinationPath)) throw new IOException("The published quarantine payload escaped its exact destination.");
        FileEvidence published = FileEvidence.Read(copiedHandle);
        if (published.Identity != copied.Identity || published.Size != entry.Size || published.Sha256 != entry.Sha256) throw new IOException("The published quarantine payload identity or content changed.");
        persistBoundary(QuarantineEntryState.Published, copied.Identity);
        _faults.Boundary("entry:published");
        published = FileEvidence.Read(copiedHandle);
        if (published.Identity != copied.Identity || published.Size != entry.Size || published.Sha256 != entry.Sha256) throw new IOException("The published quarantine payload changed before source deletion.");
        ApplyTimes(copiedHandle, entry.Metadata);
        VerifyMetadata(entry.DestinationPath, CleanupEntryKind.File, entry.Metadata);
        VerifySource(api, source, entry);
        api.Delete(source);
        return copied.Identity;
    }

    private string StoreDirectory(QuarantineManifestEntry entry, CleanupDirectoryGuard destinationGuard, Action<QuarantineEntryState, string> persistBoundary)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle source = api.Open(new SecureFileOpenRequest(
            entry.SourcePath,
            SecureFileAccess.Delete | SecureFileAccess.Attributes,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Directory))
            ?? throw new DirectoryNotFoundException(entry.SourcePath);
        SecureFileMetadata metadata = api.GetMetadata(source);
        if (!metadata.IsDirectory || metadata.IsReparsePoint) throw new CleanupPlanException("The planned directory became a reparse point or file.");
        if (!CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(source), entry.SourcePath)) throw new CleanupPlanException("The planned directory resolves outside its direct path.");
        if (!string.Equals(WindowsCleanupProbe.Identity(source), entry.PreIdentity, StringComparison.Ordinal)) throw new CleanupPlanException("The planned directory identity changed.");
        if (Directory.EnumerateFileSystemEntries(entry.SourcePath).Any()) throw new IOException("The planned directory is no longer empty.");
        SafeFileHandle destination = destinationGuard.EnsureDirectory(entry.DestinationPath);
        ApplyMetadata(entry.DestinationPath, CleanupEntryKind.Directory, entry.Metadata);
        ApplyTimes(destination, entry.Metadata);
        VerifyMetadata(entry.DestinationPath, CleanupEntryKind.Directory, entry.Metadata);
        string identity = WindowsCleanupProbe.IdentityOfDirectory(entry.DestinationPath);
        persistBoundary(QuarantineEntryState.Copied, identity);
        api.Delete(source);
        _faults.Boundary("directory:deleted");
        persistBoundary(QuarantineEntryState.Published, identity);
        return identity;
    }

    private void MoveOrCopy(string source, string destination, long size, string sha256, CleanupPathMetadata? metadata, CancellationToken cancellationToken)
    {
        WindowsSecureFileApi api = new();
        using SafeFileHandle held = api.Open(new SecureFileOpenRequest(
            source,
            SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes,
            FileShare.Read,
            SecureFileDisposition.OpenExisting,
            SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
            ?? throw new FileNotFoundException("The quarantined source is missing.", source);
        SecureFileMetadata sourceMetadata = api.GetMetadata(held);
        if (sourceMetadata.IsDirectory || sourceMetadata.IsReparsePoint || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(held), source))
            throw new IOException("The quarantined source is not a direct regular file.");
        FileEvidence evidence = FileEvidence.Read(held);
        if (evidence.Size != size || evidence.Sha256 != sha256) throw new IOException("The quarantined source evidence changed.");
        if (_faults.IsSameVolume(source, destination))
        {
            ApplyTimes(held, metadata);
            api.Rename(held, destination, false);
            VerifyMetadata(destination, CleanupEntryKind.File, metadata);
            _faults.Boundary("restore:moved");
        }
        else
        {
            string partial = destination + ".partial";
            if (File.Exists(partial))
            {
                using SafeFileHandle existing = api.Open(new SecureFileOpenRequest(partial, SecureFileAccess.Read | SecureFileAccess.Delete | SecureFileAccess.Attributes | SecureFileAccess.WriteAttributes, FileShare.Read, SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential))
                    ?? throw new IOException("The existing restore partial could not be held.");
                SecureFileMetadata existingMetadata = api.GetMetadata(existing);
                FileEvidence existingEvidence = FileEvidence.Read(existing);
                if (existingMetadata.IsDirectory || existingMetadata.IsReparsePoint || existingEvidence.Size != size || existingEvidence.Sha256 != sha256 || !MetadataMatches(partial, CleanupEntryKind.File, metadata))
                {
                    api.Delete(existing);
                }
                else
                {
                    ApplyTimes(existing, metadata);
                    api.Rename(existing, destination, false);
                    _faults.Boundary("restore:published");
                    FileEvidence publishedExisting = FileEvidence.Read(existing);
                    if (publishedExisting.Identity != existingEvidence.Identity || publishedExisting.Size != size || publishedExisting.Sha256 != sha256 || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(existing), destination))
                        throw new IOException("The resumed restore payload changed or escaped its destination.");
                    ApplyTimes(existing, metadata);
                    VerifyMetadata(destination, CleanupEntryKind.File, metadata);
                    api.Delete(held);
                    _faults.Boundary("restore:deleted");
                    return;
                }
            }
            using SafeFileHandle copiedHandle = api.Open(new SecureFileOpenRequest(partial, SecureFileAccess.Read | SecureFileAccess.Write | SecureFileAccess.Delete | SecureFileAccess.Attributes, FileShare.Read | FileShare.Write, SecureFileDisposition.CreateNew, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.WriteThrough))
                ?? throw new IOException("The restore partial could not be created.");
            CopyHandle(held, copiedHandle, cancellationToken);
            ApplyTimes(copiedHandle, metadata);
            _faults.Boundary("restore:copied");
            FileEvidence copied = FileEvidence.Read(copiedHandle);
            if (copied.Size != size || copied.Sha256 != sha256) throw new IOException("The restored copy did not verify.");
            ApplyTimes(copiedHandle, metadata);
            api.Rename(copiedHandle, destination, false);
            _faults.Boundary("restore:published");
            FileEvidence published = FileEvidence.Read(copiedHandle);
            if (published.Identity != copied.Identity || published.Size != size || published.Sha256 != sha256 || !CleanupPlanner.PathEquals(WindowsCleanupProbe.FinalPath(copiedHandle), destination))
                throw new IOException("The published restore payload changed or escaped its destination.");
            ApplyTimes(copiedHandle, metadata);
            VerifyMetadata(destination, CleanupEntryKind.File, metadata);
            api.Delete(held);
            _faults.Boundary("restore:deleted");
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void CopyHandle(SafeFileHandle source, SafeFileHandle destination, CancellationToken cancellationToken)
    {
        long length = RandomAccess.GetLength(source);
        byte[] buffer = new byte[81920];
        long offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(source, buffer, offset);
            if (read == 0) throw new EndOfStreamException();
            RandomAccess.Write(destination, buffer.AsSpan(0, read), offset);
            offset += read;
        }
        RandomAccess.FlushToDisk(destination);
    }

    private static void ValidatePlanRoots(CleanupPlan plan)
    {
        if (CleanupPlanner.SameOrChild(plan.QuarantineRoot, plan.GameRoot) || CleanupPlanner.SameOrChild(plan.GameRoot, plan.QuarantineRoot))
            throw new CleanupManifestException("Cleanup roots overlap.");
        if (CleanupPlanner.TouchesProtectedSystemRoot(plan.QuarantineRoot))
            throw new CleanupManifestException("The quarantine root cannot be a system path.");
        WindowsCleanupProbe probe = new();
        CleanupRootObservation game = probe.ObserveRoot(plan.GameRoot, false, CancellationToken.None);
        CleanupRootObservation vault = probe.ObserveRoot(plan.QuarantineRoot, false, CancellationToken.None);
        if (game.IsReparse || game.HasReparseAncestor || vault.IsReparse || vault.HasReparseAncestor
            || game.IsFileSystemRoot || vault.IsFileSystemRoot
            || !CleanupPlanner.PathEquals(game.PhysicalPath, plan.GameRoot)
            || !CleanupPlanner.PathEquals(vault.PhysicalPath, plan.QuarantineRoot)
            || !string.Equals(game.FileIdentity, plan.GameRootIdentity, StringComparison.Ordinal)
            || !string.Equals(vault.FileIdentity, plan.QuarantineRootIdentity, StringComparison.Ordinal))
            throw new CleanupManifestException("Cleanup root identity changed.");
    }

    private void ValidateAuthorization(CleanupPlan plan, CleanupAuthorization authorization)
    {
        if (authorization.AuthorizationId.Length != 32
            || authorization.Scope.ScopeToken.Length != 32
            || !string.Equals(CleanupAuthorizationCodec.PlanSha256(plan), authorization.PlanSha256, StringComparison.Ordinal)
            || authorization.PolicyKind == CleanupPolicyKind.Game && authorization.LegacyCommit.Length != 40
            || authorization.PolicyKind is CleanupPolicyKind.CyberpunkAppData or CleanupPolicyKind.IndexedGame && authorization.LegacyCommit.Length != 0
            || !Enum.IsDefined(authorization.PolicyKind)
            || authorization.PolicySha256.Length != 64
            || authorization.IssuedAtUtc > _clock()
            || authorization.ExpiresAtUtc <= _clock())
            throw new CleanupAuthorizationException("The cleanup authorization is invalid, changed, or expired.");
        authorization.Scope.Validate(plan);
    }

    private static void EnsureProjectedBounds(CleanupPlan plan, string quarantineId, string walPath)
    {
        string filesRoot = Path.Combine(plan.QuarantineRoot, quarantineId, "files");
        QuarantineManifestEntry[] entries = plan.Entries.Select((entry, index) => new QuarantineManifestEntry(
            index,
            entry.RelativePath,
            entry.SourcePath,
            CleanupPlanner.ResolveUnder(filesRoot, entry.RelativePath),
            entry.Kind,
            entry.Size,
            entry.Sha256,
            entry.FileIdentity,
            new string('f', 128),
            entry.Attributes,
            QuarantineEntryState.Stored,
            QuarantineFailure.None)
        { Metadata = entry.Metadata }).ToArray();
        QuarantineManifest projected = new(1, quarantineId, plan.GameRoot, plan.GameRootIdentity, plan.QuarantineRoot, plan.QuarantineRootIdentity, QuarantineTransactionState.Committed, entries);
        foreach (QuarantineManifestEntry entry in entries)
            if (ContractJson.SerializeUtf8(EntryStateStore.Snapshot(new string('a', 64), projected, entry)).Length > EntryStateStore.MaximumSnapshotBytes)
                throw new CleanupManifestException("A projected cleanup entry state exceeds its strict serialized bound.");
        if (ContractJson.SerializeUtf8(projected).Length > MaximumTransactionBytes
            || ContractJson.SerializeUtf8(projected with { State = QuarantineTransactionState.Staged }).Length > MaximumTransactionBytes
            || !CleanupPlanner.SameOrChild(walPath, plan.QuarantineRoot))
            throw new CleanupManifestException("The projected cleanup transaction exceeds its strict serialized bound or root.");
    }

    private static void ValidatePlanEntries(CleanupPlan plan)
    {
        if (plan.Entries.Length > 200000 || plan.RequiredBytes < 0) throw new CleanupManifestException("The cleanup plan bounds are invalid.");
        HashSet<string> relative = new(StringComparer.OrdinalIgnoreCase);
        long required = 0;
        foreach (CleanupEntry entry in plan.Entries)
        {
            if (entry.Status != CleanupEntryStatus.Available
                || entry.Kind is not (CleanupEntryKind.File or CleanupEntryKind.Directory)
                || entry.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || !relative.Add(entry.RelativePath))
                throw new CleanupManifestException("The cleanup plan contains an unavailable, unsupported, reparse, or duplicate entry.");
            if (entry.Metadata is null || entry.Metadata.Attributes != entry.Attributes)
                throw new CleanupManifestException("The cleanup plan lacks exact filesystem metadata.");
            string expected = CleanupPlanner.ResolveUnder(plan.GameRoot, entry.RelativePath);
            if (!CleanupPlanner.PathEquals(expected, entry.SourcePath) || string.IsNullOrEmpty(entry.FileIdentity))
                throw new CleanupManifestException("The cleanup plan source binding is invalid.");
            if (entry.Kind == CleanupEntryKind.File)
            {
                if (entry.Size < 0 || entry.Sha256.Length != 64 || !entry.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                    throw new CleanupManifestException("The cleanup plan file evidence is invalid.");
                required = checked(required + entry.Size);
            }
            else if (entry.Size != 0 || entry.Sha256.Length != 0)
            {
                throw new CleanupManifestException("The cleanup plan directory evidence is invalid.");
            }
        }
        if (required != plan.RequiredBytes) throw new CleanupManifestException("The cleanup plan required byte count is invalid.");
    }

    private static void ApplyMetadata(string path, CleanupEntryKind kind, CleanupPathMetadata? metadata)
    {
        if (metadata is null) throw new CleanupManifestException("Cleanup metadata is missing.");
        if (kind == CleanupEntryKind.Directory)
        {
            Directory.SetCreationTimeUtc(path, metadata.CreationTimeUtc);
            Directory.SetLastWriteTimeUtc(path, metadata.LastWriteTimeUtc);
            Directory.SetLastAccessTimeUtc(path, metadata.LastAccessTimeUtc);
        }
        else
        {
            File.SetCreationTimeUtc(path, metadata.CreationTimeUtc);
            File.SetLastWriteTimeUtc(path, metadata.LastWriteTimeUtc);
            File.SetLastAccessTimeUtc(path, metadata.LastAccessTimeUtc);
        }
        FileSystemSecurity current = kind == CleanupEntryKind.Directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (current.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) != metadata.SecuritySddl && kind == CleanupEntryKind.Directory)
        {
            DirectorySecurity security = new();
            security.SetSecurityDescriptorSddlForm(metadata.SecuritySddl, AccessControlSections.Access);
            new DirectoryInfo(path).SetAccessControl(security);
        }
        else if (current.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) != metadata.SecuritySddl)
        {
            FileSecurity security = new();
            security.SetSecurityDescriptorSddlForm(metadata.SecuritySddl, AccessControlSections.Access);
            new FileInfo(path).SetAccessControl(security);
        }
        File.SetAttributes(path, metadata.Attributes);
    }

    private static void ApplyTimes(SafeFileHandle handle, CleanupPathMetadata? metadata)
    {
        if (metadata is null) throw new CleanupManifestException("Cleanup metadata is missing.");
        System.Runtime.InteropServices.ComTypes.FILETIME creation = FileTime(metadata.CreationTimeUtc);
        System.Runtime.InteropServices.ComTypes.FILETIME access = FileTime(metadata.LastAccessTimeUtc);
        System.Runtime.InteropServices.ComTypes.FILETIME write = FileTime(metadata.LastWriteTimeUtc);
        if (!SetFileTime(handle, ref creation, ref access, ref write)) throw new IOException("Cleanup timestamps could not be applied through the held identity.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }

    private static System.Runtime.InteropServices.ComTypes.FILETIME FileTime(DateTime value)
    {
        long fileTime = value.ToFileTimeUtc();
        return new System.Runtime.InteropServices.ComTypes.FILETIME { dwLowDateTime = unchecked((int)fileTime), dwHighDateTime = unchecked((int)(fileTime >> 32)) };
    }

    private static void VerifyMetadata(string path, CleanupEntryKind kind, CleanupPathMetadata? expected)
    {
        if (expected is null) throw new CleanupManifestException("Cleanup metadata is missing.");
        FileSystemSecurity security = kind == CleanupEntryKind.Directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        CleanupPathMetadata actual = new(
            File.GetAttributes(path),
            File.GetCreationTimeUtc(path),
            File.GetLastWriteTimeUtc(path),
            File.GetLastAccessTimeUtc(path),
            security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
        if (actual != expected) throw new CleanupManifestException($"Cleanup metadata did not round-trip exactly: attributes={actual.Attributes == expected.Attributes}, creation={actual.CreationTimeUtc == expected.CreationTimeUtc}, write={actual.LastWriteTimeUtc == expected.LastWriteTimeUtc}, access={actual.LastAccessTimeUtc == expected.LastAccessTimeUtc}, security={actual.SecuritySddl == expected.SecuritySddl}.");
    }

    private static bool MetadataMatches(string path, CleanupEntryKind kind, CleanupPathMetadata? expected)
    {
        try
        {
            if (expected is null) return false;
            FileSystemSecurity security = kind == CleanupEntryKind.Directory
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            return File.GetAttributes(path) == expected.Attributes
                && File.GetCreationTimeUtc(path) == expected.CreationTimeUtc
                && File.GetLastWriteTimeUtc(path) == expected.LastWriteTimeUtc
                && security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access) == expected.SecuritySddl;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CleanupManifestException)
        {
            return false;
        }
    }

    internal static bool MetadataMatchesForTests(string path, CleanupEntryKind kind, CleanupPathMetadata? expected)
        => MetadataMatches(path, kind, expected);

    internal static void ApplyMetadataForTests(string path, CleanupEntryKind kind, CleanupPathMetadata? metadata)
        => ApplyMetadata(path, kind, metadata);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileTime(
        SafeFileHandle file,
        ref System.Runtime.InteropServices.ComTypes.FILETIME creationTime,
        ref System.Runtime.InteropServices.ComTypes.FILETIME lastAccessTime,
        ref System.Runtime.InteropServices.ComTypes.FILETIME lastWriteTime);

    private static QuarantineFailure Classify(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return QuarantineFailure.AccessDenied;
        int? nativeError = NativeError(exception);
        if (nativeError == 5) return QuarantineFailure.AccessDenied;
        if (nativeError is 32 or 33) return QuarantineFailure.Locked;
        if (nativeError is 80 or 183) return QuarantineFailure.Conflict;
        if (exception is CleanupPlanException plan)
        {
            if (plan.Message.Contains("identity", StringComparison.OrdinalIgnoreCase)) return QuarantineFailure.IdentityChanged;
            if (plan.Message.Contains("content", StringComparison.OrdinalIgnoreCase)) return QuarantineFailure.HashChanged;
            if (plan.Message.Contains("reparse", StringComparison.OrdinalIgnoreCase)) return QuarantineFailure.ReparsePoint;
        }
        if (exception is FileNotFoundException or DirectoryNotFoundException) return QuarantineFailure.Missing;
        return QuarantineFailure.IoFailure;
    }

    private static int? NativeError(Exception exception)
    {
        if (exception is SecureFileOpenException secure) return secure.NativeError;
        if (exception is System.ComponentModel.Win32Exception native) return native.NativeErrorCode;
        if (((uint)exception.HResult & 0xffff0000u) == 0x80070000u) return exception.HResult & 0xffff;
        return exception.InnerException is null ? null : NativeError(exception.InnerException);
    }

    private static FileAttributes? AttributesOrMissing(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static byte[] ReadBounded(string path, int maximumBytes)
    {
        FileAttributes? attributes = AttributesOrMissing(path);
        if (attributes is null || (attributes.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new CleanupManifestException("A cleanup journal file is missing or unsafe.");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes) throw new CleanupManifestException("A cleanup journal file exceeds its strict reader bound.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private sealed record EntrySnapshot(int SchemaVersion, string HeaderSha256, string QuarantineId,
        string GameRootIdentity, string QuarantineRootIdentity, QuarantineManifestEntry Entry);

    private sealed class EntryStateStore : IDisposable
    {
        internal const int MaximumSnapshotBytes = 512 * 1024;
        private readonly string _headerSha256;
        private readonly QuarantineManifest _header;
        private readonly string _directory;
        private readonly CleanupDirectoryGuard _guard;

        internal EntryStateStore(string walPath, string headerSha256, QuarantineManifest header)
        {
            _headerSha256 = headerSha256;
            _header = header;
            _directory = StateDirectory(walPath, headerSha256);
            _guard = new CleanupDirectoryGuard(Path.GetDirectoryName(walPath)!);
            try { _guard.EnsureDirectory(_directory); }
            catch { _guard.Dispose(); throw; }
        }

        internal static EntrySnapshot Snapshot(string hash, QuarantineManifest header, QuarantineManifestEntry entry)
            => new(1, hash, header.QuarantineId, header.GameRootIdentity, header.QuarantineRootIdentity, entry);

        internal void Persist(QuarantineManifestEntry entry)
        {
            Validate(Snapshot(_headerSha256, _header, entry), _headerSha256, _header);
            string path = Path.Combine(_directory, FileName(entry.Order));
            FileAttributes? existing = AttributesOrMissing(path);
            if (existing is not null && ReadSnapshot(path, _headerSha256, _header).Entry.Order != entry.Order)
                throw new CleanupManifestException("The cleanup entry state filename does not match its order.");
            byte[] bytes = ContractJson.SerializeUtf8(Snapshot(_headerSha256, _header, entry));
            if (bytes.Length > MaximumSnapshotBytes) throw new CleanupManifestException("The cleanup entry state exceeds its strict serialized bound.");
            WriteAtomic(path, bytes);
        }

        internal static QuarantineManifest Read(string walPath, string hash, QuarantineManifest header)
        {
            string parent = Path.Combine(Path.GetDirectoryName(walPath)!, "entry-state");
            FileAttributes? parentAttributes = AttributesOrMissing(parent);
            if (parentAttributes is null) return header;
            if (!parentAttributes.Value.HasFlag(FileAttributes.Directory) || parentAttributes.Value.HasFlag(FileAttributes.ReparsePoint))
                throw new CleanupManifestException("The cleanup entry state directory is unsafe.");
            using SecureCacheRoot heldParent = new(parent, new WindowsSecureFileApi(), false);
            string directory = StateDirectory(walPath, hash);
            FileAttributes? attributes = AttributesOrMissing(directory);
            if (attributes is null) return header;
            if (!attributes.Value.HasFlag(FileAttributes.Directory) || attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                throw new CleanupManifestException("The cleanup entry state namespace is unsafe.");
            using SecureCacheRoot heldDirectory = new(directory, new WindowsSecureFileApi(), false);
            QuarantineManifestEntry[] entries = [.. header.Entries];
            int count = 0;
            bool applied = false;
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++count > entries.Length + 1024) throw new CleanupManifestException("The cleanup entry state count exceeds its bound.");
                FileAttributes item = File.GetAttributes(path);
                if ((item & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    throw new CleanupManifestException("The cleanup entry state contains unsafe topology.");
                string name = Path.GetFileName(path);
                if (IsUnpublished(name, entries.Length)) continue;
                if (name.Length != 13 || !int.TryParse(name.AsSpan(0, 8), out int order)
                    || order < 0 || order >= entries.Length || name != FileName(order))
                    throw new CleanupManifestException("The cleanup entry state contains an unknown file.");
                EntrySnapshot snapshot = ReadSnapshot(path, hash, header);
                if (snapshot.Entry.Order != order) throw new CleanupManifestException("The cleanup entry state filename does not match its order.");
                entries[order] = snapshot.Entry;
                applied = true;
            }
            if (!applied) return header;
            QuarantineTransactionState state = entries.Any(value => value.State == QuarantineEntryState.Failed)
                || entries.All(value => value.State == QuarantineEntryState.Stored)
                ? QuarantineTransactionState.Recoverable : QuarantineTransactionState.InProgress;
            QuarantineManifest merged = header with { State = state, Entries = entries };
            QuarantineManifestCodec.Validate(merged, false);
            return merged;
        }

        private static EntrySnapshot ReadSnapshot(string path, string hash, QuarantineManifest header)
        {
            byte[] bytes = ReadBounded(path, MaximumSnapshotBytes);
            EntrySnapshot snapshot;
            try { snapshot = ContractJson.Deserialize<EntrySnapshot>(bytes); }
            catch (Exception exception) when (exception is ContractFormatException or ArgumentException)
            {
                throw new CleanupManifestException("The cleanup entry state is invalid.");
            }
            if (!bytes.AsSpan().SequenceEqual(ContractJson.SerializeUtf8(snapshot)))
                throw new CleanupManifestException("The cleanup entry state is noncanonical.");
            Validate(snapshot, hash, header);
            return snapshot;
        }

        private static void Validate(EntrySnapshot snapshot, string hash, QuarantineManifest header)
        {
            QuarantineManifestEntry? entry = snapshot.Entry;
            if (snapshot.SchemaVersion != 1 || snapshot.HeaderSha256 != hash || snapshot.QuarantineId != header.QuarantineId
                || snapshot.GameRootIdentity != header.GameRootIdentity || snapshot.QuarantineRootIdentity != header.QuarantineRootIdentity
                || entry is null || entry.Order < 0 || entry.Order >= header.Entries.Length
                || !Enum.IsDefined(entry.State) || !Enum.IsDefined(entry.Failure))
                throw new CleanupManifestException("The cleanup entry state identity is invalid.");
            QuarantineManifestEntry original = header.Entries[entry.Order];
            if (entry with { State = original.State, PostIdentity = original.PostIdentity, Failure = original.Failure, FailureReason = original.FailureReason,
                NativeErrorCode = original.NativeErrorCode, FailureHResult = original.FailureHResult } != original)
                throw new CleanupManifestException("The cleanup entry state changed immutable file evidence.");
            if (entry.State is QuarantineEntryState.Copied or QuarantineEntryState.Published or QuarantineEntryState.Stored && entry.PostIdentity.Length == 0)
                throw new CleanupManifestException("The cleanup entry state is missing its published identity.");
        }

        private static string StateDirectory(string walPath, string hash)
            => Path.Combine(Path.GetDirectoryName(walPath)!, "entry-state", hash);
        private static string FileName(int order) => order.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".json";
        private static bool IsUnpublished(string name, int entryCount)
        {
            if (name.Length != 50 || !int.TryParse(name.AsSpan(0, 8), out int order) || order < 0 || order >= entryCount) return false;
            return name[..13] == FileName(order) && name[13] == '.' && name.EndsWith(".tmp", StringComparison.Ordinal)
                && Guid.TryParseExact(name.AsSpan(14, 32), "N", out _);
        }

        public void Dispose() => _guard.Dispose();
    }

    private static void PersistWal(string path, QuarantineManifest value)
    {
        byte[] bytes = ContractJson.SerializeUtf8(value);
        if (bytes.Length > MaximumTransactionBytes) throw new CleanupManifestException("The cleanup WAL exceeds its strict reader bound.");
        WriteAtomic(path, bytes);
    }

    internal static void WriteAtomic(string path, byte[] bytes, bool replace = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, replace);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record FileEvidence(long Size, string Sha256, string Identity)
    {
        public static FileEvidence Read(string path)
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
            return Read(handle);
        }

        public static FileEvidence Read(SafeFileHandle handle)
        {
            long length = RandomAccess.GetLength(handle);
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[81920];
            long offset = 0;
            while (offset < length)
            {
                int read = RandomAccess.Read(handle, buffer, offset);
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            string sha = Convert.ToHexStringLower(hash.GetHashAndReset());
            return new(length, sha, WindowsCleanupProbe.Identity(handle));
        }
    }
}
