using System.Text.Json;
using ChromeBlood.Core.Cleanup;
using ChromeBlood.Core.Parity;
using ChromeBlood.Testing;

namespace ChromeBlood.Setup.Tests.Cleanup;

[TestClass]
public sealed class CleanupPlannerTests
{
    [TestMethod]
    public void PurgedVortexMetadataStaysAvailableAndDoesNotBlockPreparation()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        Directory.CreateDirectory(Path.Combine(game, "V2077"));
        string metadata = Path.Combine(game, "V2077", "modlist.txt");
        File.WriteAllText(metadata, "previous Vortex order");
        string manifest = Path.Combine(game, "vortex.deployment.json");
        string json = JsonSerializer.Serialize(new { version = 1, instance = "previous-manager", gameId = "cyberpunk2077", targetPath = game, files = Array.Empty<object>() });
        File.WriteAllText(manifest, json);
        CleanupPreparationService.RequireNoDeployment(game);
        CleanupPolicy policy = CleanupPolicy.ForPreparation(LegacyParityCorpus.LoadEmbedded());
        Assert.IsFalse(policy.Targets.Any(target => target.RelativePath is "V2077" or "vortex.deployment.json"));
        Assert.AreEqual(json, File.ReadAllText(manifest));
        Assert.AreEqual("previous Vortex order", File.ReadAllText(metadata));
    }

    [TestMethod]
    public void ActiveOrUnreadableVortexDeploymentNeedsPurgeBeforeCleanup()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        Directory.CreateDirectory(game);
        string manifest = Path.Combine(game, "vortex.deployment.json");
        File.WriteAllText(manifest, "{\"version\":1,\"files\":[{\"relPath\":\"r6/scripts/personal.reds\"}]}");
        Assert.ThrowsExactly<CleanupPlanException>(() => CleanupPreparationService.RequireNoDeployment(game));
        File.WriteAllText(manifest, "{damaged");
        Assert.ThrowsExactly<CleanupPlanException>(() => CleanupPreparationService.RequireNoDeployment(game));
        Assert.AreEqual("{damaged", File.ReadAllText(manifest));
    }

    private static readonly string[] ExpandedPaths = [@"archive\pc\mod\nested\a.archive", @"archive\pc\mod\nested", @"archive\pc\mod\z.archive", @"archive\pc\mod", "vortex.deployment.json"];
    private static readonly string[] DynamicPaths = [@"mods\alpha\a.archive", @"mods\alpha", @"mods\Zulu\z.archive", @"mods\Zulu", @"engine\config\platform\pc\alpha.ini", @"engine\config\platform\pc\Zulu.ini"];
    private static readonly string[] AutomatedTextExtensions = [".cs", ".ps1", ".json", ".xml", ".runsettings"];

    [TestMethod]
    public void Plan_PreservesEveryLegacyTargetOrderAndReasonAndExpandsDirectories()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string quarantine = Path.Combine(temporary.Path, "quarantine");
        Directory.CreateDirectory(quarantine);
        Directory.CreateDirectory(Path.Combine(game, "archive", "pc", "mod", "nested"));
        File.WriteAllText(Path.Combine(game, "archive", "pc", "mod", "z.archive"), "z");
        File.WriteAllText(Path.Combine(game, "archive", "pc", "mod", "nested", "a.archive"), "a");
        File.WriteAllText(Path.Combine(game, "vortex.deployment.json"), "{}");

        CleanupPlan plan = new CleanupPlanner().Plan(game, quarantine, corpus);
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepositoryRoot(), "fixtures", "legacy", "full-clean.json")));
        (string Path, string Reason)[] expected = fixture.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("relPath").GetString()!, item.GetProperty("reason").GetString()!))
            .Where(value => value.Item2 is not "installed REDmod mod - not in a clean install" and not "config override - not in a clean install")
            .ToArray();

        CollectionAssert.AreEqual(expected.Select(value => value.Path).ToArray(), plan.Targets.Select(value => value.RelativePath).ToArray());
        CollectionAssert.AreEqual(expected.Select(value => value.Reason).ToArray(), plan.Targets.Select(value => value.Reason).ToArray());
        CollectionAssert.AreEqual(ExpandedPaths, plan.Entries.Select(value => value.RelativePath).ToArray());
        Assert.IsTrue(plan.Entries.All(value => value.Status == CleanupEntryStatus.Available));
        Assert.IsTrue(plan.Entries.Where(value => value.Kind == CleanupEntryKind.File).All(value => value.Size > 0 && value.Sha256.Length == 64 && value.FileIdentity.Length > 0));
        Assert.IsTrue(plan.Entries.All(value => value.SourcePath.StartsWith(game, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Plan_SeparatesMissingDeniedLockedReparseUnavailableAndRejectsCaseCollision()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        FakeCleanupProbe probe = new()
        {
            RootIdentity = "root-id"
        };
        probe.Add(@"C:\Game\archive\pc\mod", CleanupEntryKind.Directory, CleanupEntryStatus.AccessDenied);
        probe.Add(@"C:\Game\red4ext", CleanupEntryKind.Directory, CleanupEntryStatus.Locked);
        probe.Add(@"C:\Game\bin\x64\plugins", CleanupEntryKind.Directory, CleanupEntryStatus.ReparsePoint);
        probe.Add(@"C:\Game\r6\scripts", CleanupEntryKind.Directory, CleanupEntryStatus.Unavailable);
        probe.Add(@"C:\Game\vortex.deployment.json", CleanupEntryKind.File, CleanupEntryStatus.Available, "file", 3, new string('a', 64));

        CleanupPlan plan = new CleanupPlanner(probe).Plan(@"C:\Game", @"D:\Quarantine", corpus);

        Assert.AreEqual(CleanupEntryStatus.AccessDenied, plan.Targets[0].Status);
        Assert.AreEqual(CleanupEntryStatus.Locked, plan.Targets[1].Status);
        Assert.AreEqual(CleanupEntryStatus.ReparsePoint, plan.Targets[2].Status);
        Assert.AreEqual(CleanupEntryStatus.Unavailable, plan.Targets[3].Status);
        Assert.AreEqual(CleanupEntryStatus.Missing, plan.Targets[4].Status);
        Assert.AreEqual("root-id", plan.GameRootIdentity);
        probe.Add(@"C:\Game\mods", CleanupEntryKind.Directory, CleanupEntryStatus.Available);
        probe.Children[@"C:\Game\mods"] =
        [
            FakeCleanupProbe.Entry(@"C:\Game\mods\Alpha", CleanupEntryKind.Directory),
            FakeCleanupProbe.Entry(@"C:\Game\mods\alpha", CleanupEntryKind.Directory)
        ];
        Assert.ThrowsExactly<CleanupPlanException>(() => new CleanupPlanner(probe).Plan(@"C:\Game", @"D:\Quarantine", corpus));
    }

    [TestMethod]
    public void Plan_RejectsUnsafeQuarantineRootsAndOnlyFixtureGuardsReferenceTheLiveList()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        FakeCleanupProbe probe = new() { RootIdentity = "game", QuarantineIdentity = "vault", AvailableBytes = long.MaxValue };
        Assert.ThrowsExactly<CleanupPlanException>(() => new CleanupPlanner(probe).Plan(@"C:\Game", @"C:\Game\vault", corpus));
        Assert.ThrowsExactly<CleanupPlanException>(() => new CleanupPlanner(probe).Plan(@"C:\Game", @"C:\", corpus));
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        if (!string.IsNullOrEmpty(system)) Assert.ThrowsExactly<CleanupPlanException>(() => new CleanupPlanner(probe).Plan(@"C:\Game", system, corpus));
        string? windows = Path.GetDirectoryName(system);
        if (!string.IsNullOrEmpty(windows)) Assert.ThrowsExactly<CleanupPlanException>(() => new CleanupPlanner(probe).Plan(@"C:\Game", Path.Combine(windows, "Temp"), corpus));
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles)) new CleanupPlanner(probe).Plan(Path.Combine(programFiles, "Cyberpunk 2077"), @"D:\Vault", corpus);
        probe.QuarantineHasReparseAncestor = true;
        Assert.ThrowsExactly<CleanupPlanException>(() => new CleanupPlanner(probe).Plan(@"C:\Game", @"D:\Vault", corpus));

        string testsRoot = Path.Combine(RepositoryRoot(), "tests");
        HashSet<string> fixtureGuards = new(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(testsRoot, "ChromeBlood.Companion.Tests", "Fixtures", "CompanionStartupRouter.cs"),
            Path.Combine(testsRoot, "ChromeBlood.Companion.Tests", "Fixtures", "Phase3FixtureAuthority.cs"),
            Path.Combine(testsRoot, "ChromeBlood.Companion.Tests", "Fixtures", "Phase4FixtureAuthority.cs")
        };
        string forbidden = string.Concat("D:\\Chrome & Blood", " - Compiled");
        Assert.IsFalse(Directory.EnumerateFiles(testsRoot, "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "fixtures"), "*", SearchOption.AllDirectories))
            .Where(path => AutomatedTextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !fixtureGuards.Contains(path))
            .Any(path => File.ReadAllText(path).Contains(forbidden, StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Plan_EnumeratesAllRedmodsAndOnlyNonbaselinePlatformConfigsInDeterministicOrder()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string vault = Path.Combine(temporary.Path, "vault");
        Directory.CreateDirectory(vault);
        Directory.CreateDirectory(Path.Combine(game, "mods", "Zulu"));
        Directory.CreateDirectory(Path.Combine(game, "mods", "alpha"));
        File.WriteAllText(Path.Combine(game, "mods", "Zulu", "z.archive"), "z");
        File.WriteAllText(Path.Combine(game, "mods", "alpha", "a.archive"), "a");
        string config = Path.Combine(game, "engine", "config", "platform", "pc");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "rendering.ini"), "vanilla");
        File.WriteAllText(Path.Combine(config, "platformgameplay.ini"), "vanilla");
        File.WriteAllText(Path.Combine(config, "Zulu.ini"), "z");
        File.WriteAllText(Path.Combine(config, "alpha.ini"), "a");

        CleanupPlan plan = new CleanupPlanner().Plan(game, vault, corpus);

        CollectionAssert.AreEqual(DynamicPaths, plan.Entries.Select(value => value.RelativePath).ToArray());
        Assert.IsFalse(plan.Entries.Any(value => value.RelativePath.EndsWith("rendering.ini", StringComparison.OrdinalIgnoreCase) || value.RelativePath.EndsWith("platformgameplay.ini", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public void Plan_IsReadOnlyAndDoesNotCreateAMissingQuarantineRoot()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-readonly");
        string vault = Path.Combine(temporary.Path, "missing-vault");
        Directory.CreateDirectory(game);

        Assert.ThrowsExactly<DirectoryNotFoundException>(() => new CleanupPlanner().Plan(game, vault, corpus));
        Assert.IsFalse(Directory.Exists(vault));
    }

    [TestMethod]
    public void Authorize_BindsCanonicalPlanCorpusActualDynamicTargetsPhysicalRootsAndExpiry()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-authorized");
        string vault = Path.Combine(temporary.Path, "vault-authorized");
        Directory.CreateDirectory(Path.Combine(game, "mods", "ActualRedmod"));
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(game, "mods", "ActualRedmod", "mod.archive"), "payload");
        DateTimeOffset now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

        AuthorizedCleanupPlan authorized = CleanupPlanner.ForDisposableTests(temporary.Path, () => now).Authorize(game, vault, corpus);

        Assert.AreEqual(64, authorized.PlanSha256.Length);
        Assert.AreEqual(corpus.Index.LegacyCommit, authorized.LegacyCommit);
        Assert.AreEqual(corpus.Fixtures.Single(value => value.Case.Id == "full-clean").Case.Sha256, authorized.CorpusSha256);
        Assert.AreEqual(now.AddMinutes(10), authorized.ExpiresAtUtc);
        Assert.IsTrue(authorized.Plan.Targets.Any(value => value.RelativePath == @"mods\ActualRedmod" && value.Reason == "installed REDmod mod - not in a clean install"));
        Assert.IsFalse(authorized.Plan.Targets.Any(value => value.RelativePath == @"mods\ParityRedmod"));
        Assert.AreEqual(authorized.PlanSha256, CleanupAuthorizationCodec.PlanSha256(authorized.Plan));
    }

    [TestMethod]
    public async Task AuthorizeSelected_MovesOnlyReviewedTargetsAndRejectsChangedInventory()
    {
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game-selected");
        string vault = Path.Combine(temporary.Path, "vault-selected");
        string archive = Path.Combine(game, "archive", "pc", "mod", "sample.archive");
        string marker = Path.Combine(game, "vortex.deployment.json");
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        Directory.CreateDirectory(vault);
        File.WriteAllText(archive, "archive");
        File.WriteAllText(marker, "marker");
        CleanupPlanner planner = CleanupPlanner.ForDisposableTests(temporary.Path);
        CleanupPlan review = planner.Plan(game, vault, corpus);

        AuthorizedCleanupPlan selected = planner.AuthorizeSelected(review, ["vortex.deployment.json"], corpus);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo.json"));
        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(selected, "selected-marker", journal, CancellationToken.None);

        Assert.AreEqual(QuarantineTransactionState.Committed, result.State, string.Join(" | ", result.Entries.Select(entry => entry.Reason)));
        Assert.IsFalse(File.Exists(marker));
        Assert.AreEqual("archive", File.ReadAllText(archive));
        Assert.AreEqual(1, result.Manifest.Entries.Length);
        Assert.ThrowsExactly<CleanupAuthorizationException>(() => planner.AuthorizeSelected(review, ["vortex.deployment.json"], corpus));
    }

    [TestMethod]
    public void QuarantineMutationHasNoPublicUnrecordedEntryPoint()
    {
        Assert.IsNull(typeof(QuarantineTransaction).GetMethod("ExecuteAsync"));
        Assert.IsNull(typeof(QuarantineTransaction).GetMethod("RestoreAsync"));
        Assert.AreEqual(typeof(AuthorizedCleanupPlan), typeof(QuarantineTransaction)
            .GetMethod(nameof(QuarantineTransaction.ExecuteRecordedAsync))!.GetParameters()[0].ParameterType);
    }

    [TestMethod]
    public async Task PreparationPolicy_MovesGeneratedCachesAndPreservesBaseCaches()
    {
        using TemporaryDirectory temporary = new();
        string game = Path.Combine(temporary.Path, "game");
        string vault = Path.Combine(temporary.Path, "vault");
        string cache = Path.Combine(game, "r6", "cache");
        Directory.CreateDirectory(Path.Combine(cache, "modded"));
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(cache, "modded", "tweakdb.bin"), "modded database");
        File.WriteAllText(Path.Combine(cache, "final.redscripts.modded"), "modded scripts");
        File.WriteAllText(Path.Combine(cache, "final.redscripts.ts"), "timestamp");
        File.WriteAllText(Path.Combine(cache, "final.redscripts"), "base scripts");
        File.WriteAllText(Path.Combine(cache, "tweakdb.bin"), "base database");
        File.WriteAllText(Path.Combine(cache, "tweakdb_ep1.bin"), "base expansion database");
        LegacyParityCorpus corpus = LegacyParityCorpus.Load(Path.Combine(RepositoryRoot(), "fixtures", "legacy"));
        CleanupPolicy policy = CleanupPolicy.ForPreparation(corpus);
        CleanupPlanner planner = CleanupPlanner.ForDisposableTests(temporary.Path);
        CleanupPlan plan = planner.Plan(game, vault, policy);
        string[] selected = plan.Targets.Where(value => value.Status == CleanupEntryStatus.Available).Select(value => value.RelativePath).ToArray();
        Assert.AreEqual(3, selected.Length);
        AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, selected, policy);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo-cache.json"));
        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, "generated-cache", journal, CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.IsFalse(Directory.Exists(Path.Combine(cache, "modded")));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "final.redscripts.modded")));
        Assert.IsFalse(File.Exists(Path.Combine(cache, "final.redscripts.ts")));
        Assert.AreEqual("base scripts", File.ReadAllText(Path.Combine(cache, "final.redscripts")));
        Assert.AreEqual("base database", File.ReadAllText(Path.Combine(cache, "tweakdb.bin")));
        Assert.AreEqual("base expansion database", File.ReadAllText(Path.Combine(cache, "tweakdb_ep1.bin")));
    }

    [TestMethod]
    public async Task AppDataSelection_MovesOnlySettingsAndPreservesDiagnosticsAndSaves()
    {
        using TemporaryDirectory temporary = new();
        string local = Path.Combine(temporary.Path, "LocalAppData");
        string vault = Path.Combine(temporary.Path, "vault");
        string settings = Path.Combine(local, "CD Projekt Red", "Cyberpunk 2077");
        string diagnostics = Path.Combine(local, "REDEngine");
        string saves = Path.Combine(temporary.Path, "Saved Games", "CD Projekt Red", "Cyberpunk 2077");
        Directory.CreateDirectory(settings);
        Directory.CreateDirectory(diagnostics);
        Directory.CreateDirectory(saves);
        Directory.CreateDirectory(vault);
        File.WriteAllText(Path.Combine(settings, "UserSettings.json"), "settings");
        File.WriteAllText(Path.Combine(diagnostics, "report.txt"), "diagnostics");
        File.WriteAllText(Path.Combine(saves, "save.dat"), "save");
        CleanupPlanner planner = CleanupPlanner.ForDisposableTests(temporary.Path);
        CleanupPolicy policy = CleanupPolicy.CyberpunkAppData();
        CleanupPlan plan = planner.Plan(local, vault, policy);
        AuthorizedCleanupPlan authorized = planner.AuthorizeSelected(plan, [@"CD Projekt Red\Cyberpunk 2077"], policy);
        UndoJournal journal = new(Path.Combine(temporary.Path, "undo-appdata.json"));
        QuarantineResult result = await new QuarantineTransaction().ExecuteRecordedAsync(authorized, "selected-settings", journal, CancellationToken.None);
        Assert.AreEqual(QuarantineTransactionState.Committed, result.State);
        Assert.IsFalse(Directory.Exists(settings));
        Assert.AreEqual("diagnostics", File.ReadAllText(Path.Combine(diagnostics, "report.txt")));
        Assert.AreEqual("save", File.ReadAllText(Path.Combine(saves, "save.dat")));
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !Directory.Exists(Path.Combine(current.FullName, "fixtures", "legacy"))) current = current.Parent;
        return current?.FullName ?? throw new DirectoryNotFoundException();
    }
}

