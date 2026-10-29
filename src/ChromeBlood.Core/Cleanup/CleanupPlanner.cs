using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using ChromeBlood.Contracts.Serialization;
using ChromeBlood.Core.Parity;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Cleanup;

public enum CleanupEntryKind { File, Directory, Other }

public enum CleanupEntryStatus { Available, Missing, AccessDenied, Locked, ReparsePoint, Unavailable }

public sealed record CleanupProbeEntry(
    string Path,
    CleanupEntryKind Kind,
    CleanupEntryStatus Status,
    FileAttributes Attributes,
    string FileIdentity,
    long Size,
    string Sha256)
{
    public CleanupPathMetadata? Metadata { get; init; }
}

public sealed record CleanupPathMetadata(
    FileAttributes Attributes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    DateTime LastAccessTimeUtc,
    string SecuritySddl);

public sealed record CleanupRootObservation(
    string RequestedPath,
    string PhysicalPath,
    string FileIdentity,
    bool IsReparse,
    bool IsFileSystemRoot,
    bool HasReparseAncestor,
    long AvailableBytes);

public interface ICleanupProbe
{
    CleanupRootObservation ObserveRoot(string path, bool create, CancellationToken cancellationToken);
    CleanupProbeEntry Observe(string path, CancellationToken cancellationToken);
    IReadOnlyList<CleanupProbeEntry> Enumerate(string path, CancellationToken cancellationToken);
}

public sealed record CleanupTarget(string RelativePath, bool IsDirectory, string Reason, CleanupEntryStatus Status);

public sealed record CleanupEntry(
    string RelativePath,
    string SourcePath,
    CleanupEntryKind Kind,
    CleanupEntryStatus Status,
    string Reason,
    FileAttributes Attributes,
    string FileIdentity,
    long Size,
    string Sha256)
{
    public CleanupPathMetadata? Metadata { get; init; }
}

public sealed record CleanupPlan(
    int SchemaVersion,
    string GameRoot,
    string GameRootIdentity,
    string QuarantineRoot,
    string QuarantineRootIdentity,
    long RequiredBytes,
    CleanupTarget[] Targets,
    CleanupEntry[] Entries,
    bool SameVolume = true);

internal enum CleanupPolicyKind { Game, CyberpunkAppData, IndexedGame }
internal enum CleanupTargetExpansion { Exact, Redmods, ConfigOverrides }
internal sealed record CleanupPolicyTarget(string RelativePath, bool IsDirectory, string Reason, CleanupTargetExpansion Expansion);

internal sealed class CleanupPolicy
{
    private CleanupPolicy(CleanupPolicyKind kind, string legacyCommit, string sha256, CleanupPolicyTarget[] targets)
    {
        Kind = kind;
        LegacyCommit = legacyCommit;
        Sha256 = sha256;
        Targets = targets;
    }

    internal CleanupPolicyKind Kind { get; }
    internal string LegacyCommit { get; }
    internal string Sha256 { get; }
    internal CleanupPolicyTarget[] Targets { get; }

    internal static CleanupPolicy FromLegacyCorpus(LegacyParityCorpus corpus)
    {
        LegacyParityFixture fixture = corpus.Fixtures.Single(value => value.Case.Id == "full-clean");
        CleanupPolicyTarget[] targets = fixture.Expected.GetProperty("items").EnumerateArray().Select(item =>
        {
            string reason = item.GetProperty("reason").GetString() ?? throw new CleanupPlanException("Cleanup reason is missing.");
            CleanupTargetExpansion expansion = reason switch
            {
                "installed REDmod mod - not in a clean install" => CleanupTargetExpansion.Redmods,
                "config override - not in a clean install" => CleanupTargetExpansion.ConfigOverrides,
                _ => CleanupTargetExpansion.Exact
            };
            return new CleanupPolicyTarget(item.GetProperty("relPath").GetString() ?? throw new CleanupPlanException("Cleanup path is missing."),
                item.GetProperty("isDir").GetBoolean(), reason, expansion);
        }).ToArray();
        return new(CleanupPolicyKind.Game, corpus.Index.LegacyCommit, fixture.Case.Sha256, targets);
    }

