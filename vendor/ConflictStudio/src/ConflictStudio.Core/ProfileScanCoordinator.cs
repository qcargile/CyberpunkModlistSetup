using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ConflictStudio.Core;

public sealed record ScanPhaseMetric(string Name, long ElapsedMilliseconds, int ItemCount);

public sealed record ScanScaleMetrics(int Providers, int Archives, long ArchiveBytes, int EvidenceFiles, int PackedResources, int SourceFiles, int MaxConflictProviders, long CompetitorReferences);

public sealed record VortexBridgeScanMetrics(long RefreshMilliseconds, int DeploymentFiles, int RelevantDeploymentFiles, int Winners, int UnmappedRelevantFiles, int TargetRelocatedFiles, bool InventoryComplete);

public sealed record ProfileScanMetrics(long TotalElapsedMilliseconds, ScanPhaseMetric[] Phases, int RefreshedArchiveFingerprints = 0, int PackedCacheHits = 0, int CodeCacheHits = 0, ScanScaleMetrics? Scale = null, VortexBridgeScanMetrics? VortexBridge = null);

public sealed record ScanProgress(string Phase, int Completed, int Total, string? CurrentItem = null, long BytesCompleted = 0, long BytesTotal = 0, long BytesRead = 0, long ElapsedMilliseconds = 0);

