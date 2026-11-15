using ConflictStudio.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConflictStudio.App;

public enum UnknownArchiveOrderImpact { None, RequiresAcknowledgement, BlockedCrossing }

public static class ArchiveUnknownImpactPolicy
{
    public static UnknownArchiveOrderImpact Evaluate(IReadOnlyList<string> currentOrder, IReadOnlyList<string> proposedOrder, IReadOnlyList<string> unreadableArchives)
    {
        return (UnknownArchiveOrderImpact)Core.ArchiveUnknownImpactPolicy.Evaluate(currentOrder, proposedOrder, unreadableArchives);
    }
}

public static class ArchiveDragAutoScroll
{
    public static int LinesAt(double pointerY, double viewportHeight, double edgeSize = 64, int maximumLines = 16)
    {
        if (viewportHeight <= 0 || pointerY < 0 || pointerY > viewportHeight) return 0;
        double edge = Math.Min(edgeSize, viewportHeight / 2);
        if (pointerY < edge) return -Speed((edge - pointerY) / edge, maximumLines);
        return pointerY > viewportHeight - edge ? Speed((pointerY - (viewportHeight - edge)) / edge, maximumLines) : 0;
    }

    public static int ConsumeWheelDelta(ref int remainder, int delta)
    {
        remainder += delta;
        int notches = remainder / 120;
        remainder -= notches * 120;
        return -Math.Clamp(notches * 6, -24, 24);
    }

    public static void Scroll(ScrollViewer viewer, int lines)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        for (int index = 0; index < Math.Abs(lines); index++)
        {
            if (lines < 0) viewer.LineUp();
            else viewer.LineDown();
        }
    }

    private static int Speed(double depth, int maximumLines) => Math.Clamp(1 + (int)(Math.Clamp(depth, 0, 1) * Math.Max(0, maximumLines - 1)), 1, Math.Max(1, maximumLines));
}

internal static class ArchiveDragSelection
{
    public static string[] Resolve(string clickedArchive, bool clickedWasSelected, IReadOnlyList<string> selectedAtPress, IReadOnlyList<string> selectedAfterPress, bool selectionModifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clickedArchive);
        ArgumentNullException.ThrowIfNull(selectedAtPress);
        ArgumentNullException.ThrowIfNull(selectedAfterPress);
        if (selectionModifier && !selectedAfterPress.Contains(clickedArchive, StringComparer.OrdinalIgnoreCase)) return [];
        IReadOnlyList<string> selected = selectionModifier ? selectedAfterPress : clickedWasSelected ? selectedAtPress : [clickedArchive];
        return selected.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

public static class ArchiveConflictPreview
{
    public static ArchiveConflictSummary[] Build(IReadOnlyList<ResourceProvider> resources, IReadOnlyList<Mo2Archive> archives, IReadOnlyList<string> order, IReadOnlyList<RdarArchiveFailure> failures)
        => ArchiveResourceIndexBuilder.Build(resources, archives, order, failures);

    public static ResourceProvider[] RehydrateResources(IReadOnlyList<ArchiveConflictSummary> summaries)
    {
        return summaries.SelectMany(summary => summary.Winning.Concat(summary.Losing).Concat(summary.Unresolved).Concat(summary.Unique).Where(outcome => outcome.ResourceHash != 0).Select(outcome => new ResourceProvider(summary.ArchiveName, outcome.ResourceHash, outcome.DisplayName, outcome.RdarPayloadSha1, ResourceType: outcome.ResourceType, PathConfidence: outcome.PathConfidence, ProviderName: summary.Provider, CookedPayloadSha256: outcome.CookedPayloadSha256)))
            .DistinctBy(value => (value.ArchiveName.ToUpperInvariant(), value.ResourceHash))
            .ToArray();
    }
}

public sealed class ArchiveOrderCloseDialog : Window
{
    public ArchiveOrderCloseAction Action { get; private set; } = ArchiveOrderCloseAction.Cancel;

    public ArchiveOrderCloseDialog(bool canApply)
    {
        Title = "Unapplied archive order";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#090F14");
        Foreground = Brush("#F2F6F7");
        FontFamily = new FontFamily("Segoe UI");
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/ConflictStudio;component/StudioTheme.xaml", UriKind.Relative) });

        StackPanel stack = new() { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = canApply ? "Changes haven't been applied" : "Changes aren't ready to apply", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 9, 0, 0), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock
        {
            Text = canApply
                ? "Apply and verify the proposed order before exiting, discard your changes, or keep editing."
                : "Keep editing to resolve what's blocking the proposed order, or discard your changes and exit.",
            Margin = new Thickness(0, 10, 0, 24),
            Foreground = Brush("#A9BBC4"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Button keepEditing = ActionButton("Keep editing", ArchiveOrderCloseAction.Cancel, "QuietButton");
        keepEditing.IsCancel = true;
        actions.Children.Add(keepEditing);
        actions.Children.Add(ActionButton("Discard changes", ArchiveOrderCloseAction.DiscardAndClose, "StudioButton"));
        if (canApply) actions.Children.Add(ActionButton("Apply and exit", ArchiveOrderCloseAction.ApplyAndClose, "PrimaryButton"));
        stack.Children.Add(actions);
        Content = stack;
    }

    private Button ActionButton(string label, ArchiveOrderCloseAction action, string style)
    {
        Button button = new() { Content = label, Style = (Style)FindResource(style), Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 8, 14, 8) };
        button.Click += (_, _) =>
        {
            Action = action;
            DialogResult = true;
        };
        return button;
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}

public enum ArchiveOrderCloseAction { ApplyAndClose, DiscardAndClose, Cancel }

internal enum ArchivePendingCloseDisposition { CloseNow, KeepOpen, ApplyThenClose }

internal static class ArchivePendingClosePolicy
{
    public static ArchivePendingCloseDisposition Resolve(ArchiveOrderCloseAction action)
        => action switch
        {
            ArchiveOrderCloseAction.ApplyAndClose => ArchivePendingCloseDisposition.ApplyThenClose,
            ArchiveOrderCloseAction.DiscardAndClose => ArchivePendingCloseDisposition.CloseNow,
            _ => ArchivePendingCloseDisposition.KeepOpen
        };
}
