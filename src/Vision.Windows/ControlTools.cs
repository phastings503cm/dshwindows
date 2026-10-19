using System.Globalization;
using System.Windows.Automation;
using Vision.Core;

namespace Vision.Windows;

// MARK: - Control tools
//
// mouse, keyboard and focus_app act as the person in another app. They prefer input that reaches the
// app without the person's own mouse and keyboard (see BackgroundInput and InputPlanner), so several
// chats can work at once while the person keeps working too; when only the real devices will do, they
// wait their turn for them through the arbiter, and give the cursor back where the person left it.

/// <summary>Shared pieces of the control tools.</summary>
internal static class ControlArgs
{
    public const string ModeSchema =
        "\"mode\":{\"type\":\"string\",\"enum\":[\"auto\",\"background\",\"real\"],\"description\":\"auto (default): in the background when that works, else the real mouse/keyboard (waits its turn); background: never the real devices; real: always them\"}";

    /// <summary>The window an app/window argument names, or an error.</summary>
    public static (WindowInfo? Window, string? Error) Window(string? app, string? window)
    {
        if (!string.IsNullOrWhiteSpace(window))
        {
            return WindowServices.Match(window) is { } byWindow
                ? (byWindow, null)
                : (null, $"Error: no window matching '{window}' is open (list_windows shows what is).");
        }
        if (string.IsNullOrWhiteSpace(app)) return (null, null);
        return WindowServices.FindApp(app) is { } byApp
            ? (byApp, null)
            : (null, $"Error: '{app}' is not running or has no window open (list_windows / process_list to check).");
    }

    /// <summary>Find an element of <paramref name="top"/> by name or #id, with what the planner needs. Never in Vision's own
    /// windows.</summary>
    public static async Task<(InputTarget? Target, string? Error)> Element(IntPtr top, string query, string appName,
                                                                         CancellationToken cancellationToken)
    {
        if (TerminalGuard.IsHarness(top)) return (null, TerminalGuard.SelfClickRefusal("using"));
        var (finished, found) = await MtaThread.Run(() =>
        {
            if (UiaElements.Find(top, query, TimeSpan.FromSeconds(4)) is not { } named) return ((AutomationElement, ElementFacts, (int, int)?)?)null;
            // A button's caption names the button.
            var (element, facts) = UiaElements.Actionable(named);
            return (element, facts, UiaElements.Center(element));
        }, TimeSpan.FromSeconds(6), cancellationToken).ConfigureAwait(false);
        if (!finished) return (null, $"Error: '{appName}' did not answer UI Automation queries in time — it may be hung (inspect_process shows whether it is responding).");
        if (found is not { } hit)
            return (null, $"Error: no element named '{query}' in {appName}. ui_tree lists its elements (names, and #ids to use as element:\"#id\").");
        return (InputTargets.ForElement(top, hit.Item1, hit.Item2, hit.Item3), null);
    }

    /// <summary>The element under the target's point, read when a pattern could carry the input.</summary>
    public static async Task Describe(InputTarget target, CancellationToken cancellationToken)
    {
        if (target.Point is not { } point) return;
        var (finished, found) = await MtaThread.Run(() =>
        {
            AutomationElement? element = null;
            ElementFacts? facts = null;
            try
            {
                element = UiaElements.AtPoint(target.Top, target.ProcessId, point.X, point.Y);
                if (element is not null) (element, facts) = UiaElements.Actionable(element);
            }
            catch (Exception ex) when (UiaElements.IsGone(ex) || ex is InvalidOperationException)
            {
                element = null;
            }
            // Older controls with no pattern may still have an accessibility default action.
            Func<bool>? msaa = null;
            if (facts is null || !(facts.Invoke || facts.Toggle || facts.SelectionItem || facts.ExpandCollapse || facts.IsTextField))
            {
                if (Msaa.DefaultActionAt(target.Control, point.X, point.Y) is { } action)
                {
                    facts = (facts ?? new ElementFacts { ControlType = "Custom" }) with { MsaaDefaultAction = action.Action };
                    msaa = action.Do;
                }
            }
            return (element, facts, msaa);
        }, UiaElements.LookupTimeout, cancellationToken).ConfigureAwait(false);
        if (!finished) return; // a slow app: no element, so no pattern; the window's kind still decides
        target.Element = found.element;
        target.Facts = found.facts;
        target.MsaaAction = found.msaa;
    }

    /// <summary>Controls whose "disabled" is worth believing (a game's pane says all sorts of things).</summary>
    public static bool IsDisabledControl(ElementFacts? facts) => facts is { Enabled: false } f && f.ControlType is "Button" or "CheckBox"
        or "RadioButton" or "MenuItem" or "Hyperlink" or "ComboBox" or "Edit" or "ListItem" or "TabItem" or "SplitButton";
}

// MARK: - mouse

public sealed class MouseTool : IToolExecutor
{
    public const string ToolName = "mouse";
    private readonly MachineChat _chat;

    public MouseTool() : this(MachineChat.Standalone()) { }

