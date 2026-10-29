using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ChromeBlood.Core.Cleanup;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests.Cleanup;

[TestClass]
public sealed class OfficialFileBackupTests
{
    [TestMethod]
    public async Task Preserve_CopiesExactBytesAndRecordsEvidenceWithoutChangingSources()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        byte[] bytes = Enumerable.Range(0, 200000).Select(value => (byte)value).ToArray();
        string source = WriteSource(game, @"archive\pc\content\base.archive", bytes);

        string? directory = await OfficialFileBackup.PreserveAsync(game, backup, [@"archive\pc\content\base.archive"], _ => { }, CancellationToken.None);

        Assert.IsNotNull(directory);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(directory, "files", "archive", "pc", "content", "base.archive")));
        using JsonDocument record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "entries", "00000000.json")));
        Assert.AreEqual(@"archive\pc\content\base.archive", record.RootElement.GetProperty("relativePath").GetString());
        Assert.AreEqual(bytes.Length, record.RootElement.GetProperty("size").GetInt64());
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(bytes)), record.RootElement.GetProperty("sha256").GetString());
        Assert.AreEqual("Complete", record.RootElement.GetProperty("state").GetString());
        Assert.IsTrue(File.Exists(Path.Combine(directory, "complete.json")));
    }

    [TestMethod]
    public async Task Preserve_RetryUsesNewFolderAndKeepsEarlierBackup()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string source = WriteSource(game, "official.bin", [1, 2, 3]);
        string? first = await OfficialFileBackup.PreserveAsync(game, backup, ["official.bin"], _ => { }, CancellationToken.None);
        File.WriteAllBytes(source, [4, 5, 6]);

        string? second = await OfficialFileBackup.PreserveAsync(game, backup, ["official.bin"], _ => { }, CancellationToken.None);

        Assert.AreNotEqual(first, second);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(first!, "files", "official.bin")));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(Path.Combine(second!, "files", "official.bin")));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(source));
    }

    [TestMethod]
    public async Task Preserve_CancellationRetainsSourcesAndCompletedCopiesWithoutCompletionClaim()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string first = WriteSource(game, "first.bin", [1, 2, 3]);
        string second = WriteSource(game, "second.bin", [4, 5, 6]);
        using CancellationTokenSource cancellation = new();
        int progressCalls = 0;

        OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OfficialFileBackup.PreserveAsync(game, backup, ["first.bin", "second.bin"], _ =>
            {
                if (++progressCalls == 2) cancellation.Cancel();
            }, cancellation.Token));

        string directory = Directory.GetDirectories(backup).Single();
        StringAssert.Contains(exception.Message, directory);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(first));
        CollectionAssert.AreEqual(new byte[] { 4, 5, 6 }, File.ReadAllBytes(second));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(directory, "files", "first.bin")));
        Assert.IsTrue(File.Exists(Path.Combine(directory, "backup.json")));
        Assert.IsFalse(File.Exists(Path.Combine(directory, "complete.json")));
    }

    [TestMethod]
    public async Task Preserve_DoesNotOverwriteConflictingDestinationOrDeleteOriginal()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string source = WriteSource(game, "official.bin", [1, 2, 3]);
        string? destination = null;

        IOException exception = await Assert.ThrowsAsync<IOException>(() => OfficialFileBackup.PreserveAsync(game, backup, ["official.bin"], _ =>
        {
            destination = Path.Combine(Directory.GetDirectories(backup).Single(), "files", "official.bin");
            File.WriteAllBytes(destination, [7, 8, 9]);
        }, CancellationToken.None));

        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
        CollectionAssert.AreEqual(new byte[] { 7, 8, 9 }, File.ReadAllBytes(destination!));
        StringAssert.Contains(exception.Message, Directory.GetDirectories(backup).Single());
        Assert.IsFalse(File.Exists(Path.Combine(Directory.GetDirectories(backup).Single(), "complete.json")));
    }

    [TestMethod]
    public async Task Preserve_RejectsSourceDirectoryAndRetainsExistingFiles()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string source = WriteSource(game, @"folder\child.bin", [1]);

        await Assert.ThrowsAsync<IOException>(() => OfficialFileBackup.PreserveAsync(game, backup, ["folder"], _ => { }, CancellationToken.None));

        Assert.IsTrue(File.Exists(source));
        Assert.IsFalse(File.Exists(Path.Combine(Directory.GetDirectories(backup).Single(), "complete.json")));
    }

    [TestMethod]
    public async Task Preserve_RejectsJunctionInSourceChainWithoutFollowingIt()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(outside);
        string source = WriteSource(outside, "official.bin", [1, 2, 3]);
        string junction = Path.Combine(game, "linked");
        CreateJunction(junction, outside);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => OfficialFileBackup.PreserveAsync(game, backup, [@"linked\official.bin"], _ => { }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
            Assert.IsFalse(File.Exists(Path.Combine(Directory.GetDirectories(backup).Single(), "files", "linked", "official.bin")));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }

    [TestMethod]
    public async Task Preserve_RejectsJunctionInBackupChainWithoutWritingOutsideBackup()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string source = WriteSource(game, @"folder\official.bin", [1, 2, 3]);
        string outside = Path.Combine(temporary.Path, "outside");
        Directory.CreateDirectory(outside);
        string? junction = null;
        try
        {
            await Assert.ThrowsAsync<IOException>(() => OfficialFileBackup.PreserveAsync(game, backup, [@"folder\official.bin"], _ =>
            {
                junction = Path.Combine(Directory.GetDirectories(backup).Single(), "files", "folder");
                CreateJunction(junction, outside);
            }, CancellationToken.None));
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(source));
            Assert.AreEqual(0, Directory.GetFileSystemEntries(outside).Length);
        }
        finally
        {
            if (junction is not null && Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [TestMethod]
    public async Task Preserve_RejectsInvalidPathsAndDuplicatesBeforeCreatingBackup()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        string[][] invalid = [[@"..\outside.bin"], [@"C:\outside.bin"], [@"folder\..\outside.bin"], ["official.bin", "OFFICIAL.BIN"], ["file.bin:stream"]];
        foreach (string[] paths in invalid)
            await Assert.ThrowsAsync<InvalidDataException>(() => OfficialFileBackup.PreserveAsync(game, backup, paths, _ => { }, CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(backup).Length);
    }

    [TestMethod]
    public async Task Preserve_RejectsRelativeAndOverlappingRootsBeforeWriting()
    {
        using TemporaryDirectory temporary = new();
        (string game, string backup) = Roots(temporary);
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialFileBackup.PreserveAsync("relative-game", backup, ["official.bin"], _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialFileBackup.PreserveAsync(game, Path.Combine(game, "backup"), ["official.bin"], _ => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => OfficialFileBackup.PreserveAsync(game, temporary.Path, ["official.bin"], _ => { }, CancellationToken.None));
        Assert.AreEqual(0, Directory.GetFileSystemEntries(game).Length);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(backup).Length);
    }

    [TestMethod]
    public async Task Preserve_NoFilesDoesNotCreateRoots()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string backup = Path.Combine(temporary.Path, "backup");

        string? result = await OfficialFileBackup.PreserveAsync(game, backup, [], _ => Assert.Fail("No work should report progress."), CancellationToken.None);

        Assert.IsNull(result);
        Assert.AreEqual(0, Directory.GetFileSystemEntries(temporary.Path).Length);
    }

    private static (string Game, string Backup) Roots(TemporaryDirectory temporary)
    {
        string game = Path.Combine(temporary.Path, "game");
        string backup = Path.Combine(temporary.Path, "backup");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(backup);
        return (game, backup);
    }

    private static string WriteSource(string game, string relative, byte[] bytes)
    {
        string path = Path.Combine(game, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static void CreateJunction(string link, string target)
    {
        ProcessStartInfo start = new("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException();
        process.WaitForExit();
        if (process.ExitCode != 0) Assert.Inconclusive("Directory junction creation is unavailable.");
    }
}
