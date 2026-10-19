using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.App.Views;
using Vision.App.Views.Chat;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

// MARK: - A long chat, measured
//
// Patrick (10-19, #14): "we need to have a 'load more' button if the chat gets too long … an eloquent way to paginate the
// chat as it expands". This step opens a chat the size that asks for it — 2,000 messages and 400 tool cards, one of the
// messages a 10,214-line paste — the way a chat from an earlier launch opens (its rows replayed from the log while the view
// watches), and writes down what that costs: how long the UI thread is blocked, how much memory the open chat holds, how
// many rows the transcript carries and lays out, and how scrolling to the top and back behaves. The "probe long chat"
// lines in the report compare a run before the change with one after it.

public sealed partial class SelfTest
{
    /// <summary>Messages in the probe's chat (prompts and replies), besides its tool cards.</summary>
    internal const int LongChatMessages = 2_000;
    /// <summary>Lines in the one very long message (the paste Patrick reported).</summary>
    internal const int LongChatLongLines = 10_214;

    private async Task ProbeLongChat(MainWindow window, AppModel model)
    {
        try
        {
            model.Mode = WorkspaceMode.Chat;
            var previous = model.Host.SelectedId;
            var chat = model.Host.NewSession(Project, select: false);
            chat.Title = "A long chat";
            var watch = Stopwatch.StartNew();
            var rows = LongChatRows(chat.Id, out var longText);
            model.Host.Log.Resync(chat.Id, rows);
            Note($"probe long chat: {LongChatMessages:N0} messages + {rows.Count - LongChatMessages:N0} tool cards ({rows.Count:N0} rows), one message " +
                 $"of {LongChatLongLines:N0} lines ({Encoding.UTF8.GetByteCount(longText) / 1024:N0} KB); stored in {watch.ElapsedMilliseconds:N0} ms");

            var before = LongChatMemory();
            // Open it the way a click in the sidebar does: the view attaches, then the rows are replayed into the chat.
            watch.Restart();
            model.Select(chat.Id);
            var selectMs = watch.ElapsedMilliseconds;
            window.UpdateLayout();
            var layoutMs = watch.ElapsedMilliseconds;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var settledMs = watch.ElapsedMilliseconds;
            var view = LongChatView(window) ?? throw new InvalidOperationException("no chat view");
            var scroller = view.Transcript.Template.FindName("Scroller", view.Transcript) as ScrollViewer
                           ?? throw new InvalidOperationException("no transcript scroll viewer");
            var lag = await LongChatBusy(window, 2000);
            Note($"probe long chat: open — select + replay {selectMs:N0} ms, first layout at {layoutMs:N0} ms, idle at {settledMs:N0} ms; " +
                 $"{LongChatShape(view, scroller)}; {lag}");
            var open = LongChatMemory();
            Note($"probe long chat: memory with it open — {LongChatDelta(before, open)}");
            await Settle(400);
            Capture(window, "long-chat");

            // To the top, as the reader would drag the scrollbar there: through every row (old) or to the first loaded one
            // (windowed: "Load earlier messages" is there, and arriving at the top brings the page before).
            var pager = view.SelfTestPagingWindow ?? throw new InvalidOperationException("no transcript window");
            Note($"probe paging: the window holds {pager.Items.Count:N0} of {chat.Entries.Count:N0} rows ({pager.Earlier:N0} earlier, " +
                 $"{pager.Later:N0} newer, {(pager.IsLive ? "live" : "NOT LIVE")}); pinned {view.SelfTestPagingPinned}");
            // The wheel first, as a reader starts: 10 notches up from the end (before the change: 0 px, snapped back).
            var events = new List<string>();
            void Log(object? sender, ScrollChangedEventArgs e)
            {
                if (events.Count < 12)
                    events.Add($"Δ{e.VerticalChange:F0}/ext{e.ExtentHeightChange:+0;-0;0}/vp{e.ViewportHeightChange:+0;-0;0}→{e.VerticalOffset:F0} reader {view.SelfTestPagingReader} pinned {view.SelfTestPagingPinned}");
            }
            scroller.ScrollChanged += Log;
            var wheelStart = scroller.VerticalOffset;
            var wheelTarget = scroller.InputHitTest(new Point(scroller.ActualWidth / 2, scroller.ActualHeight / 2)) as UIElement;
            await LongChatWheel(wheelTarget, 10, 120);
            scroller.UpdateLayout();
            Note($"probe paging: 10 wheel notches up from the end moved the transcript {wheelStart - scroller.VerticalOffset:F0} px; pinned {view.SelfTestPagingPinned}; " +
                 $"events: {string.Join(" | ", events)}");
            events.Clear();

            watch.Restart();
            view.SelfTestPagingAsReader(scroller.ScrollToTop);
            var readerBefore = view.SelfTestPagingReader;
            window.UpdateLayout();
            var topMs = watch.ElapsedMilliseconds;
            Note($"probe long chat: scrolled to the top in {topMs:N0} ms; {LongChatShape(view, scroller)}; reader {readerBefore}; events: {string.Join(" | ", events)}");
            scroller.ScrollChanged -= Log;
            Capture(window, "long-chat-top");
            var earlier = pager.Earlier;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Settle(300);
            Note($"probe paging: arriving at the top loaded {earlier - pager.Earlier:N0} earlier rows ({pager.Earlier:N0} left) and held the " +
                 $"reader's place — {LongChatShape(view, scroller)}");
            if (pager.Earlier >= earlier) Fail("probe-long-chat", new InvalidOperationException("scrolling to the top didn't load the page before"));

            // "Load earlier messages" pressed: how long it takes, and how far the row being read moves (it shouldn't).
            for (var i = 0; i < 3; i++)
            {
                var anchor = view.SelfTestPagingAnchor() ?? throw new InvalidOperationException("no row in view");
                watch.Restart();
                var count = view.LoadEarlier();
                var loadMs = watch.ElapsedMilliseconds;
                window.UpdateLayout();
                var container = view.SelfTestPagingContainer(anchor.Row);
                var top = container?.TranslatePoint(new Point(0, 0), scroller).Y ?? double.NaN;
                Note($"probe paging: Load earlier #{i + 1} brought {count} rows in {loadMs:N0} ms; the row being read moved {top - anchor.Top:+0.0;-0.0;0} px; " +
                     $"window {pager.Start:N0}–{pager.End:N0} ({pager.Items.Count} rows, {(pager.IsLive ? "live" : $"{pager.Later:N0} newer")})");
                if (double.IsNaN(top) || Math.Abs(top - anchor.Top) > 2) Fail("probe-long-chat", new InvalidOperationException($"the row being read moved {top - anchor.Top} px"));
            }

            // Jump to an old prompt (what the Prompts timeline and search do): the window moves to it, it is in view and flashes.
            var prompts = chat.Entries.OfType<MessageEntryVM>().Where(m => m.IsUser).ToList();
            var target = prompts[150];
            var targetIndex = chat.Entries.IndexOf(target);
            watch.Restart();
            var jumped = view.ShowEntry(target.Id);
            var jumpMs = watch.ElapsedMilliseconds;
            window.UpdateLayout();
            var shown = view.SelfTestPagingContainer(target);
            var shownTop = shown?.TranslatePoint(new Point(0, 0), scroller).Y ?? double.NaN;
            Note($"probe paging: jump to prompt 151 (row {targetIndex:N0}) {(jumped ? "done" : "FAILED")} in {jumpMs:N0} ms — window {pager.Start:N0}–{pager.End:N0} " +
                 $"({pager.Earlier:N0} earlier, {pager.Later:N0} newer); its top is {shownTop:F0} px into a {scroller.ViewportHeight:F0} px viewport");
            if (!jumped || double.IsNaN(shownTop) || shownTop < 0 || shownTop > scroller.ViewportHeight / 3) Fail("probe-long-chat", new InvalidOperationException("the jumped-to prompt isn't in view"));
            await Settle(350);
            Capture(window, "long-chat-jump");

            // Find (Ctrl+F) searches every row, loaded or not, and jumps to the newest match.
            view.SelfTestFind("Prompt 42:");
            await Settle(350);
            var found = prompts[41];
            var foundRow = view.SelfTestPagingContainer(found);
            var foundTop = foundRow?.TranslatePoint(new Point(0, 0), scroller).Y ?? double.NaN;
            Note($"probe find: “Prompt 42:” — {view.SelfTestFindCount}; window {pager.Start:N0}–{pager.End:N0}; the match's top is {foundTop:F0} px into the viewport");
            if (foundRow is null || foundTop < 48 || foundTop > scroller.ViewportHeight / 3) Fail("probe-long-chat", new InvalidOperationException("Find didn't bring its match into view"));
            Capture(window, "long-chat-find");
            view.SelfTestCloseFind();

            // Back to the newest page, then a reply streamed in: the transcript follows it to the end.
            view.JumpToLatest();
            await Settle(400);
            var reply = new StringBuilder();
            var followed = 0;
            for (var i = 0; i < 40; i++)
            {
                var chunk = i % 8 == 7 ? $"line {i}\n\n" : $"word{i} ";
                reply.Append(chunk);
                chat.AppendDelta(chunk);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (scroller.VerticalOffset >= scroller.ScrollableHeight - 2) followed++;
            }
            chat.EndStreaming();
            Note($"probe paging: back to the latest ({(pager.IsLive ? "live" : "NOT LIVE")}, pinned {view.SelfTestPagingPinned}); a streamed reply of 40 chunks " +
                 $"was followed to the end after {followed} of them; window {pager.Items.Count} rows");
            if (followed < 38) Fail("probe-long-chat", new InvalidOperationException("the streamed reply wasn't followed"));

            // A long session followed at its bottom: the window lets go of its oldest rows.
            for (var i = 0; i < 400; i++) chat.AppendMessage(i % 2 == 0 ? MessageRole.User : MessageRole.Assistant, $"follow-up {i}");
            await Settle(300);
            Note($"probe paging: 400 more rows while following the end — the window holds {pager.Items.Count} ({pager.Earlier:N0} earlier); " +
                 $"at the end: {scroller.VerticalOffset >= scroller.ScrollableHeight - 2}");
            if (pager.Items.Count > pager.MaxRows + pager.PageSize) Fail("probe-long-chat", new InvalidOperationException("the live window kept growing"));

            // A scroll up that no input marked as the reader's (UI Automation, a screen reader) also stays away from the end. Where
            // exactly it lands is the virtualizing panel's (it re-estimates rows it hasn't laid out), so the check is that it
            // didn't go back to the end and the transcript stopped following.
            var end = scroller.VerticalOffset;
            scroller.ScrollToVerticalOffset(end - 600);
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var up = scroller.ScrollableHeight - scroller.VerticalOffset;
            Note($"probe paging: a scroll up 600 px from the end without input stayed {end - scroller.VerticalOffset:F0} px up " +
                 $"({up:F0} px from the end); pinned {view.SelfTestPagingPinned}");
            if (up <= 32 || view.SelfTestPagingPinned) Fail("probe-long-chat", new InvalidOperationException("a scroll up from the end snapped back"));

            // The very long prompt: collapsed, Copy gives it back whole, Show more opens it in its own box, Show less closes it.
            var longRow = chat.Entries.OfType<MessageEntryVM>().First(m => ReferenceEquals(m.Text, longText) || m.Text.Length == longText.Length);
            view.ShowEntry(longRow.Id);
            // Past the jump's flash, so the pictures show the row as it is.
            await Settle(2100);
            var row = view.SelfTestPagingContainer(longRow) ?? throw new InvalidOperationException("the long prompt's row isn't laid out");
            var body = LongChatFind<MessageBody>(row) ?? throw new InvalidOperationException("no message body");
            Note($"probe paging: the {LongChatLongLines:N0}-line prompt's row is {row.ActualHeight:F0} px tall, collapsed {body.IsCollapsed}, “{body.ToggleLabel}”");
            if (!body.IsCollapsed || row.ActualHeight > 1500) Fail("probe-long-chat", new InvalidOperationException("the long prompt isn't collapsed"));
            await CheckCopy(window, view, body, longRow, longText);

            // (Copy focused a reply, which scrolled it into view: back to the long prompt.)
            view.ShowEntry(longRow.Id);
            await Settle(2100);
            row = view.SelfTestPagingContainer(longRow) ?? throw new InvalidOperationException("the long prompt's row isn't laid out");
            body = LongChatFind<MessageBody>(row) ?? throw new InvalidOperationException("no message body");
            watch.Restart();
            body.Toggle();
            window.UpdateLayout();
            var openMs = watch.ElapsedMilliseconds;
            var box = body.Shown;
            var boxLines = box is null ? 0 : LongChatFind<VirtualizingStackPanel>(box)?.Children.Count ?? 0;
            Note($"probe paging: Show more in {openMs:N0} ms — {(body.InBox ? "in its own box" : "INLINE")} {box?.ActualHeight:F0} px tall with {boxLines} of " +
                 $"{LongChatLongLines:N0} lines laid out; the row is {row.ActualHeight:F0} px; “{body.ToggleLabel}”");
            if (!body.InBox || row.ActualHeight > MessageBody.BoxHeight + 300) Fail("probe-long-chat", new InvalidOperationException("Show more didn't open the box"));
            var lag2 = await LongChatLag(window, 1000);
            Note($"probe paging: with it open, {lag2}");
            await Settle(300);
            Capture(window, "long-chat-show-more");

            // The wheel over the open box scrolls the box; at its end it hands the wheel to the transcript.
            var inner = box is null ? null : LongChatFind<ScrollViewer>(box);
            if (inner is not null)
            {
                var innerStart = inner.VerticalOffset;
                var outerStart = scroller.VerticalOffset;
                var under = box!.InputHitTest(new Point(box.ActualWidth / 2, box.ActualHeight / 2)) as UIElement ?? box;
                await LongChatWheel(under, 5, -120);
                inner.UpdateLayout();
                Note($"probe paging: 5 notches down over the box moved the box {inner.VerticalOffset - innerStart:F0} px and the transcript {scroller.VerticalOffset - outerStart:F0} px");
                inner.ScrollToEnd();
                inner.UpdateLayout();
                outerStart = scroller.VerticalOffset;
                under = box.InputHitTest(new Point(box.ActualWidth / 2, box.ActualHeight / 2)) as UIElement ?? box;
                await LongChatWheel(under, 3, -120);
                scroller.UpdateLayout();
                Note($"probe paging: at the box's end, 3 more notches moved the transcript {scroller.VerticalOffset - outerStart:F0} px");
            }
            body.Toggle();
            window.UpdateLayout();
            Note($"probe paging: Show less — the row is {row.ActualHeight:F0} px again, “{body.ToggleLabel}”");

            // Closed again (the rows stay loaded in the chat): what the open view itself was holding.
            model.Select(previous);
            await Settle(600);
            var closed = LongChatMemory();
            Note($"probe long chat: memory with it closed again (rows still loaded) — {LongChatDelta(before, closed)}; " +
                 $"the open transcript held {(open.Heap - closed.Heap) / 1048576.0:F1} MB managed, {(open.Private - closed.Private) / 1048576.0:F1} MB private");
        }
        catch (Exception error)
        {
            Fail("probe-long-chat", error);
        }
    }