    public MouseTool(MachineChat chat) => _chat = chat;

    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Click, double-click, right-click, drag or scroll in an app. Aim at screen coordinates (x,y, pixels from screenshot/list_windows; " +
        "the main display's top-left is 0,0 and monitors to its left or above are negative) or at an element by name " +
        "(app + element, a name or #automationId from ui_tree). By default (mode auto) a click reaches the app in the background — " +
        "UI Automation on the control, or a click message to it — so the person's mouse stays free and other chats can work at the same time; " +
        "where that can't work (games, canvases, right-clicks, drags) it uses the real mouse, waiting its turn if the person or another chat is using it, " +
        "and puts the cursor back afterwards. window aims at one window even when it is behind others; without it, x,y go to the window this chat " +
        "last captured (screenshot window=…) or named, covered or not, and a point outside it on another app is refused. " +
        "Never clicks Vision itself. scroll=up/down/left/right with amount (notches). " +
        "Apps running as administrator ignore it unless Vision is elevated too. Screenshot after to verify.",
        "{\"type\":\"object\",\"properties\":{" +
        "\"x\":{\"type\":\"integer\",\"description\":\"Screen x (pixels)\"},\"y\":{\"type\":\"integer\",\"description\":\"Screen y (pixels)\"}," +
        "\"button\":{\"type\":\"string\",\"enum\":[\"left\",\"right\",\"middle\"],\"description\":\"Default left\"}," +
        "\"clicks\":{\"type\":\"integer\",\"description\":\"1 default, 2 for double-click\"}," +
        "\"to_x\":{\"type\":\"integer\",\"description\":\"Drag release x\"},\"to_y\":{\"type\":\"integer\",\"description\":\"Drag release y\"}," +
        "\"scroll\":{\"type\":\"string\",\"enum\":[\"up\",\"down\",\"left\",\"right\"],\"description\":\"Scroll instead of clicking\"}," +
        "\"amount\":{\"type\":\"integer\",\"description\":\"Scroll notches (default 3)\"}," +
        "\"app\":{\"type\":\"string\",\"description\":\"The app (process name or window title) whose element or window to use\"}," +
        "\"element\":{\"type\":\"string\",\"description\":\"With app: click this element (name or #automationId from ui_tree) instead of x,y\"}," +
        "\"window\":{\"type\":\"string\",\"description\":\"A window id or title from list_windows: aim at it even when other windows cover the point\"}," +
        ControlArgs.ModeSchema + "},\"required\":[]}");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        if (InputPlanner.ParseMode(JsonArgs.String(args, "mode")) is not { } mode) return "Error: mode must be auto, background or real.";
        var button = (JsonArgs.String(args, "button") ?? "left").Trim().ToLowerInvariant();
        if (button is not ("left" or "right" or "middle")) return "Error: button must be left, right or middle.";
        var clicks = Math.Clamp(JsonArgs.Int(args, "clicks", 1), 1, 2);
        (int Notches, bool Horizontal)? scroll = null;
        if (JsonArgs.String(args, "scroll") is { Length: > 0 } direction)
        {
            var amount = Math.Clamp(JsonArgs.Int(args, "amount", 3), 1, 50);
            scroll = direction.Trim().ToLowerInvariant() switch
            {
                "up" => (amount, false),
                "down" => (-amount, false),
                "left" => (-amount, true),
                "right" => (amount, true),
                _ => null,
            };
            if (scroll is null) return "Error: scroll must be up, down, left or right.";
        }
        var app = JsonArgs.String(args, "app");
        var elementName = JsonArgs.String(args, "element");
        var (window, windowError) = ControlArgs.Window(app, JsonArgs.String(args, "window"));
        if (windowError is not null) return windowError;
        // Never Vision itself, even in the background and by element name: it could press its own gate's Allow Once.
        if (window is not null && TerminalGuard.IsHarness(window.Handle)) return TerminalGuard.SelfClickRefusal();
        if (window is { Minimized: true }) return $"Error: {window.Label} is minimized: focus_app restores it first.";
        // A window named on purpose becomes this chat's: its next clicks by coordinates go there too.
        if (window is not null) _chat.Aim(window.Handle, window.Label);
        var screen = ScreenCapture.VirtualScreen();
        (int X, int Y)? to = null;
        if (ToolArgs.Number(args, "to_x") is { } tx && ToolArgs.Number(args, "to_y") is { } ty)
        {
            to = ((int)Math.Round(tx), (int)Math.Round(ty));
            if (!screen.Contains(to.Value.X, to.Value.Y)) return OffScreen(to.Value.X, to.Value.Y, screen);
        }

