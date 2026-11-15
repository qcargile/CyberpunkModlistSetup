using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ConflictStudio.Core;

public sealed record EvidenceNote(
    string ProfileName,
    string InstallationId,
    ConflictSurface Surface,
    string Target,
    string[] Providers,
    string EvidenceSha256,
    string Text,
    DateTimeOffset UpdatedAtUtc);

public sealed class EvidenceNoteStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true };
    private readonly string _path;
    private readonly string _mutexName;
    public string? LastRecoveryPath { get; private set; }

    public EvidenceNoteStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "evidence-notes.json");
        _mutexName = "Local\\ConflictStudio.Notes." + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_path).ToUpperInvariant())));
    }

    public EvidenceNote[] Load() => WithLock(() => { LastRecoveryPath = null; return LoadUnlocked(); });

    public EvidenceNote[] SaveMany(string installationId, string profileName, IReadOnlyList<ConflictWorkItem> items, string text, DateTimeOffset updatedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(text);
        if (items.Count == 0) throw new EvidenceNoteException("Select at least one evidence item.");
        if (updatedAtUtc.Offset != TimeSpan.Zero) throw new EvidenceNoteException("The note time must use UTC.");
        return WithLock(() =>
        {
            List<EvidenceNote> notes = LoadUnlocked().ToList();
            foreach (ConflictWorkItem item in items)
            {
                if (item is null) throw new EvidenceNoteException("A selected evidence item is invalid.");
                notes.RemoveAll(value => SameNote(value, installationId, profileName, item) || DisplayedNote(value, item.OpenNote, installationId, profileName, item));
                if (!string.IsNullOrWhiteSpace(text)) notes.Add(new EvidenceNote(profileName, installationId, item.Surface, item.Target, item.Providers, item.EvidenceSha256, text.Trim(), updatedAtUtc));
            }
            SaveUnlocked(notes);
            return notes.ToArray();
        });
    }

    private EvidenceNote[] LoadUnlocked()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            EvidenceNoteDocument document = JsonSerializer.Deserialize<EvidenceNoteDocument>(File.ReadAllText(_path), Options) ?? throw new EvidenceNoteException("The note document is empty.");
            if (document.SchemaVersion != SchemaVersion || document.Notes is null) throw new EvidenceNoteException("The note document has an unsupported schema.");
            foreach (EvidenceNote note in document.Notes) Validate(note);
            return document.Notes;
        }
        catch (Exception exception) when (exception is JsonException or EvidenceNoteException)
        {
            LastRecoveryPath = PreserveInvalidFile();
            return [];
        }
    }

    private void SaveUnlocked(IReadOnlyList<EvidenceNote> notes)
    {
        foreach (EvidenceNote note in notes) Validate(note);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new EvidenceNoteDocument(SchemaVersion, notes.ToArray()), Options));
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private string PreserveInvalidFile()
    {
        string preserved = Path.Combine(Path.GetDirectoryName(_path)!, $"evidence-notes.invalid-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        File.Move(_path, preserved, false);
        return preserved;
    }

    private static bool SameNote(EvidenceNote note, string installationId, string profileName, ConflictWorkItem item)
        => string.Equals(note.InstallationId, installationId, StringComparison.Ordinal)
            && string.Equals(note.ProfileName, profileName, StringComparison.Ordinal)
            && note.Surface == item.Surface
            && string.Equals(note.Target, item.Target, StringComparison.Ordinal)
            && note.Providers.SequenceEqual(item.Providers, StringComparer.OrdinalIgnoreCase);

    private static bool DisplayedNote(EvidenceNote note, EvidenceNote? displayed, string installationId, string profileName, ConflictWorkItem item)
        => displayed is not null
            && note.InstallationId == installationId && note.ProfileName == profileName && note.Surface == item.Surface && note.Target == item.Target
            && note.Providers.SequenceEqual(displayed.Providers, StringComparer.OrdinalIgnoreCase)
            && note.EvidenceSha256 == displayed.EvidenceSha256 && note.UpdatedAtUtc == displayed.UpdatedAtUtc && note.Text == displayed.Text;

    private static void Validate(EvidenceNote note)
    {
        if (note is null || string.IsNullOrWhiteSpace(note.ProfileName) || string.IsNullOrWhiteSpace(note.InstallationId) || string.IsNullOrWhiteSpace(note.Target) || note.Providers is null || note.Providers.Length == 0 || note.Providers.Any(string.IsNullOrWhiteSpace) || string.IsNullOrWhiteSpace(note.Text) || note.UpdatedAtUtc.Offset != TimeSpan.Zero || !IsSha256(note.EvidenceSha256)) throw new EvidenceNoteException("An evidence note is invalid.");
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private T WithLock<T>(Func<T> action)
    {
        using Mutex mutex = new(false, _mutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new EvidenceNoteException("The evidence note file is busy. Try again.");
        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }

    private sealed record EvidenceNoteDocument(int SchemaVersion, EvidenceNote[] Notes);
}

public sealed class EvidenceNoteException(string message) : Exception(message);