public sealed record ProfileScanReceipt(
    int SchemaVersion,
    string ProfileName,
    DateTimeOffset ScannedAtUtc,
    string[] ActiveProviders,
    string[] ArchiveOrder,
    RdarArchiveFailure[] ArchiveFailures,
    ResourceConflict[] ResourceConflicts,
    VirtualFileShadow[] VirtualFileShadows,
    InteractionFinding[] InteractionFindings,
    RedScriptFlowEvidence[] RedScriptFlows,
    SharedStateWriteFinding[] SharedStateWrites,
    LuaCallbackEvidence[] LuaCallbacks,
    TweakOverlap[] TweakOverlaps,
    ArchiveXlOperationChain[] ArchiveXlChains,
    ArchiveXlSourceFailure[] ArchiveXlFailures,
    SourceAnalysisFailure[]? SourceFailures = null,
    ProfileScanMetrics? Metrics = null,
    string? InstallationId = null,
    ArchiveConflictSummary[]? ArchiveSummaries = null,
    ArchiveOrderEvidence? ArchiveOrderEvidence = null,
    ResourcePathIndexEvidence? ResourcePathIndexEvidence = null,
    RdarArchiveWarning[]? ArchiveWarnings = null,
    Mo2Archive[]? ArchiveInventory = null,
    Mo2Archive[]? EditableArchiveInventory = null,
    string[]? EditableArchiveOrder = null,
    ArchiveOrderEvidence? EditableArchiveOrderEvidence = null,
    ModManagerKind ManagerKind = ModManagerKind.Mo2,
    string? ManagerContextPath = null,
    bool DeploymentFresh = true,
    CodeCoverageReceipt? CodeCoverage = null,
    string? ToolVersion = null,
    string? AnalysisVersion = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public DeploymentProvider[] SourceProviders { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public CodeSourceEvidence[] CodeEvidence { get; init; } = [];

    [System.Text.Json.Serialization.JsonIgnore]
    public TweakReferenceIndex TweakReferences { get; init; } = TweakReferenceIndex.Empty;
}

public static class ProfileScanCoordinator
{
    private const int PackedAnalysisSchema = 1;
    private const int CodeAnalysisSchema = 9;

    public static ProfileScanReceipt Scan(string mo2Root, Mo2Profile profile, DateTimeOffset scannedAtUtc)
        => Scan(mo2Root, profile, scannedAtUtc, null, CancellationToken.None);

    public static ProfileScanReceipt Scan(string mo2Root, Mo2Profile profile, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => Scan(mo2Root, profile, scannedAtUtc, progress, null, cancellationToken);

    internal static ProfileScanReceipt Scan(string mo2Root, Mo2Profile profile, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, Action? beforeFinalValidation, CancellationToken cancellationToken)
        => Scan(mo2Root, profile, scannedAtUtc, progress, beforeFinalValidation, null, cancellationToken);

    internal static ProfileScanReceipt Scan(string mo2Root, Mo2Profile profile, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, Action? beforeFinalValidation, string? vortexContextPath, CancellationToken cancellationToken)
        => Scan(mo2Root, profile, scannedAtUtc, progress, beforeFinalValidation, vortexContextPath, ContentAddressedAnalysisCache.Default(), cancellationToken);

    internal static ProfileScanReceipt Scan(string mo2Root, Mo2Profile profile, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, Action? beforeFinalValidation, string? vortexContextPath, ContentAddressedAnalysisCache analysisCache, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(analysisCache);
        if (scannedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Scan timestamps must use UTC.", nameof(scannedAtUtc));
        Stopwatch total = Stopwatch.StartNew();
        string fingerprintCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio", "cache", "archive-fingerprints-v1.json");
        PreparedProfileScan prepared = PrepareMo2(mo2Root, profile, progress, vortexContextPath, fingerprintCache, false, cancellationToken);
        try { return ScanPrepared(prepared, scannedAtUtc, progress, beforeFinalValidation, total, analysisCache, cancellationToken); }
        catch (CachedFingerprintMismatchException)
        {
            progress?.Report(new ScanProgress("deployment · refreshing cached archive fingerprints", 0, 1));
            prepared = PrepareMo2(mo2Root, profile, progress, vortexContextPath, fingerprintCache, true, cancellationToken);
            ProfileScanReceipt receipt = ScanPrepared(prepared, scannedAtUtc, progress, beforeFinalValidation, total, analysisCache, cancellationToken);
            if (receipt.Metrics is null) return receipt;
            return receipt with { Metrics = receipt.Metrics with { RefreshedArchiveFingerprints = 1 } };
        }
    }

    public static ProfileScanReceipt ScanVortex(string contextPath, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => ScanVortex(contextPath, scannedAtUtc, progress, null, cancellationToken);

    internal static ProfileScanReceipt ScanVortex(string contextPath, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, Action? beforeFinalValidation, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        if (scannedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Scan timestamps must use UTC.", nameof(scannedAtUtc));
        Stopwatch total = Stopwatch.StartNew();
        PreparedProfileScan prepared = PrepareVortex(contextPath, scannedAtUtc, progress, cancellationToken);
        return ScanPrepared(prepared, scannedAtUtc, progress, beforeFinalValidation, total, ContentAddressedAnalysisCache.Default(), cancellationToken);
    }

    public static ProfileScanReceipt ScanManual(string gameRoot, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
        => ScanManual(gameRoot, scannedAtUtc, progress, null, null, cancellationToken);

    internal static ProfileScanReceipt ScanManual(string gameRoot, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, Action? beforeFinalValidation, string? vortexContextPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        if (scannedAtUtc.Offset != TimeSpan.Zero) throw new ArgumentException("Scan timestamps must use UTC.", nameof(scannedAtUtc));
        Stopwatch total = Stopwatch.StartNew();
        PreparedProfileScan prepared = PrepareManual(gameRoot, progress, vortexContextPath, cancellationToken);
        return ScanPrepared(prepared, scannedAtUtc, progress, beforeFinalValidation, total, ContentAddressedAnalysisCache.Default(), cancellationToken);
    }

    private static PreparedProfileScan PrepareMo2(string mo2Root, Mo2Profile profile, IProgress<ScanProgress>? progress, string? vortexContextPath, string fingerprintCache, bool forceFingerprint, CancellationToken cancellationToken)
    {
        Mo2InstancePaths instancePaths = Mo2InstancePathResolver.Resolve(mo2Root);
        CrossManagerGuardBinding? crossManagerBinding = null;
        if (instancePaths.GameRoot is not null)
        {
            string vortexContext = vortexContextPath ?? VortexDeploymentGuard.DefaultContextPath;
            VortexDeploymentGuard.RequireNoDeployment(instancePaths.GameRoot, vortexContext);
            crossManagerBinding = new CrossManagerGuardBinding(instancePaths.GameRoot, vortexContext);
        }
        Stopwatch phase = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("deployment · reading MO2 profile", 0, 4));
        ProfileInputSnapshot profileSnapshot = ProfileInputGuard.Capture(profile.ModlistPath);
        Mo2ActiveProvider[] activeProviderEntries = Mo2ProfileReader.ReadActiveProviderEntries(profileSnapshot.Content);
        string[] activeProviders = activeProviderEntries.Select(value => value.Name).ToArray();
        progress?.Report(new ScanProgress("deployment · resolving MO2 paths", 1, 4));
        if (activeProviderEntries.Length > 0 && !Directory.Exists(instancePaths.ModsRoot)) throw new DirectoryNotFoundException($"The configured MO2 mods directory does not exist: {instancePaths.ModsRoot}. Check MO2 Settings > Paths and make sure Conflict Studio starts in the instance folder containing ModOrganizer.ini.");
        Mo2ActiveProvider[] missingProviders = activeProviderEntries.Where(value => !Directory.Exists(Path.Combine(instancePaths.ModsRoot, value.Name))).ToArray();
        if (activeProviderEntries.Length > 0 && missingProviders.Length == activeProviderEntries.Length) throw new DirectoryNotFoundException($"None of the {activeProviderEntries.Length:N0} active MO2 mods were found under {instancePaths.ModsRoot}. Check the instance's mod_directory setting.");
        SourceAnalysisFailure[] pathFailures = missingProviders.Select(value => new SourceAnalysisFailure(value.Name, value.Name, "MO2 path", "The active mod directory was not found under the configured MO2 mods path.")).ToArray();
        DeploymentProvider[] deploymentProviders = Mo2ProviderTopology.Discover(mo2Root, activeProviderEntries);
        progress?.Report(new ScanProgress("deployment · indexing archives", 2, 4));
        Mo2ArchiveProfile legacyArchives = Mo2ArchiveProfileScanner.ScanInstance(mo2Root, profile.ModlistPath, activeProviderEntries, fingerprintCache, forceFingerprint, progress, cancellationToken);
        RedmodArchiveProfile redmods = RedmodArchiveProfileScanner.Scan(deploymentProviders, null, progress, cancellationToken);
        Mo2ArchiveProfile archives = PackedArchiveTopology.Compose(legacyArchives, redmods);
        ProfileInputSnapshot[] inputs = [profileSnapshot, .. EvidenceSnapshots(archives.OrderEvidence)];
        progress?.Report(new ScanProgress("deployment · capturing active files", 3, 4));
        DeploymentFileManifest manifest = DeploymentFileManifest.Build(deploymentProviders, progress, cancellationToken);
        EvidenceFileCapture evidenceFiles = EvidenceFileSnapshots(archives, manifest, instancePaths.GameRoot, cancellationToken);
        progress?.Report(new ScanProgress("deployment · ready", 4, 4));
        return new PreparedProfileScan(ModManagerKind.Mo2, profile.Name, activeProviders, ProfileInstallationIdentity.Create(mo2Root), instancePaths, deploymentProviders, manifest, legacyArchives, redmods, archives, null, inputs, archives.OrderEvidence?.AbsentSources ?? [], evidenceFiles.Files, evidenceFiles.UnavailablePaths, null, true, phase.ElapsedMilliseconds, pathFailures.Concat(evidenceFiles.Failures).Concat(archives.Failures).ToArray(), null, crossManagerBinding);
    }

    private static PreparedProfileScan PrepareVortex(string contextPath, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        Stopwatch phase = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("deployment · reading Vortex profile", 0, 4));
        string fullContextPath = Path.GetFullPath(contextPath);
        ProfileInputSnapshot contextSnapshot = ProfileInputGuard.Capture(fullContextPath);
        VortexManagerContext context = VortexManagerContextStore.Read(contextSnapshot.Content);
        VortexProviderContext[] missingProviders = context.Providers.Where(value => !Directory.Exists(value.RootPath)).ToArray();
        DateTimeOffset contextHeartbeat = context.HeartbeatAtUtc ?? context.CapturedAtUtc;
        bool bridgeLive;
        string heartbeatPath = VortexBridgeHeartbeatStore.PathForContext(fullContextPath);
        ProfileInputSnapshot? heartbeatSnapshot = null;
        if (File.Exists(heartbeatPath))
        {
            try
            {
                heartbeatSnapshot = ProfileInputGuard.Capture(heartbeatPath);
                VortexBridgeHeartbeat heartbeat = VortexBridgeHeartbeatStore.Read(heartbeatSnapshot.Content);
                bridgeLive = string.Equals(heartbeat.ContextId, context.ContextId, StringComparison.Ordinal)
                    && string.Equals(heartbeat.ProfileId, context.ProfileId, StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                bridgeLive = false;
            }
        }
        else
        {
            TimeSpan contextAge = scannedAtUtc - contextHeartbeat;
            bridgeLive = contextAge >= TimeSpan.FromSeconds(-5) && contextAge <= TimeSpan.FromSeconds(15);
        }
        bool managerEvidenceReliable = bridgeLive && context.DeploymentFresh && context.DeploymentInventoryComplete && missingProviders.Length == 0 && context.UnmappedRelevantFileCount == 0;
        DeploymentProvider[] active = managerEvidenceReliable ? context.Providers.OrderBy(value => value.Order).Select(value => new DeploymentProvider(value.Name, value.RootPath, null, value.Id)).ToArray() : [];
        DeploymentProvider[] providers = [.. active, new DeploymentProvider("Game directory", context.GameRoot, -1, "game-directory")];
        progress?.Report(new ScanProgress("deployment · indexing archives", 1, 4));
        VortexManagerContext scanContext = managerEvidenceReliable ? context : context with { DeploymentFresh = false, Providers = [], DeployedWinners = [] };
        VortexMovedFiles movedFiles = VortexMovedFiles.Capture(scanContext, cancellationToken);
        Mo2ArchiveProfile legacyArchives = VortexArchiveProfileScanner.Scan(scanContext, progress, movedFiles, cancellationToken);
        RedmodArchiveProfile redmods = RedmodArchiveProfileScanner.Scan(providers, managerEvidenceReliable ? context.DeployedWinners : null, progress, movedFiles, cancellationToken);
        Mo2ArchiveProfile archives = PackedArchiveTopology.Compose(legacyArchives, redmods);
        progress?.Report(new ScanProgress("deployment · resolving active files", 2, 4));
        DeploymentFileManifest manifest = DeploymentFileManifest.Build(providers, progress, cancellationToken).WithMovedFiles(movedFiles);
        Mo2InstancePaths paths = new(context.StagingRoot, context.StagingRoot, string.Empty, string.Empty, context.GameRoot, context.ProfileName);
        ProfileInputSnapshot[] inputs = heartbeatSnapshot is null ? EvidenceSnapshots(archives.OrderEvidence) : [.. EvidenceSnapshots(archives.OrderEvidence), heartbeatSnapshot];
        inputs = [.. inputs, .. movedFiles.Inputs];
        bool deploymentFresh = context.DeploymentFresh && context.DeploymentInventoryComplete && bridgeLive && missingProviders.Length == 0 && context.UnmappedRelevantFileCount == 0;
        List<SourceAnalysisFailure> failures = [];
        if (!deploymentFresh || context.UnmappedRelevantFileCount > 0) failures.Add(new SourceAnalysisFailure("Vortex", Path.GetFileName(fullContextPath), "Deployment", !bridgeLive ? "The Vortex bridge is offline or stale. Only deployed game files were scanned; open Vortex and refresh before relying on provider losers or applying an order." : missingProviders.Length > 0 ? "At least one active Vortex staging folder is missing. Only deployed game files were scanned; reinstall or remove the named provider, deploy, and refresh." : !context.DeploymentInventoryComplete ? "Vortex returned an incomplete deployment inventory. Only deployed game files were scanned; refresh the active profile before relying on provider losers or applying an order." : context.TargetRelocatedFileCount > 0 ? $"Vortex uses target relocation for {context.TargetRelocatedFileCount:N0} relevant deployed file{(context.TargetRelocatedFileCount == 1 ? string.Empty : "s")}. Conflict Studio cannot map those deployed paths back to their staging sources, so only deployed game files were scanned. Redeploying will not change this bridge limitation." : context.UnmappedRelevantFileCount > 0 ? $"Vortex reported {context.UnmappedRelevantFileCount:N0} relevant deployed file{(context.UnmappedRelevantFileCount == 1 ? string.Empty : "s")} without an active provider mapping. Only deployed game files were scanned; deploy the profile again before relying on provider losers or applying an order." : "Vortex has pending deployment changes. Deploy the active profile before relying on winners or applying an order."));
        failures.AddRange(missingProviders.Select(value => new SourceAnalysisFailure(value.Name, value.RootPath, "Vortex path", "The active Vortex provider staging folder does not exist.")));
        VortexContextBinding binding = new(fullContextPath, context.ContextId, context.ProfileId);
        VortexBridgeScanMetrics bridgeMetrics = new(context.BridgeRefreshMilliseconds, context.DeploymentFileCount, context.RelevantDeploymentFileCount, context.DeployedWinners.Count, context.UnmappedRelevantFileCount, context.TargetRelocatedFileCount, context.DeploymentInventoryComplete);
        progress?.Report(new ScanProgress("deployment · capturing active files", 3, 4));
        EvidenceFileCapture evidenceFiles = EvidenceFileSnapshots(archives, manifest, context.GameRoot, cancellationToken);
        progress?.Report(new ScanProgress("deployment · ready", 4, 4));
        return new PreparedProfileScan(ModManagerKind.Vortex, context.ProfileName, active.Select(value => value.Name).ToArray(), ProfileInstallationIdentity.Create("Vortex", context.GameRoot + "|" + context.ProfileId), paths, providers, manifest, legacyArchives, redmods, archives, managerEvidenceReliable ? context.DeployedWinners : null, inputs, archives.OrderEvidence?.AbsentSources ?? [], evidenceFiles.Files, evidenceFiles.UnavailablePaths, fullContextPath, deploymentFresh, phase.ElapsedMilliseconds, failures.Concat(evidenceFiles.Failures).Concat(archives.Failures).ToArray(), binding, null, bridgeMetrics);
    }

    private static PreparedProfileScan PrepareManual(string gameRoot, IProgress<ScanProgress>? progress, string? vortexContextPath, CancellationToken cancellationToken)
    {
        Stopwatch phase = Stopwatch.StartNew();
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ScanProgress("deployment · reading deployed game", 0, 3));
        string root = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(Path.Combine(root, "archive", "pc", "content")) && !File.Exists(Path.Combine(root, "bin", "x64", "Cyberpunk2077.exe"))) throw new DirectoryNotFoundException("Choose the Cyberpunk 2077 installation folder.");
        string vortexContext = vortexContextPath ?? VortexDeploymentGuard.DefaultContextPath;
        VortexDeploymentGuard.RequireNoDeployment(root, vortexContext);
        CrossManagerGuardBinding crossManagerBinding = new(root, vortexContext);
        DeploymentProvider[] providers = [new DeploymentProvider("Game directory", root, -1, "game-directory")];
        Mo2ArchiveProfile legacyArchives = ManualArchiveProfileScanner.Scan(root, cancellationToken);
        progress?.Report(new ScanProgress("deployment · indexing archives", 1, 3));
        RedmodArchiveProfile redmods = RedmodArchiveProfileScanner.Scan(providers, null, progress, cancellationToken);
        Mo2ArchiveProfile archives = PackedArchiveTopology.Compose(legacyArchives, redmods);
        progress?.Report(new ScanProgress("deployment · capturing active files", 2, 3));
        DeploymentFileManifest manifest = DeploymentFileManifest.Build(providers, progress, cancellationToken);
        Mo2InstancePaths paths = new(root, root, string.Empty, root, root, "Deployed game");
        ProfileInputSnapshot[] inputs = EvidenceSnapshots(archives.OrderEvidence);
        EvidenceFileCapture evidenceFiles = EvidenceFileSnapshots(archives, manifest, root, cancellationToken);
        progress?.Report(new ScanProgress("deployment · ready", 3, 3));
        return new PreparedProfileScan(ModManagerKind.Manual, "Deployed game", ["Game directory"], ProfileInstallationIdentity.Create("Manual", root), paths, providers, manifest, legacyArchives, redmods, archives, null, inputs, archives.OrderEvidence?.AbsentSources ?? [], evidenceFiles.Files, evidenceFiles.UnavailablePaths, null, true, phase.ElapsedMilliseconds, evidenceFiles.Failures.Concat(archives.Failures).ToArray(), null, crossManagerBinding);
    }

    private static ProfileScanReceipt ScanPrepared(PreparedProfileScan prepared, DateTimeOffset scannedAtUtc, IProgress<ScanProgress>? progress, Action? beforeFinalValidation, Stopwatch total, ContentAddressedAnalysisCache analysisCache, CancellationToken cancellationToken)
    {
        List<ScanPhaseMetric> phases = [new ScanPhaseMetric("deployment", prepared.DeploymentElapsedMilliseconds, prepared.Archives.Archives.Length)];
        progress?.Report(new ScanProgress("deployment", 1, 1));
        Stopwatch phase = Stopwatch.StartNew();
        HashSet<string> excludedPhysicalPaths = prepared.UnavailableEvidencePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] normalizedExclusions = PhysicalPathExclusions.Normalize(excludedPhysicalPaths);
        RdarArchiveFailure[] excludedArchiveFailures = prepared.Archives.Archives.Where(value => PhysicalPathExclusions.Contains(normalizedExclusions, value.PhysicalPath)).Select(value => new RdarArchiveFailure(value.Provider, value.ArchiveName, "The archive evidence snapshot could not be captured, so this archive was excluded from resource claims.")).ToArray();
        List<RdarArchiveInput> archiveInputs = prepared.Archives.Archives.Where(value => !PhysicalPathExclusions.Contains(normalizedExclusions, value.PhysicalPath)).Select(value => new RdarArchiveInput(value.Provider, value.PhysicalPath, value.ArchiveName, value.LogicalProvider)).ToList();
        string? oodleCandidate = prepared.Paths.GameRoot is null ? null : Path.Combine(prepared.Paths.GameRoot, "bin", "x64", "oo2ext_7_win64.dll");
        string? oodlePath = oodleCandidate is not null && !PhysicalPathExclusions.Contains(normalizedExclusions, oodleCandidate) ? oodleCandidate : null;
        string packedCacheKey = PackedCacheKey(prepared, normalizedExclusions);
        PackedProfileAnalysis? packedToCache = null;
        bool packedCacheHit = analysisCache.TryRead("packed", packedCacheKey, out PackedProfileAnalysis? packed) && packed?.SchemaVersion == PackedAnalysisSchema;
        if (!packedCacheHit)
        {
            RdarResourceScanResult resources = RdarResourceScanner.ScanResilient(archiveInputs, null, oodlePath, progress, cancellationToken);
            RdarArchiveFailure[] failures = resources.Failures.Concat(excludedArchiveFailures).ToArray();
            ResourcePathIndexResult pathIndex = ResourcePathIndex.Resolve(prepared.Paths, prepared.DeploymentProviders, resources.Resources.Select(value => value.ResourceHash).ToHashSet(), prepared.DeployedWinners, excludedPhysicalPaths, prepared.FileManifest, cancellationToken);
            ResourceProvider[] resolved = ResourcePathIndex.Apply(resources.Resources, pathIndex.Paths);
            resolved = RdarPayloadFingerprint.Apply(resolved, resources.PayloadIndexes ?? [], oodlePath, cancellationToken);
            packed = new PackedProfileAnalysis(PackedAnalysisSchema, resolved, failures, resources.Warnings ?? [], pathIndex.Evidence, resources.Resources.Length);
            packedToCache = packed;
        }
        else progress?.Report(new ScanProgress("packed resources · cache", packed!.IndexedResourceCount, Math.Max(1, packed.IndexedResourceCount)));
        cancellationToken.ThrowIfCancellationRequested();
        ResourceProvider[] resolvedResources = packed!.Resources;
        RdarArchiveFailure[] archiveFailures = packed.ArchiveFailures;
        RdarArchiveWarning[] archiveWarnings = packed.ArchiveWarnings;
        ResourcePathIndexEvidence pathIndexEvidence = packed.ResourcePathIndexEvidence;
        ArchiveOrderEvidence? orderEvidence = prepared.Archives.OrderEvidence;
        ArchiveOrderProblemLane unresolvedLane = orderEvidence?.Kind == ArchiveOrderEvidenceKind.Unresolved ? orderEvidence.ProblemLane : ArchiveOrderProblemLane.None;
        ArchiveOrderProblemLane incompleteLane = orderEvidence?.IncompleteArchiveLane ?? ArchiveOrderProblemLane.None;
        ResourceConflict[] resourceConflicts = ResourceConflictAnalyzer.Analyze(resolvedResources, prepared.Archives.EffectiveOrder, archiveFailures, unresolvedLane, incompleteLane, orderEvidence?.UnlistedArchives);
        ArchiveConflictSummary[] archiveSummaries = ArchiveResourceIndexBuilder.Build(resolvedResources, prepared.Archives.Archives, prepared.Archives.EffectiveOrder, archiveFailures, unresolvedLane, incompleteLane, orderEvidence?.UnlistedArchives);
        phases.Add(new ScanPhaseMetric("packed resources", phase.ElapsedMilliseconds, packed.IndexedResourceCount));
        phase.Restart();
        cancellationToken.ThrowIfCancellationRequested();
        string codeCacheKey = CodeCacheKey(prepared, normalizedExclusions);
        CodeProfileAnalysis? codeToCache = null;
        bool codeCacheHit = analysisCache.TryRead("code", codeCacheKey, out CodeProfileAnalysis? code)
            && code?.SchemaVersion == CodeAnalysisSchema
            && code.CodeEvidence is not null
            && code.TweakReferences is { } references
            && references.IsValid(code.CodeEvidence)
            && code.InteractionFindings is not null && code.InteractionFindings.All(finding => finding is not null)
            && CodeSourceEvidenceBuilder.MatchesTweakDefinitions(references, code.InteractionFindings.Select(finding => finding.Target).ToHashSet(StringComparer.Ordinal), prepared.FileManifest, prepared.DeployedWinners, excludedPhysicalPaths, cancellationToken);
        if (!codeCacheHit)
        {
            progress?.Report(new ScanProgress("effective source", 0, 2));
            ModSourceInventory inventory = ModSourceScanner.ScanManifest(prepared.FileManifest, prepared.DeployedWinners, excludedPhysicalPaths, cancellationToken);
            progress?.Report(new ScanProgress("effective source", 1, 2));
            VirtualFileShadowScanResult virtualShadowScan = VirtualFileShadowScanner.ScanManifest(prepared.FileManifest, prepared.DeployedWinners, excludedPhysicalPaths, cancellationToken);
            RedScriptFlowEvidence[] flows = RedScriptFlowEvidenceAnalyzer.Analyze(inventory.RedScripts);
            SharedStateWrite[] runtimeWrites = SharedStateWriteAnalyzer.Collect(inventory.RedScripts, inventory.LuaSources);
            SharedStateWriteFinding[] stateWrites = SharedStateWriteAnalyzer.Analyze(runtimeWrites);
            LuaCallbackEvidence[] callbacks = LuaCallbackEvidenceAnalyzer.Analyze(inventory.LuaSources);
            TweakAnalysisResult tweakAnalysis = TweakInteractionAnalyzer.AnalyzeDetailed(inventory.TweakSources);
            InteractionFinding[] interactions = InteractionReportBuilder.Build(inventory, flows, callbacks, tweakAnalysis.Overlaps, tweakAnalysis.Operations, runtimeWrites);
            CodeSourceCapture sourceCapture = CodeSourceEvidenceBuilder.BuildCapture(prepared.FileManifest, inventory, interactions, flows, callbacks, runtimeWrites, stateWrites, tweakAnalysis.Operations);
            int sourceItemCount = inventory.RedScripts.Length + inventory.LuaSources.Length + inventory.TweakSources.Length;
            phases.Add(new ScanPhaseMetric("effective source", phase.ElapsedMilliseconds, sourceItemCount));
            progress?.Report(new ScanProgress("effective source", 2, 2));
            phase.Restart();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new ScanProgress("ArchiveXL", 0, 1));
            ArchiveXlSourceScanResult archiveXlSources = ArchiveXlSourceScanner.ScanManifest(prepared.FileManifest, prepared.DeployedWinners, excludedPhysicalPaths, cancellationToken);
            ArchiveXlAnalysisResult archiveXlAnalysis = ArchiveXlManifestAnalyzer.AnalyzeDetailed(archiveXlSources.Sources);
            ArchiveXlOperationChain[] chains = ArchiveXlProviderChainAnalyzer.Group(archiveXlAnalysis.Operations);
            ArchiveXlSourceFailure[] xlFailures = archiveXlSources.Failures.Concat(archiveXlAnalysis.Failures).ToArray();
            SourceAnalysisFailure[] sourceFailures = inventory.Failures.Concat(tweakAnalysis.Failures).Concat(virtualShadowScan.Failures).ToArray();
            CodeCoverageReceipt coverage = CodeCoverageReceipt.Build(inventory, callbacks, inventory.Failures.Concat(prepared.AdditionalFailures).ToArray(), archiveXlSources.Sources.Length);
            code = new CodeProfileAnalysis(CodeAnalysisSchema, virtualShadowScan.Shadows, interactions, flows, stateWrites, callbacks, tweakAnalysis.Overlaps, chains, xlFailures, sourceFailures, sourceItemCount, archiveXlSources.Sources.Length, coverage, sourceCapture.Evidence, sourceCapture.TweakReferences);
            codeToCache = code;
            phases.Add(new ScanPhaseMetric("ArchiveXL", phase.ElapsedMilliseconds, code.ArchiveXlSourceCount));
        }
        else
        {
            CodeProfileAnalysis cachedCode = code!;
            progress?.Report(new ScanProgress("effective source · cache", cachedCode.SourceItemCount, Math.Max(1, cachedCode.SourceItemCount)));
            phases.Add(new ScanPhaseMetric("effective source", phase.ElapsedMilliseconds, cachedCode.SourceItemCount));
            phases.Add(new ScanPhaseMetric("ArchiveXL", 0, cachedCode.ArchiveXlSourceCount));
        }
        CodeProfileAnalysis codeResult = code!;
        VirtualFileShadow[] virtualShadows = codeResult.VirtualFileShadows;
        InteractionFinding[] findings = codeResult.InteractionFindings!;
        RedScriptFlowEvidence[] redScriptFlows = codeResult.RedScriptFlows;
        SharedStateWriteFinding[] sharedStateWrites = codeResult.SharedStateWrites;
        LuaCallbackEvidence[] luaCallbacks = codeResult.LuaCallbacks;
        TweakOverlap[] tweakOverlaps = codeResult.TweakOverlaps;
        ArchiveXlOperationChain[] archiveXlChains = codeResult.ArchiveXlChains;
        ArchiveXlSourceFailure[] archiveXlFailures = codeResult.ArchiveXlFailures;
        SourceAnalysisFailure[] codeSourceFailures = codeResult.SourceFailures;
        progress?.Report(new ScanProgress("ArchiveXL", 1, 1));
        phase.Restart();
        beforeFinalValidation?.Invoke();
        ProfileInputSnapshot[] requiredInputs = prepared.RequiredInputs.DistinctBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        int validationTotal = requiredInputs.Length + prepared.RequiredAbsentInputs.Length + prepared.EvidenceFiles.Length + (prepared.VortexBinding is null ? 0 : 1) + (prepared.CrossManagerBinding is null ? 0 : 1);
        int validationCompleted = 0;
        progress?.Report(new ScanProgress("validation", validationCompleted, Math.Max(1, validationTotal)));
        foreach (ProfileInputSnapshot input in requiredInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileInputGuard.RequireUnchanged(input);
            progress?.Report(new ScanProgress("validation", ++validationCompleted, Math.Max(1, validationTotal)));
        }
        foreach (string path in prepared.RequiredAbsentInputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProfileInputGuard.RequireStillAbsent(path);
            progress?.Report(new ScanProgress("validation", ++validationCompleted, Math.Max(1, validationTotal)));
        }
        int validationBase = validationCompleted;
        ProfileInputGuard.RequireAllUnchanged(prepared.EvidenceFiles, cancellationToken, value => progress?.Report(new ScanProgress("validation", validationBase + value.Completed, Math.Max(1, validationTotal), value.CurrentItem, value.BytesCompleted, value.BytesTotal, value.BytesCompleted, phase.ElapsedMilliseconds)));
        validationCompleted += prepared.EvidenceFiles.Length;
        if (prepared.VortexBinding is not null)
        {
            VortexManagerContext current = VortexManagerContextStore.Read(prepared.VortexBinding.ContextPath);
            if (!string.Equals(current.ContextId, prepared.VortexBinding.ContextId, StringComparison.Ordinal) || !string.Equals(current.ProfileId, prepared.VortexBinding.ProfileId, StringComparison.Ordinal)) throw new ProfileInputChangedException("The active Vortex profile or deployment changed during the scan. Run the scan again to produce one consistent result.");
            progress?.Report(new ScanProgress("validation", ++validationCompleted, Math.Max(1, validationTotal)));
        }
        if (prepared.CrossManagerBinding is not null)
        {
            VortexDeploymentGuard.RequireNoDeployment(prepared.CrossManagerBinding.GameRoot, prepared.CrossManagerBinding.ContextPath);
            progress?.Report(new ScanProgress("validation", ++validationCompleted, Math.Max(1, validationTotal)));
        }
        if (packedToCache is not null) analysisCache.Write("packed", packedCacheKey, packedToCache);
        if (codeToCache is not null) analysisCache.Write("code", codeCacheKey, codeToCache);
        phases.Add(new ScanPhaseMetric("validation", phase.ElapsedMilliseconds, validationTotal));
        total.Stop();
        int maxConflictProviders = resourceConflicts.Select(value => value.Providers.Length).DefaultIfEmpty(0).Max();
        long competitorReferences = resourceConflicts.Sum(value => (long)value.Providers.Length * Math.Max(0, value.Providers.Length - 1));
        ScanScaleMetrics scale = new(prepared.DeploymentProviders.Length, prepared.Archives.Archives.Length, prepared.Archives.Archives.Sum(value => value.Size), prepared.EvidenceFiles.Length, packed!.IndexedResourceCount, codeResult.SourceItemCount, maxConflictProviders, competitorReferences);
        return new ProfileScanReceipt(
            2,
            prepared.ProfileName,
            scannedAtUtc,
            prepared.ActiveProviders,
            prepared.Archives.EffectiveOrder,
            archiveFailures,
            resourceConflicts,
            virtualShadows,
            findings,
            redScriptFlows,
            sharedStateWrites,
            luaCallbacks,
            tweakOverlaps,
            archiveXlChains,
            archiveXlFailures,
            codeSourceFailures.Concat(prepared.AdditionalFailures).ToArray(),
            new ProfileScanMetrics(total.ElapsedMilliseconds, phases.ToArray(), PackedCacheHits: packedCacheHit ? 1 : 0, CodeCacheHits: codeCacheHit ? 1 : 0, Scale: scale, VortexBridge: prepared.VortexBridge),
            prepared.InstallationId,
            archiveSummaries,
            prepared.Archives.OrderEvidence,
            pathIndexEvidence,
            archiveWarnings,
            prepared.Archives.Archives,
            prepared.LegacyArchives.Archives,
            prepared.LegacyArchives.EffectiveOrder,
            prepared.LegacyArchives.OrderEvidence,
            prepared.ManagerKind,
            prepared.ManagerContextPath,
            prepared.DeploymentFresh,
            codeResult.CodeCoverage,
            typeof(ProfileScanCoordinator).Assembly.GetName().Version?.ToString(3),
            $"packed-{PackedAnalysisSchema}/code-{CodeAnalysisSchema}")
        {
            SourceProviders = prepared.DeploymentProviders,
            CodeEvidence = codeResult.CodeEvidence ?? [],
            TweakReferences = codeResult.TweakReferences ?? TweakReferenceIndex.Empty
        };
    }

    private static string PackedCacheKey(PreparedProfileScan prepared, IReadOnlyList<string> exclusions)
    {
        List<string> values = [$"packed-schema={PackedAnalysisSchema}", $"build={typeof(ProfileScanCoordinator).Assembly.ManifestModule.ModuleVersionId:N}", prepared.ManagerKind.ToString()];
        values.AddRange(prepared.DeploymentProviders.Select((provider, position) => $"provider|{position}|{provider.Name}|{provider.RootPath}|{provider.ManagerId}"));
        if (prepared.DeployedWinners is not null) values.AddRange(prepared.DeployedWinners.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase).Select(value => $"winner|{value.Key}|{value.Value}"));
        values.AddRange(prepared.Archives.Archives.Select(value => $"archive|{value.ArchiveName}|{value.Provider}|{value.LogicalProvider}|{value.Size}|{value.Sha256}"));
        values.AddRange(prepared.EvidenceFiles.Where(value => value.Path.EndsWith("usedhashes.kark", StringComparison.OrdinalIgnoreCase) || value.Path.EndsWith("oo2ext_7_win64.dll", StringComparison.OrdinalIgnoreCase)).OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).Select(value => $"path-source|{value.Path}|{value.Length}|{value.Sha256}"));
        values.AddRange(exclusions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => "excluded|" + value));
        return ContentAddressedAnalysisCache.Key(values);
    }

    private static string CodeCacheKey(PreparedProfileScan prepared, IReadOnlyList<string> exclusions)
    {
        Dictionary<string, ProfileFileSnapshot> fingerprints = prepared.EvidenceFiles.Where(value => value.Sha256 is not null).ToDictionary(value => value.Path, StringComparer.OrdinalIgnoreCase);
        List<string> values = [$"code-schema={CodeAnalysisSchema}", $"build={typeof(ProfileScanCoordinator).Assembly.ManifestModule.ModuleVersionId:N}", prepared.ManagerKind.ToString()];
        values.AddRange(prepared.DeploymentProviders.Select((provider, position) => $"provider|{position}|{provider.Name}|{provider.RootPath}|{provider.ManagerId}"));
        if (prepared.DeployedWinners is not null) values.AddRange(prepared.DeployedWinners.OrderBy(value => value.Key, StringComparer.OrdinalIgnoreCase).Select(value => $"winner|{value.Key}|{value.Value}"));
        foreach (DeploymentFileEntry file in prepared.FileManifest.Files.OrderBy(value => value.ProviderPosition).ThenBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            fingerprints.TryGetValue(file.PhysicalPath, out ProfileFileSnapshot? fingerprint);
            values.Add($"file|{file.ProviderPosition}|{file.Provider.Name}|{file.Provider.ManagerId}|{file.RelativePath}|{file.ArchiveXlFallbackRoot}|{fingerprint?.Length}|{fingerprint?.Sha256}");
        }
        values.AddRange(prepared.FileManifest.Failures.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).Select(value => $"failure|{value.Provider}|{value.Lane}|{value.Path}|{value.Message}"));
        values.AddRange(exclusions.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Select(value => "excluded|" + value));
        return ContentAddressedAnalysisCache.Key(values);
    }

    private static ProfileInputSnapshot[] EvidenceSnapshots(ArchiveOrderEvidence? evidence)
        => (evidence?.SourceFingerprints ?? []).Select(value => new ProfileInputSnapshot(value.Key, value.Value, [])).ToArray();

    private static EvidenceFileCapture EvidenceFileSnapshots(Mo2ArchiveProfile archives, DeploymentFileManifest manifest, string? gameRoot, CancellationToken cancellationToken)
    {
        List<ProfileFileSnapshot> files = [];
        List<SourceAnalysisFailure> failures = [];
        HashSet<string> unavailablePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (Mo2Archive archive in archives.Archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { files.Add(ProfileInputGuard.CaptureFile(archive.PhysicalPath, archive.Sha256, archive.FingerprintSource)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProfileInputChangedException)
            {
                unavailablePaths.Add(Path.GetFullPath(archive.PhysicalPath));
                failures.Add(new SourceAnalysisFailure(archive.Provider, archive.ArchiveName, "Packed archive", exception.Message));
            }
        }

        string[] relativeRoots = ["archive\\pc\\mod", "bin\\x64\\plugins", "engine\\config", "r6\\input", "r6\\scripts", "r6\\tweaks", "red4ext\\plugins"];
        string[] sourceExtensions = [".reds", ".lua", ".tweak", ".yaml", ".yml", ".xl"];
        Dictionary<string, List<DeploymentFileEntry>> candidates = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, DeploymentFileEntry> required = new(StringComparer.OrdinalIgnoreCase);
        foreach (DeploymentFileEnumerationFailure failure in manifest.Failures)
        {
            unavailablePaths.Add(Path.GetFullPath(failure.Path));
            failures.Add(new SourceAnalysisFailure(failure.Provider, failure.Lane, "Deployment evidence", failure.Message));
        }
        foreach (DeploymentFileEntry file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool knownRoot = relativeRoots.Any(root => file.RelativePath.Equals(root, StringComparison.OrdinalIgnoreCase) || file.RelativePath.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase));
            if (!knownRoot && !string.Equals(Path.GetExtension(file.PhysicalPath), ".xl", StringComparison.OrdinalIgnoreCase)) continue;
            if (DeploymentFilePolicy.IsMutableOutput(file.RelativePath)) continue;
            if (!candidates.TryGetValue(file.RelativePath, out List<DeploymentFileEntry>? values)) candidates[file.RelativePath] = values = [];
            values.Add(file);
            if (sourceExtensions.Contains(Path.GetExtension(file.PhysicalPath), StringComparer.OrdinalIgnoreCase)) required[file.PhysicalPath] = file;
            if (file.RelativePath.Equals("bin\\x64\\plugins\\cyber_engine_tweaks\\tweakdb\\usedhashes.kark", StringComparison.OrdinalIgnoreCase)) required[file.PhysicalPath] = file;
        }
        foreach (List<DeploymentFileEntry> values in candidates.Values.Where(value => value.Count > 1)) foreach (DeploymentFileEntry file in values) required[file.PhysicalPath] = file;
        foreach ((string path, DeploymentFileEntry file) in required)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { files.Add(manifest.Capture(file, true, cancellationToken)); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProfileInputChangedException)
            {
                unavailablePaths.Add(Path.GetFullPath(path));
                failures.Add(new SourceAnalysisFailure("Deployment evidence", Path.GetFileName(path), "Evidence snapshot", exception.Message));
            }
        }
        if (gameRoot is not null)
        {
            string oodlePath = Path.Combine(gameRoot, "bin", "x64", "oo2ext_7_win64.dll");
            if (File.Exists(oodlePath))
            {
                try { files.Add(ProfileInputGuard.CaptureFile(oodlePath, true)); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ProfileInputChangedException)
                {
                    unavailablePaths.Add(Path.GetFullPath(oodlePath));
                    failures.Add(new SourceAnalysisFailure("Game directory", Path.GetFileName(oodlePath), "Evidence snapshot", exception.Message));
                }
            }
        }
        return new EvidenceFileCapture(files.DistinctBy(value => value.Path, StringComparer.OrdinalIgnoreCase).ToArray(), failures.ToArray(), unavailablePaths.ToArray());
    }

    private sealed record EvidenceFileCapture(ProfileFileSnapshot[] Files, SourceAnalysisFailure[] Failures, string[] UnavailablePaths);

    private sealed record PreparedProfileScan(ModManagerKind ManagerKind, string ProfileName, string[] ActiveProviders, string InstallationId, Mo2InstancePaths Paths, DeploymentProvider[] DeploymentProviders, DeploymentFileManifest FileManifest, Mo2ArchiveProfile LegacyArchives, RedmodArchiveProfile Redmods, Mo2ArchiveProfile Archives, IReadOnlyDictionary<string, string>? DeployedWinners, ProfileInputSnapshot[] RequiredInputs, string[] RequiredAbsentInputs, ProfileFileSnapshot[] EvidenceFiles, string[] UnavailableEvidencePaths, string? ManagerContextPath, bool DeploymentFresh, long DeploymentElapsedMilliseconds, SourceAnalysisFailure[]? Failures = null, VortexContextBinding? VortexBinding = null, CrossManagerGuardBinding? CrossManagerBinding = null, VortexBridgeScanMetrics? VortexBridge = null)
    {
        public SourceAnalysisFailure[] AdditionalFailures => Failures ?? [];
    }

    private sealed record VortexContextBinding(string ContextPath, string ContextId, string ProfileId);
    private sealed record CrossManagerGuardBinding(string GameRoot, string ContextPath);
}

public static class ProfileInstallationIdentity
{
    public static string Create(string mo2Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        string identity = Path.GetFullPath(mo2Root).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    public static string Create(string manager, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manager);
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        string value = manager.ToUpperInvariant() + "|" + identity.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }
}
