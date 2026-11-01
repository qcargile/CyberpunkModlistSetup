using System.Security;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Detection;
using Microsoft.Win32;

namespace ChromeBlood.Core.Preparation;

public static class PreparationMachine
{
    public static Task<PreparationSnapshot> ScanAsync(string? selectedGameRoot, string? wjDir,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = CreateContext();
        var detector = new GameInstallationDetector();
        var installations = detector.DetectCandidates(context).ToList();
        GameInstallation? selected = null;
        if (!string.IsNullOrWhiteSpace(selectedGameRoot))
        {
            selected = detector.Classify(selectedGameRoot, context);
            if (selected is not null && !installations.Any(game => SamePath(game.Root, selected.Root))) installations.Add(selected);
        }
        else if (installations.Count == 1) selected = installations[0];
        string directory = string.IsNullOrWhiteSpace(wjDir) ? DefaultWabbajackDirectory() : wjDir;
        try { directory = Path.GetFullPath(directory); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { }
        List<PreparationCheck> checks = [];
        checks.Add(new("game", "Cyberpunk 2077", selected is null ? string.IsNullOrWhiteSpace(selectedGameRoot)
            ? "Choose the game installation to prepare." : $"No readable bin\\x64\\Cyberpunk2077.exe was found in {selectedGameRoot}. Choose the game installation folder, or repair it through your game store." : selected.Root,
            selected is null ? CheckState.Action : CheckState.Ready, true));
        if (selected is not null)
        {
            string version = selected.ProductVersion.Value ?? "";
            bool supportedVersion = Version.TryParse(version, out var product) && product.Major == 2 && product.Minor == 5;
            bool steamBuild = selected.Platform != GamePlatform.Steam ||
                selected.SteamManifestStatus == ProbeStatus.Available && selected.BuildId == "26161058" &&
                selected.SteamManifest is { StateFlagsPresent: true, StateFlagsValid: true } manifest &&
                manifest.StateFlags == 4 && (string.IsNullOrEmpty(selected.BetaKey) || string.Equals(selected.BetaKey, "public", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(manifest.TargetBuildId) || manifest.TargetBuildId == "0" || manifest.TargetBuildId == selected.BuildId);
            checks.Add(new("version", "Supported game version", supportedVersion && steamBuild ?
                $"Cyberpunk {version}{(selected.Platform == GamePlatform.Steam ? $", Steam build {selected.BuildId}" : "")}." :
                $"C&B requires Cyberpunk 2.5{(selected.Platform == GamePlatform.Steam ? " on Steam public build 26161058, fully updated" : "")}. Detected {version} {selected.BuildId}.",
                supportedVersion && steamBuild ? CheckState.Ready : CheckState.Action, true));
            checks.Add(new("phantomliberty", "Phantom Liberty", selected.PhantomLiberty ? "Installed in the selected game copy." : "Install Phantom Liberty for this game copy.",
                selected.PhantomLiberty ? CheckState.Ready : CheckState.Action, true));
            checks.Add(new("redmod", "REDmod", selected.Redmod ? "Installed in the selected game copy." : "Install the free REDmod DLC for this game copy.",
                selected.Redmod ? CheckState.Ready : CheckState.Action, true,
                selected.Platform == GamePlatform.Steam ? "steam://install/2060310" : null));
            bool launchOnly = selected.SteamManifest?.AutoUpdateBehavior == "1";
            checks.Add(new("steamupdate", "Steam update preference", selected.Platform != GamePlatform.Steam ? "Applies to Steam copies only." :
                launchOnly ? "Updates only when launched from Steam." : "Optional: update only when launched from Steam.",
                selected.Platform != GamePlatform.Steam || launchOnly ? CheckState.Ready : CheckState.Optional, false));
        }
        checks.Add(ObserveRequiredSetting("vcredist", "Microsoft Visual C++ runtimes",
            () => VcRuntimePresence.IsInstalled("x64") && VcRuntimePresence.IsInstalled("x86"),
            "Microsoft VC++ x64 and x86 runtimes are installed.", "Install the missing x64 and x86 runtimes used by the game and MO2 tools."));
        checks.Add(ObserveRequiredSetting("longpaths", "Windows long paths", () => LongPathsEnabled,
            "Enabled.", "Enable long paths for mod installation."));
        bool folderAllowed = IsAllowedWabbajackDirectory(directory, selected?.Root);
        bool writable = folderAllowed && CanCreateDirectory(directory, context.Files);
        checks.Add(new("wabbajackfolder", "Wabbajack app folder", !folderAllowed ? @"Change app folder. Use C:\Wabbajack or another folder outside user folders, Steam and the game." :
            writable ? directory : "This folder is not writable. Choose another folder.", writable ? CheckState.Ready : CheckState.Action, true));
        checks.Add(await InspectWabbajackAsync(directory, cancellationToken).ConfigureAwait(false));
        var directX = new WindowsDirectXRuntimePresenceProbe().Observe();
        checks.Add(new("directx", "Legacy DirectX", directX.Evidence,
            directX.Status == DirectXRuntimeStatus.Satisfied ? CheckState.Ready : CheckState.Optional, false));
        cancellationToken.ThrowIfCancellationRequested();
        return new PreparationSnapshot
        {
            GameCandidates = installations.Select(game => new GameCandidate(game.Root, game.Platform.ToString(), game.ProductVersion.Value ?? "Unknown", game.BuildId)).ToArray(),
            SelectedGameRoot = selected?.Root,
            WabbajackDirectory = directory,
            Checks = checks,
            ReadyForHandoff = checks.Where(check => check.Required).All(check => check.Status == CheckState.Ready)
        };
    }, cancellationToken);

    public static async Task<string?> FindVerifiedWabbajackAsync(string wjDir, CancellationToken cancellationToken = default)
    {
        PreparationCheck check = await InspectWabbajackAsync(wjDir, cancellationToken).ConfigureAwait(false);
        return check.Status == CheckState.Ready ? check.Detail : null;
    }

    internal static Task<PreparationCheck> InspectWabbajackAsync(string wjDir, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string releases = "https://github.com/wabbajack-tools/wabbajack/releases/latest";
        string path;
        try { path = Path.Combine(Path.GetFullPath(wjDir), "Wabbajack.exe"); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        { return new PreparationCheck("wabbajack", "Wabbajack", "The app folder path is invalid. Choose another folder.", CheckState.Action, true, releases); }
        if (Directory.Exists(path)) return new PreparationCheck("wabbajack", "Wabbajack",
            "A folder named Wabbajack.exe prevents the app from being saved here. Choose another app folder, or move that folder manually before retrying.", CheckState.Action, true, releases);
        if (!File.Exists(path)) return new PreparationCheck("wabbajack", "Wabbajack",
            "Download the official signed Wabbajack app. If the download fails, download Wabbajack.exe from the official releases page and place it in this app folder, then check again.", CheckState.Needed, true, releases);
        var verdict = new AuthenticodeVerifier().Verify(path, PreparationPackages.WabbajackSigner);
        cancellationToken.ThrowIfCancellationRequested();
        return verdict.Status == AuthenticodeStatus.Trusted ? new PreparationCheck("wabbajack", "Wabbajack", path, CheckState.Ready, true)
            : new PreparationCheck("wabbajack", "Wabbajack", $"The existing Wabbajack.exe could not be trusted ({verdict.Status}). Close Wabbajack and retry, or choose another app folder and download an official copy. You can download it manually from the official releases page.", CheckState.Action, true, releases);
    }, cancellationToken);

    internal static PreparationCheck ObserveRequiredSetting(string id, string title, Func<bool> observe,
        string readyDetail, string neededDetail)
    {
        try { return observe() ? new(id, title, readyDetail, CheckState.Ready, true) : new(id, title, neededDetail, CheckState.Needed, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return new(id, title, "Windows could not check this requirement: " + exception.Message + " " + neededDetail + " Check again after resolving access to the Windows setting.", CheckState.Action, true);
        }
    }

    public static bool LongPathsEnabled => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", null) is int value && value == 1;

    internal static DetectionContext CreateContext() => new(new WindowsFileProbe(), new WindowsRegistryProbe(
        [GameInstallationDetector.SteamCurrentUser, GameInstallationDetector.SteamLocalMachine, GameInstallationDetector.GogPath, GameInstallationDetector.GogWow6432Path]),
        new WindowsProcessProbe(), new WindowsDriveProbe(), new WindowsKnownFolderProbe(), DateTimeOffset.UtcNow);

    public static bool IsAllowedWabbajackDirectory(string path, string? gameRoot = null)
        => IsAllowedWabbajackDirectory(path, gameRoot,
            [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
                DownloadsDirectory()]);

    internal static bool IsAllowedWabbajackDirectory(string path, string? gameRoot, IReadOnlyCollection<string> userFolders)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) return false;
            string full = Path.GetFullPath(path);
            if (!Path.IsPathFullyQualified(full) || Path.GetPathRoot(full) == full || full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            DirectoryInfo? common = gameRoot is null ? null : new DirectoryInfo(Path.GetFullPath(gameRoot)).Parent;
            string? steamLibrary = common?.Name.Equals("common", StringComparison.OrdinalIgnoreCase) == true
                && common.Parent?.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase) == true ? common.Parent.Parent?.FullName : null;
            string drive = Path.GetPathRoot(full)!;
            var excluded = new[] { gameRoot, steamLibrary, Path.Combine(drive, "Steam"), Path.Combine(drive, "SteamLibrary"),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive") };
            if (excluded.Any(root => !string.IsNullOrWhiteSpace(root) && (Under(full, root) || gameRoot is not null && SamePath(root, gameRoot) && Under(root, full)))) return false;
            if (userFolders.Any(root => !string.IsNullOrWhiteSpace(root) && Under(full, root))) return false;
            for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
                if (current.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase)
                    || current.Exists && ((current.Attributes & FileAttributes.ReparsePoint) != 0
                        || File.Exists(Path.Combine(current.FullName, "steam.exe")))) return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException) { return false; }
    }

    private static bool Under(string path, string root) => SamePath(path, root) || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool CanCreateDirectory(string path, IFileProbe files)
    {
        string? existing = path;
        while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing))
        {
            if (File.Exists(existing)) return false;
            existing = Path.GetDirectoryName(existing);
        }
        return existing is not null && files.IsWritable(existing) is { Status: ProbeStatus.Available, Value: true };
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
    internal static string DownloadsDirectory()
    {
        Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        nint path = 0;
        try
        {
            return SHGetKnownFolderPath(ref downloads, 0x4000, 0, out path) >= 0 && path != 0
                ? Marshal.PtrToStringUni(path) ?? "" : "";
        }
        finally { Marshal.FreeCoTaskMem(path); }
    }

    [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, nint token, out nint path);

    internal static string DefaultWabbajackDirectory()
    {
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                string path = Path.Combine(drive.RootDirectory.FullName, "Wabbajack");
                if (File.Exists(Path.Combine(path, "Wabbajack.exe")) && IsAllowedWabbajackDirectory(path)) return path;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        return Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\", "Wabbajack");
    }
}
