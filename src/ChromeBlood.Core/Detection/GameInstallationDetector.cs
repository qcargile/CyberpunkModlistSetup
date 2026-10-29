using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using ChromeBlood.Contracts.Paths;


namespace ChromeBlood.Core.Detection;

public enum GamePlatform { Steam, Gog, Epic, Unknown }

public sealed record GameInstallation(
    GamePlatform Platform,
    string Root,
    string BuildId,
    string BetaKey,
    ulong StateFlags,
    SteamManifest? SteamManifest,
    ProbeStatus SteamManifestStatus,
    bool PhantomLiberty,
    ProbeStatus PhantomLibertyStatus,
    bool Redmod,
    ProbeStatus RedmodStatus)
{
    public ProbeObservation<string> ProductVersion { get; init; } = ProbeObservation<string>.Unavailable();
}

public sealed record GameDetectionResult(
    GameInstallation? Installation,
    ProbeStatus Status,
    string Evidence);

public sealed class GameInstallationDetector
{
    public static readonly RegistryValueDefinition SteamCurrentUser = new(
        new RegistryValueId("steam.current-user.path"),
        RegistryHiveScope.CurrentUser,
        RegistryViewScope.Registry64,
        @"Software\Valve\Steam",
        "SteamPath",
        RegistryValueType.Text);

    public static readonly RegistryValueDefinition SteamLocalMachine = new(
        new RegistryValueId("steam.local-machine.path"),
        RegistryHiveScope.LocalMachine,
        RegistryViewScope.Registry64,
        @"SOFTWARE\WOW6432Node\Valve\Steam",
        "InstallPath",
        RegistryValueType.Text);

    public static readonly RegistryValueDefinition GogWow6432Path = new(
        new RegistryValueId("gog.cyberpunk.wow6432.path"),
        RegistryHiveScope.LocalMachine,
        RegistryViewScope.Registry64,
        @"SOFTWARE\WOW6432Node\GOG.com\Games\1423049311",
        "path",
        RegistryValueType.Text);

    public static readonly RegistryValueDefinition GogPath = new(
        new RegistryValueId("gog.cyberpunk.path"),
        RegistryHiveScope.LocalMachine,
        RegistryViewScope.Registry64,
        @"SOFTWARE\GOG.com\Games\1423049311",
        "path",
        RegistryValueType.Text);

    private static readonly SteamManifest EmptyManifest = new(
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        false,
        false,
        false,
        string.Empty,
        false);

    public GameInstallation? Detect(DetectionContext context)
        => DetectResult(context).Installation;

    public IReadOnlyList<GameInstallation> DetectCandidates(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        List<GameInstallation> installations = [];
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);

        void Add(string root, GamePlatform platform)
        {
            if (!roots.Contains(root) && HasGameExecutable(root, context.Files))
            {
                roots.Add(root);
                installations.Add(platform == GamePlatform.Steam
                    ? Classify(root, context)!
                    : CreateInstallation(root, platform, context));
            }
        }

        foreach (string library in SteamLibraries(context).Libraries)
            Add(Join(library, @"steamapps\common\Cyberpunk 2077"), GamePlatform.Steam);
        foreach (RegistryValueDefinition definition in new[] { GogWow6432Path, GogPath })
        {
            string? root = ReadRegistryPath(context.Registry, definition.Id);
            if (root is not null) Add(root, GamePlatform.Gog);
        }
        ProbeObservation<string> commonApplicationData = context.KnownFolders.GetPath(KnownFolder.CommonApplicationData);
        if (commonApplicationData.Status == ProbeStatus.Available)
        {
            foreach (string root in EpicRoots(commonApplicationData.Value!, context))
                Add(root, GamePlatform.Epic);
        }
        return installations;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public GameDetectionResult DetectResult(DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        GameInstallation? installation = DetectInstallation(context);
        return installation is null
            ? MissingInstallationEvidence(context)
            : new GameDetectionResult(
                installation,
                ProbeStatus.Available,
                $"Detected {installation.Platform} installation at {installation.Root}.");
    }

