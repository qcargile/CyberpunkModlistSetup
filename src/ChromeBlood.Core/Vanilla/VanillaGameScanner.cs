using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Cleanup;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Vanilla;

public enum VanillaEntryStatus { OfficialUnverified, OfficialVerified, Extra, Missing, Changed, Unreadable, ReparsePoint }

public sealed record VanillaScanEntry(string RelativePath, bool IsDirectory, VanillaEntryStatus Status,
    long? ExpectedSize = null, long? ActualSize = null, string? ExpectedSha1 = null, string? ActualSha1 = null);

public sealed record VanillaScanProgress(int EntriesScanned, long BytesHashed, string CurrentPath);

public sealed record VanillaScanResult(VanillaProfileMatch ProfileMatch, string ReferenceIdentity,
    IReadOnlyList<VanillaScanEntry> Entries, bool HashVerificationRequested, bool Complete)
{
    public string ObservationFingerprint { get; init; } = string.Empty;
}

public sealed class VanillaGameScanner(VanillaGameIndex? index = null)
{
    private readonly VanillaGameIndex _index = index ?? VanillaGameIndex.LoadEmbedded();

    public Task<VanillaScanResult> ScanAsync(string gameRoot, string steamManifestText, bool verifyOfficialHashes = false,
        IProgress<VanillaScanProgress>? progress = null, CancellationToken cancellationToken = default)
        => Task.Run(() => ScanCoreAsync(gameRoot, steamManifestText, verifyOfficialHashes, progress, cancellationToken), cancellationToken);