    internal static CleanupPolicy ForPreparation(LegacyParityCorpus corpus)
    {
        CleanupPolicy original = FromLegacyCorpus(corpus);
        CleanupPolicyTarget[] targets =
        [
            .. original.Targets.Where(target => !target.RelativePath.Equals("V2077", StringComparison.OrdinalIgnoreCase)
                && !target.RelativePath.Equals("vortex.deployment.json", StringComparison.OrdinalIgnoreCase)),
            new(@"r6\cache\modded", true, "generated mod cache", CleanupTargetExpansion.Exact),
            new(@"r6\cache\final.redscripts.modded", false, "generated mod script cache", CleanupTargetExpansion.Exact),
            new(@"r6\cache\final.redscripts.ts", false, "generated script cache timestamp", CleanupTargetExpansion.Exact)
        ];
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(ContractJson.SerializeUtf8(targets)));
        return new(CleanupPolicyKind.Game, original.LegacyCommit, sha256, targets);
    }

    internal static CleanupPolicy FromIndexedTargets(string referenceIdentity, CleanupPolicyTarget[] targets)
        => new(CleanupPolicyKind.IndexedGame, string.Empty,
            Convert.ToHexStringLower(SHA256.HashData(ContractJson.SerializeUtf8(new { referenceIdentity, targets }))), targets);

    internal static CleanupPolicy CyberpunkAppData(bool resetSettings = true, bool clearDiagnostics = true)
    {
        CleanupPolicyTarget[] targets =
        [
            new(@"CD Projekt Red\Cyberpunk 2077", true, "Resets Cyberpunk 2077 local settings and crash information. Saved Games is outside this folder.", CleanupTargetExpansion.Exact),
            new("REDEngine", true, "Resets shared REDengine diagnostics and reports, which may contain reports from other games.", CleanupTargetExpansion.Exact)
        ];
        targets = targets.Where(value => value.RelativePath == "REDEngine" ? clearDiagnostics : resetSettings).ToArray();
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(ContractJson.SerializeUtf8(targets)));
        return new(CleanupPolicyKind.CyberpunkAppData, string.Empty, sha256, targets);
    }
}

public sealed class CleanupPlanException(string message) : InvalidOperationException(message);

public sealed class CleanupManualRequiredException(string message) : InvalidOperationException(message);

public sealed class CleanupAuthorizationException(string message) : InvalidOperationException(message);

internal sealed record OwnedCleanupMutationScope(string? DisposableRoot, string ScopeToken)
{
    internal static OwnedCleanupMutationScope ForDisposableTest(string disposableRoot, CleanupPlan plan)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(disposableRoot));
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (CleanupPlanner.PathEquals(root, temporary) || !CleanupPlanner.SameOrChild(root, temporary))
            throw new CleanupAuthorizationException("Disposable cleanup tests require an owned child of the system temporary root.");
        OwnedCleanupMutationScope scope = new(root, Guid.NewGuid().ToString("N"));
        scope.Validate(plan);
        return scope;
    }

    internal static OwnedCleanupMutationScope ForDisposableTestRoots(string disposableRoot, string gameRoot, string quarantineRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(disposableRoot));
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (CleanupPlanner.PathEquals(root, temporary) || !CleanupPlanner.SameOrChild(root, temporary))
            throw new CleanupAuthorizationException("Disposable cleanup tests require an owned child of the system temporary root.");
        OwnedCleanupMutationScope scope = new(root, Guid.NewGuid().ToString("N"));
        scope.ValidateRoots(gameRoot, quarantineRoot);
        return scope;
    }

    internal void Validate(CleanupPlan plan) => ValidateRoots(plan.GameRoot, plan.QuarantineRoot);

    internal void ValidateRoots(string gameRoot, string quarantineRoot)
    {
        if (DisposableRoot is not null
            && (!CleanupPlanner.SameOrChild(gameRoot, DisposableRoot) || !CleanupPlanner.SameOrChild(quarantineRoot, DisposableRoot)))
            throw new CleanupAuthorizationException("The cleanup roots are outside the owned disposable mutation scope.");
        string production = @"D:\Chrome & Blood - Compiled";
        if (CleanupPlanner.SameOrChild(gameRoot, production) || CleanupPlanner.SameOrChild(quarantineRoot, production))
            throw new CleanupAuthorizationException("The compiled modlist root cannot be a cleanup mutation scope.");
    }
}

