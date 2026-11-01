using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Parity;
using ChromeBlood.Core.Vanilla;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests.Cleanup;

[TestClass]
public sealed class IndexedGameCleanupTests
{
    [TestMethod]
    public async Task UnreadableSavedJournalKeepsTheGameFindingsAndPreservesTheRecord()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPreparationService service, string game, _) = Service(temporary.Path);
        File.WriteAllText(Path.Combine(game, "extra.dll"), "added file");
        string state = Directory.CreateDirectory(Path.Combine(temporary.Path, "state")).FullName;
        string journal = Path.Combine(state, "preparation-journal.json");
        File.WriteAllText(journal, "{broken journal");
        CleanupPreparationPreview preview = await service.PreviewAsync(game, CancellationToken.None);
        Assert.IsFalse(string.IsNullOrWhiteSpace(preview.InspectionFailure));
        Assert.IsTrue(preview.Items.Any(item => item.RelativePath == "extra.dll"));
        Assert.AreEqual("{broken journal", File.ReadAllText(journal));
        Assert.AreEqual("added file", File.ReadAllText(Path.Combine(game, "extra.dll")));
    }

    [TestMethod]
    public async Task IndexedCleanupPreservesOfficialFilesAndMovesUnknownExtrasWithRecoverableEvidence()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string backup = Path.Combine(temporary.Path, "backup");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(game, "official.bin"), "official");
        File.WriteAllText(Path.Combine(game, "unusual-personal-file.bin"), "personal");
        string extraDirectory = Directory.CreateDirectory(Path.Combine(game, "unusual-folder")).FullName;
        File.WriteAllText(Path.Combine(extraDirectory, "data.txt"), "nested");
        VanillaScanResult scan = Scan([
            new("official.bin", false, VanillaEntryStatus.OfficialVerified),
            new("unusual-personal-file.bin", false, VanillaEntryStatus.Extra),
            new("unusual-folder", true, VanillaEntryStatus.Extra),
            new(@"unusual-folder\data.txt", false, VanillaEntryStatus.Extra)]);
        CleanupPolicy policy = IndexedGameCleanup.CreatePolicy(scan);
        Assert.AreEqual(2, policy.Targets.Length);
        CleanupPlanner planner = CleanupPlanner.ForDisposableTests(temporary.Path);
        CleanupPlan plan = planner.Plan(game, backup, policy);
        AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, policy.Targets.Select(target => target.RelativePath).ToArray(), policy);
        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, "indexed-cleanup",
            new UndoJournal(Path.Combine(temporary.Path, "journal.json")), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.AreEqual("official", File.ReadAllText(Path.Combine(game, "official.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(game, "unusual-personal-file.bin")));
        Assert.IsFalse(Directory.Exists(extraDirectory));
        QuarantineManifestEntry personal = result.Manifest.Entries.Single(entry => entry.RelativePath == "unusual-personal-file.bin");
        Assert.AreEqual("personal", File.ReadAllText(personal.DestinationPath));
        Assert.IsTrue(File.Exists(result.ManifestPath));
    }

    [TestMethod]
    public void IndexedPolicyKeepsManagerRecordsAndChangedOfficialContainers()
    {
        VanillaScanResult scan = Scan([
            new("red4ext", true, VanillaEntryStatus.Extra),
            new(@"red4ext\__folder_managed_by_vortex", false, VanillaEntryStatus.Extra),
            new(@"red4ext\personal.dll", false, VanillaEntryStatus.Extra),
            new("V2077", true, VanillaEntryStatus.Extra),
            new(@"V2077\modlist.txt", false, VanillaEntryStatus.Extra),
            new("vortex.deployment.json", false, VanillaEntryStatus.Extra),
            new("official-file.bin", true, VanillaEntryStatus.Changed),
            new(@"official-file.bin\personal.txt", false, VanillaEntryStatus.Extra)]);
        CleanupPolicy policy = IndexedGameCleanup.CreatePolicy(scan);
        CollectionAssert.AreEqual(new[] { @"red4ext\personal.dll" }, policy.Targets.Select(target => target.RelativePath).ToArray());
    }

    [TestMethod]
    public void IncompleteOrDifferentReferenceCannotAuthorizeIndexedCleanup()
    {
        VanillaScanResult scan = Scan([new("personal.dll", false, VanillaEntryStatus.Extra)]);
        Assert.ThrowsExactly<CleanupPlanException>(() => IndexedGameCleanup.CreatePolicy(scan with { Complete = false }));
        Assert.ThrowsExactly<CleanupPlanException>(() => IndexedGameCleanup.CreatePolicy(scan with { ProfileMatch = new(false, "different build") }));
    }

    [TestMethod]
    public async Task PreparationServiceUsesTheIndexAndBacksUpChangedOfficialContentWithoutRemovingIt()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPreparationService service, string game, _) = Service(temporary.Path);
        string official = Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe");
        string extra = Path.Combine(game, "arbitrary-extra.dat");
        File.WriteAllText(extra, "personal");
        CleanupPreparationResult result = await service.ExecuteAsync(game, true, false, false, _ => { }, CancellationToken.None);
        Assert.IsTrue(result.Completed);
        Assert.IsTrue(result.GameFiles!.ProfileMatch.Matched);
        Assert.IsTrue(result.GameFiles.Complete);
        Assert.IsTrue(result.GameFiles.HashVerificationRequested);
        Assert.IsFalse(File.Exists(extra));
        Assert.AreEqual("modified executable", File.ReadAllText(official));
        string retained = Directory.GetFiles(result.BackupRoot, "Cyberpunk2077.exe", SearchOption.AllDirectories).Single();
        Assert.AreEqual("modified executable", File.ReadAllText(retained));
        Assert.IsTrue(Directory.GetDirectories(result.BackupRoot, "game-indexed-26161058-*").Length == 1);
    }

    [TestMethod]
    public async Task AStorefrontBuildChangeDuringVerificationStopsBeforeAnyGameFileMoves()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPreparationService service, string game, string manifest) = Service(temporary.Path);
        string extra = Path.Combine(game, "arbitrary-extra.dat");
        File.WriteAllText(extra, "personal");
        await Assert.ThrowsExactlyAsync<CleanupPlanException>(() => service.ExecuteAsync(game, true, false, false,
            message =>
            {
                if (message.StartsWith("Verifying official game files:", StringComparison.Ordinal))
                    File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("26161058", "different-build", StringComparison.Ordinal));
            }, CancellationToken.None));
        Assert.AreEqual("personal", File.ReadAllText(extra));
        Assert.AreEqual("modified executable", File.ReadAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe")));
        Assert.IsFalse(Directory.EnumerateFiles(temporary.Path, "quarantine.json", SearchOption.AllDirectories).Any());
    }

    private static (CleanupPreparationService Service, string Game, string Manifest) Service(string root)
    {
        string game = Path.Combine(root, "steamapps", "common", "Cyberpunk 2077");
        Directory.CreateDirectory(Path.Combine(game, "bin", "x64"));
        File.WriteAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe"), "modified executable");
        string manifest = Path.Combine(root, "steamapps", "appmanifest_1091500.acf");
        File.WriteAllText(manifest, """
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
        string appData = Directory.CreateDirectory(Path.Combine(root, "appdata")).FullName;
        string appBackup = Directory.CreateDirectory(Path.Combine(root, "appdata-backup")).FullName;
        CleanupPreparationService service = CleanupPreparationService.ForDisposableTests(root, LegacyParityCorpus.LoadEmbedded(),
            Path.Combine(root, "state"), appData, appBackup);
        return (service, game, manifest);
    }

    private static VanillaScanResult Scan(VanillaScanEntry[] entries) => new(new(true, "matching reference"),
        "reference-identity", entries, true, true);
}
