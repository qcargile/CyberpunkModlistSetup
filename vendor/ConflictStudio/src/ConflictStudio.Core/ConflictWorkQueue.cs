using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace ConflictStudio.Core;

public enum ConflictSurface { PackedResource, VirtualFile, ScriptAndTweak, SharedState, ArchiveXl, Diagnostic }

public enum EvidenceClassification { Redundant, Intentional, EffectiveOverwrite, Exclusive, Review, Composable, Unresolved, OrderSensitive, Informational, CompetingDeclaration, CompilerEvidence }

public enum ConflictCaseKind { ProvenConflict, FileOverride, OrderSensitive, RuntimeCheck, Composes, SameEvidence, SharedTarget, Unknown, Reviewed, CompetingDeclaration, CompilerEvidence }

public enum ConflictWorkState { NeedsAttention = 0, Reviewed = 1, NoActionNeeded = 2, ReviewWhenRelevant = 3 }

public sealed record ConflictSourceFile(string Provider, string FilePath);

public sealed record ConflictWorkItem(
    ConflictSurface Surface,
    string Target,
    EvidenceClassification Classification,
    ConflictWorkState State,
    string Summary,
    string NextAction,
    string? Winner,
    string[] Providers,
    string EvidenceSha256,
    string? ReviewRationale = null,
    string? ResultOverride = null,
    string? ProofOverride = null,
    string? MeaningOverride = null,
    string? BoundaryOverride = null)
{
    public string[] RelatedTargets { get; init; } = [];
    [JsonIgnore]
    public string FilesSummary { get; init; } = "Not recorded";
    [JsonIgnore]
    public string FilesDetails { get; init; } = "No source file was recorded for this item.";
    [JsonIgnore]
    public ConflictSourceFile[] SourceFiles { get; init; } = [];
    [JsonIgnore]
    public CodeFindingWitness[] Comparisons { get; init; } = [];
    public string AnalysisLabel { get; init; } = string.Empty;
    public string? ReviewStatusLabel { get; init; }
    public EvidenceDecision? PreviousReview { get; init; }
    public string? PreviousReviewReason { get; init; }
    public EvidenceNote? OpenNote { get; init; }
    public bool NoteIsStale { get; init; }
    public string StateLabel => State switch
    {
        ConflictWorkState.NeedsAttention => "Needs attention",
        ConflictWorkState.ReviewWhenRelevant => "Review when relevant",
        ConflictWorkState.Reviewed => "Reviewed",
        _ => "No action needed"
    };
    public string SurfaceLabel => Surface switch
    {
        ConflictSurface.PackedResource => "Packed archive file",
        ConflictSurface.VirtualFile => "Loose file",
        ConflictSurface.ScriptAndTweak => "Script or TweakXL",
        ConflictSurface.SharedState => "Values changed by mod code",
        ConflictSurface.ArchiveXl => "ArchiveXL",
        _ => "Setup or scan"
    };
    public string ClassificationLabel => Classification == EvidenceClassification.Intentional ? "Reviewed for this profile" : ResultOverride ?? (Classification switch
    {
        EvidenceClassification.Redundant => "No action: same change",
        EvidenceClassification.EffectiveOverwrite => "One installed copy takes priority",
        EvidenceClassification.Exclusive => "Confirmed: different replacements",
        EvidenceClassification.Review => "Review: behavior must be checked",
        EvidenceClassification.CompilerEvidence => "Review: duplicate code definitions",
        EvidenceClassification.Composable => "No action: no competing outcome found",
        EvidenceClassification.OrderSensitive => Providers.Length == 1 ? "Review: one source may stop the next" : "Review: one mod may stop the next",
        EvidenceClassification.Informational => "Information: shared target",
        EvidenceClassification.CompetingDeclaration => "Review: different values assigned",
        _ => "Review: evidence is incomplete"
    });
    public ConflictCaseKind CaseKind => Classification switch
    {
        EvidenceClassification.Intentional => ConflictCaseKind.Reviewed,
        EvidenceClassification.Exclusive => ConflictCaseKind.ProvenConflict,
        EvidenceClassification.EffectiveOverwrite => ConflictCaseKind.FileOverride,
        EvidenceClassification.OrderSensitive => ConflictCaseKind.OrderSensitive,
        EvidenceClassification.CompetingDeclaration => ConflictCaseKind.CompetingDeclaration,
        EvidenceClassification.CompilerEvidence => ConflictCaseKind.CompilerEvidence,
        EvidenceClassification.Review => ConflictCaseKind.RuntimeCheck,
        EvidenceClassification.Composable => ConflictCaseKind.Composes,
        EvidenceClassification.Redundant => ConflictCaseKind.SameEvidence,
        EvidenceClassification.Informational => ConflictCaseKind.SharedTarget,
        _ => ConflictCaseKind.Unknown
    };
    public bool IsActionable => CaseKind is ConflictCaseKind.ProvenConflict or ConflictCaseKind.FileOverride or ConflictCaseKind.OrderSensitive or ConflictCaseKind.CompetingDeclaration or ConflictCaseKind.CompilerEvidence or ConflictCaseKind.RuntimeCheck or ConflictCaseKind.Unknown;
    public bool IsCodeCase => Surface is not ConflictSurface.PackedResource and not ConflictSurface.Diagnostic;
    public string ProviderSummary => string.Join("  ↔  ", Providers);
    public string ProofLabel => CaseKind == ConflictCaseKind.Reviewed ? "You recorded an outcome for this profile" : ProofOverride ?? (CaseKind switch
    {
        ConflictCaseKind.ProvenConflict => "Different replacements for the same piece of game code",
        ConflictCaseKind.FileOverride => "The current mod order selects one copy of this file",
        ConflictCaseKind.OrderSensitive => "At least one path can stop before later code runs",
        ConflictCaseKind.CompetingDeclaration => "The files set the same game value differently",
        ConflictCaseKind.CompilerEvidence => "RedScript's startup check is needed",
        ConflictCaseKind.RuntimeCheck => "The files alone cannot tell us what happens in game",
        ConflictCaseKind.Composes => "The analyzed paths preserve one another",
        ConflictCaseKind.SameEvidence => "The compared code or file contents match",
        ConflictCaseKind.SharedTarget => Providers.Length == 1 ? "The sources share a target, but no competing outcome is shown" : "The mods share a target, but no competing outcome is shown",
        _ => "The scan could not determine the result"
    });
    public string MeaningLabel => MeaningOverride ?? (CaseKind switch
    {
        ConflictCaseKind.ProvenConflict => Providers.Length == 1 ? "These files replace the same piece of game code differently. Only one replacement can be used." : "Two active mods replace the same piece of game code differently. Only one replacement can be used.",
        ConflictCaseKind.FileOverride => Winner is null ? "Several mods install a file at the same location. The scan cannot tell which copy takes priority." : $"Several mods install a file at the same location. Your current profile selects the copy from {Winner}.",
        ConflictCaseKind.OrderSensitive => Providers.Length == 1 ? "The sources touch the same method, and at least one path can stop before later code runs. Their conditions may still be separate." : "The mods touch the same method, and at least one path can stop before later code runs. Their conditions may still be separate.",
        ConflictCaseKind.CompetingDeclaration => Providers.Length == 1 ? "These files set the same game value differently. Only one value can be used at a time." : "Two active mod files set the same game value differently. Only one value can be used at a time.",
        ConflictCaseKind.CompilerEvidence => "The files add code definitions with the same name. RedScript checks whether these can load together when the game starts.",
        ConflictCaseKind.RuntimeCheck => "These files change the same part of the game. Reading them alone does not tell us what happens in game.",
        ConflictCaseKind.Composes => "The analyzed changes do not produce competing outcomes at this boundary.",
        ConflictCaseKind.SameEvidence => Providers.Length == 1 ? "These file copies contain the same data. No competing change is shown here." : "The listed copies contain the same data. No competing change is shown here.",
        ConflictCaseKind.SharedTarget => Providers.Length == 1 ? "The sources refer to the same target. Shared use alone is not a conflict." : "The mods refer to the same target. Shared use alone is not a conflict.",
        ConflictCaseKind.Reviewed => "You recorded how this case should be handled for the current profile.",
        _ => "Conflict Studio could not collect enough information to determine this result."
    });
    public string BoundaryLabel => BoundaryOverride ?? (CaseKind switch
    {
        ConflictCaseKind.ProvenConflict => Providers.Length == 1 ? "These code replacements compete. This does not show that RedScript reports an error or that the replacements caused a bug." : "These code replacements compete. This does not show that RedScript reports an error or that either mod caused a bug.",
        ConflictCaseKind.FileOverride => "This shows which file copy takes priority, not whether it is the version you want.",
        ConflictCaseKind.OrderSensitive => Providers.Length == 1 ? "This is not a confirmed conflict. It does not prove a feature fails or that declaration order is the runtime call order." : "This is not a confirmed conflict. It does not prove either feature fails or that the listed mod order is the runtime call order.",
        ConflictCaseKind.CompetingDeclaration => "The files contain different values. The scan does not check which value is used in game.",
        ConflictCaseKind.CompilerEvidence => "This scan does not check RedScript startup errors or read compiler logs.",
        ConflictCaseKind.RuntimeCheck => "This is not a confirmed conflict. The affected feature still needs to be checked in game.",
        ConflictCaseKind.Reviewed => "This records your profile decision. It does not create new technical evidence.",
        ConflictCaseKind.Unknown => "There is not enough information to tell whether these changes conflict or work together.",
        _ => Providers.Length == 1 ? "No conflict is shown for this item. Other files may still conflict." : "No conflict is shown for this item. The mods may still conflict elsewhere."
    });
}

