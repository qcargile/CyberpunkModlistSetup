using ConflictStudio.Core;

namespace ConflictStudio.App;

public sealed record ScanCoveragePresentation(string Summary, string Details)
{
    public static ScanCoveragePresentation Create(ProfileScanReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        List<string> details = [$"Manager: {receipt.ManagerKind} · Profile: {receipt.ProfileName}"];
        if (receipt.ManagerKind == ModManagerKind.Manual) details.Add("Manual scans inspect deployed files without a mod manager's provider attribution.");
        if (!receipt.DeploymentFresh) details.Add("Manager evidence is limited. Provider ownership may be unavailable; inspect the recorded scan issues.");
        if (receipt.Metrics?.VortexBridge is { } bridge)
            details.Add($"Vortex inventory complete: {(bridge.InventoryComplete ? "yes" : "no")}. Unmapped relevant files: {bridge.UnmappedRelevantFiles:N0}.");
        if (receipt.CodeCoverage is not { } coverage)
            return new ScanCoveragePresentation("Coverage was not recorded · run a fresh scan", string.Join(Environment.NewLine, details.Append("This scan cannot establish how much source was analyzed. No finding does not mean no conflict.")));
        details.AddRange(coverage.Sources.Select(value => $"{value.Surface}: {value.AnalyzedFiles:N0} files included"));
        details.Add($"RED .tweak files not analyzed: {coverage.UnsupportedTweakFiles:N0}");
        details.Add($"Dynamic callbacks: {coverage.DynamicCallbacks:N0}. Their execution is not established by the source scan.");
        details.Add($"Unreadable source inputs: {coverage.UnreadableInputs:N0} · unreadable archives: {receipt.ArchiveFailures.Length:N0}");
        details.Add($"ArchiveXL unavailable inputs: {receipt.ArchiveXlFailures.Count(value => value.Kind == ArchiveXlFailureKind.Operational):N0}");
        details.Add($"ArchiveXL malformed data: {receipt.ArchiveXlFailures.Count(value => value.Kind == ArchiveXlFailureKind.Malformed):N0}");
        details.Add($"ArchiveXL unsupported operations: {receipt.ArchiveXlFailures.Count(value => value.Kind == ArchiveXlFailureKind.Coverage):N0}");
        details.AddRange(coverage.Limitations);
        int issues = receipt.ArchiveFailures.Length + (receipt.SourceFailures?.Length ?? 0) + receipt.ArchiveXlFailures.Length;
        if (issues > 0) details.Add($"{issues:N0} recorded scan issues are available under Support and the Scan problems filter.");
        string manager = receipt.DeploymentFresh ? string.Empty : " · limited manager evidence";
        return new ScanCoveragePresentation($"Scan coverage: {coverage.Sources.Sum(value => value.AnalyzedFiles):N0} files included · {coverage.UnsupportedTweakFiles:N0} unsupported · {coverage.DynamicCallbacks:N0} dynamic callbacks · {coverage.UnreadableInputs + receipt.ArchiveFailures.Length:N0} unreadable · {receipt.ArchiveXlFailures.Length:N0} ArchiveXL issues{manager}", string.Join(Environment.NewLine, details));
    }
}
