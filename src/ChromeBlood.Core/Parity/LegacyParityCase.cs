namespace ChromeBlood.Core.Parity;

public enum LegacyParityCommand
{
    ClassifyGame,
    CleanScan,
    FullClean
}

public sealed record LegacyParityCase(
    string Id,
    LegacyParityCommand Command,
    string ExpectedFile,
    string Sha256);

public sealed record LegacyParityIndex(
    int SchemaVersion,
    string LegacyCommit,
    LegacyParityCase[] Cases);
