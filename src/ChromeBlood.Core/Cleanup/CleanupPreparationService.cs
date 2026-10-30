using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ChromeBlood.Contracts.Serialization;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Parity;
using ChromeBlood.Core.Vanilla;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Cleanup;

public sealed record CleanupPreparationItem(string Scope, string RelativePath, string Label, CleanupEntryStatus Status);
public sealed record CleanupPreparationPending(string Id, string GameRoot, string BackupRoot, string State, string[] Targets);
public sealed record CleanupPreparationPreview(CleanupPreparationItem[] Items, CleanupPreparationPending[] Pending, string GameBackupRoot, string AppDataBackupRoot)
{
    public VanillaScanResult? GameFiles { get; init; }
}
public sealed record CleanupPreparationResult(string BackupRoot, bool Completed, bool Cancelled, string? Message = null)
{
    public VanillaScanResult? GameFiles { get; init; }
}

public sealed class CleanupPreparationService
{
    private readonly LegacyParityCorpus _corpus;
    private readonly string _stateRoot;
    private readonly string _appData;
    private readonly string _appDataBackup;
    private readonly string? _disposableRoot;

    public CleanupPreparationService(string corpusRoot, string? stateRoot = null)
        : this(LegacyParityCorpus.Load(corpusRoot), stateRoot)
    {
    }

    public CleanupPreparationService(LegacyParityCorpus corpus, string? stateRoot = null)
    {
        _corpus = corpus;
        _stateRoot = stateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromeBloodSetup");
        _appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _appDataBackup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ChromeBlood-Backups", "LocalAppData");
    }

    private CleanupPreparationService(LegacyParityCorpus corpus, string stateRoot, string appData, string appDataBackup, string disposableRoot)
    {
        _corpus = corpus;
        _stateRoot = stateRoot;
        _appData = appData;
        _appDataBackup = appDataBackup;
        _disposableRoot = disposableRoot;
    }

    internal static CleanupPreparationService ForDisposableTests(string disposableRoot, LegacyParityCorpus corpus, string stateRoot, string appData, string appDataBackup)
    {
        _ = OwnedCleanupMutationScope.ForDisposableTestRoots(disposableRoot, appData, appDataBackup);
        if (!CleanupPlanner.SameOrChild(stateRoot, disposableRoot)) throw new CleanupAuthorizationException("The test journal is outside its owned disposable root.");
        return new(corpus, stateRoot, appData, appDataBackup, disposableRoot);
    }

    public Task<CleanupPreparationPreview> PreviewAsync(string gameRoot, CancellationToken cancellationToken)
        => PreviewAsync(gameRoot, false, null, cancellationToken);

