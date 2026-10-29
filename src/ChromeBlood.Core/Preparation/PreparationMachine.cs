using System.Security;
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
        string directory = string.IsNullOrWhiteSpace(wjDir) ? DefaultWabbajackDirectory() : Path.GetFullPath(wjDir);
        List<PreparationCheck> checks = [];
        checks.Add(new("game", "Cyberpunk 2077", selected is null ? "Choose the game installation to prepare." : selected.Root,
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
        bool runtime = VcRuntimePresence.IsInstalled("x64") && VcRuntimePresence.IsInstalled("x86");
        checks.Add(new("vcredist", "Microsoft Visual C++ runtimes", runtime ? "Microsoft VC++ x64 and x86 runtimes are installed." : "Install the missing x64 and x86 runtimes used by the game and MO2 tools.",
            runtime ? CheckState.Ready : CheckState.Needed, true));
        checks.Add(new("longpaths", "Windows long paths", LongPathsEnabled ? "Enabled." : "Enable long paths for mod installation.",
            LongPathsEnabled ? CheckState.Ready : CheckState.Needed, true));
        bool folderAllowed = IsAllowedWabbajackDirectory(directory, selected?.Root);
        bool writable = folderAllowed && CanCreateDirectory(directory, context.Files);
        checks.Add(new("wabbajackfolder", "Wabbajack app folder", !folderAllowed ? "Choose a folder outside the game, Windows, Program Files and cloud sync." :
            writable ? directory : "This folder is not writable. Choose another folder.", writable ? CheckState.Ready : CheckState.Action, true));
        string? wabbajack = folderAllowed ? await FindVerifiedWabbajackAsync(directory, cancellationToken).ConfigureAwait(false) : null;
        checks.Add(new("wabbajack", "Wabbajack", wabbajack is null ? "Download the official signed Wabbajack app." : wabbajack,
            wabbajack is null ? CheckState.Needed : CheckState.Ready, true));
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

    public static Task<string?> FindVerifiedWabbajackAsync(string wjDir, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(Path.GetFullPath(wjDir), "Wabbajack.exe");
        if (!File.Exists(path)) return null;
        var verdict = new AuthenticodeVerifier().Verify(path, PreparationPackages.WabbajackSigner);
        cancellationToken.ThrowIfCancellationRequested();
        return verdict.Status == AuthenticodeStatus.Trusted ? path : null;
    }, cancellationToken);

    public static bool LongPathsEnabled => Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", null) is int value && value == 1;

    internal static DetectionContext CreateContext() => new(new WindowsFileProbe(), new WindowsRegistryProbe(
        [GameInstallationDetector.SteamCurrentUser, GameInstallationDetector.SteamLocalMachine, GameInstallationDetector.GogPath, GameInstallationDetector.GogWow6432Path]),
        new WindowsProcessProbe(), new WindowsDriveProbe(), new WindowsKnownFolderProbe(), DateTimeOffset.UtcNow);

    public static bool IsAllowedWabbajackDirectory(string path, string? gameRoot = null)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (!Path.IsPathFullyQualified(full) || Path.GetPathRoot(full) == full || full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var excluded = new[] { gameRoot, Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("OneDrive"), Environment.GetEnvironmentVariable("OneDriveConsumer"), Environment.GetEnvironmentVariable("OneDriveCommercial"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Dropbox"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Google Drive") };
            if (excluded.Any(root => !string.IsNullOrWhiteSpace(root) && (Under(full, root) || gameRoot is not null && SamePath(root, gameRoot) && Under(root, full)))) return false;
            for (var current = new DirectoryInfo(full); current is not null; current = current.Parent)
                if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or SecurityException) { return false; }
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
    private static string DefaultWabbajackDirectory() => DriveInfo.GetDrives().Where(drive => drive.IsReady && drive.DriveType == DriveType.Fixed)
        .Select(drive => Path.Combine(drive.RootDirectory.FullName, "Wabbajack")).FirstOrDefault(path => File.Exists(Path.Combine(path, "Wabbajack.exe")))
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games", "Wabbajack");
}
