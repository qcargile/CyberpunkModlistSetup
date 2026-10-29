using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Parity;
using ChromeBlood.Testing;
using ChromeBlood.Contracts.Serialization;
using System.Text;
using System.Text.Json.Nodes;

namespace ChromeBlood.Setup.Tests.Cleanup;

[TestClass]
public sealed class UndoJournalTests
{
    [TestMethod]
    [DataRow("entries")]
    [DataRow("entry")]
    [DataRow("quarantineId")]
    public void Journal_RejectsExplicitNullRequiredFieldsWithOwnedError(string field)
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "undo.json");
        UndoJournal journal = new(path);
        journal.Record(new QuarantineUndoReference("q-null", new string('a', 64), Path.Combine(temporary.Path, "vault")));
        JsonNode document = JsonNode.Parse(File.ReadAllBytes(path))!;
        if (field == "entries") document[field] = null;
        else if (field == "entry") document["entries"]![0] = null;
        else document["entries"]![0]![field] = null;
        string corrupt = ContractJson.Serialize(document);
        File.WriteAllText(path, corrupt);
        UndoJournalException error = Assert.ThrowsExactly<UndoJournalException>(() => _ = journal.Entries);
        StringAssert.Contains(error.Message, field == "entry" ? "null entry" : "corrupt");
        Assert.AreEqual(corrupt, File.ReadAllText(path));
    }

    [TestMethod]
    public void ContractJson_RejectsRequiredNullAndPreservesOptionalNullForBothReaders()
    {
        const string invalid = "{\"quarantineId\":null,\"manifestSha256\":\"hash\",\"quarantineRoot\":\"root\"}";
        Assert.ThrowsExactly<ContractFormatException>(() => ContractJson.Deserialize<QuarantineUndoReference>(invalid));
        Assert.ThrowsExactly<ContractFormatException>(() => ContractJson.Deserialize<QuarantineUndoReference>(Encoding.UTF8.GetBytes(invalid)));
        QuarantineRestoreEntry entry = new("file", QuarantineRestoreEntryState.Restored, null);
        Assert.AreEqual(entry, ContractJson.Deserialize<QuarantineRestoreEntry>(ContractJson.Serialize(entry)));
        Assert.AreEqual(entry, ContractJson.Deserialize<QuarantineRestoreEntry>(ContractJson.SerializeUtf8(entry)));
    }

    [TestMethod]
    public void AbandonUnstarted_RemovesOnlyAReservationWithoutTransactionFiles()
    {
        using TemporaryDirectory temporary = new();
        string vault = Path.Combine(temporary.Path, "vault");
        Directory.CreateDirectory(vault);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo.json"));
        string hash = new('a', 64);
        string untouched = Path.Combine(vault, "q-unstarted");
        journal.Reserve("q-unstarted", hash, vault, Path.Combine(untouched, "transaction.wal.json"), Path.Combine(untouched, "quarantine.json"));

        Assert.IsTrue(journal.AbandonUnstarted("q-unstarted", hash));
        Assert.AreEqual(0, journal.Entries.Count);

        string present = Path.Combine(vault, "q-present");
        journal.Reserve("q-present", hash, vault, Path.Combine(present, "transaction.wal.json"), Path.Combine(present, "quarantine.json"));
        Directory.CreateDirectory(present);
        File.WriteAllText(Path.Combine(present, "unknown.txt"), "preserve");
        Assert.IsFalse(journal.AbandonUnstarted("q-present", hash));
        Assert.AreEqual(1, journal.Entries.Count);
    }

    [TestMethod]
    public void Journal_BindsExactIdAndManifestHashAndRejectsReplayOrCorruption()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "undo.json");
        UndoJournal journal = new(path, 4);
        QuarantineUndoReference first = new("q-1", new string('a', 64), Path.Combine(temporary.Path, "vault"));
        QuarantineUndoReference second = new("q-2", new string('b', 64), Path.Combine(temporary.Path, "vault"));

        journal.Record(first);
        journal.Record(second);
        Assert.AreEqual(first, journal.Require("q-1", first.ManifestSha256));
        Assert.AreEqual(second, journal.Require("q-2", second.ManifestSha256));
        Assert.ThrowsExactly<UndoJournalException>(() => journal.Require("q-2", first.ManifestSha256));
        Assert.ThrowsExactly<UndoJournalException>(() => journal.Record(first));

        File.WriteAllText(path, "{}");
        Assert.ThrowsExactly<UndoJournalException>(() => journal.Require("q-1", first.ManifestSha256));
    }

    [TestMethod]
    public void Journal_UpdatesStateAtomicallyAndEnforcesCountBound()
    {
        using TemporaryDirectory temporary = new();
        string path = Path.Combine(temporary.Path, "undo.json");
        UndoJournal journal = new(path, 2);
        QuarantineUndoReference first = new("q-1", new string('a', 64), Path.Combine(temporary.Path, "vault"));
        journal.Record(first);
        journal.Update("q-1", first.ManifestSha256, UndoJournalState.Restored);
        Assert.AreEqual(UndoJournalState.Restored, journal.Entries.Single().State);
        Assert.ThrowsExactly<UndoJournalException>(() => journal.Update("q-1", new string('b', 64), UndoJournalState.Restored));
        journal.Record(new QuarantineUndoReference("q-2", new string('b', 64), Path.Combine(temporary.Path, "vault")));
        Assert.ThrowsExactly<UndoJournalException>(() => journal.Record(new QuarantineUndoReference("q-3", new string('c', 64), Path.Combine(temporary.Path, "vault"))));
        Assert.IsFalse(Directory.EnumerateFiles(temporary.Path, "*.tmp", SearchOption.TopDirectoryOnly).Any());
    }

    [TestMethod]
    public async Task RecordedRestore_RequiresTheJournalReferenceAndMakesRepeatedUndoIdempotent()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string vault = Path.Combine(temporary.Path, "vault");
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(vault);
        File.WriteAllText(source, "payload");
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path).Authorize(game, vault, corpus);
        CleanupPlan plan = authorized.Plan;
        QuarantineTransaction transaction = new();
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo.json"));
        QuarantineResult stored = await transaction.ExecuteRecordedAsync(authorized, "q-journal", journal, CancellationToken.None);
        string hash = QuarantineManifestCodec.Sha256(stored.ManifestPath);

        Assert.ThrowsExactly<UndoJournalException>(() => transaction.RestoreRecordedAsync(journal, "q-journal", new string('0', 64), game, CancellationToken.None).GetAwaiter().GetResult());
        QuarantineRestoreResult restored = await transaction.RestoreRecordedAsync(journal, "q-journal", hash, game, CancellationToken.None);
        Assert.AreEqual(QuarantineRestoreEntryState.Restored, restored.Entries.Single().State);
        Assert.AreEqual(UndoJournalState.Restored, journal.Entries.Single().State);
        QuarantineRestoreResult repeated = await transaction.RestoreRecordedAsync(journal, "q-journal", hash, game, CancellationToken.None);
        Assert.AreEqual(QuarantineRestoreEntryState.AlreadyRestored, repeated.Entries.Single().State);
        Assert.AreEqual(UndoJournalState.Restored, journal.Entries.Single().State);
    }

    [TestMethod]
    public async Task RecordedExecute_WritesTheExactCommittedManifestReferenceToTheJournal()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-recorded");
        string vault = Path.Combine(temporary.Path, "vault-recorded");
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(vault);
        File.WriteAllText(source, "payload");
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path).Authorize(game, vault, corpus);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo-recorded.json"));

        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, "q-recorded", journal, CancellationToken.None);

        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        UndoJournalEntry recorded = journal.Entries.Single();
        Assert.AreEqual("q-recorded", recorded.QuarantineId);
        Assert.AreEqual(QuarantineManifestCodec.Sha256(result.ManifestPath), recorded.ManifestSha256);
        Assert.AreEqual(UndoJournalState.Available, recorded.State);
        journal.UpdateReservation("q-recorded", authorized.PlanSha256, UndoJournalState.Executing);
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(journal, "q-recorded", authorized.PlanSha256, CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.AreEqual(UndoJournalState.Available, journal.Entries.Single().State);
    }

    [TestMethod]
    public async Task Execute_RejectsExpiredFabricatedOrJournalFullAuthorizationBeforeMutation()
    {
        using TemporaryDirectory temporary = new();
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        string game = Path.Combine(temporary.Path, "game-authority");
        string vault = Path.Combine(temporary.Path, "vault-authority");
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(vault);
        File.WriteAllText(source, "payload");
        DateTimeOffset now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path, () => now).Authorize(game, vault, corpus);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo-authority.json"), 1);
        journal.Record(new QuarantineUndoReference("occupied", new string('a', 64), vault));

        Assert.ThrowsExactly<UndoJournalException>(() => new QuarantineTransaction(new NoCleanupTransactionFaults(), () => now).ExecuteRecordedAsync(authorized, "q-full", journal, CancellationToken.None).GetAwaiter().GetResult());
        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(Directory.Exists(Path.Combine(vault, "q-full")));

        UndoJournal available = new(Path.Combine(temporary.Path, "undo-available.json"));
        Assert.ThrowsExactly<CleanupAuthorizationException>(() => new QuarantineTransaction(new NoCleanupTransactionFaults(), () => now.AddMinutes(11)).ExecuteRecordedAsync(authorized, "q-expired", available, CancellationToken.None).GetAwaiter().GetResult());
        Assert.IsTrue(File.Exists(source));
    }

    [TestMethod]
    public async Task Execute_ConcurrentDuplicateIdHasOneReservationWinnerAndOneMutation()
    {
        using TemporaryDirectory temporary = new();
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        string game = Path.Combine(temporary.Path, "game-concurrent");
        string vault = Path.Combine(temporary.Path, "vault-concurrent");
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(vault);
        File.WriteAllText(source, "payload");
        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path).Authorize(game, vault, corpus);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo-concurrent.json"));

        Task<object> First() => Task.Run<object>(async () =>
        {
            try { return await new QuarantineTransaction().ExecuteRecordedAsync(authorized, "q-concurrent", journal, CancellationToken.None); }
            catch (Exception exception) { return exception; }
        });
        object[] outcomes = await Task.WhenAll(First(), First());

        Assert.AreEqual(1, outcomes.Count(value => value is QuarantineResult { State: QuarantineTransactionState.Committed }));
        Assert.AreEqual(1, outcomes.Count(value => value is UndoJournalException or IOException));
        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual(1, journal.Entries.Count(value => value.QuarantineId == "q-concurrent"));
    }

    [TestMethod]
    public async Task Execute_CancelBeforeStagingLeavesReleasablePlannedReservation()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPreparationService service, UndoJournal journal, string game, string vault) = ResumeFixture(temporary, false);
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        File.WriteAllText(source, "unchanged");
        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path).Authorize(game, vault, LegacyParityCorpus.LoadEmbedded());
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new QuarantineTransaction().ExecuteRecordedAsync(authorized, "cancel-before-stage", journal, cancellation.Token));
        Assert.AreEqual(UndoJournalState.Planned, journal.Entries.Single().State);
        Assert.IsFalse(Directory.Exists(Path.Combine(vault, "cancel-before-stage")));
        CleanupPreparationResult released = await service.ResumeAsync(game, "cancel-before-stage", _ => { }, CancellationToken.None);
        Assert.IsFalse(released.Completed);
        StringAssert.Contains(released.Message!, "Review");
        Assert.AreEqual(0, journal.Entries.Count);
        Assert.AreEqual("unchanged", File.ReadAllText(source));
    }

    [TestMethod]
    public async Task Execute_MarksExecutingOnlyAfterDurableWalExists()
    {
        using TemporaryDirectory temporary = new();
        (_, UndoJournal journal, string game, string vault) = ResumeFixture(temporary, false);
        File.WriteAllText(Path.Combine(game, "bin", "x64", "winmm.dll"), "payload");
        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path).Authorize(game, vault, LegacyParityCorpus.LoadEmbedded());
        bool observed = false;
        RecordingCleanupFaults faults = new() { OnBoundary = name =>
        {
            if (name != "wal:staged") return;
            UndoJournalEntry entry = journal.Entries.Single();
            Assert.AreEqual(UndoJournalState.Executing, entry.State);
            Assert.IsTrue(File.Exists(entry.WalPath));
            Assert.AreEqual(QuarantineTransactionState.Staged, QuarantineTransaction.ReadRecovery(vault, entry.QuarantineId).State);
            observed = true;
        } };
        QuarantineResult result = await new QuarantineTransaction(faults).ExecuteRecordedAsync(authorized, "staged-order", journal, CancellationToken.None);
        Assert.IsTrue(observed);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
    }

    [TestMethod]
    [DataRow(false, UndoJournalState.Planned)]
    [DataRow(false, UndoJournalState.Executing)]
    [DataRow(true, UndoJournalState.Planned)]
    [DataRow(true, UndoJournalState.Executing)]
    public async Task Resume_UnstartedGameOrAppDataReservationRequiresFreshReview(bool appData, UndoJournalState state)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPreparationService service, UndoJournal journal, string game, string vault) = ResumeFixture(temporary, appData);
        string id = "unstarted-" + state;
        string hash = new('b', 64);
        string transaction = Path.Combine(vault, id);
        journal.Reserve(id, hash, vault, Path.Combine(transaction, "transaction.wal.json"), Path.Combine(transaction, "quarantine.json"));
        if (state == UndoJournalState.Executing) journal.UpdateReservation(id, hash, state);
        Directory.CreateDirectory(Path.Combine(transaction, "files"));
        CleanupPreparationResult result = await service.ResumeAsync(game, id, _ => { }, CancellationToken.None);
        Assert.IsFalse(result.Completed);
        Assert.IsFalse(result.Cancelled);
        StringAssert.Contains(result.Message!, "Review");
        Assert.AreEqual(0, journal.Entries.Count);
        Assert.AreEqual(0, (await service.PreviewAsync(game, CancellationToken.None)).Pending.Length);
        Assert.IsTrue(Directory.Exists(Path.Combine(transaction, "files")));
    }

    [TestMethod]
    [DataRow(false, UndoJournalState.Planned)]
    [DataRow(false, UndoJournalState.Executing)]
    [DataRow(true, UndoJournalState.Planned)]
    [DataRow(true, UndoJournalState.Executing)]
    public async Task Resume_UnstagedTransactionWithUnknownDataRemainsBlocked(bool appData, UndoJournalState state)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPreparationService service, UndoJournal journal, string game, string vault) = ResumeFixture(temporary, appData);
        string id = "unknown-" + state;
        string hash = new('c', 64);
        string transaction = Path.Combine(vault, id);
        Directory.CreateDirectory(Path.Combine(transaction, "files"));
        string personal = Path.Combine(transaction, "files", "personal.txt");
        File.WriteAllText(personal, "preserve");
        journal.Reserve(id, hash, vault, Path.Combine(transaction, "transaction.wal.json"), Path.Combine(transaction, "quarantine.json"));
        if (state == UndoJournalState.Executing) journal.UpdateReservation(id, hash, state);
        await Assert.ThrowsExactlyAsync<CleanupManifestException>(() => service.ResumeAsync(game, id, _ => { }, CancellationToken.None));
        Assert.AreEqual("preserve", File.ReadAllText(personal));
        Assert.AreEqual(1, journal.Entries.Count);
        Assert.AreEqual(state, journal.Entries.Single().State);
    }

    private static (CleanupPreparationService Service, UndoJournal Journal, string Game, string Vault) ResumeFixture(TemporaryDirectory temporary, bool appData)
    {
        string game = Path.Combine(temporary.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "bin", "x64"));
        File.WriteAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe"), "fixture");
        string state = Path.Combine(temporary.Path, "state");
        string local = Path.Combine(temporary.Path, "LocalAppData");
        string localBackup = Path.Combine(temporary.Path, "AppDataBackup");
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(localBackup);
        string vault = appData ? localBackup : game + ".ChromeBlood-Backups";
        Directory.CreateDirectory(vault);
        CleanupPreparationService service = CleanupPreparationService.ForDisposableTests(temporary.Path, LegacyParityCorpus.LoadEmbedded(), state, local, localBackup);
        UndoJournal journal = new(Path.Combine(state, "preparation-journal.json"));
        return (service, journal, game, vault);
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "fixtures", "legacy"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}
