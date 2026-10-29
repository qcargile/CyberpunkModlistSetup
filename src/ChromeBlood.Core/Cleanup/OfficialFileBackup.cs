using System.Buffers;
using System.Security.Cryptography;
using ChromeBlood.Contracts.Serialization;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Vanilla;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Cleanup;

internal static class OfficialFileBackup
{
    internal static async Task<string?> PreserveAsync(string gameRoot, string backupRoot,
        IReadOnlyCollection<string> relativePaths, Action<string> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();
        if (relativePaths.Count == 0) return null;
        if (relativePaths.Count > 100000) throw new InvalidDataException("Too many official files were selected for backup.");
        string game = ValidateRoot(gameRoot);
        string backup = ValidateRoot(backupRoot);
        if (CleanupPlanner.SameOrChild(game, backup) || CleanupPlanner.SameOrChild(backup, game))
            throw new InvalidDataException("The official-file backup must be outside the game folder and cannot contain it.");
        if (!CleanupPlanner.PathEquals(Path.GetPathRoot(game)!, Path.GetPathRoot(backup)!))
            throw new InvalidDataException("The official-file backup must be on the game drive.");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        string[] paths = relativePaths.Select(VanillaGameIndex.ValidateRelativePath).ToArray();
        foreach (string path in paths)
            if (!seen.Add(path)) throw new InvalidDataException("The official-file backup contains duplicate paths.");

        WindowsSecureFileApi api = new();
        using SecureCacheRoot heldGame = new(game, api, false);
        using SecureCacheRoot heldBackup = new(backup, api, false);
        string directory = Path.Combine(backup, "official-" + Guid.NewGuid().ToString("N"));
        api.CreateDirectory(directory);
        try
        {
            using SecureCacheRoot heldDirectory = new(directory, api, false);
            using CleanupDirectoryGuard guard = new(directory);
            guard.EnsureDirectory(Path.Combine(directory, "files"));
            guard.EnsureDirectory(Path.Combine(directory, "entries"));
            WriteRecord(heldDirectory, "backup.json", new BackupHeader(1, game, paths, "Incomplete"));
            for (int index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                string relative = paths[index];
                progress($"Backing up changed official file {index + 1} of {paths.Length}: {relative}");
                token.ThrowIfCancellationRequested();
                string source = CleanupPlanner.ResolveUnder(game, relative);
                using SecureCacheRoot sourceParent = new(Path.GetDirectoryName(source)!, api, false);
                using SecureFileLease sourceFile = sourceParent.TryOpenRead(Path.GetFileName(source))
                    ?? throw new IOException($"The official file is missing: {relative}");
                long requiredBytes = checked(sourceFile.Length + 1024 * 1024);
                if (new DriveInfo(Path.GetPathRoot(backup)!).AvailableFreeSpace < requiredBytes)
                    throw new IOException("There is not enough free space to preserve the changed official file.");
                string destination = CleanupPlanner.ResolveUnder(Path.Combine(directory, "files"), relative);
                guard.EnsureParent(destination);
                using SecureCacheRoot destinationParent = new(Path.GetDirectoryName(destination)!, api, false);
                string name = Path.GetFileName(destination);
                using SafeFileHandle partial = destinationParent.CreateAtomicPartial(name + ".partial");
                string sha256 = await CopyAsync(sourceFile.Handle, partial, sourceFile.Length, token).ConfigureAwait(false);
                FileHashResult copied = await new FileHandleHasher().HashAsync(partial, sourceFile.Length, token).ConfigureAwait(false);
                if (copied.Length != sourceFile.Length || copied.Sha256 != sha256)
                    throw new IOException("The official-file backup did not match the source bytes.");
                token.ThrowIfCancellationRequested();
                heldGame.RevalidateRoot();
                heldBackup.RevalidateRoot();
                sourceParent.RevalidateRoot();
                destinationParent.RevalidateRoot();
                api.Rename(partial, destination, false);
                using SecureCacheRoot entries = new(Path.Combine(directory, "entries"), api, false);
                WriteRecord(entries, index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".json",
                    new BackupEntry(relative, sourceFile.Length, sha256, "Complete"));
            }
            token.ThrowIfCancellationRequested();
            WriteRecord(heldDirectory, "complete.json", new BackupCompletion("Complete", paths.Length));
            return directory;
        }
        catch (OperationCanceledException exception)
        {
            throw new OperationCanceledException($"Official-file backup was cancelled. Original files remain in place. Retained backup files: {directory}", exception, token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new IOException($"Official-file backup failed. Original files remain in place. Retained backup files: {directory}", exception);
        }
    }

    private static string ValidateRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("The official-file backup roots must be absolute paths.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private static async Task<string> CopyAsync(SafeFileHandle source, SafeFileHandle destination, long length, CancellationToken token)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                int read = await RandomAccess.ReadAsync(source, buffer.AsMemory(0, (int)Math.Min(81920, length - offset)), offset, token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("The official source file changed while being backed up.");
                await RandomAccess.WriteAsync(destination, buffer.AsMemory(0, read), offset, token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                offset += read;
            }
            RandomAccess.FlushToDisk(destination);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static void WriteRecord<T>(SecureCacheRoot root, string name, T record)
    {
        byte[] bytes = ContractJson.SerializeUtf8(record);
        using SafeFileHandle partial = root.CreateAtomicPartial(name + ".partial");
        RandomAccess.Write(partial, bytes, 0);
        RandomAccess.FlushToDisk(partial);
        root.RevalidateRoot();
        new WindowsSecureFileApi().Rename(partial, Path.Combine(root.Path, name), false);
    }

    private sealed record BackupHeader(int SchemaVersion, string GameRoot, string[] PlannedFiles, string State);
    private sealed record BackupEntry(string RelativePath, long Size, string Sha256, string State);
    private sealed record BackupCompletion(string State, int FileCount);
}
