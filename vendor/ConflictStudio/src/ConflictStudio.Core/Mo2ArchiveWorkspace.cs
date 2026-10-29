namespace ConflictStudio.Core;

public sealed record Mo2ArchiveRow(string Name, string Provider, string PhysicalPath, int Position, bool Editable, int Winning, int Losing, int Same, int Unknown, int Unique);
public sealed record Mo2ArchivePaletteEntry(string Tone, string? ColorHex, string? BackgroundHex);
public sealed record Mo2ArchiveWorkspaceState(string Token, string ProfilePath, string ProfileName, string? OrderSource, string WriteTarget, string? WriteBlockedReason, string[] EffectiveOrder, string[] EditableOrder, Mo2ArchiveRow[] ArchiveRows, bool CanUndo, RdarArchiveFailure[] ArchiveFailures, RdarArchiveWarning[] ArchiveWarnings, string OrderMessage, Mo2ArchivePaletteEntry[] Palette, bool NeedsOrderWrite, string[] ArchiveDirectories);
public sealed record Mo2ArchivePreview(string[] ChangedArchives, ArchiveWinnerDelta[] WinnerDeltas, int TotalWinnerDeltas, bool WinnerChangesKnown, bool Truncated, bool CanApply, string? BlockedReason, UnknownArchiveOrderImpact UnknownImpact);
public sealed record Mo2ArchiveSharedResource(string ResourceHash, string DisplayName, string Kind, string WinnerArchive, string[] Archives, bool Truncated);
public sealed record Mo2ArchiveSelection(ArchiveRelationship[] Relationships, Mo2ArchiveSharedResource[] SharedResources, int TotalSharedResources, bool Truncated, int Offset, int Limit);

public sealed class Mo2ArchiveWorkspace
{
    private readonly ArchiveOrderWriter _writer;
    private readonly string _cacheRoot;
    private Snapshot? _snapshot;
    private ArchiveOrderPreview? _preview;
    private bool _previewAcknowledged;
    private ArchiveOrderApplyResult? _lastApply;
    private string? _lastPreviousSha256;
    private string? _projectionToken;
    private string[] _projectionOrder = [];
    private ResourceConflict[] _projectionConflicts = [];
    private ArchiveConflictSummary[] _projectionSummaries = [];
    private int _operation;

    public Mo2ArchiveWorkspace() : this(new ArchiveOrderWriter(() => DateTimeOffset.UtcNow)) { }

    public Mo2ArchiveWorkspace(ArchiveOrderWriter writer) : this(writer, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cyberpunk Conflict Studio", "cache")) { }

    internal Mo2ArchiveWorkspace(ArchiveOrderWriter writer, string cacheRoot)
    {
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
        _cacheRoot = Path.GetFullPath(cacheRoot);
    }

    public Mo2ArchiveWorkspaceState Scan(string instanceRoot, string profileModlistPath, IReadOnlyList<Mo2ActiveProvider>? activeProviders = null, IProgress<ScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            _snapshot = null;
            ClearProjection();
            _preview = null;
            _lastApply = null;
            _lastPreviousSha256 = null;
            _snapshot = BuildSnapshot(instanceRoot, profileModlistPath, activeProviders, progress, cancellationToken);
            return State(_snapshot);
        }
        finally { ExitOperation(); }
    }

