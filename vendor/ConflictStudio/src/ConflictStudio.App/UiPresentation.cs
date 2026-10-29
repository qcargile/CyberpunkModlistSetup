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
    public static string Describe(ProfileScanDrift drift)
    {
        ArgumentNullException.ThrowIfNull(drift);
        return $"Since previous scan: {drift.NewWorkItems.Length:N0} added · {drift.ChangedWorkItems.Length:N0} updated · {drift.RemovedWorkItems.Length:N0} no longer present";
    }
}