        // Where the input goes: an element by name, or a point (in a given window, the chat's own window, or whatever is on
        // top there).
        InputTarget target;
        int x, y;
        var aimNote = "";
        var aimedThere = window is not null;
        if (!string.IsNullOrWhiteSpace(elementName))
        {
            if (window is null) return "Error: element needs app (or window): which app's element to click.";
            var (found, error) = await ControlArgs.Element(window.Handle, elementName, window.Owner, cancellationToken).ConfigureAwait(false);
            if (found is null) return error!;
            target = found;
            (x, y) = target.Point ?? (int.MinValue, int.MinValue);
        }
        else
        {
            if (ToolArgs.Number(args, "x") is not { } fx || ToolArgs.Number(args, "y") is not { } fy)
                return "Error: x and y are required (screen pixels from screenshot/list_windows), or app and element (from ui_tree).";
            (x, y) = ((int)Math.Round(fx), (int)Math.Round(fy));
            if (!screen.Contains(x, y)) return OffScreen(x, y, screen);
            if (window is not null && !window.Bounds.Contains(x, y))
                return $"Error: ({x},{y}) is outside {window.Label} (it spans {window.Bounds}). Take coordinates from a screenshot of it.";
            // No window named: the one this chat last captured or used, where the point is in it — not whatever the person
            // (or another chat's app) has on top there.
            var aimAt = window?.Handle ?? IntPtr.Zero;
            if (window is null && _chat.AimedWindow is { } aimed)
            {
                var top = InputTargets.TopAt(x, y);
                var bounds = WindowServices.Bounds(aimed.Top);
                var decision = InputAim.Decide(bounds.Contains(x, y), top == aimed.Top, BelongsTo(top, aimed));
                switch (decision)
                {
                    case AimDecision.Aimed:
                        aimAt = aimed.Top;
                        aimNote = InputAim.AimedNote(aimed.Label, top == IntPtr.Zero ? "nothing" : TerminalGuard.AppName(top));
                        break;
                    case AimDecision.Refuse:
                        return InputAim.OutsideAimed(x, y, aimed.Label, bounds.ToString(), top == IntPtr.Zero ? "no window" : TerminalGuard.AppName(top));
                }
            }
            if (InputTargets.AtPoint(x, y, aimAt) is not { } atPoint) return $"Error: no window is at ({x},{y}).";
            target = atPoint;
            aimedThere = aimAt != IntPtr.Zero;
        }

        var action = to is not null ? PointerAction.Drag
            : scroll is not null ? PointerAction.Scroll
            : button == "right" ? PointerAction.RightClick
            : button == "middle" ? PointerAction.MiddleClick
            : clicks == 2 ? PointerAction.DoubleClick
            : PointerAction.Click;

        if (TerminalGuard.IsHarness(target.Top) || (to is { } dropAt && TerminalGuard.IsHarness(InputTargets.TopAt(dropAt.X, dropAt.Y))))
            return TerminalGuard.SelfClickRefusal(to is null ? "clicking in" : "dragging into");
        // Right and middle clicks paste the clipboard in most terminals.
        if (button != "left" && scroll is null)
        {
            foreach (var top in new[] { target.Top, to is { } end ? InputTargets.TopAt(end.X, end.Y) : IntPtr.Zero })
            {
                if (top != IntPtr.Zero && TerminalGuard.IsTerminal(top)) return TerminalGuard.ClickRefusal(TerminalGuard.AppName(top), button);
            }
        }
        if (_chat.Refuse(target.Top, "clicking in") is { } refusal) return refusal;
        if (to is { } release && _chat.Refuse(InputTargets.TopAt(release.X, release.Y), "dragging into") is { } dragRefusal) return dragRefusal;

        // What is under the point, when a pattern could carry the click.
        if (mode != InputMode.Real && target.Element is null && action is PointerAction.Click or PointerAction.DoubleClick or PointerAction.Scroll
            && !(action == PointerAction.Click && target.Kind == WindowKind.Win32Edit))
        {
            await ControlArgs.Describe(target, cancellationToken).ConfigureAwait(false);
        }
        if (mode != InputMode.Real && action is PointerAction.Click or PointerAction.DoubleClick && ControlArgs.IsDisabledControl(target.Facts))
            return $"Did not click: {target.Label} is disabled (greyed out). Screenshot to see why.";