internal sealed record CleanupAuthorization(
    string AuthorizationId,
    string PlanSha256,
    CleanupPolicyKind PolicyKind,
    string LegacyCommit,
    string PolicySha256,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    OwnedCleanupMutationScope Scope);

public sealed class AuthorizedCleanupPlan
{
    private readonly CleanupPlan _plan;
    private readonly CleanupAuthorization _authorization;

    internal AuthorizedCleanupPlan(CleanupPlan plan, CleanupAuthorization authorization)
    {
        _plan = CleanupAuthorizationCodec.Clone(plan);
        _authorization = authorization;
    }

    public CleanupPlan Plan => CleanupAuthorizationCodec.Clone(_plan);
    public string PlanSha256 => _authorization.PlanSha256;
    public string LegacyCommit => _authorization.LegacyCommit;
    public string CorpusSha256 => _authorization.PolicySha256;
    public DateTimeOffset ExpiresAtUtc => _authorization.ExpiresAtUtc;

    internal (CleanupPlan Plan, CleanupAuthorization Authorization) Snapshot()
        => (CleanupAuthorizationCodec.Clone(_plan), _authorization);
}

public static class CleanupAuthorizationCodec
{
    public static string PlanSha256(CleanupPlan plan)
        => Convert.ToHexStringLower(SHA256.HashData(ContractJson.SerializeUtf8(plan)));

    internal static CleanupPlan Clone(CleanupPlan plan)
        => ContractJson.Deserialize<CleanupPlan>(ContractJson.SerializeUtf8(plan));
}

public sealed class CleanupPlanner
{
    private readonly ICleanupProbe _probe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly string? _disposableRoot;

    public CleanupPlanner() : this(new WindowsCleanupProbe(), () => DateTimeOffset.UtcNow, null) { }

    public CleanupPlanner(ICleanupProbe probe)
        : this(probe, () => DateTimeOffset.UtcNow, null)
    {
    }

