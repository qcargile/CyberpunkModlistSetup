namespace ConflictStudio.Core;

public sealed record CodeSourceCoverage(string Surface, int AnalyzedFiles);

public sealed record CodeCoverageReceipt(
    int SchemaVersion,
    CodeSourceCoverage[] Sources,
    int UnsupportedTweakFiles,
    int UnreadableInputs,
    int LiteralCallbacks,
    int DynamicCallbacks,
    string[] Limitations)
{
    internal static CodeCoverageReceipt Build(ModSourceInventory inventory, LuaCallbackEvidence[] callbacks, SourceAnalysisFailure[] failures, int archiveXlSources)
    {
        int unreadable = failures.Where(value => value.Surface is "RedScript" or "CET Lua" or "TweakXL" or "Evidence snapshot" or "Deployment evidence")
            .DistinctBy(value => (value.Provider, value.FilePath)).Count();
        int unsupported = inventory.Failures.Where(value => value.Surface == "TweakXL RED").DistinctBy(value => (value.Provider, value.FilePath)).Count();
        return new(1,
            [new("RedScript", inventory.RedScripts.Length), new("CET Lua", inventory.LuaSources.Length), new("TweakXL YAML", inventory.TweakSources.Length), new("ArchiveXL", archiveXlSources)],
            unsupported, unreadable,
            callbacks.Where(value => value.Confidence != EvidenceConfidence.Dynamic).Sum(value => value.Copies.Length),
            callbacks.Where(value => value.Confidence == EvidenceConfidence.Dynamic).Sum(value => value.Copies.Length),
            ["These counts show files included in the scan. Conflict Studio reads code without running it and cannot understand every language feature, so inclusion does not prove that the code runs.",
             "RedScript checks cover code definitions marked with annotations such as @replaceMethod, not every definition or use of a name. The scan also collects code that changes a directly named game database value while running.",
             "CET Lua checks start at the selected init.lua and follow other files loaded by name. If the scan cannot tell which files are loaded, it keeps the mod's other files as possible inputs. It cannot confirm that a function runs or identify every event or method whose name is calculated by code.",
             "RED .tweak files are listed but not analyzed. The unreadable count is the number of distinct files or folders with reported read problems, not the number of conflicts. Scan diagnostics explain other limits on reading code or determining whether it loads.",
             "The scan does not inspect code inside native plugin DLLs."]);
    }
}
