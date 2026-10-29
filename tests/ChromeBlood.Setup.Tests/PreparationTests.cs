using System.Text.Json;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Parity;
using ChromeBlood.Core.Preparation;
using ChromeBlood.Testing;
using ChromeBlood.Setup.ViewModels;

namespace ChromeBlood.Setup.Tests;

[TestClass]
public sealed class PreparationTests
{
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
