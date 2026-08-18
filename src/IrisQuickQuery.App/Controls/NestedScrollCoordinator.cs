using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace IrisQuickQuery.App.Controls;

public static class NestedScrollCoordinator
{
    public static void RouteMouseWheel(ScrollViewer outer, MouseWheelEventArgs e)
    {
        var current = e.OriginalSource as DependencyObject;
        while (current is not null)
        {
            if (current is ScrollViewer viewer && TryScroll(viewer, e.Delta))
            {
                e.Handled = true;
                return;
            }

            if (ReferenceEquals(current, outer)) break;
            current = GetParent(current);
        }

        if (TryScroll(outer, e.Delta)) e.Handled = true;
    }

    public static bool TryScrollAncestors(DependencyObject start, int delta)
    {
        var current = GetParent(start);
        while (current is not null)
        {
            if (current is ScrollViewer viewer && TryScroll(viewer, delta)) return true;
            current = GetParent(current);
        }

        return false;
    }

    public static bool TryScroll(ScrollViewer viewer, int delta)
    {
        if (!CanScroll(viewer, delta)) return false;
        Scroll(viewer, delta);
        return true;
    }

    public static bool CanScroll(ScrollViewer viewer, int delta)
    {
        if (viewer.ScrollableHeight <= 0) return false;
        return delta > 0
            ? viewer.VerticalOffset > 0.1
            : viewer.VerticalOffset < viewer.ScrollableHeight - 0.1;
    }

    private static void Scroll(ScrollViewer viewer, int delta)
        => viewer.ScrollToVerticalOffset(Math.Clamp(viewer.VerticalOffset - delta * 0.6, 0, viewer.ScrollableHeight));

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is ContentElement content)
            return ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent;
        if (child is Visual or Visual3D) return VisualTreeHelper.GetParent(child);
        return LogicalTreeHelper.GetParent(child);
    }
}