    public Mo2ArchiveSelection Select(string token, IReadOnlyList<string> selectedArchives, IReadOnlyList<string>? proposedOrder = null, int offset = 0, int limit = 1000)
    {
        Snapshot snapshot = RequireSnapshot(token);
        ArgumentNullException.ThrowIfNull(selectedArchives);
        string[] order = proposedOrder?.ToArray() ?? snapshot.Legacy.EffectiveOrder;
        ArchiveOrderPlanner.RequireComplete(snapshot.Observation.Archives, order);
        HashSet<string> names = snapshot.Combined.EffectiveOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selectedArchives.Any(value => !names.Contains(value))) throw new ArchiveOrderException("The selection contains an archive outside this scan.");
        string[] combinedOrder = ArchiveRelationshipProjection.ComposeCombinedOrder(order, snapshot.Combined.EffectiveOrder);
        ArchiveRelationship[] relationships = ArchiveRelationshipProjection.BuildRelationships(combinedOrder, combinedOrder, snapshot.Packed.Resources, selectedArchives,
            snapshot.Combined.OrderEvidence?.Kind == ArchiveOrderEvidenceKind.Unresolved ? snapshot.Combined.OrderEvidence.ProblemLane : ArchiveOrderProblemLane.None, snapshot.Legacy.OrderEvidence?.UnlistedArchives);
        HashSet<string> selected = selectedArchives.ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnsureProjection(snapshot, combinedOrder);
        ResourceConflict[] conflicts = _projectionConflicts;
        ResourceConflict[] shared = conflicts.Where(value => value.Providers.Any(provider => selected.Contains(provider.ArchiveName))).ToArray();
        int start = Math.Clamp(offset, 0, shared.Length);
        int count = Math.Clamp(limit, 1, 1000);
        Mo2ArchiveSharedResource[] page = shared.Skip(start).Take(count).Select(value => new Mo2ArchiveSharedResource(value.ResourceHash.ToString(System.Globalization.CultureInfo.InvariantCulture), value.DisplayName, value.Kind.ToString(), value.EngineWinnerArchive, value.Providers.Select(provider => provider.ArchiveName).Take(32).ToArray(), value.Providers.Length > 32)).ToArray();
        return new Mo2ArchiveSelection(relationships, page, shared.Length, start + page.Length < shared.Length, start, count);
    }

    public Mo2ArchivePreview Preview(string token, IReadOnlyList<string> order, bool unknownImpactAcknowledged = false)
    {
        Snapshot snapshot = RequireSnapshot(token);
        ArchiveOrderPreview preview = ArchiveOrderPlanner.CreatePreview(snapshot.Observation, order);
        string[] combined = ArchiveRelationshipProjection.ComposeCombinedOrder(preview.ProposedOrder, snapshot.Combined.EffectiveOrder);
        UnknownArchiveOrderImpact unknown = ArchiveUnknownImpactPolicy.Evaluate(snapshot.Combined.EffectiveOrder, combined, snapshot.Packed.Failures.Select(value => value.ArchiveName).ToArray());
        string? blocked = snapshot.WriteBlockedReason;
        if (unknown == UnknownArchiveOrderImpact.BlockedCrossing) blocked = "This move passes an unreadable archive. Repair that archive and scan again before saving.";
        else if (unknown == UnknownArchiveOrderImpact.RequiresAcknowledgement && !unknownImpactAcknowledged) blocked = "Acknowledge that some archive effects cannot be previewed before saving.";
        bool winnerChangesKnown = snapshot.Legacy.OrderEvidence?.UnlistedArchives.Length is not > 0 && snapshot.Combined.OrderEvidence?.Kind != ArchiveOrderEvidenceKind.Unresolved;
        ArchiveWinnerDelta[] deltas = winnerChangesKnown ? ArchiveOrderImpactAnalyzer.Analyze(snapshot.Packed.Resources, snapshot.Combined.EffectiveOrder, combined) : [];
        _preview = preview;
        _previewAcknowledged = unknownImpactAcknowledged;
        return new Mo2ArchivePreview(preview.ChangedArchives, deltas.Take(1000).ToArray(), deltas.Length, winnerChangesKnown, deltas.Length > 1000, blocked is null, blocked, unknown);
    }

    public Mo2ArchiveWorkspaceState Apply(string token, IReadOnlyList<string> order, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            Snapshot snapshot = RequireSnapshot(token);
            if (_preview is null || !_preview.ProposedOrder.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) throw new ArchiveOrderException("Preview this exact archive order before saving it.");
            Mo2ArchivePreview checkedPreview = Preview(token, order, _previewAcknowledged);
            if (!checkedPreview.CanApply) throw new ArchiveOrderException(checkedPreview.BlockedReason!);
            ArchiveOrderPreview preview = _preview!;
            RequireContext(snapshot, true, cancellationToken);
            Mo2ArchiveProfile refreshed = RefreshAndRequireArchives(snapshot, cancellationToken);
            ArchiveOrderApplyResult result = _writer.Apply(preview, Fingerprints(refreshed), () =>
            {
                RequireContext(snapshot, true, cancellationToken);
                Mo2ArchiveProfile current = RefreshAndRequireArchives(snapshot, cancellationToken);
                RequireContext(snapshot, true, cancellationToken);
                return Fingerprints(current);
            });
            try
            {
                RequireContext(snapshot, false, CancellationToken.None);
                Snapshot written = BuildSnapshot(snapshot.Root, snapshot.ProfilePath, snapshot.ActiveProviders, null, CancellationToken.None, snapshot.Packed);
                RequireSameArchiveSet(snapshot.Combined, written.Combined);
                if (!written.Legacy.EffectiveOrder.SequenceEqual(preview.ProposedOrder, StringComparer.OrdinalIgnoreCase)
                    || !string.Equals(written.Legacy.OrderEvidence?.SourcePath, snapshot.Target.ModlistPath, StringComparison.OrdinalIgnoreCase))
                    throw new ArchiveOrderException("The saved profile archive order did not become the effective order.");
                _snapshot = written;
                _lastApply = result;
                _lastPreviousSha256 = snapshot.Observation.OrderFileSha256;
                _preview = null;
                return State(written);
            }
            catch (Exception exception)
            {
                try { _writer.RestorePrevious(result, snapshot.Target.ModlistPath, () => RequireBackup(result, snapshot.Observation.OrderFileSha256)); }
                catch (Exception rollbackException) { _snapshot = null; throw new ArchiveOrderException($"Post-save verification failed: {exception.Message} Rollback also failed: {rollbackException.Message}"); }
                _snapshot = null;
                _lastApply = null;
                _lastPreviousSha256 = null;
                throw new ArchiveOrderException($"Post-save verification failed and the previous archive order was restored: {exception.Message}");
            }
        }
        finally { ExitOperation(); }
    }

    public Mo2ArchiveWorkspaceState Undo(string token, CancellationToken cancellationToken = default)
    {
        EnterOperation();
        try
        {
            Snapshot snapshot = RequireSnapshot(token);
            ArchiveOrderApplyResult previous = _lastApply ?? throw new ArchiveOrderException("There is no archive-order save to undo in this session.");
            RequireContext(snapshot, true, cancellationToken);
            RefreshAndRequireArchives(snapshot, cancellationToken);
            RequireBackup(previous, _lastPreviousSha256);
            _writer.RestorePrevious(previous, snapshot.Target.ModlistPath, () =>
            {
                RequireContext(snapshot, true, cancellationToken);
                RefreshAndRequireArchives(snapshot, cancellationToken);
                RequireContext(snapshot, true, cancellationToken);
                RequireBackup(previous, _lastPreviousSha256);
            });
            _lastApply = null;
            _lastPreviousSha256 = null;
            _preview = null;
            _snapshot = BuildSnapshot(snapshot.Root, snapshot.ProfilePath, snapshot.ActiveProviders, null, CancellationToken.None, snapshot.Packed);
            return State(_snapshot);
        }
        finally { ExitOperation(); }
    }

    private Snapshot BuildSnapshot(string instanceRoot, string profileModlistPath, IReadOnlyList<Mo2ActiveProvider>? suppliedProviders, IProgress<ScanProgress>? progress, CancellationToken cancellationToken, PackedAnalysis? reusable = null, bool forceFingerprint = false)
    {
        string root = Path.GetFullPath(instanceRoot);
        string profilePath = Path.GetFullPath(profileModlistPath);
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(root);
        Mo2ArchiveWriteTarget target = Mo2ArchiveWriteTargetResolver.ResolveProfile(root, profilePath);
        ProfileInputSnapshot profileInput = ProfileInputGuard.Capture(profilePath);
        Mo2ActiveProvider[] active = suppliedProviders?.ToArray() ?? Mo2ProfileReader.ReadActiveProviderEntries(profileInput.Content);
        if (active.Any(value => string.IsNullOrWhiteSpace(value.Name) || value.Name != Path.GetFileName(value.Name) || value.Name.Contains('\\') || value.Name.Contains('/') || value.Name is "." or "..")
            || active.Select(value => value.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != active.Length) throw new ArchiveOrderException("The active provider list contains invalid or duplicate mod names.");
        RequireNoCrossManager(target);
        string cachePath = Path.Combine(_cacheRoot, "archive-fingerprints-v1.json");
        Mo2ArchiveProfile legacy = Mo2ArchiveProfileScanner.ScanInstance(root, profilePath, active, cachePath, forceFingerprint, progress, cancellationToken);
        DeploymentProvider[] providers = Mo2ProviderTopology.Discover(root, active);
        string[] archiveDirectories = new[] { paths.OverwriteRoot }.Concat(active.Select(value => Path.Combine(paths.ModsRoot, value.Name)))
            .Concat(paths.GameRoot is null ? [] : new[] { paths.GameRoot }).Select(value => Path.Combine(value, "archive", "pc", "mod")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        RedmodArchiveProfile redmods = RedmodArchiveProfileScanner.Scan(providers, null, progress, cancellationToken);
        Mo2ArchiveProfile combined = PackedArchiveTopology.Compose(legacy, redmods);
        ArchiveOrderObservation observation = ManagedArchiveOrderObserver.Observe(legacy, target, true) with { EffectiveOrder = legacy.EffectiveOrder };
        List<ProfileFileSnapshot> files = combined.Archives.Select(value => ProfileInputGuard.CaptureFile(value.PhysicalPath, value.Sha256, value.FingerprintSource)).ToList();
        List<ProfileFileSnapshot> auxiliaryFiles = [];
        List<string> absentAuxiliary = [];
        string? decoderPath = paths.GameRoot is null ? null : Path.Combine(paths.GameRoot, "bin", "x64", "oo2ext_7_win64.dll");
        if (decoderPath is not null)
        {
            if (File.Exists(decoderPath)) auxiliaryFiles.Add(ProfileInputGuard.CaptureFile(decoderPath, true));
            else absentAuxiliary.Add(decoderPath);
        }
        foreach (DeploymentProvider provider in providers)
        {
            string hashPath = Path.Combine(provider.RootPath, "bin", "x64", "plugins", "cyber_engine_tweaks", "tweakdb", "usedhashes.kark");
            if (File.Exists(hashPath)) { auxiliaryFiles.Add(ProfileInputGuard.CaptureFile(hashPath, true)); break; }
            absentAuxiliary.Add(hashPath);
        }
        files.AddRange(auxiliaryFiles);
        string key = ContentAddressedAnalysisCache.Key(new[] { "mo2-archives-v1", typeof(Mo2ArchiveWorkspace).Assembly.ManifestModule.ModuleVersionId.ToString("N") }
            .Concat(providers.Select(value => value.Name + "|" + value.RootPath))
            .Concat(files.OrderBy(value => value.Path, StringComparer.OrdinalIgnoreCase).Select(value => value.Path + "|" + value.Sha256)));
        ContentAddressedAnalysisCache cache = new(Path.Combine(_cacheRoot, "analysis-v1"));
        PackedAnalysis? packed = reusable?.Key == key ? reusable : null;
        bool cacheHit = packed is not null || cache.TryRead("mo2-archives", key, out packed) && packed?.Key == key;
        if (!cacheHit)
        {
            RdarResourceScanResult read = RdarResourceScanner.ScanResilient(combined.Archives.Select(value => new RdarArchiveInput(value.Provider, value.PhysicalPath, value.ArchiveName, value.LogicalProvider)).ToArray(), null, decoderPath, progress, cancellationToken);
            ResourcePathIndexResult pathsIndex = ResourcePathIndex.Resolve(paths, providers, read.Resources.Select(value => value.ResourceHash).ToHashSet(), cancellationToken);
            ResourceProvider[] resources = ResourcePathIndex.Apply(read.Resources, pathsIndex.Paths);
            resources = RdarPayloadFingerprint.Apply(resources, read.PayloadIndexes ?? [], decoderPath, cancellationToken);
            packed = new PackedAnalysis(key, resources, read.Failures, read.Warnings ?? []);
        }
        else progress?.Report(new ScanProgress("packed resources · cache", packed!.Resources.Length, Math.Max(1, packed.Resources.Length)));
        ProfileInputSnapshot[] evidence = (combined.OrderEvidence?.SourceFingerprints ?? []).Select(value => new ProfileInputSnapshot(value.Key, value.Value, [])).ToArray();
        string[] absent = combined.OrderEvidence?.AbsentSources ?? [];
        try
        {
            ProfileInputGuard.RequireUnchanged(profileInput);
            RequireSameInstancePaths(paths, Mo2InstancePathResolver.Resolve(root));
            foreach (ProfileInputSnapshot input in evidence) ProfileInputGuard.RequireUnchanged(input);
            foreach (string path in absent) ProfileInputGuard.RequireStillAbsent(path);
            foreach (string path in absentAuxiliary) ProfileInputGuard.RequireStillAbsent(path);
            ProfileInputGuard.RequireAllUnchanged(files, cancellationToken, value => progress?.Report(new ScanProgress("validation", value.Completed, value.Total, value.CurrentItem, value.BytesCompleted, value.BytesTotal)));
            RequireNoCrossManager(target);
        }
        catch (CachedFingerprintMismatchException) when (!forceFingerprint)
        {
            return BuildSnapshot(root, profilePath, suppliedProviders, progress, cancellationToken, null, true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!cacheHit) cache.Write("mo2-archives", key, packed);
        string? blocked = target.WriteBlockedReason;
        if (active.Any(value => !Directory.Exists(Path.Combine(paths.ModsRoot, value.Name)))) blocked = "An active mod directory is missing. Restore it or disable that mod, then scan again.";
        if (legacy.OrderEvidence?.IncompleteArchiveLane != ArchiveOrderProblemLane.None) blocked = "The active archive inventory is incomplete. Resolve the scan errors before saving.";
        if (legacy.OrderEvidence?.Kind == ArchiveOrderEvidenceKind.Unresolved && legacy.OrderEvidence.IsRepairableLegacyOrder != true) blocked = legacy.OrderEvidence.Message;
        return new Snapshot(Guid.NewGuid().ToString("N"), root, profilePath, active, profileInput, paths, archiveDirectories, evidence, absent, auxiliaryFiles.ToArray(), absentAuxiliary.ToArray(), legacy, combined, target, observation, packed!, blocked);
    }

    private static void RequireContext(Snapshot snapshot, bool includeOrderEvidence, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireSameInstancePaths(snapshot.InstancePaths, Mo2InstancePathResolver.Resolve(snapshot.Root));
        string target = Mo2ProfileArchiveOrder.Resolve(snapshot.Root, snapshot.ProfilePath);
        if (!string.Equals(target, snapshot.Target.ModlistPath, StringComparison.OrdinalIgnoreCase)) throw new ArchiveOrderException("The profile archive-order target changed. Scan again before saving.");
        ProfileInputGuard.RequireUnchanged(snapshot.ProfileInput);
        Mo2ArchiveWriteTarget current = Mo2ArchiveWriteTargetResolver.ResolveProfile(snapshot.Root, snapshot.ProfilePath);
        if (current.WriteBlockedReason is not null) throw new ArchiveOrderException(current.WriteBlockedReason);
        RequireNoCrossManager(current);
        foreach (ProfileFileSnapshot file in snapshot.AuxiliaryFiles) ProfileInputGuard.RequireUnchanged(file, null, cancellationToken);
        foreach (string path in snapshot.AbsentAuxiliaryFiles) ProfileInputGuard.RequireStillAbsent(path);
        if (includeOrderEvidence)
        {
            foreach (ProfileInputSnapshot input in snapshot.OrderInputs) ProfileInputGuard.RequireUnchanged(input);
            foreach (string path in snapshot.AbsentOrderInputs) ProfileInputGuard.RequireStillAbsent(path);
        }
    }

    private static Mo2ArchiveProfile RefreshAndRequireArchives(Snapshot snapshot, CancellationToken cancellationToken)
    {
        Mo2ArchiveProfile legacy = Mo2ArchiveProfileScanner.ScanInstance(snapshot.Root, snapshot.ProfilePath, snapshot.ActiveProviders, null, true, cancellationToken);
        RedmodArchiveProfile redmods = RedmodArchiveProfileScanner.Scan(Mo2ProviderTopology.Discover(snapshot.Root, snapshot.ActiveProviders), cancellationToken);
        Mo2ArchiveProfile combined = PackedArchiveTopology.Compose(legacy, redmods);
        RequireSameArchiveSet(snapshot.Combined, combined);
        return legacy;
    }

    private static void RequireSameArchiveSet(Mo2ArchiveProfile expected, Mo2ArchiveProfile current)
    {
        if (current.OrderEvidence?.IncompleteArchiveLane != ArchiveOrderProblemLane.None) throw new ArchiveOrderException("The active archive inventory is incomplete. Scan again before saving.");
        Dictionary<string, Mo2Archive> names = current.Archives.ToDictionary(value => value.ArchiveName, StringComparer.OrdinalIgnoreCase);
        if (names.Count != expected.Archives.Length || expected.Archives.Any(value => !names.TryGetValue(value.ArchiveName, out Mo2Archive? now)
            || now.Size != value.Size || !string.Equals(now.Sha256, value.Sha256, StringComparison.Ordinal)
            || !string.Equals(now.PhysicalPath, value.PhysicalPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(now.Provider, value.Provider, StringComparison.OrdinalIgnoreCase))) throw new ArchiveOrderException("The active archive inventory changed after the scan. Scan again before saving.");
        if (!expected.EffectiveOrder.Where(value => value.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase)).SequenceEqual(current.EffectiveOrder.Where(value => value.StartsWith("REDmod/", StringComparison.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase)) throw new ArchiveOrderException("The REDmod archive order changed after the scan. Scan again before saving.");
    }

    private static void RequireNoCrossManager(Mo2ArchiveWriteTarget target)
    {
        if (target.GameRoot is not null) VortexDeploymentGuard.RequireNoDeployment(target.GameRoot, target.CrossManagerContextPath ?? VortexDeploymentGuard.DefaultContextPath);
    }

    private static void RequireSameInstancePaths(Mo2InstancePaths expected, Mo2InstancePaths current)
    {
        if (!string.Equals(expected.Root, current.Root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ModsRoot, current.ModsRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.ProfilesRoot, current.ProfilesRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.OverwriteRoot, current.OverwriteRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.GameRoot, current.GameRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.SelectedProfile, current.SelectedProfile, StringComparison.OrdinalIgnoreCase)
            || expected.EnforcesArchiveOrder != current.EnforcesArchiveOrder)
            throw new ProfileInputChangedException("The MO2 instance paths, selected profile, or archive-order generator changed. Scan again before saving or undoing.");
    }

    private static void RequireBackup(ArchiveOrderApplyResult result, string? expectedSha256)
    {
        if (!result.TargetPreviouslyExisted) return;
        if (result.BackupPath is null || expectedSha256 is null || !string.Equals(ProfileInputGuard.Capture(result.BackupPath).Sha256, expectedSha256, StringComparison.Ordinal)) throw new ArchiveOrderException("The previous archive-order backup changed. Undo was stopped to preserve the current order.");
    }

    private static ArchiveFingerprint[] Fingerprints(Mo2ArchiveProfile profile) => profile.Archives.Select(value => new ArchiveFingerprint(value.ArchiveName, value.Size, value.Sha256)).ToArray();

    private void EnsureProjection(Snapshot snapshot, string[] order)
    {
        if (_projectionToken == snapshot.Token && _projectionOrder.SequenceEqual(order, StringComparer.OrdinalIgnoreCase)) return;
        _projectionSummaries = ArchiveResourceIndexBuilder.Build(snapshot.Packed.Resources, snapshot.Combined.Archives, order, snapshot.Packed.Failures,
            snapshot.Combined.OrderEvidence?.Kind == ArchiveOrderEvidenceKind.Unresolved ? snapshot.Combined.OrderEvidence.ProblemLane : ArchiveOrderProblemLane.None,
            snapshot.Combined.OrderEvidence?.IncompleteArchiveLane ?? ArchiveOrderProblemLane.None, snapshot.Legacy.OrderEvidence?.UnlistedArchives);
        _projectionConflicts = ResourceConflictAnalyzer.Analyze(snapshot.Packed.Resources, order, snapshot.Packed.Failures,
            snapshot.Combined.OrderEvidence?.Kind == ArchiveOrderEvidenceKind.Unresolved ? snapshot.Combined.OrderEvidence.ProblemLane : ArchiveOrderProblemLane.None,
            snapshot.Combined.OrderEvidence?.IncompleteArchiveLane ?? ArchiveOrderProblemLane.None, snapshot.Legacy.OrderEvidence?.UnlistedArchives);
        _projectionOrder = order.ToArray();
        _projectionToken = snapshot.Token;
    }

    private void ClearProjection()
    {
        _projectionToken = null;
        _projectionOrder = [];
        _projectionSummaries = [];
        _projectionConflicts = [];
    }

    private Mo2ArchiveWorkspaceState State(Snapshot snapshot)
    {
        HashSet<string> editable = snapshot.Legacy.EffectiveOrder.ToHashSet(StringComparer.OrdinalIgnoreCase);
        EnsureProjection(snapshot, snapshot.Combined.EffectiveOrder);
        Mo2ArchiveRow[] rows = _projectionSummaries.Select(value => new Mo2ArchiveRow(value.ArchiveName, value.Provider, value.PhysicalPath ?? string.Empty, value.OrderPosition ?? 0, editable.Contains(value.ArchiveName), value.Winning.Length, value.Losing.Length, value.Redundant.Length, value.Unresolved.Length, value.Unique.Length)).ToArray();
        Mo2ArchivePaletteEntry[] palette = Enum.GetValues<ArchiveRelationshipKind>().Where(value => value != ArchiveRelationshipKind.None).Select(value => new Mo2ArchivePaletteEntry(value.ToString(), ArchiveRelationshipPalette.Border(value), ArchiveRelationshipPalette.Background(value))).ToArray();
        bool needsOrderWrite = snapshot.Observation.OrderFileSha256 is null || snapshot.Legacy.OrderEvidence?.UnlistedArchives.Length > 0 || snapshot.Legacy.OrderEvidence?.IsRepairableLegacyOrder == true;
        return new Mo2ArchiveWorkspaceState(snapshot.Token, snapshot.ProfilePath, snapshot.Legacy.ProfileName, snapshot.Legacy.OrderEvidence?.SourcePath, snapshot.Target.ModlistPath, snapshot.WriteBlockedReason, snapshot.Combined.EffectiveOrder, snapshot.Legacy.EffectiveOrder, rows, _lastApply is not null, snapshot.Packed.Failures, snapshot.Packed.Warnings, snapshot.Combined.OrderEvidence?.Message ?? string.Empty, palette, needsOrderWrite, snapshot.ArchiveDirectories);
    }

    private Snapshot RequireSnapshot(string token)
    {
        if (_snapshot is null || !string.Equals(token, _snapshot.Token, StringComparison.Ordinal)) throw new ArchiveOrderException("This archive scan is no longer current. Scan the active profile again.");
        return _snapshot;
    }

    private void EnterOperation()
    {
        if (Interlocked.CompareExchange(ref _operation, 1, 0) != 0) throw new ArchiveOrderException("Another archive operation is already in progress.");
    }

    private void ExitOperation() => Volatile.Write(ref _operation, 0);

    private sealed record PackedAnalysis(string Key, ResourceProvider[] Resources, RdarArchiveFailure[] Failures, RdarArchiveWarning[] Warnings);
    private sealed record Snapshot(string Token, string Root, string ProfilePath, Mo2ActiveProvider[] ActiveProviders, ProfileInputSnapshot ProfileInput, Mo2InstancePaths InstancePaths, string[] ArchiveDirectories, ProfileInputSnapshot[] OrderInputs, string[] AbsentOrderInputs, ProfileFileSnapshot[] AuxiliaryFiles, string[] AbsentAuxiliaryFiles, Mo2ArchiveProfile Legacy, Mo2ArchiveProfile Combined, Mo2ArchiveWriteTarget Target, ArchiveOrderObservation Observation, PackedAnalysis Packed, string? WriteBlockedReason);
}