        var routes = InputPlanner.ForPointer(action, mode, target.Kind, target.Facts);
        if (routes.Count == 0) return BackgroundRefusal(action, target);
        // A click anywhere else moves the focus off the field an earlier click picked (as it would for a person).
        if (action != PointerAction.Scroll) _chat.TypingTarget = null;
        var skipped = new List<string>();
        foreach (var route in routes)
        {
            if (route == InputRoute.RealMouse)
            {
                if (x == int.MinValue) return $"Error: {target.Label} has no position on screen to click with the real mouse.";
                if (!screen.Contains(x, y)) return OffScreen(x, y, screen);
                var real = await RealAsync(target, aimedThere, x, y, to, button, clicks, scroll, cancellationToken).ConfigureAwait(false);
                return real.Output.StartsWith("Error", StringComparison.Ordinal) ? real : WithNote(real.Output, aimNote);
            }
            var result = await PointerRoutes.TryAsync(route, target, _chat, action, scroll ?? default, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == RouteOutcome.Done) return WithNote(result.Message, aimNote);
            if (result.Outcome != RouteOutcome.Skip) return result.Message;
            if (result.Message.Length > 0) skipped.Add($"{InputPlanner.Describe(route)}: {result.Message}");
        }
        return $"Error: couldn't do that to {target.Label} in the background ({string.Join("; ", skipped)}). " +
               "Use mode \"auto\" to let it use the real mouse (it waits its turn).";
    }

    /// <summary>Whether <paramref name="top"/> is part of the chat's window: a dialog or popup it owns, or a menu of its app.</summary>
    private static bool BelongsTo(IntPtr top, AimedWindow aimed)
    {
        if (top == IntPtr.Zero || WindowServices.ProcessIdOf(top) != aimed.ProcessId) return false;
        if (WindowServices.ClassName(top) == "#32768") return true; // a menu
        var owner = top;
        for (var hop = 0; hop < 8; hop++)
        {
            owner = Native.GetWindow(owner, Native.GW_OWNER);
            if (owner == IntPtr.Zero) return false;
            if (owner == aimed.Top) return true;
        }
        return false;
    }

    private static string BackgroundRefusal(PointerAction action, InputTarget target)
    {
        var why = action switch
        {
            PointerAction.RightClick => "a right-click opens a menu that follows the real pointer",
            PointerAction.MiddleClick => "a middle-click needs the real mouse",
            PointerAction.Drag => "a drag needs the real mouse",
            _ => $"it offers no UI Automation pattern for this, and its window ({target.Kind}) ignores click messages",
        };
        return $"Error: can't do that to {target.Label} in the background: {why}. Use mode \"auto\" to let it use the real mouse (it waits its turn).";
    }

    /// <summary>The real mouse: one chat at a time, never while the person is using it, and the cursor
    /// goes back where it was.</summary>
    private async Task<ToolResult> RealAsync(InputTarget target, bool aimed, int x, int y, (int X, int Y)? to, string button, int clicks,
                                             (int Notches, bool Horizontal)? scroll, CancellationToken cancellationToken)
    {
        InputLease lease;
        try
        {
            lease = await _chat.AcquireRealInputAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InputWaitException wait)
        {
            return wait.Message;
        }
        using (lease)
        {
            Native.GetCursorPos(out var before);
            var previous = IntPtr.Zero;
            try
            {
                // A window named on purpose but covered at that point comes to the front first.
                var front = "";
                if (aimed && InputTargets.TopAt(x, y) != target.Top)
                {
                    previous = Native.GetForegroundWindow();
                    if (!WindowFocus.Bring(target.Top, out var why)) return $"Error: {target.App} is covered at ({x},{y}) and could not be brought to the front ({why}).";
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    front = $" (brought {target.App} to the front first, then gave your window back)";
                }
                var done = await ClickAsync(x, y, to, button, clicks, scroll, cancellationToken).ConfigureAwait(false);
                return done.StartsWith("Error", StringComparison.Ordinal) ? done : WithNote(done, front);
            }
            finally
            {
                // Give the person their cursor back (unless they took it meanwhile).
                if (!MachineControl.Arbiter.PersonActive)
                {
                    PersonInput.Shared.ExpectCursor(before.X, before.Y);
                    Native.SetCursorPos(before.X, before.Y);
                }
                // And the window they had in front, when it was this click that took it.
                ForegroundReturn.Restore(previous, target);
            }
        }
    }

    /// <summary>"Clicked left at (x,y) (note). Screenshot to verify."</summary>
    internal static string WithNote(string result, string note)
    {
        if (note.Length == 0) return result;
        const string tail = ". Screenshot to verify.";
        return result.EndsWith(tail, StringComparison.Ordinal) ? result[..^tail.Length] + note + tail : result.TrimEnd('.') + note + ".";
    }

    /// <summary>SendInput, as the tool always did.</summary>
    private static async Task<string> ClickAsync(int x, int y, (int X, int Y)? to, string button, int clicks, (int Notches, bool Horizontal)? scroll,
                                          CancellationToken cancellationToken)
    {
        var (down, up) = InputSender.ButtonFlags(button);
        if (scroll is { } wheel)
        {
            if (!InputSender.MoveTo(x, y)) return InputSender.Rejected;
            await Task.Delay(30, cancellationToken).ConfigureAwait(false);
            var notches = Math.Abs(wheel.Notches);
            for (var i = 0; i < notches; i++)
            {
                if (!InputSender.Send([Native.INPUT.Wheel(Math.Sign(wheel.Notches) * 120, wheel.Horizontal)])) return InputSender.Rejected;
                await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);
            }
            var dir = wheel.Horizontal ? (wheel.Notches > 0 ? "right" : "left") : (wheel.Notches > 0 ? "up" : "down");
            return $"Scrolled {dir} {notches} at ({x},{y}) with the real mouse. Screenshot to verify.";
        }
        if (to is { } release)
        {
            // Drag: press, several intermediate moves (apps track them), release. Once the button
            // is down nothing may cancel before it is released again.
            if (!InputSender.MoveTo(x, y) || !InputSender.Send([Native.INPUT.Mouse(down)])) return InputSender.Rejected;
            await Task.Delay(30, CancellationToken.None).ConfigureAwait(false);
            const int steps = 6;
            for (var step = 1; step <= steps; step++)
            {
                var t = step / (double)steps;
                InputSender.MoveTo((int)Math.Round(x + (release.X - x) * t), (int)Math.Round(y + (release.Y - y) * t));
                await Task.Delay(15, CancellationToken.None).ConfigureAwait(false);
            }
            InputSender.Send([Native.INPUT.Mouse(up)]);
            return $"Dragged ({x},{y}) → ({release.X},{release.Y}).";
        }
        if (!InputSender.MoveTo(x, y)) return InputSender.Rejected;
        await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        for (var click = 0; click < clicks; click++)
        {
            if (!InputSender.Send([Native.INPUT.Mouse(down), Native.INPUT.Mouse(up)])) return InputSender.Rejected;
            if (click < clicks - 1) await Task.Delay(60, CancellationToken.None).ConfigureAwait(false);
        }
        return $"Clicked {button}{(clicks > 1 ? $" ×{clicks}" : "")} at ({x},{y}). Screenshot to verify.";
    }

    private static string OffScreen(int x, int y, ScreenRect screen) =>
        $"Error: ({x},{y}) is off screen (the screen spans {screen}). Take coordinates from screenshot/list_windows.";
}

