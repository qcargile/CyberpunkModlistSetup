using System.Security.Cryptography;

namespace ConflictStudio.Core;

public static class ManualArchiveProfileScanner
{
    public static Mo2ArchiveProfile Scan(string gameRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        string root = Path.GetFullPath(gameRoot);
        string archiveRoot = Path.Combine(root, "archive", "pc", "mod");
        string orderPath = Path.Combine(archiveRoot, "modlist.txt");
        if (!Directory.Exists(archiveRoot)) return new Mo2ArchiveProfile("Deployed game", orderPath, [], [], new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.FilenameFallback, "Game directory", null, "No deployed legacy archive folder exists. Cyberpunk has no legacy archive order to manage.") { AbsentSources = [orderPath] });
        List<SourceAnalysisFailure> failures = [];
        string[] archivePaths;
        try
        {
            archivePaths = Directory.EnumerateFiles(archiveRoot, "*.archive", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            archivePaths = [];
            failures.Add(new SourceAnalysisFailure("Game directory", archiveRoot, "Archive enumeration", exception.Message));
        }
        List<Mo2Archive> readable = [];
        foreach (string path in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                FileSha256Result fingerprint = FileSha256.Fingerprint(path, cancellationToken: cancellationToken);
                readable.Add(new Mo2Archive("Game directory", Path.GetFileName(path), path, fingerprint.Length, fingerprint.Sha256));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new SourceAnalysisFailure("Game directory", path, "Archive fingerprint", exception.Message));
            }
        }
        Mo2Archive[] archives = readable.ToArray();
        string[] discovered = archives.Select(value => value.ArchiveName).ToArray();
        if (!File.Exists(orderPath)) return Profile(archives, discovered, orderPath, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.FilenameFallback, "Game directory", null, "No deployed archive modlist.txt exists. Cyberpunk filename order is used.") { AbsentSources = [orderPath] }, failures);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(orderPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new SourceAnalysisFailure("Game directory", orderPath, "Archive order", exception.Message));
            ArchiveOrderEvidence unreadable = new(ArchiveOrderEvidenceKind.Unresolved, "Game directory", orderPath, "The deployed archive order file could not be read.") { ProblemLane = ArchiveOrderProblemLane.Legacy };
            return Profile(archives, discovered, orderPath, unreadable, failures);
        }
        HashSet<string> active = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] allEntries = ArchiveOrderText.ArchiveEntries(System.Text.Encoding.UTF8.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        string[] order = allEntries.Where(active.Contains).ToArray();
        string[] ignored = allEntries.Where(value => !active.Contains(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] missing = discovered.Where(value => !order.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
        string[] duplicates = order.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Where(value => value.Count() > 1).Select(value => value.Key).ToArray();
        Dictionary<string, string> source = new(StringComparer.OrdinalIgnoreCase) { [orderPath] = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
        string[] effectiveOrder = ArchiveOrderPlanner.CreateRepairOrder(discovered, order);
        if (duplicates.Length == 0)
        {
            string partial = missing.Length == 0 ? string.Empty : $" {missing.Length} active archive{(missing.Length == 1 ? " is" : "s are")} unlisted and load{(missing.Length == 1 ? "s" : string.Empty)} after every listed archive: {string.Join(", ", missing)}.";
            return Profile(archives, effectiveOrder, orderPath, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.ManagedModlist, "Game directory", orderPath, $"Archive winners use the deployed archive modlist.txt.{partial}") { IgnoredEntries = ignored, MissingEntries = missing, UnlistedArchives = missing, SourceFingerprints = source }, failures);
        }
        else
        {
            List<string> reasons = [];
            if (missing.Length > 0) reasons.Add($"missing active archives: {string.Join(", ", missing)}");
            if (duplicates.Length > 0) reasons.Add($"duplicate active archives: {string.Join(", ", duplicates)}");
            if (ignored.Length > 0) reasons.Add($"inactive entries ignored: {string.Join(", ", ignored)}");
            return Profile(archives, effectiveOrder, orderPath, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, "Game directory", orderPath, $"Archive winners cannot be determined because the deployed modlist.txt has {string.Join("; ", reasons)}.") { IgnoredEntries = ignored, MissingEntries = missing, DuplicateEntries = duplicates, SourceFingerprints = source, ProblemLane = ArchiveOrderProblemLane.Legacy }, failures);
        }
    }

    private static Mo2ArchiveProfile Profile(Mo2Archive[] archives, string[] order, string orderPath, ArchiveOrderEvidence evidence, List<SourceAnalysisFailure> failures)
    {
        if (failures.Any(value => value.Surface is "Archive enumeration" or "Archive fingerprint")) evidence = new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, evidence.Provider, evidence.SourcePath, "At least one deployed legacy archive could not be fingerprinted, so the archive set is incomplete.") { SourcePaths = evidence.SourcePaths, IgnoredEntries = evidence.IgnoredEntries, MissingEntries = evidence.MissingEntries, UnlistedArchives = evidence.UnlistedArchives, DuplicateEntries = evidence.DuplicateEntries, SourceFingerprints = evidence.SourceFingerprints, AbsentSources = evidence.AbsentSources, ProblemLane = ArchiveOrderProblemLane.Legacy, IncompleteArchiveLane = ArchiveOrderProblemLane.Legacy };
        return new Mo2ArchiveProfile("Deployed game", orderPath, archives, order, evidence) { Failures = failures.ToArray() };
    }
}
