namespace ConflictStudio.Core;

internal sealed record PackedProfileAnalysis(
    int SchemaVersion,
    ResourceProvider[] Resources,
    RdarArchiveFailure[] ArchiveFailures,
    RdarArchiveWarning[] ArchiveWarnings,
    ResourcePathIndexEvidence ResourcePathIndexEvidence,
    int IndexedResourceCount);

internal sealed record CodeProfileAnalysis(
    int SchemaVersion,
    VirtualFileShadow[] VirtualFileShadows,
    InteractionFinding[] InteractionFindings,
    RedScriptFlowEvidence[] RedScriptFlows,
    SharedStateWriteFinding[] SharedStateWrites,
    LuaCallbackEvidence[] LuaCallbacks,
    TweakOverlap[] TweakOverlaps,
    ArchiveXlOperationChain[] ArchiveXlChains,
    ArchiveXlSourceFailure[] ArchiveXlFailures,
    SourceAnalysisFailure[] SourceFailures,
    int SourceItemCount,
    int ArchiveXlSourceCount,
    CodeCoverageReceipt? CodeCoverage = null,
    CodeSourceEvidence[]? CodeEvidence = null,
    TweakReferenceIndex? TweakReferences = null);