// MARK: - keyboard

public sealed class KeyboardTool : IToolExecutor
{
    public const string ToolName = "keyboard";
    private readonly MachineChat _chat;

    public KeyboardTool() : this(MachineChat.Standalone()) { }

    public KeyboardTool(MachineChat chat) => _chat = chat;

    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Type into an app: text=literal string, and/or keys like " +
        "[\"ctrl+s\"] [\"enter\"] [\"ctrl+c\"] [\"esc\"] [\"up\",\"down\"] [\"f5\"] with modifiers ctrl/alt/shift/win. " +
        "app picks the app (process name or window title); element (with app) picks a field by name or #automationId from ui_tree — " +
        "keyboard(app:\"Notepad\", element:\"Text editor\", text:\"hi\"); replace:true replaces the field's text instead of adding to it. " +
        "Without app it types where the last mouse click picked a field (for 2 minutes), else into the window this chat last captured or " +
        "used (for 5 minutes), else into an app this chat started that is in front — otherwise name the app (the window in front may be the person's). " +
        "Text ending in a line break is the text and then Enter (Enter in a web form's field presses its submit button in the background). " +
        "By default (mode auto) text and lone keys go to the app in the background (UI Automation values, messages to the control), " +
        "leaving the person's keyboard alone; shortcuts with ctrl/alt/shift/win and apps that need it use the real keyboard, waiting " +
        "their turn (the reliable pattern for those: keyboard(app:\"Godot\", keys:[\"F5\"])). " +
        "Refused into terminals (Windows Terminal, PowerShell, cmd, …), the Run box and Start search, and Vision itself — " +
        "that route would bypass the shell permission gate.",
        "{\"type\":\"object\",\"properties\":{" +
        "\"text\":{\"type\":\"string\",\"description\":\"Literal text to type\"}," +
        "\"keys\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"Key combos, e.g. ['ctrl+s','enter']\"}," +
        "\"app\":{\"type\":\"string\",\"description\":\"The app to type into (process name or window title)\"}," +
        "\"element\":{\"type\":\"string\",\"description\":\"With app: the field to type into (name or #automationId from ui_tree)\"}," +
        "\"replace\":{\"type\":\"boolean\",\"description\":\"Replace the field's text instead of adding to it\"}," +
        ControlArgs.ModeSchema + "},\"required\":[]}");

    /// <summary>UTF-16 units per SendInput batch: small enough that apps keep up.</summary>
    private const int Chunk = 20;

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        if (InputPlanner.ParseMode(JsonArgs.String(args, "mode")) is not { } mode) return "Error: mode must be auto, background or real.";
        var text = JsonArgs.String(args, "text") ?? "";
        var replace = JsonArgs.Bool(args, "replace", false);
        // Every combo is checked before anything is typed: a typo must not leave half an input behind.
        var combos = new List<KeyCombo>();
        foreach (var name in ToolArgs.Strings(args, "keys"))
        {
            if (KeyNames.Parse(name) is not { } combo) return $"Error: unknown key '{name}'. Known: {KeyNames.Known}.";
            combos.Add(combo);
        }
        if (text.Length == 0 && combos.Count == 0) return "Error: give text and/or keys.";
        // "terms\n" is the text and then Enter: the text can go in the background, the Enter by the field's submit.
        if (mode != InputMode.Real && TrailingEnter.Split(text) is (var head, true) && KeyNames.Parse("enter") is { } enter)
        {
            text = head;
            combos.Insert(0, enter);
        }

        // Where the keystrokes go. The terminal guard resolves the *destination* window and refuses TTYs,
        // launchers and Vision.
        var app = JsonArgs.String(args, "app");
        var elementName = JsonArgs.String(args, "element");
        InputTarget? target;
        string targetName;
        var remembered = false;
        if (!string.IsNullOrWhiteSpace(app))
        {
            targetName = app;
            if (WindowServices.FindApp(app) is not { } hit)
                return $"Error: '{app}' is not running or cannot be focused: no window of it is open.";
            if (TerminalGuard.IsBlocked(hit.Handle)) return TerminalGuard.Refusal(app);
            _chat.Aim(hit.Handle, hit.Label);
            if (!string.IsNullOrWhiteSpace(elementName))
            {
                var (found, error) = await ControlArgs.Element(hit.Handle, elementName, app, cancellationToken).ConfigureAwait(false);
                if (found is null) return error!;
                target = found;
                targetName = target.Label;
            }
            else
            {
                target = InputTargets.ForWindow(hit.Handle);
            }
        }
        else if (!string.IsNullOrWhiteSpace(elementName))
        {
            return "Error: element needs app: which app's field to type into.";
        }
        else if (_chat.TypingTarget is { IsFresh: true } field)
        {
            remembered = true;
            target = new InputTarget
            {
                Top = field.Top, Control = field.Control, Kind = field.Kind, ProcessId = WindowServices.ProcessIdOf(field.Top),
                Point = (field.X, field.Y), Element = field.Element, Facts = field.Facts,
            };
            targetName = target.Label;
            if (TerminalGuard.IsBlocked(field.Top)) return TerminalGuard.Refusal(TerminalGuard.AppName(field.Top));
        }
        else if (_chat.AimedWindow is { } aimed)
        {
            // The window this chat last captured or used — not the person's front window.
            if (TerminalGuard.IsBlocked(aimed.Top)) return TerminalGuard.Refusal(TerminalGuard.AppName(aimed.Top));
            target = InputTargets.ForWindow(aimed.Top);
            targetName = aimed.Label;
            remembered = true;
        }
        else
        {
            // Only an app this chat started itself may be typed into without naming it: the window in front may be the person's.
            var front = Native.GetForegroundWindow();
            if (front != IntPtr.Zero && TerminalGuard.IsBlocked(front)) return TerminalGuard.Refusal(TerminalGuard.AppName(front));
            if (front == IntPtr.Zero || !_chat.Owns(WindowServices.ProcessIdOf(front)))
                return InputAim.NoTypingTarget(front == IntPtr.Zero ? "none" : TerminalGuard.AppName(front));
            target = InputTargets.ForWindow(front);
            targetName = TerminalGuard.AppName(front);
        }
        if (target is not null && _chat.Refuse(target.Top, "typing into") is { } refusal) return refusal;

        // How each part goes: all in the background when every part can, else (auto) the real keyboard.
        var kind = target?.Kind ?? WindowKind.Other;
        IReadOnlyList<InputRoute> textRoutes = text.Length == 0 ? [] : InputPlanner.ForText(text, replace, mode, kind, target?.Facts);
        var keyRoutes = combos.Select(c => InputPlanner.ForKey(c.Key, c.Ctrl, c.Alt, c.Shift, c.Win, mode, kind, target?.Facts)).ToList();
        var characters = text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;
        var backgroundText = text.Length == 0 || textRoutes.Any(InputPlanner.IsBackground);
        var backgroundKeys = keyRoutes.All(r => r.Any(InputPlanner.IsBackground));
        if (mode == InputMode.Background && target is null) return "Error: no window to type into (nothing is in front). Name the app.";
        if (mode == InputMode.Background && !backgroundText)
            return $"Error: can't type into {targetName} in the background: {TextWhy(target!)}. Use mode \"auto\" to let it use the real keyboard (it waits its turn).";
        if (mode == InputMode.Background && !backgroundKeys)
        {
            var key = combos[keyRoutes.FindIndex(r => !r.Any(InputPlanner.IsBackground))];
            return $"Error: can't press {Describe(key)} in {targetName} in the background: {KeyWhy(key, target!)}. " +
                   "Use mode \"auto\" to let it use the real keyboard (it waits its turn).";
        }
        if (mode == InputMode.Real || target is null || !backgroundText || !backgroundKeys)
            return await RealAsync(target, app, remembered, targetName, text, combos, characters, cancellationToken).ConfigureAwait(false);

        var via = new List<string>();
        if (text.Length > 0)
        {
            var done = false;
            foreach (var route in textRoutes.Where(InputPlanner.IsBackground))
            {
                var result = await KeyRoutes.TextAsync(route, target, text, cancellationToken).ConfigureAwait(false);
                if (result.Outcome == RouteOutcome.Stop) return result.Message;
                if (result.Outcome != RouteOutcome.Done) continue;
                via.Add(result.Message);
                done = true;
                break;
            }
            if (!done)
            {
                if (mode == InputMode.Background) return $"Error: {targetName} did not take the text in the background. Use mode \"auto\" to let it use the real keyboard.";
                return await RealAsync(target, app, remembered, targetName, text, combos, characters, cancellationToken).ConfigureAwait(false);
            }
        }
        for (var i = 0; i < combos.Count; i++)
        {
            var done = false;
            foreach (var route in keyRoutes[i].Where(InputPlanner.IsBackground))
            {
                var result = await KeyRoutes.KeyAsync(route, target, combos[i], cancellationToken).ConfigureAwait(false);
                if (result.Outcome == RouteOutcome.Stop) return result.Message;
                if (result.Outcome != RouteOutcome.Done) continue;
                via.Add(result.Message);
                done = true;
                break;
            }
            if (done) continue;
            if (mode == InputMode.Background)
                return $"Error: typed {characters} characters and pressed {i} key combo(s) into {targetName} in the background, then {targetName} did not take {Describe(combos[i])}.";
            // The rest goes by the real keyboard (what was typed stays typed).
            var rest = await RealAsync(target, app, remembered, targetName, "", combos.Skip(i).ToList(), 0, cancellationToken).ConfigureAwait(false);
            return text.Length == 0 && i == 0 ? rest : $"Typed {characters} characters and {i} key combo(s) in the background, then: {rest.Output}";
        }
        return $"Typed {characters} characters and {combos.Count} key combo(s) into {targetName} with {string.Join(", ", via.Distinct())} " +
               $"({KeyRoutes.Background}). Screenshot to verify.";
    }

    private static string Describe(KeyCombo combo) =>
        string.Join("+", new[] { combo.Ctrl ? "ctrl" : null, combo.Alt ? "alt" : null, combo.Shift ? "shift" : null, combo.Win ? "win" : null, combo.Key }
            .Where(p => p is not null));

    private static string TextWhy(InputTarget target) => target.Facts is { Value: true }
        ? "line breaks and tabs are keys that need the real keyboard there"
        : $"its window ({target.Kind}) ignores character messages and no field with a value was named (give element from ui_tree)";

    private static string KeyWhy(KeyCombo combo, InputTarget target) => combo.Ctrl || combo.Alt || combo.Shift || combo.Win
        ? "shortcuts with ctrl/alt/shift/win need the real keyboard (key messages carry no modifiers)"
        : $"its window ({target.Kind}) ignores key messages";

    /// <summary>The real keyboard: one chat at a time, paused while the person types or moves the mouse.</summary>
    private async Task<ToolResult> RealAsync(InputTarget? target, string? app, bool remembered, string targetName, string text,
                                             IReadOnlyList<KeyCombo> combos, int characters, CancellationToken cancellationToken)
    {
        InputLease lease;
        try
        {
            lease = await _chat.AcquireRealInputAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InputWaitException wait)
        {
            return wait.Message;
        }
        var previous = Native.GetForegroundWindow();
        using (lease)
        {
            try
            {
                return await TypeAsync().ConfigureAwait(false);
            }
            finally
            {
                // The person gets their window back once the keystrokes are in (unless they moved on meanwhile).
                if (target is not null) ForegroundReturn.Restore(previous, target);
            }
        }

        async Task<ToolResult> TypeAsync()
        {
            // Put the keyboard focus where the text goes: the app, then its field.
            if (target is not null && (app is not null || remembered))
            {
                if (!WindowFocus.Bring(target.Top, out var why)) return $"Error: '{(app ?? targetName)}' is not running or cannot be focused: {why}.";
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                if (target.Element is { } element)
                {
                    await MtaThread.Run(() =>
                    {
                        try
                        {
                            element.SetFocus();
                        }
                        catch (Exception ex) when (UiaElements.IsGone(ex) || ex is InvalidOperationException)
                        {
                            // the window has the focus; the field may still take it from a click
                        }
                        return true;
                    }, UiaElements.LookupTimeout, cancellationToken).ConfigureAwait(false);
                }
                else if (remembered && target.Point is { } point)
                {
                    // The field picked by an earlier click: click it for real now, to put the caret there.
                    Native.GetCursorPos(out var before);
                    var (down, up) = InputSender.ButtonFlags("left");
                    if (InputSender.MoveTo(point.X, point.Y)) InputSender.Send([Native.INPUT.Mouse(down), Native.INPUT.Mouse(up)]);
                    PersonInput.Shared.ExpectCursor(before.X, before.Y);
                    Native.SetCursorPos(before.X, before.Y);
                    await Task.Delay(120, cancellationToken).ConfigureAwait(false);
                }
            }
            if (Guard(0, 0) is { } refusal) return refusal;

            var typed = 0;
            var pressed = 0;
            var units = KeyNames.TextUnits(text);
            for (var start = 0; start < units.Count;)
            {
                var end = ChunkEnd(units, start);
                if (await Pause(typed, pressed).ConfigureAwait(false) is { } paused) return paused;
                if (Guard(typed, pressed) is { } stopped) return stopped;
                if (!InputSender.Send(TextInputs(units, start, end))) return InputSender.Rejected;
                typed += end - start;
                start = end;
                await Task.Delay(8, cancellationToken).ConfigureAwait(false);
            }
            foreach (var combo in combos)
            {
                if (await Pause(typed, pressed).ConfigureAwait(false) is { } paused) return paused;
                if (Guard(typed, pressed) is { } stopped) return stopped;
                if (!InputSender.Send(ComboInputs(combo))) return InputSender.Rejected;
                pressed++;
                // A shortcut may open something (Win+R, a terminal); give it time to take the
                // foreground before the next check.
                await Task.Delay(combo.Win || combo.Ctrl || combo.Alt ? 200 : 20, cancellationToken).ConfigureAwait(false);
            }
            return $"Typed {characters} characters and {pressed} key combo(s) into {targetName}. Screenshot to verify.";
        }

        // The person took the mouse or keyboard: wait for them, and if they moved the focus, put it back
        // (or stop, when it isn't known where it belongs).
        async Task<string?> Pause(int typedSoFar, int pressedSoFar)
        {
            if (!MachineControl.Arbiter.PersonActive) return null;
            if (!await _chat.WaitForPersonAsync(cancellationToken).ConfigureAwait(false))
                return $"Error: stopped after {typedSoFar} characters and {pressedSoFar} key combo(s): the person kept using the mouse and keyboard.";
            if (target is null || WindowFocus.IsForeground(target.Top)) return null;
            if ((app is not null || remembered) && WindowFocus.Bring(target.Top, out _))
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                return null;
            }
            return $"Stopped after {typedSoFar} characters and {pressedSoFar} key combo(s): you started using the computer and the focus moved. " +
                   "Name the app (app:\"…\") to carry on there.";
        }

        // Checked before every batch, not just once: typing can itself move the focus (Win+R, a
        // shortcut that opens a terminal, the user clicking elsewhere).
        string? Guard(int typedSoFar, int pressedSoFar)
        {
            var front = Native.GetForegroundWindow();
            if (front == IntPtr.Zero || !TerminalGuard.IsBlocked(front)) return null;
            if (typedSoFar + pressedSoFar == 0)
                return TerminalGuard.Refusal(string.IsNullOrWhiteSpace(app) ? TerminalGuard.AppName(front) : app);
            return $"{TerminalGuard.Refusal(TerminalGuard.AppName(front))} (Stopped after {typedSoFar} characters and {pressedSoFar} key combo(s): the focus moved there.)";
        }
    }

    /// <summary>Where the batch starting at <paramref name="start"/> ends: <see cref="Chunk"/> units,
    /// one more when that would split a surrogate pair.</summary>
    internal static int ChunkEnd(IReadOnlyList<(ushort Code, bool IsVirtualKey)> units, int start)
    {
        var end = Math.Min(units.Count, start + Chunk);
        if (end < units.Count && !units[end - 1].IsVirtualKey && char.IsHighSurrogate((char)units[end - 1].Code)) end++;
        return end;
    }

    /// <summary>The events that type units [start, end): named keys as key presses, characters as
    /// Unicode input.</summary>
    internal static List<Native.INPUT> TextInputs(IReadOnlyList<(ushort Code, bool IsVirtualKey)> units, int start, int end)
    {
        var inputs = new List<Native.INPUT>((end - start) * 2);
        for (var i = start; i < end; i++)
        {
            var (code, isKey) = units[i];
            if (isKey) inputs.AddRange([InputSender.Key(code, up: false), InputSender.Key(code, up: true)]);
            else inputs.AddRange(InputSender.Unicode(code));
        }
        return inputs;
    }

    /// <summary>The events for one combo, modifiers included.</summary>
    internal static List<Native.INPUT> ComboInputs(KeyCombo combo) =>
        KeyNames.Sequence(combo).Select(e => InputSender.Key(e.VirtualKey, e.Up)).ToList();
}

