using System.Security.Cryptography;
using System.Diagnostics;

namespace ConflictStudio.Core;

public sealed record ArchiveFingerprint(string Name, long Size, string Sha256);

public sealed record ArchiveOrderObservation(string ProfileId, string DirectoryPath, string? OrderFileSha256, ArchiveFingerprint[] Archives, string[] EffectiveOrder, bool PreserveInactiveArchives = false)
{
    public string OrderFileName { get; internal init; } = "modlist.txt";
    internal string? BackupDirectoryPath { get; init; }
    internal ProfileInputSnapshot? InheritedOrder { get; init; }
}

public sealed record ArchiveOrderPreview(ArchiveOrderObservation Observation, string[] ProposedOrder, string[] ChangedArchives);

public interface IArchiveOrderWriter
{
    ArchiveOrderApplyResult Apply(ArchiveOrderPreview preview, IReadOnlyList<ArchiveFingerprint> currentArchives);
    void RestorePrevious(ArchiveOrderApplyResult result, string targetPath);
}

public static class ArchiveOrderScanner
{
    public static ArchiveOrderObservation Scan(string profileId, string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        string directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory)) throw new ArchiveOrderException("The archive directory does not exist.");

        ArchiveFingerprint[] archives = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".archive", StringComparison.OrdinalIgnoreCase))
            .Select(Fingerprint)
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ToArray();
        string orderPath = Path.Combine(directory, "modlist.txt");
        if (!File.Exists(orderPath)) return new ArchiveOrderObservation(profileId, directory, null, archives, archives.Select(value => value.Name).ToArray());

        byte[] bytes = File.ReadAllBytes(orderPath);
        string[] order = ArchiveOrderText.ArchiveEntries(File.ReadAllLines(orderPath));
        ArchiveOrderPlanner.RequireComplete(archives, order);
        return new ArchiveOrderObservation(profileId, directory, Hash(bytes), archives, order);
    }

    private static ArchiveFingerprint Fingerprint(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return new ArchiveFingerprint(Path.GetFileName(path), stream.Length, Convert.ToHexStringLower(SHA256.HashData(stream)));
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}

public static class ArchiveOrderPlanner
{
    public static string[] CreateRepairOrder(IReadOnlyList<string> discovered, IReadOnlyList<string> current)
    {
        ArgumentNullException.ThrowIfNull(discovered);
        ArgumentNullException.ThrowIfNull(current);
        HashSet<string> active = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> included = new(StringComparer.OrdinalIgnoreCase);
        List<string> repaired = current.Where(value => active.Contains(value) && included.Add(value)).ToList();
        repaired.AddRange(discovered.Where(included.Add));
        return repaired.ToArray();
    }

    public static ArchiveOrderPreview CreatePreview(ArchiveOrderObservation observation, IReadOnlyList<string> proposedOrder)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(proposedOrder);
        string[] proposed = proposedOrder.ToArray();
        RequireComplete(observation.Archives, proposed);
        string[] changed = observation.EffectiveOrder.Where((name, index) => !string.Equals(name, proposed[index], StringComparison.OrdinalIgnoreCase)).ToArray();
        return new ArchiveOrderPreview(observation, proposed, changed);
    }

    internal static void RequireComplete(IReadOnlyList<ArchiveFingerprint> archives, IReadOnlyList<string> order)
    {
        if (archives.Count != order.Count) throw new ArchiveOrderException("The archive order must include every discovered archive exactly once.");
        HashSet<string> expected = archives.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> actual = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in order)
        {
            if (string.IsNullOrWhiteSpace(name) || !expected.Contains(name) || !actual.Add(name)) throw new ArchiveOrderException("The archive order must include every discovered archive exactly once.");
        }
    }
}

