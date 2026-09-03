using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ZoomAutoAdmit.WindowsUI.Infrastructure;

/// <summary>
/// Wheel scrolling for the whole app: the page scrolls smoothly, and a list that has reached its end
/// hands the wheel back to the page instead of swallowing it.
/// </summary>
public static class SmoothScrolling
{
    private const double ItemsPerNotch = 3;
    private static bool _registered;

    public static void Enable()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer viewer || viewer.ScrollableHeight <= 0) return;
        // The event tunnels outside-in: leave it alone while an inner list can still move in this direction.
        if (InnerHasRoom(e.OriginalSource as DependencyObject, viewer, e.Delta)) return;
        double step = viewer.CanContentScroll ? Math.Sign(e.Delta) * ItemsPerNotch : e.Delta;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - step);
        e.Handled = true;
    }

    private static bool InnerHasRoom(DependencyObject? source, ScrollViewer outer, int delta)
    {
        for (var node = source; node != null && !ReferenceEquals(node, outer); node = Parent(node))
        {
            if (node is not ScrollViewer inner || ReferenceEquals(inner, outer) || inner.ScrollableHeight <= 0) continue;
            if (delta < 0 ? inner.VerticalOffset < inner.ScrollableHeight - 0.5 : inner.VerticalOffset > 0.5) return true;
        }
        return false;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node)
            : LogicalTreeHelper.GetParent(node);
}