// MARK: - Giving the person their window back

/// <summary>After the real mouse or keyboard brought a chat's window to the front: the window the person had in front goes
/// back to the front, unless they used the computer meanwhile or something else came up since (see <see cref="FocusReturn"/>).</summary>
internal static class ForegroundReturn
{
    public static void Restore(IntPtr previous, InputTarget target)
    {
        if (previous == IntPtr.Zero) return;
        static IntPtr Root(IntPtr window)
        {
            var root = Native.GetAncestor(window, Native.GA_ROOT);
            return root == IntPtr.Zero ? window : root;
        }
        var front = Root(Native.GetForegroundWindow());
        var restore = FocusReturn.ShouldRestore(
            hadPrevious: true,
            previousIsTarget: Root(previous) == target.Top,
            previousExists: Native.IsWindow(previous),
            frontIsTargetApp: front != IntPtr.Zero && (front == target.Top || WindowServices.ProcessIdOf(front) == target.ProcessId),
            personActive: MachineControl.Arbiter.PersonActive);
        if (restore) WindowFocus.Bring(previous, out _);
    }
}

// MARK: - focus_app

public sealed class FocusAppTool : IToolExecutor
{
    public const string ToolName = "focus_app";
    private readonly MachineChat _chat;

    public FocusAppTool() : this(MachineChat.Standalone()) { }

