using ChromeBlood.Core.Preparation;
using ChromeBlood.Setup.ViewModels;

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

    private static PreparationViewModel Model(PreparationSnapshot snapshot)
    {
        PreparationViewModel model = new(new PreparationService());
        model.UpdateSnapshot(snapshot);
        model.PrepareCommand.Execute(null);
        return model;
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
    public void PendingCleanupKeepsRecordedChoicesAndDisablesNewVerification()
    {
        PreparationViewModel model = Model(Snapshot() with { PendingCleanup = true, ReadyForHandoff = false });
        model.ReviewCleanupCommand.Execute(null);
        model.CleanGame = false;
        model.ResetSettings = true;
        model.ClearDiagnostics = true;
        Assert.IsTrue(model.CleanGame);
        Assert.IsFalse(model.ResetSettings);
        Assert.IsFalse(model.ClearDiagnostics);
        Assert.IsFalse(model.CheckGameFilesCommand.CanExecute(null));
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
        Assert.AreEqual("Review preparation", model.PrimaryText);
    }

    [TestMethod]
    public void CleanupDominatesTheFirstPageAndOpensReviewBeforeWriting()
    {
        PreparationViewModel model = Model(Snapshot());
        Assert.AreEqual("Your game needs cleanup", model.CleanupTitle);
        Assert.AreEqual("Dirty", model.CleanupState);
        Assert.AreEqual("Clean up game", model.PrimaryText);
        Assert.IsTrue(model.ShowCleanup);
        Assert.IsFalse(model.ShowPageHeading);
        Assert.IsFalse(model.CleanGame);
        Assert.IsTrue(model.ShowSecondaryContinue);
        model.PrimaryCommand.Execute(null);
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
        Assert.AreEqual("✓", model.CleanupSymbol);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.ShowSecondaryContinue);
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
    }

    [TestMethod]
    public void MetadataScanWithoutExtraFilesStillRequiresVerification()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [] });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Unverified", model.CleanupState);
        Assert.AreEqual("Ready to check your game", model.CleanupTitle);
        Assert.AreEqual("Verify game files", model.PrimaryText);
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
        Assert.AreEqual("Continue without verification", model.SecondaryContinueText);
        model.ContinueWithoutCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
        Assert.IsFalse(model.GameIsClean);
    }

    [TestMethod]
    public void RecognizedDlssFilesRemainVisibleWithoutRepairOrCleanup()
    {
        PreparationSnapshot snapshot = Snapshot() with
        {
            CleanupItems = [new(@"bin\x64\nvngx_dlss.dll", "NVIDIA-signed replacement", "Preserved DLSS")],
            PreservedDlssFiles = 1
        };
        PreparationViewModel model = Model(snapshot);
        Assert.IsFalse(model.NeedsGameRepair);
        Assert.AreEqual(0, model.ExtraGameLocations);
        Assert.IsEmpty(model.SelectedCleanupItems);
        Assert.AreEqual("Unverified", model.CleanupState);
        StringAssert.Contains(model.CleanupDetail, "Custom DLSS files");
        model.UpdateSnapshot(snapshot with { GameFileHashesChecked = true });
        Assert.AreEqual("Clean", model.CleanupState);
        Assert.AreEqual("Your game is ready", model.CleanupTitle);
        Assert.AreEqual("Continue to Wabbajack", model.PrimaryText);
        Assert.IsFalse(model.CleanupDetail.Contains("Official files verified", StringComparison.Ordinal));
        model.UpdateSnapshot(snapshot with { MissingOfficialFiles = 1, ReadyForHandoff = false });
        Assert.AreEqual("Repair", model.CleanupState);
        Assert.IsTrue(model.NeedsGameRepair);
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
    public void OfficialFileDamageRequiresRepairAndAllowsVerificationAfterRepair()
    {
        PreparationViewModel model = Model(Snapshot() with { ChangedOfficialFiles = 1, GameFileHashesChecked = true });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Repair", model.CleanupState);
        Assert.AreEqual("Your game needs repair", model.CleanupTitle);
        Assert.AreEqual("Verify again", model.PrimaryText);
        Assert.IsTrue(model.CheckGameFilesCommand.CanExecute(null));
        Assert.IsFalse(model.ContinueWithoutCleanupCommand.CanExecute(null));
        Assert.IsFalse(model.ShowSecondaryContinue);
        Assert.IsTrue(model.ShowOfficialBackupAction);
        model.ReviewCleanupCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsTrue(model.CleanGame);
        Assert.AreEqual("Back up and clean", model.PrimaryText);
        Assert.IsFalse(model.ShowOfficialBackupAction);
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
        Assert.AreEqual("Check again", model.PrimaryText);
        Assert.IsFalse(model.GameIsClean);
        Assert.IsFalse(model.ReviewCleanupCommand.CanExecute(null));
        Assert.IsFalse(model.ContinueWithoutCleanupCommand.CanExecute(null));
        Assert.IsTrue(model.RefreshCommand.CanExecute(null));
        model.UpdateSnapshot(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true, GameFileScanComplete = false });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.IsFalse(model.GameIsClean);
        model.UpdateSnapshot(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true, CleanupIssue = true });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.IsFalse(model.GameIsClean);
        model.UpdateSnapshot(Snapshot() with { ChangedOfficialFiles = 1, MissingOfficialFiles = 1, UnreadableGameFiles = 1 });
        Assert.AreEqual("Blocked", model.CleanupState);
        Assert.AreEqual("Check again", model.PrimaryText);
        Assert.IsFalse(model.ShowOfficialBackupAction);
    }

    [TestMethod]
    public void PendingCleanupOverridesOtherwiseCleanEvidenceAndRequiresRecordedResume()
    {
        PreparationViewModel model = Model(Snapshot() with { CleanupItems = [], GameFileHashesChecked = true,
            PendingCleanup = true, ReadyForHandoff = false });
        Assert.IsFalse(model.GameIsClean);
        Assert.AreEqual("Pending", model.CleanupState);
        Assert.AreEqual("Finish your cleanup", model.CleanupTitle);
        Assert.AreEqual("Resume cleanup", model.PrimaryText);
        Assert.IsFalse(model.ContinueWithoutCleanupCommand.CanExecute(null));
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsTrue(model.CleanGame);
        Assert.IsFalse(model.IsBusy);
    }

    [TestMethod]
    public void FailedCleanupShowsLatestErrorAndKeepsRetryUnderReview()
    {
        PreparationViewModel model = Model(Snapshot());
        PreparationSnapshot pending = Snapshot() with { PendingCleanup = true, ReadyForHandoff = false };
        model.ApplyPreparationResult(new(pending, false, false, "Cannot move archive\\locked.archive: Windows error 32.", @"C:\Backups\Cyberpunk"));
        Assert.IsTrue(model.HasCleanupFailure);
        Assert.AreEqual("Cleanup needs attention", model.CleanupTitle);
        Assert.AreEqual("Retry cleanup", model.PrimaryText);
        StringAssert.Contains(model.CleanupFailure, "locked.archive");
        Assert.IsTrue(model.HasBackup);
        Assert.AreEqual(@"C:\Backups\Cyberpunk", model.BackupRoot);
        Assert.IsFalse(model.WabbajackCommand.CanExecute(null));
        Assert.IsFalse(model.ContinueWithoutCleanupCommand.CanExecute(null));
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.IsBusy);
        Assert.AreEqual("Retry the saved cleanup", model.ReviewTitle);
        model.ApplyPreparationResult(new(pending, false, false, "Cannot move archive\\locked.archive: insufficient space."));
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
        Assert.AreEqual("Retry cleanup", model.PrimaryText);
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
        Assert.AreEqual("Finish your cleanup", model.CleanupTitle);
        Assert.AreEqual("Resume cleanup", model.PrimaryText);
        Assert.IsFalse(model.WabbajackCommand.CanExecute(null));
        Assert.IsFalse(model.ContinueWithoutCleanupCommand.CanExecute(null));
    }

    [TestMethod]
    public void UnreadableRecoveryStateShowsItsErrorWithoutAllowingCleanupOrHandoff()
    {
        PreparationViewModel model = Model(Snapshot() with
        {
            CleanupIssue = true, ReadyForHandoff = false,
            CleanupFailure = "Cleanup could not be inspected: The cleanup WAL is invalid."
        });
        Assert.IsTrue(model.HasCleanupFailure);
        StringAssert.Contains(model.CleanupFailure, "WAL is invalid");
        Assert.AreEqual("Check again", model.PrimaryText);
        Assert.IsFalse(model.CleanupDetail.Contains("Close the game", StringComparison.Ordinal));
        Assert.IsFalse(model.ReviewCleanupCommand.CanExecute(null));
        Assert.IsFalse(model.WabbajackCommand.CanExecute(null));
        Assert.IsFalse(model.ContinueWithoutCleanupCommand.CanExecute(null));
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
