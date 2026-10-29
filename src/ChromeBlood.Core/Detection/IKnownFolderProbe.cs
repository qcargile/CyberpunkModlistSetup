namespace ChromeBlood.Core.Detection;

public enum KnownFolder
{
    CommonApplicationData,
    LocalApplicationData,
    RoamingApplicationData,
    ProgramFiles,
    Windows
}

public interface IKnownFolderProbe
{
    ProbeObservation<string> GetPath(KnownFolder folder);
}