public static class ArchiveOrderText
{
    public static string[] ArchiveEntries(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Select(value => value.Trim().TrimStart('\uFEFF')).Where(value => value.EndsWith(".archive", StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    public static byte[] Merge(byte[]? existing, IReadOnlyList<string> proposedOrder, bool preserveInactiveArchives = false)
    {
        ArgumentNullException.ThrowIfNull(proposedOrder);
        if (existing is null) return System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", proposedOrder) + "\r\n");
        string[] lines = System.Text.Encoding.UTF8.GetString(existing).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim().TrimStart('\uFEFF')).Where(value => value.Length > 0).ToArray();
        Queue<string> archives = new(proposedOrder);
        HashSet<string>? active = preserveInactiveArchives ? proposedOrder.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
        HashSet<string>? inactive = preserveInactiveArchives ? new(StringComparer.OrdinalIgnoreCase) : null;
        List<string> merged = [];
        foreach (string line in lines)
        {
            if (line.EndsWith(".archive", StringComparison.OrdinalIgnoreCase))
            {
                if (active is not null && !active.Contains(line))
                {
                    if (inactive!.Add(line)) merged.Add(line);
                }
                else if (archives.Count > 0) merged.Add(archives.Dequeue());
            }
            else
            {
                merged.Add(line);
            }
        }
        merged.AddRange(archives);
        return System.Text.Encoding.UTF8.GetBytes(string.Join("\r\n", merged) + "\r\n");
    }
}

public static class ManagedArchiveOrderObserver
{
    public static ArchiveOrderObservation Observe(Mo2ArchiveProfile profile, Mo2ArchiveWriteTarget target, bool allowIncompleteOrder = false)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(target);
        ArchiveFingerprint[] archives = profile.Archives.Select(value => new ArchiveFingerprint(value.ArchiveName, value.Size, value.Sha256)).ToArray();
        string[] order = profile.EffectiveOrder;
        string? hash = null;
        if (File.Exists(target.ModlistPath))
        {
            byte[] bytes = File.ReadAllBytes(target.ModlistPath);
            hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            HashSet<string> active = archives.Select(value => value.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] managedOrder = ArchiveOrderText.ArchiveEntries(File.ReadAllLines(target.ModlistPath)).Where(active.Contains).ToArray();
            try
            {
                ArchiveOrderPlanner.RequireComplete(archives, managedOrder);
                order = managedOrder;
            }
            catch (ArchiveOrderException) when (allowIncompleteOrder)
            {
                order = profile.EffectiveOrder;
            }
        }
        bool preserveInactiveArchives = target.ManagerKind == ModManagerKind.Mo2 && Mo2ProfileArchiveOrder.IsProfilePath(profile.ProfileModlistPath, target.ModlistPath);
        bool canonicalProfileOrder = preserveInactiveArchives && string.Equals(Path.GetFileName(target.ModlistPath), "archive-order.txt", StringComparison.OrdinalIgnoreCase);
        ProfileInputSnapshot? inheritedOrder = null;
        if (canonicalProfileOrder && hash is null && profile.OrderEvidence?.SourcePath is string sourcePath && string.Equals(Path.GetFileName(sourcePath), "modlist.txt", StringComparison.OrdinalIgnoreCase) && Mo2ProfileArchiveOrder.IsProfilePath(profile.ProfileModlistPath, sourcePath))
        {
            Mo2ProfileArchiveOrder.RequireNoRedirect(sourcePath);
            inheritedOrder = ProfileInputGuard.Capture(sourcePath);
            if (!profile.OrderEvidence.SourceFingerprints.TryGetValue(inheritedOrder.Path, out string? expectedHash) || !string.Equals(inheritedOrder.Sha256, expectedHash, StringComparison.Ordinal)) throw new ArchiveOrderException("The inherited profile archive order changed after the scan.");
        }
        return new ArchiveOrderObservation(profile.ProfileName, Path.GetDirectoryName(target.ModlistPath)!, hash, archives, order, preserveInactiveArchives)
        {
            OrderFileName = Path.GetFileName(target.ModlistPath),
            BackupDirectoryPath = canonicalProfileOrder ? Path.Combine(Path.GetDirectoryName(target.ModlistPath)!, "archive-order", "backups") : null,
            InheritedOrder = inheritedOrder
        };
    }
}

public sealed class ArchiveOrderWriter : IArchiveOrderWriter
{
    private readonly Func<DateTimeOffset> _clock;
    private readonly Func<IReadOnlyList<string>> _runningProcesses;
    private readonly Func<string, byte[]> _readAllBytes;

    public ArchiveOrderWriter(Func<DateTimeOffset> clock) : this(clock, RunningProcesses) { }

    public ArchiveOrderWriter(Func<DateTimeOffset> clock, Func<IReadOnlyList<string>> runningProcesses) : this(clock, runningProcesses, File.ReadAllBytes) { }

