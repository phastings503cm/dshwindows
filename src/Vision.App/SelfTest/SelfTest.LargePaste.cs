using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.App.Views;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App;

// MARK: - A very large paste, measured
//
// Patrick pasted a 10,000+ line prompt into the composer on Windows and then couldn't scroll the window. This step
// does the same in the real app — a paste of 10,214 log lines into the composer, then the same text as a sent
// message in the transcript — and writes down what WPF does with it: how long the UI thread is blocked, how often
// layout runs, what the scroll viewers report, and whether the mouse wheel moves the transcript. The numbers go in
// the self-test report ("probe …" lines), so a run on the old code and one on the new can be compared.

public sealed partial class SelfTest
{
    /// <summary>Lines in the probe's paste: the size Patrick reported.</summary>
    internal const int ProbeLines = 10_214;

    private async Task ProbeLargePaste(MainWindow window, AppModel model)
    {
        try
        {
            var chat = model.NewChat();
            chat.Title = "A very large paste";
            model.Mode = WorkspaceMode.Chat;
            model.Select(chat.Id);
            await Settle();
            var view = window.SelfTestChatView;
            var composer = view.SelfTestComposer;
            var text = ProbeText(ProbeLines);
            Note($"probe paste: {ProbeLines:N0} lines, {text.Length:N0} characters, {Encoding.UTF8.GetByteCount(text) / 1024:N0} KB");

            // 1. Into the composer, the way Ctrl+V does it (TextBox.Paste goes through the same editing command).
            composer.Focus();
            Keyboard.Focus(composer);
            var watch = Stopwatch.StartNew();
            var pasted = false;
            try
            {
                Clipboard.SetText(text);
                composer.Paste();
                pasted = true;
            }
            catch (ExternalException)
            {
                composer.Text = text;
            }
            window.UpdateLayout();
            var pasteMs = watch.ElapsedMilliseconds;
            var composerLag = await UiLag(window, 3000);
            var host = composer.Template?.FindName("PART_ContentHost", composer) as ScrollViewer;
            Note($"probe composer: {(pasted ? "pasted" : "set")} in {pasteMs:N0} ms; holds {composer.Text.Length:N0} characters; " +
                 $"{view.SelfTestAttachmentCount} attachment(s); height {composer.ActualHeight:F0} (max {composer.MaxHeight:F0}); " +
                 $"inner scroll extent {host?.ExtentHeight ?? -1:F0} viewport {host?.ViewportHeight ?? -1:F0}; " +
                 $"spell check {(SpellCheck.GetIsEnabled(composer) ? "on" : "off")}; {composerLag}");
            if (host is not null && host.ScrollableHeight > 0)
            {
                host.ScrollToEnd();
                window.UpdateLayout();
                Note($"probe composer: scrolled to its end — offset {host.VerticalOffset:F0} of {host.ScrollableHeight:F0}; " +
                     $"the wheel (48 px a notch) needs {Math.Ceiling(host.ScrollableHeight / 48):N0} notches top to bottom");
            }
            foreach (var label in view.SelfTestChipLabels) Note($"probe composer chip: {label}");
            Capture(window, "probe-paste-composer");

            // 1b. Add to tasks (Ctrl+Enter, or Enter while the agent works) takes the chip with it (10-18 #11 × #5). The list
            // is paused first, so the task waits instead of running against no model.
            model.Host.PauseChatTasks(chat.Id);
            view.SelfTestAddToTasks();
            await Settle(300);
            var queued = model.Host.Queue.Tasks.LastOrDefault(t => t.SessionId == chat.Id && t.AsPrompt);
            Note($"probe queue: Add to tasks with the chip queued {(queued is null ? "NOTHING" : $"“{queued.Title}” with {queued.Attachments.Count} attachment(s) ({string.Join(", ", queued.Attachments.Select(a => a.Kind))})")}; " +
                 $"{view.SelfTestAttachmentCount} chip(s) left in the composer");
            if (queued is not { Attachments.Count: 1 } || view.SelfTestAttachmentCount != 0)
                throw new InvalidOperationException("Add to tasks didn't queue the 10,214-line paste with its chip");
            Capture(window, "probe-paste-queued");
            model.Host.QueueRemove(queued.Id);
            model.Host.ResumeChatTasks(chat.Id);

            // 2. The same text as a sent message (or one from a chat saved before large pastes became attachments).
            composer.Clear();
            await Settle(300);
            watch.Restart();
            chat.AppendMessage(MessageRole.User, text);
            chat.AppendMessage(MessageRole.Assistant, "Thanks — I've read the log. The slow requests all go through `/api/stock/reserve`.");
            window.UpdateLayout();
            var layoutMs = watch.ElapsedMilliseconds;
            var transcriptLag = await UiLag(window, 4000);
            var scroller = view.SelfTestScroller;
            if (scroller is null)
            {
                Note("probe transcript: no scroll viewer");
                return;
            }
            Note($"probe transcript: first layout {layoutMs:N0} ms; extent {scroller.ExtentHeight:F0} viewport {scroller.ViewportHeight:F0} " +
                 $"offset {scroller.VerticalOffset:F0}; {transcriptLag}");
            // A narrower window re-wraps every row that is on screen.
            watch.Restart();
            window.Width -= 160;
            window.UpdateLayout();
            var narrowMs = watch.ElapsedMilliseconds;
            watch.Restart();
            window.Width += 160;
            window.UpdateLayout();
            Note($"probe transcript: resizing the window re-lays it out in {narrowMs:N0} ms and {watch.ElapsedMilliseconds:N0} ms");

            // The wheel over the middle of the transcript, as the input system raises it (tunnel, then bubble).
            var start = scroller.VerticalOffset;
            var target = scroller.InputHitTest(new Point(scroller.ActualWidth / 2, scroller.ActualHeight / 2)) as DependencyObject;
            var under = target is null ? "nothing" : Describe(target);
            var moved = await Wheel(target as UIElement, scroller, notches: 10, delta: 120);
            var perNotch = Math.Abs(start - scroller.VerticalOffset) / 10;
            Note($"probe transcript: 10 wheel notches up over {under} moved it {start - scroller.VerticalOffset:F0} px " +
                 $"({(moved ? "it scrolls" : "IT DOES NOT SCROLL")}); top to bottom takes " +
                 $"{(perNotch > 0 ? Math.Ceiling(scroller.ScrollableHeight / perNotch) : double.PositiveInfinity):N0} notches, " +
                 $"{scroller.ScrollableHeight / Math.Max(1, scroller.ViewportHeight):N0} screens");
            scroller.ScrollToTop();
            await Settle(300);
            Note($"probe transcript: scrolled to the top — offset {scroller.VerticalOffset:F0}");
            Capture(window, "probe-paste-transcript-top");
            scroller.ScrollToEnd();
            await Settle(600);
            Note($"probe transcript: scrolled to the end — offset {scroller.VerticalOffset:F0} of {scroller.ScrollableHeight:F0}");
            Capture(window, "probe-paste-transcript");
        }
        catch (Exception error)
        {
            Fail("probe-paste", error);
        }
    }

