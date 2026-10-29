using Microsoft.Win32;

namespace ChromeBlood.Core.Acquisition;

public static class VcRuntimePresence
{
    public static bool IsInstalled(string architecture)
    {
        if (architecture is not ("x64" or "x86")) throw new ArgumentException("Unsupported runtime architecture.", nameof(architecture));
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + architecture);
            if (key?.GetValue("Installed") is int installed && installed == 1 &&
                Version.TryParse((key.GetValue("Version") as string)?.TrimStart('v'), out var version) &&
                version.Major == 14) return true;
        }
        return false;
    }
}
