namespace ConflictStudio.Core;

public sealed record SourceAnalysisFailure(string Provider, string FilePath, string Surface, string Message);

public sealed record ModSourceInventory(RedScriptSource[] RedScripts, LuaSource[] LuaSources, TweakSource[] TweakSources, SourceAnalysisFailure[] Failures);

public static class ModSourceScanner
{
    private static readonly HashSet<string> RegisteredRed4ExtScriptFolders = new(["ArchiveXL", "Codeware", "TweakXL"], StringComparer.OrdinalIgnoreCase);

    public static ModSourceInventory Scan(string modsRoot, IReadOnlyList<string> activeProviders, CancellationToken cancellationToken = default)
        => ScanProviders(activeProviders.Select(provider => new DeploymentProvider(provider, Path.Combine(modsRoot, provider))).ToArray(), cancellationToken);

    public static ModSourceInventory ScanProviders(IReadOnlyList<DeploymentProvider> providers, CancellationToken cancellationToken = default)
        => ScanProviders(providers, null, cancellationToken);

    public static ModSourceInventory ScanProviders(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, CancellationToken cancellationToken = default)
        => ScanProviders(providers, deployedWinners, null, cancellationToken);

    public static ModSourceInventory ScanProviders(IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
        => ScanManifest(DeploymentFileManifest.Build(providers, cancellationToken), deployedWinners, excludedPhysicalPaths, cancellationToken);

    public static ModSourceInventory ScanManifest(DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string[] exclusions = PhysicalPathExclusions.Normalize(excludedPhysicalPaths);
        DeploymentProvider[] providers = manifest.Providers;
        PhysicalPathReservation[] reservations = SourceReservations(providers, exclusions);
        Dictionary<string, Candidate> redScripts = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Candidate> luaSources = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Candidate> redTweaks = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Candidate> tweakSources = TweakCandidates(manifest, deployedWinners, exclusions, reservations, cancellationToken);
        List<SourceAnalysisFailure> failures = manifest.Failures.SelectMany(Failures).ToList();
        foreach (DeploymentFileEntry file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsRedScriptPath(file.RelativePath)) Set(redScripts, file, deployedWinners, exclusions, reservations);
            else if (IsLuaPath(file.RelativePath)) Set(luaSources, file, deployedWinners, exclusions, reservations);
            else if (IsRedTweakPath(file.RelativePath)) Set(redTweaks, file, deployedWinners, exclusions, reservations);
            else if (IsUnregisteredRed4ExtScriptPath(file.RelativePath)) failures.Add(new SourceAnalysisFailure(file.Provider.Name, file.RelativePath, "RedScript registration", "This .reds file is under a RED4ext plugin, but Conflict Studio has no source evidence that the plugin registers this file or folder. It was not analyzed as active source."));
        }
        if (deployedWinners is not null)
        {
            AddMissingWinners(redScripts, providers, deployedWinners, failures, IsRedScriptPath, "RedScript");
            AddMissingWinners(luaSources, providers, deployedWinners, failures, IsLuaPath, "CET Lua");
            AddMissingWinners(redTweaks, providers, deployedWinners, failures, IsRedTweakPath, "TweakXL RED");
            AddMissingWinners(tweakSources, providers, deployedWinners, failures, IsTweakPath, "TweakXL");
        }
        RequireCetLuaActivation(luaSources, failures);

        RedScriptSource[] redScriptInventory = Read(redScripts.Values, "RedScript", value => new RedScriptSource(value.Provider, value.RelativePath, manifest.ReadText(value.File, cancellationToken)), failures, cancellationToken);
        LuaSource[] luaInventory = Read(luaSources.Values, "CET Lua", value => new LuaSource(value.Provider, value.RelativePath, manifest.ReadText(value.File, cancellationToken)), failures, cancellationToken);
        luaInventory = LuaSourceReachability.Select(luaInventory, luaSources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), failures, cancellationToken);
        TweakSource[] tweakInventory = Read(tweakSources.Values, "TweakXL", value => new TweakSource(value.Provider, value.RelativePath, manifest.ReadText(value.File, cancellationToken)), failures, cancellationToken);
        foreach (Candidate tweak in redTweaks.Values.Where(value => !value.Excluded)) failures.Add(new SourceAnalysisFailure(tweak.Provider, tweak.RelativePath, "TweakXL RED", "RED .tweak source is captured as an effective deployment file, but Conflict Studio does not parse it as TweakXL YAML."));
        failures.AddRange(RedScriptConditionalSourceFilter.Failures(redScriptInventory));
        return new ModSourceInventory(redScriptInventory, luaInventory, tweakInventory, failures.ToArray());
    }

    internal static DeploymentFileEntry[] EffectiveTweakFiles(DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, IReadOnlySet<string>? excludedPhysicalPaths, CancellationToken cancellationToken)
    {
        string[] exclusions = PhysicalPathExclusions.Normalize(excludedPhysicalPaths);
        return TweakCandidates(manifest, deployedWinners, exclusions, SourceReservations(manifest.Providers, exclusions), cancellationToken).Values.Where(value => !value.Excluded).Select(value => value.File).ToArray();
    }

    private static PhysicalPathReservation[] SourceReservations(IEnumerable<DeploymentProvider> providers, string[] exclusions)
        => PhysicalPathExclusions.Reservations(providers.Select(value => value.RootPath).ToArray(), exclusions, relative => new[] { ".reds", ".lua", ".tweak", ".yaml", ".yml" }.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase));

    private static Dictionary<string, Candidate> TweakCandidates(DeploymentFileManifest manifest, IReadOnlyDictionary<string, string>? deployedWinners, string[] exclusions, PhysicalPathReservation[] reservations, CancellationToken cancellationToken)
    {
        Dictionary<string, Candidate> candidates = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeploymentFileEntry file in manifest.Files.Where(file => IsTweakPath(file.RelativePath)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Set(candidates, file, deployedWinners, exclusions, reservations);
        }
        return candidates;
    }

    private static T[] Read<T>(IEnumerable<Candidate> candidates, string surface, Func<Candidate, T> read, List<SourceAnalysisFailure> failures, CancellationToken cancellationToken)
    {
        List<T> sources = [];
        foreach (Candidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.Excluded) continue;
            try { sources.Add(read(candidate)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { failures.Add(new SourceAnalysisFailure(candidate.Provider, candidate.RelativePath, surface, exception.Message)); }
        }
        return sources.ToArray();
    }

    private static void Set(Dictionary<string, Candidate> candidates, DeploymentFileEntry file, IReadOnlyDictionary<string, string>? deployedWinners, string[] exclusions, PhysicalPathReservation[] reservations)
    {
        string relative = file.RelativePath;
        Candidate candidate = new(file.Provider.Name, relative, file, PhysicalPathExclusions.Contains(exclusions, file.PhysicalPath));
        if (deployedWinners is not null)
        {
            if (!VortexDeploymentFiles.IsEffective(file.Provider.ManagerId, relative, deployedWinners)) return;
            candidates[relative] = candidate;
            return;
        }
        if (PhysicalPathExclusions.ReservedBefore(reservations, file.ProviderPosition, relative)) return;
        candidates.TryAdd(relative, candidate);
    }

    private static void RequireCetLuaActivation(Dictionary<string, Candidate> candidates, List<SourceAnalysisFailure> failures)
    {
        foreach (Candidate loose in candidates.Values.Where(value => CetLuaRoot(value.RelativePath) is null).ToArray())
        {
            candidates.Remove(loose.RelativePath);
            failures.Add(new SourceAnalysisFailure(loose.Provider, loose.RelativePath, "CET Lua activation", "This Lua file is directly under the CET mods root, so no activated CET mod root could be established."));
        }
        foreach (IGrouping<string, Candidate> root in candidates.Values.Select(value => new { Root = CetLuaRoot(value.RelativePath), Candidate = value }).Where(value => value.Root is not null).GroupBy(value => value.Root!, value => value.Candidate, StringComparer.OrdinalIgnoreCase))
        {
            if (root.Any(value => !value.Excluded && string.Equals(value.RelativePath, root.Key + "\\init.lua", StringComparison.OrdinalIgnoreCase))) continue;
            foreach (Candidate candidate in root) candidates.Remove(candidate.RelativePath);
            Candidate source = root.First();
            failures.Add(new SourceAnalysisFailure(source.Provider, root.Key, "CET Lua activation", "This CET mod folder has no available init.lua after scan exclusions. That file is required to start the mod, so its Lua code was not analyzed as active."));
        }
    }

    internal static string? CetLuaRoot(string relative)
    {
        const string prefix = "bin\\x64\\plugins\\cyber_engine_tweaks\\mods\\";
        string remainder = relative[prefix.Length..];
        int separator = remainder.IndexOf('\\');
        return separator < 0 ? null : prefix + remainder[..separator];
    }

    private static IEnumerable<SourceAnalysisFailure> Failures(DeploymentFileEnumerationFailure failure)
    {
        if (failure.Lane.Equals("r6\\scripts", StringComparison.OrdinalIgnoreCase) || failure.Lane.Equals("red4ext\\plugins", StringComparison.OrdinalIgnoreCase)) yield return new SourceAnalysisFailure(failure.Provider, failure.Lane, "RedScript", failure.Message);
        if (failure.Lane.Equals("bin\\x64\\plugins", StringComparison.OrdinalIgnoreCase)) yield return new SourceAnalysisFailure(failure.Provider, failure.Lane, "CET Lua", failure.Message);
        if (failure.Lane.Equals("r6\\tweaks", StringComparison.OrdinalIgnoreCase)) yield return new SourceAnalysisFailure(failure.Provider, failure.Lane, "TweakXL", failure.Message);
    }

    private static void AddMissingWinners(Dictionary<string, Candidate> candidates, IReadOnlyList<DeploymentProvider> providers, IReadOnlyDictionary<string, string> deployedWinners, List<SourceAnalysisFailure> failures, Func<string, bool> matches, string surface)
    {
        foreach ((string relative, string winnerId) in deployedWinners.Where(value => matches(value.Key)))
        {
            if (candidates.ContainsKey(relative)) continue;
            DeploymentProvider? winner = providers.FirstOrDefault(value => string.Equals(value.ManagerId, winnerId, StringComparison.OrdinalIgnoreCase));
            if (winner is not null) failures.Add(new SourceAnalysisFailure(winner.Name, relative, surface, "Vortex's selected file was missing from the scanned mod folder, so its code was not analyzed."));
        }
    }

    private static bool IsRedScriptPath(string relative)
    {
        if (!string.Equals(Path.GetExtension(relative), ".reds", StringComparison.OrdinalIgnoreCase)) return false;
        if (relative.StartsWith("r6\\scripts\\", StringComparison.OrdinalIgnoreCase)) return true;
        string[] segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4 || !string.Equals(segments[0], "red4ext", StringComparison.OrdinalIgnoreCase) || !string.Equals(segments[1], "plugins", StringComparison.OrdinalIgnoreCase)) return false;
        if (segments.Length >= 5 && RegisteredRed4ExtScriptFolders.Contains(segments[2]) && string.Equals(segments[3], "Scripts", StringComparison.OrdinalIgnoreCase)) return true;
        string fileName = segments[^1];
        return segments.Length == 4
            && string.Equals(segments[2], "mod_settings", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(fileName, "packed.reds", StringComparison.OrdinalIgnoreCase) || string.Equals(fileName, "module.reds", StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsUnregisteredRed4ExtScriptPath(string relative)
        => relative.StartsWith("red4ext\\plugins\\", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(relative), ".reds", StringComparison.OrdinalIgnoreCase)
            && !IsRedScriptPath(relative);
    private static bool IsLuaPath(string relative) => relative.StartsWith("bin\\x64\\plugins\\cyber_engine_tweaks\\mods\\", StringComparison.OrdinalIgnoreCase) && string.Equals(Path.GetExtension(relative), ".lua", StringComparison.OrdinalIgnoreCase);
    private static bool IsTweakPath(string relative)
    {
        string extension = Path.GetExtension(relative);
        return relative.StartsWith("r6\\tweaks\\", StringComparison.OrdinalIgnoreCase) && (string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase));
    }
    private static bool IsRedTweakPath(string relative)
        => relative.StartsWith("r6\\tweaks\\", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetExtension(relative), ".tweak", StringComparison.OrdinalIgnoreCase);

    private sealed record Candidate(string Provider, string RelativePath, DeploymentFileEntry File, bool Excluded);
}

internal static class PhysicalPathExclusions
{
    public static string[] Normalize(IReadOnlySet<string>? paths)
        => paths is null ? [] : paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool Contains(IReadOnlyList<string> exclusions, string path)
    {
        string fullPath = Path.GetFullPath(path);
        foreach (string excluded in exclusions)
        {
            if (string.Equals(fullPath, excluded, StringComparison.OrdinalIgnoreCase)) return true;
            string root = Path.TrimEndingDirectorySeparator(excluded);
            if (fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static PhysicalPathReservation[] Reservations(IReadOnlyList<string> providerRoots, IReadOnlyList<string> exclusions, Func<string, bool> isExactFile)
    {
        List<PhysicalPathReservation> reservations = [];
        for (int providerPosition = 0; providerPosition < providerRoots.Count; providerPosition++)
        {
            string root = Path.GetFullPath(providerRoots[providerPosition]);
            foreach (string excluded in exclusions)
            {
                string relative = Path.GetRelativePath(root, excluded).Replace('/', '\\');
                if (relative == ".." || relative.StartsWith("..\\", StringComparison.Ordinal)) continue;
                if (relative == ".") relative = string.Empty;
                reservations.Add(new PhysicalPathReservation(providerPosition, relative, Directory.Exists(excluded) || !isExactFile(relative)));
            }
        }
        return reservations.ToArray();
    }

    public static bool ReservedBefore(IReadOnlyList<PhysicalPathReservation> reservations, int providerPosition, string relativePath)
        => reservations.Any(value => value.ProviderPosition < providerPosition && Matches(value, relativePath));

    public static PhysicalPathReservation? First(IReadOnlyList<PhysicalPathReservation> reservations, string relativePath)
        => reservations.Where(value => Matches(value, relativePath)).OrderBy(value => value.ProviderPosition).FirstOrDefault();

    private static bool Matches(PhysicalPathReservation reservation, string relativePath)
        => reservation.Descendants
            ? reservation.RelativePath.Length == 0 || relativePath.StartsWith(reservation.RelativePath + "\\", StringComparison.OrdinalIgnoreCase)
            : string.Equals(reservation.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase);
}

internal sealed record PhysicalPathReservation(int ProviderPosition, string RelativePath, bool Descendants);
