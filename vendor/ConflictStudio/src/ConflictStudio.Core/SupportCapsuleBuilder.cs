namespace ConflictStudio.Core;

public sealed record ConflictCasefile(int SchemaVersion, string ProfileName, DateTimeOffset CreatedAtUtc, string[] ActiveProviders, string[] ArchiveOrder, ResourceConflict[] ResourceConflicts, InteractionFinding[] Findings);

public sealed record SupportEvidence(
    RdarArchiveFailure[] ArchiveFailures,
    VirtualFileShadow[] VirtualFileShadows,
    RedScriptFlowEvidence[] RedScriptFlows,
    SharedStateWriteFinding[] SharedStateWrites,
    LuaCallbackEvidence[] LuaCallbacks,
    TweakOverlap[] TweakOverlaps,
    ArchiveXlOperationChain[] ArchiveXlChains,
    ArchiveXlSourceFailure[] ArchiveXlFailures,
    SourceAnalysisFailure[] SourceFailures,
    ProfileScanMetrics? Metrics,
    string? InstallationId = null,
    ArchiveConflictSummary[]? ArchiveSummaries = null,
    ArchiveOrderEvidence? ArchiveOrderEvidence = null,
    ResourcePathIndexEvidence? ResourcePathIndexEvidence = null,
    RdarArchiveWarning[]? ArchiveWarnings = null,
    ModManagerKind ManagerKind = ModManagerKind.Mo2,
    bool DeploymentFresh = true,
    CodeCoverageReceipt? CodeCoverage = null);

public sealed record SupportCapsule(int SchemaVersion, ConflictCasefile Casefile, ConflictWorkItem[] WorkQueue, SupportEvidence Evidence, EvidenceDecision[] Decisions, SupportCapsuleSummary Summary)
{
    public EvidenceNote[] Notes { get; init; } = [];
}

public sealed record SupportCapsuleSummary(int ActiveProviders, int Archives, int ArchiveFailures, int ResourceConflicts, int VirtualShadows, int InteractionFindings, int ReviewDecisions);

public static class SupportCapsuleBuilder
{
    public static SupportCapsule Build(ProfileScanReceipt receipt, IReadOnlyList<EvidenceDecision> decisions, IReadOnlyList<EvidenceNote>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(decisions);
        ConflictWorkItem[] currentItems = ConflictWorkQueueBuilder.Build(receipt, []);
        EvidenceDecision[] profileDecisions = decisions.Where(value => string.Equals(value.ProfileName, receipt.ProfileName, StringComparison.Ordinal) && string.Equals(value.InstallationId, receipt.InstallationId, StringComparison.Ordinal))
            .Where(decision => currentItems.Any(item => item.Surface == decision.Surface
                && string.Equals(item.Target, decision.Target, StringComparison.Ordinal)
                && item.Providers.SequenceEqual(decision.Providers, StringComparer.OrdinalIgnoreCase)
                && EvidenceDecisionStore.Evaluate(decision, receipt.InstallationId!, receipt.ProfileName, item.Surface, item.EvidenceSha256) == EvidenceDecisionState.Resolved))
            .ToArray();
        ConflictCasefile casefile = new(1, receipt.ProfileName, receipt.ScannedAtUtc, receipt.ActiveProviders, receipt.ArchiveOrder, receipt.ResourceConflicts, receipt.InteractionFindings);
        EvidenceNote[] profileNotes = (notes ?? []).Where(value => string.Equals(value.ProfileName, receipt.ProfileName, StringComparison.Ordinal) && string.Equals(value.InstallationId, receipt.InstallationId, StringComparison.Ordinal))
            .Where(note => currentItems.Any(item => item.Surface == note.Surface && string.Equals(item.Target, note.Target, StringComparison.Ordinal)))
            .ToArray();
        ConflictWorkItem[] workQueue = ConflictWorkQueueBuilder.Build(receipt, profileDecisions, profileNotes).Select(value => value with { Target = PrivatePathRedactor.RelativeLabel(value.Target), Summary = PrivatePathRedactor.Redact(value.Summary), NextAction = PrivatePathRedactor.Redact(value.NextAction), RelatedTargets = value.RelatedTargets.Select(PrivatePathRedactor.RelativeLabel).ToArray() }).ToArray();
        VirtualFileShadow[] shadows = receipt.VirtualFileShadows.Select(value => value with { Providers = value.Providers.Select(provider => provider with { PhysicalPath = string.Empty }).ToArray() }).ToArray();
        ArchiveConflictSummary[]? archiveSummaries = receipt.ArchiveSummaries?.Select(value => value with { PhysicalPath = null }).ToArray();
        RdarArchiveFailure[] archiveFailures = receipt.ArchiveFailures.Select(value => value with { Message = PrivatePathRedactor.Redact(value.Message) }).ToArray();
        RdarArchiveWarning[]? archiveWarnings = receipt.ArchiveWarnings?.Select(value => value with { Message = PrivatePathRedactor.Redact(value.Message) }).ToArray();
        ArchiveXlSourceFailure[] archiveXlFailures = receipt.ArchiveXlFailures.Select(value => value with { FilePath = PrivatePathRedactor.RelativeLabel(value.FilePath), Message = PrivatePathRedactor.Redact(value.Message) }).ToArray();
        SourceAnalysisFailure[] sourceFailures = (receipt.SourceFailures ?? []).Select(value => value with { FilePath = PrivatePathRedactor.RelativeLabel(value.FilePath), Message = PrivatePathRedactor.Redact(value.Message) }).ToArray();
        ArchiveOrderEvidence? archiveOrderEvidence = receipt.ArchiveOrderEvidence is null ? null : receipt.ArchiveOrderEvidence with { SourcePath = null, SourcePaths = [], SourceFingerprints = [], AbsentSources = [], Message = PrivatePathRedactor.Redact(receipt.ArchiveOrderEvidence.Message) };
        ResourcePathIndexEvidence? resourcePathIndexEvidence = receipt.ResourcePathIndexEvidence is null ? null : receipt.ResourcePathIndexEvidence with { SourcePath = null, Message = PrivatePathRedactor.Redact(receipt.ResourcePathIndexEvidence.Message) };
        SupportEvidence evidence = new(archiveFailures, shadows, receipt.RedScriptFlows, receipt.SharedStateWrites, receipt.LuaCallbacks, receipt.TweakOverlaps, receipt.ArchiveXlChains, archiveXlFailures, sourceFailures, receipt.Metrics, receipt.InstallationId, archiveSummaries, archiveOrderEvidence, resourcePathIndexEvidence, archiveWarnings, receipt.ManagerKind, receipt.DeploymentFresh, receipt.CodeCoverage);
        SupportCapsuleSummary summary = new(receipt.ActiveProviders.Length, receipt.ArchiveOrder.Length, receipt.ArchiveFailures.Length, receipt.ResourceConflicts.Length, receipt.VirtualFileShadows.Length, receipt.InteractionFindings.Length, profileDecisions.Length);
        return PrivatePathRedactor.RedactObject(new SupportCapsule(6, casefile, workQueue, evidence, profileDecisions, summary) { Notes = profileNotes });
    }
}
