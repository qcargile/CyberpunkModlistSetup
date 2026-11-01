using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Parity;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests.Cleanup;

[TestClass]
public sealed class BackupLocationTests
{
    [TestMethod]
    public void CompiledKeepFilterIncludesAncestorsAndDescendantsWithoutMatchingNeighborNames()
    {
        Func<string, bool> keep = CleanupPolicy.CreateKeepFilter([@"BIN\x64\dlss.dll", @"archive\pc\mod", "root.dll"]);
        foreach (string path in new[] { "bin", "bin/x64", "bin/x64/dlss.dll", "ARCHIVE/PC/MOD", "archive/pc/mod/nested/file.archive", "root.dll" })
            Assert.IsTrue(keep(path), path);
        foreach (string path in new[] { "bin/x64/dlss.dll.extra", "bin/x64/other.dll", "archive/pc/modded/file.archive", "archive/pc/other", "root.dll.extra" })
            Assert.IsFalse(keep(path), path);
        Assert.IsFalse(CleanupPolicy.CreateKeepFilter(null)("any.dll"));
    }

    [TestMethod]
    public async Task PreviewUsesChosenLocationsWithoutCreatingThem()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string gameBackup = Path.Combine(temporary.Path, "chosen-game-backup");
        string appBackup = Path.Combine(temporary.Path, "chosen-settings-backup");
        CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None, gameBackup, appBackup);
        Assert.AreEqual(gameBackup, preview.GameBackupRoot);
        Assert.AreEqual(appBackup, preview.AppDataBackupRoot);
        Assert.IsFalse(Directory.Exists(gameBackup));
        Assert.IsFalse(Directory.Exists(appBackup));
    }

    [TestMethod]
    public async Task GameCleanupPreservesFilesInTheChosenLocation()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        File.WriteAllText(source, "mod loader");
        string backup = Path.Combine(temporary.Path, "chosen-backup");
        CleanupPreparationResult result = await service.ExecuteAsync(game, true, false, false, _ => { }, CancellationToken.None,
            gameBackupRoot: backup);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(backup, result.BackupRoot);
        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("mod loader", File.ReadAllText(Directory.GetFiles(backup, "winmm.dll", SearchOption.AllDirectories).Single()));
        Assert.IsFalse(Directory.Exists(game + ".ChromeBlood-Backups"));
    }

    [TestMethod]
    public async Task SettingsCleanupUsesItsSeparateChosenLocation()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, appData) = Service(temporary.Path);
        string settings = Directory.CreateDirectory(Path.Combine(appData, "CD Projekt Red", "Cyberpunk 2077")).FullName;
        File.WriteAllText(Path.Combine(settings, "UserSettings.json"), "settings");
        string backup = Path.Combine(temporary.Path, "chosen-settings");
        CleanupPreparationResult result = await service.ExecuteAsync(game, false, true, false, _ => { }, CancellationToken.None,
            appDataBackupRoot: backup);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(backup, result.BackupRoot);
        Assert.IsFalse(Directory.Exists(settings));
        Assert.AreEqual("settings", File.ReadAllText(Directory.GetFiles(backup, "UserSettings.json", SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public async Task ChangingBackupChoicesDoesNotLoseGameOrSettingsRecovery()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, appData) = Service(temporary.Path);
        string gameBackup = Directory.CreateDirectory(Path.Combine(temporary.Path, "old-game-backup")).FullName;
        string appBackup = Directory.CreateDirectory(Path.Combine(temporary.Path, "old-settings-backup")).FullName;
        await Interrupt(temporary.Path, game, gameBackup, "game-old");
        await Interrupt(temporary.Path, appData, appBackup, "appdata-old");
        CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None,
            Path.Combine(temporary.Path, "new-game-backup"), Path.Combine(temporary.Path, "new-settings-backup"));
        Assert.HasCount(2, preview.Pending);
        Assert.AreEqual(gameBackup, preview.Pending.Single(value => value.Id == "game-old").BackupRoot);
        Assert.AreEqual(appData, preview.Pending.Single(value => value.Id == "appdata-old").GameRoot);
        foreach (CleanupPreparationPending pending in preview.Pending)
        {
            CleanupPreparationResult result = await service.ResumeAsync(game, pending.Id, _ => { }, CancellationToken.None);
            Assert.IsTrue(result.Completed);
            Assert.AreEqual(pending.BackupRoot, result.BackupRoot);
            Assert.AreEqual("preserved", File.ReadAllText(Directory.GetFiles(pending.BackupRoot, "pending.dll", SearchOption.AllDirectories).Single()));
        }
        Assert.IsEmpty((await service.PreviewAsync(game, CancellationToken.None)).Pending);
    }

    [TestMethod]
    public async Task InvalidFutureBackupChoiceKeepsOriginalRecoveryAvailable()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string backup = Directory.CreateDirectory(Path.Combine(temporary.Path, "original-backup")).FullName;
        await Interrupt(temporary.Path, game, backup, "game-original");
        string invalid = Path.Combine(game, "future-backup");
        CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None, invalid);
        Assert.HasCount(1, preview.Pending);
        Assert.AreEqual(backup, preview.Pending[0].BackupRoot);
        Assert.IsFalse(string.IsNullOrWhiteSpace(preview.InspectionFailure));
        CleanupPreparationResult result = await service.ResumeAsync(game, preview.Pending[0].Id, _ => { }, CancellationToken.None);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual(backup, result.BackupRoot);
        Assert.IsFalse(Directory.Exists(invalid));
        Assert.AreEqual("preserved", File.ReadAllText(Directory.GetFiles(backup, "pending.dll", SearchOption.AllDirectories).Single()));
    }

    [TestMethod]
    public async Task SourceUnknownReservationCanOnlyReleaseItsProvenEmptyRecord()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string backup = Directory.CreateDirectory(Path.Combine(temporary.Path, "old-backup")).FullName;
        string transaction = Path.Combine(backup, "game-unstarted");
        UndoJournal journal = new(Path.Combine(temporary.Path, "state", "preparation-journal.json"));
        journal.Reserve("game-unstarted", new string('a', 64), backup, Path.Combine(transaction, "transaction.wal.json"), Path.Combine(transaction, "quarantine.json"));
        CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None, Path.Combine(temporary.Path, "new-backup"));
        Assert.HasCount(1, preview.Pending);
        Assert.AreEqual("", preview.Pending[0].GameRoot);
        CleanupPreparationResult result = await service.ResumeAsync(game, "game-unstarted", _ => { }, CancellationToken.None);
        Assert.IsFalse(result.Completed);
        Assert.IsEmpty(journal.Entries);
        Assert.IsTrue(File.Exists(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe")));
    }

    [TestMethod]
    public async Task SourceUnknownReservationWithUnrecognizedDataIsRetained()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string backup = Directory.CreateDirectory(Path.Combine(temporary.Path, "old-backup")).FullName;
        string transaction = Directory.CreateDirectory(Path.Combine(backup, "game-unstarted")).FullName;
        File.WriteAllText(Path.Combine(transaction, "unknown.txt"), "retain this");
        UndoJournal journal = new(Path.Combine(temporary.Path, "state", "preparation-journal.json"));
        journal.Reserve("game-unstarted", new string('a', 64), backup, Path.Combine(transaction, "transaction.wal.json"), Path.Combine(transaction, "quarantine.json"));
        await Assert.ThrowsExactlyAsync<CleanupManifestException>(() => service.ResumeAsync(game, "game-unstarted", _ => { }, CancellationToken.None));
        Assert.HasCount(1, journal.Entries);
        Assert.AreEqual("retain this", File.ReadAllText(Path.Combine(transaction, "unknown.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(backup, "preparation.lock")));
    }

    [TestMethod]
    public async Task UnsafeBackupLocationsAreRejectedWithoutMovingFiles()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, appData) = Service(temporary.Path);
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        File.WriteAllText(source, "keep source");
        string occupied = Path.Combine(temporary.Path, "occupied");
        File.WriteAllText(occupied, "keep destination");
        foreach (string backup in new[] { game, Path.Combine(game, "backup"), temporary.Path, Path.Combine(appData, "backup"), occupied, "relative-backup" })
        {
            CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None, backup);
            Assert.IsFalse(string.IsNullOrWhiteSpace(preview.InspectionFailure));
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(game, true, false, false, _ => { }, CancellationToken.None, gameBackupRoot: backup));
            Assert.AreEqual("keep source", File.ReadAllText(source));
            Assert.AreEqual("keep destination", File.ReadAllText(occupied));
        }
    }

    [TestMethod]
    public async Task RecoveryRefusesAReplacedOriginalSettingsRoot()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, appData) = Service(temporary.Path);
        string backup = Directory.CreateDirectory(Path.Combine(temporary.Path, "old-backup")).FullName;
        await Interrupt(temporary.Path, appData, backup, "appdata-old");
        Directory.Move(appData, appData + ".retained");
        Directory.CreateDirectory(appData);
        File.WriteAllText(Path.Combine(appData, "pending.dll"), "replacement");
        await Assert.ThrowsExactlyAsync<CleanupManifestException>(() => service.ResumeAsync(game, "appdata-old", _ => { }, CancellationToken.None));
        Assert.AreEqual("replacement", File.ReadAllText(Path.Combine(appData, "pending.dll")));
        Assert.AreEqual("preserved", File.ReadAllText(Path.Combine(appData + ".retained", "pending.dll")));
    }

    [TestMethod]
    public async Task KeptFilesProtectOverlappingFoldersWhileOtherSelectedModsAreCleaned()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string archives = Directory.CreateDirectory(Path.Combine(game, "archive", "pc", "mod")).FullName;
        File.WriteAllText(Path.Combine(archives, "keep.archive"), "kept archive");
        File.WriteAllText(Path.Combine(archives, "neighbor.archive"), "grouped archive");
        File.WriteAllText(Path.Combine(game, "bin", "x64", "dxgi.dll"), "intentional replacement");
        File.WriteAllText(Path.Combine(game, "bin", "x64", "winmm.dll"), "remove loader");
        string backup = Path.Combine(temporary.Path, "backup");
        CleanupPreparationResult result = await service.ExecuteAsync(game, true, false, false, _ => { }, CancellationToken.None,
            gameBackupRoot: backup, keptGamePaths: [@"archive\pc\mod\keep.archive", @"BIN/X64/DXGI.DLL"]);
        Assert.IsTrue(result.Completed);
        Assert.AreEqual("kept archive", File.ReadAllText(Path.Combine(archives, "keep.archive")));
        Assert.AreEqual("grouped archive", File.ReadAllText(Path.Combine(archives, "neighbor.archive")));
        Assert.AreEqual("intentional replacement", File.ReadAllText(Path.Combine(game, "bin", "x64", "dxgi.dll")));
        Assert.IsFalse(File.Exists(Path.Combine(game, "bin", "x64", "winmm.dll")));
        Assert.IsEmpty(Directory.GetFiles(backup, "dxgi.dll", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task KeptChangedOfficialFileIsNotIncludedInBackupCopies()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        File.WriteAllText(Path.Combine(temporary.Path, "steamapps", "appmanifest_1091500.acf"), """
            "AppState" {
                "appid" "1091500" "installdir" "Cyberpunk 2077" "StateFlags" "4" "buildid" "26161058"
                "UserConfig" { "language" "english" "BetaKey" "public" }
                "InstalledDepots" {
                    "1091501" { "manifest" "3524634124112875626" }
                    "2060310" { "manifest" "4793018690565322422" }
                    "2138330" { "manifest" "5565496786254603194" }
                }
            }
            """);
        File.WriteAllText(Path.Combine(game, "extra.dat"), "remove extra");
        string backup = Path.Combine(temporary.Path, "backup");
        CleanupPreparationResult result = await service.ExecuteAsync(game, true, false, false, _ => { }, CancellationToken.None,
            gameBackupRoot: backup, keptGamePaths: [@"bin\x64\Cyberpunk2077.exe"]);
        Assert.IsTrue(result.Completed);
        Assert.IsTrue(result.GameFiles!.ProfileMatch.Matched);
        Assert.AreEqual("game executable", File.ReadAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe")));
        Assert.IsEmpty(Directory.GetFiles(backup, "Cyberpunk2077.exe", SearchOption.AllDirectories));
        Assert.IsFalse(File.Exists(Path.Combine(game, "extra.dat")));
    }

    [TestMethod]
    public async Task RedirectedBackupFolderIsRejectedAndItsTargetIsUntouched()
    {
        using TemporaryDirectory temporary = new();
        var (service, game, _) = Service(temporary.Path);
        string target = Directory.CreateDirectory(Path.Combine(temporary.Path, "target")).FullName;
        File.WriteAllText(Path.Combine(target, "existing.txt"), "retain target");
        string link = Path.Combine(temporary.Path, "linked-backup");
        System.Diagnostics.ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using System.Diagnostics.Process process = System.Diagnostics.Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode);
        try
        {
            CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None, link);
            Assert.IsFalse(string.IsNullOrWhiteSpace(preview.InspectionFailure));
            await Assert.ThrowsExactlyAsync<CleanupPlanException>(() => service.ExecuteAsync(game, true, false, false, _ => { }, CancellationToken.None, gameBackupRoot: link));
            Assert.AreEqual("retain target", File.ReadAllText(Path.Combine(target, "existing.txt")));
            Assert.IsFalse(File.Exists(Path.Combine(target, "preparation.lock")));
        }
        finally { Directory.Delete(link); }
    }

    private static async Task Interrupt(string disposableRoot, string source, string backup, string id)
    {
        File.WriteAllText(Path.Combine(source, "pending.dll"), "preserved");
        CleanupPolicy policy = CleanupPolicy.FromIndexedTargets("disposable", [new("pending.dll", false, "test file", CleanupTargetExpansion.Exact)]);
        CleanupPlanner planner = CleanupPlanner.ForDisposableTests(disposableRoot);
        CleanupPlan plan = planner.Plan(source, backup, policy);
        AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, ["pending.dll"], policy);
        QuarantineResult result = await new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "entry:moving" })
            .ExecuteRecordedAsync(authorized, id, new UndoJournal(Path.Combine(disposableRoot, "state", "preparation-journal.json")), CancellationToken.None);
        Assert.AreNotEqual(QuarantineTransactionState.Committed, result.State);
        Assert.IsTrue(File.Exists(Path.Combine(backup, id, "transaction.wal.json")));
    }

    private static (CleanupPreparationService Service, string Game, string AppData) Service(string root)
    {
        string game = Path.Combine(root, "steamapps", "common", "Cyberpunk 2077");
        Directory.CreateDirectory(Path.Combine(game, "bin", "x64"));
        File.WriteAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe"), "game executable");
        string appData = Directory.CreateDirectory(Path.Combine(root, "appdata")).FullName;
        string appBackup = Directory.CreateDirectory(Path.Combine(root, "appdata-backup")).FullName;
        return (CleanupPreparationService.ForDisposableTests(root, LegacyParityCorpus.LoadEmbedded(), Path.Combine(root, "state"), appData, appBackup), game, appData);
    }
}
