namespace ChromeBlood.Core.Detection;

public sealed record ProcessObservation(
    string? ExecutableName,
    int ProcessId,
    ProbeStatus ExecutableNameStatus,
    DateTimeOffset? CreationTimeUtc,
    ProbeStatus CreationTimeStatus,
    ProbeStatus PathStatus,
    string? ExecutablePath);

public interface IProcessProbe
{
    IReadOnlyList<ProcessObservation> GetProcesses();
}
