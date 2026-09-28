using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Dsh.App.Infrastructure;

/// <summary>Read-only text controls inside the transcript would otherwise swallow the mouse wheel
/// (TextBoxBase has its own ScrollViewer), freezing scrolling while the pointer is over a message.
/// This hands vertical wheel events up to the transcript.</summary>
public static class WheelBubbling
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(WheelBubbling), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        if ((bool)e.NewValue) element.PreviewMouseWheel += OnWheel;
        else element.PreviewMouseWheel -= OnWheel;
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source) return;
        // Shift+wheel scrolls a wide code block sideways; plain wheel goes to the transcript.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        var parent = VisualTreeHelper.GetParent(source) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender,
        });
    }
}

/// <summary>Buttons whose ContextMenu opens on a left click, below (or above) the button — the
/// Windows equivalent of a SwiftUI Menu.</summary>
public static class DropDown
{
    public static readonly DependencyProperty PlacementProperty = DependencyProperty.RegisterAttached(
        "Placement", typeof(System.Windows.Controls.Primitives.PlacementMode), typeof(DropDown),
        new PropertyMetadata(System.Windows.Controls.Primitives.PlacementMode.Bottom));

    public static System.Windows.Controls.Primitives.PlacementMode GetPlacement(DependencyObject d) =>
        (System.Windows.Controls.Primitives.PlacementMode)d.GetValue(PlacementProperty);

    public static void SetPlacement(DependencyObject d, System.Windows.Controls.Primitives.PlacementMode value) =>
        d.SetValue(PlacementProperty, value);

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(DropDown), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject d) => (bool)d.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject d, bool value) => d.SetValue(IsEnabledProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClick;
        if ((bool)e.NewValue) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.ContextMenu is not { } menu) return;
        menu.PlacementTarget = element;
        menu.Placement = GetPlacement(element);
        menu.IsOpen = true;
    }
}

public static class VisualTreeExtensions
{
    public static T? FindDescendant<T>(this DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (child.FindDescendant<T>() is { } nested) return nested;
        }
        return null;
    }

    public static T? FindAncestor<T>(this DependencyObject start) where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
