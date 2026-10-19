using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Vision.App.Model;
using Vision.App.Views.Chat;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>A very long message collapses and opens in its own box (10-19 #14); Copy gives the whole message — a prompt as it
/// was sent — from the button, the menu and Ctrl+C with nothing selected (10-19 #13).</summary>
public sealed class TranscriptPagingTests
{
    private static string Lines(int count) => string.Join("\n", Enumerable.Range(1, count).Select(i => $"12:00:{i % 60:00} req {i} /reserve"));

    /// <summary>A body in a panel that answers the copy command the way the transcript does, recording what was asked for.</summary>
    private static (MessageBody Body, List<object?> Copied) Host(object row, string text, LongTextKind kind)
    {
        var copied = new List<object?>();
        var body = new MessageBody { Kind = kind, DataContext = row, Text = text };
        var panel = new StackPanel { Children = { body } };
        panel.CommandBindings.Add(new CommandBinding(TranscriptCommands.CopyMessage, (_, e) => copied.Add(e.Parameter), (_, e) => e.CanExecute = true));
        var window = new Window { Content = panel, Width = 800, Height = 600, ShowActivated = false, WindowStyle = WindowStyle.None, Left = -10_000 };
        window.Show();
        window.UpdateLayout();
        return (body, copied);
    }

    [Fact]
    public void ATenThousandLinePromptShowsItsFirstLinesThenOpensInItsOwnBox() => UiThread.Run(() =>
    {
        var text = Lines(10_214);
        var row = new MessageEntryVM(MessageRole.User, text);
        var (body, _) = Host(row, text, LongTextKind.Prompt);

        Assert.True(body.IsCollapsed);
        var preview = Assert.IsType<TextBox>(body.Shown);
        Assert.Equal(LongText.PromptLines, preview.Text.Split('\n').Length);
        Assert.Equal("Show more · 10,174 more lines", body.ToggleLabel);

        body.Toggle();
        body.UpdateLayout();
        Assert.True(body.IsOpen);
        Assert.True(body.InBox);
        var box = Assert.IsAssignableFrom<ListBox>(body.Shown);
        Assert.Equal(10_214, box.Items.Count);
        Assert.True(box.ActualHeight <= MessageBody.BoxHeight + 1, $"the box is {box.ActualHeight} px tall");
        Assert.Equal("Show less", body.ToggleLabel);

        body.Toggle();
        Assert.True(body.IsCollapsed);
        Assert.IsType<TextBox>(body.Shown);
        return Task.CompletedTask;
    });

    [Fact]
    public void AMessageThatFitsInlineOpensInlineAndAShortOneHasNoToggle() => UiThread.Run(() =>
    {
        var text = Lines(120);
        var (body, _) = Host(new MessageEntryVM(MessageRole.User, text), text, LongTextKind.Prompt);
        body.Toggle();
        Assert.False(body.InBox);
        Assert.Equal(text, Assert.IsType<TextBox>(body.Shown).Text);

        var (shortBody, _) = Host(new MessageEntryVM(MessageRole.User, "hello"), "hello", LongTextKind.Prompt);
        Assert.False(shortBody.IsLong);
        Assert.Equal("", shortBody.ToggleLabel);
        return Task.CompletedTask;
    });

    [Fact]
    public void OpenIsRememberedPerRowAcrossRecycledControls() => UiThread.Run(() =>
    {
        var text = Lines(500);
        var first = new MessageEntryVM(MessageRole.User, text);
        var second = new MessageEntryVM(MessageRole.User, text);
        var (body, _) = Host(first, text, LongTextKind.Prompt);
        body.Toggle();
        Assert.True(body.IsOpen);

        // The same control shows another row (recycling): that row is collapsed; back to the first, it is open again.
        body.DataContext = second;
        Assert.True(body.IsCollapsed);
        body.DataContext = first;
        Assert.True(body.IsOpen);
        return Task.CompletedTask;
    });

    [Fact]
    public void ARepliesBarIsHigherAndAToolsOutputStaysInItsBox() => UiThread.Run(() =>
    {
        var reply = Lines(200);
        var (replyBody, _) = Host(new MessageEntryVM(MessageRole.Assistant, reply), reply, LongTextKind.Reply);
        Assert.False(replyBody.IsLong);
        Assert.IsType<MarkdownPresenter>(replyBody.Shown);

        var output = Lines(10_000);
        var tool = new ToolEntryVM("bash", "cat big.log") { Output = output, IsOk = true };
        var (outputBody, _) = Host(tool, output, LongTextKind.Output);
        Assert.True(outputBody.IsCollapsed);
        var box = Assert.IsType<TextBox>(outputBody.Shown);
        Assert.Equal(280, box.MaxHeight);
        outputBody.Toggle();
        Assert.True(outputBody.InBox);
        Assert.Equal(280, outputBody.Shown!.MaxHeight);
        return Task.CompletedTask;
    });

    [Fact]
    public void CtrlCWithNothingSelectedCopiesTheWholeMessage() => UiThread.Run(() =>
    {
        var text = Lines(10_214);
        var row = new MessageEntryVM(MessageRole.User, text);
        var (body, copied) = Host(row, text, LongTextKind.Prompt);
        var preview = Assert.IsType<TextBox>(body.Shown);

        Assert.True(body.CopyKey(Key.C, ModifierKeys.Control));
        Assert.Same(row, Assert.Single(copied));

        // Part of it selected: Ctrl+C is the text box's own copy of the selection.
        preview.Select(0, 10);
        Assert.False(body.CopyKey(Key.C, ModifierKeys.Control));
        // All a collapsed message shows, selected (Ctrl+A): the whole message, not just its first lines.
        preview.SelectAll();
        Assert.True(body.CopyKey(Key.C, ModifierKeys.Control));
        Assert.Equal(2, copied.Count);

        Assert.False(body.CopyKey(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.False(body.CopyKey(Key.V, ModifierKeys.Control));
        return Task.CompletedTask;
    });

    [Fact]
    public void CopyGivesEachKindOfRowWhatItHolds()
    {
        var prompt = Lines(10_214);
        Assert.Equal(prompt, ChatView.CopyText(new MessageEntryVM(MessageRole.User, prompt)));
        Assert.Equal("/goal ship it", ChatView.CopyText(new MessageEntryVM(MessageRole.User, "🎯 /goal ship it")));
        Assert.Equal("tidy the README", ChatView.CopyText(new MessageEntryVM(MessageRole.User, "📋 Task: tidy the README")));
        Assert.Equal("**Done.**", ChatView.CopyText(new MessageEntryVM(MessageRole.Assistant, "**Done.**")));
        Assert.Equal("out", ChatView.CopyText(new ToolEntryVM("bash", "ls") { Output = "out", Summary = "1 line" }));
        Assert.Equal("1 line", ChatView.CopyText(new ToolEntryVM("bash", "ls") { Summary = "1 line" }));
        Assert.Equal("summary", ChatView.CopyText(new CompactionEntryVM(4, "summary")));
        Assert.Null(ChatView.CopyText(null));
    }

    [Fact]
    public void CopyPutsALargePasteBackAfterWhatWasTyped()
    {
        var paste = Lines(10_214);
        var saved = new Dictionary<string, string> { ["Pasted text.txt"] = paste };
        var pasted = new AttachmentBadge("Pasted text.txt", DigestFormat.Pasted.Label() + " · 10,214 lines · 412 KB · sent as text", stored: "Pasted text.txt");
        var file = new AttachmentBadge("notes.md", "Markdown · 2 KB · sent as text", stored: "notes.md");
        var message = new MessageEntryVM(MessageRole.User, "why is this slow?") { Attachments = [pasted, file] };

        Assert.Equal("why is this slow?\n\n" + paste, ChatView.CopyText(message, name => saved.GetValueOrDefault(name)));
        Assert.Equal(paste, ChatView.CopyText(new MessageEntryVM(MessageRole.User, "") { Attachments = [pasted] }, name => saved.GetValueOrDefault(name)));
        // Not saved (or no store to read): the prompt as typed.
        Assert.Equal("why is this slow?", ChatView.CopyText(message, _ => null));
        Assert.Equal("why is this slow?", ChatView.CopyText(message));
    }

    [Fact]
    public void FindSearchesEveryRowOfTheChat()
    {
        var rows = new List<ChatEntryVM>
        {
            new MessageEntryVM(MessageRole.User, "Prompt 1: check SKU-1000"),
            new ToolEntryVM("read_file", "data/stock-1000.csv") { Summary = "20 lines", IsOk = true },
            new MessageEntryVM(MessageRole.Assistant, "SKU-1000 is fine"),
            new CompactionEntryVM(3, "Earlier: the SKU-0999 report"),
            new MessageEntryVM(MessageRole.User, "prompt 2: and sku-1001?"),
        };
        Assert.Equal([rows[0], rows[4]], ChatView.FindIn(rows, "PROMPT"));
        Assert.Equal([rows[1]], ChatView.FindIn(rows, "stock-1000.csv"));
        Assert.Equal([rows[3]], ChatView.FindIn(rows, "sku-0999"));
        Assert.Empty(ChatView.FindIn(rows, "   "));
        Assert.Empty(ChatView.FindIn(rows, "nothing like this"));
    }

    [Fact]
    public void TheCopyButtonSaysCopiedThenGoesBack() => UiThread.Run(async () =>
    {
        var button = new CopyButton();
        Assert.False(button.IsCopied);
        button.ShowCopied();
        Assert.True(button.IsCopied);
        await Task.Delay(CopyButton.CopiedFor + TimeSpan.FromMilliseconds(400));
        Assert.False(button.IsCopied);
    });
}
