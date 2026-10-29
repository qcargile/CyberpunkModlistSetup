using System.Security.Cryptography;

namespace ConflictStudio.Core;

public static class ArchiveOrderBackupRestorer
{
    public static void Restore(string backupPath, string targetPath, string expectedCurrentSha256, Action? authorizeCommit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentSha256);
        if (!File.Exists(backupPath) || !File.Exists(targetPath)) throw new ArchiveOrderException("The archive order backup or current file is missing.");
        byte[] current = File.ReadAllBytes(targetPath);
        string currentHash = Convert.ToHexStringLower(SHA256.HashData(current));
        if (!string.Equals(currentHash, expectedCurrentSha256, StringComparison.Ordinal)) throw new ArchiveOrderException("The current archive order changed after the backup was created.");
        byte[] backup = File.ReadAllBytes(backupPath);
        string temporary = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(backup);
                stream.Flush(true);
            }
            byte[] commitCurrent = File.ReadAllBytes(targetPath);
            string commitHash = Convert.ToHexStringLower(SHA256.HashData(commitCurrent));
            if (!string.Equals(commitHash, expectedCurrentSha256, StringComparison.Ordinal)) throw new ArchiveOrderException("The current archive order changed during restore preparation. The concurrent edit was preserved.");
            authorizeCommit?.Invoke();
            File.Move(temporary, targetPath, true);
            if (!File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(backup)) throw new ArchiveOrderException("The restored archive order did not verify.");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}

public static class ArchiveOrderRollback
{
    public static void RestorePrevious(ArchiveOrderApplyResult result, string targetPath, Action? authorizeCommit = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (!File.Exists(targetPath)) throw new ArchiveOrderException("The written archive order is missing; automatic rollback cannot prove the current state.");
        string currentHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(targetPath)));
        if (!string.Equals(currentHash, result.WrittenSha256, StringComparison.Ordinal)) throw new ArchiveOrderException("The written archive order changed before automatic rollback.");
        if (result.TargetPreviouslyExisted)
        {
            if (result.BackupPath is null) throw new ArchiveOrderException("The previous archive order backup is missing.");
            ArchiveOrderBackupRestorer.Restore(result.BackupPath, targetPath, result.WrittenSha256, authorizeCommit);
            return;
        }
        authorizeCommit?.Invoke();
        File.Delete(targetPath);
        if (File.Exists(targetPath)) throw new ArchiveOrderException("The newly created archive order could not be removed during rollback.");
    }
}