    public Task<CleanupPreparationPreview> PreviewAsync(string gameRoot, bool verifyOfficialHashes,
        IProgress<VanillaScanProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            CleanupPreparationPreview preview = Preview(gameRoot, cancellationToken);
            VanillaScanResult scan = await new VanillaGameScanner().ScanAsync(gameRoot,
                IndexedGameCleanup.ReadSteamManifest(gameRoot), verifyOfficialHashes, progress, cancellationToken).ConfigureAwait(false);
            if (!scan.ProfileMatch.Matched) return preview with { GameFiles = scan };
            List<CleanupPreparationItem> items = preview.Items.Where(item => item.Scope != "Game").ToList();
            if (scan.Complete)
                items.AddRange(IndexedGameCleanup.CreatePolicy(scan).Targets.Select(target =>
                    new CleanupPreparationItem("Game", target.RelativePath, target.Reason, CleanupEntryStatus.Available)));
            items.AddRange(OfficialFindings(scan));
            return preview with { Items = [.. items], GameFiles = scan };
        }, cancellationToken);

    internal static IEnumerable<CleanupPreparationItem> OfficialFindings(VanillaScanResult scan)
    {
        foreach (VanillaScanEntry entry in scan.Entries.Where(entry => entry.Status is VanillaEntryStatus.VendorReplacement or VanillaEntryStatus.VendorBackup))
            yield return new("Preserved DLSS", entry.RelativePath,
                entry.Status == VanillaEntryStatus.VendorReplacement
                    ? "NVIDIA-signed DLSS replacement; kept in place. Its signature does not establish game compatibility."
                    : "Verified DLSS Swapper backup; kept in place so Swapper can restore it.", CleanupEntryStatus.Available);
        foreach (VanillaScanEntry entry in scan.Entries.Where(entry => entry.Status is VanillaEntryStatus.Changed or VanillaEntryStatus.Missing
            or VanillaEntryStatus.Unreadable or VanillaEntryStatus.ReparsePoint))
            yield return new(entry.Status == VanillaEntryStatus.Changed ? "Changed official" : entry.Status == VanillaEntryStatus.Missing ? "Missing official" : "Unreadable",
                entry.RelativePath, entry.Status == VanillaEntryStatus.Changed ? "Official game content differs from the Steam reference; back up the original and repair through Steam."
                    : entry.Status == VanillaEntryStatus.Missing ? "Official game content is missing; repair through Steam."
                    : "This path could not be inspected safely. Close active tools or resolve its access before cleanup.", CleanupEntryStatus.Available);
    }

    public Task<CleanupPreparationResult> ExecuteAsync(string gameRoot, bool cleanGame, bool resetSettings, bool clearDiagnostics,
        Action<string> progress, CancellationToken cancellationToken, IReadOnlyCollection<string>? changedOfficialPaths = null)
        => Task.Run(async () =>
        {
            string game = Normalize(gameRoot);
            string gameBackup = GameBackup(game);
            string backup = cleanGame ? gameBackup : _appDataBackup;
            VanillaScanResult? verifiedFiles = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!cleanGame && !resetSettings && !clearDiagnostics) return new CleanupPreparationResult(backup, true, false);
                RequireGame(game);
                RequireClosedWriters();
                using SecureCacheRoot heldGame = new(game, new WindowsSecureFileApi(), false);
                UndoJournal journal = Journal();
                if (Pending(journal, game, gameBackup).Length > 0)
                    throw new CleanupAuthorizationException("An interrupted preparation needs to be resumed before starting another cleanup.");
                if (cleanGame)
                {
                    RequireClosedSteam();
                    RequireNoDeployment(game);
                    using BackupLease lease = new(gameBackup);
                    var reporting = new CleanupScanProgress(value => progress($"Verifying official game files: {value.BytesHashed:N0} bytes read. {value.CurrentPath}"));
                    VanillaScanResult scan = await new VanillaGameScanner().ScanAsync(game,
                        IndexedGameCleanup.ReadSteamManifest(game), true, reporting, cancellationToken).ConfigureAwait(false);
                    verifiedFiles = scan;
                    CleanupPolicy policy = scan.ProfileMatch.Matched ? IndexedGameCleanup.CreatePolicy(scan) : CleanupPolicy.ForPreparation(_corpus);
                    if (scan.ProfileMatch.Matched)
                    {
                        RequireMatchingReference(game);
                        VanillaGameIndex index = VanillaGameIndex.LoadEmbedded();
                        string[] originals = scan.Entries.Where(entry => entry.Status == VanillaEntryStatus.Changed
                                && !entry.IsDirectory && entry.ExpectedSha1 is not null).Select(entry => entry.RelativePath)
                            .Concat(changedOfficialPaths ?? []).Where(path => index.Files.ContainsKey(path)
                                && File.Exists(CleanupPlanner.ResolveUnder(game, path)))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        await OfficialFileBackup.PreserveAsync(game, gameBackup, originals, progress, cancellationToken).ConfigureAwait(false);
                    }
                    CleanupPlanner planner = new();
                    CleanupPlan plan = planner.Plan(game, gameBackup, policy, cancellationToken);
                    string[] selected = SelectAvailable(plan, null);
                    if (selected.Length > 0)
                    {
                        RequireClosedWriters();
                        RequireClosedSteam();
                        RequireNoDeployment(game);
                        if (scan.ProfileMatch.Matched) RequireMatchingReference(game);
                        AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, selected, policy, cancellationToken);
                        progress($"Preserving {plan.Entries.Length} files and folders in {gameBackup}. Large installations can take a few minutes.");
                        string scope = scan.ProfileMatch.Matched ? "game-indexed-" + VanillaGameIndex.LoadEmbedded().BuildId : "game";
                        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, NewId(scope), journal, cancellationToken).ConfigureAwait(false);
                        if (result.State != QuarantineTransactionState.Committed)
                            return Failed(result, gameBackup, cancellationToken);
                    }
                }
                if (resetSettings || clearDiagnostics)
                {
                    RequireClosedWriters();
                    using SecureCacheRoot heldAppData = new(_appData, new WindowsSecureFileApi(), false);
                    using BackupLease lease = new(_appDataBackup);
                    CleanupPolicy policy = CleanupPolicy.CyberpunkAppData(resetSettings, clearDiagnostics);
                    CleanupPlanner planner = new();
                    CleanupPlan plan = planner.Plan(_appData, _appDataBackup, policy, cancellationToken);
                    string[] requested = policy.Targets.Where(value => value.RelativePath == "REDEngine" ? clearDiagnostics : resetSettings).Select(value => value.RelativePath).ToArray();
                    string[] selected = SelectAvailable(plan, requested);
                    if (selected.Length > 0)
                    {
                        RequireClosedWriters();
                        AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, selected, policy, cancellationToken);
                        progress("Moving selected local settings and diagnostics to " + _appDataBackup);
                        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, NewId("appdata"), journal, cancellationToken).ConfigureAwait(false);
                        if (result.State != QuarantineTransactionState.Committed)
                            return Failed(result, _appDataBackup, cancellationToken);
                    }
                }
                progress("Preparation cleanup completed. Backups are retained.");
                return new CleanupPreparationResult(backup, true, false) { GameFiles = verifiedFiles };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CleanupPreparationResult(backup, false, true);
            }
        }, cancellationToken);

    public Task<CleanupPreparationResult> ResumeAsync(string gameRoot, string transactionId, Action<string> progress, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            string game = Normalize(gameRoot);
            RequireGame(game);
            UndoJournal journal = Journal();
            CleanupPreparationPending pending = Pending(journal, game, GameBackup(game)).SingleOrDefault(value => value.Id == transactionId)
                ?? throw new CleanupAuthorizationException("The selected preparation transaction is not pending for this game or local settings folder.");
            using BackupLease lease = new(pending.BackupRoot);
            if (CleanupPlanner.PathEquals(pending.GameRoot, game)) RequireNoDeployment(game);
            UndoJournalEntry reservation = journal.Entries.Single(value => value.QuarantineId == transactionId);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(reservation.WalPath) && !File.Exists(reservation.ManifestPath))
            {
                if (!journal.AbandonUnstarted(transactionId, reservation.PlanSha256))
                    throw new CleanupManifestException("This preparation has no staged recovery journal and contains unknown transaction data. Keep its backup folder and inspect it before continuing.");
                const string message = "The interrupted preparation had not staged any files. Its reservation was released. Review the selected cleanup again before preparing.";
                progress(message);
                return new CleanupPreparationResult(pending.BackupRoot, false, false, message);
            }
            RequireClosedWriters();
            if (transactionId.StartsWith("game-indexed-", StringComparison.Ordinal))
            {
                if (!transactionId.StartsWith("game-indexed-" + VanillaGameIndex.LoadEmbedded().BuildId + "-", StringComparison.Ordinal))
                    throw new CleanupManifestException("The interrupted cleanup uses a different game-file reference. Keep its backup and use the matching setup version to continue it.");
                RequireClosedSteam();
                RequireMatchingReference(game);
            }
            QuarantineRecoveryState recovery = QuarantineTransaction.ReadRecovery(reservation.QuarantineRoot, transactionId);
            string evidencePath = recovery.HasCommittedManifest ? reservation.ManifestPath : reservation.WalPath;
            QuarantineManifest manifest = ContractJson.Deserialize<QuarantineManifest>(File.ReadAllBytes(evidencePath));
            using SecureCacheRoot heldSource = new(pending.GameRoot, new WindowsSecureFileApi(), false);
            ValidateRecoveryRoots(manifest);
            try
            {
                progress("Continuing preparation " + transactionId + ". Backup: " + pending.BackupRoot);
                QuarantineResult result = await new QuarantineTransaction().RecoverRecordedAsync(journal, transactionId, reservation.PlanSha256, cancellationToken).ConfigureAwait(false);
                return result.State == QuarantineTransactionState.Committed
                    ? new CleanupPreparationResult(pending.BackupRoot, true, false)
                    : Failed(result, pending.BackupRoot, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new CleanupPreparationResult(pending.BackupRoot, false, true);
            }
        }, cancellationToken);

    private CleanupPreparationPreview Preview(string gameRoot, CancellationToken cancellationToken)
    {
        string game = Normalize(gameRoot);
        RequireGame(game);
        string gameBackup = GameBackup(game);
        string journalPath = Path.Combine(_stateRoot, "preparation-journal.json");
        CleanupPreparationPending[] pending = File.Exists(journalPath) ? Pending(Journal(), game, gameBackup) : [];
        List<CleanupPreparationItem> items = [];
        foreach (CleanupPolicyTarget target in CleanupPolicy.ForPreparation(_corpus).Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target.Expansion == CleanupTargetExpansion.Exact)
            {
                AddPreview(items, game, target.RelativePath, "Game", target.Reason);
                continue;
            }
            string relative = Path.GetDirectoryName(target.RelativePath)!;
            string directory = CleanupPlanner.ResolveUnder(game, relative);
            CleanupEntryStatus status = PathStatus(directory);
            if (status == CleanupEntryStatus.Missing) continue;
            if (status != CleanupEntryStatus.Available)
            {
                items.Add(new("Game", relative, target.Reason, status));
                continue;
            }
            try
            {
                foreach (string path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(path);
                    bool isDirectory = attributes.HasFlag(FileAttributes.Directory);
                    string name = Path.GetFileName(path);
                    if (target.Expansion == CleanupTargetExpansion.Redmods && !isDirectory) continue;
                    if (target.Expansion == CleanupTargetExpansion.ConfigOverrides && (isDirectory
                        || name.Equals("rendering.ini", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("platformgameplay.ini", StringComparison.OrdinalIgnoreCase))) continue;
                    AddPreview(items, game, Path.GetRelativePath(game, path), "Game", target.Reason);
                }
            }
            catch (UnauthorizedAccessException) { items.Add(new("Game", relative, target.Reason, CleanupEntryStatus.AccessDenied)); }
            catch (IOException) { items.Add(new("Game", relative, target.Reason, CleanupEntryStatus.Unavailable)); }
        }
        foreach (CleanupPolicyTarget target in CleanupPolicy.CyberpunkAppData().Targets)
            AddPreview(items, _appData, target.RelativePath, target.RelativePath == "REDEngine" ? "Diagnostics" : "Settings", target.Reason);
        return new([.. items], pending, gameBackup, _appDataBackup);
    }

    private static void AddPreview(List<CleanupPreparationItem> items, string root, string relative, string scope, string reason)
    {
        CleanupEntryStatus status = PathStatus(CleanupPlanner.ResolveUnder(root, relative));
        if (status != CleanupEntryStatus.Missing) items.Add(new(scope, relative, reason, status));
    }

    private static CleanupEntryStatus PathStatus(string path)
    {
        try
        {
            string? current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return CleanupEntryStatus.ReparsePoint;
                current = Path.GetDirectoryName(current);
            }
            return CleanupEntryStatus.Available;
        }
        catch (FileNotFoundException) { return CleanupEntryStatus.Missing; }
        catch (DirectoryNotFoundException) { return CleanupEntryStatus.Missing; }
        catch (UnauthorizedAccessException) { return CleanupEntryStatus.AccessDenied; }
        catch (IOException) { return CleanupEntryStatus.Unavailable; }
    }

    private UndoJournal Journal() => new(Path.Combine(_stateRoot, "preparation-journal.json"));

    private CleanupPreparationPending[] Pending(UndoJournal journal, string game, string gameBackup)
    {
        List<CleanupPreparationPending> pending = [];
        foreach (UndoJournalEntry entry in journal.Entries.Where(value => value.State is not UndoJournalState.Available and not UndoJournalState.Restored))
        {
            if (!CleanupPlanner.PathEquals(entry.QuarantineRoot, gameBackup) && !CleanupPlanner.PathEquals(entry.QuarantineRoot, _appDataBackup)) continue;
            string target = CleanupPlanner.PathEquals(entry.QuarantineRoot, gameBackup) ? game : _appData;
            string transactionRoot = Path.Combine(entry.QuarantineRoot, entry.QuarantineId);
            if (!CleanupPlanner.PathEquals(entry.ManifestPath, Path.Combine(transactionRoot, "quarantine.json"))
                || !CleanupPlanner.PathEquals(entry.WalPath, Path.Combine(transactionRoot, "transaction.wal.json")))
                throw new CleanupManifestException("The preparation journal does not point to its exact transaction files.");
            if (!File.Exists(entry.WalPath) && !File.Exists(entry.ManifestPath))
            {
                pending.Add(new(entry.QuarantineId, target, entry.QuarantineRoot, "Preparation journal needs attention before any files can move", []));
                continue;
            }
            QuarantineRecoveryState recovery = QuarantineTransaction.ReadRecovery(entry.QuarantineRoot, entry.QuarantineId);
            if (!CleanupPlanner.PathEquals(recovery.GameRoot, target)) throw new CleanupManifestException("A preparation journal belongs to a different physical target.");

            pending.Add(new(entry.QuarantineId, recovery.GameRoot, entry.QuarantineRoot, recovery.State.ToString(), recovery.Entries.Select(value => value.RelativePath).ToArray()));
        }
        return [.. pending];
    }

    private static string[] SelectAvailable(CleanupPlan plan, string[]? requested)
    {
        CleanupTarget[] targets = plan.Targets.Where(value => requested is null || requested.Contains(value.RelativePath, StringComparer.OrdinalIgnoreCase)).ToArray();
        CleanupTarget? unavailable = targets.FirstOrDefault(value => value.Status is not CleanupEntryStatus.Available and not CleanupEntryStatus.Missing);
        if (unavailable is not null) throw new CleanupPlanException("Preparation cannot access " + unavailable.RelativePath + ": " + unavailable.Status + ". Close the game and mod tools, check access, then retry.");
        return targets.Where(value => value.Status == CleanupEntryStatus.Available).Select(value => value.RelativePath).ToArray();
    }

    private static CleanupPreparationResult Failed(QuarantineResult result, string backup, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(backup, false, true);
        string details = string.Join("; ", result.Entries.Where(value => value.Failure != QuarantineFailure.None).Select(value => value.RelativePath + ": " + (value.Reason ?? value.Failure.ToString())));
        throw new CleanupManifestException("Preparation paused. Backups are retained at " + backup + ". Resume the recorded transaction. " + details);
    }

    private static void ValidateRecoveryRoots(QuarantineManifest manifest)
    {
        WindowsCleanupProbe probe = new();
        CleanupRootObservation source = probe.ObserveRoot(manifest.GameRoot, false, CancellationToken.None);
        CleanupRootObservation backup = probe.ObserveRoot(manifest.QuarantineRoot, false, CancellationToken.None);
        if (source.IsReparse || source.HasReparseAncestor || backup.IsReparse || backup.HasReparseAncestor
            || !CleanupPlanner.PathEquals(source.PhysicalPath, manifest.GameRoot) || !CleanupPlanner.PathEquals(backup.PhysicalPath, manifest.QuarantineRoot)
            || source.FileIdentity != manifest.GameRootIdentity || backup.FileIdentity != manifest.QuarantineRootIdentity)
            throw new CleanupManifestException("A preparation root changed since this transaction was recorded. Keep its backup and inspect the selected physical location.");
    }

    private void RequireClosedWriters()
    {
        if (_disposableRoot is not null) return;
        string[] writers = ["Cyberpunk2077", "ModOrganizer", "Vortex", "redMod", "Wabbajack", "wabbajack-cli"];
        foreach (string writer in writers)
        {
            Process[] processes = Process.GetProcessesByName(writer);
            try { if (processes.Length > 0) throw new CleanupPlanException("Close " + writer + " before preparing this installation, then retry."); }
            finally { foreach (Process process in processes) process.Dispose(); }
        }
    }

    private void RequireClosedSteam()
    {
        if (_disposableRoot is not null) return;
        Process[] processes = Process.GetProcessesByName("steam");
        try
        {
            if (processes.Length > 0)
                throw new CleanupPlanException("Close Steam before cleaning the game folder so it cannot update files during preparation. Open Steam again for any required repair afterward.");
        }
        finally { foreach (Process process in processes) process.Dispose(); }
    }

    private static void RequireMatchingReference(string game)
    {
        VanillaProfileMatch match = VanillaGameIndex.LoadEmbedded().MatchSteamManifest(IndexedGameCleanup.ReadSteamManifest(game));
        if (!match.Matched) throw new CleanupPlanException("The Steam build or installed depots changed during preparation. No further game files can move. Check the game again. " + match.Message);
    }

    internal static void RequireNoDeployment(string game)
    {
        using SecureCacheRoot root = new(game, new WindowsSecureFileApi(), false);
        foreach (string path in Directory.EnumerateFiles(game, "vortex.deployment.*json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using SecureFileLease file = root.TryOpenRead(Path.GetFileName(path))
                    ?? throw new IOException("The deployment record changed while it was being checked.");
                if (file.Length > 1024 * 1024) throw new IOException("The deployment record is too large to inspect.");
                byte[] bytes = new byte[(int)file.Length];
                if (RandomAccess.Read(file.Handle, bytes, 0) != bytes.Length) throw new IOException("The deployment record could not be read completely.");
                using JsonDocument document = JsonDocument.Parse(bytes);
                JsonElement value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count(property => property.Name == "files") != 1
                    || !value.TryGetProperty("version", out JsonElement version) || !version.TryGetInt32(out int schema) || schema != 1
                    || !value.TryGetProperty("files", out JsonElement files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() != 0)
                    throw new IOException("The deployment is active or its record is not a supported empty manifest.");
                if (value.TryGetProperty("gameId", out JsonElement gameId) && gameId.GetString() != "cyberpunk2077")
                    throw new IOException("The deployment record names a different game.");
                if (value.TryGetProperty("targetPath", out JsonElement target) && !CleanupPlanner.PathEquals(target.GetString() ?? "", game))
                    throw new IOException("The deployment record names a different target folder.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
            {
                throw new CleanupPlanException("Purge Cyberpunk's deployed mods in Vortex, close Vortex, then check preparation again. The deployment record is kept: " + path + ". " + exception.Message);
            }
        }
    }

    private static void RequireGame(string game)
    {
        if (!File.Exists(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe")))
            throw new CleanupPlanException("Select the installed Cyberpunk 2077 folder before preparation.");
        _ = new WindowsCleanupProbe().ObserveRoot(game, false, CancellationToken.None);
    }

    private string Normalize(string root)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (_disposableRoot is not null && !CleanupPlanner.SameOrChild(path, _disposableRoot))
            throw new CleanupAuthorizationException("The selected game is outside the owned disposable test root.");
        return path;
    }
    private static string GameBackup(string game)
    {
        string sibling = game + ".ChromeBlood-Backups";
        if (!CleanupPlanner.TouchesProtectedSystemRoot(sibling)) return sibling;
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string baseRoot = string.Equals(Path.GetPathRoot(profile), Path.GetPathRoot(game), StringComparison.OrdinalIgnoreCase)
            ? profile : Path.GetPathRoot(game) ?? throw new CleanupPlanException("The selected game has no physical drive root.");
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(game.ToUpperInvariant())))[..16];
        return Path.Combine(baseRoot, "ChromeBlood-Backups", "Games", identity);
    }
    private static string NewId(string scope) => scope + "-" + Guid.NewGuid().ToString("N");

    private sealed class CleanupScanProgress(Action<VanillaScanProgress> report) : IProgress<VanillaScanProgress>
    {
        public void Report(VanillaScanProgress value) => report(value);
    }

    private sealed class BackupLease : IDisposable
    {
        private readonly SecureCacheRoot _root;
        private readonly SafeFileHandle _lock;
        public BackupLease(string path)
        {
            _root = new SecureCacheRoot(path);
            try { _lock = _root.OpenLock("preparation.lock"); }
            catch { _root.Dispose(); throw; }
        }
        public void Dispose() { _lock.Dispose(); _root.Dispose(); }
    }
}
