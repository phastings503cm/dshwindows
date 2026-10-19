using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Vision.App.Infrastructure;

/// <summary>A box of its own height inside the transcript (a long message opened with "Show more", a tool's output) scrolls
/// with the wheel while it has somewhere to go, then hands the wheel to the transcript — instead of either swallowing it at
/// its ends (a plain inner ScrollViewer) or never taking it at all (<see cref="WheelBubbling"/>).</summary>
public static class WheelChaining
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelChaining), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnWheel;
        else element.PreviewMouseWheel -= OnWheel;
    }

    /// <summary>Whether a wheel turn of <paramref name="delta"/> (positive = up) has room to move a box scrolled to
    /// <paramref name="offset"/> of <paramref name="scrollable"/>.</summary>
    public static bool CanScroll(double offset, double scrollable, int delta) =>
        scrollable > 0.5 && (delta > 0 ? offset > 0.5 : offset < scrollable - 0.5);

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source) return;
        // Shift+wheel scrolls a wide block sideways, inside the box.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var scroller = InnerScroller(source);
        if (scroller is not null && CanScroll(scroller.VerticalOffset, scroller.ScrollableHeight, e.Delta)) return;
        e.Handled = true;
        (VisualTreeHelper.GetParent(source) as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }

    private static ScrollViewer? InnerScroller(DependencyObject root)
    {
        if (root is ScrollViewer self) return self;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (InnerScroller(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
