using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Parity;
using ChromeBlood.Testing;
using System.Security.AccessControl;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using ChromeBlood.Contracts.Serialization;

namespace ChromeBlood.Setup.Tests.Cleanup;

[TestClass]
public sealed class QuarantineTransactionTests
{
    private static readonly string[] ExpectedBoundaries = ["wal:staged", "entry:moving", "entry:moved", "entry:stored", "manifest:before-commit", "manifest:committed"];

    [TestMethod]
    public async Task Execute_WritesDurableWalBeforeMutationAndManifestLast()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, string vault) = CreatePlan(temporary, "payload");
        string dacl = new FileInfo(source).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        RecordingCleanupFaults faults = new();

        QuarantineResult result = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-0001");

        string storedPath = Path.Combine(vault, "q-0001", "files", "bin", "x64", "winmm.dll");
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State, $"failure={result.Entries.Single().Failure}; source={File.Exists(source)}; destination={File.Exists(storedPath)}; expected={plan.Entries.Single().Metadata}; actual={(File.Exists(storedPath) ? WindowsCleanupProbe.ReadMetadataForTests(storedPath, false) : null)}");
        Assert.IsFalse(File.Exists(source));
        Assert.IsTrue(File.Exists(Path.Combine(vault, "q-0001", "files", "bin", "x64", "winmm.dll")));
        Assert.AreEqual(dacl, new FileInfo(Path.Combine(vault, "q-0001", "files", "bin", "x64", "winmm.dll")).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(storedPath, CleanupEntryKind.File, plan.Entries.Single().Metadata));
        Assert.IsTrue(File.Exists(result.ManifestPath));
        CollectionAssert.AreEqual(ExpectedBoundaries, faults.Events.ToArray());
        QuarantineManifest manifest = QuarantineManifestCodec.ReadStrict(result.ManifestPath);
        Assert.AreEqual("q-0001", manifest.QuarantineId);
        Assert.AreEqual(QuarantineEntryState.Stored, manifest.Entries.Single().State);
        Assert.AreEqual(plan.GameRootIdentity, manifest.GameRootIdentity);
    }

    [TestMethod]
    public async Task Execute_CrossVolumePartialMutationThroughHeldPublishNeverDeletesOnlyGoodSource()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "payload", "held-partial");
        bool mutationBlocked = false;
        RecordingCleanupFaults faults = new()
        {
            CrossVolume = true,
            OnBoundary = name =>
            {
                if (name != "entry:copied") return;
                string partial = Path.Combine(plan.QuarantineRoot, "q-held", "files", "bin", "x64", "winmm.dll.partial");
                try { File.WriteAllText(partial, "mutated"); }
                catch (IOException) { mutationBlocked = true; }
            }
        };

        QuarantineResult result = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-held");

        Assert.IsTrue(mutationBlocked, result.Entries.Single().Reason);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public async Task Execute_SourceMutationLockAndInterruptedCopyFailClosedWithoutLosingBothCopies()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "payload");
        File.WriteAllText(source, "mutated");
        QuarantineResult mutated = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-mutation");
        Assert.AreEqual(QuarantineTransactionState.Failed, mutated.State);
        Assert.IsTrue(File.Exists(source));

        (plan, source, _) = CreatePlan(temporary, "again", "locked");
        using (FileStream held = new(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            QuarantineResult locked = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-locked");
            Assert.AreEqual(QuarantineTransactionState.Failed, locked.State);
            Assert.AreEqual(QuarantineFailure.Locked, locked.Entries.Single().Failure);
        }

        (plan, source, _) = CreatePlan(temporary, "copy", "copy");
        RecordingCleanupFaults faults = new() { CrossVolume = true, ThrowAfterCopy = true };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-copy");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(Directory.EnumerateFiles(Path.Combine(plan.QuarantineRoot, "q-copy"), "*.partial", SearchOption.AllDirectories).Any());
        Assert.IsFalse(File.Exists(interrupted.ManifestPath));
        QuarantineRecoveryState recovery = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-copy");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, recovery.State);
        Assert.AreEqual(QuarantineEntryState.Failed, recovery.Entries.Single().State);
        Assert.IsFalse(recovery.HasCommittedManifest);
        QuarantineResult recoveredCopy = await new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true }).RecoverRecordedAsync(JournalFor(plan, "q-copy"), "q-copy", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recoveredCopy.State);

        (plan, source, _) = CreatePlan(temporary, "move", "move");
        faults = new RecordingCleanupFaults { ThrowAt = "entry:moved" };
        QuarantineResult moved = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-move");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, moved.State);
        Assert.IsFalse(File.Exists(source));
        Assert.IsTrue(File.Exists(Path.Combine(plan.QuarantineRoot, "q-move", "files", "bin", "x64", "winmm.dll")));
        Assert.IsFalse(File.Exists(moved.ManifestPath));
        QuarantineResult recoveredMove = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-move"), "q-move", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recoveredMove.State);

        (plan, source, _) = CreatePlan(temporary, "manifest", "manifest");
        faults = new RecordingCleanupFaults { ThrowAt = "manifest:before-commit" };
        QuarantineResult manifestInterrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-manifest");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, manifestInterrupted.State);
        Assert.IsFalse(File.Exists(source));
        Assert.IsFalse(File.Exists(manifestInterrupted.ManifestPath));
        Assert.IsTrue(QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-manifest").Entries.All(value => value.State == QuarantineEntryState.Stored));

        (plan, _, _) = CreatePlan(temporary, "committed", "committed");
        faults = new RecordingCleanupFaults { ThrowAt = "manifest:committed" };
        QuarantineResult committed = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-committed");
        Assert.AreEqual(QuarantineTransactionState.Committed, committed.State);
        Assert.IsTrue(File.Exists(committed.ManifestPath));
        Assert.IsTrue(QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-committed").HasCommittedManifest);
    }

    [TestMethod]
    public async Task Recover_RejectsSourceJunctionAliasingTheOnlyPayloadAndResumesAfterRepair()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "only payload", "source-alias");
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "entry:moved" }), plan, "q-source-alias");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        string payload = interrupted.Manifest.Entries.Single().DestinationPath;
        string parent = Path.GetDirectoryName(source)!;
        Directory.Delete(parent);
        CreateDirectoryJunction(parent, Path.GetDirectoryName(payload)!);
        try
        {
            QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-source-alias"), "q-source-alias", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.IsTrue(File.Exists(payload), "Recovery deleted the only payload through the source junction.");
            Assert.AreEqual("only payload", File.ReadAllText(payload));
            Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
            Assert.IsFalse(File.Exists(rejected.ManifestPath));
        }
        finally
        {
            Directory.Delete(parent);
        }
        Directory.CreateDirectory(parent);
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-source-alias"), "q-source-alias", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.AreEqual("only payload", File.ReadAllText(payload));
    }

    [TestMethod]
    [DataRow("entry:copied")]
    [DataRow("entry:published")]
    public async Task Recover_RejectsPayloadParentJunctionBeforeDeletingSourceOrPartial(string boundary)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "retained source", "payload-alias");
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true, ThrowAt = boundary }), plan, "q-payload-alias");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        string payload = interrupted.Manifest.Entries.Single().DestinationPath;
        string parent = Path.GetDirectoryName(payload)!;
        string outside = Path.Combine(temporary.Path, "displaced-payload");
        Directory.Move(parent, outside);
        CreateDirectoryJunction(parent, outside);
        string retained = Path.Combine(outside, boundary == "entry:copied" ? "winmm.dll.partial" : "winmm.dll");
        try
        {
            QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-payload-alias"), "q-payload-alias", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
            Assert.AreEqual("retained source", File.ReadAllText(source));
            Assert.AreEqual("retained source", File.ReadAllText(retained));
            Assert.IsFalse(File.Exists(rejected.ManifestPath));
        }
        finally
        {
            Directory.Delete(parent);
        }
        Directory.Move(outside, parent);
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-payload-alias"), "q-payload-alias", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.AreEqual("retained source", File.ReadAllText(payload));
        Assert.IsFalse(File.Exists(source));
    }

    [TestMethod]
    public async Task ManifestReader_RejectsNullEntryWithOwnedErrorAndRetainsPayload()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, _) = CreatePlan(temporary, "retained payload", "null-entry");
        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-null-entry");
        string payload = stored.Manifest.Entries.Single().DestinationPath;
        JsonNode manifest = JsonNode.Parse(File.ReadAllBytes(stored.ManifestPath))!;
        manifest["entries"]![0] = null;
        File.WriteAllText(stored.ManifestPath, ContractJson.Serialize(manifest));
        Assert.ThrowsExactly<CleanupManifestException>(() => QuarantineManifestCodec.ReadStrict(stored.ManifestPath));
        Assert.AreEqual("retained payload", File.ReadAllText(payload));
    }

    [TestMethod]
    [DataRow("manifest")]
    [DataRow("restore")]
    [DataRow("hash")]
    public void RecoveryReaders_RejectOversizedStateBeforeAllocatingItsPayload(string reader)
    {
        using TemporaryDirectory temporary = new();
        string transaction = Path.Combine(temporary.Path, "q-oversized");
        Directory.CreateDirectory(transaction);
        string path = Path.Combine(transaction, reader == "restore" ? "restore.wal.json" : "quarantine.json");
        using (FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength((reader == "restore" ? 4L : 32L) * 1024 * 1024 + 1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.ThrowsExactly<CleanupManifestException>(() =>
        {
            if (reader == "restore") QuarantineTransaction.ReadRestoreRecovery(new QuarantineUndoReference("q-oversized", new string('a', 64), temporary.Path));
            else if (reader == "manifest") QuarantineManifestCodec.ReadStrict(path);
            else QuarantineManifestCodec.Sha256(path);
        });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.IsTrue(allocated < 1024 * 1024, $"Oversized {reader} allocated {allocated} bytes.");
    }

    [TestMethod]
    public async Task Recover_AdoptsLegacyMovingDirectoryOnlyAtExactVerifiedTransactionDestination()
    {
        using TemporaryDirectory temporary = new();
        CleanupPlan plan = CreateDirectoryPlan(temporary, "legacy-directory");
        RecordingCleanupFaults faults = new() { ThrowAt = "directory:deleted" };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-legacy-directory");
        QuarantineManifestEntry directory = interrupted.Manifest.Entries.First(value => value.Kind == CleanupEntryKind.Directory && value.State == QuarantineEntryState.Failed);
        Assert.IsFalse(Directory.Exists(directory.SourcePath));
        Assert.IsTrue(Directory.Exists(directory.DestinationPath));
        RewriteDirectoryWal(plan, "q-legacy-directory", directory.RelativePath, QuarantineEntryState.Moving, string.Empty);

        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-legacy-directory"), "q-legacy-directory", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);

        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.IsTrue(File.Exists(recovered.ManifestPath));
        Assert.IsTrue(recovered.Manifest.Entries.All(value => value.State == QuarantineEntryState.Stored && value.PostIdentity.Length > 0));
    }

    [TestMethod]
    public async Task Recover_DoesNotAdoptDirectoryWhenDurableDestinationIdentityDiffers()
    {
        using TemporaryDirectory temporary = new();
        CleanupPlan plan = CreateDirectoryPlan(temporary, "spoofed-directory");
        RecordingCleanupFaults faults = new() { ThrowAt = "directory:deleted" };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-spoofed-directory");
        QuarantineManifestEntry directory = interrupted.Manifest.Entries.First(value => value.Kind == CleanupEntryKind.Directory && value.State == QuarantineEntryState.Failed);
        RewriteDirectoryWal(plan, "q-spoofed-directory", directory.RelativePath, QuarantineEntryState.Published, "ffffffff:ffffffffffffffff");

        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-spoofed-directory"), "q-spoofed-directory", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);

        Assert.AreEqual(QuarantineTransactionState.Recoverable, recovered.State);
        Assert.AreEqual(QuarantineFailure.IdentityChanged, recovered.Entries.Single(value => value.RelativePath == directory.RelativePath).Failure);
        Assert.IsFalse(File.Exists(recovered.ManifestPath));
    }

    [TestMethod]
    public async Task Restore_RequiresExactManifestHashNeverOverwritesAndIsIdempotent()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "payload");
        QuarantineTransaction transaction = new();
        QuarantineResult stored = await ExecuteTestAsync(transaction, plan, "q-restore");
        string manifestHash = QuarantineManifestCodec.Sha256(stored.ManifestPath);

        File.WriteAllText(source, "collision");
        QuarantineRestoreResult conflict = await RestoreTestAsync(transaction, new QuarantineUndoReference("q-restore", manifestHash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.Conflict, conflict.Entries.Single().State);
        Assert.AreEqual("collision", File.ReadAllText(source));
        File.Delete(source);

        QuarantineRestoreResult restored = await RestoreTestAsync(transaction, new QuarantineUndoReference("q-restore", manifestHash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.Restored, restored.Entries.Single().State);
        Assert.AreEqual("payload", File.ReadAllText(source));
        Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(source, CleanupEntryKind.File, plan.Entries.Single().Metadata));
        QuarantineRestoreResult repeated = await RestoreTestAsync(transaction, new QuarantineUndoReference("q-restore", manifestHash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.AlreadyRestored, repeated.Entries.Single().State);

        Assert.ThrowsExactly<CleanupManifestException>(() => RestoreTestAsync(transaction, new QuarantineUndoReference("q-restore", new string('0', 64), plan.QuarantineRoot), plan.GameRoot).GetAwaiter().GetResult());
        Assert.ThrowsExactly<CleanupManifestException>(() => RestoreTestAsync(transaction, new QuarantineUndoReference("missing", manifestHash, plan.QuarantineRoot), plan.GameRoot).GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task Restore_RejectsCorruptPayloadReparseEscapeAndPartialRollbackRemainsRetryable()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "first");
        string second = Path.Combine(plan.GameRoot, "r6", "scripts", "second.reds");
        Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(second, "second");
        CleanupEntry secondEntry = WindowsCleanupProbe.ObserveFileForPlan(plan.GameRoot, second, "reason");
        plan = plan with { Entries = [plan.Entries[0], secondEntry], RequiredBytes = plan.RequiredBytes + secondEntry.Size };
        QuarantineTransaction transaction = new();
        QuarantineResult stored = await ExecuteTestAsync(transaction, plan, "q-partial");
        string hash = QuarantineManifestCodec.Sha256(stored.ManifestPath);
        string quarantinedSecond = stored.Manifest.Entries[1].DestinationPath;
        File.WriteAllText(quarantinedSecond, "corrupt");

        QuarantineRestoreResult partial = await RestoreTestAsync(transaction, new QuarantineUndoReference("q-partial", hash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.Restored, partial.Entries[0].State);
        Assert.AreEqual(QuarantineRestoreEntryState.Failed, partial.Entries[1].State);
        QuarantineRestoreRecovery restoreRecovery = QuarantineTransaction.ReadRestoreRecovery(new QuarantineUndoReference("q-partial", hash, plan.QuarantineRoot));
        CollectionAssert.AreEqual(partial.Entries.Select(value => value.State).ToArray(), restoreRecovery.Entries.Select(value => value.State).ToArray());
        File.WriteAllText(quarantinedSecond, "second");
        QuarantineTransaction.ApplyMetadataForTests(quarantinedSecond, CleanupEntryKind.File, stored.Manifest.Entries[1].Metadata);
        QuarantineRestoreResult retry = await RestoreTestAsync(transaction, new QuarantineUndoReference("q-partial", hash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.AlreadyRestored, retry.Entries[0].State);
        Assert.AreEqual(QuarantineRestoreEntryState.Restored, retry.Entries[1].State);
    }

    [TestMethod]
    public async Task Restore_CrossVolumeCopyFaultKeepsQuarantinePayloadAndRecoverablePartial()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "payload");
        QuarantineTransaction initial = new();
        QuarantineResult stored = await ExecuteTestAsync(initial, plan, "q-cross-restore");
        string hash = QuarantineManifestCodec.Sha256(stored.ManifestPath);
        RecordingCleanupFaults faults = new() { CrossVolume = true, ThrowAt = "restore:copied" };

        QuarantineRestoreResult failed = await RestoreTestAsync(new QuarantineTransaction(faults), new QuarantineUndoReference("q-cross-restore", hash, plan.QuarantineRoot), plan.GameRoot);

        Assert.AreEqual(QuarantineRestoreEntryState.Failed, failed.Entries.Single().State);
        Assert.IsFalse(File.Exists(source));
        Assert.IsTrue(File.Exists(stored.Manifest.Entries.Single().DestinationPath));
        Assert.IsTrue(File.Exists(source + ".partial"));
        QuarantineRestoreResult retry = await RestoreTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true }), new QuarantineUndoReference("q-cross-restore", hash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.Restored, retry.Entries.Single().State);
        Assert.AreEqual("payload", File.ReadAllText(source));
        Assert.IsFalse(File.Exists(source + ".partial"));
    }

    [TestMethod]
    public async Task Restore_SameVolumeMoveCrashIsIdempotentOnRetry()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "payload");
        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-restore-move");
        string hash = QuarantineManifestCodec.Sha256(stored.ManifestPath);
        RecordingCleanupFaults faults = new() { ThrowAt = "restore:moved" };

        QuarantineRestoreResult interrupted = await RestoreTestAsync(new QuarantineTransaction(faults), new QuarantineUndoReference("q-restore-move", hash, plan.QuarantineRoot), plan.GameRoot);

        Assert.AreEqual(QuarantineRestoreEntryState.Failed, interrupted.Entries.Single().State);
        Assert.AreEqual("payload", File.ReadAllText(source));
        Assert.IsFalse(File.Exists(stored.Manifest.Entries.Single().DestinationPath));
        QuarantineRestoreResult retry = await RestoreTestAsync(new QuarantineTransaction(), new QuarantineUndoReference("q-restore-move", hash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.AlreadyRestored, retry.Entries.Single().State);
    }

    [TestMethod]
    public async Task Restore_CrossVolumePublishedCopyIsReconciledBeforeIdempotentCompletion()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "payload");
        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-restore-publish");
        string hash = QuarantineManifestCodec.Sha256(stored.ManifestPath);
        RecordingCleanupFaults faults = new() { CrossVolume = true, ThrowAt = "restore:published" };
        QuarantineRestoreResult interrupted = await RestoreTestAsync(new QuarantineTransaction(faults), new QuarantineUndoReference("q-restore-publish", hash, plan.QuarantineRoot), plan.GameRoot);
        Assert.AreEqual(QuarantineRestoreEntryState.Failed, interrupted.Entries.Single().State);
        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(File.Exists(stored.Manifest.Entries.Single().DestinationPath));

        QuarantineRestoreResult retry = await RestoreTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true }), new QuarantineUndoReference("q-restore-publish", hash, plan.QuarantineRoot), plan.GameRoot);

        Assert.AreEqual(QuarantineRestoreEntryState.AlreadyRestored, retry.Entries.Single().State);
        Assert.IsFalse(File.Exists(stored.Manifest.Entries.Single().DestinationPath));
    }

    [TestMethod]
    public async Task Restore_RejectsCanonicalHashMatchedButCorruptManifest()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, _) = CreatePlan(temporary, "payload");
        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-corrupt");
        File.AppendAllText(stored.ManifestPath, " ");
        string corruptHash = QuarantineManifestCodec.Sha256(stored.ManifestPath);
        Assert.ThrowsExactly<CleanupManifestException>(() => RestoreTestAsync(new QuarantineTransaction(), new QuarantineUndoReference("q-corrupt", corruptHash, plan.QuarantineRoot), plan.GameRoot).GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task Execute_RejectsAFileThatPhysicallyEscapesThroughDirectoryReparse()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-reparse");
        string vault = Path.Combine(temporary.Path, "vault-reparse");
        string outside = Path.Combine(temporary.Path, "outside", "x64");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(outside);
        string outsideFile = Path.Combine(outside, "winmm.dll");
        File.WriteAllText(outsideFile, "outside");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(game, "bin"), Path.GetDirectoryName(outside)!);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("mklink");
            start.ArgumentList.Add("/J");
            start.ArgumentList.Add(Path.Combine(game, "bin"));
            start.ArgumentList.Add(Path.GetDirectoryName(outside)!);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                Assert.Inconclusive("Directory reparse creation is unavailable on this machine.");
                return;
            }
        }
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        CleanupEntry entry = WindowsCleanupProbe.ObserveFileForPlan(game, source, "reason");
        CleanupPlan plan = new(1, game, WindowsCleanupProbe.IdentityOfDirectory(game), vault, WindowsCleanupProbe.IdentityOfDirectory(vault), entry.Size, [], [entry]);

        QuarantineResult result = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-reparse");

        Assert.AreEqual(QuarantineTransactionState.Failed, result.State);
        Assert.IsTrue(File.Exists(outsideFile));
        Assert.IsFalse(File.Exists(result.ManifestPath));
        Directory.Delete(Path.Combine(game, "bin"), false);
    }

    [TestMethod]
    public async Task Restore_RejectsChildJunctionSwapWithoutWritingOutsideOwnedGameRoot()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, _) = CreatePlan(temporary, "payload", "restore-junction");
        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-restore-junction");
        Directory.Delete(Path.Combine(plan.GameRoot, "bin", "x64"));
        Directory.Delete(Path.Combine(plan.GameRoot, "bin"));
        string outside = Path.Combine(temporary.Path, "outside-restore");
        Directory.CreateDirectory(outside);
        CreateDirectoryJunction(Path.Combine(plan.GameRoot, "bin"), outside);
        string outsideTarget = Path.Combine(outside, "x64", "winmm.dll");

        QuarantineRestoreResult result = await RestoreTestAsync(new QuarantineTransaction(), new QuarantineUndoReference("q-restore-junction", QuarantineManifestCodec.Sha256(stored.ManifestPath), plan.QuarantineRoot), plan.GameRoot);

        Assert.AreEqual(QuarantineRestoreEntryState.Conflict, result.Entries.Single().State);
        Assert.IsFalse(File.Exists(outsideTarget));
        Assert.IsTrue(File.Exists(stored.Manifest.Entries.Single().DestinationPath));
        Directory.Delete(Path.Combine(plan.GameRoot, "bin"), false);
    }

    [TestMethod]
    public void Execute_RejectsAnArbitraryOrCaseCollidingPlanBeforeCreatingAWal()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, _) = CreatePlan(temporary, "payload");
        string outside = Path.Combine(temporary.Path, "outside.dll");
        File.WriteAllText(outside, "outside");
        CleanupEntry escaped = WindowsCleanupProbe.ObserveFileForPlan(temporary.Path, outside, "reason") with { RelativePath = @"bin\x64\winmm.dll" };
        CleanupPlan malicious = plan with { Entries = [escaped], RequiredBytes = escaped.Size };

        Assert.ThrowsExactly<CleanupManifestException>(() => ExecuteTestAsync(new QuarantineTransaction(), malicious, "q-escape").GetAwaiter().GetResult());
        Assert.IsTrue(File.Exists(outside));
        Assert.IsFalse(Directory.Exists(Path.Combine(plan.QuarantineRoot, "q-escape")));

        CleanupEntry duplicate = plan.Entries.Single() with { RelativePath = plan.Entries.Single().RelativePath.ToUpperInvariant() };
        CleanupPlan colliding = plan with { Entries = [plan.Entries.Single(), duplicate], RequiredBytes = plan.RequiredBytes * 2 };
        Assert.ThrowsExactly<CleanupManifestException>(() => ExecuteTestAsync(new QuarantineTransaction(), colliding, "q-collision").GetAwaiter().GetResult());
    }

    [TestMethod]
    public async Task ExecuteAndRestore_HandlesNestedDirectoryEntriesWithoutOverwritingTheirPayloadParents()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-tree");
        string vault = Path.Combine(temporary.Path, "vault-tree");
        Directory.CreateDirectory(vault);
        string nested = Path.Combine(game, "archive", "pc", "mod", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "mod.archive"), "payload");
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        CleanupPlan plan = new CleanupPlanner().Plan(game, vault, corpus);

        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-tree");

        Assert.AreEqual(QuarantineTransactionState.Committed, stored.State, string.Join(" | ", stored.Entries.Where(value => value.State == QuarantineEntryState.Failed).Select(value => value.Reason)));
        Assert.IsFalse(Directory.Exists(Path.Combine(game, "archive", "pc", "mod")));
        Assert.AreEqual(3, stored.Manifest.Entries.Length);
        string hash = QuarantineManifestCodec.Sha256(stored.ManifestPath);
        QuarantineRestoreResult restored = await RestoreTestAsync(new QuarantineTransaction(), new QuarantineUndoReference("q-tree", hash, vault), game);
        Assert.IsTrue(restored.Entries.All(value => value.State is QuarantineRestoreEntryState.Restored or QuarantineRestoreEntryState.AlreadyRestored));
        Assert.AreEqual("payload", File.ReadAllText(Path.Combine(nested, "mod.archive")));
    }

    [TestMethod]
    public async Task Restore_PreflightsAnUnknownDirectoryCollisionBeforeRestoringAnyDescendant()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-tree-conflict");
        string vault = Path.Combine(temporary.Path, "vault-tree-conflict");
        Directory.CreateDirectory(vault);
        string mod = Path.Combine(game, "archive", "pc", "mod");
        Directory.CreateDirectory(Path.Combine(mod, "nested"));
        File.WriteAllText(Path.Combine(mod, "nested", "mod.archive"), "payload");
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        CleanupPlan plan = new CleanupPlanner().Plan(game, vault, corpus);
        QuarantineResult stored = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-tree-conflict");
        Directory.CreateDirectory(mod);
        string foreign = Path.Combine(mod, "foreign.txt");
        File.WriteAllText(foreign, "foreign");

        QuarantineRestoreResult restored = await RestoreTestAsync(new QuarantineTransaction(), new QuarantineUndoReference("q-tree-conflict", QuarantineManifestCodec.Sha256(stored.ManifestPath), vault), game);

        Assert.IsTrue(restored.Entries.All(value => value.State == QuarantineRestoreEntryState.Conflict));
        Assert.AreEqual("foreign", File.ReadAllText(foreign));
        Assert.IsFalse(File.Exists(Path.Combine(mod, "nested", "mod.archive")));
        Assert.IsTrue(File.Exists(stored.Manifest.Entries.Single(value => value.Kind == CleanupEntryKind.File).DestinationPath));
    }

    private static (CleanupPlan Plan, string Source, string Vault) CreatePlan(TemporaryDirectory temporary, string content, string suffix = "base")
    {
        string game = Path.Combine(temporary.Path, "game-" + suffix);
        string vault = Path.Combine(temporary.Path, "vault-" + suffix);
        string source = Path.Combine(game, "bin", "x64", "winmm.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        Directory.CreateDirectory(vault);
        File.WriteAllText(source, content);
        CleanupEntry entry = WindowsCleanupProbe.ObserveFileForPlan(game, source, "mod loader proxy - not in a clean install");
        CleanupPlan plan = new(1, game, WindowsCleanupProbe.IdentityOfDirectory(game), vault, WindowsCleanupProbe.IdentityOfDirectory(vault), entry.Size, [], [entry]);
        return (plan, source, vault);
    }

    private static CleanupPlan CreateDirectoryPlan(TemporaryDirectory temporary, string suffix)
    {
        string game = Path.Combine(temporary.Path, "game-" + suffix);
        string vault = Path.Combine(temporary.Path, "vault-" + suffix);
        Directory.CreateDirectory(Path.Combine(game, "archive", "pc", "mod", "nested"));
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(game, "archive", "pc", "mod", "nested", "mod.archive"), "payload");
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        return new CleanupPlanner().Plan(game, vault, corpus);
    }

    [TestMethod]
    public void DirectoryMetadata_AppliesSourceDaclThroughHeldDestination()
    {
        using TemporaryDirectory temporary = new();
        string source = Path.Combine(temporary.Path, "source");
        string vault = Path.Combine(temporary.Path, "vault");
        string destination = Path.Combine(vault, "stored");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(vault);
        DirectorySecurity security = new DirectoryInfo(source).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        security.SetAccessRuleProtection(true, false);
        System.Security.Principal.SecurityIdentifier user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(source).SetAccessControl(security);
        CleanupPathMetadata metadata = WindowsCleanupProbe.ReadMetadataForTests(source, true);
        using CleanupDirectoryGuard guard = new(vault);
        guard.EnsureDirectory(destination);
        Assert.AreNotEqual(metadata.SecuritySddl, WindowsCleanupProbe.ReadMetadataForTests(destination, true).SecuritySddl);
        QuarantineTransaction.ApplyMetadataForTests(destination, CleanupEntryKind.Directory, metadata);
        Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(destination, CleanupEntryKind.Directory, metadata));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExecuteAndRestore_PreservesDirectoryMetadataWhenVaultInheritanceDiffers(bool withFile)
    {
        using TemporaryDirectory temporary = new();
        CleanupPlan initial = CreateDirectoryPlan(temporary, "different-dacl");
        string source = Path.Combine(initial.GameRoot, "archive", "pc", "mod", "nested");
        if (!withFile) File.Delete(Path.Combine(source, "mod.archive"));
        DirectorySecurity security = new DirectoryInfo(source).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        security.SetAccessRuleProtection(true, false);
        System.Security.Principal.SecurityIdentifier user = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(source).SetAccessControl(security);
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.Hidden);
        CleanupPathMetadata expected = WindowsCleanupProbe.ReadMetadataForTests(source, true);
        Assert.AreNotEqual(expected.SecuritySddl, WindowsCleanupProbe.ReadMetadataForTests(initial.QuarantineRoot, true).SecuritySddl);
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        CleanupPlan plan = new CleanupPlanner().Plan(initial.GameRoot, initial.QuarantineRoot, corpus);
        QuarantineResult result = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-different-dacl");
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State, string.Join(" | ", result.Entries.Select(value => value.Reason)));
        QuarantineManifestEntry stored = result.Manifest.Entries.Single(value => value.RelativePath == @"archive\pc\mod\nested");
        Assert.IsFalse(Directory.Exists(source));
        Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(stored.DestinationPath, CleanupEntryKind.Directory, expected));
        QuarantineRestoreResult restored = await RestoreTestAsync(new QuarantineTransaction(), new QuarantineUndoReference("q-different-dacl", QuarantineManifestCodec.Sha256(result.ManifestPath), plan.QuarantineRoot), plan.GameRoot);
        Assert.IsTrue(restored.Entries.All(value => value.State is QuarantineRestoreEntryState.Restored or QuarantineRestoreEntryState.AlreadyRestored));
        Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(source, CleanupEntryKind.Directory, expected));
    }

    [TestMethod]
    [DataRow("header")]
    [DataRow("transaction")]
    [DataRow("sourceRoot")]
    [DataRow("backupRoot")]
    [DataRow("order")]
    [DataRow("immutable")]
    [DataRow("malformed")]
    [DataRow("unknown")]
    public async Task Recovery_RejectsUnboundOrMalformedEntryStateBeforeMutation(string change)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, string statePath, _) = await MovingSnapshot(temporary);
        JsonNode document = JsonNode.Parse(File.ReadAllBytes(statePath))!;
        switch (change)
        {
            case "header": document["headerSha256"] = new string('0', 64); break;
            case "transaction": document["quarantineId"] = "another-transaction"; break;
            case "sourceRoot": document["gameRootIdentity"] = "another-root"; break;
            case "backupRoot": document["quarantineRootIdentity"] = "another-backup"; break;
            case "order": document["entry"]!["order"] = 1; break;
            case "immutable": document["entry"]!["destinationPath"] = Path.Combine(temporary.Path, "outside.txt"); break;
        }
        if (change == "malformed") File.WriteAllText(statePath, "{truncated");
        else if (change == "unknown") File.WriteAllText(Path.Combine(Path.GetDirectoryName(statePath)!, "personal.txt"), "preserve");
        else File.WriteAllBytes(statePath, ContractJson.SerializeUtf8(document));
        Assert.ThrowsExactly<CleanupManifestException>(() => QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-snapshot"));
        await Assert.ThrowsExactlyAsync<CleanupManifestException>(() => new QuarantineTransaction().RecoverRecordedAsync(
            JournalFor(plan, "q-snapshot"), "q-snapshot", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None));
        Assert.AreEqual("snapshot payload", File.ReadAllText(source));
        if (change == "unknown") Assert.AreEqual("preserve", File.ReadAllText(Path.Combine(Path.GetDirectoryName(statePath)!, "personal.txt")));
    }

    [TestMethod]
    public async Task Recovery_IgnoresUnpublishedTornEntryStateAndRetainsIt()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, string statePath, _) = await MovingSnapshot(temporary);
        File.Delete(statePath);
        string unpublished = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(unpublished, "{truncated unpublished state");
        QuarantineRecoveryState before = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-snapshot");
        Assert.AreEqual(QuarantineEntryState.Planned, before.Entries.Single().State);
        Assert.IsTrue(File.Exists(source));
        QuarantineResult result = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-snapshot"), "q-snapshot", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("{truncated unpublished state", File.ReadAllText(unpublished));
    }

    [TestMethod]
    public async Task Recovery_UsesHeaderHashNamespaceAndPreservesEarlierStateEvidence()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, string statePath, byte[] headerBytes) = await MovingSnapshot(temporary);
        File.WriteAllText(statePath, "old state evidence");
        QuarantineManifest header = ContractJson.Deserialize<QuarantineManifest>(headerBytes);
        string walPath = Path.Combine(plan.QuarantineRoot, "q-snapshot", "transaction.wal.json");
        QuarantineTransaction.WriteAtomic(walPath, ContractJson.SerializeUtf8(header with { State = QuarantineTransactionState.Recoverable }));
        QuarantineRecoveryState current = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-snapshot");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, current.State);
        Assert.AreEqual(QuarantineEntryState.Planned, current.Entries.Single().State);
        QuarantineResult result = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-snapshot"), "q-snapshot", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.AreEqual("old state evidence", File.ReadAllText(statePath));
    }

    [TestMethod]
    public async Task Recovery_ReconcilesPublishedEntrySnapshotAfterLostMoveAcknowledgment()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "published payload", "published-snapshot");
        byte[]? staged = null;
        RecordingCleanupFaults faults = new() { ThrowAt = "entry:moved", OnBoundary = name =>
        {
            if (name == "wal:staged") staged = File.ReadAllBytes(Path.Combine(plan.QuarantineRoot, "q-published", "transaction.wal.json"));
        } };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-published");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        Assert.IsFalse(File.Exists(source));
        QuarantineTransaction.WriteAtomic(Path.Combine(plan.QuarantineRoot, "q-published", "transaction.wal.json"), staged!);
        QuarantineRecoveryState recovery = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-published");
        Assert.AreEqual(QuarantineEntryState.Published, recovery.Entries.Single().State);
        QuarantineResult result = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-published"), "q-published", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.AreEqual("published payload", File.ReadAllText(result.Manifest.Entries.Single().DestinationPath));
    }

    private static async Task<(CleanupPlan Plan, string Source, string StatePath, byte[] Header)> MovingSnapshot(TemporaryDirectory temporary)
    {
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "snapshot payload", "snapshot-state");
        byte[]? header = null;
        RecordingCleanupFaults faults = new() { ThrowAt = "entry:moving", OnBoundary = name =>
        {
            if (name == "wal:staged") header = File.ReadAllBytes(Path.Combine(plan.QuarantineRoot, "q-snapshot", "transaction.wal.json"));
        } };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-snapshot");
        Assert.AreEqual(QuarantineTransactionState.Failed, interrupted.State);
        QuarantineTransaction.WriteAtomic(Path.Combine(plan.QuarantineRoot, "q-snapshot", "transaction.wal.json"), header!);
        string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(header!));
        string statePath = Path.Combine(plan.QuarantineRoot, "q-snapshot", "entry-state", hash, "00000000.json");
        Assert.IsTrue(File.Exists(statePath));
        return (plan, source, statePath, header!);
    }

    [TestMethod]
    [TestCategory("CleanupScale")]
    public async Task CleanupScale_6000Files()
    {
        if (Environment.GetEnvironmentVariable("CHROMEBLOOD_RUN_CLEANUP_SCALE") != "1")
        {
            Assert.Inconclusive("Opt in with CHROMEBLOOD_RUN_CLEANUP_SCALE=1.");
            return;
        }
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string vault = Path.Combine(temporary.Path, "vault");
        string mods = Path.Combine(game, "archive", "pc", "mod");
        Directory.CreateDirectory(vault);
        for (int directory = 0; directory < 60; directory++)
        {
            string folder = Path.Combine(mods, "pack-" + directory.ToString("D2"));
            Directory.CreateDirectory(folder);
            for (int file = 0; file < 100; file++) File.WriteAllText(Path.Combine(folder, "asset-" + file.ToString("D3") + ".archive"), "x");
        }
        using Process process = Process.GetCurrentProcess();
        Assert.IsTrue(GetProcessIoCounters(process.Handle, out IoCounters before));
        Stopwatch elapsed = Stopwatch.StartNew();
        CleanupPlan? plan = null;
        QuarantineResult? result = null;
        Exception? failure = null;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
        try
        {
            LegacyParityCorpus corpus = LegacyParityCorpus.LoadEmbedded();
            CleanupPlanner planner = CleanupPlanner.ForDisposableTests(temporary.Path);
            plan = planner.Plan(game, vault, corpus, cancellation.Token);
            AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, [@"archive\pc\mod"], corpus, cancellation.Token);
            UndoJournal journal = new(Path.Combine(temporary.Path, "scale-undo.json"));
            result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, "scale-6000", journal, cancellation.Token);
        }
        catch (Exception exception) { failure = exception; }
        elapsed.Stop();
        process.Refresh();
        Assert.IsTrue(GetProcessIoCounters(process.Handle, out IoCounters after));
        string metrics = $"files=6000; entries={plan?.Entries.Length}; elapsedSeconds={elapsed.Elapsed.TotalSeconds:F3}; peakRssBytes={process.PeakWorkingSet64}; processWriteBytes={after.WriteBytes - before.WriteBytes}; processWriteOperations={after.WriteOperations - before.WriteOperations}; state={result?.State}; error={failure?.GetType().Name}: {failure?.Message}";
        Console.WriteLine(metrics);
        Assert.IsNull(failure, metrics);
        Assert.AreEqual(QuarantineTransactionState.Committed, result!.State, metrics);
        Assert.IsTrue(elapsed.Elapsed <= TimeSpan.FromSeconds(90), metrics);
        Assert.IsTrue(process.PeakWorkingSet64 <= 512L * 1024 * 1024, metrics);
        Assert.IsFalse(Directory.Exists(mods));
        Assert.AreEqual(6000, result.Manifest.Entries.Count(value => value.Kind == CleanupEntryKind.File));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations;
        public ulong WriteOperations;
        public ulong OtherOperations;
        public ulong ReadBytes;
        public ulong WriteBytes;
        public ulong OtherBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(nint process, out IoCounters counters);

    private static void RewriteDirectoryWal(CleanupPlan plan, string quarantineId, string relativePath, QuarantineEntryState state, string postIdentity)
    {
        string walPath = Path.Combine(plan.QuarantineRoot, quarantineId, "transaction.wal.json");
        QuarantineManifest wal = ContractJson.Deserialize<QuarantineManifest>(File.ReadAllBytes(walPath));
        QuarantineManifestEntry[] entries = wal.Entries.Select(entry => entry.RelativePath == relativePath
            ? entry with { State = state, PostIdentity = postIdentity, Failure = QuarantineFailure.None, FailureReason = null }
            : entry).ToArray();
        QuarantineTransaction.WriteAtomic(walPath, ContractJson.SerializeUtf8(wal with { State = QuarantineTransactionState.Recoverable, Entries = entries }));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "fixtures", "legacy"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static Task<QuarantineResult> ExecuteTestAsync(QuarantineTransaction transaction, CleanupPlan plan, string quarantineId)
    {
        string sandbox = Directory.GetParent(plan.GameRoot)?.FullName ?? throw new DirectoryNotFoundException();
        OwnedCleanupMutationScope scope = OwnedCleanupMutationScope.ForDisposableTest(sandbox, plan);
        UndoJournal journal = new(Path.Combine(sandbox, "undo-" + quarantineId + ".json"));
        return transaction.ExecuteDisposableTestAsync(plan, quarantineId, journal, scope, CancellationToken.None);
    }

    private static UndoJournal JournalFor(CleanupPlan plan, string quarantineId)
    {
        string sandbox = Directory.GetParent(plan.GameRoot)?.FullName ?? throw new DirectoryNotFoundException();
        return new UndoJournal(Path.Combine(sandbox, "undo-" + quarantineId + ".json"));
    }

    private static Task<QuarantineRestoreResult> RestoreTestAsync(QuarantineTransaction transaction, QuarantineUndoReference reference, string gameRoot)
    {
        string sandbox = Directory.GetParent(gameRoot)?.FullName ?? throw new DirectoryNotFoundException();
        OwnedCleanupMutationScope scope = OwnedCleanupMutationScope.ForDisposableTestRoots(sandbox, gameRoot, reference.QuarantineRoot);
        return transaction.RestoreDisposableTestAsync(reference, gameRoot, JournalForRoot(sandbox, reference.QuarantineId), scope, CancellationToken.None);
    }

    private static UndoJournal JournalForRoot(string sandbox, string quarantineId)
        => new(Path.Combine(sandbox, "undo-" + quarantineId + ".json"));

    private static void CreateDirectoryJunction(string link, string target)
    {
        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link);
        start.ArgumentList.Add(target);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        if (process.ExitCode != 0) Assert.Inconclusive("Directory junction creation is unavailable on this machine.");
    }
}

internal sealed class RecordingCleanupFaults : ICleanupTransactionFaults
{
    internal List<string> Events { get; } = [];
    internal bool CrossVolume { get; set; }
    internal bool ThrowAfterCopy { get; set; }
    internal string? ThrowAt { get; set; }
    internal Action<string>? OnBoundary { get; set; }

    public bool IsSameVolume(string source, string destination) => !CrossVolume;
    public void Boundary(string name)
    {
        Events.Add(name);
        OnBoundary?.Invoke(name);
        if (ThrowAfterCopy && name == "entry:copied" || name == ThrowAt) throw new IOException("fault");
    }
}
