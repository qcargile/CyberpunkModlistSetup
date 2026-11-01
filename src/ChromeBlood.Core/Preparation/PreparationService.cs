using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Parity;
using ChromeBlood.Core.Vanilla;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Preparation;

public sealed class PreparationService
{
    private readonly string _stateRoot;
    private readonly CleanupPreparationService _cleanup;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CleanupPreparationPreview? _lastCleanup;
    private CleanupPreparationPreview? _verifiedCleanup;
    private string? _verifiedGameRoot;
    private DateTimeOffset? _verifiedAt;
    private string? _manualCleanupRoot;
    private string? _manualCleanupIdentity;
    private string _cleanupStepFailure = "";
    private string? _cleanupFailureRoot;
    private string? _keptGameRoot;
    private IReadOnlyList<string> _keptGamePaths = [];

    public PreparationSnapshot KeepGamePaths(PreparationSnapshot snapshot, IEnumerable<string> paths)
    {
        _keptGameRoot = snapshot.SelectedGameRoot;
        _keptGamePaths = paths.Select(path => ChromeBlood.Contracts.Paths.ContractRelativePath.Normalize(path).Replace('/', '\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        Func<string, bool> isKept = CleanupPolicy.CreateKeepFilter(_keptGamePaths.ToArray());
        CleanupItemLabel[] items = snapshot.CleanupItems.Select(item => item with
        { IsKept = item.Scope is "Game" or "Changed official" && isKept(item.Path) }).ToArray();
        return snapshot with
        {
            KeptGamePaths = _keptGamePaths, CleanupItems = items,
            ChangedOfficialFiles = items.Count(item => item.Scope == "Changed official" && !item.IsKept)
        };
    }

    public string? RememberLocations(PreparationSnapshot snapshot)
    {
        try { SaveLocations(snapshot); return null; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return "These choices apply for this session, but could not be saved to " + Path.Combine(_stateRoot, "locations.json") + ". " + exception.Message; }
    }

    public PreparationSnapshot DeferCleanup(PreparationSnapshot snapshot)
    {
        _manualCleanupRoot = snapshot.SelectedGameRoot;
        _manualCleanupIdentity = snapshot.GameRootIdentity;
        return snapshot with
        {
            CleanupDeferred = true,
            ReadyForHandoff = RequiredChecksReady(snapshot) && snapshot.ChangedOfficialFiles == 0 && snapshot.MissingOfficialFiles == 0
        };
    }

    private static bool RequiredChecksReady(PreparationSnapshot snapshot)
        => snapshot.Checks.Where(check => check.Required).All(check => check.Status == CheckState.Ready);

    private bool CleanupWasDeferred(string? root, string? identity)
        => root is not null && string.Equals(root, _manualCleanupRoot, StringComparison.OrdinalIgnoreCase) && identity == _manualCleanupIdentity;

    public PreparationService()
    {
        _stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromeBloodSetup");
        _cleanup = new CleanupPreparationService(LegacyParityCorpus.LoadEmbedded(), _stateRoot);
    }

    internal PreparationService(string disposableStateRoot)
    {
        string temporary = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        if (!CleanupPlanner.SameOrChild(disposableStateRoot, temporary) || CleanupPlanner.PathEquals(disposableStateRoot, temporary))
            throw new ArgumentException("Test state must be inside an owned temporary folder.", nameof(disposableStateRoot));
        _stateRoot = Path.GetFullPath(disposableStateRoot);
        _cleanup = new CleanupPreparationService(LegacyParityCorpus.LoadEmbedded(), _stateRoot);
    }

    public async Task<PreparationSnapshot> ScanAsync(string? gameRoot, string? wabbajackDirectory, CancellationToken cancellationToken = default,
        string? gameBackupRoot = null, string? settingsBackupRoot = null)
    {
        SavedLocations saved = ReadLocations();
        PreparationSnapshot snapshot = await PreparationMachine.ScanAsync(gameRoot ?? saved.Game,
            wabbajackDirectory ?? saved.Wabbajack, cancellationToken).ConfigureAwait(false);
        if (gameRoot is null && snapshot.SelectedGameRoot is null && snapshot.GameCandidates.Count == 1)
            snapshot = await PreparationMachine.ScanAsync(null, snapshot.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
        _lastCleanup = null;
        if (snapshot.SelectedGameRoot is not { } selected)
        {
            _verifiedCleanup = null;
            _verifiedGameRoot = null;
            _verifiedAt = null;
            return snapshot;
        }
        bool sameSavedGame = string.Equals(selected, saved.Game, StringComparison.OrdinalIgnoreCase);
        if (_keptGameRoot is null || !string.Equals(selected, _keptGameRoot, StringComparison.OrdinalIgnoreCase))
        { _keptGameRoot = selected; _keptGamePaths = sameSavedGame ? saved.KeptGamePaths : []; }
        snapshot = snapshot with
        {
            GameBackupRoot = gameBackupRoot ?? (sameSavedGame ? saved.GameBackup : null),
            SettingsBackupRoot = settingsBackupRoot ?? saved.SettingsBackup,
            KeptGamePaths = _keptGamePaths
        };
        string? identity = null;
        try
        {
            identity = new WindowsCleanupProbe().ObserveRoot(selected, false, cancellationToken).FileIdentity;
            _lastCleanup = await _cleanup.PreviewAsync(selected, cancellationToken, snapshot.GameBackupRoot, snapshot.SettingsBackupRoot).ConfigureAwait(false);
            if (_verifiedGameRoot != identity || _lastCleanup.GameFiles?.ProfileMatch.Matched != true
                || _verifiedCleanup?.GameFiles?.ReferenceIdentity != _lastCleanup.GameFiles.ReferenceIdentity)
            { _verifiedCleanup = null; _verifiedGameRoot = null; _verifiedAt = null; }
            else if (!CanReuseGameFileVerification(identity, _lastCleanup.GameFiles, _verifiedGameRoot, _verifiedCleanup?.GameFiles))
                _verifiedAt = null;
            return WithCleanup(snapshot, _lastCleanup, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            _verifiedAt = null;
            return snapshot with
            {
                GameRootIdentity = identity,
                RecoveryDetail = "Cleanup could not be inspected: " + exception.Message,
                CleanupFailure = "Cleanup could not be inspected: " + exception.Message,
                CleanupIssue = true,
                CleanupDeferred = CleanupWasDeferred(selected, identity),
                ManualCleanupGuidance = ManualGuidance(selected),
                ChangedOfficialFiles = _verifiedGameRoot == identity ? _verifiedCleanup?.Items.Count(item => item.Scope == "Changed official") ?? 0 : 0,
                MissingOfficialFiles = _verifiedGameRoot == identity ? _verifiedCleanup?.Items.Count(item => item.Scope == "Missing official") ?? 0 : 0,
                ReadyForHandoff = RequiredChecksReady(snapshot) && identity is not null && CleanupWasDeferred(selected, identity)
                    && (_verifiedGameRoot != identity || _verifiedCleanup?.Items.Any(item => item.Scope is "Changed official" or "Missing official") != true)
            };
        }
    }

    public async Task<PreparationSnapshot> VerifyGameFilesAsync(PreparationSnapshot reviewed,
        IProgress<PreparationProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PreparationSnapshot current = await ScanAsync(reviewed.SelectedGameRoot, reviewed.WabbajackDirectory, cancellationToken,
                reviewed.GameBackupRoot, reviewed.SettingsBackupRoot).ConfigureAwait(false);
            if (current.SelectedGameRoot is null || current.GameRootIdentity != reviewed.GameRootIdentity)
                throw new InvalidOperationException("The selected game folder changed. Select it again before verification.");
            var reporting = new InlineProgress<VanillaScanProgress>(value => progress?.Report(new("Verifying official game files",
                $"{value.EntriesScanned:N0} entries checked; {value.BytesHashed:N0} bytes read. {value.CurrentPath}")));
            _lastCleanup = await _cleanup.PreviewAsync(current.SelectedGameRoot, true, reporting, cancellationToken,
                current.GameBackupRoot, current.SettingsBackupRoot).ConfigureAwait(false);
            _verifiedCleanup = _lastCleanup.GameFiles is { Complete: true, ProfileMatch.Matched: true } ? _lastCleanup : null;
            _verifiedGameRoot = _verifiedCleanup is null ? null : current.GameRootIdentity;
            _verifiedAt = _verifiedCleanup is null ? null : DateTimeOffset.UtcNow;
            return WithCleanup(current, _lastCleanup, current.GameRootIdentity!);
        }
        finally { _operation.Release(); }
    }

    private PreparationSnapshot WithCleanup(PreparationSnapshot snapshot, CleanupPreparationPreview preview, string identity)
    {
        VanillaScanResult? scan = preview.GameFiles;
        Func<string, bool> isKept = CleanupPolicy.CreateKeepFilter(snapshot.KeptGamePaths.ToArray());
        List<CleanupPreparationItem> items = [.. preview.Items];
        if (scan?.HashVerificationRequested != true && _verifiedGameRoot == identity && _verifiedCleanup is { } verified)
            items.AddRange(verified.Items.Where(item => item.Scope is "Changed official" or "Missing official")
                .Where(previous => scan?.Entries.Any(entry => entry.RelativePath.Equals(previous.RelativePath, StringComparison.OrdinalIgnoreCase)
                    && (entry.Status is VanillaEntryStatus.VendorReplacement or VanillaEntryStatus.OfficialVerified
                        || entry.Status == VanillaEntryStatus.OfficialUnverified && entry.ActualSize == entry.ExpectedSize
                            && entry.ActualSha1 is not null && entry.ActualSha1.Equals(entry.ExpectedSha1, StringComparison.OrdinalIgnoreCase))) != true)
                .Where(previous => !items.Any(item => item.Scope == previous.Scope && item.RelativePath == previous.RelativePath)));
        int changed = items.Count(item => item.Scope == "Changed official" && !isKept(item.RelativePath));
        HashSet<string> changedPaths = items.Where(item => item.Scope == "Changed official").Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int missing = items.Count(item => item.Scope == "Missing official");
        int unreadable = items.Count(item => item.Scope == "Unreadable");
        bool hashesChecked = _verifiedAt is not null
            && CanReuseGameFileVerification(identity, scan, _verifiedGameRoot, _verifiedCleanup?.GameFiles);
        string reference = scan is null ? "Known mod locations only. Official game contents have not been verified."
            : scan.ProfileMatch.Message + (scan.ProfileMatch.Matched ? " Vortex ownership records are retained."
                : " Known mod locations can still be cleaned; whole-folder classification is unavailable.");
        bool deferred = CleanupWasDeferred(snapshot.SelectedGameRoot, identity);
        return snapshot with
        {
            GameRootIdentity = identity,
            CleanupItems = items.Select(item => new CleanupItemLabel(item.RelativePath,
                item.Label + (item.Status == CleanupEntryStatus.Available ? "" : " (" + item.Status + ")"), item.Scope)
                { IsKept = item.Scope is "Game" or "Changed official" && isKept(item.RelativePath) }).ToArray(),
            PendingCleanup = preview.Pending.Length > 0,
            CleanupFailure = string.Join("\n\n", preview.Pending.Where(pending => !string.IsNullOrWhiteSpace(pending.FailureDetail))
                .Select(pending => "Saved cleanup " + pending.Id + ": " + pending.FailureDetail).Append(preview.InspectionFailure)
                .Append(string.Equals(snapshot.SelectedGameRoot, _cleanupFailureRoot, StringComparison.OrdinalIgnoreCase) ? _cleanupStepFailure : "")
                .Where(value => value.Length > 0)),
            CleanupIssue = preview.InspectionFailure.Length > 0 || scan is { ProfileMatch.Matched: true, Complete: false },
            CleanupDeferred = deferred,
            ManualCleanupGuidance = ManualGuidance(snapshot.SelectedGameRoot!),
            ManualCleanupItems = items.Select(item => new CleanupItemLabel(
                Path.Combine(item.Scope is "Settings" or "Diagnostics" ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) : snapshot.SelectedGameRoot!, item.RelativePath),
                item.Scope is "Game" or "Changed official" && isKept(item.RelativePath) ? "Kept by you. Leave this item in place."
                : item.Scope == "Preserved DLSS" ? "Verified DLSS file or Swapper backup; kept automatically."
                : item.Scope == "Game" && item.Status == CleanupEntryStatus.Available ? "Move unwanted added files to a backup outside the game folder."
                : item.Scope is "Changed official" or "Missing official" ? "Official game content: repair through your game store. Do not delete it manually."
                : item.Scope == "Settings" ? "Optional settings reset: back up this folder before moving it. Keep it if you want to retain your preferences."
                : item.Scope == "Diagnostics" ? "Optional crash-report cleanup: this can be left alone. It is not required for installation."
                : "Inspect this path and its access or link target. Do not delete or follow a linked folder blindly.", item.Scope)).ToArray(),
            CleanupReferenceDetail = reference,
            GameFileHashesChecked = hashesChecked,
            GameFileReferenceMatched = scan?.ProfileMatch.Matched == true,
            GameFileScanComplete = scan?.Complete == true,
            GameFileVerifiedAt = hashesChecked ? _verifiedAt : null,
            ChangedOfficialFiles = changed,
            MissingOfficialFiles = missing,
            UnreadableGameFiles = unreadable,
            PreservedDlssFiles = items.Count(item => item.Scope == "Preserved DLSS"),
            CleanupBytes = scan?.Entries.Where(entry => !isKept(entry.RelativePath) && (!entry.IsDirectory && entry.Status == VanillaEntryStatus.Extra
                && !IndexedGameCleanup.IsManagerOwned(entry.RelativePath)
                || !entry.IsDirectory && changedPaths.Contains(entry.RelativePath)))
                .Sum(entry => entry.ActualSize ?? 0) ?? 0,
            PlannedBackupRoot = "Game files: " + preview.GameBackupRoot + "\nOptional local settings: " + preview.AppDataBackupRoot,
            GameBackupRoot = preview.GameBackupRoot,
            SettingsBackupRoot = preview.AppDataBackupRoot,
            RetainedBackupRoot = preview.Pending.FirstOrDefault()?.BackupRoot,
            RecoveryDetail = preview.InspectionFailure.Length > 0 ? preview.InspectionFailure
                : preview.Pending.Length > 0 ? string.Join("\n", preview.Pending.Select(pending =>
                (string.IsNullOrEmpty(pending.GameRoot) ? "Unstarted saved cleanup." : "Saved cleanup for " + pending.GameRoot + ".") + " Keep preserved files at " + pending.BackupRoot + "."
                + (pending.Targets.Length == 0 ? "" : "\nRecorded paths:\n" + string.Join("\n", pending.Targets.Take(50).Select(path => Path.Combine(pending.GameRoot, path)))
                    + (pending.Targets.Length > 50 ? $"\n{pending.Targets.Length - 50:N0} more paths are recorded in the retained transaction journal." : ""))))
                : scan is { ProfileMatch.Matched: true, Complete: false }
                    ? "The game folder could not be inspected completely. Expand the findings, close active tools and resolve unreadable or linked paths, then check again. No indexed cleanup can run until the scan completes." : null,
            ReadyForHandoff = RequiredChecksReady(snapshot) && changed == 0 && missing == 0
                && (deferred || preview.InspectionFailure.Length == 0 && preview.Pending.Length == 0 && unreadable == 0
                    && scan is not { ProfileMatch.Matched: true, Complete: false })
        };
    }

    private static string ManualGuidance(string gameRoot)
        => "Close the game and mod tools. Purge Vortex mods through Vortex first. Keep all backups and journals. "
            + "Move only unwanted added files out of " + gameRoot + ". Keep intentional changes, including swapped DLSS files. "
            + "Use your game store to repair official files you want restored. Settings and crash reports are optional. "
            + "If a file is locked or access is denied, close the app using it or restart Windows. An incomplete scan cannot identify every file.";

    internal static bool CanReuseGameFileVerification(string identity, VanillaScanResult? current,
        string? verifiedIdentity, VanillaScanResult? verified)
        => !string.IsNullOrEmpty(identity) && identity == verifiedIdentity
            && current is { Complete: true, ProfileMatch.Matched: true }
            && verified is { HashVerificationRequested: true, Complete: true, ProfileMatch.Matched: true }
            && current.ReferenceIdentity == verified.ReferenceIdentity
            && !string.IsNullOrEmpty(verified.ObservationFingerprint)
            && current.ObservationFingerprint == verified.ObservationFingerprint;

    public async Task<PreparationResult> ApplyAsync(PreparationSnapshot reviewed, PreparationOptions options,
        IProgress<PreparationProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? backup = null;
        bool reboot = false;
        List<string> failures = [];
        try
        {
            PreparationSnapshot current = await ScanAsync(reviewed.SelectedGameRoot, reviewed.WabbajackDirectory, cancellationToken,
                reviewed.GameBackupRoot, reviewed.SettingsBackupRoot).ConfigureAwait(false);
            bool cleanupRootUnchanged = current.SelectedGameRoot is not null
                && string.Equals(current.SelectedGameRoot, reviewed.SelectedGameRoot, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(reviewed.GameRootIdentity) && current.GameRootIdentity == reviewed.GameRootIdentity;
            await RunPreparationActionAsync("Remembering your folders", () => { SaveLocations(current); return Task.CompletedTask; }, failures, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Preparing Windows components", "Only missing selected components will be installed."));
            await RunPreparationActionAsync("Windows components", async () =>
            {
                reboot |= await PreparationPackages.InstallPrerequisitesAsync(options, progress, cancellationToken, value => reboot |= value,
                    failure => failures.Add(failure)).ConfigureAwait(false);
            }, failures, cancellationToken).ConfigureAwait(false);
            if (options.SteamLaunchOnly && current.Checks.FirstOrDefault(check => check.Id == "steamupdate")?.Status != CheckState.Ready)
            {
                progress?.Report(new("Preparing the Steam update preference", "Steam must be closed before its Cyberpunk preference can change."));
                await RunPreparationActionAsync("Steam update preference", () => SteamPreparation.SetLaunchOnlyAsync(
                    current.SelectedGameRoot ?? throw new InvalidOperationException("Select a Steam game folder before changing its update preference."), cancellationToken),
                    failures, cancellationToken).ConfigureAwait(false);
            }
            if (options.CleanGame || options.ResetSettings || options.ClearDiagnostics)
            {
                int cleanupFailuresStart = failures.Count;
                await RunPreparationActionAsync("Cleanup", async () =>
                {
                    if (!cleanupRootUnchanged)
                        throw new IOException("The game folder changed after review. Check it again before cleanup. Other preparation can continue.");
                    if (_lastCleanup is null || current.CleanupIssue && !current.PendingCleanup)
                        throw new IOException(current.CleanupFailure.Length > 0 ? current.CleanupFailure : current.RecoveryDetail ?? "Cleanup could not be inspected safely. Use the manual cleanup instructions or check again.");
                    if (_lastCleanup is { Pending.Length: > 0 } pending)
                    {
                        if (!options.CleanGame) throw new IOException("A saved cleanup is still pending. Keep its backup and use Retry cleanup or continue with manual cleanup. No new cleanup was started.");
                        foreach (CleanupPreparationPending transaction in pending.Pending)
                        {
                            backup = transaction.BackupRoot;
                            await RunPreparationActionAsync("Saved cleanup " + transaction.Id, async () =>
                            {
                                CleanupPreparationResult resumed = await _cleanup.ResumeAsync(current.SelectedGameRoot!, transaction.Id,
                                    message => progress?.Report(new("Continuing cleanup", message)), cancellationToken).ConfigureAwait(false);
                                backup = resumed.BackupRoot;
                                if (!resumed.Completed) throw new IOException(resumed.Message ?? "The saved cleanup is unfinished. Keep the backup and use the manual cleanup instructions.");
                            }, failures, cancellationToken).ConfigureAwait(false);
                        }
                        return;
                    }
                    backup = options.CleanGame ? _lastCleanup.GameBackupRoot : _lastCleanup.AppDataBackupRoot;
                    CleanupPreparationResult result = await _cleanup.ExecuteAsync(current.SelectedGameRoot!,
                        options.CleanGame, options.ResetSettings, options.ClearDiagnostics,
                        message => progress?.Report(new("Preparing the selected installation", message)), cancellationToken,
                        options.CleanGame ? current.CleanupItems.Where(item => item.Scope == "Changed official" && !item.IsKept).Select(item => item.Path).ToArray() : null,
                        current.GameBackupRoot, current.SettingsBackupRoot, current.KeptGamePaths).ConfigureAwait(false);
                    backup = result.BackupRoot;
                    if (result.GameFiles is { ProfileMatch.Matched: true, Complete: true } verifiedFiles && _lastCleanup is { } inspected)
                    {
                        _verifiedCleanup = inspected with
                        {
                            GameFiles = verifiedFiles,
                            Items = [.. inspected.Items.Where(item => item.Scope is not ("Changed official" or "Missing official" or "Unreadable" or "Preserved DLSS")),
                                .. CleanupPreparationService.OfficialFindings(verifiedFiles)]
                        };
                        _verifiedGameRoot = current.GameRootIdentity;
                        _verifiedAt = DateTimeOffset.UtcNow;
                    }
                    if (!result.Completed)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new IOException(result.Message ?? "Cleanup has remaining work. Preserved files are retained. Use the manual cleanup instructions or retry.");
                    }
                }, failures, cancellationToken).ConfigureAwait(false);
                _cleanupFailureRoot = current.SelectedGameRoot;
                _cleanupStepFailure = string.Join("\n\n", failures.Skip(cleanupFailuresStart));
            }
            if (options.DownloadWabbajack)
            {
                await RunPreparationActionAsync("Wabbajack download", async () =>
                {
                    if (current.Checks.FirstOrDefault(check => check.Id == "wabbajackfolder")?.Status != CheckState.Ready)
                        throw new InvalidOperationException("Choose an allowed, writable Wabbajack app folder before downloading it.");
                    string? existing = await PreparationMachine.FindVerifiedWabbajackAsync(current.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
                    if (existing is null)
                        await PreparationPackages.DownloadWabbajackAsync(current.WabbajackDirectory, progress, cancellationToken).ConfigureAwait(false);
                }, failures, cancellationToken).ConfigureAwait(false);
            }
            string message = _verifiedCleanup?.Items.Any(item => item.Scope is "Changed official" or "Missing official") == true
                ? "Backups are kept. Changed official files remain in place; keep your replacements or repair them through your game store."
                : "Selected preparation finished.";
            if (failures.Count > 0) message = "Preparation continued, but these actions need attention:\n\n" + string.Join("\n\n", failures)
                + "\n\nCompleted actions are kept. Retry only the unfinished actions, or use the manual instructions. Cleanup does not have to finish before other preparation.";
            return await ResultAsync(current, failures.Count == 0, false, message, backup, reboot).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await ResultAsync(reviewed, false, true, "Preparation stopped. Completed work and preserved files are retained.", backup, reboot).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return await ResultAsync(reviewed, false, false, exception.Message, backup, reboot).ConfigureAwait(false);
        }
        finally { _operation.Release(); }
    }

    internal static async Task<bool> RunPreparationActionAsync(string title, Func<Task> action, List<string> failures, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            await action().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            failures.Add(title + ": " + (exception is OperationCanceledException ? "The operation timed out. Check the connection and retry." : exception.Message));
            return false;
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async Task<string> OpenWabbajackAsync(PreparationSnapshot reviewed, CancellationToken cancellationToken = default,
        bool useCurrentFolder = false)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PreparationSnapshot current = await PreparationMachine.ScanAsync(reviewed.SelectedGameRoot, reviewed.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
            if (!useCurrentFolder && current.Checks.FirstOrDefault(check => check.Id == "wabbajackfolder")?.Status != CheckState.Ready)
                throw new InvalidOperationException("Choose a recommended Wabbajack folder, or select Use this folder anyway for your existing app.");
            string executable = await PreparationMachine.FindVerifiedWabbajackAsync(current.WabbajackDirectory, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The verified Wabbajack app is no longer available. Prepare it again.");
            cancellationToken.ThrowIfCancellationRequested();
            ProcessStartInfo start = new(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("wabbajack://Chrome_and_Blood/SchoolBoyQ");
            using Process? process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows could not open Wabbajack. Open " + executable
                + " manually, choose Browse Modlists, then search for Chrome & Blood.");
            string? saveFailure = RememberLocations(reviewed);
            if (saveFailure is not null) return "Chrome & Blood was sent to Wabbajack. " + saveFailure;
            return "Chrome & Blood was sent to Wabbajack. Choose installation and download folders there; an existing archive folder can be reused.";
        }
        finally { _operation.Release(); }
    }

    private async Task<PreparationResult> ResultAsync(PreparationSnapshot previous, bool completed, bool cancelled, string message, string? backup, bool reboot)
    {
        PreparationSnapshot snapshot;
        try { snapshot = await ScanAsync(previous.SelectedGameRoot, previous.WabbajackDirectory, CancellationToken.None,
            previous.GameBackupRoot, previous.SettingsBackupRoot).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _verifiedAt = null;
            snapshot = previous with
            {
                ReadyForHandoff = false,
                RecoveryDetail = exception.Message,
                CleanupFailure = "The current cleanup state could not be inspected: " + exception.Message,
                CleanupIssue = true,
                GameFileHashesChecked = false,
                GameFileScanComplete = false,
                GameFileVerifiedAt = null
            };
        }
        return new(snapshot, completed, cancelled, message, backup, reboot);
    }

    private sealed record SavedLocations(string? Game = null, string? Wabbajack = null, string? GameBackup = null,
        string? SettingsBackup = null, IReadOnlyList<string>? KeptPaths = null)
    {
        public IReadOnlyList<string> KeptGamePaths => KeptPaths ?? [];
    }

    private SavedLocations ReadLocations()
    {
        string path = Path.Combine(_stateRoot, "locations.json");
        if (!File.Exists(path)) return new();
        try
        {
            using SecureCacheRoot root = new(_stateRoot);
            using SecureFileLease? file = root.TryOpenRead("locations.json");
            if (file is null || file.Length > 32 * 1024 * 1024) return new();
            byte[] bytes = new byte[(int)file.Length];
            int read = RandomAccess.Read(file.Handle, bytes, 0);
            if (read != bytes.Length) return new();
            using JsonDocument json = JsonDocument.Parse(bytes);
            string? ReadString(string name) => json.RootElement.TryGetProperty(name, out JsonElement value) ? value.GetString() : null;
            string[] kept = json.RootElement.TryGetProperty("keptGamePaths", out JsonElement paths)
                ? paths.EnumerateArray().Take(200000).Select(value => ChromeBlood.Contracts.Paths.ContractRelativePath.Normalize(value.GetString()!).Replace('/', '\\')).ToArray() : [];
            return new(ReadString("game"), ReadString("wabbajack"), ReadString("gameBackup"), ReadString("settingsBackup"), kept);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return new();
        }
    }

    private void SaveLocations(PreparationSnapshot snapshot)
        => WriteOwnFile(_stateRoot, "locations.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            game = snapshot.SelectedGameRoot,
            wabbajack = snapshot.WabbajackDirectory,
            gameBackup = snapshot.GameBackupRoot,
            settingsBackup = snapshot.SettingsBackupRoot,
            keptGamePaths = snapshot.KeptGamePaths
        }));

    private static void WriteOwnFile(string directory, string name, byte[] bytes)
    {
        using SecureCacheRoot root = new(directory);
        using SafeFileHandle gate = root.OpenLock("setup-state.lock");
        using (SecureFileLease? current = root.TryOpenRead(name))
        {
            if (current is not null && current.Length == bytes.Length)
            {
                byte[] existing = new byte[bytes.Length];
                int read = RandomAccess.Read(current.Handle, existing, 0);
                if (read == existing.Length && existing.AsSpan().SequenceEqual(bytes)) return;
            }
        }
        string temporary = Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using SafeFileHandle file = root.CreateAtomicPartial(temporary);
            RandomAccess.Write(file, bytes, 0);
            RandomAccess.FlushToDisk(file);
            new WindowsSecureFileApi().Rename(file, Path.Combine(root.Path, name), true);
        }
        finally { root.DeleteIfPresent(temporary); }
    }
}