    public FocusAppTool(MachineChat chat) => _chat = chat;

    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Bring an application to the front by name (before screenshotting a game that renders behind other windows, or before input that " +
        "needs the real keyboard). Restores it if minimized. It takes the foreground from the person, so it waits its turn like the real mouse; " +
        "clicks and typing usually don't need it (they reach apps in the background).",
        """{"type":"object","properties":{"app":{"type":"string","description":"Process name or window title (e.g. 'Godot')"}},"required":["app"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var app = JsonArgs.String(arguments, "app");
        if (string.IsNullOrWhiteSpace(app)) return "Error: app is required.";
        string why;
        if (WindowServices.FindApp(app) is not { } hit) why = "no window of it is open";
        else
        {
            if (TerminalGuard.IsHarness(hit.Handle)) return TerminalGuard.SelfClickRefusal("bringing forward");
            if (_chat.Refuse(hit.Handle, "bringing forward") is { } refusal) return refusal;
            _chat.Aim(hit.Handle, hit.Label);
            InputLease lease;
            try
            {
                lease = await _chat.AcquireRealInputAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InputWaitException wait)
            {
                return wait.Message;
            }
            using (lease)
            {
                if (WindowFocus.Bring(hit.Handle, out why))
                {
                    await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    return $"Focused {app} ({hit.Label}).";
                }
            }
        }
        return $"Error: '{app}' is not running or not focusable ({why}). process_list/inspect_process to check; process_start or `Start-Process \"{app}\"` to launch it.";
    }
}
