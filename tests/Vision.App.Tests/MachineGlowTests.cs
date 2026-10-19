using Vision.App.Views;
using Vision.Core;
using Vision.Windows;

namespace Vision.App.Tests;

/// <summary>Where the glow's windows go on each monitor (physical pixels, any DPI, monitors left of or
/// above the main one), and that its label is drawn from the shared wording.</summary>
public sealed class MachineGlowTests
{
    [Fact]
    public void EdgesHugTheMonitorWithoutOverlappingAtTheCorners()
    {
        var monitor = new ScreenRect(-1920, 0, 1920, 1080);
        var edges = GlowLook.EdgeRects(monitor, scale: 1.5);
        Assert.Equal(4, edges.Count);
        var band = 24; // 16 device-independent pixels at 150 %
        Assert.Equal(new ScreenRect(-1920, 0, 1920, band), edges.Single(e => e.Side == GlowLook.Side.Top).Rect);
        Assert.Equal(new ScreenRect(-1920, 1080 - band, 1920, band), edges.Single(e => e.Side == GlowLook.Side.Bottom).Rect);
        Assert.Equal(new ScreenRect(-1920, band, band, 1080 - 2 * band), edges.Single(e => e.Side == GlowLook.Side.Left).Rect);
        Assert.Equal(new ScreenRect(-band, band, band, 1080 - 2 * band), edges.Single(e => e.Side == GlowLook.Side.Right).Rect);
        // Every strip lies inside the monitor.
        foreach (var (_, rect) in edges) Assert.Equal(rect, rect.Intersect(monitor));
    }

    [Fact]
    public void ATinyScaleStillDrawsAVisibleBand()
    {
        var edges = GlowLook.EdgeRects(new ScreenRect(0, 0, 800, 600), scale: 0.1);
        Assert.All(edges, e => Assert.True(Math.Min(e.Rect.Width, e.Rect.Height) >= 4));
    }

    [Fact]
    public void TheLabelSitsCentredJustAboveTheTaskbar()
    {
        var monitor = new MonitorInfo(new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1392), 1.25, true);
        var (x, y) = GlowLook.LabelSpot(monitor, width: 600, height: 80);
        Assert.Equal((2560 - 600) / 2, x);
        Assert.Equal(1392 - 80 - 10, y); // 8 device-independent pixels of gap at 125 % (the label's window has 10 of shadow room)
    }

    [Fact]
    public void ThePreviewAndTheLabelBuildFromTheSharedWording()
    {
        UiThread.Run(() =>
        {
            var text = MachineText.Glow([new MachineSessionInfo("a", "Fix login", DateTimeOffset.Now, DateTimeOffset.Now, false)], ArbiterSnapshot.Idle)!;
            var label = GlowLook.Label(text);
            var words = new List<string>();
            void Collect(System.Windows.DependencyObject node)
            {
                if (node is System.Windows.Controls.TextBlock block) words.Add(block.Text);
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(node).OfType<System.Windows.DependencyObject>()) Collect(child);
            }
            Collect(label);
            Assert.Contains("Machine in use by Vision AI", words);
            Assert.Contains("  ·  Ctrl+Alt+. stops", words);
            Assert.Contains("“Fix login” is working — your mouse is free", words);
            Assert.NotNull(GlowLook.Preview(text));
            return Task.CompletedTask;
        });
    }
}
