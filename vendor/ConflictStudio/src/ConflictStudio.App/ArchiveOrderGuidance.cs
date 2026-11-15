using ConflictStudio.Core;

namespace ConflictStudio.App;

public static class ArchiveOrderGuidance
{
    public static string ActionLabel(ArchiveOrderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind != ArchiveOrderEvidenceKind.Unresolved)
        {
            if (evidence.UnlistedArchives.Length > 0) return "Review additions";
            return evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives ? "Review cleanup" : "View load order";
        }
        if (evidence.IsRepairableLegacyOrder) return "Review repair draft";
        if (evidence.IncompleteArchiveLane != ArchiveOrderProblemLane.None) return "Show scan details";
        return evidence.ProblemLane switch
        {
            ArchiveOrderProblemLane.Redmod => "Show REDmod deployment steps",
            ArchiveOrderProblemLane.Combined => "Show repair steps",
            ArchiveOrderProblemLane.Legacy => "Show repair steps",
            _ => "Show scan details"
        };
    }

    internal static bool OpensPreparedOrder(ArchiveOrderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return evidence.IsRepairableLegacyOrder || evidence.Kind != ArchiveOrderEvidenceKind.Unresolved && (evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives || evidence.UnlistedArchives.Length > 0);
    }

    internal static string BlockedTitle(ArchiveOrderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.IncompleteArchiveLane != ArchiveOrderProblemLane.None) return "Archive scan input is incomplete";
        return evidence.ProblemLane switch
        {
            ArchiveOrderProblemLane.Legacy => "Legacy archive winners are blocked by one order problem",
            ArchiveOrderProblemLane.Redmod => "REDmod winners are blocked by one order problem",
            ArchiveOrderProblemLane.Combined => "Archive winners are blocked by two order problems",
            _ => "Archive order could not be verified"
        };
    }

    internal static string MaintenanceTitle(ArchiveOrderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.UnlistedArchives.Length > 0) return evidence.UnlistedArchives.Length == 1 ? "1 archive is not in your saved order" : $"{evidence.UnlistedArchives.Length} archives are not in your saved order";
        if (evidence.PreserveInactiveArchives) return "Order verified; inactive archive positions are kept";
        return "Order verified; inactive entries can be cleaned";
    }

    internal static string Summary(ArchiveOrderEvidence evidence, ModManagerKind managerKind)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.IsRepairableLegacyOrder) return "Review the repair draft before saving. Nothing has been written yet.";
        if (evidence.Kind == ArchiveOrderEvidenceKind.Unresolved) return Instruction(evidence, managerKind);
        if (evidence.UnlistedArchives.Length > 0)
        {
            string cleanup = evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives ? " Saving also removes inactive entries; see details." : string.Empty;
            return "These archives are shown after your saved archives. Your existing order is preserved. Find them to choose their positions, then review and apply when ready." + cleanup;
        }
        if (evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives) return "Inactive entries do not affect the current results. Review the proposed cleanup before applying it.";
        return "Your current archive order is shown below. Scanning does not save load-order changes.";
    }

    public static string Instruction(ArchiveOrderEvidence evidence, ModManagerKind managerKind = ModManagerKind.Mo2)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.IsRepairableLegacyOrder) return "Review the complete repair draft, adjust it if needed, then apply and verify it.";
        if (evidence.IncompleteArchiveLane != ArchiveOrderProblemLane.None) return "Fix the named unreadable or missing archive input, then re-check conflicts.";
        if (managerKind == ModManagerKind.Vortex && evidence.ProblemLane is ArchiveOrderProblemLane.Redmod or ArchiveOrderProblemLane.Combined) return evidence.ProblemLane == ArchiveOrderProblemLane.Combined ? "Repair the named legacy archive-order entries, deploy the active Vortex profile, then re-check conflicts." : "Deploy the active Vortex profile, then re-check conflicts.";
        if (managerKind == ModManagerKind.Manual && evidence.ProblemLane is ArchiveOrderProblemLane.Redmod or ArchiveOrderProblemLane.Combined) return evidence.ProblemLane == ArchiveOrderProblemLane.Combined ? "Repair the named legacy archive-order entries, deploy REDmods with REDlauncher or REDmod, then re-check conflicts." : "Deploy REDmods with REDlauncher or REDmod, then re-check conflicts.";
        return evidence.ProblemLane switch
        {
            ArchiveOrderProblemLane.Redmod => "Re-deploy REDmods from this active MO2 profile, then re-check conflicts.",
            ArchiveOrderProblemLane.Combined => "Repair the named legacy archive-order entries and re-deploy REDmods, then re-check conflicts.",
            ArchiveOrderProblemLane.Legacy => "Add every named active archive once to the legacy load order, then re-check conflicts.",
            _ => "Run the profile scan again. If this remains, copy the support report."
        };
    }
}