internal sealed class FakeCleanupProbe : ICleanupProbe
{
    internal Dictionary<string, CleanupProbeEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal Dictionary<string, IReadOnlyList<CleanupProbeEntry>> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal string RootIdentity { get; set; } = "root";
    internal string QuarantineIdentity { get; set; } = "vault";
    internal long AvailableBytes { get; set; } = long.MaxValue;
    internal bool QuarantineHasReparseAncestor { get; set; }

    internal void Add(string path, CleanupEntryKind kind, CleanupEntryStatus status, string identity = "identity", long size = 0, string? sha = null)
        => Entries[path] = new CleanupProbeEntry(path, kind, status, FileAttributes.Normal, identity, size, sha ?? string.Empty);

    internal static CleanupProbeEntry Entry(string path, CleanupEntryKind kind)
        => new(path, kind, CleanupEntryStatus.Available, kind == CleanupEntryKind.Directory ? FileAttributes.Directory : FileAttributes.Normal, path, 0, string.Empty);

    public CleanupRootObservation ObserveRoot(string path, bool create, CancellationToken cancellationToken)
        => new(path, path, path.StartsWith(@"C:\Game", StringComparison.OrdinalIgnoreCase) ? RootIdentity : QuarantineIdentity, false, path.EndsWith(@":\", StringComparison.Ordinal), QuarantineHasReparseAncestor && !path.StartsWith(@"C:\Game", StringComparison.OrdinalIgnoreCase), AvailableBytes);

    public CleanupProbeEntry Observe(string path, CancellationToken cancellationToken)
        => Entries.TryGetValue(path, out CleanupProbeEntry? entry) ? entry : new(path, CleanupEntryKind.Other, CleanupEntryStatus.Missing, 0, string.Empty, 0, string.Empty);

    public IReadOnlyList<CleanupProbeEntry> Enumerate(string path, CancellationToken cancellationToken)
        => Children.TryGetValue(path, out IReadOnlyList<CleanupProbeEntry>? children) ? children : [];
}
