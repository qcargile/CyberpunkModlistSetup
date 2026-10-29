using System.Security.Cryptography;
using System.Text.Json;

namespace ConflictStudio.Core;

public sealed record RedmodArchiveProfile(Mo2Archive[] Archives, string[] EffectiveOrder, ArchiveOrderEvidence OrderEvidence, SourceAnalysisFailure[] Failures);

public static class RedmodArchiveProfileScanner
{
    private const int ArchiveFingerprintParallelism = 4;
    private const string OrderRelativePath = "r6\\cache\\modded\\MO_REDmod_load_order.txt";

    public static RedmodArchiveProfile Scan(IReadOnlyList<DeploymentProvider> providers, CancellationToken cancellationToken = default)
        => Scan(providers, null, null, null, cancellationToken);

    public static RedmodArchiveProfile Scan(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, CancellationToken cancellationToken = default)
        => Scan(providers, deployedWinners, null, null, cancellationToken);

    public static RedmodArchiveProfile Scan(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, IProgress<ScanProgress>? progress, CancellationToken cancellationToken = default)
        => Scan(providers, deployedWinners, progress, null, cancellationToken);

    public static RedmodArchiveProfile Scan(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, IProgress<ScanProgress>? progress, VortexMovedFiles? movedFiles, CancellationToken cancellationToken = default)
        => Scan(providers, deployedWinners, progress, ArchiveFingerprintParallelism, FileSha256.Fingerprint, CandidateDirectories, movedFiles, cancellationToken);

    internal static RedmodArchiveProfile Scan(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, IProgress<ScanProgress>? progress, int maximumParallelism, Func<string, Action<long>?, CancellationToken, FileSha256Result> fingerprintFile, Func<DeploymentProvider, CancellationToken, string[]> candidateDirectories, CancellationToken cancellationToken)
        => Scan(providers, deployedWinners, progress, maximumParallelism, fingerprintFile, candidateDirectories, null, cancellationToken);

