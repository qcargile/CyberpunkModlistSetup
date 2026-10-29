using System.Text.Json;

namespace ConflictStudio.Core;

public enum VirtualFileRelation { Identical = 0, Different = 1, Equivalent = 2 }

public sealed record VirtualFileProvider(string Provider, string PhysicalPath, long Size, string Sha256, int ProfilePosition, int? Mo2Priority = null);

public sealed record VirtualFileShadow(string RelativePath, string WinnerProvider, VirtualFileRelation Relation, VirtualFileProvider[] Providers);

public sealed record VirtualFileShadowScanResult(VirtualFileShadow[] Shadows, SourceAnalysisFailure[] Failures);

public static class VirtualFileShadowScanner
{
    public static VirtualFileShadow[] Scan(string modsRoot, IReadOnlyList<string> activeProviders, CancellationToken cancellationToken = default)
        => ScanProviders(activeProviders.Select(provider => new DeploymentProvider(provider, Path.Combine(modsRoot, provider))).ToArray(), cancellationToken);

    public static VirtualFileShadow[] ScanProviders(IReadOnlyList<DeploymentProvider> providers, CancellationToken cancellationToken = default)
        => ScanProviders(providers, null, cancellationToken);

    public static VirtualFileShadow[] ScanProviders(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, CancellationToken cancellationToken = default)
        => ScanProvidersResilient(providers, deployedWinners, cancellationToken).Shadows;

    public static VirtualFileShadowScanResult ScanProvidersResilient(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners = null, CancellationToken cancellationToken = default)
        => ScanProvidersResilient(providers, deployedWinners, null, cancellationToken);

    public static VirtualFileShadowScanResult ScanProvidersResilient(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
        => ScanManifest(DeploymentFileManifest.Build(providers, cancellationToken), deployedWinners, excludedPhysicalPaths, cancellationToken);

    public static VirtualFileShadowScanResult ScanManifest(DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string[] exclusions = PhysicalPathExclusions.Normalize(excludedPhysicalPaths);
        PhysicalPathReservation[] reservations = PhysicalPathExclusions.Reservations(manifest.Providers.Select(value => value.RootPath).ToArray(), exclusions, relative => Path.HasExtension(relative));
        Dictionary<string, List<Candidate>> files = new(StringComparer.OrdinalIgnoreCase);
        List<SourceAnalysisFailure> failures = manifest.Failures.Select(value => new SourceAnalysisFailure(value.Provider, value.Path, "Virtual file enumeration", value.Message)).ToList();
        foreach (DeploymentFileEntry file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = file.RelativePath;
            if (relative.Equals("meta.ini", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".git\\", StringComparison.OrdinalIgnoreCase) || DeploymentFilePolicy.IsMutableOutput(relative)) continue;
            if (!VortexDeploymentFiles.IsDeployedPath(file.Provider.ManagerId, relative, deployedWinners)) continue;
            if (deployedWinners?.ContainsKey(relative) != true && PhysicalPathExclusions.ReservedBefore(reservations, file.ProviderPosition, relative)) continue;
            Candidate candidate = new(file, PhysicalPathExclusions.Contains(exclusions, file.PhysicalPath));
            if (!files.TryGetValue(relative, out List<Candidate>? fileProviders)) files[relative] = fileProviders = [];
            fileProviders.Add(candidate);
        }

        VirtualFileShadow[] shadows = files.Where(value => value.Value.Count > 1)
            .Select(value => Create(value.Key, value.Value, manifest, deployedWinners, failures, cancellationToken))
            .Where(value => value is not null)
            .Cast<VirtualFileShadow>()
            .OrderBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new VirtualFileShadowScanResult(shadows, failures.ToArray());
    }

    private static VirtualFileShadow? Create(string relative, List<Candidate> candidates, DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, List<SourceAnalysisFailure> failures, CancellationToken cancellationToken)
    {
        if (candidates.Any(value => value.Excluded)) return null;
        string? winnerId = deployedWinners?.GetValueOrDefault(relative);
        if (winnerId is not null && !candidates.Any(value => string.Equals(value.File.Provider.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add(new SourceAnalysisFailure("Vortex", relative, "Virtual file winner", "The deployed winner is absent from the captured provider set, so lower providers were not promoted."));
            return null;
        }
        List<VirtualFileProvider> readable = [];
        bool failed = false;
        foreach (Candidate candidate in candidates.OrderBy(value => winnerId is not null && string.Equals(value.File.Provider.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(value => value.File.ProviderPosition))
        {
            try
            {
                readable.Add(Fingerprint(candidate, manifest, cancellationToken));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add(new SourceAnalysisFailure(candidate.File.Provider.Name, candidate.File.PhysicalPath, "Virtual file fingerprint", exception.Message));
                failed = true;
            }
        }
        if (failed || readable.Count < 2) return null;
        VirtualFileProvider[] ordered = readable.ToArray();
        bool identical = ordered.Select(value => value.Sha256).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
        VirtualFileRelation relation = identical ? VirtualFileRelation.Identical : IsSemanticallyEquivalentJson(relative, candidates, manifest, cancellationToken) ? VirtualFileRelation.Equivalent : VirtualFileRelation.Different;
        return new VirtualFileShadow(relative, ordered[0].Provider, relation, ordered);
    }

    private static bool IsSemanticallyEquivalentJson(string relative, IReadOnlyList<Candidate> candidates, DeploymentFileManifest manifest, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(relative), ".json", StringComparison.OrdinalIgnoreCase)) return false;
        List<JsonDocument> documents = [];
        try
        {
            foreach (Candidate candidate in candidates)
            {
                documents.Add(JsonDocument.Parse(manifest.ReadBytes(candidate.File, cancellationToken)));
            }
            JsonElement first = documents[0].RootElement;
            return documents.Skip(1).All(value => JsonElement.DeepEquals(first, value.RootElement));
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            foreach (JsonDocument document in documents) document.Dispose();
        }
    }

    private static VirtualFileProvider Fingerprint(Candidate candidate, DeploymentFileManifest manifest, CancellationToken cancellationToken)
    {
        ArchiveFingerprint fingerprint = manifest.Fingerprint(candidate.File, cancellationToken);
        return new VirtualFileProvider(candidate.File.Provider.Name, candidate.File.PhysicalPath, fingerprint.Size, fingerprint.Sha256, candidate.File.ProviderPosition, candidate.File.Provider.Mo2Priority);
    }

    private sealed record Candidate(DeploymentFileEntry File, bool Excluded);
}

internal static class DeploymentFilePolicy
{
    private static readonly HashSet<string> MutableExtensions = new([".log", ".tmp", ".dmp", ".bak", ".old", ".db", ".sqlite", ".sqlite3"], StringComparer.OrdinalIgnoreCase);

    public static bool IsMutableOutput(string relativePath)
    {
        string normalized = relativePath.Replace('/', '\\');
        return MutableExtensions.Contains(Path.GetExtension(normalized)) || normalized.Contains("\\logs\\", StringComparison.OrdinalIgnoreCase);
    }
}
