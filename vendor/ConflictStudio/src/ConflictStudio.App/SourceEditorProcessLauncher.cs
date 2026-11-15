using System.Diagnostics;

namespace ConflictStudio.App;

internal interface ISourceEditorProcessLauncher
{
    void Start(ProcessStartInfo startInfo);
}

internal sealed class SourceEditorProcessLauncher : ISourceEditorProcessLauncher
{
    public static SourceEditorProcessLauncher Instance { get; } = new();

    public void Start(ProcessStartInfo startInfo)
    {
        using Process? process = Process.Start(startInfo);
    }
}
