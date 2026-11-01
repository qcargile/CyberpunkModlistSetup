using ChromeBlood.Core.Preparation;
using ChromeBlood.Setup.ViewModels;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests;

[TestClass]
public sealed class CleanupScreenTests
{
    private static PreparationSnapshot Snapshot() => new()
    {
        SelectedGameRoot = @"C:\Games\Cyberpunk 2077",
        GameCandidates = [new(@"C:\Games\Cyberpunk 2077", "Steam", "2.5", "26161058")],
        ReadyForHandoff = true,
        GameFileReferenceMatched = true,
        GameFileScanComplete = true,
        CleanupItems = [new("extra.dll", "Not in the official file index"), new("settings.json", "User settings", "Settings")],
        Checks = [new("vcredist", "Runtime", "Installed", CheckState.Ready, true),
            new("longpaths", "Long paths", "Enabled", CheckState.Ready, true),
            new("wabbajack", "Wabbajack", "Verified", CheckState.Ready, true)]
    };

    private static PreparationViewModel Model(PreparationSnapshot snapshot, PreparationService? service = null)
    {
        PreparationViewModel model = new(service ?? new PreparationService());
        model.UpdateSnapshot(snapshot);
        model.PrepareCommand.Execute(null);
        return model;
    }

    [TestMethod]
    public void IntentionalDlssChangesCanBeKeptWithoutClaimingStockFiles()
    {
        using TemporaryDirectory temporary = new();
        PreparationService service = new(Path.Combine(temporary.Path, "state"));
        PreparationViewModel model = Model(Snapshot() with
        {
            ChangedOfficialFiles = 1,
            CleanupItems = [new(@"bin\x64\nvngx_dlss.dll", "Different from store", "Changed official")]
        }, service);
        Assert.AreEqual("Review game changes", model.CleanupTitle);
        model.FileChoices.Single().Keep = true;
        Assert.AreEqual(1, model.KeptFileCount);
        Assert.IsFalse(model.NeedsGameRepair);
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Keeping your changes", model.CleanupTitle);
        Assert.IsFalse(model.CleanGame);
        using System.Text.Json.JsonDocument choices = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(temporary.Path, "state", "locations.json")));
        Assert.AreEqual(@"bin\x64\nvngx_dlss.dll", choices.RootElement.GetProperty("keptGamePaths")[0].GetString());
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
    }

    [TestMethod]
    public void AutomaticallyRecognizedDlssFilesStayOutOfCleanupAndStockVerificationClaims()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            GameFileHashesChecked = true,
            CleanupItems = [new(@"bin\x64\nvngx_dlss.dll", "NVIDIA-signed replacement", "Preserved DLSS")],
            PreservedDlssFiles = 1
        });
        Assert.IsFalse(model.NeedsGameRepair);
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Kept", model.CleanupState);
        Assert.AreEqual("Keeping your changes", model.CleanupTitle);
        Assert.IsEmpty(model.SelectedCleanupItems);
        Assert.AreEqual(0, model.ExtraGameLocations);
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
    }

    [TestMethod]
    public void ReadOnlyPreferencesKeepTheSessionChoiceWithoutEscapingTheCommand()
    {
        using TemporaryDirectory temporary = new();
        string state = Path.Combine(temporary.Path, "state");
        PreparationService service = new(state);
        PreparationSnapshot snapshot = Snapshot();
        Assert.IsNull(service.RememberLocations(snapshot));
        string preferences = Path.Combine(state, "locations.json");
        string original = File.ReadAllText(preferences);
        File.SetAttributes(preferences, FileAttributes.ReadOnly);
        try
        {
            PreparationViewModel model = Model(snapshot, service);
            model.KeepMyChangesCommand.Execute(null);
            Assert.AreEqual(1, model.KeptFileCount);
            StringAssert.Contains(model.Message, "could not be saved");
            Assert.AreEqual(original, File.ReadAllText(preferences));
            Assert.IsEmpty(Directory.GetFiles(state, "*.tmp"));
            model.SkipSetupCommand.Execute(null);
            Assert.IsTrue(model.IsWabbajackPage);
        }
        finally { File.SetAttributes(preferences, FileAttributes.Normal); }
    }

    [TestMethod]
    public void KeepAllPreservesEveryReviewedGameChoiceAndCanBeUndoneIndividually()
    {
        using TemporaryDirectory temporary = new();
        PreparationViewModel model = Model(Snapshot() with
        {
            ChangedOfficialFiles = 1,
            CleanupItems = [new("extra.dll", "Added"), new(@"bin\x64\nvngx_dlss.dll", "Changed", "Changed official")]
        }, new PreparationService(Path.Combine(temporary.Path, "state")));
        model.KeepMyChangesCommand.Execute(null);
        Assert.AreEqual(2, model.KeptFileCount);
        Assert.AreEqual(0, model.ExtraGameLocations);
        model.FileChoices.Single(item => item.Path == "extra.dll").Keep = false;
        Assert.AreEqual(1, model.KeptFileCount);
        Assert.AreEqual(1, model.ExtraGameLocations);
    }

    [TestMethod]
    public void SetupCanBeSkippedWithoutAGameOrCompletedRequirements()
    {
        PreparationViewModel model = Model(new PreparationSnapshot
        {
            WabbajackDirectory = @"C:\Games\Wabbajack",
            Checks = [new("wabbajack", "Wabbajack", "Download it", CheckState.Needed, true)]
        });
        Assert.IsFalse(model.HasGame);
        Assert.IsTrue(model.SkipSetupCommand.CanExecute(null));
        model.SkipSetupCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
        Assert.AreEqual("Get Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.BrowseWabbajackCommand.CanExecute(null));
        Assert.IsTrue(model.UseExistingWabbajackCommand.CanExecute(null));
    }

    [TestMethod]
    public void ExistingTrustedWabbajackInAnUnsuitableFolderRequiresAnExplicitChoice()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            WabbajackDirectory = @"C:\Users\Player\Downloads\Wabbajack",
            Checks = [new("wabbajack", "Wabbajack", "Verified app", CheckState.Ready, true),
                new("wabbajackfolder", "App folder", @"Choose C:\Wabbajack instead", CheckState.Action, true)]
        });
        model.SkipSetupCommand.Execute(null);
        Assert.AreEqual("Change Wabbajack folder", model.PrimaryText);
        Assert.IsTrue(model.CanUseWabbajackFolderAnyway);
        model.AcceptWabbajackFolderCommand.Execute(null);
        Assert.AreEqual("Open C&B in Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.CanUseWabbajackFolderAnyway);
        model.UpdateSnapshot(Snapshot() with
        {
            WabbajackDirectory = @"C:\Users\Player\Documents\Wabbajack",
            Checks = [new("wabbajack", "Wabbajack", "Untrusted", CheckState.Action, true),
                new("wabbajackfolder", "App folder", "Change folder", CheckState.Action, true)]
        });
        Assert.IsFalse(model.AcceptWabbajackFolderCommand.CanExecute(null));
        Assert.AreEqual("Change Wabbajack folder", model.PrimaryText);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void ManualContinuationLeavesRecoveryVisibleAndReachesWabbajack(bool pending, bool unreadable)
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            PendingCleanup = pending, CleanupIssue = unreadable, ReadyForHandoff = false,
            CleanupFailure = "Backup metadata needs inspection.", RetainedBackupRoot = @"C:\Backups\saved-cleanup",
            ManualCleanupGuidance = "Keep the backup. Move only added mod files outside the game."
        });
        Assert.IsTrue(model.ShowManualCleanup);
        Assert.IsTrue(model.WabbajackCommand.CanExecute(null));
        model.ContinueWithoutCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
        Assert.IsTrue(model.CleanupDeferred);
        Assert.AreEqual(pending, model.PendingCleanup);
        Assert.IsTrue(model.HasCleanupFailure);
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual(@"C:\Backups\saved-cleanup", model.BackupRoot);
        StringAssert.Contains(model.HandoffDetail, "Setup is optional");
        model.BackCommand.Execute(null);
        Assert.IsTrue(model.ShowManualCleanup);
        Assert.IsTrue(model.ReviewCleanupCommand.CanExecute(null) == !unreadable);
    }

    [TestMethod]
    public void ManualCleanupCannotClearRequiredComponentsOrKnownOfficialDamage()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            PendingCleanup = true, ChangedOfficialFiles = 1, ReadyForHandoff = false,
            Checks = [new("longpaths", "Windows long paths", "Enable manually", CheckState.Needed, true)]
        });
        model.ContinueWithoutCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.CleanGame);
        Assert.IsTrue(model.NeedsGameRepair);
        Assert.IsFalse(model.ReadyForHandoff);
        Assert.IsTrue(model.WabbajackCommand.CanExecute(null));
        Assert.IsTrue(model.HasSelectedWork);
        StringAssert.Contains(model.SelectedActions.Single(), "Windows long paths");
    }

    [TestMethod]
    public void OtherPreparationRemainsReachableWhileCleanupIsPending()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            PendingCleanup = true, ReadyForHandoff = false,
            Checks = [new("wabbajack", "Download Wabbajack", "Manual instructions", CheckState.Needed, true)]
        });
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.CleanGame);
        model.BackCommand.Execute(null);
        model.ReviewPreparationCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.CleanGame);
        Assert.IsTrue(model.PendingCleanup);
        Assert.AreEqual("Download Wabbajack", model.SelectedActions.Single());
    }

    [TestMethod]
    public void AnUnrelatedPreparationFailureDoesNotBecomeACleanupFailure()
    {
        PreparationViewModel model = Model(Snapshot());
        model.ApplyPreparationResult(new(Snapshot() with { PendingCleanup = true, ReadyForHandoff = false },
            false, false, "Wabbajack could not download."));
        Assert.IsFalse(model.HasCleanupFailure);
        Assert.IsTrue(model.PendingCleanup);
        StringAssert.Contains(model.Message, "Wabbajack");
    }

    [TestMethod]
    public void CleanupShortcutReviewsBeforeAnyWritesAndKeepsOptionalChoicesOff()
    {
        PreparationViewModel model = Model(Snapshot());
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsTrue(model.CleanGame);
        Assert.IsFalse(model.ResetSettings);
        Assert.IsFalse(model.ClearDiagnostics);
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual("Back up and clean", model.PrimaryText);
        Assert.HasCount(1, model.SelectedCleanupItems);
    }

    [TestMethod]
    public void SettingsOnlySelectionStillRequiresReviewWithoutSelectingGameFiles()
    {
        PreparationViewModel model = Model(Snapshot());
        model.ResetSettings = true;
        Assert.IsTrue(model.HasSelectedWork);
        model.ContinueWithoutCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.CleanGame);
        Assert.AreEqual("Back up and clean", model.PrimaryText);
        Assert.AreEqual("Settings", model.SelectedCleanupItems.Single().Scope);
        Assert.HasCount(1, model.SelectedActions);
    }

    [TestMethod]
    public void PendingCleanupKeepsRecordedChoicesAndAllowsReadOnlyVerification()
    {
        PreparationViewModel model = Model(Snapshot() with { PendingCleanup = true, ReadyForHandoff = false });
        model.ReviewCleanupCommand.Execute(null);
        model.CleanGame = false;
        model.ResetSettings = true;
        model.ClearDiagnostics = true;
        Assert.IsTrue(model.CleanGame);
        Assert.IsFalse(model.ResetSettings);
        Assert.IsFalse(model.ClearDiagnostics);
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
        Assert.AreEqual("Resume cleanup", model.PrimaryText);
        Assert.HasCount(1, model.SelectedActions);
    }

    [TestMethod]
    public void FastFindingsDoNotClaimContentVerificationAndHashFindingsRequireRepair()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupReferenceDetail = "Known-path cleanup only", CleanupBytes = 4096, PlannedBackupRoot = @"C:\Backups\Cyberpunk" });
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
        StringAssert.Contains(model.GameFileCheckDetail, "have not been verified");
        Assert.AreEqual("Known-path cleanup only", model.CleanupReferenceDetail);
        StringAssert.Contains(model.BackupSizeDetail, 4096.ToString("N0"));
        Assert.IsTrue(model.HasPlannedBackup);
        Assert.AreEqual(@"C:\Backups\Cyberpunk", model.PlannedBackupRoot);
        Assert.IsFalse(model.NeedsGameRepair);
        model.UpdateSnapshot(Snapshot() with { GameFileHashesChecked = true, ChangedOfficialFiles = 1, MissingOfficialFiles = 2, UnreadableGameFiles = 3 });
        StringAssert.Contains(model.GameFileCheckDetail, "1 changed, 2 missing, 3 unreadable");
        Assert.IsTrue(model.NeedsGameRepair);
    }

    [TestMethod]
    public void ChoiceChangesNotifyThePrimaryAction()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true });
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
        Assert.AreEqual("Open C&B in Wabbajack", model.PrimaryText);
        model.PrepareCommand.Execute(null);
        List<string?> changes = [];
        model.PropertyChanged += (_, change) => changes.Add(change.PropertyName);
        model.ClearDiagnostics = true;
        Assert.IsTrue(changes.Any(name => name is "" or nameof(model.PrimaryText)));
        Assert.AreEqual("Review selected actions", model.PrimaryText);
    }

    [TestMethod]
    public void CleanupIsOptionalAndExplicitReviewPrecedesWrites()
    {
        PreparationViewModel model = Model(Snapshot());
        Assert.AreEqual("Extra files found", model.CleanupTitle);
        Assert.AreEqual("Dirty", model.CleanupState);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.ShowCleanup);
        Assert.IsFalse(model.ShowPageHeading);
        Assert.IsFalse(model.CleanGame);
        Assert.IsTrue(model.ShowSecondaryContinue);
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsTrue(model.CleanGame);
        Assert.IsFalse(model.IsBusy);
        Assert.IsFalse(model.ShowSecondaryContinue);
        Assert.AreEqual("Back up and clean", model.PrimaryText);
        Assert.IsFalse(model.HasAdditionalActions);
    }

    [TestMethod]
    public void VerifiedCleanGameHasAnExplicitPositiveOutcomeAndContinueAction()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true,
            GameFileVerifiedAt = DateTimeOffset.UtcNow });
        Assert.IsTrue(model.GameIsClean);
        Assert.AreEqual("Clean", model.CleanupState);
        Assert.AreEqual("Your game is clean", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.ShowSecondaryContinue);
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
    }

    [TestMethod]
    public void MetadataScanOffersVerificationWithoutRequiringIt()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [] });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Unverified", model.CleanupState);
        Assert.AreEqual("Ready to check your game", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
        Assert.AreEqual("Skip cleanup", model.SecondaryContinueText);
        model.ContinueWithoutCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
        Assert.IsFalse(model.GameIsClean);
    }

    [TestMethod]
    public void UnsupportedReferenceKeepsTheLimitedResultVisibleWithoutClaimingClean()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [], GameFileReferenceMatched = false,
            GameFileScanComplete = false, CleanupReferenceDetail = "Known mod locations only" });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Unsupported", model.CleanupState);
        Assert.AreEqual("Full check unavailable", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.AreEqual("Known mod locations only", model.CleanupReferenceDetail);
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
    }

    [TestMethod]
    public void ChangedOfficialFilesOfferRepairWithoutBlockingContinuation()
    {
        PreparationViewModel model = Model(Snapshot() with { ChangedOfficialFiles = 1, GameFileHashesChecked = true });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Repair", model.CleanupState);
        Assert.AreEqual("Review game changes", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
        Assert.IsTrue(model.ContinueWithoutCleanupCommand.CanExecute(null));
        Assert.IsTrue(model.ShowSecondaryContinue);
        Assert.IsTrue(model.ReviewCleanupCommand.CanExecute(null));
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsTrue(model.CleanGame);
        Assert.AreEqual("Back up and clean", model.PrimaryText);
        Assert.IsTrue(model.IsReview);
        model.BackCommand.Execute(null);
        model.UpdateSnapshot(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true });
        Assert.IsTrue(model.GameIsClean);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
    }

    [TestMethod]
    public void UnreadableOrIncompleteScanOffersAnotherCheckAndBlocksCleanup()
    {
        PreparationViewModel model = Model(Snapshot() with { GameFileHashesChecked = true, UnreadableGameFiles = 1 });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.GameIsClean);
        Assert.IsFalse(model.ReviewCleanupCommand.CanExecute(null));
        Assert.IsTrue(model.ContinueWithoutCleanupCommand.CanExecute(null));
        Assert.IsTrue(model.RefreshCommand.CanExecute(null));
        model.UpdateSnapshot(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true, GameFileScanComplete = false });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.IsFalse(model.GameIsClean);
        model.UpdateSnapshot(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true, CleanupIssue = true });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.IsFalse(model.GameIsClean);
        model.UpdateSnapshot(Snapshot() with { ChangedOfficialFiles = 1, MissingOfficialFiles = 1, UnreadableGameFiles = 1 });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.ReviewCleanupCommand.CanExecute(null));
    }

    [TestMethod]
    public void PendingCleanupRemainsVisibleAndRecordedResumeIsExplicit()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true,
            PendingCleanup = true, ReadyForHandoff = false });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Pending", model.CleanupState);
        Assert.AreEqual("Cleanup is unfinished", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.ContinueWithoutCleanupCommand.CanExecute(null));
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsTrue(model.CleanGame);
        Assert.IsFalse(model.IsBusy);
    }

    [TestMethod]
    public void FailedCleanupShowsLatestErrorAndKeepsRetryUnderReview()
    {
        PreparationViewModel model = Model(Snapshot());
        PreparationSnapshot pending = Snapshot() with { PendingCleanup = true, ReadyForHandoff = false,
            CleanupFailure = "Cannot move archive\\locked.archive: Windows error 32." };
        model.ApplyPreparationResult(new(pending, false, false, "Cannot move archive\\locked.archive: Windows error 32.", @"C:\Backups\Cyberpunk"));
        Assert.IsTrue(model.HasCleanupFailure);
        Assert.AreEqual("Cleanup is unfinished", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        StringAssert.Contains(model.CleanupFailure, "locked.archive");
        Assert.IsTrue(model.HasBackup);
        Assert.AreEqual(@"C:\Backups\Cyberpunk", model.BackupRoot);
        Assert.IsTrue(model.WabbajackCommand.CanExecute(null));
        Assert.IsTrue(model.ContinueWithoutCleanupCommand.CanExecute(null));
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual("Retry the saved cleanup", model.ReviewTitle);
        model.ApplyPreparationResult(new(pending with { CleanupFailure = "Cannot move archive\\locked.archive: insufficient space." }, false, false, "Cannot move archive\\locked.archive: insufficient space."));
        Assert.IsFalse(model.IsReview);
        StringAssert.Contains(model.CleanupFailure, "insufficient space");
        Assert.IsFalse(model.CleanupFailure.Contains("error 32", StringComparison.Ordinal));
        Assert.AreEqual(@"C:\Backups\Cyberpunk", model.BackupRoot);
    }

    [TestMethod]
    public void ReopenedCleanupRetainsItsFailureUntilRecoveryCompletes()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            PendingCleanup = true, ReadyForHandoff = false,
            CleanupFailure = "Cannot move CleanSlate_Quarantine\\old: access denied. Windows error 5."
        });
        Assert.IsTrue(model.HasCleanupFailure);
        StringAssert.Contains(model.CleanupFailure, "CleanSlate_Quarantine");
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        model.ReviewCleanupCommand.Execute(null);
        model.ApplyPreparationResult(new(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true }, true, false, "Cleanup finished."));
        Assert.IsFalse(model.HasCleanupFailure);
        Assert.AreEqual("", model.CleanupFailure);
        Assert.IsFalse(model.CleanGame);
        Assert.AreEqual("Your game is clean", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.WabbajackCommand.CanExecute(null));
    }

    [TestMethod]
    public void CancelledCleanupRemainsResumableWithoutClaimingFailureOrCompletion()
    {
        PreparationViewModel model = Model(Snapshot());
        model.ApplyPreparationResult(new(Snapshot() with { PendingCleanup = true, ReadyForHandoff = false },
            false, true, "Preparation stopped. Preserved files are retained."));
        Assert.IsFalse(model.HasCleanupFailure);
        Assert.AreEqual("Cleanup is unfinished", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsTrue(model.WabbajackCommand.CanExecute(null));
        Assert.IsTrue(model.ContinueWithoutCleanupCommand.CanExecute(null));
    }

    [TestMethod]
    public void UnreadableRecoveryStateStopsCleanupButAllowsContinuation()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            CleanupIssue = true, ReadyForHandoff = false,
            CleanupFailure = "Cleanup could not be inspected: The cleanup WAL is invalid."
        });
        Assert.IsTrue(model.HasCleanupFailure);
        StringAssert.Contains(model.CleanupFailure, "WAL is invalid");
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.CleanupDetail.Contains("Close the game", StringComparison.Ordinal));
        Assert.IsFalse(model.ReviewCleanupCommand.CanExecute(null));
        Assert.IsTrue(model.WabbajackCommand.CanExecute(null));
        Assert.IsTrue(model.ContinueWithoutCleanupCommand.CanExecute(null));
    }

    [TestMethod]
    public void ChoosingAnotherInstallationDoesNotCarryItsPreviousCleanupError()
    {
        PreparationViewModel model = Model(Snapshot() with { PendingCleanup = true, ReadyForHandoff = false, CleanupFailure = "Original folder failed." });
        model.UpdateSnapshot(Snapshot() with { SelectedGameRoot = @"D:\Games\Cyberpunk 2077" });
        Assert.IsFalse(model.HasCleanupFailure);
        Assert.AreEqual("", model.CleanupFailure);
    }

    [TestMethod]
    public void MissingComponentsStaySelectedAndManualRequirementsStaySeparate()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            Checks = [new("vcredist", "Runtime", "Installed", CheckState.Ready, true),
                new("longpaths", "Long paths", "Enable long paths", CheckState.Needed, true),
                new("directx", "DirectX", "Optional", CheckState.Optional, false),
                new("redmod", "REDmod", "Install the DLC", CheckState.Action, true)]
        });
        Assert.HasCount(1, model.ManualChecks);
        Assert.AreEqual("REDmod", model.ManualChecks[0].Title);
        Assert.IsTrue(model.NeedsLongPaths);
        Assert.IsTrue(model.EnableLongPaths);
        Assert.IsFalse(model.NeedsRuntimes);
        Assert.IsTrue(model.ShowDirectXChoice);
        Assert.IsFalse(model.LegacyDirectX);
        Assert.HasCount(1, model.ReadyChecks);
        Assert.IsTrue(model.HasManualChecks);
        Assert.IsTrue(model.HasReadyChecks);
    }

    [TestMethod]
    public void RefreshRemovesSatisfiedChoicesWithoutOverridingUserSelections()
    {
        PreparationSnapshot snapshot = Snapshot() with
        {
            Checks = [new("vcredist", "Runtime", "Install", CheckState.Needed, true),
                new("longpaths", "Long paths", "Enable", CheckState.Needed, true),
                new("wabbajack", "Wabbajack", "Download", CheckState.Needed, true),
                new("steamupdate", "Steam", "Launch only", CheckState.Optional, false),
                new("directx", "DirectX", "Installed", CheckState.Ready, false)]
        };
        PreparationViewModel model = Model(snapshot);
        Assert.IsTrue(model.InstallRuntimes && model.EnableLongPaths && model.DownloadWabbajack);
        Assert.IsTrue(model.HasAutomaticPreparation);
        Assert.IsFalse(model.AutomaticPreparationReady);
        Assert.IsTrue(model.ShowSteamChoice);
        Assert.IsFalse(model.ShowDirectXChoice);
        model.EnableLongPaths = false;
        model.ResetSettings = true;
        model.UpdateSnapshot(snapshot);
        Assert.IsFalse(model.EnableLongPaths);
        Assert.IsTrue(model.ResetSettings);
        model.ReviewCleanupCommand.Execute(null);
        CollectionAssert.AreEqual(new[] { "Runtime", "Wabbajack", "Back up and reset Cyberpunk settings and user mod settings. Saves are kept." }, model.ReviewedPreparationActions.ToArray());
        Assert.IsFalse(model.ShowChecklist);
        model.BackCommand.Execute(null);
        model.UpdateSnapshot(Snapshot());
        Assert.IsFalse(model.HasAutomaticPreparation);
        Assert.IsTrue(model.AutomaticPreparationReady);
        Assert.IsFalse(model.ShowSteamChoice);
        Assert.IsFalse(model.ShowDirectXChoice);
        Assert.IsTrue(model.ShowChecklist);
    }
}