    private CleanupPlanner(ICleanupProbe probe, Func<DateTimeOffset> clock, string? disposableRoot)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(clock);
        _probe = probe;
        _clock = clock;
        _disposableRoot = disposableRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(disposableRoot));
    }

    internal static CleanupPlanner ForDisposableTests(string disposableRoot, Func<DateTimeOffset>? clock = null)
        => new(new WindowsCleanupProbe(), clock ?? (() => DateTimeOffset.UtcNow), Path.TrimEndingDirectorySeparator(Path.GetFullPath(disposableRoot)));

    public AuthorizedCleanupPlan Authorize(string gameRoot, string quarantineRoot, LegacyParityCorpus corpus, CancellationToken cancellationToken = default)
    {
        CleanupPolicy policy = CleanupPolicy.FromLegacyCorpus(corpus);
        CleanupPlan plan = Plan(gameRoot, quarantineRoot, policy, cancellationToken);
        return AuthorizePlan(plan, policy);
    }

    public AuthorizedCleanupPlan AuthorizeSelected(CleanupPlan reviewed, IReadOnlyCollection<string> selectedTargets, LegacyParityCorpus corpus, CancellationToken cancellationToken = default)
        => AuthorizeSelected(reviewed, selectedTargets, CleanupPolicy.FromLegacyCorpus(corpus), cancellationToken);

    internal AuthorizedCleanupPlan AuthorizeSelected(CleanupPlan reviewed, IReadOnlyCollection<string> selectedTargets, CleanupPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        ArgumentNullException.ThrowIfNull(selectedTargets);
        CleanupPlan current = Plan(reviewed.GameRoot, reviewed.QuarantineRoot, policy, cancellationToken);
        if (ReviewFingerprint(reviewed) != ReviewFingerprint(current))
            throw new CleanupAuthorizationException("The cleanup inventory changed after review.");
        HashSet<string> selected = selectedTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 || selected.Count != selectedTargets.Count
            || selected.Any(value => current.Targets.All(target => !string.Equals(target.RelativePath, value, StringComparison.OrdinalIgnoreCase))))
            throw new CleanupAuthorizationException("Cleanup selection must contain distinct reviewed targets.");
        CleanupTarget[] targets = current.Targets.Where(target => selected.Contains(target.RelativePath)).ToArray();
        if (targets.Any(target => target.Status != CleanupEntryStatus.Available))
            throw new CleanupAuthorizationException("An unavailable cleanup target was selected.");
        CleanupEntry[] entries = current.Entries.Where(entry => targets.Any(target =>
            string.Equals(target.RelativePath, entry.RelativePath, StringComparison.OrdinalIgnoreCase)
            || target.IsDirectory && SameOrChild(entry.SourcePath, ResolveUnder(current.GameRoot, target.RelativePath)))).ToArray();
        CleanupPlan selection = current with
        {
            Targets = targets,
            Entries = entries,
            RequiredBytes = checked(entries.Where(entry => entry.Kind == CleanupEntryKind.File).Sum(entry => entry.Size))
        };
        return AuthorizePlan(selection, policy);
    }

    private static string ReviewFingerprint(CleanupPlan plan)
    {
        CleanupEntry[] entries = plan.Entries.Select(entry => entry with
        {
            Metadata = entry.Metadata is null ? null : entry.Metadata with { LastAccessTimeUtc = default }
        }).ToArray();
        return CleanupAuthorizationCodec.PlanSha256(plan with { Entries = entries });
    }

    private AuthorizedCleanupPlan AuthorizePlan(CleanupPlan plan, CleanupPolicy policy)
    {
        if (!plan.SameVolume) throw new CleanupManualRequiredException("Cross-volume cleanup is manual until exact NTFS timestamps, owner, DACL, attributes, and topology preservation pass target acceptance.");
        DateTimeOffset issued = _clock();
        string planHash = CleanupAuthorizationCodec.PlanSha256(plan);
        OwnedCleanupMutationScope scope = new(_disposableRoot, Guid.NewGuid().ToString("N"));
        scope.Validate(plan);
        CleanupAuthorization authorization = new(Guid.NewGuid().ToString("N"), planHash, policy.Kind, policy.LegacyCommit, policy.Sha256, issued, issued.AddMinutes(10), scope);
        return new AuthorizedCleanupPlan(plan, authorization);
    }

    public CleanupPlan Plan(string gameRoot, string quarantineRoot, LegacyParityCorpus corpus, CancellationToken cancellationToken = default)
        => Plan(gameRoot, quarantineRoot, CleanupPolicy.FromLegacyCorpus(corpus), cancellationToken);

    internal CleanupPlan Plan(string gameRoot, string quarantineRoot, CleanupPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        string game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameRoot));
        string vault = Path.TrimEndingDirectorySeparator(Path.GetFullPath(quarantineRoot));
        if (policy.Kind == CleanupPolicyKind.CyberpunkAppData && _disposableRoot is null
            && !PathEquals(game, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
            throw new CleanupPlanException("AppData cleanup requires the current user's Local AppData root.");
        if (SameOrChild(vault, game) || SameOrChild(game, vault)) throw new CleanupPlanException("The quarantine root must be outside the game root and cannot contain it.");
        if (TouchesProtectedSystemRoot(vault)) throw new CleanupPlanException("The quarantine root cannot be a system directory or its parent or child.");

        CleanupRootObservation gameObservation = _probe.ObserveRoot(game, false, cancellationToken);
        CleanupRootObservation vaultObservation = _probe.ObserveRoot(vault, false, cancellationToken);
        if (gameObservation.IsReparse || gameObservation.HasReparseAncestor || vaultObservation.IsReparse || vaultObservation.HasReparseAncestor)
            throw new CleanupPlanException("Cleanup roots cannot contain reparse ancestry.");
        if (gameObservation.IsFileSystemRoot || vaultObservation.IsFileSystemRoot)
            throw new CleanupPlanException("Cleanup roots cannot be filesystem roots.");
        if (!PathEquals(gameObservation.PhysicalPath, game) || !PathEquals(vaultObservation.PhysicalPath, vault))
            throw new CleanupPlanException("Cleanup roots must resolve to their requested physical paths.");

        List<CleanupTarget> targets = [];
        List<CleanupEntry> entries = [];
        HashSet<string> relativePaths = new(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupPolicyTarget item in policy.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = NormalizeRelative(item.RelativePath);
            bool directory = item.IsDirectory;
            string reason = item.Reason;
            string source = ResolveUnder(game, relative);
            CleanupProbeEntry observed = _probe.Observe(source, cancellationToken);
            if (item.Expansion == CleanupTargetExpansion.Redmods)
            {
                ExpandRedmods(game, reason, targets, relativePaths, entries, cancellationToken);
                continue;
            }
            if (item.Expansion == CleanupTargetExpansion.ConfigOverrides)
            {
                ExpandConfigOverrides(game, reason, targets, relativePaths, entries, cancellationToken);
                continue;
            }
            if (observed.Status != CleanupEntryStatus.Available)
            {
                targets.Add(new CleanupTarget(relative, directory, reason, observed.Status));
                continue;
            }
            if (directory && observed.Kind != CleanupEntryKind.Directory || !directory && observed.Kind != CleanupEntryKind.File)
            {
                if (policy.Kind == CleanupPolicyKind.CyberpunkAppData)
                {
                    targets.Add(new CleanupTarget(relative, directory, reason, CleanupEntryStatus.Unavailable));
                    continue;
                }
                throw new CleanupPlanException($"Cleanup target kind changed: {relative}");
            }
            if (policy.Kind == CleanupPolicyKind.CyberpunkAppData && directory)
            {
                HashSet<string> targetPaths = new(StringComparer.OrdinalIgnoreCase);
                List<CleanupEntry> targetEntries = [];
                try
                {
                    ExpandDirectory(game, source, reason, targetPaths, targetEntries, cancellationToken);
                }
                catch (Exception exception) when (exception is CleanupPlanException or IOException or UnauthorizedAccessException or Win32Exception)
                {
                    targets.Add(new CleanupTarget(relative, directory, reason, CleanupEntryStatus.Unavailable));
                    continue;
                }
                if (targetPaths.Any(path => !relativePaths.Add(path)))
                    throw new CleanupPlanException("AppData targets overlap by Windows path semantics.");
                entries.AddRange(targetEntries);
            }
            else if (directory) ExpandDirectory(game, source, reason, relativePaths, entries, cancellationToken);
            else AddEntry(game, observed, reason, relativePaths, entries);
            targets.Add(new CleanupTarget(relative, directory, reason, observed.Status));
        }

        long required = checked(entries.Where(value => value.Kind == CleanupEntryKind.File).Sum(value => value.Size));
        if (vaultObservation.AvailableBytes < required) throw new CleanupPlanException("The quarantine root lacks required free space.");
        bool sameVolume = VolumeIdentity(gameObservation.FileIdentity) == VolumeIdentity(vaultObservation.FileIdentity);
        return new CleanupPlan(1, game, gameObservation.FileIdentity, vault, vaultObservation.FileIdentity, required, [.. targets], [.. entries], sameVolume);
    }

    private void ExpandRedmods(string gameRoot, string reason, List<CleanupTarget> targets, HashSet<string> paths, List<CleanupEntry> entries, CancellationToken cancellationToken)
    {
        string mods = ResolveUnder(gameRoot, "mods");
        CleanupProbeEntry root = _probe.Observe(mods, cancellationToken);
        if (root.Status == CleanupEntryStatus.Missing) return;
        if (root.Status != CleanupEntryStatus.Available || root.Kind != CleanupEntryKind.Directory) throw new CleanupPlanException("The REDmod directory cannot be enumerated safely.");
        foreach (CleanupProbeEntry child in _probe.Enumerate(mods, cancellationToken).OrderBy(value => Path.GetFileName(value.Path), StringComparer.OrdinalIgnoreCase).ThenBy(value => Path.GetFileName(value.Path), StringComparer.Ordinal))
        {
            if (child.Status == CleanupEntryStatus.ReparsePoint) throw new CleanupPlanException("A REDmod cleanup target is a reparse point.");
            if (child.Status != CleanupEntryStatus.Available) throw new CleanupPlanException("A REDmod cleanup target is unavailable.");
            if (child.Kind == CleanupEntryKind.Directory)
            {
                targets.Add(new CleanupTarget(Path.GetRelativePath(gameRoot, child.Path).Replace('/', '\\'), true, reason, child.Status));
                ExpandDirectory(gameRoot, child.Path, reason, paths, entries, cancellationToken);
            }
        }
    }

    private void ExpandConfigOverrides(string gameRoot, string reason, List<CleanupTarget> targets, HashSet<string> paths, List<CleanupEntry> entries, CancellationToken cancellationToken)
    {
        string config = ResolveUnder(gameRoot, @"engine\config\platform\pc");
        CleanupProbeEntry root = _probe.Observe(config, cancellationToken);
        if (root.Status == CleanupEntryStatus.Missing) return;
        if (root.Status != CleanupEntryStatus.Available || root.Kind != CleanupEntryKind.Directory) throw new CleanupPlanException("The platform config directory cannot be enumerated safely.");
        foreach (CleanupProbeEntry child in _probe.Enumerate(config, cancellationToken).OrderBy(value => Path.GetFileName(value.Path), StringComparer.OrdinalIgnoreCase).ThenBy(value => Path.GetFileName(value.Path), StringComparer.Ordinal))
        {
            string name = Path.GetFileName(child.Path);
            if (name.Equals("rendering.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("platformgameplay.ini", StringComparison.OrdinalIgnoreCase)) continue;
            if (child.Status != CleanupEntryStatus.Available || child.Kind != CleanupEntryKind.File) throw new CleanupPlanException("A platform config override is unavailable, a reparse point, or not a file.");
            targets.Add(new CleanupTarget(Path.GetRelativePath(gameRoot, child.Path).Replace('/', '\\'), false, reason, child.Status));
            AddEntry(gameRoot, child, reason, paths, entries);
        }
    }

    private void ExpandDirectory(string gameRoot, string directory, string reason, HashSet<string> paths, List<CleanupEntry> entries, CancellationToken cancellationToken)
    {
        IReadOnlyList<CleanupProbeEntry> children = _probe.Enumerate(directory, cancellationToken);
        foreach (CleanupProbeEntry child in children.OrderBy(value => Path.GetFileName(value.Path), StringComparer.OrdinalIgnoreCase).ThenBy(value => Path.GetFileName(value.Path), StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Status != CleanupEntryStatus.Available) throw new CleanupPlanException($"Cleanup directory contains an unavailable entry: {child.Path}");
            if (child.Kind == CleanupEntryKind.Directory) ExpandDirectory(gameRoot, child.Path, reason, paths, entries, cancellationToken);
            else if (child.Kind != CleanupEntryKind.File) throw new CleanupPlanException($"Cleanup directory contains an unsupported entry: {child.Path}");
            if (child.Kind == CleanupEntryKind.File) AddEntry(gameRoot, child, reason, paths, entries);
        }
        AddEntry(gameRoot, _probe.Observe(directory, cancellationToken), reason, paths, entries);
    }

    private static void AddEntry(string gameRoot, CleanupProbeEntry observed, string reason, HashSet<string> paths, List<CleanupEntry> entries)
    {
        if (observed.Status != CleanupEntryStatus.Available || observed.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new CleanupPlanException($"Cleanup entry is not a direct available path: {observed.Path}");
        string physical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(observed.Path));
        if (!SameOrChild(physical, gameRoot)) throw new CleanupPlanException("Cleanup entry escapes the game root.");
        string relative = Path.GetRelativePath(gameRoot, physical).Replace('/', '\\');
        if (!paths.Add(relative)) throw new CleanupPlanException($"Cleanup entry collides by Windows path semantics: {relative}");
        entries.Add(new CleanupEntry(relative, physical, observed.Kind, observed.Status, reason, observed.Attributes, observed.FileIdentity, observed.Size, observed.Sha256) { Metadata = observed.Metadata });
    }

    internal static string ResolveUnder(string root, string relative)
    {
        string value = Path.GetFullPath(Path.Combine(root, relative));
        if (!SameOrChild(value, root) || PathEquals(value, root)) throw new CleanupPlanException("Cleanup relative path escapes its root.");
        return value;
    }

    internal static bool SameOrChild(string path, string root)
    {
        string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return PathEquals(normalizedPath, normalizedRoot)
            || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool PathEquals(string first, string second)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    internal static bool TouchesProtectedSystemRoot(string path)
    {
        Environment.SpecialFolder[] protectedFolders =
        [
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonProgramFiles,
            Environment.SpecialFolder.CommonProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData
        ];
        foreach (Environment.SpecialFolder folder in protectedFolders)
        {
            string value = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(value) && (SameOrChild(path, value) || SameOrChild(value, path))) return true;
        }
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string? windows = Path.GetDirectoryName(system);
        if (!string.IsNullOrEmpty(windows) && (SameOrChild(path, windows) || SameOrChild(windows, path))) return true;
        return false;
    }

    private static string NormalizeRelative(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new CleanupPlanException("Cleanup relative path is invalid.");
        string normalized = value.Replace('/', '\\');
        if (normalized.Split('\\').Any(segment => segment is "" or "." or "..")) throw new CleanupPlanException("Cleanup relative path is invalid.");
        return normalized;
    }

    private static string VolumeIdentity(string identity)
        => identity.Split(':', 2)[0];
}

public sealed class WindowsCleanupProbe : ICleanupProbe
{
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public CleanupRootObservation ObserveRoot(string path, bool create, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (create) Directory.CreateDirectory(full);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException($"Cleanup root does not exist: {full}");
        bool ancestry = HasReparseAncestor(full);
        FileAttributes attributes = File.GetAttributes(full);
        string physical = FinalPath(full, true, out string identity);
        string? root = Path.GetPathRoot(full);
        long available = new DriveInfo(root ?? full).AvailableFreeSpace;
        return new CleanupRootObservation(full, physical, identity, attributes.HasFlag(FileAttributes.ReparsePoint), root is not null && CleanupPlanner.PathEquals(full, root), ancestry, available);
    }

    public CleanupProbeEntry Observe(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint)) return new(path, Kind(attributes), CleanupEntryStatus.ReparsePoint, attributes, string.Empty, 0, string.Empty);
            if (attributes.HasFlag(FileAttributes.Directory))
            {
                FinalPath(path, true, out string identity);
                return new CleanupProbeEntry(path, CleanupEntryKind.Directory, CleanupEntryStatus.Available, attributes, identity, 0, string.Empty) { Metadata = ReadMetadata(path, true, attributes) };
            }
            CleanupEntry file = ObserveFileForPlan(Path.GetDirectoryName(path)!, path, string.Empty);
            return new CleanupProbeEntry(path, CleanupEntryKind.File, CleanupEntryStatus.Available, file.Attributes, file.FileIdentity, file.Size, file.Sha256) { Metadata = file.Metadata };
        }
        catch (FileNotFoundException) { return Missing(path); }
        catch (DirectoryNotFoundException) { return Missing(path); }
        catch (UnauthorizedAccessException) { return Status(path, CleanupEntryStatus.AccessDenied); }
        catch (IOException exception) { return Status(path, IsLocked(exception) ? CleanupEntryStatus.Locked : CleanupEntryStatus.Unavailable); }
    }

    public IReadOnlyList<CleanupProbeEntry> Enumerate(string path, CancellationToken cancellationToken)
    {
        List<CleanupProbeEntry> entries = [];
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            cancellationToken.ThrowIfCancellationRequested();
            entries.Add(Observe(child, cancellationToken));
        }
        return entries;
    }

    public static CleanupEntry ObserveFileForPlan(string gameRoot, string path, string reason)
    {
        string full = Path.GetFullPath(path);
        using SafeFileHandle handle = File.OpenHandle(full, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);
        FileAttributes attributes = File.GetAttributes(full);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new CleanupPlanException("Planned file is not a direct regular file.");
        string identity = Identity(handle);
        long size = RandomAccess.GetLength(handle);
        using FileStream stream = new(handle, FileAccess.Read, 81920, false);
        string sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        string relative = Path.GetRelativePath(gameRoot, full).Replace('/', '\\');
        return new CleanupEntry(relative, full, CleanupEntryKind.File, CleanupEntryStatus.Available, reason, attributes, identity, size, sha) { Metadata = ReadMetadata(full, false, attributes) };
    }

    public static string IdentityOfDirectory(string path)
    {
        FinalPath(path, true, out string identity);
        return identity;
    }

    internal static string Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        ulong index = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return $"{information.VolumeSerialNumber:x8}:{index:x16}";
    }

    internal static string FinalPath(string path, bool directory, out string identity)
    {
        using SafeFileHandle handle = CreateFile(path, 0x80, 1 | 2 | 4, 0, 3, directory ? 0x02000000u : 0u, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        identity = Identity(handle);
        char[] buffer = new char[512];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return RemovePrefix(new string(buffer, 0, (int)length));
    }

    internal static string FinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length) throw new Win32Exception(Marshal.GetLastPInvokeError());
        return RemovePrefix(new string(buffer, 0, (int)length));
    }

    private static bool HasReparseAncestor(string path)
    {
        string? root = Path.GetPathRoot(path);
        string current = root ?? throw new CleanupPlanException("Cleanup root has no drive root.");
        string relative = Path.GetRelativePath(current, path);
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
        }
        return false;
    }

    private static CleanupPathMetadata ReadMetadata(string path, bool directory, FileAttributes attributes)
    {
        FileSystemSecurity security = directory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        return new CleanupPathMetadata(
            attributes,
            File.GetCreationTimeUtc(path),
            File.GetLastWriteTimeUtc(path),
            File.GetLastAccessTimeUtc(path),
            security.GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access));
    }

    internal static CleanupPathMetadata ReadMetadataForTests(string path, bool directory)
        => ReadMetadata(path, directory, File.GetAttributes(path));

    private static string RemovePrefix(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ? path[4..]
            : path;

    private static CleanupProbeEntry Missing(string path) => Status(path, CleanupEntryStatus.Missing);
    private static CleanupProbeEntry Status(string path, CleanupEntryStatus status) => new(path, CleanupEntryKind.Other, status, 0, string.Empty, 0, string.Empty);
    private static CleanupEntryKind Kind(FileAttributes attributes) => attributes.HasFlag(FileAttributes.Directory) ? CleanupEntryKind.Directory : CleanupEntryKind.File;
    private static bool IsLocked(IOException exception) => (exception.HResult & 0xffff) is ErrorSharingViolation or ErrorLockViolation;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public FileAttributes FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes, uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint length, uint flags);
}
