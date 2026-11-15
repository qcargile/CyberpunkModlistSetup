using ConflictStudio.Core;

namespace ConflictStudio.App;

public static class ArchiveScrollbarNavigation
{
    public static double OffsetAtPointer(double pointerCoordinate, double trackLength, double maximum)
    {
        if (trackLength <= 0 || maximum <= 0) return 0;
        return Math.Clamp(pointerCoordinate / trackLength, 0, 1) * maximum;
    }
}

public static class ScanHistoryPresentation
{
    public static string Describe(ScanHistoryComparison comparison, int visibleCount)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        int added = comparison.Entries.Count(entry => entry.ChangeKind == ScanHistoryChangeKind.New);
        int changed = comparison.Entries.Count(entry => entry.ChangeKind == ScanHistoryChangeKind.Changed);
        int removed = comparison.Entries.Count(entry => entry.ChangeKind == ScanHistoryChangeKind.NoLongerDetected);
        string summary = added + changed + removed == 0 ? "No finding changes detected." : $"{added:N0} new, {changed:N0} changed, {removed:N0} no longer detected.";
        return $"{summary} Showing {visibleCount:N0} of {comparison.Entries.Length:N0} compared findings. Earlier scan: {comparison.Previous.ScannedAtUtc.LocalDateTime:g}; current scan: {comparison.Current.ScannedAtUtc.LocalDateTime:g}.";
    }

    public static string Describe(ProfileScanDrift drift)
    {
        ArgumentNullException.ThrowIfNull(drift);
        return $"Since previous scan: {drift.NewWorkItems.Length:N0} added · {drift.ChangedWorkItems.Length:N0} updated · {drift.RemovedWorkItems.Length:N0} no longer present";
    }
}