    private GameInstallation? DetectInstallation(DetectionContext context)
    {
        foreach (string library in SteamLibraries(context).Libraries)
        {
            string root = Join(library, @"steamapps\common\Cyberpunk 2077");
            if (HasGameExecutable(root, context.Files))
            {
                return Classify(root, context);
            }
        }

        foreach (RegistryValueDefinition definition in new[] { GogWow6432Path, GogPath })
        {
            string? root = ReadRegistryPath(context.Registry, definition.Id);
            if (root is not null && HasGameExecutable(root, context.Files))
            {
                return CreateInstallation(root, GamePlatform.Gog, context);
            }
        }

        ProbeObservation<string> commonApplicationData = context.KnownFolders.GetPath(KnownFolder.CommonApplicationData);
        if (commonApplicationData.Status == ProbeStatus.Available)
        {
            GameInstallation? epic = FindEpicInstallation(commonApplicationData.Value!, context);
            if (epic is not null)
            {
                return epic;
            }
        }

        return null;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static")]
    public GameInstallation? Classify(string root, DetectionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ContractWindowsPath.TryNormalizeAbsolute(root, out string? normalizedRoot)
            || normalizedRoot is null
            || !HasGameExecutable(normalizedRoot, context.Files))
        {
            return null;
        }

        GamePlatform platform = ClassifyPlatform(normalizedRoot, context.Files);
        return CreateInstallation(normalizedRoot, platform, context);
    }

    private static bool HasGameExecutable(string root, IFileProbe files)
        => IsAvailableFile(Join(root, @"bin\x64\Cyberpunk2077.exe"), files);

    private static GamePlatform ClassifyPlatform(string root, IFileProbe files)
    {
        if (files.GetExistence(Join(root, ".egstore")).Status == ProbeStatus.Available)
        {
            return GamePlatform.Epic;
        }

        ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> entries = files.Enumerate(root);
        if (entries.Status == ProbeStatus.Available
            && entries.Value!.Any(entry => IsGogMarker(entry, files)))
        {
            return GamePlatform.Gog;
        }

        string? steamApps = SteamApps(root);
        if (steamApps is not null
            && files.GetExistence(Join(steamApps, "appmanifest_1091500.acf")).Status == ProbeStatus.Available)
        {
            return GamePlatform.Steam;
        }

        return GamePlatform.Unknown;
    }

    private static bool IsGogMarker(FileSystemEntryObservation entry, IFileProbe files)
    {
        if (!IsRegularFile(entry, files))
        {
            return false;
        }

        string fileName = FileName(entry.Path);
        return fileName.Length > 8 && fileName.StartsWith("goggame-", StringComparison.Ordinal);
    }

    private static GameInstallation CreateInstallation(string root, GamePlatform platform, DetectionContext context)
    {
        bool steam = platform == GamePlatform.Steam;
        (SteamManifest? manifest, ProbeStatus manifestStatus) = steam
            ? ReadAssociatedSteamManifest(root, context.Files)
            : (null, ProbeStatus.NotAllowed);
        var marker = context.Files.GetExistence(Join(root, @"tools\redmod\bin\redMod.exe"));
        bool redmod = IsAvailableFileObservation(marker);
        ProbeStatus redmodStatus = redmod ? ProbeStatus.Available : marker.Status;
        MarkerObservation phantomLiberty = DetectPhantomLiberty(root, context.Files);
        SteamManifest values = manifest ?? EmptyManifest;

        return new GameInstallation(
            platform,
            root,
            values.BuildId,
            values.BetaKey,
            values.StateFlags,
            manifest,
            manifestStatus,
            phantomLiberty.Detected,
            phantomLiberty.Status,
            redmod,
            redmodStatus)
        {
            ProductVersion = ReadProductVersion(root, context.Files)
        };
    }

    private static ProbeObservation<string> ReadProductVersion(string root, IFileProbe files)
    {
        string executable = Join(root, @"bin\x64\Cyberpunk2077.exe");
        ProbeObservation<FileSystemEntryKind> existence = files.GetExistence(executable);
        if (existence.Status != ProbeStatus.Available)
            return ProductVersionStatus(existence.Status);
        if (existence.Value != FileSystemEntryKind.File)
            return ProbeObservation<string>.Unavailable();

        ProbeObservation<FileAttributes> attributes = files.GetAttributes(executable);
        if (attributes.Status != ProbeStatus.Available)
            return ProductVersionStatus(attributes.Status);
        if (attributes.Value.HasFlag(FileAttributes.ReparsePoint))
        {
            ProbeObservation<string> finalPath = files.GetFinalPath(executable);
            if (finalPath.Status != ProbeStatus.Available || string.IsNullOrWhiteSpace(finalPath.Value))
                return ProductVersionStatus(finalPath.Status);
            executable = finalPath.Value;
            ProbeObservation<FileSystemEntryKind> target = files.GetExistence(executable);
            ProbeObservation<FileAttributes> targetAttributes = files.GetAttributes(executable);
            if (target.Status != ProbeStatus.Available || targetAttributes.Status != ProbeStatus.Available)
                return ProductVersionStatus(target.Status != ProbeStatus.Available ? target.Status : targetAttributes.Status);
            if (target.Value != FileSystemEntryKind.File || targetAttributes.Value.HasFlag(FileAttributes.ReparsePoint))
                return ProbeObservation<string>.ReparsePoint();
        }

        return files is IExecutableProductVersionProbe versionProbe
            ? versionProbe.GetProductVersion(executable)
            : ProbeObservation<string>.Unavailable();
    }