    /// <summary>Copy on the very long prompt (its Copy button, then Ctrl+C in it with nothing selected) gives back exactly what
    /// was sent, and the row says "Copied".</summary>
    private async Task CheckCopy(MainWindow window, ChatView view, MessageBody body, MessageEntryVM prompt, string sent)
    {
        var button = CopyButton.InRow(body) ?? throw new InvalidOperationException("the prompt has no Copy button");
        try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { }
        // A click, as UI Automation (and so a screen reader) makes one.
        (new System.Windows.Automation.Peers.ButtonAutomationPeer(button).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)
            as System.Windows.Automation.Provider.IInvokeProvider)?.Invoke();
        await Settle(150);
        var copied = LongChatClipboard();
        Note($"probe copy: Copy on the {LongChatLongLines:N0}-line prompt put {copied?.Length ?? 0:N0} characters on the clipboard — " +
             $"{(copied == sent ? "exactly the prompt as sent" : copied is null ? "CLIPBOARD UNREADABLE" : "NOT THE PROMPT")}; the button says “{(button.IsCopied ? "Copied" : "Copy")}”");
        if (copied is not null && copied != sent) Fail("probe-long-chat", new InvalidOperationException("Copy didn't give back the prompt"));
        Capture(window, "long-chat-copied");

