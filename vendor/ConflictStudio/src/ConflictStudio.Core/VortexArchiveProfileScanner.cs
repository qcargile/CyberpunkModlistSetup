using System.Security.Cryptography;
using System.Text;

namespace ConflictStudio.Core;

public static class VortexArchiveProfileScanner
{
    private const int ArchiveFingerprintParallelism = 4;

    public static Mo2ArchiveProfile Scan(VortexManagerContext context, CancellationToken cancellationToken = default)
        => Scan(context, null, cancellationToken);

    public static Mo2ArchiveProfile Scan(VortexManagerContext context, IProgress<ScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        VortexMovedFiles movedFiles = VortexMovedFiles.Capture(context, cancellationToken);
        Mo2ArchiveProfile result = Scan(context, progress, movedFiles, cancellationToken);
        foreach (ProfileInputSnapshot input in movedFiles.Inputs) ProfileInputGuard.RequireUnchanged(input);
        return result;
    }

    internal static Mo2ArchiveProfile Scan(VortexManagerContext context, IProgress<ScanProgress>? progress, VortexMovedFiles? movedFiles, CancellationToken cancellationToken)
        => Scan(context, progress, ArchiveFingerprintParallelism, FileSha256.Fingerprint, movedFiles, cancellationToken);

    internal static Mo2ArchiveProfile Scan(VortexManagerContext context, IProgress<ScanProgress>? progress, int maximumParallelism, Func<string, Action<long>?, CancellationToken, FileSha256Result> fingerprintFile, CancellationToken cancellationToken)
        => Scan(context, progress, maximumParallelism, fingerprintFile, null, cancellationToken);