    private static ProbeObservation<string> ProductVersionStatus(ProbeStatus status)
        => status switch
        {
            ProbeStatus.Missing => ProbeObservation<string>.Missing(),
            ProbeStatus.AccessDenied => ProbeObservation<string>.AccessDenied(),
            ProbeStatus.Locked => ProbeObservation<string>.Locked(),
            ProbeStatus.ReparsePoint => ProbeObservation<string>.ReparsePoint(),
            _ => ProbeObservation<string>.Unavailable()
        };

    private static (SteamManifest? Manifest, ProbeStatus Status) ReadAssociatedSteamManifest(
        string root,
        IFileProbe files)
    {
        string? steamApps = SteamApps(root);
        if (steamApps is null)
        {
            return (null, ProbeStatus.Missing);
        }

        ProbeObservation<string> manifest = files.ReadText(Join(steamApps, "appmanifest_1091500.acf"));
        if (manifest.Status == ProbeStatus.Available &&
            (SteamManifestParser.GetValue(manifest.Value!, "appid") != "1091500" ||
             !string.Equals(SteamManifestParser.GetValue(manifest.Value!, "installdir"),
                 FileName(root), StringComparison.OrdinalIgnoreCase)))
            return (null, ProbeStatus.Unavailable);
        return manifest.Status == ProbeStatus.Available
            ? (SteamManifestParser.Parse(manifest.Value!), ProbeStatus.Available)
            : (null, manifest.Status);
    }

    private static MarkerObservation DetectPhantomLiberty(string root, IFileProbe files)
    {
        ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> ep1 = files.Enumerate(Join(root, @"archive\pc\ep1"));
        if (ep1.Status == ProbeStatus.Available)
        {
            if (ep1.Value!.Any(entry =>
                IsRegularFile(entry, files)
                && string.Equals(Extension(entry.Path), ".archive", StringComparison.Ordinal)))
            {
                return new MarkerObservation(true, ProbeStatus.Available);
            }
        }

        ProbeObservation<FileSystemEntryKind> tweakDb = files.GetExistence(Join(root, @"r6\cache\tweakdb_ep1.bin"));
        if (IsAvailableFileObservation(tweakDb))
        {
            return new MarkerObservation(true, ProbeStatus.Available);
        }

        IEnumerable<ProbeStatus> ep1Statuses = ep1.Status == ProbeStatus.Available
            ? ep1.Value!
                .Where(entry => string.Equals(Extension(entry.Path), ".archive", StringComparison.Ordinal))
                .Select(entry => entry.Status)
                .DefaultIfEmpty(ProbeStatus.Missing)
            : [ep1.Status];
        return new MarkerObservation(false, Strongest(ep1Statuses.Append(tweakDb.Status)));
    }

    private static SteamLibraryDiscovery SteamLibraries(DetectionContext context)
    {
        string? steam = ReadRegistryPath(context.Registry, SteamCurrentUser.Id)
            ?? ReadRegistryPath(context.Registry, SteamLocalMachine.Id);
        if (steam is null)
        {
            return new SteamLibraryDiscovery([], ProbeStatus.Missing, "Steam root is missing; libraryfolders.vdf was not probed.");
        }

        List<string> libraries = [steam];
        string libraryFoldersPath = Join(steam, @"steamapps\libraryfolders.vdf");
        ProbeObservation<string> libraryFolders = context.Files.ReadText(libraryFoldersPath);
        if (libraryFolders.Status == ProbeStatus.Available)
        {
            foreach (string path in SteamManifestParser.ReadPaths(libraryFolders.Value!))
            {
                if (ContractWindowsPath.TryNormalizeAbsolute(path, out string? normalized)
                    && normalized is not null
                    && !libraries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    libraries.Add(normalized);
                }
            }
        }

        return new SteamLibraryDiscovery(
            libraries,
            libraryFolders.Status,
            $"{libraryFoldersPath} is {libraryFolders.Status}.");
    }