    /// <summary>A plausible pasted log, about 40 bytes a line (10,214 lines ≈ 412 KB, the paste Patrick reported), with a
    /// line long enough to wrap every fifty.</summary>
    internal static string ProbeText(int lines)
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

    private static string Describe(DependencyObject element)
    {
        var chain = new List<string>();
        for (var at = element; at is not null && chain.Count < 6; at = VisualTreeHelper.GetParent(at))
        {
            if (at is TextBox or TextBlock or ScrollViewer or ContentPresenter or Border) chain.Add(at.GetType().Name);
        }
        return string.Join(" < ", chain);
    }

    /// <summary>Raise the wheel the way the input system does; true when the transcript moved.</summary>
    private static async Task<bool> Wheel(UIElement? target, ScrollViewer scroller, int notches, int delta)
    {
        if (target is null) return false;
        var before = scroller.VerticalOffset;
        for (var i = 0; i < notches; i++)
        {
            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
            target.RaiseEvent(preview);
            if (!preview.Handled)
            {
                var bubble = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta) { RoutedEvent = UIElement.MouseWheelEvent };
                target.RaiseEvent(bubble);
            }
            await Dispatcher.Yield(DispatcherPriority.Background);
        }
        scroller.UpdateLayout();
        return Math.Abs(scroller.VerticalOffset - before) > 0.5;
    }

    /// <summary>For <paramref name="milliseconds"/>: how long an Input-priority callback waits for the UI thread (what a
    /// click or the wheel waits), how many layout passes run, and how often the transcript's extent changes.</summary>
    private static async Task<string> UiLag(Window window, int milliseconds)
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
        var p95 = lags.Count == 0 ? 0 : lags[Math.Min(lags.Count - 1, (int)Math.Ceiling(lags.Count * 0.95) - 1)];
        return $"input waits max {lags.LastOrDefault():N0} ms, p95 {p95:N0} ms over {lags.Count} samples; {layouts:N0} layout passes in {milliseconds / 1000.0:F0} s";
    }
}
