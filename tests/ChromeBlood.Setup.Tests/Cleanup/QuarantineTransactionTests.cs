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
    [DataRow("none")]
    [DataRow("entry:before-metadata")]
    [DataRow("entry:metadata-applied")]
    [DataRow("entry:published")]
    public async Task CrossVolumeCopy_PreservesExplicitFileDaclAndAttributesAcrossMetadataInterruption(string boundary)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source) = CreateFileMetadataPlan(temporary, "copy-metadata");
        RecordingCleanupFaults faults = new() { CrossVolume = true, ThrowAt = boundary == "none" ? null : boundary };
        QuarantineResult initial = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-copy-metadata");
        Assert.AreEqual(boundary == "none" ? QuarantineTransactionState.Committed : QuarantineTransactionState.Recoverable, initial.State,
            string.Join("; ", initial.Entries.Select(value => value.Reason)));
        if (boundary != "none") Assert.AreEqual("metadata payload", File.ReadAllText(source));
        if (boundary is "entry:before-metadata" or "entry:metadata-applied")
        {
            bool matches = QuarantineTransaction.MetadataMatchesForTests(initial.Manifest.Entries.Single().DestinationPath + ".partial", CleanupEntryKind.File, plan.Entries.Single().Metadata);
            Assert.AreEqual(boundary == "entry:metadata-applied", matches);
        }
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult recovered = await new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true }).RecoverRecordedAsync(
                JournalFor(plan, "q-copy-metadata"), "q-copy-metadata", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State, string.Join("; ", recovered.Entries.Select(value => value.Reason)));
            string destination = recovered.Manifest.Entries.Single().DestinationPath;
            Assert.AreEqual("metadata payload", File.ReadAllText(destination));
            Assert.AreEqual(FileAttributes.Hidden | FileAttributes.Archive, File.GetAttributes(destination));
            Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(destination, CleanupEntryKind.File, plan.Entries.Single().Metadata));
            Assert.IsFalse(File.Exists(source));
        }
    }

    [TestMethod]
    [DataRow("none")]
    [DataRow("entry:before-metadata")]
    [DataRow("entry:metadata-applied")]
    public async Task PublishedMetadataRecovery_RepairsLegacyCopyOnlyWithVerifiedOriginalAcrossInterruption(string boundary)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, string destination) = await CreatePublishedMetadataMismatch(temporary);
        if (boundary != "none")
        {
            QuarantineResult interrupted = await new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = boundary }).RecoverRecordedAsync(
                JournalFor(plan, "q-legacy-metadata"), "q-legacy-metadata", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
            Assert.AreEqual("metadata payload", File.ReadAllText(source));
            Assert.AreEqual("metadata payload", File.ReadAllText(destination));
            Assert.AreEqual(boundary == "entry:metadata-applied", QuarantineTransaction.MetadataMatchesForTests(destination, CleanupEntryKind.File, plan.Entries.Single().Metadata));
        }
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-legacy-metadata"), "q-legacy-metadata", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State, string.Join("; ", recovered.Entries.Select(value => value.Reason)));
            Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(destination, CleanupEntryKind.File, plan.Entries.Single().Metadata));
            Assert.AreEqual("metadata payload", File.ReadAllText(destination));
            Assert.IsFalse(File.Exists(source));
        }
    }

    [TestMethod]
    [DataRow("source-missing")]
    [DataRow("source-content")]
    [DataRow("source-identity")]
    [DataRow("source-metadata")]
    [DataRow("destination-identity")]
    [DataRow("destination-identity-missing")]
    public async Task PublishedMetadataRecovery_PreservesMismatchedBackupWithoutAuthoritativeSourceAndDestination(string change)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, string destination) = await CreatePublishedMetadataMismatch(temporary);
        if (change == "source-missing") File.Delete(source);
        else if (change == "source-content")
        {
            using FileStream stream = new(source, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(0);
            stream.Write("different source content"u8);
        }
        else if (change == "source-metadata") File.SetAttributes(source, FileAttributes.Archive);
        else if (change is "source-identity" or "destination-identity")
        {
            string path = change == "source-identity" ? source : destination;
            File.Move(path, path + ".retained");
            File.Copy(path + ".retained", path);
        }
        else
        {
            string walPath = Path.Combine(plan.QuarantineRoot, "q-legacy-metadata", "transaction.wal.json");
            QuarantineManifest wal = ContractJson.Deserialize<QuarantineManifest>(File.ReadAllBytes(walPath));
            QuarantineTransaction.WriteAtomic(walPath, ContractJson.SerializeUtf8(wal with { Entries = wal.Entries.Select(entry => entry with { PostIdentity = string.Empty }).ToArray() }));
        }
        CleanupPathMetadata before = WindowsCleanupProbe.ReadMetadataForTests(destination, false);
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-legacy-metadata"), "q-legacy-metadata", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
            Assert.IsFalse(File.Exists(rejected.ManifestPath));
            Assert.AreEqual("metadata payload", File.ReadAllText(destination));
            CleanupPathMetadata after = WindowsCleanupProbe.ReadMetadataForTests(destination, false);
            Assert.AreEqual(before.Attributes, after.Attributes);
            Assert.AreEqual(before.SecuritySddl, after.SecuritySddl);
            Assert.AreEqual(before.CreationTimeUtc, after.CreationTimeUtc);
            Assert.AreEqual(before.LastWriteTimeUtc, after.LastWriteTimeUtc);
            if (change != "source-missing") Assert.IsTrue(File.Exists(source));
        }
    }

    [TestMethod]
    [DataRow("entry:before-metadata")]
    [DataRow("entry:metadata-applied")]
    public async Task PublishedMetadataRecovery_RechecksSourceAndBackupMetadataAtCommitBoundary(string boundary)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, string destination) = await CreatePublishedMetadataMismatch(temporary);
        CleanupPathMetadata before = WindowsCleanupProbe.ReadMetadataForTests(destination, false);
        RecordingCleanupFaults faults = new() { OnBoundary = current =>
        {
            if (current != boundary) return;
            File.SetAttributes(boundary == "entry:before-metadata" ? source : destination, FileAttributes.Archive);
        } };
        QuarantineResult rejected = await new QuarantineTransaction(faults).RecoverRecordedAsync(JournalFor(plan, "q-legacy-metadata"), "q-legacy-metadata", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
        Assert.AreEqual("metadata payload", File.ReadAllText(source));
        Assert.AreEqual("metadata payload", File.ReadAllText(destination));
        Assert.IsFalse(File.Exists(rejected.ManifestPath));
        if (boundary == "entry:before-metadata")
        {
            CleanupPathMetadata after = WindowsCleanupProbe.ReadMetadataForTests(destination, false);
            Assert.AreEqual(before.Attributes, after.Attributes);
            Assert.AreEqual(before.SecuritySddl, after.SecuritySddl);
        }
    }

    [TestMethod]
    public async Task Recovery_RetainsLockedFailureAcrossRepeatedRetriesAndClearsItAfterRelease()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "locked payload", "repeated-lock");
        UndoJournal journal = JournalFor(plan, "q-repeated-lock");
        using (FileStream held = new(source, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            QuarantineResult initial = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-repeated-lock");
            Assert.AreEqual(QuarantineFailure.Locked, initial.Entries.Single().Failure);
            Assert.AreEqual(32, initial.Entries.Single().NativeErrorCode);
            for (int retry = 0; retry < 2; retry++)
            {
                QuarantineResult failed = await new QuarantineTransaction().RecoverRecordedAsync(journal, "q-repeated-lock", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
                QuarantineEntryResult entry = failed.Entries.Single();
                Assert.AreEqual(QuarantineTransactionState.Recoverable, failed.State);
                Assert.AreEqual(QuarantineFailure.Locked, entry.Failure);
                Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Reason));
                Assert.AreEqual(32, entry.NativeErrorCode);
                Assert.IsNotNull(entry.FailureHResult);
                QuarantineManifestEntry persisted = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-repeated-lock").Entries.Single();
                Assert.AreEqual(entry.Reason, persisted.FailureReason);
                Assert.AreEqual(entry.NativeErrorCode, persisted.NativeErrorCode);
                Assert.IsTrue(File.Exists(source));
                Assert.IsFalse(File.Exists(failed.ManifestPath));
            }
        }
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(journal, "q-repeated-lock", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.IsNull(recovered.Entries.Single().Reason);
        Assert.IsNull(recovered.Entries.Single().NativeErrorCode);
        Assert.IsNull(recovered.Entries.Single().FailureHResult);
        QuarantineResult repeated = await new QuarantineTransaction().RecoverRecordedAsync(journal, "q-repeated-lock", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, repeated.State);
        Assert.AreEqual("locked payload", File.ReadAllText(repeated.Manifest.Entries.Single().DestinationPath));
    }

    [TestMethod]
    public async Task Recovery_ReportsNativeAccessDeniedAndContinuesAfterPermissionRepair()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "protected payload", "permission");
        string original = new FileInfo(source).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        FileSecurity denied = new FileInfo(source).GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadData, AccessControlType.Deny));
        try
        {
            new FileInfo(source).SetAccessControl(denied);
            QuarantineResult result = await ExecuteTestAsync(new QuarantineTransaction(), plan, "q-permission");
            Assert.AreEqual(QuarantineFailure.AccessDenied, result.Entries.Single().Failure);
            Assert.AreEqual(5, result.Entries.Single().NativeErrorCode);
            QuarantineResult retry = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-permission"), "q-permission", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineFailure.AccessDenied, retry.Entries.Single().Failure);
            Assert.AreEqual(5, retry.Entries.Single().NativeErrorCode);
            Assert.IsFalse(string.IsNullOrWhiteSpace(retry.Entries.Single().Reason));
        }
        finally
        {
            FileSecurity restored = new();
            restored.SetSecurityDescriptorSddlForm(original, AccessControlSections.Access);
            new FileInfo(source).SetAccessControl(restored);
        }
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-permission"), "q-permission", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State, recovered.Entries.Single().Reason);
        Assert.AreEqual("protected payload", File.ReadAllText(recovered.Manifest.Entries.Single().DestinationPath));
    }

    [TestMethod]
    [DataRow(false, "wal:staged")]
    [DataRow(false, "entry:moving")]
    [DataRow(false, "entry:moved")]
    [DataRow(false, "entry:stored")]
    [DataRow(false, "manifest:before-commit")]
    [DataRow(false, "manifest:committed")]
    [DataRow(true, "entry:partial-created")]
    [DataRow(true, "entry:copied")]
    [DataRow(true, "entry:published")]
    public async Task Recovery_ContinuesAfterInterruptionAtDurableBoundaries(bool crossVolume, string boundary)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "boundary payload", "boundary");
        QuarantineTransaction interrupted = new(new RecordingCleanupFaults { CrossVolume = crossVolume, ThrowAt = boundary });
        if (boundary == "wal:staged")
            await Assert.ThrowsExactlyAsync<IOException>(() => ExecuteTestAsync(interrupted, plan, "q-boundary"));
        else
            await ExecuteTestAsync(interrupted, plan, "q-boundary");
        QuarantineTransaction recovery = new(new RecordingCleanupFaults { CrossVolume = crossVolume });
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult result = await recovery.RecoverRecordedAsync(JournalFor(plan, "q-boundary"), "q-boundary", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
            Assert.AreEqual("boundary payload", File.ReadAllText(result.Manifest.Entries.Single().DestinationPath));
            Assert.IsFalse(File.Exists(source));
        }
    }

    [TestMethod]
    public async Task Recovery_RetriesCancelledIncompleteCopyOnlyWithRecordedIdentityAndVerifiedSource()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "cancelled copy payload", "cancel-copy");
        using CancellationTokenSource cancellation = new();
        RecordingCleanupFaults faults = new() { CrossVolume = true, OnBoundary = boundary =>
        {
            if (boundary == "entry:partial-created") cancellation.Cancel();
        } };
        OwnedCleanupMutationScope scope = OwnedCleanupMutationScope.ForDisposableTest(temporary.Path, plan);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new QuarantineTransaction(faults).ExecuteDisposableTestAsync(plan, "q-cancel-copy", JournalFor(plan, "q-cancel-copy"), scope, cancellation.Token));
        QuarantineManifestEntry entry = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-cancel-copy").Entries.Single();
        Assert.AreEqual(QuarantineEntryState.Moving, entry.State);
        Assert.IsTrue(entry.PostIdentity.Length > 0);
        Assert.AreEqual(0L, new FileInfo(entry.DestinationPath + ".partial").Length);
        Assert.AreEqual("cancelled copy payload", File.ReadAllText(source));
        QuarantineResult recovered = await new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true }).RecoverRecordedAsync(JournalFor(plan, "q-cancel-copy"), "q-cancel-copy", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.AreEqual("cancelled copy payload", File.ReadAllText(recovered.Manifest.Entries.Single().DestinationPath));
        Assert.IsFalse(File.Exists(entry.DestinationPath + ".partial"));
    }

    [TestMethod]
    [DataRow("source-missing")]
    [DataRow("source-changed")]
    [DataRow("partial-replaced")]
    public async Task Recovery_RetainsPartialWhenSourceOrPartialEvidenceCannotAuthorizeDeletion(string change)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "preserved partial", "unsafe-partial");
        string boundary = change == "partial-replaced" ? "entry:partial-created" : "entry:copied";
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { CrossVolume = true, ThrowAt = boundary }), plan, "q-unsafe-partial");
        string partial = interrupted.Manifest.Entries.Single().DestinationPath + ".partial";
        if (change == "source-missing") File.Delete(source);
        else if (change == "source-changed") File.WriteAllText(source, "changed source");
        else
        {
            File.Move(partial, partial + ".retained");
            File.WriteAllText(partial, "unrelated partial");
        }
        string retained = File.ReadAllText(partial);
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-unsafe-partial"), "q-unsafe-partial", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
            Assert.AreEqual(retained, File.ReadAllText(partial));
            Assert.IsFalse(File.Exists(rejected.ManifestPath));
        }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("changed")]
    [DataRow("replaced")]
    public async Task Recovery_DoesNotCommitPreviouslyStoredPayloadWithoutRecheckingItsEvidence(string change)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, _) = CreatePlan(temporary, "stored payload", "stored-check");
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "manifest:before-commit" }), plan, "q-stored-check");
        string destination = interrupted.Manifest.Entries.Single().DestinationPath;
        if (change == "missing") File.Delete(destination);
        else if (change == "changed") File.WriteAllText(destination, "altered payload");
        else
        {
            File.Move(destination, destination + ".retained");
            File.Copy(destination + ".retained", destination);
        }
        QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-stored-check"), "q-stored-check", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
        Assert.IsFalse(File.Exists(rejected.ManifestPath));
        Assert.IsTrue(rejected.Entries.Single().Failure != QuarantineFailure.None);
    }

    [TestMethod]
    public async Task Recovery_RetainsDestinationConflictAcrossRetriesAndResumesAfterExternalRepair()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "original payload", "conflict-retry");
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "entry:moving" }), plan, "q-conflict-retry");
        string destination = interrupted.Manifest.Entries.Single().DestinationPath;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, "unrelated file");
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-conflict-retry"), "q-conflict-retry", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
            Assert.AreEqual("original payload", File.ReadAllText(source));
            Assert.AreEqual("unrelated file", File.ReadAllText(destination));
            Assert.IsFalse(string.IsNullOrWhiteSpace(rejected.Entries.Single().Reason));
        }
        File.Move(destination, destination + ".retained");
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-conflict-retry"), "q-conflict-retry", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State, recovered.Entries.Single().Reason);
        Assert.AreEqual("unrelated file", File.ReadAllText(destination + ".retained"));
    }

    [TestMethod]
    public async Task Recovery_RetainsManifestPublicationErrorUntilDestinationIsRepaired()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, _, _) = CreatePlan(temporary, "publication payload", "publication-error");
        string manifestPath = Path.Combine(plan.QuarantineRoot, "q-publication-error", "quarantine.json");
        RecordingCleanupFaults faults = new() { OnBoundary = boundary =>
        {
            if (boundary == "manifest:before-commit") Directory.CreateDirectory(manifestPath);
        } };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-publication-error");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(interrupted.Manifest.FailureReason));
        Assert.AreEqual(interrupted.Manifest.FailureReason, QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-publication-error").FailureReason);
        QuarantineResult retry = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-publication-error"), "q-publication-error", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Recoverable, retry.State);
        Assert.IsFalse(string.IsNullOrWhiteSpace(retry.Manifest.FailureReason));
        Assert.IsTrue(Directory.Exists(manifestPath));
        Directory.Delete(manifestPath);
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-publication-error"), "q-publication-error", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.IsNull(recovered.Manifest.FailureReason);
        Assert.AreEqual("publication payload", File.ReadAllText(recovered.Manifest.Entries.Single().DestinationPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("malformed")]
    [DataRow("wrong-quarantine")]
    [DataRow("wrong-root-identity")]
    public async Task Recovery_RejectsMissingMalformedOrUnboundWalAndRetainsSource(string change)
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "journal payload", "bad-wal");
        await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "entry:moving" }), plan, "q-bad-wal");
        string wal = Path.Combine(plan.QuarantineRoot, "q-bad-wal", "transaction.wal.json");
        if (change == "missing") File.Move(wal, wal + ".retained");
        else if (change == "malformed") File.WriteAllText(wal, "{truncated journal");
        else
        {
            QuarantineManifest manifest = ContractJson.Deserialize<QuarantineManifest>(File.ReadAllBytes(wal));
            if (change == "wrong-quarantine")
                manifest = manifest with { QuarantineId = "another-quarantine", Entries = manifest.Entries.Select(entry => entry with
                { DestinationPath = Path.Combine(plan.QuarantineRoot, "another-quarantine", "files", entry.RelativePath) }).ToArray() };
            else manifest = manifest with { GameRootIdentity = "another-root" };
            QuarantineTransaction.WriteAtomic(wal, ContractJson.SerializeUtf8(manifest));
        }
        byte[]? retained = File.Exists(wal) ? File.ReadAllBytes(wal) : null;
        for (int retry = 0; retry < 2; retry++)
        {
            await Assert.ThrowsExactlyAsync<CleanupManifestException>(() => new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-bad-wal"), "q-bad-wal", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None));
            Assert.AreEqual("journal payload", File.ReadAllText(source));
            if (retained is not null) CollectionAssert.AreEqual(retained, File.ReadAllBytes(wal));
        }
    }

    [TestMethod]
    [DataRow("directory:deleted")]
    [DataRow("manifest:before-commit")]
    public async Task Recovery_CompletesNestedRetainedCleanupFolderWithExactDirectoryMetadata(string boundary)
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-nested-backup");
        string vault = Path.Combine(temporary.Path, "vault-nested-backup");
        string previous = Path.Combine(game, "CleanSlate_Quarantine", "2026-10-28_004428");
        string nested = Path.Combine(previous, "archive", "pc", "mod");
        Directory.CreateDirectory(nested);
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(previous, "transaction.json"), "previous cleanup journal");
        File.WriteAllText(Path.Combine(nested, "old.archive"), "previous mod payload");
        CleanupPolicy policy = CleanupPolicy.FromIndexedTargets("disposable-file-inventory", [new("CleanSlate_Quarantine", true, "retained cleanup folder", CleanupTargetExpansion.Exact)]);
        CleanupPlan plan = new CleanupPlanner().Plan(game, vault, policy);
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = boundary }), plan, "q-nested-backup");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        for (int retry = 0; retry < 2; retry++)
        {
            QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-nested-backup"), "q-nested-backup", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State, string.Join("; ", recovered.Entries.Select(value => value.Reason)));
            Assert.IsFalse(Directory.Exists(Path.Combine(game, "CleanSlate_Quarantine")));
            foreach (QuarantineManifestEntry entry in recovered.Manifest.Entries)
                Assert.IsTrue(QuarantineTransaction.MetadataMatchesForTests(entry.DestinationPath, entry.Kind, entry.Metadata), entry.RelativePath);
            string files = Path.Combine(vault, "q-nested-backup", "files", "CleanSlate_Quarantine", "2026-10-28_004428");
            Assert.AreEqual("previous cleanup journal", File.ReadAllText(Path.Combine(files, "transaction.json")));
            Assert.AreEqual("previous mod payload", File.ReadAllText(Path.Combine(files, "archive", "pc", "mod", "old.archive")));
        }
    }

    [TestMethod]
    public async Task Recovery_ReadsLegacyCanonicalWalWithoutOptionalNativeDiagnostics()
    {
        using TemporaryDirectory temporary = new();
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "legacy journal payload", "legacy-fields");
        await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "entry:moving" }), plan, "q-legacy-fields");
        string wal = Path.Combine(plan.QuarantineRoot, "q-legacy-fields", "transaction.wal.json");
        JsonNode document = JsonNode.Parse(File.ReadAllBytes(wal))!;
        document.AsObject().Remove("failureReason");
        foreach (JsonNode? entry in document["entries"]!.AsArray())
        {
            entry!.AsObject().Remove("nativeErrorCode");
            entry.AsObject().Remove("failureHResult");
        }
        QuarantineTransaction.WriteAtomic(wal, ContractJson.SerializeUtf8(document));
        QuarantineRecoveryState recovery = QuarantineTransaction.ReadRecovery(plan.QuarantineRoot, "q-legacy-fields");
        Assert.IsNull(recovery.Entries.Single().NativeErrorCode);
        Assert.IsNull(recovery.Entries.Single().FailureHResult);
        Assert.AreEqual("fault", recovery.Entries.Single().FailureReason);
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-legacy-fields"), "q-legacy-fields", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State);
        Assert.IsFalse(File.Exists(source));
        Assert.AreEqual("legacy journal payload", File.ReadAllText(recovered.Manifest.Entries.Single().DestinationPath));
        Assert.AreEqual(1, QuarantineManifestCodec.ReadStrict(recovered.ManifestPath).SchemaVersion);
    }

    [TestMethod]
    [DataRow("unowned-file")]
    [DataRow("unowned-directory")]
    [DataRow("reparse")]
    public async Task Recovery_StoredDirectoryRejectsUnknownOrReparseChildrenBeforeCommit(string change)
    {
        using TemporaryDirectory temporary = new();
        CleanupPlan plan = CreateDirectoryPlan(temporary, "stored-directory-guard");
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "manifest:before-commit" }), plan, "q-directory-guard");
        QuarantineManifestEntry directory = interrupted.Manifest.Entries.First(value => value.Kind == CleanupEntryKind.Directory);
        string unexpected = Path.Combine(directory.DestinationPath, "unowned");
        string outside = Path.Combine(temporary.Path, "outside");
        if (change == "unowned-file") File.WriteAllText(unexpected, "retained unknown payload");
        else if (change == "unowned-directory") Directory.CreateDirectory(unexpected);
        else
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "personal.txt"), "outside retained");
            CreateDirectoryJunction(unexpected, outside);
        }
        QuarantineTransaction.ApplyMetadataForTests(directory.DestinationPath, CleanupEntryKind.Directory, directory.Metadata!);
        try
        {
            QuarantineResult rejected = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-directory-guard"), "q-directory-guard", CleanupAuthorizationCodec.PlanSha256(plan), CancellationToken.None);
            Assert.AreEqual(QuarantineTransactionState.Recoverable, rejected.State);
            Assert.IsFalse(File.Exists(rejected.ManifestPath));
            StringAssert.Contains(rejected.Entries.Single(value => value.RelativePath == directory.RelativePath).Reason!, "unowned or reparse");
            if (change == "unowned-file") Assert.AreEqual("retained unknown payload", File.ReadAllText(unexpected));
            else if (change == "reparse") Assert.AreEqual("outside retained", File.ReadAllText(Path.Combine(outside, "personal.txt")));
            else Assert.IsTrue(Directory.Exists(unexpected));
        }
        finally { if (change == "reparse") Directory.Delete(unexpected); }
    }

    [TestMethod]
    public async Task Recovery_ManyStoredDirectoriesReadsEachPayloadOnceWithinJournalOverhead()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-directory-resume");
        string vault = Path.Combine(temporary.Path, "vault-directory-resume");
        Directory.CreateDirectory(vault);
        byte[] payload = new byte[512 * 1024];
        Random.Shared.NextBytes(payload);
        const int files = 48;
        for (int index = 0; index < files; index++)
        {
            string directory = Path.Combine(game, "archive", "pc", "mod", "pack-" + index.ToString("D2"), "a", "b", "c", "d");
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(Path.Combine(directory, "payload.archive"), payload);
        }
        CleanupPlan plan = new CleanupPlanner().Plan(game, vault, LegacyParityCorpus.LoadEmbedded());
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(new RecordingCleanupFaults { ThrowAt = "manifest:before-commit" }), plan, "q-directory-resume");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State);
        Assert.IsTrue(interrupted.Manifest.Entries.All(value => value.State == QuarantineEntryState.Stored));
        using Process process = Process.GetCurrentProcess();
        Assert.IsTrue(GetProcessIoCounters(process.Handle, out IoCounters before));
        Stopwatch elapsed = Stopwatch.StartNew();
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(30));
        QuarantineResult recovered = await new QuarantineTransaction().RecoverRecordedAsync(JournalFor(plan, "q-directory-resume"), "q-directory-resume", CleanupAuthorizationCodec.PlanSha256(plan), cancellation.Token);
        elapsed.Stop();
        Assert.IsTrue(GetProcessIoCounters(process.Handle, out IoCounters after));
        ulong readBytes = after.ReadBytes - before.ReadBytes;
        ulong payloadBytes = (ulong)files * (ulong)payload.Length;
        string metrics = $"files={files}; directories={plan.Entries.Count(value => value.Kind == CleanupEntryKind.Directory)}; payloadBytes={payloadBytes}; readBytes={readBytes}; elapsedSeconds={elapsed.Elapsed.TotalSeconds:F3}";
        Console.WriteLine(metrics);
        Assert.AreEqual(QuarantineTransactionState.Committed, recovered.State, metrics);
        Assert.IsTrue(readBytes >= payloadBytes && readBytes < payloadBytes * 2, metrics);
        Assert.AreEqual(files, recovered.Manifest.Entries.Count(value => value.Kind == CleanupEntryKind.File));
        Assert.IsFalse(Directory.Exists(Path.Combine(game, "archive", "pc", "mod")));
    }

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

    private static (CleanupPlan Plan, string Source) CreateFileMetadataPlan(TemporaryDirectory temporary, string suffix)
    {
        (CleanupPlan plan, string source, _) = CreatePlan(temporary, "metadata payload", suffix);
        FileSecurity security = new FileInfo(source).GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadAttributes, AccessControlType.Allow));
        new FileInfo(source).SetAccessControl(security);
        File.SetAttributes(source, FileAttributes.Hidden | FileAttributes.Archive);
        CleanupEntry entry = WindowsCleanupProbe.ObserveFileForPlan(plan.GameRoot, source, "disposable file metadata");
        return (plan with { Entries = [entry] }, source);
    }

    private static async Task<(CleanupPlan Plan, string Source, string Destination)> CreatePublishedMetadataMismatch(TemporaryDirectory temporary)
    {
        (CleanupPlan plan, string source) = CreateFileMetadataPlan(temporary, "legacy-metadata");
        string destination = Path.Combine(plan.QuarantineRoot, "q-legacy-metadata", "files", plan.Entries.Single().RelativePath);
        RecordingCleanupFaults faults = new() { CrossVolume = true, ThrowAt = "entry:published", OnBoundary = boundary =>
        {
            if (boundary != "entry:published") return;
            FileSecurity security = new FileInfo(destination).GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, FileSystemRights.ReadPermissions, AccessControlType.Allow));
            new FileInfo(destination).SetAccessControl(security);
            File.SetAttributes(destination, FileAttributes.Archive);
        } };
        QuarantineResult interrupted = await ExecuteTestAsync(new QuarantineTransaction(faults), plan, "q-legacy-metadata");
        Assert.AreEqual(QuarantineTransactionState.Recoverable, interrupted.State, string.Join("; ", interrupted.Entries.Select(value => value.Reason)));
        Assert.IsTrue(File.Exists(source));
        Assert.IsTrue(File.Exists(destination));
        Assert.IsFalse(QuarantineTransaction.MetadataMatchesForTests(destination, CleanupEntryKind.File, plan.Entries.Single().Metadata));
        return (plan, source, destination);
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
