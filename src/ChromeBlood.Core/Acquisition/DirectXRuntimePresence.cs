using ChromeBlood.Core.Detection;

namespace ChromeBlood.Core.Acquisition;

public enum DirectXRuntimeStatus { Satisfied, Missing, Unknown }

public sealed record DirectXRuntimeObservation(DirectXRuntimeStatus Status, string Evidence);

public interface IDirectXRuntimePresenceProbe
{
    DirectXRuntimeObservation Observe();
}

public static class DirectXRuntimePresence
{
    public const string MarkerRelativePath = @"System32\d3dx9_43.dll";

    public static DirectXRuntimeObservation Classify(
        ProbeObservation<string> windowsRoot,
        IFileProbe files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (windowsRoot.Status != ProbeStatus.Available || string.IsNullOrWhiteSpace(windowsRoot.Value))
            return new(DirectXRuntimeStatus.Unknown, "The Windows directory is unavailable, so the DirectX June 2010 marker could not be probed.");
        ProbeObservation<FileSystemEntryKind> marker = files.GetExistence(
            Path.Combine(windowsRoot.Value, MarkerRelativePath));
        if (marker.Status == ProbeStatus.Available && marker.Value == FileSystemEntryKind.File)
        {
            ProbeObservation<FileAttributes> attributes = files.GetAttributes(
                Path.Combine(windowsRoot.Value, MarkerRelativePath));
            if (attributes.Status == ProbeStatus.Available
                && !attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                return new(DirectXRuntimeStatus.Satisfied, "The x64 d3dx9_43.dll marker from the legacy DirectX runtime is present; other optional June 2010 components were not individually proven.");
            return new(DirectXRuntimeStatus.Unknown, "The x64 d3dx9_43.dll marker is redirected or its attributes are unavailable; DirectX June 2010 presence is unknown.");
        }
        if (marker.Status == ProbeStatus.Missing)
            return new(DirectXRuntimeStatus.Missing, "The x64 d3dx9_43.dll marker from the legacy DirectX runtime is missing; other optional June 2010 components were not individually probed.");
        return new(DirectXRuntimeStatus.Unknown, $"The x64 d3dx9_43.dll marker probe is {marker.Status}; DirectX June 2010 presence is unknown.");
    }
}

public sealed class WindowsDirectXRuntimePresenceProbe : IDirectXRuntimePresenceProbe
{
    public DirectXRuntimeObservation Observe()
        => DirectXRuntimePresence.Classify(
            new WindowsKnownFolderProbe().GetPath(KnownFolder.Windows),
            new WindowsFileProbe());
}