    public ArchiveOrderWriter(Func<DateTimeOffset> clock, Func<IReadOnlyList<string>> runningProcesses, Func<string, byte[]> readAllBytes)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _runningProcesses = runningProcesses ?? throw new ArgumentNullException(nameof(runningProcesses));
        _readAllBytes = readAllBytes ?? throw new ArgumentNullException(nameof(readAllBytes));
    }

    public ArchiveOrderApplyResult Apply(ArchiveOrderPreview preview) => Apply(preview, preview.Observation.Archives);

    public ArchiveOrderApplyResult Apply(ArchiveOrderPreview preview, IReadOnlyList<ArchiveFingerprint> currentArchives)
        => Apply(preview, currentArchives, () => currentArchives);

    public ArchiveOrderApplyResult Apply(ArchiveOrderPreview preview, IReadOnlyList<ArchiveFingerprint> currentArchives, Func<IReadOnlyList<ArchiveFingerprint>> currentArchivesAtCommit)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(currentArchives);
        ArgumentNullException.ThrowIfNull(currentArchivesAtCommit);
        if (!SameInventory(preview.Observation.Archives, currentArchives)) throw new ArchiveOrderException("The archive inventory changed after the preview. Scan again before applying.");
        EnsureGameNotRunning();
        string orderPath = Path.Combine(preview.Observation.DirectoryPath, preview.Observation.OrderFileName);
        if (preview.Observation.PreserveInactiveArchives) Mo2ProfileArchiveOrder.RequireNoRedirect(orderPath);
        Directory.CreateDirectory(preview.Observation.DirectoryPath);
        string operationLockPath = orderPath + ".conflictstudio.lock";
        using FileStream operationLock = AcquireOperationLock(operationLockPath);
        EnsureGameNotRunning();
        byte[]? existing = File.Exists(orderPath) ? _readAllBytes(orderPath) : null;
        string? currentHash = existing is null ? null : Hash(existing);
        if (!string.Equals(currentHash, preview.Observation.OrderFileSha256, StringComparison.Ordinal)) throw new ArchiveOrderException("The archive order file changed after the scan.");