public static class ConflictWorkQueueBuilder
{
    public static ConflictWorkItem[] Build(ProfileScanReceipt receipt, IReadOnlyList<EvidenceDecision> decisions, IReadOnlyList<EvidenceNote>? notes = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(decisions);
        ILookup<(ConflictSurface Surface, string Target), EvidenceDecision> reviewContext = decisions.Where(value => value.InstallationId == receipt.InstallationId && value.ProfileName == receipt.ProfileName).ToLookup(value => (value.Surface, value.Target));
        ILookup<(ConflictSurface Surface, string Target), EvidenceNote> noteContext = (notes ?? []).Where(value => value.InstallationId == receipt.InstallationId && value.ProfileName == receipt.ProfileName).ToLookup(value => (value.Surface, value.Target));
        List<ConflictWorkItem> items = [];
        InteractionLookup interactions = new(receipt);
        CodeFindingWitnessBuilder comparisons = new(receipt);
        bool sharedPackedBlocker = receipt.ArchiveFailures.Length > 0 || receipt.ArchiveOrderEvidence?.Kind == ArchiveOrderEvidenceKind.Unresolved;
        foreach (ResourceConflict conflict in receipt.ResourceConflicts)
        {
            if (sharedPackedBlocker && conflict.Kind == ResourceConflictKind.Unresolved) continue;
            string[] providers = conflict.Providers.Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            EvidenceClassification classification = conflict.Kind switch
            {
                ResourceConflictKind.Redundant => EvidenceClassification.Redundant,
                ResourceConflictKind.Divergent or ResourceConflictKind.OrderedOverlap => EvidenceClassification.EffectiveOverwrite,
                _ => EvidenceClassification.Unresolved
            };
            string summary = classification switch
            {
                EvidenceClassification.Redundant => "Every packed provider has the same payload.",
                EvidenceClassification.EffectiveOverwrite => PackedSummary(conflict),
                _ => "The packed provider chain is incomplete, so no winner is claimed."
            };
            string action = classification == EvidenceClassification.Redundant ? "No action is needed." : classification == EvidenceClassification.Unresolved ? "Resolve the named archive diagnostic, then rescan." : "Open Archives → Archive conflicts to see every selected and shadowed file. If the selected owner is wrong, change archive load order and preview the exact ownership delta.";
            Add(items, receipt, decisions, ConflictSurface.PackedResource, conflict.DisplayName, classification, summary, action, classification == EvidenceClassification.Unresolved ? null : conflict.EngineWinnerArchive, providers, conflict.Providers.Select(value => $"{value.ArchiveName}|{PayloadIdentity(value)}|{value.ResourcePath}|{value.Provider}").ToArray());
        }
        foreach (IGrouping<string, VirtualFileShadow> group in receipt.VirtualFileShadows.GroupBy(value => value.Relation + "|" + value.WinnerProvider + "|" + string.Join("|", value.Providers.Select(provider => provider.Provider)), StringComparer.OrdinalIgnoreCase))
        {
            VirtualFileShadow[] shadows = group.ToArray();
            VirtualFileShadow shadow = shadows[0];
            string[] providers = shadow.Providers.Select(value => value.Provider).ToArray();
            EvidenceClassification classification = shadow.Relation is VirtualFileRelation.Identical or VirtualFileRelation.Equivalent ? EvidenceClassification.Redundant : EvidenceClassification.EffectiveOverwrite;
            VirtualFileProvider winner = shadow.Providers[0];
            string priority = receipt.ManagerKind switch { ModManagerKind.Vortex => "Vortex's installed file choices", ModManagerKind.Manual => "the installed game files", _ => winner.Mo2Priority is int value && value >= 0 && value != int.MaxValue ? $"MO2 priority {value}" : winner.Provider == "Overwrite" ? "the MO2 overwrite directory" : "the current mod order" };
            string paths = shadows.Length == 1 ? "this path" : $"{shadows.Length} paths";
            string summary = shadow.Relation == VirtualFileRelation.Identical ? $"{string.Join(" and ", providers)} install files with exactly the same content at {paths}." : shadow.Relation == VirtualFileRelation.Equivalent ? $"{string.Join(" and ", providers)} install JSON files containing the same data at {paths}, although the files are not identical." : $"{winner.Provider} takes priority because of {priority}. Its copies are used instead of those from {string.Join(", ", providers.Skip(1))} at {paths}. The file contents differ.";
            string action = classification == EvidenceClassification.Redundant ? "No action is needed." : receipt.ManagerKind switch { ModManagerKind.Vortex => $"If {winner.Provider} is intended, mark this override intentional. If not, change which mod wins the file conflict in Vortex, deploy the profile, then rescan.", ModManagerKind.Manual => $"If {winner.Provider} is intended, mark this override intentional. If not, correct the deployed game files with your installer or mod manager, then rescan.", _ => $"If {winner.Provider} is intended, mark this override intentional. If not, change the mods' priority in MO2, then rescan; Conflict Studio will not silently reorder MO2 mods." };
            string target = shadows.Length == 1 ? shadow.RelativePath : $"{winner.Provider} overrides {string.Join(", ", providers.Skip(1))} ({shadows.Length} files)";
            string[] evidence = shadows.SelectMany(value => value.Providers.Select((provider, index) => $"{value.RelativePath}|{provider.Provider}|{provider.Sha256}|{index}")).ToArray();
            Add(items, receipt, decisions, ConflictSurface.VirtualFile, target, classification, summary, action, shadow.WinnerProvider, providers, evidence, shadows.Select(value => value.RelativePath).ToArray(), sourceFiles: shadows.SelectMany(value => value.Providers.Select(provider => (provider.Provider, value.RelativePath))));
        }
        foreach (InteractionFinding finding in receipt.InteractionFindings)
        {
            EvidenceClassification classification = ClassifyInteraction(finding, interactions);
            if (classification is EvidenceClassification.Informational or EvidenceClassification.Composable or EvidenceClassification.Redundant) continue;
            (string summary, string action) = DescribeInteraction(finding, classification, interactions);
            (string? result, string? proof, string? meaning, string? boundary) = InteractionLabels(finding, interactions, classification);
            Add(items, receipt, decisions, ConflictSurface.ScriptAndTweak, finding.Target, classification, summary, action, null, InteractionProviders(finding, interactions), InteractionEvidence(finding, interactions), null, result, proof, meaning, boundary, InteractionFiles(finding, interactions), hash => comparisons.ForInteraction(hash, finding));
        }
        foreach (SharedStateWriteFinding finding in receipt.SharedStateWrites)
        {
            var competing = finding.CompetingValues().ToArray();
            if (competing.Length == 0) continue;
            string[] providers = competing.SelectMany(value => new[] { value.First.Provider, value.Second.Provider }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            string summary = string.Join(" ", competing.Select(value => $"{value.First.Provider} writes {value.FirstValue} ({value.First.FilePath}:{value.First.Line}); {value.Second.Provider} writes {value.SecondValue} ({value.Second.FilePath}:{value.Second.Line})."));
            Add(items, receipt, decisions, ConflictSurface.SharedState, finding.Target, EvidenceClassification.CompetingDeclaration, summary,
                "If this is the change you want, mark it reviewed. Otherwise, check for a compatibility patch or share the report with the mod author. The scan does not check which value is used in game.", null, providers,
                finding.Writes.Select(value => $"{value.Provider}|{CanonicalEvidencePath(value.FilePath)}|{value.Surface}|{value.Target}|{value.Operation}|{value.Evidence}|{ScopedEvidence(value.CallSha256, value.SourceHash)}").ToArray(),
                null, "Review: mod code sets different values", "The mods' code sets the same game value differently",
                "The mods' code can set different values for the same game entry.", "The scan does not check which change happens last or whether it causes a problem in game.", finding.Writes.Select(value => (value.Provider, value.FilePath)), hash => comparisons.ForSharedState(hash, finding));
        }
        foreach (RdarArchiveFailure failure in receipt.ArchiveFailures) Add(items, receipt, decisions, ConflictSurface.Diagnostic, failure.ArchiveName, EvidenceClassification.Unresolved, failure.Message, "Repair or remove the unreadable archive, then rescan.", null, [failure.Provider], [failure.ArchiveName, failure.Message]);
        foreach (ArchiveXlSourceFailure failure in receipt.ArchiveXlFailures.Where(value => value.Kind != ArchiveXlFailureKind.Coverage)) Add(items, receipt, decisions, ConflictSurface.Diagnostic, failure.FilePath, EvidenceClassification.Unresolved, failure.Message, "The scan could not read this ArchiveXL provider or file. Fix the named problem, then rescan.", null, [failure.Provider], [failure.FilePath, failure.Message]);
        foreach (SourceAnalysisFailure failure in (receipt.SourceFailures ?? []).Where(value => !IsParserCoverageLimitation(value)))
        {
            if (failure.Surface is "TweakXL RED" or "TweakXL interpretation" or "RedScript registration" or "CET Lua activation" or "CET Lua reachability" or "CET Lua import")
            {
                Add(items, receipt, decisions, ConflictSurface.Diagnostic, failure.FilePath, EvidenceClassification.Informational, failure.Message, "Source interpretation and coverage details are available here when needed.", null, [failure.Provider], [failure.Surface, failure.FilePath, failure.Message], null, "Information: source coverage limit", "The scan records how this source was interpreted or excluded", failure.Message, "This does not establish a conflict or a broken installation.");
                continue;
            }
            bool invalidRedmod = failure.Surface == "REDmod" && failure.Message.StartsWith("This folder will not load as a REDmod", StringComparison.Ordinal);
            string action = invalidRedmod ? "Reinstall or repair the named REDmod so its folder contains a valid info.json, then deploy REDmods again." : "The scan could not use this active file or provider. Fix the named problem, then rescan.";
            Add(items, receipt, decisions, ConflictSurface.Diagnostic, failure.FilePath, EvidenceClassification.Unresolved, failure.Message, action, null, [failure.Provider], [failure.Surface, failure.FilePath, failure.Message], null, invalidRedmod ? "REDmod not loaded" : null, invalidRedmod ? "Required descriptor is missing or invalid" : null);
        }
        if (receipt.ArchiveOrderEvidence is { Kind: ArchiveOrderEvidenceKind.Unresolved } unresolvedOrder)
        {
            int affected = receipt.ResourceConflicts.Count(value => value.Kind == ResourceConflictKind.Unresolved);
            bool incompleteArchives = unresolvedOrder.IncompleteArchiveLane != ArchiveOrderProblemLane.None;
            string action = incompleteArchives
                ? "Fix the named unreadable or missing archive input, then rescan."
                : receipt.ManagerKind == ModManagerKind.Vortex
                    ? unresolvedOrder.ProblemLane switch { ArchiveOrderProblemLane.Redmod => "Deploy the active Vortex profile so the enabled REDmod set and order match, then rescan.", ArchiveOrderProblemLane.Combined => "Repair the named legacy archive order entries, deploy the active Vortex profile, then rescan.", _ => "In Archives, add every named active archive once to the legacy load order, then rescan." }
                    : unresolvedOrder.ProblemLane switch { ArchiveOrderProblemLane.Redmod => "Re-deploy REDmods from the active MO2 profile so the enabled REDmod set and order match, then rescan.", ArchiveOrderProblemLane.Combined => "Repair the named legacy archive order entries and re-deploy REDmods from the active MO2 profile, then rescan.", _ => "In Archives, add every named active archive once to the legacy load order, then rescan." };
            string problem = incompleteArchives ? "incomplete archive scan input" : "one order problem";
            Add(items, receipt, decisions, ConflictSurface.Diagnostic, "Archive load order", EvidenceClassification.Unresolved, $"{unresolvedOrder.Message} {affected} packed resource{(affected == 1 ? string.Empty : "s")} are waiting on {problem}.", action, null, unresolvedOrder.Provider is null ? ["Archive order"] : [unresolvedOrder.Provider], [unresolvedOrder.Message, .. unresolvedOrder.MissingEntries, .. unresolvedOrder.DuplicateEntries]);
        }
        else if (receipt.ArchiveOrderEvidence is { IgnoredEntries.Length: > 0, PreserveInactiveArchives: false } maintainedOrder)
        {
            Add(items, receipt, decisions, ConflictSurface.Diagnostic, "Archive order maintenance", EvidenceClassification.Review, maintainedOrder.Message, "Open Archives → Load order and apply the current order when you want to remove the inactive lines from modlist.txt.", null, maintainedOrder.Provider is null ? ["Archive order"] : [maintainedOrder.Provider], [maintainedOrder.Message, .. maintainedOrder.IgnoredEntries]);
        }
        foreach (RdarArchiveWarning warning in receipt.ArchiveWarnings ?? []) Add(items, receipt, decisions, ConflictSurface.Diagnostic, warning.ArchiveName + " path metadata", EvidenceClassification.Unresolved, warning.Message, "Files in this archive can still be compared by their IDs, but some names could not be read. Verify the game files if the error names a missing game library. If it names the archive, share the report with the mod author.", null, [warning.Provider], [warning.ArchiveName, warning.Message]);
        if (receipt.ResourcePathIndexEvidence is { State: not ResourcePathIndexState.Resolved } pathEvidence) Add(items, receipt, decisions, ConflictSurface.Diagnostic, "Global resource path index", EvidenceClassification.Unresolved, pathEvidence.Message, "Some file names could not be read. Check the named missing or unreadable files, repair the CET or game installation as appropriate, then scan again. Share the report if you need help.", null, pathEvidence.Provider is null ? ["Resource path resolver"] : [pathEvidence.Provider], [pathEvidence.State.ToString(), pathEvidence.Message]);
        return items.Select(value => WithReviewContext(receipt, reviewContext[(value.Surface, value.Target)], noteContext[(value.Surface, value.Target)], value)).OrderBy(value => StateOrder(value.State)).ThenBy(value => value.Classification == EvidenceClassification.Unresolved ? 0 : 1).ThenBy(value => value.Surface).ThenBy(value => value.Target, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsParserCoverageLimitation(SourceAnalysisFailure failure)
        => failure.Surface == "RedScript condition" || failure.Surface == "TweakXL" && failure.Message.StartsWith("TweakXL source could not be represented completely:", StringComparison.Ordinal);

    private static int StateOrder(ConflictWorkState state) => state switch
    {
        ConflictWorkState.NeedsAttention => 0,
        ConflictWorkState.ReviewWhenRelevant => 1,
        ConflictWorkState.Reviewed => 2,
        _ => 3
    };

    private static void Add(List<ConflictWorkItem> items, ProfileScanReceipt receipt, IReadOnlyList<EvidenceDecision> decisions, ConflictSurface surface, string target, EvidenceClassification classification, string summary, string action, string? winner, string[] providers, string[] evidence, string[]? relatedTargets = null, string? resultOverride = null, string? proofOverride = null, string? meaningOverride = null, string? boundaryOverride = null, IEnumerable<(string Provider, string FilePath)>? sourceFiles = null, Func<string, CodeFindingWitness[]>? comparisonFactory = null)
    {
        string hash = Hash(surface, target, classification, winner, providers, evidence);
        EvidenceDecision? decision = classification == EvidenceClassification.Unresolved || receipt.InstallationId is null ? null : decisions.Where(value => value.Target == target && value.Providers.SequenceEqual(providers, StringComparer.OrdinalIgnoreCase)).FirstOrDefault(value => EvidenceDecisionStore.Evaluate(value, receipt.InstallationId, receipt.ProfileName, surface, hash) == EvidenceDecisionState.Resolved);
        bool reviewed = decision is not null;
        ConflictWorkState state = reviewed ? ConflictWorkState.Reviewed : classification is EvidenceClassification.Redundant or EvidenceClassification.Composable or EvidenceClassification.Informational ? ConflictWorkState.NoActionNeeded : classification is EvidenceClassification.Exclusive or EvidenceClassification.Unresolved ? ConflictWorkState.NeedsAttention : ConflictWorkState.ReviewWhenRelevant;
        EvidenceClassification effectiveClassification = reviewed ? EvidenceClassification.Intentional : classification;
        var files = (sourceFiles ?? []).Where(value => !string.IsNullOrWhiteSpace(value.FilePath))
            .Select(value => (value.Provider, FilePath: CanonicalEvidencePath(value.FilePath))).Distinct()
            .OrderBy(value => value.Provider, StringComparer.OrdinalIgnoreCase).ThenBy(value => value.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();
        items.Add(new ConflictWorkItem(surface, target, effectiveClassification, state, summary, action, winner, providers, hash, reviewed ? decision!.Rationale : null, resultOverride, proofOverride, meaningOverride, boundaryOverride)
        {
            AnalysisLabel = AnalysisLabel(classification, resultOverride, providers.Length),
            RelatedTargets = relatedTargets ?? [target],
            Comparisons = comparisonFactory?.Invoke(hash) ?? [],
            SourceFiles = files.Select(value => new ConflictSourceFile(value.Provider, value.FilePath)).ToArray(),
            FilesSummary = files.Length == 0 ? "Not recorded" : string.Join(", ", files.Select(value => Path.GetFileName(value.FilePath)).Distinct(StringComparer.OrdinalIgnoreCase)),
            FilesDetails = files.Length == 0 ? "No source file was recorded for this item." : string.Join(Environment.NewLine, files.Select(value => $"{value.Provider}: {value.FilePath}"))
        });
    }

    private static ConflictWorkItem WithReviewContext(ProfileScanReceipt receipt, IEnumerable<EvidenceDecision> decisions, IEnumerable<EvidenceNote> notes, ConflictWorkItem item)
    {
        bool reviewed = item.State == ConflictWorkState.Reviewed;
        EvidenceDecision? previous = reviewed || receipt.InstallationId is null ? null : decisions
            .Where(value => string.Equals(value.InstallationId, receipt.InstallationId, StringComparison.Ordinal)
                && string.Equals(value.ProfileName, receipt.ProfileName, StringComparison.Ordinal)
                && value.Surface == item.Surface
                && string.Equals(value.Target, item.Target, StringComparison.Ordinal))
            .OrderByDescending(value => value.ReviewedAtUtc)
            .FirstOrDefault();
        string? previousReason = previous is null ? null : previous.Providers.SequenceEqual(item.Providers, StringComparer.OrdinalIgnoreCase)
            ? "Review expired because the evidence changed."
            : "Review expired because the providers changed.";
        EvidenceNote? note = receipt.InstallationId is null ? null : notes
            .Where(value => string.Equals(value.InstallationId, receipt.InstallationId, StringComparison.Ordinal)
                && string.Equals(value.ProfileName, receipt.ProfileName, StringComparison.Ordinal)
                && value.Surface == item.Surface
                && string.Equals(value.Target, item.Target, StringComparison.Ordinal))
            .OrderByDescending(value => value.Providers.SequenceEqual(item.Providers, StringComparer.OrdinalIgnoreCase) && string.Equals(value.EvidenceSha256, item.EvidenceSha256, StringComparison.Ordinal))
            .ThenByDescending(value => value.UpdatedAtUtc)
            .FirstOrDefault();
        bool noteIsStale = note is not null && (!note.Providers.SequenceEqual(item.Providers, StringComparer.OrdinalIgnoreCase) || !string.Equals(note.EvidenceSha256, item.EvidenceSha256, StringComparison.Ordinal));
        return item with
        {
            ReviewStatusLabel = reviewed ? "Reviewed" : previous is null ? null : "Review expired",
            PreviousReview = previous,
            PreviousReviewReason = previousReason,
            OpenNote = note,
            NoteIsStale = noteIsStale
        };
    }

    private static string AnalysisLabel(EvidenceClassification classification, string? resultOverride, int providerCount)
        => resultOverride ?? (classification switch
        {
            EvidenceClassification.Redundant => "No action: same change",
            EvidenceClassification.EffectiveOverwrite => "One installed copy takes priority",
            EvidenceClassification.Exclusive => "Confirmed: different replacements",
            EvidenceClassification.Review => "Review: behavior must be checked",
            EvidenceClassification.CompilerEvidence => "Review: duplicate code definitions",
            EvidenceClassification.Composable => "No action: no competing outcome found",
            EvidenceClassification.OrderSensitive => providerCount == 1 ? "Review: one source may stop the next" : "Review: one mod may stop the next",
            EvidenceClassification.Informational => "Information: shared target",
            EvidenceClassification.CompetingDeclaration => "Review: different values assigned",
            _ => "Review: evidence is incomplete"
        });

    private static IEnumerable<(string Provider, string FilePath)> InteractionFiles(InteractionFinding finding, InteractionLookup interactions)
    {
        IEnumerable<(string Provider, string FilePath)> files = interactions.Flows(finding.Target).Select(value => (value.Provider, value.FilePath))
            .Concat(interactions.Callbacks(finding.Target).SelectMany(value => value.Copies.Select(copy => (copy.Provider, copy.FilePath))))
            .Concat((interactions.Tweak(finding.Target)?.Operations ?? []).Select(value => (value.Provider, value.FilePath)))
            .Concat((finding.DeclarationEvidence ?? []).Select(value => (value.Provider, value.FilePath)));
        if (finding.TweakRuntimeEvidence is { } runtime) files = files.Concat(runtime.Declarations.Select(value => (value.Provider, value.FilePath))).Concat(runtime.Writes.Select(value => (value.Provider, value.FilePath)));
        return files;
    }

    private static string Hash(ConflictSurface surface, string target, EvidenceClassification classification, string? winner, string[] providers, string[] evidence)
    {
        string canonical = string.Join('\n', [surface.ToString(), target, classification.ToString(), winner ?? string.Empty, .. providers, .. evidence]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string[] InteractionEvidence(InteractionFinding finding, InteractionLookup interactions)
    {
        List<string> evidence = [finding.Kind.ToString()];
        if (finding.TweakRuntimeEvidence is { } runtime)
        {
            evidence.AddRange(runtime.Declarations.Select(value => $"declarative|{value.Provider}|{CanonicalEvidencePath(value.FilePath)}|{value.Kind}|{value.Value}"));
            evidence.AddRange(runtime.Writes.Select(value => $"runtime|{value.Provider}|{CanonicalEvidencePath(value.FilePath)}|{value.Operation}|{ScopedEvidence(value.CallSha256, value.SourceHash)}"));
        }
        evidence.AddRange((finding.DeclarationEvidence ?? []).Select(value => $"field|{value.Provider}|{CanonicalEvidencePath(value.FilePath)}|{RedScriptTarget.NormalizeType(value.Type)}").OrderBy(value => value, StringComparer.Ordinal));
        if (interactions.Tweak(finding.Target) is { } tweak) evidence.AddRange(tweak.Operations.Select(value => $"tweak|{value.Provider}|{CanonicalEvidencePath(value.FilePath)}|{value.Kind}|{value.Value}"));
        if (interactions.Tweak(finding.Target) is { Kind: TweakOverlapKind.BaseRecordDependency } dependency) evidence.AddRange(dependency.Operations.Select(value => "property|" + value.Target));
        evidence.AddRange(interactions.Flows(finding.Target).Select(value => $"redscript|{value.Provider}|{CanonicalEvidencePath(value.FilePath)}|{value.Kind}|{value.Continuation}|{ScopedEvidence(value.BodySha256, value.SourceHash)}"));
        evidence.AddRange(LuaInteractionEvidence(interactions.Callbacks(finding.Target)));
        return evidence.ToArray();
    }

    private static string[] InteractionProviders(InteractionFinding finding, InteractionLookup interactions)
        => finding.Providers
            .Concat(interactions.Flows(finding.Target).Select(value => value.Provider))
            .Concat(interactions.Callbacks(finding.Target).SelectMany(value => value.Copies.Select(copy => copy.Provider)))
            .Concat(interactions.Tweak(finding.Target)?.Operations.Select(value => value.Provider) ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IEnumerable<string> LuaInteractionEvidence(IEnumerable<LuaCallbackEvidence> callbacks)
        => callbacks.SelectMany(value => value.Copies.Length == 0
                ? [$"lua|{value.Target}|{value.Kind}|{value.Continuation}|unknown-provider|{ScopedEvidence(value.CallbackSha256, value.SourceHash)}"]
                : value.Copies.Select(copy => $"lua|{value.Target}|{value.Kind}|{value.Continuation}|{copy.Provider}:{CanonicalEvidencePath(copy.FilePath)}|{ScopedEvidence(value.CallbackSha256, value.SourceHash)}"))
            .OrderBy(value => value, StringComparer.Ordinal);

    private static string ScopedEvidence(string? scopedSha256, string sourceHash)
        => !string.IsNullOrWhiteSpace(scopedSha256) ? "scoped:" + scopedSha256 : "legacy-source:" + (string.IsNullOrWhiteSpace(sourceHash) ? "unavailable" : sourceHash);

    private static string CanonicalEvidencePath(string value)
        => PrivatePathRedactor.RelativeLabel(value).Replace('/', '\\');

    private static string PayloadIdentity(ResourceProvider provider)
        => !string.IsNullOrWhiteSpace(provider.CookedPayloadSha256) ? "cooked-sha256:" + provider.CookedPayloadSha256 : !string.IsNullOrWhiteSpace(provider.PayloadSha1) ? "rdar-sha1:" + provider.PayloadSha1 : "unavailable";

    private static string PackedSummary(ResourceConflict conflict)
    {
        ResourceProvider? winner = conflict.Providers.FirstOrDefault(value => string.Equals(value.ArchiveName, conflict.EngineWinnerArchive, StringComparison.OrdinalIgnoreCase));
        string comparison = conflict.Kind == ResourceConflictKind.OrderedOverlap ? "Payload comparison is unavailable, but the selected archive is established by the static order." : $"{conflict.Providers.Length - 1} lower archive provider{(conflict.Providers.Length == 2 ? string.Empty : "s")} contain a different payload.";
        return winner is null ? $"{conflict.EngineWinnerArchive} is first in the proven archive order. {comparison}" : $"{winner.Provider} supplies {winner.ArchiveName}, which is first in the proven archive order. {comparison}";
    }

    private static (string Summary, string Action) DescribeInteraction(InteractionFinding finding, EvidenceClassification classification, InteractionLookup interactions)
    {
        if (finding.TweakRuntimeEvidence is { } runtime)
        {
            var competing = runtime.CompetingValues().ToArray();
            if (competing.Length > 0)
            {
                string opposition = string.Join(" ", competing.Select(value => $"{value.Declaration.Provider} assigns {value.Declaration.Value} ({value.Declaration.FilePath}:{value.Declaration.LineNumber}); {value.Write.Provider} writes {value.Value} ({value.Write.FilePath}:{value.Write.Line})."));
                return (opposition, "If the later change is intended, mark this reviewed. Otherwise, check for a compatibility patch or share the report with the mod author. The scan does not check which value is used in game.");
            }
            if (classification is EvidenceClassification.Informational or EvidenceClassification.Composable)
            {
                string initialChanges = string.Join(" ", runtime.Declarations.Select(value => $"{value.Provider} declares {value.Kind} ({value.FilePath}:{value.LineNumber})."));
                string writes = string.Join(" ", runtime.Writes.Select(value => $"{value.Provider} calls {value.Operation} ({value.FilePath}:{value.Line})."));
                return ($"{initialChanges} {writes} No competing value is established by this relationship alone.", "No conflict action follows from this shared target. These source locations are available as context.");
            }
        }
        if (finding.DeclarationEvidence is { Length: > 0 } declarations)
        {
            string summary = string.Join(" ", declarations.Select(value => $"{value.Provider} adds this field as {value.Type} ({value.FilePath}:{value.Line})."));
            return (summary, "Check for RedScript errors when starting this profile. If an error is reported or you are unsure, share this report with the mod author before changing any files.");
        }
        RedScriptFlowEvidence[] flows = interactions.Flows(finding.Target);
        LuaCallbackEvidence[] callbacks = interactions.Callbacks(finding.Target);
        if (classification == EvidenceClassification.CompilerEvidence)
        {
            string summary = string.Join(" ", flows.Where(value => value.Kind == RedScriptFlowKind.Add).Select(value => $"{value.Provider} adds this piece of code ({value.FilePath}:{value.Line})."));
            return (summary, "Check for RedScript errors when starting this profile. If an error is reported or you are unsure, share this report with the mod author before changing any files.");
        }
        if (flows.Any(value => value.Kind == RedScriptFlowKind.Add) && callbacks.Any(value => value.Kind == LuaCallbackEvidenceKind.Override))
        {
            string summary = string.Join(" ", flows.Select(value => $"{value.Provider} adds this piece of code ({value.FilePath}:{value.Line}).")) + " " + string.Join(" ", callbacks.Where(value => value.Kind == LuaCallbackEvidenceKind.Override).Select(value => $"{string.Join(", ", value.Copies.Select(copy => copy.Provider))} registers a CET override ({string.Join(", ", value.Copies.Select(copy => copy.FilePath))}:{value.Line})."));
            return (summary, classification == EvidenceClassification.Informational ? "An added method and its extension do not by themselves require conflict investigation." : "The CET override has no forwarding call to the added method. Compare the replacement behavior with the method's intended use.");
        }
        if (flows.Length > 0)
        {
            if (HasIdenticalReplacementBodies(flows))
            {
                string identicalSummary = string.Join(" ", flows.Select(value => $"{value.Provider} contains the same replacement body ({value.FilePath}:{value.Line})."));
                return (identicalSummary, "No action is needed for this method because the analyzed replacement bodies are identical.");
            }
            string summary = string.Join(" ", flows.Select(value => value.Kind switch
            {
                RedScriptFlowKind.Wrap => $"{value.Provider} wraps this method and {(value.Continuation == RedScriptContinuationEvidence.Continues ? "continues to the next implementation" : value.Continuation == RedScriptContinuationEvidence.EarlyReturnBeforeContinuation ? "can return before the next implementation" : "does not call the next implementation")} ({value.FilePath}:{value.Line}).",
                RedScriptFlowKind.Replace => $"{value.Provider} replaces this piece of game code ({value.FilePath}:{value.Line}).",
                _ => $"{value.Provider} adds this piece of code ({value.FilePath}:{value.Line})."
            }));
            bool everyWrapperContinues = flows.All(value => value.Kind != RedScriptFlowKind.Wrap || value.Continuation == RedScriptContinuationEvidence.Continues);
            if (classification == EvidenceClassification.Informational) return (summary, "No conflict is proven by these continuation paths. Inspect the combined behavior only when troubleshooting this feature.");
            if (finding.Providers.Length == 1)
            {
                string internalAction = classification == EvidenceClassification.Exclusive
                    ? "Only one code replacement can be used. Check for an updated version or compatibility patch, or share the report with the mod author. Do not edit the mod's code unless you know which replacement is needed."
                    : classification == EvidenceClassification.CompilerEvidence
                        ? "Capture the RedScript compiler log for this active profile before deciding which duplicate declaration needs correction."
                        : classification is EvidenceClassification.Review or EvidenceClassification.OrderSensitive
                            ? "Test the affected feature with this exact profile and record what happens before changing the source declarations."
                            : "No action is needed unless the combined feature behaves incorrectly in game.";
                return (summary, internalAction);
            }
            string action = classification == EvidenceClassification.Exclusive ? $"Only one code replacement can be used. Look for a compatibility patch, or choose which mod to keep: {finding.Providers[0]} or {finding.Providers[1]}." : classification == EvidenceClassification.Review ? "Test the affected feature once with this exact profile and record what happens before changing either mod." : everyWrapperContinues ? "No action is needed unless the combined feature behaves incorrectly in game." : "Test the affected feature once with this exact profile. If it fails, disable one mod only as a direction check, then rescan before blaming either mod.";
            return (summary, action);
        }

        TweakOverlap? tweak = interactions.Tweak(finding.Target);
        if (tweak is not null)
        {
            return DescribeTweak(tweak, classification);
        }

        if (callbacks.Length > 0)
        {
            string summary = string.Join(" ", callbacks.Select(value => $"{string.Join(", ", value.Copies.Select(copy => copy.Provider))} registers {value.Kind}; continuation is {value.Continuation}."));
            return classification == EvidenceClassification.Informational ? (summary, "No conflict is proven. Inspect this only when troubleshooting the affected callback.") : classification == EvidenceClassification.Composable ? (summary, "No action is needed unless a related feature fails.") : (summary, "Run the callback check from the support bundle. If both callbacks arrive and the feature works, mark the interaction intentional.");
        }
        return (finding.Summary, classification == EvidenceClassification.Composable ? "No action is needed unless the feature behaves differently in game." : "Open Technical details for the source locations, then test the affected feature once.");
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length] + "…";

    private static (string Summary, string Action) DescribeTweak(TweakOverlap tweak, EvidenceClassification classification)
    {
        if (tweak.Kind == TweakOverlapKind.OpposingMutation)
        {
            string opposition = string.Join(" ", tweak.Operations.Select(value => $"{value.Provider} {OperationVerb(value.Kind)} {Truncate(value.Value, 72)} ({value.FilePath}:{value.LineNumber})."));
            return (opposition, "If the resulting list is what you want, mark this reviewed. Otherwise, check for a compatibility patch or share these file locations with the mod author.");
        }
        if (tweak.Kind == TweakOverlapKind.InternalContext)
        {
            string definitions = string.Join(" ", tweak.Operations.Select(value => $"{value.Kind}: {Truncate(value.Value, 72)} ({value.FilePath}:{value.LineNumber})."));
            return (definitions, "These definitions are available as source context. No competing active component or required change is established.");
        }
        if (tweak.Kind == TweakOverlapKind.BaseRecordDependency)
        {
            string declarations = string.Join(" ", tweak.Operations.Select(value => $"{value.Provider}: {value.Target} {value.Kind} {Truncate(value.Value, 72)} ({value.FilePath}:{value.LineNumber})."));
            return (declarations, "Use these source locations when investigating the derived record. This relationship alone does not require a patch.");
        }
        string operations = string.Join(" ", tweak.Operations.Select(value => $"{value.Provider} {OperationVerb(value.Kind)} {Truncate(value.Value, 72)}."));
        if (classification == EvidenceClassification.Composable)
        {
            if (tweak.Kind == TweakOverlapKind.Redundant) return ($"{operations} Every active provider declares the same value.", "No action is needed.");
            if (tweak.Kind == TweakOverlapKind.AssignmentThenMutation) return ($"{operations} TweakXL commits the assignment first, then applies every mutation in its documented phase.", "No action is needed unless the resulting array is wrong in game.");
            if (HasAddRemovePair(tweak)) return ($"{operations} TweakXL processes removals first and additions afterward, so a value named by both operations ends present.", "No action is needed unless the final array differs in game.");
            bool additionsOnly = tweak.Operations.All(value => IsTweakAddition(value.Kind));
            bool oneGuardedValue = additionsOnly && tweak.Operations.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() == 1 && tweak.Operations.All(value => IsUniqueTweakAddition(value.Kind));
            if (oneGuardedValue) return ($"{operations} The uniqueness guard keeps one copy of the value.", "No action is needed unless its array position matters in game.");
            bool hasSourceCopy = tweak.Operations.Any(value => value.Kind is TweakOperationKind.ArrayAppendFrom or TweakOperationKind.ArrayPrependFrom);
            return additionsOnly
                ? hasSourceCopy
                    ? ($"{operations} TweakXL applies the additions; source-copy operations skip values already present in the target.", "No action is needed unless the final array differs in game.")
                    : ($"{operations} TweakXL applies every addition.", "No action is needed unless the final array differs in game.")
                : ($"{operations} The operations affect distinct values, so no operation cancels another.", "No action is needed unless the final array differs in game.");
        }
        if (tweak.Kind == TweakOverlapKind.ScalarOverwrite) return ($"{operations} The files set different values. The scan does not check which value is used in game.", "If this is an intended override, mark it reviewed. If you are unsure which value should apply, check the mod's instructions or share this report with its author. Reading hidden game values may need the author's help.");
        if (tweak.Kind == TweakOverlapKind.DuplicateMutation) return ($"{operations} The same value can be inserted more than once.", "Repeated entries can be intentional. If you are unsure whether the duplicates belong in this list, share the report with the mod author before changing anything.");
        if (tweak.Kind == TweakOverlapKind.RecordDefinitionCollision) return ($"{operations} TweakXL uses the first definition it reads. The scan cannot tell which of these files it reads first.", "Check for a compatibility patch or ask the mod authors which version of this game entry should be kept. Do not remove definitions unless you know they are unwanted.");
        if (tweak.Kind == TweakOverlapKind.SourceArrayDependency) return ($"{operations} One file copies a list that another changes. Copying skips entries already in the destination list.", "The mod author may need to check both lists in game to decide whether a patch is needed. Share this report if you need help; the scan cannot show the final lists.");
        if (tweak.Operations.Any(value => value.Kind == TweakOperationKind.ArrayReplacement)) return ($"{operations} The files provide different versions of the same list. TweakXL applies additions and removals after selecting a version.", "These files replace the same list with different contents. If this is intentional, mark it reviewed. Otherwise, check for a compatibility patch or share the report with the mod author.");
        return ($"{operations} The number of repeated entries depends on which addition TweakXL applies first.", "Ask the mod author whether repeated entries are intended. Share this report before changing the files or adding a compatibility patch.");
    }

    private static string OperationVerb(TweakOperationKind kind) => kind switch
    {
        TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayAppendOnce => "adds",
        TweakOperationKind.ArrayAppendFrom => "adds unique values from",
        TweakOperationKind.ArrayPrepend or TweakOperationKind.ArrayPrependOnce => "adds to the start",
        TweakOperationKind.ArrayPrependFrom => "adds unique values to the start from",
        TweakOperationKind.ArrayRemove => "removes",
        TweakOperationKind.ArrayReplacement => "replaces the whole list with",
        _ => "assigns"
    };

    private static bool IsTweakAddition(TweakOperationKind kind)
        => kind is TweakOperationKind.ArrayAppend or TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayAppendFrom or TweakOperationKind.ArrayPrepend or TweakOperationKind.ArrayPrependOnce or TweakOperationKind.ArrayPrependFrom;

    private static bool IsUniqueTweakAddition(TweakOperationKind kind)
        => kind is TweakOperationKind.ArrayAppendOnce or TweakOperationKind.ArrayAppendFrom or TweakOperationKind.ArrayPrependOnce or TweakOperationKind.ArrayPrependFrom;

    private static bool HasAddRemovePair(TweakOverlap tweak)
        => tweak.Operations.GroupBy(value => value.Value, StringComparer.Ordinal).Any(values => values.Any(value => IsTweakAddition(value.Kind)) && values.Any(value => value.Kind == TweakOperationKind.ArrayRemove));

    private static (string? Result, string? Proof, string? Meaning, string? Boundary) InteractionLabels(InteractionFinding finding, InteractionLookup interactions, EvidenceClassification classification)
    {
        if (finding.TweakRuntimeEvidence is { } runtime)
        {
            if (runtime.CompetingValues().Any())
                return ("Review: code requests a different value", "A mod file and another mod's code set different values", "One mod sets a value in its files. Another mod's code can change that same value while running.", "The values differ in the files and code. The scan does not check the result in game or whether it causes a problem.");
            if (classification is EvidenceClassification.Informational or EvidenceClassification.Composable)
                return ("Information: shared TweakDB target", "Declarations and runtime writes refer to the same field", "The source relationship does not establish competing values.", "Settings updates, integrations, and unknown expressions are not conflicts by themselves.");
        }
        RedScriptFlowEvidence[] flows = interactions.Flows(finding.Target);
        LuaCallbackEvidence[] callbacks = interactions.Callbacks(finding.Target);
        if (classification == EvidenceClassification.Review && flows.Any(value => value.Kind == RedScriptFlowKind.Add) && callbacks.Any(value => value.Kind == LuaCallbackEvidenceKind.Override && value.Continuation == LuaContinuationEvidence.Missing))
            return ("Review: extension replaces an added method", "The CET override contains no forwarding call to the added implementation", "One provider can replace a method supplied by another.", "This does not prove a gameplay failure or that the replacement is unwanted.");
        if (classification == EvidenceClassification.Informational && flows.Any(value => value.Kind == RedScriptFlowKind.Add) && callbacks.Any(value => value.Kind == LuaCallbackEvidenceKind.Override))
            return ("Information: added method and extension", "An added RedScript method and a CET override share a target", "This can be ordinary extension or compatibility wiring.", "Their shared target alone does not establish a conflict or a runtime outcome.");
        if (classification == EvidenceClassification.Informational && flows.Any(value => value.Kind == RedScriptFlowKind.Wrap))
            return ("Information: continuation paths found", "Each analyzed wrapper contains a continuation path", "The next implementation is not statically suppressed by these wrappers.", "A continuation path does not prove behavioral compatibility, argument preservation, or an unchanged result.");
        if (finding.Providers.Length == 1 && HasIdenticalReplacementBodies(interactions.Flows(finding.Target)))
            return ("No action: same replacement", "The replacement bodies are identical", "The declarations contain the same replacement body. The analyzed method result is identical in these sources.", "This proves identical replacement source, not a competing method result. It does not prove the sources are compatible elsewhere.");
        if (HasIdenticalReplacementBodies(interactions.Flows(finding.Target))) return ("No action: same replacement", "The replacement bodies are identical", "Both mods declare the same replacement body. The analyzed method result is identical in both sources.", "This proves identical replacement source, not a competing method result. It does not prove the mods are compatible elsewhere.");
        TweakOverlap? tweak = interactions.Tweak(finding.Target);
        if (tweak is null) return (null, null, null, null);
        if (tweak.Kind == TweakOverlapKind.InternalContext)
            return ("Information: internal source definitions", "Differing definitions occur within the same provider", "The source difference alone does not establish competing active components.", "This does not establish a gameplay failure or require a compatibility patch.");
        if (tweak.Kind == TweakOverlapKind.BaseRecordDependency)
            return ("Information: direct base-record relationship", "A $base declaration names a record changed by another provider", "The evidence includes changed base properties and matching explicit derived-property writes.", "This does not resolve final inherited values, recursive dependencies, or prove that the relationship is a conflict.");
        if (tweak.Kind == TweakOverlapKind.OpposingMutation)
            return ("Review: additions oppose removals", "Different files add and remove the same list entries",
                "One file adds an entry to a list while another removes that same entry.",
                "The files disagree about what belongs in the list. This does not show whether the resulting list is unwanted or causes a problem in game.");
        (string? result, string? proof) = tweak.Kind switch
        {
            TweakOverlapKind.ScalarOverwrite => ("Review: different values assigned", "The files set the same game value differently"),
            TweakOverlapKind.AssignmentThenMutation => ("No action: assignment followed by changes", "TweakXL applies the operations in a documented order"),
            TweakOverlapKind.MixedArrayOperations when HasCompetingWholeArrayReplacements(tweak) => ("Review: different versions of the same list", "The files replace the same list with different contents"),
            TweakOverlapKind.MixedArrayOperations when tweak.Operations.Any(value => value.Kind == TweakOperationKind.ArrayReplacement) => ("Review: the resulting list needs checking", "One file replaces the list while others change its entries"),
            TweakOverlapKind.MixedArrayOperations => ("Review: duplicate count may change", "Some additions prevent duplicates and others do not"),
            TweakOverlapKind.DuplicateMutation => ("Review: one value may be added twice", "Several additions allow the same entry to appear more than once"),
            TweakOverlapKind.RecordDefinitionCollision => ("Review: different definitions for one game entry", finding.Providers.Length == 1 ? "Several files define the same game entry differently" : "Several mods define the same game entry differently"),
            TweakOverlapKind.SourceArrayDependency => ("Review: a copied list may change", "One mod copies a list that another mod changes"),
            TweakOverlapKind.ComposableMutation when HasAddRemovePair(tweak) => ("No action: remove happens before add", "TweakXL's documented order decides membership"),
            TweakOverlapKind.ComposableMutation when tweak.Operations.All(value => IsTweakAddition(value.Kind)) && tweak.Operations.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() == 1 && tweak.Operations.All(value => IsUniqueTweakAddition(value.Kind)) => ("No action: duplicate is prevented", "A uniqueness guard keeps one copy of the value"),
            TweakOverlapKind.ComposableMutation when tweak.Operations.All(value => IsTweakAddition(value.Kind)) => ("No action: all additions apply", "The mods add different values"),
            TweakOverlapKind.ComposableMutation => ("No action: array changes can coexist", "The mods change different array values"),
            TweakOverlapKind.Redundant => ("No action: same declaration", "Every provider declares the same value"),
            _ => (null, null)
        };
        return (result, proof, null, null);
    }

    private static EvidenceClassification ClassifyInteraction(InteractionFinding finding, InteractionLookup interactions)
    {
        if (finding.DeclarationEvidence is { Length: > 0 }) return EvidenceClassification.CompilerEvidence;
        if (interactions.Flows(finding.Target).Count(value => value.Kind == RedScriptFlowKind.Add) > 1) return EvidenceClassification.CompilerEvidence;
        if (finding.Kind == InteractionFindingKind.Exclusive)
        {
            RedScriptFlowEvidence[] replacements = interactions.Flows(finding.Target);
            bool competingReplacements = finding.Providers.Length == 1
                ? replacements.Count(value => value.Kind == RedScriptFlowKind.Replace) > 1
                : HasDocumentedExclusiveMechanism(replacements);
            return competingReplacements ? EvidenceClassification.Exclusive : EvidenceClassification.Informational;
        }
        if (finding.TweakRuntimeEvidence?.CompetingValues().Any() == true) return EvidenceClassification.CompetingDeclaration;
        if (finding.Kind == InteractionFindingKind.Informational) return EvidenceClassification.Informational;
        TweakOverlap? tweak = interactions.Tweak(finding.Target);
        if (tweak is not null) return tweak.Kind == TweakOverlapKind.BaseRecordDependency ? EvidenceClassification.Informational
            : tweak.Kind is TweakOverlapKind.ScalarOverwrite or TweakOverlapKind.OpposingMutation || HasCompetingWholeArrayReplacements(tweak) ? EvidenceClassification.CompetingDeclaration
            : tweak.Kind is TweakOverlapKind.ComposableMutation or TweakOverlapKind.AssignmentThenMutation or TweakOverlapKind.Redundant ? EvidenceClassification.Composable
            : tweak.Kind is TweakOverlapKind.DuplicateMutation or TweakOverlapKind.RecordDefinitionCollision or TweakOverlapKind.SourceArrayDependency ? EvidenceClassification.Review
            : EvidenceClassification.OrderSensitive;
        return EvidenceClassification.Informational;
    }

    private static bool HasCompetingWholeArrayReplacements(TweakOverlap tweak)
        => tweak.Operations.Length > 1
            && tweak.Operations.All(value => value.Kind == TweakOperationKind.ArrayReplacement)
            && tweak.Operations.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count() > 1;

    private static bool HasIdenticalReplacementBodies(RedScriptFlowEvidence[] flows)
        => flows.Length > 1
            && flows.All(value => value.Kind == RedScriptFlowKind.Replace && value.BodySha256 is not null)
            && flows.Select(value => value.BodySha256).Distinct(StringComparer.Ordinal).Count() == 1;

    private static bool HasDocumentedExclusiveMechanism(IEnumerable<RedScriptFlowEvidence> flows)
        => flows.Where(value => value.Kind == RedScriptFlowKind.Replace).Select(value => value.Provider).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;

    private sealed class InteractionLookup
    {
        private readonly Dictionary<string, TweakOverlap> _tweaks;
        private readonly ILookup<string, RedScriptFlowEvidence> _flows;
        private readonly ILookup<string, LuaCallbackEvidence> _callbacks;

        public InteractionLookup(ProfileScanReceipt receipt)
        {
            _tweaks = receipt.TweakOverlaps.ToDictionary(value => value.Target, StringComparer.Ordinal);
            _flows = receipt.RedScriptFlows.ToLookup(value => value.Target, StringComparer.Ordinal);
            _callbacks = receipt.LuaCallbacks.ToLookup(value => value.Target, StringComparer.Ordinal);
        }

        public TweakOverlap? Tweak(string target) => _tweaks.GetValueOrDefault(target);

        public RedScriptFlowEvidence[] Flows(string target) => _flows[target].ToArray();

        public LuaCallbackEvidence[] Callbacks(string target)
        {
            string methodBase = InteractionReportBuilder.MethodBase(target);
            return string.Equals(methodBase, target, StringComparison.Ordinal)
                ? _callbacks[target].ToArray()
                : _callbacks[target].Concat(_callbacks[methodBase]).ToArray();
        }
    }
}
