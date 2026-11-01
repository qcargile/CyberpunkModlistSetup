using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Preparation;
using ChromeBlood.Core.Parity;
using ChromeBlood.Core.Vanilla;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests.Vanilla;

[TestClass]
public sealed class VanillaGameScannerTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string Manifest = "\"AppState\" { \"appid\" \"1091500\" \"buildid\" \"26161058\" \"StateFlags\" \"4\" \"UserConfig\" { \"language\" \"english\" \"BetaKey\" \"public\" } \"InstalledDepots\" { \"1091501\" { \"manifest\" \"3524634124112875626\" } \"2060310\" { \"manifest\" \"4793018690565322422\" } \"2138330\" { \"manifest\" \"5565496786254603194\" } } }";

    [TestMethod]
    public Task CandidateChangedAfterClassificationIsKeptUntilAnotherCheck()
        => RejectChangedCandidate(null);

    [TestMethod]
    [TestCategory("GameIndexLive")]
    public Task NewlyRecognizedBackupCannotBeMovedByAnOlderClassification()
    {
        string? root = Environment.GetEnvironmentVariable("CHROMEBLOOD_VERIFY_STEAM25_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Set CHROMEBLOOD_VERIFY_STEAM25_ROOT to test a copied NVIDIA backup.");
        return RejectChangedCandidate(Path.Combine(root!, "bin", "x64", "nvngx_dlss.dll.dlsss"));
    }

    private static async Task RejectChangedCandidate(string? replacement)
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "steamapps", "common", "Cyberpunk 2077");
        Directory.CreateDirectory(Path.Combine(game, "bin", "x64"));
        File.WriteAllText(Path.Combine(game, "bin", "x64", "Cyberpunk2077.exe"), "test executable");
        File.WriteAllText(Path.Combine(temporary.Path, "steamapps", "appmanifest_1091500.acf"),
            Manifest.Replace("\"appid\" \"1091500\"", "\"appid\" \"1091500\" \"installdir\" \"Cyberpunk 2077\"", StringComparison.Ordinal));
        string candidate = Path.Combine(game, "bin", "x64", "nvngx_dlss.dll.dlsss");
        File.WriteAllText(candidate, "unrecognized backup");
        string appData = Directory.CreateDirectory(Path.Combine(temporary.Path, "appdata")).FullName;
        string backup = Path.Combine(temporary.Path, "backup");
        CleanupPreparationService service = CleanupPreparationService.ForDisposableTests(temporary.Path, LegacyParityCorpus.LoadEmbedded(),
            Path.Combine(temporary.Path, "state"), appData, Path.Combine(temporary.Path, "settings-backup"));
        bool changed = false;
        await Assert.ThrowsExactlyAsync<CleanupAuthorizationException>(() => service.ExecuteAsync(game, true, false, false, message =>
        {
            if (!changed && message.StartsWith("Verifying official game files:", StringComparison.Ordinal)
                && message.EndsWith("bytes read. ", StringComparison.Ordinal))
            {
                if (replacement is null) File.WriteAllText(candidate, "changed backup");
                else File.Copy(replacement, candidate, true);
                changed = true;
            }
        }, CancellationToken.None, gameBackupRoot: backup));
        Assert.IsTrue(changed);
        Assert.IsTrue(File.Exists(candidate));
        Assert.IsEmpty(Directory.GetFiles(temporary.Path, "quarantine.json", SearchOption.AllDirectories));
        if (replacement is not null)
        {
            using FileStream expected = File.OpenRead(replacement);
            using FileStream actual = File.OpenRead(candidate);
            CollectionAssert.AreEqual(SHA256.HashData(expected), SHA256.HashData(actual));
            VanillaScanResult checkedAgain = await new VanillaGameScanner().ScanAsync(game,
                File.ReadAllText(Path.Combine(temporary.Path, "steamapps", "appmanifest_1091500.acf")));
            Assert.AreEqual(VanillaEntryStatus.VendorBackup, Entry(checkedAgain, @"bin\x64\nvngx_dlss.dll.dlsss").Status);
        }
        else Assert.AreEqual("changed backup", File.ReadAllText(candidate));
    }

    [TestMethod]
    public void EmbeddedReferenceHasCompletePinnedSteamDepotInventory()
    {
        VanillaGameIndex index = VanillaGameIndex.LoadEmbedded();
        Assert.AreEqual(5125, index.Files.Count);
        Assert.AreEqual(980, index.Directories.Count);
        Assert.AreEqual(82066017811L, index.Files.Values.Sum(file => file.Size));
        Assert.IsTrue(index.Files.ContainsKey(@"bin\x64\Cyberpunk2077.exe"));
        Assert.IsTrue(index.Files.Keys.Any(path => path.StartsWith(@"tools\redmod\", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(index.Files.Keys.Any(path => path.StartsWith(@"archive\pc\ep1\", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(index.MatchSteamManifest(Manifest).Matched);
    }

    [TestMethod]
    public async Task PreviewFindsArbitraryLooseFilesAndEmptyDirectoriesWithoutHashingContent()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "wrong");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "unusual", "empty folder"));
        File.WriteAllText(Path.Combine(temporary.Path, "unusual", "sidecar.custom"), "personal file");
        File.WriteAllText(Path.Combine(temporary.Path, "vortex.deployment.custom.json"), "{}");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "V2077"));
        VanillaScanResult result = await new VanillaGameScanner(TestIndex()).ScanAsync(temporary.Path, Manifest);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(VanillaEntryStatus.OfficialUnverified, Entry(result, @"bin\game.dat").Status);
        Assert.IsNull(Entry(result, @"bin\game.dat").ActualSha1);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, @"unusual\empty folder").Status);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, @"unusual\sidecar.custom").Status);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, "vortex.deployment.custom.json").Status);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, "V2077").Status);
        Assert.AreEqual("wrong", File.ReadAllText(Path.Combine(temporary.Path, "bin", "game.dat")));
        Assert.AreEqual(3, Directory.EnumerateFiles(temporary.Path, "*", SearchOption.AllDirectories).Count());
    }

    [TestMethod]
    public async Task ExplicitVerificationDetectsSameSizeOfficialOverwriteAndAcceptsCorrectBytes()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "wrong");
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult changed = await scanner.ScanAsync(temporary.Path, Manifest, true);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(changed, @"bin\game.dat").Status);
        Assert.AreNotEqual(Entry(changed, @"bin\game.dat").ExpectedSha1, Entry(changed, @"bin\game.dat").ActualSha1);
        WriteOfficial(temporary.Path, "right");
        VanillaScanResult matched = await scanner.ScanAsync(temporary.Path, Manifest, true);
        Assert.AreEqual(VanillaEntryStatus.OfficialVerified, Entry(matched, @"bin\game.dat").Status);
        Assert.AreEqual(Entry(matched, @"bin\game.dat").ExpectedSha1, Entry(matched, @"bin\game.dat").ActualSha1);
    }

    [TestMethod]
    public async Task UnchangedPreviewRetainsVerificationWithoutReadingOfficialContentAgain()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        VanillaScanResult preview = await scanner.ScanAsync(temporary.Path, Manifest);
        string identity = WindowsCleanupProbe.IdentityOfDirectory(temporary.Path);
        Assert.IsFalse(string.IsNullOrEmpty(verified.ObservationFingerprint));
        Assert.AreEqual(verified.ObservationFingerprint, preview.ObservationFingerprint);
        Assert.IsNull(Entry(preview, @"bin\game.dat").ActualSha1);
        Assert.IsTrue(PreparationService.CanReuseGameFileVerification(identity, preview, identity, verified));
    }

    [TestMethod]
    public async Task AddingAndRemovingExtrasDoesNotInvalidateOfficialVerification()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        string extra = Path.Combine(temporary.Path, "bin", "mod.dll");
        File.WriteAllText(extra, "mod");
        Directory.CreateDirectory(Path.Combine(temporary.Path, "mod", "empty"));
        Directory.SetLastWriteTimeUtc(Path.Combine(temporary.Path, "bin"), DateTime.UtcNow.AddMinutes(1));
        VanillaScanResult withExtras = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(verified.ObservationFingerprint, withExtras.ObservationFingerprint);
        Assert.IsTrue(withExtras.Entries.Any(entry => entry.Status == VanillaEntryStatus.Extra));
        File.Delete(extra);
        Directory.Delete(Path.Combine(temporary.Path, "mod"), true);
        VanillaScanResult cleaned = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(verified.ObservationFingerprint, cleaned.ObservationFingerprint);
        Assert.IsFalse(cleaned.Entries.Any(entry => entry.Status == VanillaEntryStatus.Extra));
    }

    [TestMethod]
    public async Task SameSizeOverwriteInvalidatesVerificationWhenWriteTimeChanges()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        string path = Path.Combine(temporary.Path, "bin", "game.dat");
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        File.WriteAllText(path, "wrong");
        File.SetLastWriteTimeUtc(path, originalWriteTime.AddSeconds(5));
        VanillaScanResult changed = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreNotEqual(verified.ObservationFingerprint, changed.ObservationFingerprint);
        Assert.AreEqual(VanillaEntryStatus.OfficialUnverified, Entry(changed, @"bin\game.dat").Status);
        string identity = WindowsCleanupProbe.IdentityOfDirectory(temporary.Path);
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, changed, identity, verified));
    }

    [TestMethod]
    public async Task ReplacementFileInvalidatesVerificationEvenWithSameSizeAndWriteTime()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        string path = Path.Combine(temporary.Path, "bin", "game.dat");
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        File.Move(path, Path.Combine(temporary.Path, "original.dat"));
        File.WriteAllText(path, "right");
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        VanillaScanResult replaced = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreNotEqual(verified.ObservationFingerprint, replaced.ObservationFingerprint);
    }

    [TestMethod]
    public async Task RestoredMetadataRequiresExplicitHashingToDiscoverChangedBytes()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        string path = Path.Combine(temporary.Path, "bin", "game.dat");
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        File.WriteAllText(path, "wrong");
        File.SetLastWriteTimeUtc(path, originalWriteTime);
        VanillaScanResult preview = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(verified.ObservationFingerprint, preview.ObservationFingerprint);
        Assert.IsNull(Entry(preview, @"bin\game.dat").ActualSha1);
        VanillaScanResult checkedBytes = await scanner.ScanAsync(temporary.Path, Manifest, true);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(checkedBytes, @"bin\game.dat").Status);
    }

    [TestMethod]
    public async Task MissingRestoredAndReplacedOfficialTypesChangeObservation()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        string path = Path.Combine(temporary.Path, "bin", "game.dat");
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        File.Delete(path);
        VanillaScanResult missing = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreNotEqual(verified.ObservationFingerprint, missing.ObservationFingerprint);
        WriteOfficial(temporary.Path, "right");
        VanillaScanResult restored = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreNotEqual(missing.ObservationFingerprint, restored.ObservationFingerprint);
        File.Delete(path);
        Directory.CreateDirectory(path);
        VanillaScanResult directory = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreNotEqual(restored.ObservationFingerprint, directory.ObservationFingerprint);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(directory, @"bin\game.dat").Status);
    }

    [TestMethod]
    public async Task VerificationReuseRequiresSameRootReferenceAndCompleteHashObservation()
    {
        using TemporaryDirectory temporary = new();
        using TemporaryDirectory other = new();
        WriteOfficial(temporary.Path, "right");
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        VanillaScanResult current = await scanner.ScanAsync(temporary.Path, Manifest);
        string identity = WindowsCleanupProbe.IdentityOfDirectory(temporary.Path);
        string otherIdentity = WindowsCleanupProbe.IdentityOfDirectory(other.Path);
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(otherIdentity, current, identity, verified));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current with { ReferenceIdentity = "different" }, identity, verified));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current with { Complete = false }, identity, verified));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current with { ProfileMatch = new(false, "unsupported") }, identity, verified));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current, identity, verified with { Complete = false }));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current, identity, verified with { HashVerificationRequested = false }));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current, identity, verified with { ObservationFingerprint = string.Empty }));
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current, identity, null));
    }

    [TestMethod]
    public async Task SizeChangesAndMissingFilesRemainDistinct()
    {
        using TemporaryDirectory temporary = new();
        VanillaGameScanner scanner = new(TestIndex());
        VanillaScanResult missing = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(VanillaEntryStatus.Missing, Entry(missing, @"bin\game.dat").Status);
        WriteOfficial(temporary.Path, "longer content");
        VanillaScanResult changed = await scanner.ScanAsync(temporary.Path, Manifest, true);
        VanillaScanEntry entry = Entry(changed, @"bin\game.dat");
        Assert.AreEqual(VanillaEntryStatus.Changed, entry.Status);
        Assert.AreEqual(5L, entry.ExpectedSize);
        Assert.AreEqual(14L, entry.ActualSize);
        Assert.IsNull(entry.ActualSha1);
    }

    [TestMethod]
    public async Task EmptyOfficialFilesWithZeroManifestHashAcceptOnlyEmptyContent()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, string.Empty);
        VanillaGameIndex index = TestIndex(size: 0, sha1: new string('0', 40));
        VanillaGameScanner scanner = new(index);
        VanillaScanResult empty = await scanner.ScanAsync(temporary.Path, Manifest, true);
        Assert.AreEqual(VanillaEntryStatus.OfficialVerified, Entry(empty, @"bin\game.dat").Status);
        Assert.AreEqual("da39a3ee5e6b4b0d3255bfef95601890afd80709", Entry(empty, @"bin\game.dat").ActualSha1);
        WriteOfficial(temporary.Path, "x");
        VanillaScanResult nonempty = await scanner.ScanAsync(temporary.Path, Manifest, true);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(nonempty, @"bin\game.dat").Status);
    }

    [TestMethod]
    public async Task UnreadableOfficialFilesBlockCompleteness()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        using FileStream locked = new(Path.Combine(temporary.Path, "bin", "game.dat"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        VanillaScanResult result = await new VanillaGameScanner(TestIndex()).ScanAsync(temporary.Path, Manifest, true);
        Assert.IsFalse(result.Complete);
        Assert.AreEqual(string.Empty, result.ObservationFingerprint);
        Assert.AreEqual(VanillaEntryStatus.Unreadable, Entry(result, @"bin\game.dat").Status);
    }

    [TestMethod]
    public async Task UnsupportedProfilesNeverClassifyTheirFilesAsForeign()
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        string[] unsupported =
        [
            Manifest.Replace("26161058", "26000000", StringComparison.Ordinal),
            Manifest.Replace("english", "french", StringComparison.Ordinal),
            Manifest.Replace("5565496786254603194", "5565496786254603195", StringComparison.Ordinal),
            Manifest.Replace("\"BetaKey\" \"public\"", "\"BetaKey\" \"test\"", StringComparison.Ordinal),
            Manifest.Replace("\"StateFlags\" \"4\"", "\"StateFlags\" \"6\"", StringComparison.Ordinal),
            Manifest.Replace("\"StateFlags\" \"4\"", "\"StateFlags\" \"0\"", StringComparison.Ordinal),
            Manifest.Replace("\"StateFlags\" \"4\"", "\"StateFlags\" \"0x4\"", StringComparison.Ordinal),
            Manifest.Replace("\"StateFlags\" \"4\"", string.Empty, StringComparison.Ordinal),
            Manifest.Replace("\"buildid\" \"26161058\"", "\"buildid\" \"26161058\" \"buildid\" \"26000000\"", StringComparison.Ordinal),
            Manifest[..^2]
        ];
        foreach (string manifest in unsupported)
        {
            VanillaScanResult result = await new VanillaGameScanner(TestIndex()).ScanAsync(temporary.Path, manifest);
            Assert.IsFalse(result.ProfileMatch.Matched);
            Assert.IsFalse(result.Complete);
            Assert.AreEqual(string.Empty, result.ObservationFingerprint);
            Assert.IsEmpty(result.Entries);
        }
        Assert.IsTrue(TestIndex().MatchSteamManifest(Manifest.Replace("\"appid\"", "\"Unrelated\" { \"buildid\" \"wrong\" } \"appid\"", StringComparison.Ordinal)).Matched);
    }

    [TestMethod]
    public async Task CancellationStopsBeforeFilesystemTraversal()
    {
        using TemporaryDirectory temporary = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => new VanillaGameScanner(TestIndex()).ScanAsync(temporary.Path, Manifest, cancellationToken: cancellation.Token));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(temporary.Path));
    }

    [TestMethod]
    public async Task ReparseDirectoryIsReportedWithoutTraversingItsTarget()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "private.txt"), "outside");
        string link = Path.Combine(game, "linked");
        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        await process.WaitForExitAsync();
        Assert.AreEqual(0, process.ExitCode, await process.StandardError.ReadToEndAsync());
        try
        {
            VanillaScanResult result = await new VanillaGameScanner(TestIndex()).ScanAsync(game, Manifest, true);
            Assert.IsFalse(result.Complete);
            Assert.AreEqual(VanillaEntryStatus.ReparsePoint, Entry(result, "linked").Status);
            Assert.IsFalse(result.Entries.Any(entry => entry.RelativePath.Contains("private.txt", StringComparison.Ordinal)));
            Assert.AreEqual("outside", File.ReadAllText(Path.Combine(outside, "private.txt")));
        }
        finally { Directory.Delete(link); }
    }

    [TestMethod]
    public void UnsafeReferencePathsAreRejectedBeforeScanning()
    {
        foreach (string path in new[] { @"..\outside", @"bin\..\outside", @"C:\outside", @"bin\game.dat:stream", @"bin\CON", @"bin\bad.\file" })
            Assert.ThrowsExactly<InvalidDataException>(() => TestIndex(path));
    }

    [TestMethod]
    [DataRow(@"bin\x64\nvngx_dlss.dll")]
    [DataRow(@"tools\redmod\bin\nvngx_dlss.dll")]
    public async Task OriginalDlssFilesKeepOfficialStatusesAndVerifiedBackupsArePreserved(string path)
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temporary.Path, path))!);
        File.WriteAllText(Path.Combine(temporary.Path, path), "right");
        File.WriteAllText(Path.Combine(temporary.Path, path + ".dlsss"), "right");
        VanillaGameScanner scanner = new(TestIndex(path));
        VanillaScanResult preview = await scanner.ScanAsync(temporary.Path, Manifest);
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        Assert.AreEqual(VanillaEntryStatus.OfficialUnverified, Entry(preview, path).Status);
        Assert.AreEqual(VanillaEntryStatus.OfficialVerified, Entry(verified, path).Status);
        Assert.AreEqual(VanillaEntryStatus.VendorBackup, Entry(preview, path + ".dlsss").Status);
        Assert.AreEqual(preview.ObservationFingerprint, verified.ObservationFingerprint);
        Assert.AreEqual(Entry(verified, path).ExpectedSha1, Entry(preview, path).ActualSha1);
    }

    [TestMethod]
    [DataRow(@"bin\x64\nvngx_dlss.dll")]
    [DataRow(@"tools\redmod\bin\nvngx_dlss.dll")]
    public async Task UnsignedDlssReplacementsAndUnrecognizedBackupsRemainRepairAndCleanupCandidates(string path)
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temporary.Path, path))!);
        Directory.CreateDirectory(Path.Combine(temporary.Path, "bin", "x64"));
        Directory.CreateDirectory(Path.Combine(temporary.Path, "other"));
        File.WriteAllText(Path.Combine(temporary.Path, path), "wrong");
        string[] extras = [path + ".dlsss", path + ".dlsss.dlsss", @"bin\x64\other.dll.dlsss", @"other\nvngx_dlss.dll", @"other\nvngx_dlss.dll.dlsss"];
        foreach (string extra in extras) File.WriteAllText(Path.Combine(temporary.Path, extra), "wrong");
        VanillaGameScanner scanner = new(TestIndex(path));
        VanillaScanResult result = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(result, path).Status);
        foreach (string extra in extras) Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, extra).Status, extra);
        File.Delete(Path.Combine(temporary.Path, path));
        File.WriteAllText(Path.Combine(temporary.Path, path + ".dlsss"), "right");
        VanillaScanResult missing = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(VanillaEntryStatus.Missing, Entry(missing, path).Status);
        Assert.AreEqual(VanillaEntryStatus.VendorBackup, Entry(missing, path + ".dlsss").Status);
    }

    [TestMethod]
    [DataRow(@"bin\x64\nvngx_dlss.dll")]
    [DataRow(@"tools\redmod\bin\nvngx_dlss.dll")]
    public async Task DlssExceptionsRequireTheComponentInTheMatchedReference(string path)
    {
        using TemporaryDirectory temporary = new();
        WriteOfficial(temporary.Path, "right");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temporary.Path, path))!);
        File.WriteAllText(Path.Combine(temporary.Path, path + ".dlsss"), "right");
        VanillaScanResult result = await new VanillaGameScanner(TestIndex()).ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, path + ".dlsss").Status);
    }

    [TestMethod]
    [DataRow(@"bin\x64\nvngx_dlss.dll")]
    [DataRow(@"tools\redmod\bin\nvngx_dlss.dll")]
    public async Task DlssDirectoriesDoNotReceiveVendorFileExceptions(string path)
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.Combine(temporary.Path, path));
        Directory.CreateDirectory(Path.Combine(temporary.Path, path + ".dlsss"));
        VanillaScanResult result = await new VanillaGameScanner(TestIndex(path)).ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(result, path).Status);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, path + ".dlsss").Status);
    }

    [TestMethod]
    [DataRow(@"bin\x64\nvngx_dlss.dll")]
    [DataRow(@"tools\redmod\bin\nvngx_dlss.dll")]
    public async Task DlssContentChangesInvalidateVerificationEvenWhenMetadataIsRestored(string path)
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temporary.Path, path))!);
        string fullPath = Path.Combine(temporary.Path, path);
        File.WriteAllText(fullPath, "right");
        DateTime writeTime = File.GetLastWriteTimeUtc(fullPath);
        VanillaGameScanner scanner = new(TestIndex(path));
        VanillaScanResult verified = await scanner.ScanAsync(temporary.Path, Manifest, true);
        File.WriteAllText(fullPath, "wrong");
        File.SetLastWriteTimeUtc(fullPath, writeTime);
        VanillaScanResult current = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(VanillaEntryStatus.Changed, Entry(current, path).Status);
        Assert.AreNotEqual(verified.ObservationFingerprint, current.ObservationFingerprint);
        string identity = WindowsCleanupProbe.IdentityOfDirectory(temporary.Path);
        Assert.IsFalse(PreparationService.CanReuseGameFileVerification(identity, current, identity, verified));
    }

    [TestMethod]
    [DataRow(@"tools\redmod\binary\nvngx_dlss.dll")]
    [DataRow(@"tools\redmod\binextra\nvngx_dlss.dll")]
    [DataRow(@"other\tools\redmod\bin\nvngx_dlss.dll")]
    public async Task DlssLookalikeLocationsDoNotReceiveBackupExceptions(string path)
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(temporary.Path, path))!);
        File.WriteAllText(Path.Combine(temporary.Path, path), "right");
        File.WriteAllText(Path.Combine(temporary.Path, path + ".dlsss"), "right");
        VanillaScanResult result = await new VanillaGameScanner(TestIndex(path)).ScanAsync(temporary.Path, Manifest);
        Assert.AreEqual(VanillaEntryStatus.OfficialUnverified, Entry(result, path).Status);
        Assert.AreEqual(VanillaEntryStatus.Extra, Entry(result, path + ".dlsss").Status);
    }

    [TestMethod]
    [TestCategory("GameIndexLive")]
    public async Task SignedDlssSwapsAndBackupsAreRecognizedWhileWrongComponentsAndDamageAreRejected()
    {
        string? root = Environment.GetEnvironmentVariable("CHROMEBLOOD_VERIFY_STEAM25_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Set CHROMEBLOOD_VERIFY_STEAM25_ROOT to test isolated copies of the installed NVIDIA DLLs.");
        using TemporaryDirectory temporary = new();
        string[] locations = [@"bin\x64", @"tools\redmod\bin"];
        string[] components = ["nvngx_dlss.dll", "nvngx_dlssd.dll", "nvngx_dlssg.dll"];
        foreach (string location in locations)
        {
            string directory = Path.Combine(temporary.Path, location);
            Directory.CreateDirectory(directory);
            foreach (string component in components)
            {
                File.Copy(Path.Combine(root!, location, component), Path.Combine(directory, component));
                File.Copy(Path.Combine(root!, location, component + ".dlsss"), Path.Combine(directory, component + ".dlsss"));
            }
        }
        ScanProgress progress = new();
        Stopwatch elapsed = Stopwatch.StartNew();
        VanillaGameScanner scanner = new();
        VanillaScanResult recognized = await scanner.ScanAsync(temporary.Path, Manifest, progress: progress);
        elapsed.Stop();
        TestContext.WriteLine($"Twelve NVIDIA files: {elapsed.Elapsed.TotalSeconds:F3} seconds; bytes hashed: {progress.Last?.BytesHashed ?? 0}");
        Assert.IsTrue(recognized.Complete);
        foreach (string location in locations)
        {
            foreach (string component in components)
            {
                Assert.AreEqual(VanillaEntryStatus.VendorReplacement, Entry(recognized, Path.Combine(location, component)).Status, Path.Combine(location, component));
                Assert.AreEqual(VanillaEntryStatus.VendorBackup, Entry(recognized, Path.Combine(location, component + ".dlsss")).Status, Path.Combine(location, component));
            }
        }
        elapsed.Restart();
        VanillaScanResult installed = await scanner.ScanAsync(root!, Manifest, progress: progress);
        elapsed.Stop();
        TestContext.WriteLine($"Installed game preview: {elapsed.Elapsed.TotalSeconds:F3} seconds; entries: {installed.Entries.Count}; bytes hashed: {progress.Last?.BytesHashed ?? 0}");
        TestContext.WriteLine($"Installed game findings: {installed.Entries.Count(entry => entry.Status == VanillaEntryStatus.Changed)} changed; {installed.Entries.Count(entry => entry.Status == VanillaEntryStatus.Missing)} missing");
        Assert.IsTrue(installed.Complete);
        foreach (string location in locations)
        {
            foreach (string component in components)
            {
                Assert.AreEqual(VanillaEntryStatus.VendorReplacement, Entry(installed, Path.Combine(location, component)).Status, Path.Combine(location, component));
                Assert.AreEqual(VanillaEntryStatus.VendorBackup, Entry(installed, Path.Combine(location, component + ".dlsss")).Status, Path.Combine(location, component));
            }
        }
        foreach (string location in locations)
        {
            string dlss = Path.Combine(temporary.Path, location, components[0]);
            DateTime writeTime = File.GetLastWriteTimeUtc(dlss);
            using (FileStream damaged = new(dlss, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                damaged.Position = 1048576;
                int value = damaged.ReadByte();
                damaged.Position--;
                damaged.WriteByte((byte)(value ^ 1));
            }
            File.SetLastWriteTimeUtc(dlss, writeTime);
            File.Copy(dlss, dlss + ".dlsss", true);
        }
        VanillaScanResult corrupt = await scanner.ScanAsync(temporary.Path, Manifest);
        Assert.AreNotEqual(recognized.ObservationFingerprint, corrupt.ObservationFingerprint);
        foreach (string location in locations)
        {
            Assert.AreEqual(VanillaEntryStatus.Changed, Entry(corrupt, Path.Combine(location, components[0])).Status);
            Assert.AreEqual(VanillaEntryStatus.Extra, Entry(corrupt, Path.Combine(location, components[0] + ".dlsss")).Status);
            string dlss = Path.Combine(temporary.Path, location, components[0]);
            File.Copy(Path.Combine(temporary.Path, location, components[1]), dlss, true);
            File.Copy(Path.Combine(temporary.Path, location, components[1]), dlss + ".dlsss", true);
        }
        VanillaScanResult wrongComponent = await scanner.ScanAsync(temporary.Path, Manifest);
        foreach (string location in locations)
        {
            Assert.AreEqual(VanillaEntryStatus.Changed, Entry(wrongComponent, Path.Combine(location, components[0])).Status);
            Assert.AreEqual(VanillaEntryStatus.Extra, Entry(wrongComponent, Path.Combine(location, components[0] + ".dlsss")).Status);
            string componentPath = Path.Combine(location, components[1]);
            VanillaGameScanner sameSize = new(TestIndex(componentPath, new FileInfo(Path.Combine(temporary.Path, componentPath)).Length));
            VanillaScanResult sameSizeSwap = await sameSize.ScanAsync(temporary.Path, Manifest);
            Assert.AreEqual(VanillaEntryStatus.VendorReplacement, Entry(sameSizeSwap, componentPath).Status);
        }
    }

    [TestMethod]
    [TestCategory("GameIndexLive")]
    public async Task InstalledSteam25ScanMeasuresOfficialHashVerificationWithoutChangingGameFiles()
    {
        string? root = Environment.GetEnvironmentVariable("CHROMEBLOOD_VERIFY_STEAM25_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Set CHROMEBLOOD_VERIFY_STEAM25_ROOT to opt into the read-only installed-game scan.");
        string gameRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root!));
        string steamApps = Directory.GetParent(gameRoot)?.Parent?.FullName ?? throw new InvalidOperationException("The game directory has no Steam library parent.");
        string manifest = await File.ReadAllTextAsync(Path.Combine(steamApps, "appmanifest_1091500.acf"));
        VanillaGameIndex index = VanillaGameIndex.LoadEmbedded();
        ScanProgress progress = new();
        using Process process = Process.GetCurrentProcess();
        long startingWorkingSet = process.WorkingSet64;
        using CancellationTokenSource cancellation = new(TimeSpan.FromMinutes(10));
        Stopwatch elapsed = Stopwatch.StartNew();
        VanillaScanResult result = await new VanillaGameScanner(index).ScanAsync(gameRoot, manifest, true, progress, cancellation.Token);
        elapsed.Stop();
        process.Refresh();
        TestContext.WriteLine($"Reference: {result.ReferenceIdentity}; profile matched: {result.ProfileMatch.Matched}; complete: {result.Complete}");
        TestContext.WriteLine($"Elapsed: {elapsed.Elapsed.TotalSeconds:F3} seconds; starting RSS: {startingWorkingSet}; final RSS: {process.WorkingSet64}; process peak RSS: {process.PeakWorkingSet64}; bytes hashed: {progress.Last?.BytesHashed ?? 0}");
        TestContext.WriteLine($"Reference files: {index.Files.Count}; reference directories: {index.Directories.Count}; reference bytes: {index.Files.Values.Sum(file => file.Size)}");
        foreach (VanillaEntryStatus status in Enum.GetValues<VanillaEntryStatus>())
        {
            VanillaScanEntry[] entries = result.Entries.Where(entry => entry.Status == status).ToArray();
            TestContext.WriteLine($"{status}: {entries.Count(entry => !entry.IsDirectory)} files; {entries.Count(entry => entry.IsDirectory)} directories; {entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.ActualSize ?? entry.ExpectedSize ?? 0)} bytes");
        }
        Assert.IsTrue(result.ProfileMatch.Matched, result.ProfileMatch.Message);
        Assert.IsTrue(result.Complete, "Unreadable paths, reparse points or resource limits prevented a complete installed-game scan.");
    }

    private static VanillaScanEntry Entry(VanillaScanResult result, string path) => result.Entries.Single(entry => entry.RelativePath == path);

    private static void WriteOfficial(string root, string content)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        File.WriteAllText(Path.Combine(root, "bin", "game.dat"), content);
    }

    private static VanillaGameIndex TestIndex(string path = @"bin\game.dat", long size = 5, string? sha1 = null)
    {
        VanillaDepot[] depots =
        [
            new(1091501, "3524634124112875626", new string('a', 64), 1, 1, 5, "00000000"),
            new(2060310, "4793018690565322422", new string('b', 64), 0, 0, 0, "00000000"),
            new(2138330, "5565496786254603194", new string('c', 64), 0, 0, 0, "00000000")
        ];
        VanillaReferenceDocument document = new(1, "2.5", 1091500, "26161058", "english", "public", "disposable scanner test", "test", depots, [],
            [new(path, size, sha1 ?? Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("right"))))]);
        return new VanillaGameIndex(document, "disposable-test");
    }

    private sealed class ScanProgress : IProgress<VanillaScanProgress>
    {
        public VanillaScanProgress? Last { get; private set; }
        public void Report(VanillaScanProgress value) => Last = value;
    }
}
