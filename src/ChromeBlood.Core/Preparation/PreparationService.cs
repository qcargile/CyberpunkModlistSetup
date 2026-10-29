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

    public PreparationService()
    {
        _stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromeBloodSetup");
        _cleanup = new CleanupPreparationService(LegacyParityCorpus.LoadEmbedded(), _stateRoot);
    }

    public async Task<PreparationSnapshot> ScanAsync(string? gameRoot, string? wabbajackDirectory, CancellationToken cancellationToken = default)
    {
        (string? savedGame, string? savedWabbajack) = ReadLocations();
        PreparationSnapshot snapshot = await PreparationMachine.ScanAsync(gameRoot ?? savedGame,
            wabbajackDirectory ?? savedWabbajack, cancellationToken).ConfigureAwait(false);
        if (snapshot.SelectedGameRoot is null && snapshot.GameCandidates.Count == 1)
            snapshot = await PreparationMachine.ScanAsync(null, snapshot.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
        _lastCleanup = null;
        if (snapshot.SelectedGameRoot is not { } selected)
        {
            _verifiedCleanup = null;
            _verifiedGameRoot = null;
            _verifiedAt = null;
            return snapshot;
        }
        string? identity = null;
        try
        {
            identity = new WindowsCleanupProbe().ObserveRoot(selected, false, cancellationToken).FileIdentity;
            _lastCleanup = await _cleanup.PreviewAsync(selected, cancellationToken).ConfigureAwait(false);
            if (_verifiedGameRoot != identity || _lastCleanup.GameFiles?.ProfileMatch.Matched != true
                || _verifiedCleanup?.GameFiles?.ReferenceIdentity != _lastCleanup.GameFiles.ReferenceIdentity)
            { _verifiedCleanup = null; _verifiedGameRoot = null; _verifiedAt = null; }
            else if (!CanReuseGameFileVerification(identity, _lastCleanup.GameFiles, _verifiedGameRoot, _verifiedCleanup?.GameFiles))
                _verifiedAt = null;
            return WithCleanup(snapshot, _lastCleanup, identity);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _verifiedAt = null;
            return snapshot with
            {
                GameRootIdentity = identity,
                RecoveryDetail = "Cleanup could not be inspected: " + exception.Message,
                CleanupIssue = true,
                ReadyForHandoff = false
            };
        }
    }

    public async Task<PreparationSnapshot> VerifyGameFilesAsync(PreparationSnapshot reviewed,
        IProgress<PreparationProgress>? progress, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PreparationSnapshot current = await ScanAsync(reviewed.SelectedGameRoot, reviewed.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
            if (current.SelectedGameRoot is null || current.GameRootIdentity != reviewed.GameRootIdentity)
                throw new InvalidOperationException("The selected game folder changed. Select it again before verification.");
            var reporting = new InlineProgress<VanillaScanProgress>(value => progress?.Report(new("Verifying official game files",
                $"{value.EntriesScanned:N0} entries checked; {value.BytesHashed:N0} bytes read. {value.CurrentPath}")));
            _lastCleanup = await _cleanup.PreviewAsync(current.SelectedGameRoot, true, reporting, cancellationToken).ConfigureAwait(false);
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
        List<CleanupPreparationItem> items = [.. preview.Items];
        if (scan?.HashVerificationRequested != true && _verifiedGameRoot == identity && _verifiedCleanup is { } verified)
            items.AddRange(verified.Items.Where(item => item.Scope is "Changed official" or "Missing official")
                .Where(previous => !items.Any(item => item.Scope == previous.Scope && item.RelativePath == previous.RelativePath)));
        int changed = items.Count(item => item.Scope == "Changed official");
        HashSet<string> changedPaths = items.Where(item => item.Scope == "Changed official").Select(item => item.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int missing = items.Count(item => item.Scope == "Missing official");
        int unreadable = items.Count(item => item.Scope == "Unreadable");
        bool hashesChecked = _verifiedAt is not null
            && CanReuseGameFileVerification(identity, scan, _verifiedGameRoot, _verifiedCleanup?.GameFiles);
        string reference = scan is null ? "Known mod locations only. Official game contents have not been verified."
            : scan.ProfileMatch.Message + (scan.ProfileMatch.Matched ? " Vortex ownership records are retained."
                : " Known mod locations can still be cleaned; whole-folder classification is unavailable.");
        return snapshot with
        {
            GameRootIdentity = identity,
            CleanupItems = items.Select(item => new CleanupItemLabel(item.RelativePath,
                item.Label + (item.Status == CleanupEntryStatus.Available ? "" : " (" + item.Status + ")"), item.Scope)).ToArray(),
            PendingCleanup = preview.Pending.Length > 0,
            CleanupIssue = scan is { ProfileMatch.Matched: true, Complete: false },
            CleanupReferenceDetail = reference,
            GameFileHashesChecked = hashesChecked,
            GameFileReferenceMatched = scan?.ProfileMatch.Matched == true,
            GameFileScanComplete = scan?.Complete == true,
            GameFileVerifiedAt = hashesChecked ? _verifiedAt : null,
            ChangedOfficialFiles = changed,
            MissingOfficialFiles = missing,
            UnreadableGameFiles = unreadable,
            CleanupBytes = scan?.Entries.Where(entry => !entry.IsDirectory && entry.Status == VanillaEntryStatus.Extra
                && !IndexedGameCleanup.IsManagerOwned(entry.RelativePath)
                || !entry.IsDirectory && changedPaths.Contains(entry.RelativePath))
                .Sum(entry => entry.ActualSize ?? 0) ?? 0,
            PlannedBackupRoot = "Game files: " + preview.GameBackupRoot + "\nOptional local settings: " + preview.AppDataBackupRoot,
            RecoveryDetail = preview.Pending.Length > 0 ? string.Join("\n", preview.Pending.Select(pending =>
                "Continue the saved preparation for " + pending.GameRoot + ". Preserved files: " + pending.BackupRoot))
                : scan is { ProfileMatch.Matched: true, Complete: false }
                    ? "The game folder could not be inspected completely. Expand the findings, close active tools and resolve unreadable or linked paths, then check again. No indexed cleanup can run until the scan completes." : null,
            ReadyForHandoff = snapshot.Checks.Where(check => check.Required).All(check => check.Status == CheckState.Ready)
                && preview.Pending.Length == 0 && changed == 0 && missing == 0 && unreadable == 0
                && scan is not { ProfileMatch.Matched: true, Complete: false }
        };
    }

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
        try
        {
            PreparationSnapshot current = await ScanAsync(reviewed.SelectedGameRoot, reviewed.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
            if (current.SelectedGameRoot is null || !string.Equals(current.SelectedGameRoot, reviewed.SelectedGameRoot, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(reviewed.GameRootIdentity) || current.GameRootIdentity != reviewed.GameRootIdentity)
                throw new InvalidOperationException("The selected game folder changed after review. Check it again, then review preparation.");
            if (options.CleanGame && current.CleanupIssue)
                throw new InvalidOperationException(current.RecoveryDetail);
            SaveLocations(current);
            progress?.Report(new("Preparing Windows components", "Only missing selected components will be installed."));
            reboot = await PreparationPackages.InstallPrerequisitesAsync(options, progress, cancellationToken, value => reboot |= value).ConfigureAwait(false);
            if (options.SteamLaunchOnly && current.Checks.FirstOrDefault(check => check.Id == "steamupdate")?.Status != CheckState.Ready)
            {
                progress?.Report(new("Preparing the Steam update preference", "Steam must be closed before its Cyberpunk preference can change."));
                await SteamPreparation.SetLaunchOnlyAsync(current.SelectedGameRoot, cancellationToken).ConfigureAwait(false);
            }
            if (_lastCleanup is { Pending.Length: > 0 } pending)
            {
                if (!options.CleanGame) throw new InvalidOperationException("Select cleanup to continue the saved preparation before starting another operation.");
                foreach (CleanupPreparationPending transaction in pending.Pending)
                {
                    CleanupPreparationResult result = await _cleanup.ResumeAsync(current.SelectedGameRoot, transaction.Id,
                        message => progress?.Report(new("Continuing preparation", message)), cancellationToken).ConfigureAwait(false);
                    backup = result.BackupRoot;
                    if (!result.Completed)
                        return await ResultAsync(current, false, result.Cancelled, result.Message ?? "Preparation has remaining work. Preserved files are retained.", backup, reboot).ConfigureAwait(false);
                }
            }
            else if (options.CleanGame || options.ResetSettings || options.ClearDiagnostics)
            {
                backup = options.CleanGame ? _lastCleanup?.GameBackupRoot : _lastCleanup?.AppDataBackupRoot;
                CleanupPreparationResult result = await _cleanup.ExecuteAsync(current.SelectedGameRoot,
                    options.CleanGame, options.ResetSettings, options.ClearDiagnostics,
                    message => progress?.Report(new("Preparing the selected installation", message)), cancellationToken,
                    options.CleanGame ? current.CleanupItems.Where(item => item.Scope == "Changed official").Select(item => item.Path).ToArray() : null).ConfigureAwait(false);
                backup = result.BackupRoot;
                if (result.GameFiles is { ProfileMatch.Matched: true, Complete: true } verifiedFiles && _lastCleanup is { } inspected)
                {
                    _verifiedCleanup = inspected with
                    {
                        GameFiles = verifiedFiles,
                        Items = [.. inspected.Items.Where(item => item.Scope is not ("Changed official" or "Missing official" or "Unreadable")),
                            .. CleanupPreparationService.OfficialFindings(verifiedFiles)]
                    };
                    _verifiedGameRoot = current.GameRootIdentity;
                    _verifiedAt = DateTimeOffset.UtcNow;
                }
                if (!result.Completed)
                    return await ResultAsync(current, false, result.Cancelled, result.Message ?? "Preparation has remaining work. Preserved files are retained.", backup, reboot).ConfigureAwait(false);
            }
            if (options.DownloadWabbajack)
            {
                if (current.Checks.FirstOrDefault(check => check.Id == "wabbajackfolder")?.Status != CheckState.Ready)
                    throw new InvalidOperationException("Choose an allowed, writable Wabbajack app folder before downloading it.");
                string? existing = await PreparationMachine.FindVerifiedWabbajackAsync(current.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
                if (existing is null)
                    await PreparationPackages.DownloadWabbajackAsync(current.WabbajackDirectory, progress, cancellationToken).ConfigureAwait(false);
            }
            string message = _verifiedCleanup?.Items.Any(item => item.Scope is "Changed official" or "Missing official") == true
                ? "Cleanup backups are retained. Changed official files remain in place with backup copies. Repair the game in Steam, then use Verify game files before continuing."
                : "Preparation finished. Continue with the remaining checks or open Chrome & Blood in Wabbajack.";
            return await ResultAsync(current, true, false, message, backup, reboot).ConfigureAwait(false);
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

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    public async Task<string> OpenWabbajackAsync(PreparationSnapshot reviewed, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PreparationSnapshot current = await ScanAsync(reviewed.SelectedGameRoot, reviewed.WabbajackDirectory, cancellationToken).ConfigureAwait(false);
            if (!current.ReadyForHandoff) throw new InvalidOperationException("Resolve the remaining required checks before opening Chrome & Blood in Wabbajack.");
            string executable = await PreparationMachine.FindVerifiedWabbajackAsync(current.WabbajackDirectory, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The verified Wabbajack app is no longer available. Prepare it again.");
            cancellationToken.ThrowIfCancellationRequested();
            ProcessStartInfo start = new(executable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! };
            start.ArgumentList.Add("wabbajack://Chrome_and_Blood/SchoolBoyQ");
            using Process? process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("Windows could not open Wabbajack. Open the C&B page to continue.");
            SaveLocations(current);
            return "Chrome & Blood was sent to Wabbajack. Choose installation and download folders there; an existing archive folder can be reused.";
        }
        finally { _operation.Release(); }
    }

    private async Task<PreparationResult> ResultAsync(PreparationSnapshot previous, bool completed, bool cancelled, string message, string? backup, bool reboot)
    {
        PreparationSnapshot snapshot;
        try { snapshot = await ScanAsync(previous.SelectedGameRoot, previous.WabbajackDirectory, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _verifiedAt = null;
            snapshot = previous with
            {
                ReadyForHandoff = false,
                RecoveryDetail = exception.Message,
                CleanupIssue = true,
                GameFileHashesChecked = false,
                GameFileScanComplete = false,
                GameFileVerifiedAt = null
            };
        }
        return new(snapshot, completed, cancelled, message, backup, reboot);
    }

    private (string? Game, string? Wabbajack) ReadLocations()
    {
        string path = Path.Combine(_stateRoot, "locations.json");
        if (!File.Exists(path)) return (null, null);
        try
        {
            using SecureCacheRoot root = new(_stateRoot);
            using SecureFileLease? file = root.TryOpenRead("locations.json");
            if (file is null || file.Length > 32768) return (null, null);
            byte[] bytes = new byte[(int)file.Length];
            int read = RandomAccess.Read(file.Handle, bytes, 0);
            if (read != bytes.Length) return (null, null);
            using JsonDocument json = JsonDocument.Parse(bytes);
            return (json.RootElement.TryGetProperty("game", out JsonElement game) ? game.GetString() : null,
                json.RootElement.TryGetProperty("wabbajack", out JsonElement wabbajack) ? wabbajack.GetString() : null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }

    private void SaveLocations(PreparationSnapshot snapshot)
        => WriteOwnFile(_stateRoot, "locations.json", JsonSerializer.SerializeToUtf8Bytes(new
        {
            game = snapshot.SelectedGameRoot,
            wabbajack = snapshot.WabbajackDirectory
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
        using SafeFileHandle file = root.CreateAtomicPartial(temporary);
        RandomAccess.Write(file, bytes, 0);
        RandomAccess.FlushToDisk(file);
        new WindowsSecureFileApi().Rename(file, Path.Combine(root.Path, name), true);
    }
}
