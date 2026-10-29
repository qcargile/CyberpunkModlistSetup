using System.Text.Json.Serialization;

namespace ChromeBlood.Core.Detection;

public enum DriveKind
{
    Unknown,
    Fixed,
    Removable,
    Network,
    Optical,
    RamDisk
}

public sealed record DriveObservation(
    string Root,
    DriveKind Kind,
    ProbeStatus Status,
    bool IsReady,
    string? FileSystem,
    long? TotalSize,
    long? AvailableFreeSpace,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] uint? VolumeSerialNumber = null);

public interface IDriveProbe
{
    IReadOnlyList<DriveObservation> GetDrives();
}