    internal static RedmodArchiveProfile Scan(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, IProgress<ScanProgress>? progress, int maximumParallelism, Func<string, Action<long>?, CancellationToken, FileSha256Result> fingerprintFile, Func<DeploymentProvider, CancellationToken, string[]> candidateDirectories, VortexMovedFiles? movedFiles, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(fingerprintFile);
        ArgumentNullException.ThrowIfNull(candidateDirectories);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumParallelism, 1);
        Dictionary<string, RedmodCandidate> candidates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<RedmodDirectory>> folderContributions = new(StringComparer.OrdinalIgnoreCase);
        List<SourceAnalysisFailure> failures = [];
        bool incompleteInput = false;
        for (int providerIndex = 0; providerIndex < providers.Count; providerIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DeploymentProvider provider = providers[providerIndex];
            progress?.Report(new ScanProgress("deployment · finding REDmods", providerIndex, providers.Count, provider.Name));
            string[] directories = [];
            try
            {
                directories = candidateDirectories(provider, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new SourceAnalysisFailure(provider.Name, Path.Combine(provider.RootPath, "mods"), "REDmod enumeration", exception.Message));
                incompleteInput = true;
            }
            foreach (string directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string folder = Path.GetFileName(directory);
                if (!folderContributions.TryGetValue(folder, out List<RedmodDirectory>? entries)) folderContributions[folder] = entries = [];
                string fullPath = Path.GetFullPath(directory);
                if (!entries.Any(value => string.Equals(value.Path, fullPath, StringComparison.OrdinalIgnoreCase))) entries.Add(new RedmodDirectory(provider, fullPath));
            }
            progress?.Report(new ScanProgress("deployment · finding REDmods", providerIndex + 1, providers.Count, provider.Name));
        }
        if (deployedWinners is not null)
        {
            foreach ((string relative, string winnerId) in deployedWinners.Where(value => RedmodFolder(value.Key) is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeploymentProvider? winner = providers.FirstOrDefault(value => string.Equals(value.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
                string? folder = RedmodFolder(relative);
                if (winner is null || folder is null) continue;
                if (!folderContributions.TryGetValue(folder, out List<RedmodDirectory>? entries)) folderContributions[folder] = entries = [];
                string path = Path.GetFullPath(Path.Combine(winner.RootPath, "mods", folder));
                if (!entries.Any(value => string.Equals(value.Path, path, StringComparison.OrdinalIgnoreCase))) entries.Insert(0, new RedmodDirectory(winner, path));
            }
        }
        int folderIndex = 0;
        foreach ((string folder, List<RedmodDirectory> directories) in folderContributions)
        {
            progress?.Report(new ScanProgress("deployment · reading REDmod descriptors", folderIndex++, folderContributions.Count, folder));
            cancellationToken.ThrowIfCancellationRequested();
            RedmodDirectory? descriptor = null;
            bool descriptorRejected = false;
            foreach (RedmodDirectory directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relativeInfo = Path.Combine("mods", folder, "info.json");
                if (!IsVisible(directory.Provider, relativeInfo, deployedWinners)) continue;
                string infoPath = movedFiles?.Resolve(Path.Combine(directory.Path, "info.json")) ?? Path.Combine(directory.Path, "info.json");
                try
                {
                    RequireValidInfo(infoPath);
                    descriptor = directory;
                    break;
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    if (deployedWinners?.ContainsKey(relativeInfo) == true)
                    {
                        failures.Add(new SourceAnalysisFailure(directory.Provider.Name, relativeInfo, "REDmod descriptor winner", "The deployed REDmod descriptor winner is absent from the captured provider."));
                        incompleteInput = true;
                        descriptorRejected = true;
                        break;
                    }
                    continue;
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException)
                {
                    failures.Add(new SourceAnalysisFailure(directory.Provider.Name, relativeInfo, "REDmod", $"This folder will not load as a REDmod because its info.json descriptor is invalid: {exception.Message}"));
                    descriptorRejected = true;
                    break;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new SourceAnalysisFailure(directory.Provider.Name, relativeInfo, "REDmod", exception.Message));
                    incompleteInput = true;
                    descriptorRejected = true;
                    break;
                }
            }
            if (descriptor is null)
            {
                if (!descriptorRejected) failures.Add(new SourceAnalysisFailure(directories[0].Provider.Name, Path.Combine("mods", folder, "info.json"), "REDmod", "This folder will not load as a REDmod because its required info.json descriptor is missing."));
                continue;
            }

            Dictionary<string, RedmodArchiveFile> archiveFiles = new(StringComparer.OrdinalIgnoreCase);
            foreach (RedmodDirectory directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string archivesRoot = Path.Combine(directory.Path, "archives");
                try
                {
                    List<(string Name, string Path)> archivePaths = [];
                    if (Directory.Exists(archivesRoot)) foreach (string path in Directory.EnumerateFiles(archivesRoot, "*.archive", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        archivePaths.Add((Path.GetFileName(path), path));
                    }
                    if (movedFiles is not null) foreach (VortexMovedFile moved in movedFiles.InDirectory(archivesRoot))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (string.Equals(directory.Provider.ManagerId, moved.Provider.Id, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(Path.GetExtension(moved.SourcePath), ".archive", StringComparison.OrdinalIgnoreCase))
                            archivePaths.Add((Path.GetFileName(moved.SourcePath), moved.PhysicalPath));
                    }
                    archivePaths.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
                    foreach ((string name, string path) in archivePaths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string relative = Path.Combine("mods", folder, "archives", name);
                        if (IsVisible(directory.Provider, relative, deployedWinners)) archiveFiles.TryAdd(name, new RedmodArchiveFile(directory.Provider.Name, path));
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add(new SourceAnalysisFailure(directory.Provider.Name, archivesRoot, "REDmod archive enumeration", exception.Message));
                    incompleteInput = true;
                }
            }
            if (deployedWinners is not null)
            {
                string prefix = Path.Combine("mods", folder, "archives") + Path.DirectorySeparatorChar;
                foreach ((string relative, string winnerId) in deployedWinners.Where(value => value.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(value.Key), ".archive", StringComparison.OrdinalIgnoreCase)))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string name = Path.GetFileName(relative);
                    if (archiveFiles.ContainsKey(name)) continue;
                    DeploymentProvider? winner = providers.FirstOrDefault(value => string.Equals(value.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
                    if (winner is not null)
                    {
                        failures.Add(new SourceAnalysisFailure(winner.Name, relative, "REDmod archive winner", "The deployed REDmod archive winner is absent from the captured provider."));
                        incompleteInput = true;
                    }
                }
            }
            candidates.Add(folder, new RedmodCandidate(archiveFiles.OrderBy(value => value.Key, StringComparer.Ordinal).Select(value => value.Value).ToArray()));
        }
        progress?.Report(new ScanProgress("deployment · reading REDmod descriptors", folderContributions.Count, folderContributions.Count));

        (string[] orderedFolders, ArchiveOrderEvidence evidence) = ResolveOrder(providers, candidates.Keys.ToArray(), deployedWinners, failures, movedFiles, cancellationToken);
        List<(string Folder, RedmodArchiveFile Archive)> fingerprintWork = [];
        foreach (string folder in orderedFolders)
        {
            if (!candidates.TryGetValue(folder, out RedmodCandidate? candidate)) continue;
            fingerprintWork.AddRange(candidate.Archives.Select(archive => (folder, archive)));
        }
        Mo2Archive?[] fingerprintResults = new Mo2Archive?[fingerprintWork.Count];
        SourceAnalysisFailure?[] fingerprintFailures = new SourceAnalysisFailure?[fingerprintWork.Count];
        ArchiveFingerprintProgress fingerprintProgress = new(progress, fingerprintWork.Select(value => value.Archive.Path).ToArray(), "deployment · indexing REDmods");
        Parallel.For(0, fingerprintWork.Count, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = maximumParallelism }, index =>
        {
            (string folder, RedmodArchiveFile archive) = fingerprintWork[index];
            fingerprintProgress.Start(archive.Path);
            try
            {
                string logicalName = $"REDmod/{folder}/{Path.GetFileName(archive.Path)}";
                FileSha256Result fingerprint = fingerprintFile(archive.Path, value => fingerprintProgress.Read(archive.Path, value), cancellationToken);
                fingerprintResults[index] = new Mo2Archive(archive.Provider, logicalName, archive.Path, fingerprint.Length, fingerprint.Sha256, LogicalProvider: $"REDmod: {folder}");
                fingerprintProgress.Complete(archive.Path, fingerprint.Length);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                fingerprintFailures[index] = new SourceAnalysisFailure(archive.Provider, archive.Path, "REDmod archive fingerprint", exception.Message);
                fingerprintProgress.Skip(archive.Path);
            }
        });
        List<Mo2Archive> archives = fingerprintResults.Where(value => value is not null).Cast<Mo2Archive>().ToList();
        SourceAnalysisFailure[] failedFingerprints = fingerprintFailures.Where(value => value is not null).Cast<SourceAnalysisFailure>().ToArray();
        failures.AddRange(failedFingerprints);
        incompleteInput |= failedFingerprints.Length > 0;
        if (incompleteInput) evidence = new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, evidence.Provider, evidence.SourcePath, "At least one active REDmod input could not be read, so the REDmod archive set is incomplete.")
        {
            SourcePaths = evidence.SourcePaths,
            IgnoredEntries = evidence.IgnoredEntries,
            MissingEntries = evidence.MissingEntries,
            UnlistedArchives = evidence.UnlistedArchives,
            DuplicateEntries = evidence.DuplicateEntries,
            SourceFingerprints = evidence.SourceFingerprints,
            AbsentSources = evidence.AbsentSources,
            ProblemLane = ArchiveOrderProblemLane.Redmod,
            IncompleteArchiveLane = ArchiveOrderProblemLane.Redmod
        };
        return new RedmodArchiveProfile(archives.ToArray(), archives.Select(value => value.ArchiveName).ToArray(), evidence, failures.ToArray());
    }

    private static string[] CandidateDirectories(DeploymentProvider provider, CancellationToken cancellationToken)
    {
        string modsRoot = Path.Combine(provider.RootPath, "mods");
        if (Directory.Exists(modsRoot))
        {
            List<string> directories = [];
            foreach (string directory in Directory.EnumerateDirectories(modsRoot))
            {
                cancellationToken.ThrowIfCancellationRequested();
                directories.Add(directory);
            }
            directories.Sort((left, right) => StringComparer.Ordinal.Compare(Path.GetFileName(left), Path.GetFileName(right)));
            return directories.ToArray();
        }
        return File.Exists(Path.Combine(provider.RootPath, "info.json")) ? [provider.RootPath] : [];
    }

    private static bool IsVisible(DeploymentProvider provider, string relativePath, IReadOnlyDictionary<string, string>? deployedWinners)
        => VortexDeploymentFiles.IsEffective(provider.ManagerId, relativePath, deployedWinners);

    private static string? RedmodFolder(string relativePath)
    {
        string[] parts = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 && string.Equals(parts[0], "mods", StringComparison.OrdinalIgnoreCase) && (string.Equals(parts[2], "info.json", StringComparison.OrdinalIgnoreCase) || string.Equals(parts[2], "archives", StringComparison.OrdinalIgnoreCase)) ? parts[1] : null;
    }

    private static void RequireValidInfo(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())
            || !document.RootElement.TryGetProperty("version", out JsonElement version) || version.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(version.GetString())) throw new InvalidDataException("The REDmod info.json descriptor must contain non-empty string name and version fields.");
    }

    private static (string[] Folders, ArchiveOrderEvidence Evidence) ResolveOrder(IReadOnlyList<DeploymentProvider> providers, string[] folders, IReadOnlyDictionary<string, string>? deployedWinners, List<SourceAnalysisFailure> failures, VortexMovedFiles? movedFiles, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] alphabetical = folders.ToArray();
        Array.Sort(alphabetical, StringComparer.Ordinal);
        List<string> absentSources = [];
        foreach (DeploymentProvider provider in providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = movedFiles?.Resolve(Path.Combine(provider.RootPath, OrderRelativePath)) ?? Path.Combine(provider.RootPath, OrderRelativePath);
            if (!VortexDeploymentFiles.IsEffective(provider.ManagerId, OrderRelativePath, deployedWinners)) continue;
            if (!File.Exists(path))
            {
                absentSources.Add(Path.GetFullPath(path));
                continue;
            }
            byte[] orderBytes;
            try
            {
                orderBytes = File.ReadAllBytes(path);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new SourceAnalysisFailure(provider.Name, path, "REDmod order", exception.Message));
                return (alphabetical, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, provider.Name, path, "The active REDmod order file could not be read.") { AbsentSources = absentSources.ToArray(), ProblemLane = ArchiveOrderProblemLane.Redmod });
            }
            string[] entries = System.Text.Encoding.UTF8.GetString(orderBytes).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(value => value.Trim().TrimStart('\uFEFF')).Where(value => value.Length > 0).ToArray();
            Dictionary<string, string> source = new(StringComparer.OrdinalIgnoreCase) { [Path.GetFullPath(path)] = Convert.ToHexStringLower(SHA256.HashData(orderBytes)) };
            HashSet<string> active = alphabetical.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string[] order = entries.Where(active.Contains).ToArray();
            string[] ignored = entries.Where(value => !active.Contains(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string[] missing = alphabetical.Where(value => !order.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
            string[] duplicates = order.GroupBy(value => value, StringComparer.OrdinalIgnoreCase).Where(value => value.Count() > 1).Select(value => value.Key).ToArray();
            if (missing.Length == 0 && duplicates.Length == 0) return (order, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.ManagedModlist, provider.Name, path, $"REDmod order comes from the active {provider.Name} MO_REDmod_load_order.txt.") { SourceFingerprints = source, AbsentSources = absentSources.ToArray(), IgnoredEntries = ignored });
            return (alphabetical, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, provider.Name, path, "The active REDmod order file is stale or incomplete.") { SourceFingerprints = source, AbsentSources = absentSources.ToArray(), IgnoredEntries = ignored, MissingEntries = missing, DuplicateEntries = duplicates, ProblemLane = ArchiveOrderProblemLane.Redmod });
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (deployedWinners is not null && deployedWinners.TryGetValue(OrderRelativePath, out string? winnerId))
        {
            DeploymentProvider? winner = providers.FirstOrDefault(value => string.Equals(value.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
            if (winner is not null)
            {
                string path = Path.Combine(winner.RootPath, OrderRelativePath);
                failures.Add(new SourceAnalysisFailure(winner.Name, path, "REDmod order", "The deployed REDmod order winner is absent from the captured provider."));
                return (alphabetical, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, winner.Name, path, "The deployed REDmod order winner is unavailable.") { AbsentSources = absentSources.ToArray(), ProblemLane = ArchiveOrderProblemLane.Redmod });
            }
        }
        return (alphabetical, new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.FilenameFallback, null, null, "No active REDmod order file exists. Documented ASCII folder order is used.") { AbsentSources = absentSources.ToArray() });
    }

    private sealed record RedmodDirectory(DeploymentProvider Provider, string Path);
    private sealed record RedmodArchiveFile(string Provider, string Path);
    private sealed record RedmodCandidate(RedmodArchiveFile[] Archives);
}

