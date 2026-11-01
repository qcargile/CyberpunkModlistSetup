using System.Text.Json;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Parity;
using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Vanilla;
using ChromeBlood.Core.Preparation;
using ChromeBlood.Testing;
using ChromeBlood.Setup.ViewModels;

namespace ChromeBlood.Setup.Tests;

[TestClass]
public sealed class PreparationTests
{
    [TestMethod]
    [DataRow(VanillaEntryStatus.VendorReplacement)]
    [DataRow(VanillaEntryStatus.OfficialUnverified)]
    public void RecognizedDlssReplacementOrRestoredOriginalClearsItsOldRepairFindingButKeepsUnrelatedDamage(VanillaEntryStatus current)
    {
        PreparationService service = new();
        CleanupPreparationPreview previous = new([
            new("Changed official", @"bin\x64\nvngx_dlss.dll", "Changed", CleanupEntryStatus.Available),
            new("Changed official", @"bin\x64\Cyberpunk2077.exe", "Changed", CleanupEntryStatus.Available)], [], "backup", "settings");
        const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        typeof(PreparationService).GetField("_verifiedGameRoot", fields)!.SetValue(service, "game");
        typeof(PreparationService).GetField("_verifiedCleanup", fields)!.SetValue(service, previous);
        VanillaScanResult scan = new(new(true, "Matched"), "reference", [
            new(@"bin\x64\nvngx_dlss.dll", false, current, 5, 5, "abcdef", "abcdef"),
            new(@"bin\x64\nvngx_dlss.dll.dlsss", false, VanillaEntryStatus.VendorBackup)], false, true);
        CleanupPreparationPreview preview = new(CleanupPreparationService.OfficialFindings(scan).ToArray(), [], "backup", "settings") { GameFiles = scan };
        PreparationSnapshot snapshot = (PreparationSnapshot)typeof(PreparationService).GetMethod("WithCleanup", fields)!
            .Invoke(service, [new PreparationSnapshot { SelectedGameRoot = @"C:\Games\Cyberpunk 2077" }, preview, "game"])!;
        Assert.AreEqual(current == VanillaEntryStatus.VendorReplacement ? 2 : 1, snapshot.PreservedDlssFiles);
        Assert.AreEqual(1, snapshot.ChangedOfficialFiles);
        Assert.IsFalse(snapshot.ReadyForHandoff);
        Assert.AreEqual(@"bin\x64\Cyberpunk2077.exe", snapshot.CleanupItems.Single(item => item.Scope == "Changed official").Path);
        typeof(PreparationService).GetField("_verifiedCleanup", fields)!.SetValue(service, null);
        snapshot = (PreparationSnapshot)typeof(PreparationService).GetMethod("WithCleanup", fields)!
            .Invoke(service, [new PreparationSnapshot { SelectedGameRoot = @"C:\Games\Cyberpunk 2077" }, preview, "game"])!;
        Assert.IsTrue(snapshot.ReadyForHandoff);
        Assert.AreEqual(0L, snapshot.CleanupBytes);
    }