    private static string? ReadRegistryPath(IRegistryProbe registry, RegistryValueId id)
    {
        RegistryValueObservation observation = registry.ReadValue(id);
        string? value = observation.Status == ProbeStatus.Available ? observation.Value!.Text : null;
        return ContractWindowsPath.TryNormalizeAbsolute(value, out string? normalized) ? normalized : null;
    }

    private static GameInstallation? FindEpicInstallation(string commonApplicationData, DetectionContext context)
    {
        foreach (string root in EpicRoots(commonApplicationData, context))
            if (HasGameExecutable(root, context.Files))
                return CreateInstallation(root, GamePlatform.Epic, context);
        return null;
    }

    private static IEnumerable<string> EpicRoots(string commonApplicationData, DetectionContext context)
    {
        string manifestsRoot = Join(commonApplicationData, @"Epic\EpicGamesLauncher\Data\Manifests");
        ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> manifests = context.Files.Enumerate(manifestsRoot);
        if (manifests.Status != ProbeStatus.Available)
        {
            yield break;
        }

        foreach (FileSystemEntryObservation entry in manifests.Value!)
        {
            if (entry.Status != ProbeStatus.Available
                || entry.Kind != FileSystemEntryKind.File
                || !string.Equals(Extension(entry.Path), ".item", StringComparison.Ordinal))
            {
                continue;
            }

            ProbeObservation<string> text = context.Files.ReadText(entry.Path);
            string? root = text.Status == ProbeStatus.Available ? ReadEpicInstallLocation(text.Value!) : null;
            if (root is not null) yield return root;
        }
    }

