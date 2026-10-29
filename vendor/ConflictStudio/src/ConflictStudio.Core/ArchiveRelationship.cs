namespace ConflictStudio.Core;

public enum ArchiveRelationshipKind { None, Wins, Loses, Mixed, Same, Unknown }

public sealed record ArchiveRelationship(string ArchiveName, bool SelectedWins, bool SelectedLoses, bool HasSame, bool HasUnknown, int Position, int TotalCount)
{
    public ArchiveRelationshipKind Tone => Classify(SelectedWins, SelectedLoses, HasSame, HasUnknown);
    public string ToneName => Tone.ToString();
    public string? ColorHex => ArchiveRelationshipPalette.Border(Tone);
    public string? BackgroundHex => ArchiveRelationshipPalette.Background(Tone);

    public static ArchiveRelationshipKind Classify(bool selectedWins, bool selectedLoses, bool same, bool unknown)
        => unknown ? ArchiveRelationshipKind.Unknown
            : selectedWins && selectedLoses ? ArchiveRelationshipKind.Mixed
            : selectedWins ? ArchiveRelationshipKind.Wins
            : selectedLoses ? ArchiveRelationshipKind.Loses
            : same ? ArchiveRelationshipKind.Same
            : ArchiveRelationshipKind.None;
}

public static class ArchiveRelationshipPalette
{
    public static string? Border(ArchiveRelationshipKind tone) => tone switch
    {
        ArchiveRelationshipKind.Wins => "#42E58B",
        ArchiveRelationshipKind.Loses => "#FF304F",
        ArchiveRelationshipKind.Mixed => "#FCEE0A",
        ArchiveRelationshipKind.Same => "#00C8FF",
        ArchiveRelationshipKind.Unknown => "#B48CFF",
        _ => null
    };

    public static string? Background(ArchiveRelationshipKind tone) => tone switch
    {
        ArchiveRelationshipKind.Wins => "#08251B",
        ArchiveRelationshipKind.Loses => "#310913",
        ArchiveRelationshipKind.Mixed => "#282500",
        ArchiveRelationshipKind.Same => "#062431",
        ArchiveRelationshipKind.Unknown => "#1D1230",
        _ => null
    };
}
