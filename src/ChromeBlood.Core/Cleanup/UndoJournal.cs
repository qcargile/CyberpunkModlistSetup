using ChromeBlood.Contracts.Serialization;
using ChromeBlood.Core.Acquisition;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Cleanup;

public enum UndoJournalState { Planned, Executing, Available, Restoring, Restored, Conflict, Failed }

public sealed record UndoJournalEntry(
    string QuarantineId,
    string PlanSha256,
    string ManifestSha256,
    string QuarantineRoot,
    string WalPath,
    string ManifestPath,
    UndoJournalState State,
    long Revision);

internal sealed record UndoJournalDocument(int SchemaVersion, UndoJournalEntry[] Entries);

public sealed class UndoJournalException(string message) : IOException(message);

public sealed class UndoJournal
{
    private const int MaximumBytes = 1024 * 1024;
    private readonly string _path;
    private readonly string _root;
    private readonly string _lockName;
    private readonly int _maximumEntries;

    public UndoJournal(string path, int maximumEntries = 256)
    {
        _path = Path.GetFullPath(path);
        _root = Path.GetDirectoryName(_path) ?? throw new ArgumentException("The undo journal path has no parent.", nameof(path));
        _lockName = Path.GetFileName(_path) + ".lock";
        if (maximumEntries is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries;
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<UndoJournalEntry> Entries => WithLock(() => Read().Entries);

    public void Record(QuarantineUndoReference reference)
    {
        ValidateReference(reference);
        WithLock(() =>
        {
            UndoJournalDocument document = Read();
            if (document.Entries.Any(entry => entry.QuarantineId == reference.QuarantineId || entry.ManifestSha256 == reference.ManifestSha256))
                throw new UndoJournalException("The quarantine reference was already recorded.");
            if (document.Entries.Length >= _maximumEntries) throw new UndoJournalException("The undo journal reached its entry bound.");
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(reference.QuarantineRoot));
            UndoJournalEntry entry = new(reference.QuarantineId, reference.ManifestSha256, reference.ManifestSha256, root, string.Empty, CleanupPlanner.ResolveUnder(root, Path.Combine(reference.QuarantineId, "quarantine.json")), UndoJournalState.Available, 1);
            Write(document with { Entries = [.. document.Entries, entry] });
            return 0;
        });
    }

    internal UndoJournalEntry Reserve(string quarantineId, string planSha256, string quarantineRoot, string walPath, string manifestPath)
    {
        if (!QuarantineManifestCodec.ValidId(quarantineId) || !ValidHash(planSha256)) throw new UndoJournalException("The cleanup reservation identity is invalid.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(quarantineRoot));
        string wal = Path.GetFullPath(walPath);
        string manifest = Path.GetFullPath(manifestPath);
        if (!CleanupPlanner.SameOrChild(wal, root) || !CleanupPlanner.SameOrChild(manifest, root)) throw new UndoJournalException("Cleanup reservation paths escape the quarantine root.");
        return WithLock(() =>
        {
            UndoJournalDocument document = Read();
            if (document.Entries.Any(entry => entry.QuarantineId == quarantineId)) throw new UndoJournalException("The quarantine ID is already reserved.");
            if (document.Entries.Length >= _maximumEntries) throw new UndoJournalException("The undo journal reached its entry bound.");
            UndoJournalEntry entry = new(quarantineId, planSha256, string.Empty, root, wal, manifest, UndoJournalState.Planned, 1);
            Write(document with { Entries = [.. document.Entries, entry] });
            return entry;
        });
    }

    internal UndoJournalEntry RequireReservation(string quarantineId, string planSha256)
        => WithLock(() => Read().Entries.SingleOrDefault(entry => entry.QuarantineId == quarantineId && entry.PlanSha256 == planSha256)
            ?? throw new UndoJournalException("The exact cleanup reservation is missing."));

    public bool AbandonUnstarted(string quarantineId, string planSha256)
    {
        using IDisposable transactionLock = AcquireTransaction(quarantineId);
        return WithLock(() =>
        {
            UndoJournalDocument document = Read();
            UndoJournalEntry entry = document.Entries.SingleOrDefault(value => value.QuarantineId == quarantineId && value.PlanSha256 == planSha256)
                ?? throw new UndoJournalException("The exact cleanup reservation is missing.");
            if (entry.State is not UndoJournalState.Planned and not UndoJournalState.Executing) return false;
            string transactionRoot = CleanupPlanner.ResolveUnder(entry.QuarantineRoot, entry.QuarantineId);
            if (!CleanupPlanner.PathEquals(entry.WalPath, Path.Combine(transactionRoot, "transaction.wal.json"))
                || !CleanupPlanner.PathEquals(entry.ManifestPath, Path.Combine(transactionRoot, "quarantine.json")))
                throw new UndoJournalException("The cleanup reservation does not point to its exact transaction files.");
            using SecureCacheRoot backupRoot = new(entry.QuarantineRoot, new WindowsSecureFileApi(), false);
            if (Attributes(entry.WalPath) is not null || Attributes(entry.ManifestPath) is not null) return false;
            FileAttributes? transactionAttributes = Attributes(transactionRoot);
            if (transactionAttributes is not null)
            {
                if (!transactionAttributes.Value.HasFlag(FileAttributes.Directory) || transactionAttributes.Value.HasFlag(FileAttributes.ReparsePoint)) return false;
                using SecureCacheRoot transaction = new(transactionRoot, new WindowsSecureFileApi(), false);
                string[] children = Directory.GetFileSystemEntries(transactionRoot);
                string files = Path.Combine(transactionRoot, "files");
                if (children.Length > 1 || children.Length == 1 && !CleanupPlanner.PathEquals(children[0], files)) return false;
                if (children.Length == 1)
                {
                    FileAttributes filesAttributes = File.GetAttributes(files);
                    if (!filesAttributes.HasFlag(FileAttributes.Directory) || filesAttributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                    using SecureCacheRoot payload = new(files, new WindowsSecureFileApi(), false);
                    if (Directory.EnumerateFileSystemEntries(files).Any()) return false;
                }
            }
            Write(document with { Entries = document.Entries.Where(value => value.QuarantineId != quarantineId).ToArray() });
            return true;
        });
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void UpdateReservation(string quarantineId, string planSha256, UndoJournalState state, string? manifestSha256 = null)
    {
        WithLock(() =>
        {
            UndoJournalDocument document = Read();
            int index = Array.FindIndex(document.Entries, entry => entry.QuarantineId == quarantineId && entry.PlanSha256 == planSha256);
            if (index < 0) throw new UndoJournalException("The exact cleanup reservation is missing.");
            UndoJournalEntry current = document.Entries[index];
            UndoJournalEntry[] updated = [.. document.Entries];
            updated[index] = current with
            {
                State = state,
                ManifestSha256 = manifestSha256 ?? current.ManifestSha256,
                Revision = checked(current.Revision + 1)
            };
            Write(document with { Entries = updated });
            return 0;
        });
    }

    internal IDisposable AcquireTransaction(string quarantineId)
    {
        if (!QuarantineManifestCodec.ValidId(quarantineId)) throw new UndoJournalException("The quarantine ID is invalid.");
        SecureCacheRoot root = new(_root);
        try
        {
            string lockName = "transaction-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(quarantineId))) + ".lock";
            SafeFileHandle handle = root.OpenLock(lockName);
            return new TransactionLock(root, handle);
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    public QuarantineUndoReference Require(string quarantineId, string manifestSha256)
    {
        return WithLock(() =>
        {
            UndoJournalEntry entry = Read().Entries.SingleOrDefault(value => value.QuarantineId == quarantineId && value.ManifestSha256 == manifestSha256)
                ?? throw new UndoJournalException("The exact quarantine ID and manifest hash are not in the undo journal.");
            return new QuarantineUndoReference(entry.QuarantineId, entry.ManifestSha256, entry.QuarantineRoot);
        });
    }

    public void Update(string quarantineId, string manifestSha256, UndoJournalState state)
    {
        if (!Enum.IsDefined(state)) throw new UndoJournalException("The undo state is invalid.");
        WithLock(() =>
        {
            UndoJournalDocument document = Read();
            int index = Array.FindIndex(document.Entries, entry => entry.QuarantineId == quarantineId && entry.ManifestSha256 == manifestSha256);
            if (index < 0) throw new UndoJournalException("The exact quarantine ID and manifest hash are not in the undo journal.");
            UndoJournalEntry current = document.Entries[index];
            if (current.State == UndoJournalState.Restored && state != UndoJournalState.Restored) throw new UndoJournalException("A restored undo entry is terminal.");
            UndoJournalEntry[] updated = [.. document.Entries];
            updated[index] = current with { State = state, Revision = checked(current.Revision + 1) };
            Write(document with { Entries = updated });
            return 0;
        });
    }

    private T WithLock<T>(Func<T> action)
    {
        using SecureCacheRoot root = new(_root);
        using SafeFileHandle held = root.OpenLock(_lockName);
        return action();
    }

    private UndoJournalDocument Read()
    {
        if (!File.Exists(_path)) return new UndoJournalDocument(1, []);
        FileInfo info = new(_path);
        if (info.Length is <= 0 or > MaximumBytes || info.Attributes.HasFlag(FileAttributes.Directory) || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UndoJournalException("The undo journal topology or size is invalid.");
        byte[] bytes = File.ReadAllBytes(_path);
        UndoJournalDocument document;
        try { document = ContractJson.Deserialize<UndoJournalDocument>(bytes); }
        catch (Exception exception) when (exception is ContractFormatException or ArgumentException)
        {
            throw new UndoJournalException("The undo journal is corrupt.");
        }
        if (!bytes.AsSpan().SequenceEqual(ContractJson.SerializeUtf8(document))) throw new UndoJournalException("The undo journal is not canonical.");
        Validate(document);
        return document;
    }

    private void Write(UndoJournalDocument document)
    {
        Validate(document);
        byte[] bytes = ContractJson.SerializeUtf8(document);
        if (bytes.Length > MaximumBytes) throw new UndoJournalException("The undo journal exceeds its size bound.");
        QuarantineTransaction.WriteAtomic(_path, bytes);
    }

    private void Validate(UndoJournalDocument document)
    {
        if (document.SchemaVersion != 1 || document.Entries.Length > _maximumEntries) throw new UndoJournalException("The undo journal schema or count is invalid.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        HashSet<string> hashes = new(StringComparer.Ordinal);
        foreach (UndoJournalEntry entry in document.Entries)
        {
            if (entry is null) throw new UndoJournalException("The undo journal contains a null entry.");
            bool manifestHashValid = entry.ManifestSha256.Length == 0 && entry.State is UndoJournalState.Planned or UndoJournalState.Executing
                || ValidHash(entry.ManifestSha256);
            if (!QuarantineManifestCodec.ValidId(entry.QuarantineId)
                || !ValidHash(entry.PlanSha256)
                || !manifestHashValid
                || !Path.IsPathFullyQualified(entry.QuarantineRoot)
                || entry.WalPath.Length > 0 && !CleanupPlanner.SameOrChild(entry.WalPath, entry.QuarantineRoot)
                || !CleanupPlanner.SameOrChild(entry.ManifestPath, entry.QuarantineRoot)
                || !ids.Add(entry.QuarantineId)
                || entry.ManifestSha256.Length > 0 && !hashes.Add(entry.ManifestSha256)
                || !Enum.IsDefined(entry.State)
                || entry.Revision < 1)
                throw new UndoJournalException("The undo journal contains a duplicate or invalid entry.");
        }
    }

    private static void ValidateReference(QuarantineUndoReference reference)
    {
        if (!QuarantineManifestCodec.ValidId(reference.QuarantineId)
            || reference.ManifestSha256.Length != 64
            || !reference.ManifestSha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            || !Path.IsPathFullyQualified(reference.QuarantineRoot))
            throw new UndoJournalException("The quarantine undo reference is invalid.");
    }

    private static bool ValidHash(string value)
        => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed class TransactionLock(SecureCacheRoot root, SafeFileHandle handle) : IDisposable
    {
        public void Dispose()
        {
            handle.Dispose();
            root.Dispose();
        }
    }
}
