using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Dsh.App.Infrastructure;

/// <summary>A small indeterminate progress ring (WPF has no ProgressRing).</summary>
public sealed class Spinner : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly RotateTransform _rotation = new();

    public Spinner()
    {
        Width = Height = 14;
        IsHitTestVisible = false;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else _rotation.BeginAnimation(RotateTransform.AngleProperty, null); };
    }

    private void Start()
    {
        if (!IsVisible) return;
        var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever };
        _rotation.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var thickness = Math.Max(1.5, size / 8);
        var radius = (size - thickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        _rotation.CenterX = center.X;
        _rotation.CenterY = center.Y;
        var track = Foreground.Clone();
        track.Opacity = 0.2;
        dc.DrawEllipse(null, new Pen(track, thickness), center, radius, radius);
        // A 270° arc.
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X - radius, center.Y);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(radius, radius), 0, true, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        dc.PushTransform(_rotation);
        dc.DrawGeometry(null, new Pen(Foreground, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        dc.Pop();
    }
}