    private async Task<VanillaScanResult> ScanCoreAsync(string gameRoot, string manifest, bool verify,
        IProgress<VanillaScanProgress>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VanillaProfileMatch profile = _index.MatchSteamManifest(manifest);
        if (!profile.Matched) return new(profile, _index.Identity, Array.Empty<VanillaScanEntry>(), verify, false);
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        WindowsSecureFileApi api = new();
        using SecureCacheRoot heldRoot = new(root, api, false);
        List<VanillaScanEntry> entries = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> blocked = new(StringComparer.OrdinalIgnoreCase);
        SortedDictionary<string, string> observations = new(StringComparer.Ordinal);
        long bytesHashed = 0;
        bool complete = true;
        Stopwatch reportClock = Stopwatch.StartNew();
        await VisitAsync(root, 0).ConfigureAwait(false);
        foreach (VanillaFile file in _index.Files.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Contains(file.RelativePath))
            {
                entries.Add(new(file.RelativePath, false, UnderBlocked(file.RelativePath) ? VanillaEntryStatus.Unreadable : VanillaEntryStatus.Missing, file.Size, null, file.Sha1));
                observations.Add(file.RelativePath.ToUpperInvariant(), "Missing file");
            }
        }
        foreach (string directory in _index.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Contains(directory))
            {
                entries.Add(new(directory, true, UnderBlocked(directory) ? VanillaEntryStatus.Unreadable : VanillaEntryStatus.Missing));
                observations.Add(directory.ToUpperInvariant(), "Missing directory");
            }
        }
        Report(string.Empty, true);
        return new(profile, _index.Identity, entries.AsReadOnly(), verify, complete)
        {
            ObservationFingerprint = complete ? Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(observations))) : string.Empty
        };

        async Task VisitAsync(string directory, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relativeDirectory = Path.GetRelativePath(root, directory);
            if (depth > 64 || entries.Count >= 200000)
            {
                Block(relativeDirectory);
                return;
            }
            try
            {
                using SecureCacheRoot heldDirectory = new(directory, api, false);
                foreach (string child in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entries.Count >= 200000) { Block(relativeDirectory); break; }
                    string relative = Path.GetRelativePath(root, child).Replace('/', '\\');
                    if (!seen.Add(relative)) throw new InvalidDataException("The game directory contains colliding Windows paths.");
                    try
                    {
                        using SafeFileHandle? handle = api.Open(new(child, SecureFileAccess.Attributes, FileShare.Read,
                            SecureFileDisposition.OpenExisting, SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.BackupSemantics));
                        if (handle is null) { Block(relative); continue; }
                        SecureFileMetadata metadata = api.GetMetadata(handle);
                        if (metadata.IsReparsePoint)
                        {
                            entries.Add(new(relative, metadata.IsDirectory, VanillaEntryStatus.ReparsePoint));
                            Block(relative, false);
                            continue;
                        }
                        if (!string.Equals(SecureCacheRoot.NormalizeFinalPath(api.GetFinalPath(handle)), Path.GetFullPath(child), StringComparison.OrdinalIgnoreCase))
                        {
                            Block(relative);
                            continue;
                        }
                        bool officialFile = _index.Files.TryGetValue(relative, out VanillaFile? expected);
                        bool officialDirectory = _index.Directories.Contains(relative);
                        if (officialFile || officialDirectory)
                            observations.Add(relative.ToUpperInvariant(), metadata.IsDirectory ? "Directory"
                                : $"File:{WindowsCleanupProbe.Identity(handle)}:{metadata.Length}:{File.GetLastWriteTimeUtc(child).Ticks}");
                        if (metadata.IsDirectory)
                        {
                            entries.Add(new(relative, true, officialFile ? VanillaEntryStatus.Changed : officialDirectory ? VanillaEntryStatus.OfficialUnverified : VanillaEntryStatus.Extra,
                                expected?.Size, null, expected?.Sha1));
                            await VisitAsync(child, depth + 1).ConfigureAwait(false);
                        }
                        else
                        {
                            VanillaEntryStatus status = officialDirectory ? VanillaEntryStatus.Changed : officialFile
                                ? metadata.Length == expected!.Size ? VanillaEntryStatus.OfficialUnverified : VanillaEntryStatus.Changed
                                : VanillaEntryStatus.Extra;
                            string? actualHash = null;
                            if (officialFile && verify && metadata.Length == expected!.Size)
                            {
                                using SecureFileLease? readable = heldDirectory.TryOpenRead(Path.GetFileName(child));
                                if (readable is null) throw new IOException("The official game file disappeared while scanning.");
                                actualHash = await HashAsync(readable.Handle, metadata.Length, relative).ConfigureAwait(false);
                                bool emptyManifestFile = expected.Size == 0 && expected.Sha1 == "0000000000000000000000000000000000000000";
                                status = emptyManifestFile || actualHash.Equals(expected.Sha1, StringComparison.OrdinalIgnoreCase) ? VanillaEntryStatus.OfficialVerified : VanillaEntryStatus.Changed;
                            }
                            entries.Add(new(relative, false, status, expected?.Size, metadata.Length, expected?.Sha1, actualHash));
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PackageAcquisitionException or Win32Exception)
                    {
                        Block(relative);
                    }
                    Report(relative);
                }
                heldDirectory.RevalidateRoot();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PackageAcquisitionException)
            {
                Block(relativeDirectory);
            }
        }

        async Task<string> HashAsync(SafeFileHandle handle, long length, string relative)
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(1048576);
            try
            {
                long offset = 0;
                while (offset < length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await RandomAccess.ReadAsync(handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), offset, cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new IOException("The official game file changed length during verification.");
                    hash.AppendData(buffer, 0, read);
                    offset += read;
                    bytesHashed += read;
                    Report(relative);
                }
                if (RandomAccess.GetLength(handle) != length) throw new IOException("The official game file changed length during verification.");
                return Convert.ToHexStringLower(hash.GetHashAndReset());
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }

        bool UnderBlocked(string relative)
        {
            if (blocked.Contains(".")) return true;
            string current = relative;
            while (true)
            {
                if (blocked.Contains(current)) return true;
                int separator = current.LastIndexOf('\\');
                if (separator < 0) return false;
                current = current[..separator];
            }
        }

        void Block(string relative, bool addEntry = true)
        {
            complete = false;
            blocked.Add(relative);
            if (addEntry)
            {
                _index.Files.TryGetValue(relative, out VanillaFile? expected);
                entries.Add(new(relative, _index.Directories.Contains(relative), VanillaEntryStatus.Unreadable, expected?.Size, null, expected?.Sha1));
            }
        }

        void Report(string relative, bool force = false)
        {
            if (progress is null || (!force && reportClock.ElapsedMilliseconds < 200)) return;
            progress.Report(new(entries.Count, bytesHashed, relative));
            reportClock.Restart();
        }
    }
}
