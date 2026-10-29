using ChromeBlood.Contracts.Paths;


namespace ChromeBlood.Core.Detection;

public sealed class WindowsDriveProbe : IDriveProbe
{
    public IReadOnlyList<DriveObservation> GetDrives()
        => DriveInfo.GetDrives()
            .Select(Observe)
            .OrderBy(drive => drive.Root, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static DriveObservation Observe(DriveInfo drive)
    {
        string root = ContractWindowsPath.NormalizeAbsolute(drive.RootDirectory.FullName);
        DriveKind kind = GetKind(drive.DriveType);
        try
        {
            if (!drive.IsReady)
            {
                return new DriveObservation(root, kind, ProbeStatus.Unavailable, false, null, null, null);
            }

            uint? volumeSerialNumber = null;


            return new DriveObservation(
                root,
                kind,
                ProbeStatus.Available,
                true,
                drive.DriveFormat,
                drive.TotalSize,
                drive.AvailableFreeSpace,
                volumeSerialNumber);
        }
        catch (UnauthorizedAccessException)
        {
            return new DriveObservation(root, kind, ProbeStatus.AccessDenied, false, null, null, null);
        }
        catch (IOException)
        {
            return new DriveObservation(root, kind, ProbeStatus.Unavailable, false, null, null, null);
        }
    }

    private static DriveKind GetKind(DriveType type)
        => type switch
        {
            DriveType.Fixed => DriveKind.Fixed,
            DriveType.Removable => DriveKind.Removable,
            DriveType.Network => DriveKind.Network,
            DriveType.CDRom => DriveKind.Optical,
            DriveType.Ram => DriveKind.RamDisk,
            _ => DriveKind.Unknown
        };
}

