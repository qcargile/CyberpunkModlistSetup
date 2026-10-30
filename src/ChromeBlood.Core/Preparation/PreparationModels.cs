namespace ChromeBlood.Core.Preparation;

public enum CheckState { Ready, Needed, Action, Optional, Failed }

public sealed record PreparationCheck(string Id, string Title, string Detail, CheckState Status, bool Required, string? ActionUrl = null);

public sealed record GameCandidate(string Root, string Store, string Version, string BuildId);

public sealed record CleanupItemLabel(string Path, string Reason, string Scope = "Game");

public sealed record PreparationSnapshot
{
    public IReadOnlyList<GameCandidate> GameCandidates { get; init; } = [];
    public string? SelectedGameRoot { get; init; }
    public string? GameRootIdentity { get; init; }
    public string WabbajackDirectory { get; init; } = "";
    public IReadOnlyList<PreparationCheck> Checks { get; init; } = [];
    public IReadOnlyList<CleanupItemLabel> CleanupItems { get; init; } = [];
    public bool PendingCleanup { get; init; }
    public bool CleanupIssue { get; init; }
    public string CleanupReferenceDetail { get; init; } = "";
    public bool GameFileHashesChecked { get; init; }
    public bool GameFileReferenceMatched { get; init; }
    public bool GameFileScanComplete { get; init; }
    public DateTimeOffset? GameFileVerifiedAt { get; init; }
    public int ChangedOfficialFiles { get; init; }
    public int MissingOfficialFiles { get; init; }
    public int UnreadableGameFiles { get; init; }
    public int PreservedDlssFiles { get; init; }
    public long CleanupBytes { get; init; }
    public string PlannedBackupRoot { get; init; } = "";
    public string? RecoveryDetail { get; init; }
    public bool ReadyForHandoff { get; init; }
}

public sealed record PreparationOptions
{
    public bool InstallRuntimes { get; init; } = true;
    public bool EnableLongPaths { get; init; } = true;
    public bool DownloadWabbajack { get; init; } = true;
    public bool CleanGame { get; init; }
    public bool ResetSettings { get; init; }
    public bool ClearDiagnostics { get; init; }
    public bool SteamLaunchOnly { get; init; }
    public bool LegacyDirectX { get; init; }
}

public sealed record PreparationProgress(string Title, string Detail = "", double? Fraction = null);

public sealed record PreparationResult(
    PreparationSnapshot Snapshot,
    bool Completed,
    bool Cancelled,
    string Message,
    string? BackupRoot = null,
    bool RebootRequired = false);
