using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ConflictStudio.App;

public static class SourceFileActions
{
    public static void OpenEditor(SourceFileLocation file)
        => OpenEditor(file, SourceEditorProcessLauncher.Instance);

    public static SourceEditorOpenResult OpenAtLine(SourceFileLocation file, int line, SourceEditorPreference? preference = null)
        => OpenAtLine(file, line, preference, SourceEditorProcessLauncher.Instance);

    internal static SourceEditorOpenResult OpenAtLine(SourceFileLocation file, int line, SourceEditorPreference? preference, ISourceEditorProcessLauncher launcher)
    {
        if (line < 1) throw new ArgumentOutOfRangeException(nameof(line), "The editor line must be at least 1.");
        string path = SourceFileLocations.ExistingPath(file);
        preference ??= new SourceEditorPreference(SourceEditorKind.NormalOpen);
        if (preference.Kind == SourceEditorKind.NormalOpen)
        {
            OpenEditor(file, launcher);
            return new SourceEditorOpenResult(false, false, "Opened with the normal editor. This editor choice does not select a line.");
        }
        if (!Enum.IsDefined(preference.Kind) || string.IsNullOrWhiteSpace(preference.ExecutablePath))
        {
            OpenEditor(file, launcher);
            return FallbackResult(line, "The selected editor setting is incomplete");
        }
        ProcessStartInfo startInfo = new(preference.ExecutablePath) { UseShellExecute = false };
        if (preference.Kind == SourceEditorKind.VisualStudioCode)
        {
            startInfo.ArgumentList.Add("--goto");
            startInfo.ArgumentList.Add(path + ":" + line.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            startInfo.ArgumentList.Add("-n" + line.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(path);
        }
        try
        {
            launcher.Start(startInfo);
            return new SourceEditorOpenResult(true, false, $"Opened line {line:N0} in {EditorName(preference.Kind)}.");
        }
        catch (Exception exception) when (exception is Win32Exception or FileNotFoundException or DirectoryNotFoundException)
        {
            OpenEditor(file, launcher);
            return FallbackResult(line, $"{EditorName(preference.Kind)} could not be started");
        }
    }

    private static void OpenEditor(SourceFileLocation file, ISourceEditorProcessLauncher launcher)
    {
        string path = SourceFileLocations.ExistingPath(file);
        try { launcher.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "edit" }); }
        catch (Win32Exception exception) when (exception.NativeErrorCode is 1155 or 31)
        {
            ProcessStartInfo notepad = new("notepad.exe") { UseShellExecute = false };
            notepad.ArgumentList.Add(path);
            launcher.Start(notepad);
        }
    }

    private static SourceEditorOpenResult FallbackResult(int line, string reason)
        => new(false, true, $"{reason}. Opened the file with its normal editor; line {line:N0} was not selected.");

    private static string EditorName(SourceEditorKind kind)
        => kind == SourceEditorKind.VisualStudioCode ? "Visual Studio Code" : "Notepad++";

    public static void ShowInFolder(SourceFileLocation file)
    {
        string path = SourceFileLocations.ExistingPath(file);
        using Process? explorer = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}

public sealed record SourceEditorOpenResult(bool OpenedAtLine, bool UsedFallback, string Status);
