namespace ConflictStudio.Core;

public sealed record ArchiveXlProviderSource(string Provider, string RootPath, string? ManagerId = null);

public enum ArchiveXlFailureKind { Operational, Malformed, Coverage }

public sealed record ArchiveXlSourceFailure(string Provider, string FilePath, string Message, ArchiveXlFailureKind Kind = ArchiveXlFailureKind.Operational);

public sealed record ArchiveXlSourceScanResult(ArchiveXlSource[] Sources, ArchiveXlSourceFailure[] Failures);

public static class ArchiveXlSourceScanner
{
    public static ArchiveXlSourceScanResult Scan(IReadOnlyList<ArchiveXlProviderSource> providers, CancellationToken cancellationToken = default)
        => Scan(providers, null, cancellationToken);

    public static ArchiveXlSourceScanResult Scan(IReadOnlyList<ArchiveXlProviderSource> providers, IReadOnlyDictionary<string, string>? deployedWinners, CancellationToken cancellationToken = default)
        => Scan(providers, deployedWinners, null, cancellationToken);

    public static ArchiveXlSourceScanResult Scan(IReadOnlyList<ArchiveXlProviderSource> providers, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        DeploymentProvider[] deploymentProviders = providers.Select(value => new DeploymentProvider(value.Provider, value.RootPath, null, value.ManagerId)).ToArray();
        return ScanManifest(DeploymentFileManifest.Build(deploymentProviders, cancellationToken), deployedWinners, excludedPhysicalPaths, cancellationToken);
    }

    public static ArchiveXlSourceScanResult ScanManifest(DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string[] exclusions = PhysicalPathExclusions.Normalize(excludedPhysicalPaths);
        DeploymentProvider[] providers = manifest.Providers;
        PhysicalPathReservation[] reservations = PhysicalPathExclusions.Reservations(providers.Select(value => value.RootPath).ToArray(), exclusions, relative => string.Equals(Path.GetExtension(relative), ".xl", StringComparison.OrdinalIgnoreCase));
        List<ArchiveXlSource> sources = [];
        List<ArchiveXlSourceFailure> failures = manifest.Failures.Where(value => value.Lane.Length == 0 || value.Lane.Equals("archive\\pc\\mod", StringComparison.OrdinalIgnoreCase)).Select(value => new ArchiveXlSourceFailure(value.Provider, value.Path, value.Message, ArchiveXlFailureKind.Operational)).ToList();
        failures.AddRange(providers.Where(value => !Directory.Exists(value.RootPath)).Select(value => new ArchiveXlSourceFailure(value.Name, value.RootPath, "The ArchiveXL provider root does not exist.", ArchiveXlFailureKind.Operational)));
        Dictionary<string, DeploymentFileEntry> effective = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeploymentFileEntry file in manifest.Files.Where(IsArchiveXlSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (deployedWinners is not null)
            {
                if (VortexDeploymentFiles.IsEffective(file.Provider.ManagerId, file.RelativePath, deployedWinners)) effective[file.RelativePath] = file;
            }
            else if (!PhysicalPathExclusions.ReservedBefore(reservations, file.ProviderPosition, file.RelativePath)) effective.TryAdd(file.RelativePath, file);
        }
        if (deployedWinners is not null)
        {
            foreach ((string relative, string winnerId) in deployedWinners.Where(value => value.Key.StartsWith("archive\\pc\\mod\\", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(value.Key), ".xl", StringComparison.OrdinalIgnoreCase)))
            {
                if (effective.ContainsKey(relative)) continue;
                DeploymentProvider? winner = providers.FirstOrDefault(value => string.Equals(value.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
                if (winner is not null) failures.Add(new ArchiveXlSourceFailure(winner.Name, relative, "The deployed winner is absent from the captured provider, so no ArchiveXL claim was made.", ArchiveXlFailureKind.Operational));
            }
        }
        foreach ((string relativePath, DeploymentFileEntry file) in effective)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (PhysicalPathExclusions.Contains(exclusions, file.PhysicalPath)) continue;
            try { sources.Add(new ArchiveXlSource(file.Provider.Name, relativePath, manifest.ReadText(file, cancellationToken))); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failures.Add(new ArchiveXlSourceFailure(file.Provider.Name, relativePath, exception.Message, ArchiveXlFailureKind.Operational)); }
        }
        return new ArchiveXlSourceScanResult(sources.ToArray(), failures.ToArray());
    }

    private static bool IsArchiveXlSource(DeploymentFileEntry file)
    {
        if (!string.Equals(Path.GetExtension(file.RelativePath), ".xl", StringComparison.OrdinalIgnoreCase)) return false;
        return file.ArchiveXlFallbackRoot || file.RelativePath.StartsWith("archive\\pc\\mod\\", StringComparison.OrdinalIgnoreCase);
    }
}
