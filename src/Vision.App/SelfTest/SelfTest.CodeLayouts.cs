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

            code.ShowEditor = false;
            code.TerminalVisible = false;
            code.ShowTree = true;
            await Settle(800);
            Note($"layout: {code.Layout}");
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
            await Settle(600);
        }
        catch (Exception error)
        {
            Fail("code-layouts", error);
        }
    }
}