    private static Mo2ArchiveProfile Scan(VortexManagerContext context, IProgress<ScanProgress>? progress, int maximumParallelism, Func<string, Action<long>?, CancellationToken, FileSha256Result> fingerprintFile, VortexMovedFiles? movedFiles, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fingerprintFile);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumParallelism, 1);
        Dictionary<string, Mo2Archive> effective = new(StringComparer.OrdinalIgnoreCase);
        List<SourceAnalysisFailure> failures = [];
        List<ArchiveCandidate> candidates = [];
        foreach (VortexProviderContext provider in context.Providers.OrderBy(value => value.Order)) DiscoverProvider(candidates, provider.Name, provider.Id, provider.RootPath, context.DeployedWinners, failures);
        DiscoverProvider(candidates, "Game directory", "game-directory", context.GameRoot, context.DeployedWinners, failures);
        if (movedFiles is not null)
        {
            foreach (VortexMovedFile moved in movedFiles.Files.Where(value => IsLegacyArchivePath(value.RelativePath)))
            {
                candidates.Add(new ArchiveCandidate(moved.Provider.Name, Path.GetFileName(moved.RelativePath), moved.PhysicalPath));
            }
        }
        Mo2Archive?[] results = new Mo2Archive?[candidates.Count];
        SourceAnalysisFailure?[] fingerprintFailures = new SourceAnalysisFailure?[candidates.Count];
        ArchiveFingerprintProgress fingerprintProgress = new(progress, candidates.Select(value => value.Path).ToArray());
        Parallel.For(0, candidates.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = maximumParallelism }, index =>
        {
            ArchiveCandidate candidate = candidates[index];
            try
            {
                fingerprintProgress.Start(candidate.Path);
                Mo2Archive archive = Fingerprint(candidate.Provider, candidate.Path, candidate.Name, value => fingerprintProgress.Read(candidate.Path, value), fingerprintFile, cancellationToken);
                results[index] = archive;
                fingerprintProgress.Complete(candidate.Path, archive.Size);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                fingerprintFailures[index] = new SourceAnalysisFailure(candidate.Provider, candidate.Path, "Archive fingerprint", exception.Message);
                fingerprintProgress.Skip(candidate.Path);
            }
        });
        failures.AddRange(fingerprintFailures.Where(value => value is not null).Cast<SourceAnalysisFailure>());
        for (int index = 0; index < candidates.Count; index++)
        {
            if (results[index] is not null) effective[candidates[index].Name] = results[index]!;
        }
        foreach ((string relative, string winnerId) in context.DeployedWinners.Where(value => IsLegacyArchivePath(value.Key)))
        {
            string name = Path.GetFileName(relative);
            if (effective.ContainsKey(name)) continue;
            VortexProviderContext? winner = context.Providers.FirstOrDefault(value => string.Equals(value.Id, winnerId, StringComparison.OrdinalIgnoreCase));
            if (winner is not null && File.Exists(Path.Combine(winner.RootPath, relative))) continue;
            if (winner is not null) failures.Add(new SourceAnalysisFailure(winner.Name, relative, "Archive winner", "The deployed archive winner is absent from the captured provider, so no archive claim was made."));
        }
        Mo2Archive[] filenameOrder = effective.Values.OrderBy(value => value.ArchiveName, StringComparer.Ordinal).ToArray();
        string orderPath = Path.Combine(context.GameRoot, "archive", "pc", "mod", "modlist.txt");
        (string[] order, ArchiveOrderEvidence evidence) = ResolveOrder(context, filenameOrder, orderPath, failures);
        Dictionary<string, Mo2Archive> byName = filenameOrder.ToDictionary(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase);
        Mo2Archive[] ordered = order.Where(byName.ContainsKey).Select(value => byName[value]).Concat(filenameOrder.Where(value => !order.Contains(value.ArchiveName, StringComparer.OrdinalIgnoreCase))).ToArray();
        if (failures.Any(value => value.Surface is "Archive enumeration" or "Archive fingerprint" or "Archive winner")) evidence = new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, evidence.Provider, evidence.SourcePath, "At least one active Vortex archive could not be fingerprinted, so the archive set is incomplete.") { SourcePaths = evidence.SourcePaths, IgnoredEntries = evidence.IgnoredEntries, MissingEntries = evidence.MissingEntries, UnlistedArchives = evidence.UnlistedArchives, DuplicateEntries = evidence.DuplicateEntries, SourceFingerprints = evidence.SourceFingerprints, AbsentSources = evidence.AbsentSources, ProblemLane = ArchiveOrderProblemLane.Legacy, IncompleteArchiveLane = ArchiveOrderProblemLane.Legacy };
        return new Mo2ArchiveProfile(context.ProfileName, orderPath, ordered, order, evidence) { Failures = failures.ToArray() };
    }

    private static void DiscoverProvider(List<ArchiveCandidate> candidates, string providerName, string? providerId, string providerRoot, Dictionary<string, string> winners, List<SourceAnalysisFailure> failures)
    {
        string archiveRoot = Path.Combine(providerRoot, "archive", "pc", "mod");
        if (!Directory.Exists(archiveRoot)) return;
        string[] paths;
        try
        {
            paths = Directory.EnumerateFiles(archiveRoot, "*.archive", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new SourceAnalysisFailure(providerName, archiveRoot, "Archive enumeration", exception.Message));
            return;
        }
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            string relative = "archive\\pc\\mod\\" + name;
            if (!VortexDeploymentFiles.IsEffective(providerId, relative, winners)) continue;
            candidates.Add(new ArchiveCandidate(providerName, name, path));
        }
    }

    private static (string[] Order, ArchiveOrderEvidence Evidence) ResolveOrder(VortexManagerContext context, Mo2Archive[] archives, string orderPath, List<SourceAnalysisFailure> failures)
    {
        string[] discovered = archives.Select(value => value.ArchiveName).ToArray();
        if (!context.DeploymentFresh) return (discovered, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, "Vortex", orderPath, "Archive winners cannot be determined until Vortex deploys the active profile.") { ProblemLane = ArchiveOrderProblemLane.Legacy });
        if (!File.Exists(orderPath)) return (discovered, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.FilenameFallback, "Vortex", null, "No deployed archive modlist.txt exists. Cyberpunk filename order is used."));
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(orderPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(new SourceAnalysisFailure("Vortex", orderPath, "Archive order", exception.Message));
            return (discovered, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, "Vortex", orderPath, "The deployed Vortex archive order file could not be read.") { ProblemLane = ArchiveOrderProblemLane.Legacy });
        }
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Dictionary<string, string> source = new(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(orderPath)] = sha256 };
        if (context.ArchiveOrderSha256 is not null && !string.Equals(context.ArchiveOrderSha256, sha256, StringComparison.OrdinalIgnoreCase)) return (discovered, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, "Vortex", orderPath, "The archive order changed after Vortex exported this profile context. Refresh the Vortex bridge before scanning.") { SourceFingerprints = source, ProblemLane = ArchiveOrderProblemLane.Legacy });
        string[] allEntries = ArchiveOrderText.ArchiveEntries(Encoding.UTF8.GetString(bytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        HashSet<string> active = discovered.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] order = allEntries.Where(active.Contains).ToArray();
        string[] ignored = allEntries.Where(value => !active.Contains(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] missing = discovered.Where(value => !order.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
        string[] duplicates = order.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Where(value => value.Count() > 1).Select(value => value.Key).ToArray();
        string[] effectiveOrder = ArchiveOrderPlanner.CreateRepairOrder(discovered, order);
        if (duplicates.Length == 0)
        {
            string partial = missing.Length == 0 ? string.Empty : $" {missing.Length} active archive{(missing.Length == 1 ? " is" : "s are")} unlisted and load{(missing.Length == 1 ? "s" : string.Empty)} after every listed archive: {string.Join(", ", missing)}.";
            return (effectiveOrder, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.ManagedModlist, "Vortex", orderPath, $"Archive winners use the active Vortex archive load order.{partial}") { IgnoredEntries = ignored, MissingEntries = missing, UnlistedArchives = missing, SourceFingerprints = source });
        }
        else
        {
            return (effectiveOrder, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, "Vortex", orderPath, "The deployed Vortex archive order lists an active archive more than once.") { IgnoredEntries = ignored, MissingEntries = missing, DuplicateEntries = duplicates, SourceFingerprints = source, ProblemLane = ArchiveOrderProblemLane.Legacy });
        }
    }

    private static Mo2Archive Fingerprint(string provider, string path, string archiveName, Action<long> progress, Func<string, Action<long>?, CancellationToken, FileSha256Result> fingerprintFile, CancellationToken cancellationToken)
    {
        FileSha256Result fingerprint = fingerprintFile(path, progress, cancellationToken);
        return new Mo2Archive(provider, archiveName, path, fingerprint.Length, fingerprint.Sha256);
    }

    private static bool IsLegacyArchivePath(string relative)
        => relative.StartsWith("archive\\pc\\mod\\", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(relative), ".archive", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetDirectoryName(relative), "archive\\pc\\mod", StringComparison.OrdinalIgnoreCase);

    private sealed record ArchiveCandidate(string Provider, string Name, string Path);
}
