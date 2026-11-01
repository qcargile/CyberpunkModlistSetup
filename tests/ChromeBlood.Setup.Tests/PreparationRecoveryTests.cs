using ChromeBlood.Core.Preparation;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests;

[TestClass]
public sealed class PreparationRecoveryTests
{
    [TestMethod]
    public void WabbajackRejectsTheUserProfileAndCurrentDesktopDocumentsAndDownloads()
    {
        string[] folders = [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments, Environment.SpecialFolderOption.DoNotVerify),
            PreparationMachine.DownloadsDirectory()];
        Assert.IsFalse(string.IsNullOrWhiteSpace(folders[3]));
        foreach (string folder in folders.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            Assert.IsFalse(PreparationMachine.IsAllowedWabbajackDirectory(folder), folder);
            Assert.IsFalse(PreparationMachine.IsAllowedWabbajackDirectory(Path.Combine(folder, "Wabbajack")), folder);
        }
    }

    [TestMethod]
    public void RedirectedUserFoldersStayExcludedOutsideTheProfile()
    {
        string[] redirected = [@"D:\Personal\Desktop", @"D:\Personal\Documents", @"D:\Personal\Downloads"];
        foreach (string folder in redirected)
        {
            Assert.IsFalse(PreparationMachine.IsAllowedWabbajackDirectory(Path.Combine(folder, "Wabbajack"), null, redirected));
            Assert.IsFalse(PreparationMachine.IsAllowedWabbajackDirectory(folder, null, redirected));
        }
        Assert.IsTrue(PreparationMachine.IsAllowedWabbajackDirectory(@"D:\Personal\Games\Wabbajack", null, redirected));
    }

    [TestMethod]
    public void WabbajackUsesADedicatedFolderOutsideSteamAndTheSelectedGame()
    {
        const string game = @"C:\Custom Library\steamapps\common\Cyberpunk 2077";
        foreach (string folder in new[] { @"C:\", @"C:\Steam\Wabbajack", @"C:\SteamLibrary\Wabbajack", @"C:\Other Library\steamapps\Wabbajack",
            @"C:\Custom Library\Wabbajack", game, Path.Combine(game, "Wabbajack"), @"C:\Custom Library", "relative-folder" })
            Assert.IsFalse(PreparationMachine.IsAllowedWabbajackDirectory(folder, game), folder);
        Assert.IsTrue(PreparationMachine.IsAllowedWabbajackDirectory(@"C:\Games\Wabbajack", game));
    }

    [TestMethod]
    public void SuggestedWabbajackFolderIsADriveChildOutsideUserFolders()
    {
        string suggested = PreparationMachine.DefaultWabbajackDirectory();
        Assert.IsTrue(PreparationMachine.IsAllowedWabbajackDirectory(suggested), suggested);
        Assert.AreEqual("Wabbajack", Path.GetFileName(suggested));
        Assert.AreEqual(Path.GetPathRoot(suggested), Directory.GetParent(suggested)!.FullName);
    }

    [TestMethod]
    public async Task CustomExistingWabbajackSelectionIsRetainedWhenItsFolderNeedsAttention()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "Wabbajack.exe"), "existing custom app");
        PreparationSnapshot snapshot = await PreparationMachine.ScanAsync(null, directory.Path);
        Assert.AreEqual(directory.Path, snapshot.WabbajackDirectory);
        PreparationCheck check = snapshot.Checks.Single(value => value.Id == "wabbajackfolder");
        Assert.AreEqual(CheckState.Action, check.Status);
        StringAssert.Contains(check.Detail, "Change app folder");
        Assert.AreEqual("existing custom app", File.ReadAllText(Path.Combine(directory.Path, "Wabbajack.exe")));
    }

    [TestMethod]
    public async Task BusyPackageCacheStopsWaitingAndNamesTheLockedFile()
    {
        using TemporaryDirectory directory = new();
        using PackageDownloader downloader = new();
        using PackageCache cache = new(directory.Path, downloader, new AuthenticodeVerifier());
        DownloadRequest request = new(new Uri("https://example.invalid/package.exe"), new string('a', 64), "win-x64", "1", SetupAcquisitionKind.Direct, false, null, 1024);
        string lockPath = Path.Combine(directory.Path, PackageCache.GetCacheId(request) + ".lock");
        using (FileStream held = new(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            PackageAcquisitionException error = await Assert.ThrowsExactlyAsync<PackageAcquisitionException>(
                () => cache.TryGetAsync(request, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20)));
            StringAssert.Contains(error.Message, lockPath);
            StringAssert.Contains(error.Message, "Other preparation can continue");
        }
        Assert.IsNull(await cache.TryGetAsync(request, CancellationToken.None));
    }

    [TestMethod]
    public async Task CancellationInterruptsABusyCacheWait()
    {
        using TemporaryDirectory directory = new();
        using PackageDownloader downloader = new();
        using PackageCache cache = new(directory.Path, downloader, new AuthenticodeVerifier());
        DownloadRequest request = new(new Uri("https://example.invalid/package.exe"), new string('a', 64), "win-x64", "1", SetupAcquisitionKind.Direct, false, null, 1024);
        using FileStream held = new(Path.Combine(directory.Path, PackageCache.GetCacheId(request) + ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.TryGetAsync(request, cancellation.Token));
    }

    [TestMethod]
    public async Task InvalidStagedInstallerIsRetainedWithItsExactManualRecoveryPath()
    {
        using TemporaryDirectory directory = new();
        using SecureCacheRoot root = new(directory.Path);
        string cacheId = new('a', 64);
        string staged = Path.Combine(directory.Path, cacheId + "-vcredist-x64.exe");
        File.WriteAllText(staged, "untrusted staged installer");
        Uri source = new("https://example.invalid/package.exe");
        PackageCacheEntry package = new(cacheId, Path.Combine(directory.Path, cacheId + ".package"), new string('b', 64), source,
            new(AuthenticodeStatus.Untrusted, null, 0), true);
        DownloadRequest request = new(source, new string('b', 64), "win-x64", "1", SetupAcquisitionKind.Direct, true, "Publisher", 1024);
        IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => PreparationPackages.OpenLaunchPackageAsync(root, package, request, "vcredist-x64", CancellationToken.None));
        StringAssert.Contains(error.Message, staged);
        StringAssert.Contains(error.Message, "moving this file out of the cache manually");
        Assert.AreEqual("untrusted staged installer", File.ReadAllText(staged));
    }

    [TestMethod]
    public async Task FailedPrerequisiteReportsManualRecoveryAndRunsTheRemainingActions()
    {
        List<string> executed = [];
        List<string> failures = [];
        await PreparationPackages.RunPrerequisiteActionsAsync(["vcredist-x64", "vcredist-x86", "longpaths", "directx"], verb =>
        {
            executed.Add(verb);
            return verb is "vcredist-x64" or "directx" ? Task.FromException(new IOException("Access denied")) : Task.CompletedTask;
        }, CancellationToken.None, failures.Add);
        CollectionAssert.AreEqual(new[] { "vcredist-x64", "vcredist-x86", "longpaths", "directx" }, executed);
        Assert.HasCount(2, failures);
        StringAssert.Contains(failures[0], "x64 redistributable manually");
        StringAssert.Contains(failures[1], "optional step can be skipped");
    }

    [TestMethod]
    public async Task PrerequisiteFailureStillThrowsWithoutARecoveryConsumer()
    {
        List<string> executed = [];
        await Assert.ThrowsExactlyAsync<IOException>(() => PreparationPackages.RunPrerequisiteActionsAsync(["vcredist-x64", "longpaths"], verb =>
        {
            executed.Add(verb);
            return Task.FromException(new IOException("Download failed"));
        }, CancellationToken.None, null));
        CollectionAssert.AreEqual(new[] { "vcredist-x64" }, executed);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeTheNextPrerequisite()
    {
        using CancellationTokenSource cancellation = new();
        List<string> executed = [];
        List<string> failures = [];
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => PreparationPackages.RunPrerequisiteActionsAsync(["vcredist-x64", "longpaths"], verb =>
        {
            executed.Add(verb);
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }, cancellation.Token, failures.Add));
        CollectionAssert.AreEqual(new[] { "vcredist-x64" }, executed);
        Assert.IsEmpty(failures);
    }

    [TestMethod]
    public async Task ATransferTimeoutDoesNotBecomeUserCancellation()
    {
        List<string> executed = [];
        List<string> failures = [];
        await PreparationPackages.RunPrerequisiteActionsAsync(["vcredist-x64", "longpaths"], verb =>
        {
            executed.Add(verb);
            return verb == "vcredist-x64" ? Task.FromException(new OperationCanceledException("Download timed out")) : Task.CompletedTask;
        }, CancellationToken.None, failures.Add);
        Assert.HasCount(2, executed);
        Assert.HasCount(1, failures);
        StringAssert.Contains(failures[0], "Download timed out");
    }

    [TestMethod]
    public void UnreadableWindowsSettingRetainsAnUnresolvedRequiredCheck()
    {
        PreparationCheck check = PreparationMachine.ObserveRequiredSetting("longpaths", "Windows long paths",
            () => throw new UnauthorizedAccessException("Registry access denied"), "Enabled.", "Enable long paths for mod installation.");
        Assert.AreEqual(CheckState.Action, check.Status);
        Assert.IsTrue(check.Required);
        StringAssert.Contains(check.Detail, "Registry access denied");
        StringAssert.Contains(check.Detail, "Check again");
    }

    [TestMethod]
    public async Task InvalidSavedWabbajackPathDoesNotDiscardTheOtherChecks()
    {
        PreparationSnapshot snapshot = await PreparationMachine.ScanAsync(null, "invalid\0folder");
        Assert.IsTrue(snapshot.Checks.Any(check => check.Id == "game"));
        Assert.IsTrue(snapshot.Checks.Any(check => check.Id == "vcredist"));
        Assert.AreEqual(CheckState.Action, snapshot.Checks.Single(check => check.Id == "wabbajackfolder").Status);
        Assert.IsFalse(snapshot.ReadyForHandoff);
    }

    [TestMethod]
    public async Task ExistingUnsignedWabbajackHasRecoveryDirectionsAndCannotBeLaunched()
    {
        using TemporaryDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "Wabbajack.exe"), "unsigned content");
        PreparationCheck check = await PreparationMachine.InspectWabbajackAsync(directory.Path);
        Assert.AreEqual(CheckState.Action, check.Status);
        StringAssert.Contains(check.Detail, "choose another app folder");
        Assert.AreEqual("https://github.com/wabbajack-tools/wabbajack/releases/latest", check.ActionUrl);
        Assert.IsNull(await PreparationMachine.FindVerifiedWabbajackAsync(directory.Path));
    }

    [TestMethod]
    public async Task DirectoryNamedWabbajackExeHasAnActionableFinding()
    {
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(Path.Combine(directory.Path, "Wabbajack.exe"));
        PreparationCheck check = await PreparationMachine.InspectWabbajackAsync(directory.Path);
        Assert.AreEqual(CheckState.Action, check.Status);
        StringAssert.Contains(check.Detail, "move that folder manually");
        Assert.IsNull(await PreparationMachine.FindVerifiedWabbajackAsync(directory.Path));
    }
}
