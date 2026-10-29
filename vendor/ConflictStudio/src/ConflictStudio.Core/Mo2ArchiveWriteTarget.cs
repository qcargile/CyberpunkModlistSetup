namespace ConflictStudio.Core;

public sealed record Mo2ArchiveWriteTarget(string ModlistPath, string Provider = "Overwrite", ModManagerKind ManagerKind = ModManagerKind.Mo2, string? ContextPath = null, string? WriteBlockedReason = null, string? ExpectedContextId = null, string? ExpectedProfileId = null, string? GameRoot = null, string? CrossManagerContextPath = null);

public static class Mo2ArchiveWriteTargetResolver
{
    public static Mo2ArchiveWriteTarget Resolve(string mo2Root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        Mo2InstancePaths paths = Mo2InstancePathResolver.Resolve(mo2Root);
        string? blocked = paths.EnforcesArchiveOrder ? "MO2's Cyberpunk plugin will regenerate archive order before launch. Disable 'enforce archive load order' in the plugin settings before applying a custom order." : null;
        return new Mo2ArchiveWriteTarget(Path.Combine(paths.OverwriteRoot, "archive", "pc", "mod", "modlist.txt"), "Overwrite", ModManagerKind.Mo2, null, blocked, GameRoot: paths.GameRoot, CrossManagerContextPath: VortexDeploymentGuard.DefaultContextPath);
    }

    public static Mo2ArchiveWriteTarget Resolve(string mo2Root, ArchiveOrderEvidence? evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mo2Root);
        Mo2ArchiveWriteTarget fallback = Resolve(mo2Root);
        if (evidence?.SourcePath is not null && Mo2ProfileArchiveOrder.ResolveOverride(mo2Root, evidence.SourcePath) is string profileOrderPath) return fallback with { ModlistPath = profileOrderPath, Provider = "Profile archive order", WriteBlockedReason = Mo2ProfileArchiveOrder.MappingBlockedReason(mo2Root) };
        if (evidence is { Kind: ArchiveOrderEvidenceKind.ManagedModlist, SourcePath: not null } && File.Exists(evidence.SourcePath)) return new Mo2ArchiveWriteTarget(Path.GetFullPath(evidence.SourcePath), evidence.Provider ?? "Active provider", ModManagerKind.Mo2, null, fallback.WriteBlockedReason, GameRoot: fallback.GameRoot, CrossManagerContextPath: fallback.CrossManagerContextPath);
        return fallback;
    }

    public static Mo2ArchiveWriteTarget ResolveProfile(string mo2Root, string profileModlistPath)
        => Resolve(mo2Root) with { ModlistPath = Mo2ProfileArchiveOrder.Resolve(mo2Root, profileModlistPath), Provider = "Profile archive order", WriteBlockedReason = Mo2ProfileArchiveOrder.MappingBlockedReason(mo2Root) };
}

public static class VortexArchiveWriteTargetResolver
{
    public static Mo2ArchiveWriteTarget Resolve(string contextPath, VortexManagerContext context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contextPath);
        ArgumentNullException.ThrowIfNull(context);
        string path = Path.Combine(context.GameRoot, "archive", "pc", "mod", "modlist.txt");
        string? blocked = context.DeploymentFresh ? null : "Deploy the active Vortex profile before applying an archive order.";
        return new Mo2ArchiveWriteTarget(path, "Vortex", ModManagerKind.Vortex, Path.GetFullPath(contextPath), blocked, context.ContextId, context.ProfileId);
    }
}
