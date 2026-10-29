namespace ChromeBlood.Core.Detection;

public sealed record DetectionContext
{
    public DetectionContext(
        IFileProbe files,
        IRegistryProbe registry,
        IProcessProbe processes,
        IDriveProbe drives,
        IKnownFolderProbe knownFolders,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(drives);
        ArgumentNullException.ThrowIfNull(knownFolders);
        if (observedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Detection timestamps must use UTC.", nameof(observedAtUtc));
        }

        Files = files;
        Registry = registry;
        Processes = processes;
        Drives = drives;
        KnownFolders = knownFolders;
        ObservedAtUtc = observedAtUtc;
    }

    public IFileProbe Files { get; }

    public IRegistryProbe Registry { get; }

    public IProcessProbe Processes { get; }

    public IDriveProbe Drives { get; }

    public IKnownFolderProbe KnownFolders { get; }

    public DateTimeOffset ObservedAtUtc { get; }
}
