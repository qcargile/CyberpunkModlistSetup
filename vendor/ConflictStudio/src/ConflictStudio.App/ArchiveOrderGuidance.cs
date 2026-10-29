using ConflictStudio.Core;

namespace ConflictStudio.App;

public static class ArchiveOrderGuidance
{
    public static string ActionLabel(ArchiveOrderEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Kind != ArchiveOrderEvidenceKind.Unresolved)
        {
            if (evidence.UnlistedArchives.Length > 0) return evidence.UnlistedArchives.Length == 1 ? "Add unlisted archive" : "Add unlisted archives";
            return evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives ? "Clean inactive entry" : "View load order";
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
        if (evidence.UnlistedArchives.Length > 0 && evidence.IgnoredEntries.Length > 0 && !evidence.PreserveInactiveArchives) return "Order verified; optional cleanup is available";
        if (evidence.UnlistedArchives.Length > 0) return "Order verified; unlisted archives can be added";
        if (evidence.PreserveInactiveArchives) return "Order verified; inactive archive positions are kept";
        return "Order verified; inactive entries can be cleaned";
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