    [TestMethod]
    public async Task ChosenLocationsAndKeptPathsSurviveReopeningWithoutChangingTheRequest()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "bin", "x64"));
        File.WriteAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe"), "test executable");
        string state = Path.Combine(temporary.Path, "state");
        string chosenApp = Path.Combine(temporary.Path, "chosen-app");
        PreparationService first = new(state);
        PreparationSnapshot selected = new()
        {
            SelectedGameRoot = game,
            WabbajackDirectory = chosenApp,
            GameBackupRoot = @"D:\ChosenBackups",
            SettingsBackupRoot = @"C:\ChosenSettingsBackups",
            KeptGamePaths = [@"bin\x64\nvngx_dlss.dll"]
        };
        Assert.IsNull(first.RememberLocations(selected));
        PreparationSnapshot reopened = await new PreparationService(state).ScanAsync(null, null);
        Assert.AreEqual(game, reopened.SelectedGameRoot);
        Assert.AreEqual(chosenApp, reopened.WabbajackDirectory);
        Assert.AreEqual(selected.GameBackupRoot, reopened.GameBackupRoot);
        Assert.AreEqual(selected.SettingsBackupRoot, reopened.SettingsBackupRoot);
        CollectionAssert.AreEqual(selected.KeptGamePaths.ToArray(), reopened.KeptGamePaths.ToArray());
        string otherGame = Path.Combine(temporary.Path, "other-game");
        Directory.CreateDirectory(Path.Combine(otherGame, "bin", "x64"));
        File.WriteAllText(Path.Combine(otherGame, "bin", "x64", "Cyberpunk2077.exe"), "other executable");
        PreparationSnapshot other = await new PreparationService(state).ScanAsync(otherGame, chosenApp);
        Assert.HasCount(0, other.KeptGamePaths);
        Assert.AreNotEqual(selected.GameBackupRoot, other.GameBackupRoot);
    }

    [TestMethod]
    public async Task FailedActionRetainsItsErrorAndAllowsTheFollowingAction()
    {
        List<string> failures = [];
        bool downloaded = false;
        Assert.IsFalse(await PreparationService.RunPreparationActionAsync("Cleanup", () => throw new UnauthorizedAccessException("Access denied: locked.archive"), failures, CancellationToken.None));
        Assert.IsTrue(await PreparationService.RunPreparationActionAsync("Wabbajack", () => { downloaded = true; return Task.CompletedTask; }, failures, CancellationToken.None));
        Assert.IsTrue(downloaded);
        Assert.HasCount(1, failures);
        StringAssert.Contains(failures[0], "locked.archive");
    }

    [TestMethod]
    public async Task UserCancellationStopsTheNextPreparationAction()
    {
        List<string> failures = [];
        using CancellationTokenSource cancellation = new();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PreparationService.RunPreparationActionAsync("Cleanup", () =>
        { cancellation.Cancel(); return Task.CompletedTask; }, failures, cancellation.Token));
        bool nextStarted = false;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PreparationService.RunPreparationActionAsync("Wabbajack", () =>
        { nextStarted = true; return Task.CompletedTask; }, failures, cancellation.Token));
        Assert.IsFalse(nextStarted);
        Assert.HasCount(0, failures);
    }

    [TestMethod]
    public async Task ALaunchedVendorFinishesBeforeARecordingFailureReturns()
    {
        string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        System.Diagnostics.ProcessStartInfo start = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("127.0.0.1");
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("2");
        using System.Diagnostics.Process child = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("The disposable vendor process did not start.");
        await Assert.ThrowsExactlyAsync<IOException>(() => PreparationPackages.DrainVendorAsync(child, active =>
        {
            Assert.IsFalse(active.HasExited, "The recording fault must occur while the process is active.");
            throw new IOException("The process record could not be saved.");
        }));
        Assert.IsTrue(child.HasExited, "The operation returned while its process was still running.");
    }

    private const string Release = """
        {"tag_name":"4.3.2.1","draft":false,"prerelease":false,"assets":[
          {"name":"Wabbajack.exe","browser_download_url":"https://github.com/wabbajack-tools/wabbajack/releases/download/4.3.2.1/Wabbajack.exe",
           "digest":"sha256:25ab43e6d3ea63bd329d0646e0303795b6e405dc1263f6677c794d3b1c9d2280"}
        ]}
        """;

    [TestMethod]
    public void EmbeddedCleanupPolicyRetainsItsRecordedIdentity()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.LoadEmbedded();
        Assert.HasCount(1, corpus.Fixtures);
        Assert.AreEqual("full-clean", corpus.Fixtures[0].Case.Id);
        Assert.AreEqual("15d2884c429e9a9dec6d1d62e888d7c64615a8c621abe85e4ea697b743291ebe", corpus.Fixtures[0].Case.Sha256);
        Assert.AreEqual(LegacyParityCommand.FullClean, corpus.Fixtures[0].Case.Command);
    }

    [TestMethod]
    public void WabbajackReleaseBindsTheExactOfficialAssetAndDigest()
    {
        DownloadRequest request = PreparationPackages.ParseWabbajackRelease(Release);
        Assert.AreEqual("25ab43e6d3ea63bd329d0646e0303795b6e405dc1263f6677c794d3b1c9d2280", request.ExpectedSha256);
        Assert.AreEqual("https://github.com/wabbajack-tools/wabbajack/releases/download/4.3.2.1/Wabbajack.exe", request.Source.AbsoluteUri);
        Assert.AreEqual(PreparationPackages.WabbajackSigner, request.ExpectedSigner);
        Assert.IsTrue(request.RequireTrustedAuthenticode);
    }

    [TestMethod]
    public void WabbajackReleaseRejectsReplacementUrlsAndMissingDigests()
    {
        Assert.ThrowsExactly<IOException>(() => PreparationPackages.ParseWabbajackRelease(
            Release.Replace("https://github.com/wabbajack-tools/", "https://example.invalid/wabbajack-tools/", StringComparison.Ordinal)));
        Assert.ThrowsExactly<IOException>(() => PreparationPackages.ParseWabbajackRelease(
            Release.Replace("sha256:25ab43e6d3ea63bd329d0646e0303795b6e405dc1263f6677c794d3b1c9d2280", "unknown", StringComparison.Ordinal)));
        Assert.ThrowsExactly<IOException>(() => PreparationPackages.ParseWabbajackRelease(
            Release.Replace("\"prerelease\":false", "\"prerelease\":true", StringComparison.Ordinal)));
        Assert.ThrowsExactly<IOException>(() => PreparationPackages.ParseWabbajackRelease(
            Release.Replace("\"name\":\"Wabbajack.exe\"", "\"name\":\"Other.exe\"", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void NativeVerifierRefusesAnUnsignedInstaller()
    {
        using TemporaryDirectory temporary = new();
        string file = Path.Combine(temporary.Path, "unverified.exe");
        File.WriteAllText(file, "unverified executable");
        AuthenticodeVerdict result = new AuthenticodeVerifier().Verify(file, PreparationPackages.WabbajackSigner);
        Assert.AreNotEqual(AuthenticodeStatus.Trusted, result.Status);
    }

    [TestMethod]
    public async Task AdministratorEntryRejectsUnapprovedOperationsBeforeAnyWrite()
    {
        Assert.AreEqual(87, await PreparationPackages.RunPrerequisiteAsync("vcredist-x64"));
        Assert.AreEqual(87, await PreparationPackages.RunPrerequisiteAsync("delete-game"));
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => PreparationPackages.RunPrerequisiteAsync("longpaths", cancelled.Token));
    }

    [TestMethod]
    public void SteamPreferenceEditorPreservesOtherManifestValuesAndIsRepeatable()
    {
        byte[] original = System.Text.Encoding.UTF8.GetBytes(
            "\"AppState\"\n{\n\"appid\" \"1091500\"\n\"installdir\" \"Cyberpunk 2077\"\n\"AutoUpdateBehavior\" \"0\"\n\"LastPlayed\" \"10\"\n}\n");
        SteamManifestEdit first = SteamManifestEditor.SetLaunchOnly(original);
        Assert.AreEqual("0", first.OriginalValue);
        string changed = System.Text.Encoding.UTF8.GetString(first.UpdatedBytes);
        Assert.AreEqual(System.Text.Encoding.UTF8.GetString(original).Replace("\"AutoUpdateBehavior\" \"0\"", "\"AutoUpdateBehavior\" \"1\"", StringComparison.Ordinal), changed);
        SteamManifestEdit repeat = SteamManifestEditor.SetLaunchOnly(first.UpdatedBytes);
        Assert.AreEqual("1", repeat.OriginalValue);
        CollectionAssert.AreEqual(first.UpdatedBytes, repeat.UpdatedBytes);
    }
}

[TestClass]
public sealed class PreparationViewModelTests
{
    private static PreparationViewModel ReadyModel(string optionalId, CheckState optionalState)
    {
        PreparationViewModel model = new(new PreparationService());
        model.UpdateSnapshot(new PreparationSnapshot
        {
            SelectedGameRoot = @"C:\Games\Cyberpunk 2077",
            GameCandidates = [new(@"C:\Games\Cyberpunk 2077", "Steam", "2.5", "26161058")],
            WabbajackDirectory = @"C:\Wabbajack",
            ReadyForHandoff = true,
            Checks = [
                new("vcredist", "Runtime", "Installed", CheckState.Ready, true),
                new("longpaths", "Long paths", "Enabled", CheckState.Ready, true),
                new("wabbajack", "Wabbajack", "Verified", CheckState.Ready, true),
                new(optionalId, "Optional action", "Available", optionalState, false)
            ]
        });
        model.PrepareCommand.Execute(null);
        return model;
    }

    [TestMethod]
    public void ReadyHostReviewsOnlySelectedDirectXWithoutRequiringCleanup()
    {
        PreparationViewModel model = ReadyModel("directx", CheckState.Optional);
        model.LegacyDirectX = true;
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.CleanGame);
        Assert.IsFalse(model.IsWabbajackPage);
        Assert.AreEqual("Install legacy DirectX components.", model.SelectedActions.Single());
    }

    [TestMethod]
    public void ReadyHostReviewsOnlySelectedSteamPreferenceWithoutRequiringCleanup()
    {
        PreparationViewModel model = ReadyModel("steamupdate", CheckState.Optional);
        model.SteamLaunchOnly = true;
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsReview);
        Assert.IsFalse(model.CleanGame);
        Assert.IsFalse(model.IsWabbajackPage);
        Assert.HasCount(1, model.SelectedActions);
    }

    [TestMethod]
    public void ReadyHostWithNoPendingSelectedWorkContinuesToWabbajack()
    {
        PreparationViewModel model = ReadyModel("directx", CheckState.Ready);
        model.PrimaryCommand.Execute(null);
        Assert.IsTrue(model.IsWabbajackPage);
        Assert.IsFalse(model.IsReview);
        Assert.IsFalse(model.CleanGame);
    }
}