    private static string? ReadEpicInstallLocation(string text)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("InstallLocation", out JsonElement location)
                && location.ValueKind == JsonValueKind.String
                && ContractWindowsPath.TryNormalizeAbsolute(location.GetString(), out string? normalized))
            {
                return normalized;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static GameDetectionResult MissingInstallationEvidence(DetectionContext context)
    {
        List<(ProbeStatus Status, string Evidence)> observations = [];
        AddRegistryCandidate(context, SteamCurrentUser, observations);
        AddRegistryCandidate(context, SteamLocalMachine, observations);
        AddRegistryCandidate(context, GogWow6432Path, observations);
        AddRegistryCandidate(context, GogPath, observations);
        SteamLibraryDiscovery steamLibraries = SteamLibraries(context);
        observations.Add((steamLibraries.IndexStatus, steamLibraries.Evidence));
        foreach (string library in steamLibraries.Libraries)
        {
            string executable = Join(library, @"steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe");
            ProbeObservation<FileSystemEntryKind> observation = context.Files.GetExistence(executable);
            observations.Add((observation.Status, $"{executable} is {observation.Status}."));
        }

        ProbeObservation<string> commonApplicationData = context.KnownFolders.GetPath(KnownFolder.CommonApplicationData);
        observations.Add((
            commonApplicationData.Status,
            $"CommonApplicationData is {commonApplicationData.Status}."));
        if (commonApplicationData.Status == ProbeStatus.Available)
        {
            string manifestsRoot = Join(
                commonApplicationData.Value!,
                @"Epic\EpicGamesLauncher\Data\Manifests");
            ProbeObservation<IReadOnlyList<FileSystemEntryObservation>> manifests = context.Files.Enumerate(manifestsRoot);
            observations.Add((manifests.Status, $"{manifestsRoot} is {manifests.Status}."));
            if (manifests.Status == ProbeStatus.Available)
            {
                foreach (FileSystemEntryObservation entry in manifests.Value!
                    .Where(entry => string.Equals(Extension(entry.Path), ".item", StringComparison.Ordinal)))
                {
                    if (entry.Status != ProbeStatus.Available)
                    {
                        observations.Add((entry.Status, $"{entry.Path} is {entry.Status}."));
                        continue;
                    }

                    ProbeObservation<string> text = context.Files.ReadText(entry.Path);
                    observations.Add((text.Status, $"{entry.Path} text is {text.Status}."));
                    string? root = text.Status == ProbeStatus.Available
                        ? ReadEpicInstallLocation(text.Value!)
                        : null;
                    if (root is not null)
                    {
                        string executable = Join(root, @"bin\x64\Cyberpunk2077.exe");
                        ProbeObservation<FileSystemEntryKind> executableObservation = context.Files.GetExistence(executable);
                        observations.Add((executableObservation.Status, $"{executable} is {executableObservation.Status}."));
                    }
                }
            }
        }

        ProbeStatus status = Strongest(observations.Select(observation => observation.Status));
        return new GameDetectionResult(
            null,
            status,
            string.Join(" ", observations.Select(observation => observation.Evidence)));
    }

    private static void AddRegistryCandidate(
        DetectionContext context,
        RegistryValueDefinition definition,
        List<(ProbeStatus Status, string Evidence)> observations)
    {
        RegistryValueObservation registry = context.Registry.ReadValue(definition.Id);
        if (registry.Status != ProbeStatus.Available)
        {
            observations.Add((registry.Status, $"{definition.Id} is {registry.Status}."));
            return;
        }

        string? value = registry.Value?.Text;
        if (!ContractWindowsPath.TryNormalizeAbsolute(value, out string? root) || root is null)
        {
            observations.Add((ProbeStatus.Unavailable, $"{definition.Id} did not contain an absolute Windows path."));
            return;
        }

        string executable = Join(root, @"bin\x64\Cyberpunk2077.exe");
        if (definition == SteamCurrentUser || definition == SteamLocalMachine)
        {
            executable = Join(root, @"steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe");
        }

        ProbeObservation<FileSystemEntryKind> observation = context.Files.GetExistence(executable);
        observations.Add((observation.Status, $"{executable} is {observation.Status}."));
    }

    private static bool IsAvailableFile(string path, IFileProbe files)
    {
        ProbeObservation<FileSystemEntryKind> existence = files.GetExistence(path);
        if (!IsAvailableFileObservation(existence))
        {
            return false;
        }

        ProbeObservation<FileAttributes> attributes = files.GetAttributes(path);
        if (attributes.Status != ProbeStatus.Available)
        {
            return false;
        }

        if (!attributes.Value.HasFlag(FileAttributes.ReparsePoint))
        {
            return true;
        }

        ProbeObservation<string> finalPath = files.GetFinalPath(path);
        if (finalPath.Status != ProbeStatus.Available)
        {
            return false;
        }

        return IsAvailableFileObservation(files.GetExistence(finalPath.Value!));
    }

    private static bool IsAvailableFileObservation(ProbeObservation<FileSystemEntryKind> observation)
        => observation.Status == ProbeStatus.Available && observation.Value == FileSystemEntryKind.File;

    private static ProbeStatus Strongest(IEnumerable<ProbeStatus> statuses)
    {
        ProbeStatus[] values = statuses.ToArray();
        foreach (ProbeStatus status in new[]
        {
            ProbeStatus.AccessDenied,
            ProbeStatus.Locked,
            ProbeStatus.Unavailable,
            ProbeStatus.ReparsePoint,
            ProbeStatus.NotAllowed
        })
        {
            if (values.Contains(status))
            {
                return status;
            }
        }

        return ProbeStatus.Missing;
    }

    private static bool IsRegularFile(FileSystemEntryObservation entry, IFileProbe files)
    {
        if (entry.Kind != FileSystemEntryKind.File)
        {
            return false;
        }

        if (entry.Status == ProbeStatus.Available)
        {
            return true;
        }

        if (entry.Status != ProbeStatus.ReparsePoint)
        {
            return false;
        }

        ProbeObservation<string> finalPath = files.GetFinalPath(entry.Path);
        if (finalPath.Status != ProbeStatus.Available)
        {
            return false;
        }

        ProbeObservation<FileSystemEntryKind> target = files.GetExistence(finalPath.Value!);
        return target.Status == ProbeStatus.Available && target.Value == FileSystemEntryKind.File;
    }

    private static string Join(string root, string relative)
        => ContractWindowsPath.NormalizeAbsolute(root.TrimEnd('\\') + "\\" + relative);

    private static string? Parent(string path)
    {
        int separator = path.LastIndexOf('\\');
        if (separator < 2)
        {
            return null;
        }

        if (separator == 2)
        {
            return path.Length > 3 ? path[..3] : null;
        }

        return path[..separator];
    }

    private static string? SteamApps(string root)
    {
        string? common = Parent(root);
        return common is null ? null : Parent(common);
    }

    private static string FileName(string path)
    {
        int separator = path.LastIndexOf('\\');
        return separator < 0 ? path : path[(separator + 1)..];
    }

    private static string Extension(string path)
    {
        string fileName = FileName(path);
        int dot = fileName.LastIndexOf('.');
        return dot < 0 ? string.Empty : fileName[dot..];
    }

    private sealed record MarkerObservation(bool Detected, ProbeStatus Status);

    private sealed record SteamLibraryDiscovery(
        IReadOnlyList<string> Libraries,
        ProbeStatus IndexStatus,
        string Evidence);
}

