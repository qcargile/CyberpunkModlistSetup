using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace ChromeBlood.Presentation;

public static class LauncherScrollRouting
{
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
        _registered = true;
    }

    public static bool RouteWheel(DependencyObject source, int delta)
    {
        if (delta == 0 || SystemParameters.WheelScrollLines == 0)
        {
            return false;
        }

        bool closedCombo = false;
        for (DependencyObject? current = source; current is not null; current = ParentOf(current))
        {
            if (current is HwndHost)
            {
                return false;
            }

            if (current is ComboBox combo)
            {
                if (combo.IsDropDownOpen)
                {
                    return false;
                }

                closedCombo = true;
            }

            if (current is ScrollViewer viewer && CanScroll(viewer, delta))
            {
                Scroll(viewer, delta);
                return true;
            }
        }

        return closedCombo;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer viewer
            || viewer.TryFindResource("LauncherScrollTrackBrush") is null
            || e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        e.Handled = RouteWheel(source, e.Delta);
    }

    private static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled
            || viewer.ScrollableHeight <= 0)
        {
            return false;
        }

        return delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;
    }

    private static void Scroll(ScrollViewer viewer, int delta)
    {
        int lines = SystemParameters.WheelScrollLines;
        if (lines < 0)
        {
            if (delta > 0)
            {
                viewer.PageUp();
            }
            else
            {
                viewer.PageDown();
            }

            return;
        }

        double wheelSteps = Math.Abs((double)delta) / Mouse.MouseWheelDeltaForOneLine;
        if (viewer.CanContentScroll)
        {
            int count = Math.Max(1, (int)Math.Round(lines * wheelSteps));
            for (int index = 0; index < count; index++)
            {
                if (delta > 0)
                {
                    viewer.LineUp();
                }
                else
                {
                    viewer.LineDown();
                }
            }

            return;
        }

        double distance = lines * wheelSteps * 16;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset + (delta > 0 ? -distance : distance));
    }

    private static DependencyObject? ParentOf(DependencyObject current)
    {
        if (current is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return LogicalTreeHelper.GetParent(current);
    }
}