public static class PackedArchiveTopology
{
    public static Mo2ArchiveProfile Compose(Mo2ArchiveProfile legacy, RedmodArchiveProfile redmods)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        ArgumentNullException.ThrowIfNull(redmods);
        ArchiveOrderEvidence legacyEvidence = legacy.OrderEvidence ?? new ArchiveOrderEvidence(ArchiveOrderEvidenceKind.Unresolved, null, null, "Legacy archive order evidence is unavailable.");
        ArchiveOrderEvidenceKind kind = legacyEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved || redmods.OrderEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved
            ? ArchiveOrderEvidenceKind.Unresolved
            : legacyEvidence.Kind == ArchiveOrderEvidenceKind.ManagedModlist || redmods.OrderEvidence.Kind == ArchiveOrderEvidenceKind.ManagedModlist
                ? ArchiveOrderEvidenceKind.ManagedModlist
                : ArchiveOrderEvidenceKind.FilenameFallback;
        ArchiveOrderEvidence primary = legacyEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved ? legacyEvidence : redmods.OrderEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved ? redmods.OrderEvidence : legacyEvidence.Kind == ArchiveOrderEvidenceKind.ManagedModlist ? legacyEvidence : redmods.OrderEvidence.Kind == ArchiveOrderEvidenceKind.ManagedModlist ? redmods.OrderEvidence : legacyEvidence;
        string[] sources = legacyEvidence.SourcePaths.Concat(redmods.OrderEvidence.SourcePaths).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        string[] providers = new[] { legacyEvidence.Provider, redmods.OrderEvidence.Provider }.Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ArchiveOrderProblemLane problemLane = legacyEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved && redmods.OrderEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved ? ArchiveOrderProblemLane.Combined : legacyEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved ? ArchiveOrderProblemLane.Legacy : redmods.OrderEvidence.Kind == ArchiveOrderEvidenceKind.Unresolved ? ArchiveOrderProblemLane.Redmod : ArchiveOrderProblemLane.None;
        ArchiveOrderEvidence evidence = new(kind, providers.Length == 0 ? null : string.Join(" + ", providers), primary.SourcePath, $"{legacyEvidence.Message} Legacy archives load before REDmods. {redmods.OrderEvidence.Message}")
        {
            SourcePaths = sources,
            IgnoredEntries = legacyEvidence.IgnoredEntries,
            PreserveInactiveArchives = legacyEvidence.PreserveInactiveArchives,
            MissingEntries = legacyEvidence.MissingEntries.Concat(redmods.OrderEvidence.MissingEntries).ToArray(),
            UnlistedArchives = legacyEvidence.UnlistedArchives,
            DuplicateEntries = legacyEvidence.DuplicateEntries.Concat(redmods.OrderEvidence.DuplicateEntries).ToArray(),
            SourceFingerprints = legacyEvidence.SourceFingerprints.Concat(redmods.OrderEvidence.SourceFingerprints).ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase),
            AbsentSources = legacyEvidence.AbsentSources.Concat(redmods.OrderEvidence.AbsentSources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            ProblemLane = problemLane,
            IncompleteArchiveLane = CombineLanes(legacyEvidence.IncompleteArchiveLane, redmods.OrderEvidence.IncompleteArchiveLane)
        };
        return new Mo2ArchiveProfile(legacy.ProfileName, legacy.ProfileModlistPath, [.. legacy.Archives, .. redmods.Archives], [.. legacy.EffectiveOrder, .. redmods.EffectiveOrder], evidence) { Failures = [.. legacy.Failures, .. redmods.Failures] };
    }

    private static ArchiveOrderProblemLane CombineLanes(ArchiveOrderProblemLane legacy, ArchiveOrderProblemLane redmod)
    {
        if (legacy == ArchiveOrderProblemLane.None) return redmod;
        if (redmod == ArchiveOrderProblemLane.None || legacy == redmod) return legacy;
        return ArchiveOrderProblemLane.Combined;
    }
}

public static class RedmodArchiveIdentity
{
    public static bool IsAmbiguousProvider(ResourceProvider provider, IReadOnlyList<ResourceProvider> providers)
        => provider.Provider.StartsWith("REDmod:", StringComparison.OrdinalIgnoreCase)
            && providers.Where(value => string.Equals(value.Provider, provider.Provider, StringComparison.OrdinalIgnoreCase)).Select(value => value.ArchiveName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    public static bool ProviderPayloadsDiffer(ResourceProvider provider, IReadOnlyList<ResourceProvider> providers)
        => providers.Where(value => string.Equals(value.Provider, provider.Provider, StringComparison.OrdinalIgnoreCase)).Select(value => value.PayloadFingerprint).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).Skip(1).Any();
}
