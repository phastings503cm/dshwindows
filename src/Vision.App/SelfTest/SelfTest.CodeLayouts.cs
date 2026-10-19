using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.App.Views;

namespace Vision.App;

// MARK: - Code mode layouts: the chat as the main view, the panes on and off

public sealed partial class SelfTest
{
    /// <summary>Code mode with the chat as the main view: beside the file tree, alone with the project status ("Chat
    /// only"), and over the terminal — then back to the editor layout the run started with.</summary>
    private async Task CaptureCodeLayouts(MainWindow window, AppModel model)
    {
        try
        {
            var code = model.Code;
            var before = (code.ShowTree, code.ShowEditor, code.TerminalVisible);
            NoteTranscriptFit(window, "beside the editor");

            code.ShowEditor = false;
            code.TerminalVisible = false;
            code.ShowTree = true;
            await Settle(800);
            Note($"layout: {code.Layout}");
            NoteTranscriptFit(window, "as the main view");
            Capture(window, "code-chat-main");

            code.ToggleChatOnly();
            await Settle(800);
            Note($"layout (chat only): {code.Layout}");
            Capture(window, "code-chat-only");

            code.ToggleChatOnly();
            code.ShowTree = false;
            code.TerminalVisible = true;
            await Settle(1200);
            Note($"layout: {code.Layout}");
            Capture(window, "code-chat-terminal");

            (code.ShowTree, code.ShowEditor, code.TerminalVisible) = before;
            await Settle(800);
            NoteTranscriptFit(window, "beside the editor again");
            Capture(window, "code-chat-side-again");
        }
        catch (Exception error)
        {
            Fail("code-layouts", error);
        }
    }

    /// <summary>Whether the transcript's rows fit its viewport (a row arranged wider than the chat is cut off on the right).</summary>
    private void NoteTranscriptFit(MainWindow window, string where)
    {
        if (window.SelfTestChatView.SelfTestScroller is not { } scroller) return;
        var panel = scroller.FindDescendant<System.Windows.Controls.VirtualizingStackPanel>();
        var widest = 0.0;
        if (panel is not null)
            foreach (var child in panel.Children.OfType<System.Windows.FrameworkElement>())
                widest = Math.Max(widest, child.ActualWidth + child.Margin.Left + child.Margin.Right);
        Note($"transcript {where}: viewport {scroller.ViewportWidth:F0}, extent {scroller.ExtentWidth:F0}, widest row {widest:F0} " +
             $"({(widest <= scroller.ViewportWidth + 1 ? "fits" : "CUT OFF")})");
        if (widest <= scroller.ViewportWidth + 1 || panel is null) return;
        // Where the width comes from: each ancestor's measured (desired) and arranged width, and the widest rows.
        var chain = new List<string>();
        for (System.Windows.DependencyObject? at = scroller; at is not null && chain.Count < 14; at = System.Windows.Media.VisualTreeHelper.GetParent(at))
        {
            if (at is System.Windows.FrameworkElement e)
                chain.Add($"{e.GetType().Name}{(e.Name.Length > 0 ? "#" + e.Name : "")} {e.DesiredSize.Width:F0}/{e.ActualWidth:F0}");
        }
        Note("  ancestors (desired/actual): " + string.Join(" < ", chain));
        foreach (var child in panel.Children.OfType<System.Windows.FrameworkElement>().OrderByDescending(c => c.DesiredSize.Width).Take(3))
            Note($"  row {child.DataContext?.GetType().Name}: desired {child.DesiredSize.Width:F0}, actual {child.ActualWidth:F0}, " +
                 $"panel {panel.DesiredSize.Width:F0}/{panel.ActualWidth:F0}");
    }
}