        // Ctrl+C in a short reply with nothing selected copies the whole reply.
        var replyRow = view.SelfTestPagingWindow?.Items.OfType<MessageEntryVM>().LastOrDefault(m => m.IsAssistant && m.Text.Length > 20);
        if (replyRow is not null && view.SelfTestPagingContainer(replyRow) is { } replyContainer && LongChatFind<MessageBody>(replyContainer) is { } replyBody
            && replyBody.Shown is Control text)
        {
            try { Clipboard.Clear(); } catch (System.Runtime.InteropServices.ExternalException) { }
            text.Focus();
            // Ctrl+C with nothing selected (the self-test can't hold the real Ctrl key, so it hands the key and modifier over).
            var handled = replyBody.CopyKey(Key.C, ModifierKeys.Control);
            await Settle(150);
            var reply = LongChatClipboard();
            Note($"probe copy: Ctrl+C in a reply with nothing selected {(handled ? "copied" : "DID NOT COPY")} " +
                 $"{(reply == replyRow.Text ? "exactly the reply" : reply is null ? "nothing readable" : "SOMETHING ELSE")} ({reply?.Length ?? 0:N0} characters); " +
                 $"a /goal prompt copies as “{TranscriptCopy.Prompt("🎯 /goal ship the release")}”");
            if (!handled || (reply is not null && reply != replyRow.Text)) Fail("probe-long-chat", new InvalidOperationException("Ctrl+C didn't copy the reply"));
        }
    }

    private static string? LongChatClipboard()
    {
        for (var i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(50); }
        }
        return null;
    }

    private static T? LongChatFind<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (LongChatFind<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>The probe chat's stored rows: <see cref="LongChatMessages"/> prompts and replies with a tool card every
    /// few turns, the second-to-last prompt being the very long one.</summary>
    internal static List<LogItemRow> LongChatRows(string chatId, out string longText)
    {
        longText = LongChatText(LongChatLongLines);
        var rows = new List<LogItemRow>(LongChatMessages + LongChatMessages / 5);
        var at = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
        void Add(string kind, string text, string? tool = null, string? args = null, string? output = null)
        {
            rows.Add(new LogItemRow(chatId, rows.Count, kind, text, tool, args, output, false, at));
            at = at.AddSeconds(37);
        }
        var turns = LongChatMessages / 2;
        for (var turn = 0; turn < turns; turn++)
        {
            var sku = 1000 + turn;
            Add("user", turn == turns - 2
                ? longText
                : turn % 7 == 0
                    ? $"Prompt {turn + 1}: the stock report for SKU-{sku} looks off.\nCompare it with yesterday's export\nand tell me which rows changed."
                    : $"Prompt {turn + 1}: check the stock level for SKU-{sku} and tell me what changed.");
            if (turn % 5 < 2)
            {
                var output = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"{i,4}  SKU-{sku}  level {(sku * i) % 97,3}  bin A{i % 9}"));
                Add("tool", "20 lines", "read_file", $"data/stock-{sku}.csv", output);
            }
            var reply = new StringBuilder();
            reply.Append("Checked **SKU-").Append(sku).Append("**. Here is what changed since yesterday:\n\n");
            for (var i = 0; i < 6; i++) reply.Append("- Bin A").Append(i).Append(": ").Append((sku * (i + 3)) % 41).Append(" units (was ").Append((sku * (i + 5)) % 43).Append(")\n");
            if (turn % 3 == 0) reply.Append("\n```csharp\nvar level = stock.Remove(\"SKU-").Append(sku).Append("\", 2);\nConsole.WriteLine(level);\n```\n");
            reply.Append("\nNothing else moved.");
            Add("assistant", reply.ToString());
        }
        return rows;
    }

    /// <summary>A pasted log, about 40 bytes a line (10,214 lines ≈ 412 KB), with a line long enough to wrap every fifty.</summary>
    internal static string LongChatText(int lines)
    {
        var builder = new StringBuilder(lines * 42);
        var start = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < lines; i++)
        {
            builder.Append(start.AddMilliseconds(i * 137).ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture))
                .Append(" req ").Append(4711 + i).Append(' ').Append(3 + i * 31 % 900).Append(" ms /");
            builder.Append((i % 5) switch { 0 => "reserve", 1 => "levels", 2 => "sku", 3 => "report", _ => "health" });
            if (i % 50 == 0) builder.Append(" detail=").Append(new string('x', 160));
            if (i < lines - 1) builder.Append('\n');
        }
        return builder.ToString();
    }

    private static ChatView? LongChatView(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ChatView { IsVisible: true } view) return view;
            if (LongChatView(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>"2,401 items, 9 rows laid out (tallest 913 px), extent 61,240 viewport 640 offset 60,600".</summary>
    private static string LongChatShape(ChatView view, ScrollViewer scroller)
    {
        var panel = LongChatPanel(view.Transcript);
        var realized = 0;
        var tallest = 0.0;
        if (panel is not null)
        {
            foreach (var child in panel.Children.OfType<FrameworkElement>())
            {
                realized++;
                tallest = Math.Max(tallest, child.ActualHeight);
            }
        }
        return $"{view.Transcript.Items.Count:N0} items, {realized:N0} rows laid out (tallest {tallest:N0} px), " +
               $"extent {scroller.ExtentHeight:N0} viewport {scroller.ViewportHeight:N0} offset {scroller.VerticalOffset:N0}";
    }

    private static VirtualizingStackPanel? LongChatPanel(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is VirtualizingStackPanel panel) return panel;
            if (LongChatPanel(child) is { } found) return found;
        }
        return null;
    }

    private readonly record struct LongChatBytes(long Heap, long WorkingSet, long Private);

    private static LongChatBytes LongChatMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heap = GC.GetTotalMemory(forceFullCollection: true);
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return new LongChatBytes(heap, process.WorkingSet64, process.PrivateMemorySize64);
    }

    private static string LongChatDelta(LongChatBytes from, LongChatBytes to) =>
        $"managed heap {to.Heap / 1048576.0:F1} MB ({(to.Heap - from.Heap) / 1048576.0:+0.0;-0.0} MB), " +
        $"working set {to.WorkingSet / 1048576.0:F0} MB ({(to.WorkingSet - from.WorkingSet) / 1048576.0:+0;-0} MB), " +
        $"private {to.Private / 1048576.0:F0} MB ({(to.Private - from.Private) / 1048576.0:+0;-0} MB)";

    /// <summary>For <paramref name="milliseconds"/>: how long an Input-priority callback waits for the UI thread (what a click
    /// or the wheel waits) and how many layout passes run.</summary>
    private static async Task<string> LongChatLag(Window window, int milliseconds)
    {
        var layouts = 0;
        void Count(object? sender, EventArgs e) => layouts++;
        window.LayoutUpdated += Count;
        var lags = new List<long>();
        var total = Stopwatch.StartNew();
        try
        {
            while (total.ElapsedMilliseconds < milliseconds)
            {
                var posted = Stopwatch.StartNew();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
                lags.Add(posted.ElapsedMilliseconds);
                await Task.Delay(40);
            }
        }
        finally
        {
            window.LayoutUpdated -= Count;
        }
        lags.Sort();
        return $"input waits max {lags.LastOrDefault():N0} ms over {lags.Count} samples, {layouts:N0} layout passes in {milliseconds / 1000.0:F0} s";
    }

    /// <summary><see cref="LongChatLag"/>, and what kept the UI thread busy meanwhile: the longest dispatcher operations by
    /// priority and method.</summary>
    private static async Task<string> LongChatBusy(Window window, int milliseconds)
    {
        var hooks = window.Dispatcher.Hooks;
        var started = new Dictionary<DispatcherOperation, long>();
        var spent = new Dictionary<string, (long Ms, int Count)>();
        var clock = Stopwatch.StartNew();
        var method = typeof(DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        void Start(object? sender, DispatcherHookEventArgs e) => started[e.Operation] = clock.ElapsedMilliseconds;
        void Done(object? sender, DispatcherHookEventArgs e)
        {
            if (!started.Remove(e.Operation, out var at)) return;
            var name = method?.GetValue(e.Operation) is Delegate d ? $"{d.Method.DeclaringType?.Name}.{d.Method.Name}" : "?";
            var key = $"{e.Operation.Priority}:{name}";
            var (ms, count) = spent.GetValueOrDefault(key);
            spent[key] = (ms + clock.ElapsedMilliseconds - at, count + 1);
        }
        hooks.OperationStarted += Start;
        hooks.OperationCompleted += Done;
        hooks.OperationAborted += Done;
        string lag;
        try
        {
            lag = await LongChatLag(window, milliseconds);
        }
        finally
        {
            hooks.OperationStarted -= Start;
            hooks.OperationCompleted -= Done;
            hooks.OperationAborted -= Done;
        }
        var top = spent.OrderByDescending(p => p.Value.Ms).Take(4).Select(p => $"{p.Key} {p.Value.Ms:N0} ms ×{p.Value.Count}");
        return $"{lag}; busiest: {string.Join(", ", top)}";
    }

    /// <summary>Raise the wheel the way the input system does (tunnel, then bubble).</summary>
    private static async Task LongChatWheel(UIElement? target, int notches, int delta)
    {
        if (target is null) return;
        for (var i = 0; i < notches; i++)
        {
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            target.RaiseEvent(preview);
            if (!preview.Handled)
            {
                target.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent });
            }
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
    }
}