        ProfileInputSnapshot? inheritedOrder = preview.Observation.InheritedOrder;
        if (inheritedOrder is not null) ProfileInputGuard.RequireUnchanged(inheritedOrder);
        string? backupPath = existing is null ? null : Backup(orderPath, existing, preview.Observation.BackupDirectoryPath);
        byte[] expected = ArchiveOrderText.Merge(existing ?? inheritedOrder?.Content, preview.ProposedOrder, preview.Observation.PreserveInactiveArchives);
        string temporary = orderPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool replaced = false;
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(expected);
                stream.Flush(true);
            }
            IReadOnlyList<ArchiveFingerprint> commitArchives = currentArchivesAtCommit();
            if (!SameInventory(preview.Observation.Archives, commitArchives)) throw new ArchiveOrderException("The archive inventory changed during archive-order preparation. Scan again before applying.");
            EnsureGameNotRunning();
            byte[]? commitOrder = File.Exists(orderPath) ? _readAllBytes(orderPath) : null;
            string? commitOrderHash = commitOrder is null ? null : Hash(commitOrder);
            if (!string.Equals(commitOrderHash, currentHash, StringComparison.Ordinal)) throw new ArchiveOrderException("The archive order file changed during archive-order preparation. The concurrent edit was preserved.");
            if (preview.Observation.PreserveInactiveArchives) Mo2ProfileArchiveOrder.RequireNoRedirect(orderPath);
            if (inheritedOrder is not null)
            {
                Mo2ProfileArchiveOrder.RequireNoRedirect(inheritedOrder.Path);
                ProfileInputGuard.RequireUnchanged(inheritedOrder);
            }
            File.Move(temporary, orderPath, true);
            replaced = true;
            bool verified = _readAllBytes(orderPath).AsSpan().SequenceEqual(expected);
            if (!verified) throw new ArchiveOrderException("The archive order file did not verify after writing.");
            return new ArchiveOrderApplyResult(backupPath, true, existing is not null, Hash(expected)) { InheritedOrder = inheritedOrder };
        }
        catch (Exception exception) when (replaced)
        {
            byte[]? current = File.Exists(orderPath) ? File.ReadAllBytes(orderPath) : null;
            if (current is null || !current.AsSpan().SequenceEqual(expected)) throw new ArchiveOrderException($"Archive order verification failed and the file changed during verification; automatic rollback was not attempted: {exception.Message}");
            try
            {
                if (existing is null) File.Delete(orderPath);
                else File.WriteAllBytes(orderPath, existing);
                byte[]? restored = File.Exists(orderPath) ? File.ReadAllBytes(orderPath) : null;
                bool restoredExactly = existing is null ? restored is null : restored is not null && restored.AsSpan().SequenceEqual(existing);
                if (!restoredExactly) throw new IOException("The previous archive order did not verify after rollback.");
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException)
            {
                throw new ArchiveOrderException($"Archive order verification failed: {exception.Message} Automatic rollback also failed: {rollbackException.Message}");
            }
            throw new ArchiveOrderException($"Archive order verification failed and the previous file was restored: {exception.Message}");
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public void RestorePrevious(ArchiveOrderApplyResult result, string targetPath)
        => RestorePrevious(result, targetPath, null);

    public void RestorePrevious(ArchiveOrderApplyResult result, string targetPath, Action? authorizeCommit)
    {
        EnsureGameNotRunning();
        string fullTargetPath = Path.GetFullPath(targetPath);
        bool canonicalProfileOrder = string.Equals(Path.GetFileName(fullTargetPath), "archive-order.txt", StringComparison.OrdinalIgnoreCase);
        if (canonicalProfileOrder)
        {
            Mo2ProfileArchiveOrder.RequireNoRedirect(fullTargetPath);
            if (result.BackupPath is not null) Mo2ProfileArchiveOrder.RequireNoRedirect(result.BackupPath);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)!);
        using FileStream operationLock = AcquireOperationLock(fullTargetPath + ".conflictstudio.lock");
        EnsureGameNotRunning();
        ArchiveOrderRollback.RestorePrevious(result, targetPath, () =>
        {
            authorizeCommit?.Invoke();
            EnsureGameNotRunning();
            if (canonicalProfileOrder) Mo2ProfileArchiveOrder.RequireNoRedirect(fullTargetPath);
            if (result.InheritedOrder is not null)
            {
                Mo2ProfileArchiveOrder.RequireNoRedirect(result.InheritedOrder.Path);
                ProfileInputGuard.RequireUnchanged(result.InheritedOrder);
            }
        });
    }

    private void EnsureGameNotRunning()
    {
        string? running = _runningProcesses().FirstOrDefault(name => string.Equals(name, "Cyberpunk2077", StringComparison.OrdinalIgnoreCase));
        if (running is not null) throw new ArchiveOrderException($"Archive order cannot be written while {running} is running.");
    }

    private static FileStream AcquireOperationLock(string path)
    {
        try { return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
        catch (IOException) { throw new ArchiveOrderException("Another archive order operation is already in progress."); }
    }

    private string Backup(string orderPath, byte[] bytes, string? directoryPath)
    {
        string backupBase = directoryPath is null ? orderPath : Path.Combine(directoryPath, Path.GetFileName(orderPath));
        string path = backupBase + "." + _clock().ToUniversalTime().ToString("yyyyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
        if (directoryPath is not null)
        {
            Mo2ProfileArchiveOrder.RequireNoRedirect(path);
            Directory.CreateDirectory(directoryPath);
        }
        if (File.Exists(path)) throw new ArchiveOrderException("The archive order backup path already exists.");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        if (!_readAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            File.Delete(path);
            throw new ArchiveOrderException("The archive order backup did not verify after writing.");
        }
        return path;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static bool SameInventory(ArchiveFingerprint[] observed, IReadOnlyList<ArchiveFingerprint> current)
    {
        if (observed.Length != current.Count) return false;
        Dictionary<string, ArchiveFingerprint> values = current.ToDictionary(value => value.Name, StringComparer.OrdinalIgnoreCase);
        return observed.All(value => values.TryGetValue(value.Name, out ArchiveFingerprint? now) && now.Size == value.Size && string.Equals(now.Sha256, value.Sha256, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> RunningProcesses() => Process.GetProcesses().Select(process => process.ProcessName).ToArray();
}

public sealed record ArchiveOrderApplyResult(string? BackupPath, bool Verified, bool TargetPreviouslyExisted, string WrittenSha256)
{
    internal ProfileInputSnapshot? InheritedOrder { get; init; }
}

public sealed class ArchiveOrderException(string message) : Exception(message);
