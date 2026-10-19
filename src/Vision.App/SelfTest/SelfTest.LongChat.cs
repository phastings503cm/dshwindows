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
            var lag = await LongChatLag(window, 2000);
            Note($"probe long chat: open — select + replay {selectMs:N0} ms, first layout at {layoutMs:N0} ms, idle at {settledMs:N0} ms; " +
                 $"{LongChatShape(view, scroller)}; {lag}");
            var open = LongChatMemory();
            Note($"probe long chat: memory with it open — {LongChatDelta(before, open)}");
            await Settle(400);
            Capture(window, "long-chat");

            // To the top: through every row (old) or to the first loaded one (windowed).
            watch.Restart();
            scroller.ScrollToTop();
            window.UpdateLayout();
            var topMs = watch.ElapsedMilliseconds;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Note($"probe long chat: scrolled to the top in {topMs:N0} ms (idle at {watch.ElapsedMilliseconds:N0} ms); {LongChatShape(view, scroller)}");
            await Settle(400);
            Capture(window, "long-chat-top");

            // The wheel over the very long message, and the scrollbar's worth: how far one notch goes and how many it takes.
            watch.Restart();
            scroller.ScrollToEnd();
            window.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Note($"probe long chat: back to the bottom in {watch.ElapsedMilliseconds:N0} ms; {LongChatShape(view, scroller)}");
            var start = scroller.VerticalOffset;
            var target = scroller.InputHitTest(new Point(scroller.ActualWidth / 2, scroller.ActualHeight / 3)) as UIElement;
            await LongChatWheel(target, 10, 120);
            scroller.UpdateLayout();
            var moved = start - scroller.VerticalOffset;
            Note($"probe long chat: 10 wheel notches up moved the transcript {moved:F0} px; " +
                 $"top to bottom is {scroller.ExtentHeight / Math.Max(1, scroller.ViewportHeight):N0} screens");

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
