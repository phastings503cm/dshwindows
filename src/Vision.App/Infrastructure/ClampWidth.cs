using System.Windows;
using System.Windows.Controls;

namespace Vision.App.Infrastructure;

/// <summary>Never asks its parent for more width than it is offered: its content is clipped instead. Without it, one wide
/// bar docked over a pane (code mode's project status bar over the chat) makes the whole pane arrange wider than its column,
/// and everything in it — the chat's transcript — is cut off on the right.</summary>
public sealed class ClampWidth : Decorator
{
    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null) return default;
        Child.Measure(constraint);
        var desired = Child.DesiredSize;
        return new Size(double.IsInfinity(constraint.Width) ? desired.Width : Math.Min(desired.Width, constraint.Width), desired.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}
