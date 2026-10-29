using System.ComponentModel;
using System.Diagnostics;
using ChromeBlood.Contracts.Paths;

namespace ChromeBlood.Core.Detection;

internal interface IWindowsProcessSource
{
    IReadOnlyList<IWindowsProcessHandle> GetProcesses();
}

internal interface IWindowsProcessHandle : IDisposable
{
    int Id { get; }

    string ProcessName { get; }

    DateTime StartTime { get; }

    string? ExecutablePath { get; }
}

public sealed class WindowsProcessProbe : IProcessProbe
{
    private readonly IWindowsProcessSource _source;

    public WindowsProcessProbe()
        : this(new WindowsProcessSource())
    {
    }

    internal WindowsProcessProbe(IWindowsProcessSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
    }

    public IReadOnlyList<ProcessObservation> GetProcesses()
    {
        List<ProcessObservation> observations = [];
        foreach (IWindowsProcessHandle process in _source.GetProcesses())
        {
            using (process)
            {
                observations.Add(Observe(process));
            }
        }

        return observations.OrderBy(process => process.ProcessId).ToArray();
    }

    private static ProcessObservation Observe(IWindowsProcessHandle process)
    {
        ProbeObservation<string> executableName = ObserveExecutableName(process);
        ProbeObservation<DateTimeOffset> creationTime = ObserveCreationTime(process);
        ProbeObservation<string> executablePath = ObserveExecutablePath(process);
        if (executablePath.Status == ProbeStatus.Available)
        {
            executableName = ProbeObservation<string>.Available(Path.GetFileName(executablePath.Value!));
        }

        return new ProcessObservation(
            executableName.Status == ProbeStatus.Available ? executableName.Value : null,
            process.Id,
            executableName.Status,
            creationTime.Status == ProbeStatus.Available ? creationTime.Value : null,
            creationTime.Status,
            executablePath.Status,
            executablePath.Status == ProbeStatus.Available ? executablePath.Value : null);
    }

    private static ProbeObservation<string> ObserveExecutableName(IWindowsProcessHandle process)
    {
        try
        {
            return ProbeObservation<string>.Available(NormalizeExecutableName(process.ProcessName));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return ProbeObservation<string>.AccessDenied();
        }
        catch (Win32Exception)
        {
            return ProbeObservation<string>.Unavailable();
        }
        catch (InvalidOperationException)
        {
            return ProbeObservation<string>.Unavailable();
        }
    }

    private static ProbeObservation<DateTimeOffset> ObserveCreationTime(IWindowsProcessHandle process)
    {
        try
        {
            return ProbeObservation<DateTimeOffset>.Available(process.StartTime.ToUniversalTime());
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return ProbeObservation<DateTimeOffset>.AccessDenied();
        }
        catch (Win32Exception)
        {
            return ProbeObservation<DateTimeOffset>.Unavailable();
        }
        catch (InvalidOperationException)
        {
            return ProbeObservation<DateTimeOffset>.Unavailable();
        }
    }

    private static ProbeObservation<string> ObserveExecutablePath(IWindowsProcessHandle process)
    {
        try
        {
            string? path = process.ExecutablePath;
            return string.IsNullOrWhiteSpace(path)
                ? ProbeObservation<string>.Unavailable()
                : ProbeObservation<string>.Available(ContractWindowsPath.NormalizeAbsolute(path));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 5)
        {
            return ProbeObservation<string>.AccessDenied();
        }
        catch (Win32Exception)
        {
            return ProbeObservation<string>.Unavailable();
        }
        catch (InvalidOperationException)
        {
            return ProbeObservation<string>.Unavailable();
        }
    }

    private static string NormalizeExecutableName(string processName)
        => processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? processName
            : processName + ".exe";

    private sealed class WindowsProcessSource : IWindowsProcessSource
    {
        public IReadOnlyList<IWindowsProcessHandle> GetProcesses()
            => Process.GetProcesses().Select(process => new WindowsProcessHandle(process)).ToArray();
    }

    private sealed class WindowsProcessHandle : IWindowsProcessHandle
    {
        private readonly Process _process;

        public WindowsProcessHandle(Process process)
        {
            _process = process;
            Id = process.Id;
        }

        public int Id { get; }

        public string ProcessName => _process.ProcessName;

        public DateTime StartTime => _process.StartTime;

        public string? ExecutablePath => _process.MainModule?.FileName;

        public void Dispose() => _process.Dispose();
    }
}
