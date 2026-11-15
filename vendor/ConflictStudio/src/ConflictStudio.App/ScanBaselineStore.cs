using ConflictStudio.Core;
using System.IO;

namespace ConflictStudio.App;

public enum ScanBaselineState { Missing, Available, Unreadable, Foreign }

public sealed record ScanBaselineLoadResult(ProfileScanReceipt? Receipt, ScanBaselineState State, string Message, string Path);

public sealed class ScanBaselineStore
{
    private readonly string _path;

    public ScanBaselineStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "baseline.json");
    }

    public ScanBaselineLoadResult Load(ProfileScanReceipt current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!File.Exists(_path)) return new ScanBaselineLoadResult(null, ScanBaselineState.Missing, "No reference scan is saved for this profile. Save one before changing mods if you want a fixed comparison point.", _path);
        ProfileScanReceipt baseline;
        try
        {
            baseline = ProfileScanReceiptStore.Read(_path);
        }
        catch (Exception exception) when (exception is ProfileScanReceiptException or IOException or UnauthorizedAccessException)
        {
            return new ScanBaselineLoadResult(null, ScanBaselineState.Unreadable, "The saved reference scan could not be read. It was preserved so you can replace or remove it explicitly.", _path);
        }
        if (!SameProfile(baseline, current)) return new ScanBaselineLoadResult(null, ScanBaselineState.Foreign, "The saved reference scan belongs to a different manager installation or profile. It was preserved.", _path);
        return new ScanBaselineLoadResult(baseline, ScanBaselineState.Available, $"Saved reference scan from {baseline.ScannedAtUtc:u}", _path);
    }

    public void Pin(ProfileScanReceipt current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(current.InstallationId)) throw new ArgumentException("A pinned baseline requires a recorded installation identity.", nameof(current));
        ProfileScanReceiptStore.Write(_path, current);
    }

    public void Clear()
    {
        File.Delete(_path);
    }

    private static bool SameProfile(ProfileScanReceipt baseline, ProfileScanReceipt current)
        => baseline.ManagerKind == current.ManagerKind
            && !string.IsNullOrWhiteSpace(baseline.InstallationId)
            && string.Equals(baseline.InstallationId, current.InstallationId, StringComparison.Ordinal)
            && string.Equals(baseline.ProfileName, current.ProfileName, StringComparison.Ordinal);
}
