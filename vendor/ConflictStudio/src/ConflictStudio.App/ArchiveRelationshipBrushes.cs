using System.Windows.Media;
using ConflictStudio.Core;

namespace ConflictStudio.App;

public static class ArchiveRelationshipBrushes
{
    public static Brush WinsBorder { get; } = Border(ArchiveRelationshipKind.Wins);
    public static Brush LosesBorder { get; } = Border(ArchiveRelationshipKind.Loses);
    public static Brush MixedBorder { get; } = Border(ArchiveRelationshipKind.Mixed);
    public static Brush SameBorder { get; } = Border(ArchiveRelationshipKind.Same);
    public static Brush UnknownBorder { get; } = Border(ArchiveRelationshipKind.Unknown);
    public static Brush WinsBackground { get; } = Background(ArchiveRelationshipKind.Wins);
    public static Brush LosesBackground { get; } = Background(ArchiveRelationshipKind.Loses);
    public static Brush MixedBackground { get; } = Background(ArchiveRelationshipKind.Mixed);
    public static Brush SameBackground { get; } = Background(ArchiveRelationshipKind.Same);
    public static Brush UnknownBackground { get; } = Background(ArchiveRelationshipKind.Unknown);

    private static SolidColorBrush Border(ArchiveRelationshipKind tone) => Create(ArchiveRelationshipPalette.Border(tone)!);
    private static SolidColorBrush Background(ArchiveRelationshipKind tone) => Create(ArchiveRelationshipPalette.Background(tone)!);

    private static SolidColorBrush Create(string color)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
