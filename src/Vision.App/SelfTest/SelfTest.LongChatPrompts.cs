using System.Windows;
using Vision.App.Model;
using Vision.App.Views;

namespace Vision.App;

// MARK: - The long chat's Prompts timeline (10-19 #13, #14 with night/runs' Prompts)
//
// The 2,400-row chat of the long-chat probe has a Prompts timeline backfilled from its rows. Jump on an old prompt goes
// through the paged transcript (the window moves to the row), and Copy on the 10,214-line prompt gives it back whole.

public sealed partial class SelfTest
{
    private async Task ProbeLongChatPrompts(MainWindow window, AppModel model)
    {
        try
        {
            var chat = model.Host.Sessions.FirstOrDefault(s => s.Title == "A long chat") ?? throw new InvalidOperationException("no long chat");
            model.Mode = WorkspaceMode.Chat;
            model.Select(chat.Id);
            await Settle(600);
            var records = model.Host.PromptsOf(chat);
            var target = records.FirstOrDefault(r => r.Text.StartsWith("Prompt 300:", StringComparison.Ordinal))
                         ?? throw new InvalidOperationException("no record of Prompt 300");
            window.ShowPromptsPanel(target.Id);
            await Settle(600);
            window.SelfTestJumpToPrompt(target);
            await Settle(500);
            var view = window.SelfTestChatView;
            var scroller = view.SelfTestPagingScroller ?? throw new InvalidOperationException("no transcript scroll viewer");
            var pager = view.SelfTestPagingWindow ?? throw new InvalidOperationException("no transcript window");
            var entryId = model.Host.EntryIdFor(target, chat);
            var entry = chat.Entries.FirstOrDefault(e => e.Id == entryId);
            var row = entry is null ? null : view.SelfTestPagingContainer(entry);
            var top = row?.TranslatePoint(new Point(0, 0), scroller).Y ?? double.NaN;
            Note($"probe prompts: {records.Count:N0} prompts in the long chat's timeline; Jump to “Prompt 300” — window {pager.Start:N0}–{pager.End:N0} " +
                 $"({pager.Earlier:N0} earlier, {pager.Later:N0} newer), its top {top:F0} px into the viewport");
            if (row is null || top < 0 || top > scroller.ViewportHeight / 3) Fail("probe-long-chat-prompts", new InvalidOperationException("Jump didn't show the prompt"));
            Capture(window, "long-chat-prompts-jump");

            var sent = LongChatText(LongChatLongLines);
            var longRecord = records.FirstOrDefault(r => r.Text.Length == sent.Length);
            var copied = longRecord is null ? null : model.Host.PromptCopyText(longRecord);
            Note($"probe prompts: Copy on the {LongChatLongLines:N0}-line prompt's record gives {copied?.Length ?? 0:N0} characters — " +
                 $"{(copied == sent ? "exactly the prompt as sent" : "NOT THE PROMPT")}");
            if (copied != sent) Fail("probe-long-chat-prompts", new InvalidOperationException("Copy in Prompts didn't give the prompt back"));
            window.ShowQueuePanel(false);
        }
        catch (Exception error)
        {
            Fail("probe-long-chat-prompts", error);
        }
    }
}
