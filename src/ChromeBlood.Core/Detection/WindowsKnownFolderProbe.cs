using ChromeBlood.Contracts.Paths;

namespace ChromeBlood.Core.Detection;

public sealed class WindowsKnownFolderProbe : IKnownFolderProbe
{
    public ProbeObservation<string> GetPath(KnownFolder folder)
    {
        try
        {
            string path = Environment.GetFolderPath(
                GetSpecialFolder(folder),
                Environment.SpecialFolderOption.DoNotVerify);
            return ContractWindowsPath.TryNormalizeAbsolute(path, out string? normalized) && normalized is not null
                ? ProbeObservation<string>.Available(normalized)
                : ProbeObservation<string>.Unavailable();
        }
        catch (ArgumentException)
        {
            return ProbeObservation<string>.Unavailable();
        }
    }

    private static Environment.SpecialFolder GetSpecialFolder(KnownFolder folder)
        => folder switch
        {
            KnownFolder.CommonApplicationData => Environment.SpecialFolder.CommonApplicationData,
            KnownFolder.LocalApplicationData => Environment.SpecialFolder.LocalApplicationData,
            KnownFolder.RoamingApplicationData => Environment.SpecialFolder.ApplicationData,
            KnownFolder.ProgramFiles => Environment.SpecialFolder.ProgramFiles,
            KnownFolder.Windows => Environment.SpecialFolder.Windows,
            _ => throw new ArgumentOutOfRangeException(nameof(folder))
        };
}
