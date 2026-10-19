using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Vision.Core;

namespace Vision.Windows;

// MARK: - Background input
//
// Clicks and keystrokes that reach an app without the person's mouse or keyboard: UI Automation
// patterns on the element, and window messages posted to the control's own window. InputPlanner (in
// Vision.Core) decides which of them to try for an element and a window; this file finds the window
// and element an input goes to and carries a route out.

/// <summary>The window, control and element an input goes to.</summary>
internal sealed class InputTarget
{
    /// <summary>The top-level window.</summary>
    public required IntPtr Top { get; init; }
    /// <summary>The control's own window: the deepest child at the point, the focused child, or the
    /// top-level window itself (WPF, browsers and modern apps draw every control inside one).</summary>
    public required IntPtr Control { get; init; }
    public required WindowKind Kind { get; init; }
    public int ProcessId { get; init; }
    /// <summary>The screen point the input is aimed at, when it has one.</summary>
    public (int X, int Y)? Point { get; init; }
    public AutomationElement? Element { get; set; }
    public ElementFacts? Facts { get; set; }
    /// <summary>MSAA's default action of the object at the point, when the element has no pattern.</summary>
    public Func<bool>? MsaaAction { get; set; }

    /// <summary>The app's name, for results ("notepad").</summary>
    public string App => WindowServices.ProcessName(ProcessId);

    /// <summary>"Button “Save” in notepad", or "notepad" without an element.</summary>
    public string Label => Facts is { } facts ? $"{facts.Label} in {App}" : App;
}

internal static class InputTargets
{
    /// <summary>The top-level window under a screen point. WindowFromPoint skips hidden windows and
    /// click-through overlays (the glow is one), as a real click would.</summary>
    public static IntPtr TopAt(int x, int y)
    {
        var hit = WindowServices.WindowAt(x, y);
        if (hit == IntPtr.Zero) return IntPtr.Zero;
        var root = Native.GetAncestor(hit, Native.GA_ROOT);
        return root == IntPtr.Zero ? hit : root;
    }

    /// <summary>The deepest visible child window of <paramref name="top"/> at a screen point.</summary>
    public static IntPtr ControlAt(IntPtr top, int x, int y)
    {
        using var dpi = DpiScope.PerMonitor();
        var current = top;
        for (var depth = 0; depth < 32; depth++)
        {
            var point = new Native.POINT { X = x, Y = y };
            if (!Native.ScreenToClient(current, ref point)) break;
            var child = Native.RealChildWindowFromPoint(current, point);
            if (child == IntPtr.Zero || child == current || !Native.IsWindowVisible(child)) break;
            current = child;
        }
        return current;
    }

    /// <summary>The window that has the keyboard focus inside <paramref name="top"/>'s thread (kept
    /// even while the window is in the background), or <paramref name="top"/> itself.</summary>
    public static IntPtr FocusOf(IntPtr top)
    {
        var thread = Native.GetWindowThreadProcessId(top, out _);
        var info = new Native.GUITHREADINFO { cbSize = Marshal.SizeOf<Native.GUITHREADINFO>() };
        if (thread != 0 && Native.GetGUIThreadInfo(thread, ref info) && info.hwndFocus != IntPtr.Zero
            && (info.hwndFocus == top || Native.GetAncestor(info.hwndFocus, Native.GA_ROOT) == top))
        {
            return info.hwndFocus;
        }
        return top;
    }

    public static WindowKind KindOf(IntPtr control, IntPtr top) =>
        InputPlanner.Classify(WindowServices.ClassName(control), WindowServices.ClassName(top));

    /// <summary>What a click at (<paramref name="x"/>, <paramref name="y"/>) would hit: in
    /// <paramref name="top"/> when given (a window behind others), else whatever is on top there.</summary>
    public static InputTarget? AtPoint(int x, int y, IntPtr top = default)
    {
        using var dpi = DpiScope.PerMonitor();
        if (top == IntPtr.Zero) top = TopAt(x, y);
        if (top == IntPtr.Zero) return null;
        var control = ControlAt(top, x, y);
        return new InputTarget
        {
            Top = top,
            Control = control,
            Kind = KindOf(control, top),
            ProcessId = WindowServices.ProcessIdOf(top),
            Point = (x, y),
        };
    }

    /// <summary>Typing into <paramref name="top"/>: its focused control.</summary>
    public static InputTarget ForWindow(IntPtr top)
    {
        var control = FocusOf(top);
        return new InputTarget { Top = top, Control = control, Kind = KindOf(control, top), ProcessId = WindowServices.ProcessIdOf(top) };
    }

    /// <summary>The element of a target found by name, with its own window when it has one.</summary>
    public static InputTarget ForElement(IntPtr top, AutomationElement element, ElementFacts facts, (int X, int Y)? center)
    {
        var handle = IntPtr.Zero;
        try
        {
            handle = new IntPtr(element.Current.NativeWindowHandle);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException)
        {
            // gone: the route will say so
        }
        var control = handle != IntPtr.Zero && Native.IsWindow(handle) ? handle : top;
        return new InputTarget
        {
            Top = top,
            Control = control,
            Kind = KindOf(control, top),
            ProcessId = WindowServices.ProcessIdOf(top),
            Point = center,
            Element = element,
            Facts = facts,
        };
    }
}

// MARK: - UI Automation

internal static class UiaElements
{
    /// <summary>How long one element lookup may take before the input moves on without it.</summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    /// <summary>The element at a screen point inside <paramref name="top"/> (pid <paramref name="pid"/>),
    /// even when another window covers that point.</summary>
    public static AutomationElement? AtPoint(IntPtr top, int pid, int x, int y)
    {
        try
        {
            var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (hit is not null && BelongsTo(hit, pid, top)) return hit;
        }
        catch (Exception ex) when (IsGone(ex))
        {
            // fall through to the walk
        }
        try
        {
            return DeepestAt(AutomationElement.FromHandle(top), x, y);
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return null;
        }
    }

    private static bool BelongsTo(AutomationElement element, int pid, IntPtr top)
    {
        if (element.Current.ProcessId != pid) return false;
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var hop = 0; hop < 40 && current is not null; hop++)
        {
            var handle = new IntPtr(current.Current.NativeWindowHandle);
            if (handle != IntPtr.Zero)
            {
                var root = Native.GetAncestor(handle, Native.GA_ROOT);
                return (root == IntPtr.Zero ? handle : root) == top;
            }
            current = walker.GetParent(current);
        }
        return false;
    }

    /// <summary>Down the control view: at each level the first child whose rectangle holds the point.</summary>
    private static AutomationElement? DeepestAt(AutomationElement root, int x, int y)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = root;
        var visited = 0;
        for (var depth = 0; depth < 40; depth++)
        {
            AutomationElement? next = null;
            for (var child = walker.GetFirstChild(current); child is not null && visited < 600; child = walker.GetNextSibling(child))
            {
                visited++;
                var rect = child.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Contains(x, y))
                {
                    next = child;
                    break;
                }
            }
            if (next is null) break;
            current = next;
        }
        return ReferenceEquals(current, root) ? null : current;
    }

    /// <summary>The element of <paramref name="top"/> named <paramref name="query"/>: "#id" or an exact
    /// AutomationId, then an exact name, then a name containing it (in that order of preference).</summary>
    public static AutomationElement? Find(IntPtr top, string query, TimeSpan budget)
    {
        query = query.Trim();
        if (query.Length == 0) return null;
        var id = query.StartsWith('#') ? query[1..] : query;
        var request = new CacheRequest { TreeFilter = Automation.ControlViewCondition };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        using (request.Activate())
        {
            var root = AutomationElement.FromHandle(top);
            var walker = new TreeWalker(Automation.ControlViewCondition);
            var clock = Stopwatch.StartNew();
            AutomationElement? exactName = null, partial = null;
            var stack = new Stack<AutomationElement>();
            stack.Push(root);
            var visited = 0;
            while (stack.Count > 0 && visited < 3000 && clock.Elapsed < budget)
            {
                var element = stack.Pop();
                visited++;
                if (!ReferenceEquals(element, root))
                {
                    var name = element.Cached.Name ?? "";
                    if (string.Equals(element.Cached.AutomationId, id, StringComparison.Ordinal)) return element;
                    if (!query.StartsWith('#'))
                    {
                        if (exactName is null && string.Equals(name.Trim(), query, StringComparison.OrdinalIgnoreCase)) exactName = element;
                        else if (partial is null && name.Contains(query, StringComparison.OrdinalIgnoreCase)) partial = element;
                    }
                }
                // Children pushed in reverse keep the walk in reading order.
                var children = new List<AutomationElement>();
                for (var child = Safe(() => walker.GetFirstChild(element, request)); child is not null;)
                {
                    children.Add(child);
                    var current = child;
                    child = Safe(() => walker.GetNextSibling(current, request));
                }
                for (var i = children.Count - 1; i >= 0; i--) stack.Push(children[i]);
            }
            return exactName ?? partial;
        }
    }

    private static AutomationElement? Safe(Func<AutomationElement?> step)
    {
        try
        {
            return step();
        }
        catch (Exception ex) when (IsGone(ex))
        {
            return null;
        }
    }

    /// <summary>What the planner needs to know about an element.</summary>
    public static ElementFacts Facts(AutomationElement element)
    {
        var current = element.Current;
        var type = current.ControlType?.ProgrammaticName ?? "";
        if (type.StartsWith("ControlType.", StringComparison.Ordinal)) type = type["ControlType.".Length..];
        var patterns = element.GetSupportedPatterns().Select(p => p.Id).ToHashSet();
        bool Has(AutomationPattern pattern) => patterns.Contains(pattern.Id);
        var value = Has(ValuePattern.Pattern);
        var readOnly = false;
        if (value)
        {
            try
            {
                readOnly = ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.IsReadOnly;
            }
            catch (Exception ex) when (ex is InvalidOperationException || IsGone(ex))
            {
                value = false;
            }
        }
        return new ElementFacts
        {
            ControlType = type,
            Name = current.Name,
            Enabled = current.IsEnabled,
            IsPassword = current.IsPassword,
            Invoke = Has(InvokePattern.Pattern),
            Toggle = Has(TogglePattern.Pattern),
            SelectionItem = Has(SelectionItemPattern.Pattern),
            ExpandCollapse = Has(ExpandCollapsePattern.Pattern),
            Value = value,
            ValueReadOnly = readOnly,
            Scroll = Scroller(element) is not null,
        };
    }

    /// <summary>What a click on <paramref name="element"/> acts on: the element itself when it has a pattern a
    /// click stands for (or is a text field), else the nearest ancestor that has one — a click on a button's
    /// caption or icon presses the button. The climb stops at windows, panes and documents.</summary>
    public static (AutomationElement Element, ElementFacts Facts) Actionable(AutomationElement element)
    {
        var facts = Facts(element);
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        var currentFacts = facts;
        for (var hop = 0; hop < 4; hop++)
        {
            if (currentFacts.Invoke || currentFacts.Toggle || currentFacts.SelectionItem || currentFacts.ExpandCollapse || currentFacts.IsTextField)
                return (current, currentFacts);
            if (currentFacts.ControlType is "Window" or "Pane" or "Document") break;
            AutomationElement? parent;
            try
            {
                parent = walker.GetParent(current);
            }
            catch (Exception ex) when (IsGone(ex))
            {
                break;
            }
            if (parent is null || Automation.Compare(parent, AutomationElement.RootElement)) break;
            current = parent;
            currentFacts = Facts(parent);
        }
        return (element, facts);
    }

    /// <summary>The element, or the nearest ancestor, that scrolls.</summary>
    public static AutomationElement? Scroller(AutomationElement element)
    {
        var walker = TreeWalker.ControlViewWalker;
        var current = element;
        for (var hop = 0; hop < 12 && current is not null && !Automation.Compare(current, AutomationElement.RootElement); hop++)
        {
            try
            {
                if (current.TryGetCurrentPattern(ScrollPattern.Pattern, out _)) return current;
                current = walker.GetParent(current);
            }
            catch (Exception ex) when (IsGone(ex))
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>An element's centre in screen pixels, when it has a rectangle.</summary>
    public static (int X, int Y)? Center(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || !double.IsFinite(rect.X) || !double.IsFinite(rect.Width)) return null;
        return ((int)Math.Round(rect.X + rect.Width / 2), (int)Math.Round(rect.Y + rect.Height / 2));
    }

    /// <summary>The element went away, or the app refused the call (hung, or more privileged).</summary>
    public static bool IsGone(Exception ex) => ex is ElementNotAvailableException or COMException or TimeoutException
        or UnauthorizedAccessException or ArgumentException;
}

// MARK: - Window messages

/// <summary>Input posted to a control's own window. None of it touches the cursor, the keyboard state
/// or the foreground window.</summary>
internal static class PostedInput
{
    /// <summary>A WM_SETTEXT / WM_GETTEXT / edit command waits at most this long for a hung app.</summary>
    private const uint SendTimeout = 2000;

    /// <summary>Client coordinates packed into a mouse message's lParam.</summary>
    internal static IntPtr PointParam(int x, int y) => new(unchecked((int)(((uint)(y & 0xFFFF) << 16) | (uint)(x & 0xFFFF))));

    /// <summary>A key message's lParam: repeat count 1, the scan code, the extended bit, and for a key-up
    /// the previous-state and transition bits.</summary>
    internal static IntPtr KeyParam(ushort vk, bool up)
    {
        var scan = Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC) & 0xFF;
        var value = 1u | (scan << 16);
        if (KeyNames.IsExtended(vk)) value |= 1u << 24;
        if (up) value |= (1u << 30) | (1u << 31);
        return new IntPtr(unchecked((int)value));
    }

    /// <summary>A screen point in <paramref name="control"/>'s client coordinates.</summary>
    private static (int X, int Y) Client(IntPtr control, int x, int y)
    {
        using var dpi = DpiScope.PerMonitor();
        var point = new Native.POINT { X = x, Y = y };
        Native.ScreenToClient(control, ref point);
        return (point.X, point.Y);
    }

    /// <summary>Hover, press, release (and press again for a double-click) at a screen point. False when
    /// Windows refused the first message (the window is gone, or runs as administrator).</summary>
    public static bool Click(IntPtr control, int x, int y, bool doubleClick)
    {
        var (cx, cy) = Client(control, x, y);
        var at = PointParam(cx, cy);
        var down = new IntPtr(Native.MK_LBUTTON);
        if (!Native.PostMessage(control, Native.WM_MOUSEMOVE, IntPtr.Zero, at)) return false;
        Native.PostMessage(control, Native.WM_LBUTTONDOWN, down, at);
        Native.PostMessage(control, Native.WM_LBUTTONUP, IntPtr.Zero, at);
        if (doubleClick)
        {
            Native.PostMessage(control, Native.WM_LBUTTONDBLCLK, down, at);
            Native.PostMessage(control, Native.WM_LBUTTONUP, IntPtr.Zero, at);
        }
        return true;
    }

    /// <summary>Wheel notches at a screen point (wheel messages carry screen coordinates).</summary>
    public static bool Wheel(IntPtr control, int x, int y, int notches, bool horizontal)
    {
        var wParam = new IntPtr(unchecked((int)((uint)(notches * 120) << 16)));
        return Native.PostMessage(control, horizontal ? Native.WM_MOUSEHWHEEL : Native.WM_MOUSEWHEEL, wParam, PointParam(x, y));
    }

    /// <summary>One key pressed and released.</summary>
    public static bool Key(IntPtr control, ushort vk) =>
        Native.PostMessage(control, Native.WM_KEYDOWN, new IntPtr(vk), KeyParam(vk, up: false))
        && Native.PostMessage(control, Native.WM_KEYUP, new IntPtr(vk), KeyParam(vk, up: true));

    /// <summary>Type text at the control's caret, every unit as WM_CHAR — line breaks as '\r' and tabs as
    /// '\t' too: a posted WM_KEYDOWN only becomes a character when the app's loop translates it, and that
    /// character is queued behind everything already posted, so it would land after the rest of the text.
    /// Returns how many units went, or -1 when the very first was refused.</summary>
    public static async Task<int> Chars(IntPtr control, IReadOnlyList<(ushort Code, bool IsVirtualKey)> units, CancellationToken cancellationToken)
    {
        for (var i = 0; i < units.Count; i++)
        {
            var (code, isKey) = units[i];
            var character = isKey ? code switch { KeyNames.VkReturn => (ushort)'\r', KeyNames.VkTab => (ushort)'\t', KeyNames.VkBack => (ushort)'\b', _ => (ushort)0 } : code;
            var sent = character != 0
                ? Native.PostMessage(control, Native.WM_CHAR, new IntPtr(character), new IntPtr(1))
                : Key(control, code);
            if (!sent) return i == 0 ? -1 : i;
            // A breather now and then: a window's message queue holds 10,000 posted messages.
            if (i % 40 == 39) await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }
        return units.Count;
    }

    /// <summary>Replace the control's whole text.</summary>
    public static bool SetText(IntPtr control, string text) =>
        Native.SendMessageTimeoutText(control, Native.WM_SETTEXT, IntPtr.Zero, text, Native.SMTO_ABORTIFHUNG, SendTimeout, out var result) != IntPtr.Zero
        && result != IntPtr.Zero;

    /// <summary>The control's text (WM_GETTEXT), or null when it doesn't answer.</summary>
    public static string? Text(IntPtr control)
    {
        if (Native.SendMessageTimeout(control, Native.WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, SendTimeout,
                                      out var length) == IntPtr.Zero)
        {
            return null;
        }
        var buffer = new char[Math.Max(1, length.ToInt32() + 1)];
        if (Native.SendMessageTimeoutBuffer(control, Native.WM_GETTEXT, new IntPtr(buffer.Length), buffer, Native.SMTO_ABORTIFHUNG,
                                            SendTimeout, out var copied) == IntPtr.Zero)
        {
            return null;
        }
        return new string(buffer, 0, Math.Clamp(copied.ToInt32(), 0, buffer.Length));
    }

    /// <summary>ctrl+a/c/x/v/z as the text box's own messages.</summary>
    public static bool EditCommand(IntPtr control, string key)
    {
        var (message, wParam, lParam) = key switch
        {
            "a" => (Native.EM_SETSEL, IntPtr.Zero, new IntPtr(-1)),
            "c" => (Native.WM_COPY, IntPtr.Zero, IntPtr.Zero),
            "x" => (Native.WM_CUT, IntPtr.Zero, IntPtr.Zero),
            "v" => (Native.WM_PASTE, IntPtr.Zero, IntPtr.Zero),
            "z" => (Native.WM_UNDO, IntPtr.Zero, IntPtr.Zero),
            _ => (0u, IntPtr.Zero, IntPtr.Zero),
        };
        return message != 0 && Native.SendMessageTimeout(control, message, wParam, lParam, Native.SMTO_ABORTIFHUNG, SendTimeout, out _) != IntPtr.Zero;
    }

    /// <summary>Ask the window to close, as Alt+F4 does.</summary>
    public static bool Close(IntPtr top) => Native.PostMessage(top, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
}

// MARK: - Carrying out a route

internal enum RouteOutcome
{
    /// <summary>Done: the message says what happened.</summary>
    Done,
    /// <summary>This route doesn't apply after all: try the next one.</summary>
    Skip,
    /// <summary>Stop here: the message says why (a disabled control, a refusal).</summary>
    Stop,
}

internal readonly record struct RouteResult(RouteOutcome Outcome, string Message = "")
{
    public static RouteResult Done(string message) => new(RouteOutcome.Done, message);
    public static RouteResult Skip(string why = "") => new(RouteOutcome.Skip, why);
    public static RouteResult Stop(string message) => new(RouteOutcome.Stop, message);
}

/// <summary>Carries out one background route for a mouse action.</summary>
internal static class PointerRoutes
{
    public const string Background = "in the background — the real mouse did not move";

    public static async Task<RouteResult> TryAsync(InputRoute route, InputTarget target, MachineChat chat, PointerAction action,
                                                   (int Notches, bool Horizontal) scroll, CancellationToken cancellationToken)
    {
        var point = target.Point;
        switch (route)
        {
            case InputRoute.PostClick or InputRoute.PostDoubleClick:
                if (point is not { } p) return RouteResult.Skip("no point to click");
                if (!PostedInput.Click(target.Control, p.X, p.Y, route == InputRoute.PostDoubleClick)) return RouteResult.Skip("Windows refused the message");
                if (target.Kind == WindowKind.Win32Edit)
                    chat.TypingTarget = new TypingTarget(target.Top, target.Control, target.Kind, p.X, p.Y, null, target.Facts, DateTimeOffset.UtcNow);
                return RouteResult.Done($"{(route == InputRoute.PostDoubleClick ? "Double-clicked" : "Clicked")} at ({p.X},{p.Y}) in {target.Label} " +
                                        $"with {InputPlanner.Describe(route)} ({Background}). Screenshot to verify.");

            case InputRoute.PostWheel:
                if (point is not { } w) return RouteResult.Skip("no point to scroll at");
                if (!PostedInput.Wheel(target.Control, w.X, w.Y, scroll.Notches, scroll.Horizontal)) return RouteResult.Skip("Windows refused the message");
                return RouteResult.Done($"Scrolled {Direction(scroll)} in {target.Label} with {InputPlanner.Describe(route)} ({Background}). Screenshot to verify.");

            case InputRoute.TypingTarget:
                if (point is not { } t) return RouteResult.Skip();
                chat.TypingTarget = new TypingTarget(target.Top, target.Control, target.Kind, t.X, t.Y, target.Element, target.Facts, DateTimeOffset.UtcNow);
                return RouteResult.Done($"Picked {target.Label} as the place to type, without clicking ({Background}): " +
                                        "keyboard text without an app goes there next.");

            case InputRoute.MsaaDefaultAction:
                if (target.MsaaAction is not { } msaa) return RouteResult.Skip();
                var (finished, ok) = await MtaThread.Run(msaa, UiaElements.LookupTimeout, cancellationToken).ConfigureAwait(false);
                if (finished && !ok) return RouteResult.Skip("the default action failed");
                return RouteResult.Done($"Activated {target.Label} with {InputPlanner.Describe(route)} ({Background}). Screenshot to verify.");

            default:
                return await UiaAsync(route, target, scroll, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Direction((int Notches, bool Horizontal) scroll) =>
        (scroll.Horizontal ? (scroll.Notches > 0 ? "right" : "left") : (scroll.Notches > 0 ? "up" : "down")) + $" {Math.Abs(scroll.Notches)}";

    private static async Task<RouteResult> UiaAsync(InputRoute route, InputTarget target, (int Notches, bool Horizontal) scroll,
                                                    CancellationToken cancellationToken)
    {
        if (target.Element is not { } element) return RouteResult.Skip();
        string verb = "";
        var (finished, result) = await MtaThread.Run(() =>
        {
            try
            {
                switch (route)
                {
                    case InputRoute.UiaInvoke:
                        verb = "Pressed";
                        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                        break;
                    case InputRoute.UiaToggle:
                        var toggle = (TogglePattern)element.GetCurrentPattern(TogglePattern.Pattern);
                        toggle.Toggle();
                        verb = $"Toggled ({toggle.Current.ToggleState.ToString().ToLowerInvariant()} now)";
                        break;
                    case InputRoute.UiaSelect:
                        verb = "Selected";
                        ((SelectionItemPattern)element.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();
                        break;
                    case InputRoute.UiaExpandCollapse:
                        var expand = (ExpandCollapsePattern)element.GetCurrentPattern(ExpandCollapsePattern.Pattern);
                        if (expand.Current.ExpandCollapseState is ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded)
                        {
                            expand.Collapse();
                            verb = "Collapsed";
                        }
                        else
                        {
                            expand.Expand();
                            verb = "Expanded";
                        }
                        break;
                    case InputRoute.UiaScroll:
                        if (UiaElements.Scroller(element) is not { } scroller) return RouteResult.Skip("nothing there scrolls");
                        var pattern = (ScrollPattern)scroller.GetCurrentPattern(ScrollPattern.Pattern);
                        if (scroll.Horizontal ? !pattern.Current.HorizontallyScrollable : !pattern.Current.VerticallyScrollable)
                            return RouteResult.Skip("it doesn't scroll that way");
                        // Wheel up (positive) shows what is above: a decrement.
                        var amount = scroll.Notches > 0 ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
                        for (var i = 0; i < Math.Abs(scroll.Notches) * 3; i++)
                        {
                            if (scroll.Horizontal) pattern.Scroll(amount, ScrollAmount.NoAmount);
                            else pattern.Scroll(ScrollAmount.NoAmount, amount);
                        }
                        verb = $"Scrolled {Direction(scroll)}";
                        break;
                    default:
                        return RouteResult.Skip();
                }
                return RouteResult.Done("");
            }
            catch (ElementNotEnabledException)
            {
                return RouteResult.Stop($"Did not click: {target.Label} is disabled (greyed out). Screenshot to see why.");
            }
            catch (InvalidOperationException)
            {
                return RouteResult.Skip("the element no longer offers that");
            }
            catch (Exception ex) when (UiaElements.IsGone(ex))
            {
                return RouteResult.Skip("the element went away");
            }
        }, UiaElements.LookupTimeout, cancellationToken).ConfigureAwait(false);
        // Invoke can block until what it opened (a modal dialog) closes: it did happen.
        if (!finished)
            return RouteResult.Done($"Pressed {target.Label} with {InputPlanner.Describe(route)} ({Background}); it opened something that is " +
                                    "still waiting (a dialog?). Screenshot to see it.");
        if (result.Outcome != RouteOutcome.Done) return result;
        return RouteResult.Done($"{verb} {target.Label} with {InputPlanner.Describe(route)} ({Background}). Screenshot to verify.");
    }
}

/// <summary>Carries out one background route for typing or a key.</summary>
internal static class KeyRoutes
{
    public const string Background = "in the background — the real keyboard was not used";

    /// <summary>Text into the target. Returns Done with the number of characters as the message.</summary>
    public static async Task<RouteResult> TextAsync(InputRoute route, InputTarget target, string text, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case InputRoute.PostChars:
                var sent = await PostedInput.Chars(target.Control, KeyNames.TextUnits(text), cancellationToken).ConfigureAwait(false);
                if (sent < 0) return RouteResult.Skip("Windows refused the message");
                return RouteResult.Done(InputPlanner.Describe(route));
            case InputRoute.PostSetText:
                return PostedInput.SetText(target.Control, text) ? RouteResult.Done(InputPlanner.Describe(route)) : RouteResult.Skip("it did not take the text");
            case InputRoute.UiaSetValue or InputRoute.UiaAppendValue:
                if (target.Element is not { } element) return RouteResult.Skip();
                var (finished, result) = await MtaThread.Run(() =>
                {
                    try
                    {
                        var value = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
                        var current = route == InputRoute.UiaAppendValue ? value.Current.Value ?? "" : "";
                        value.SetValue(current + text);
                        return RouteResult.Done(InputPlanner.Describe(route));
                    }
                    catch (ElementNotEnabledException)
                    {
                        return RouteResult.Stop($"Error: did not type: {target.Label} is disabled.");
                    }
                    catch (InvalidOperationException)
                    {
                        return RouteResult.Skip("the field did not take the text");
                    }
                    catch (Exception ex) when (UiaElements.IsGone(ex))
                    {
                        return RouteResult.Skip("the field went away");
                    }
                }, UiaElements.LookupTimeout, cancellationToken).ConfigureAwait(false);
                return finished ? result : RouteResult.Skip("the app did not answer");
            default:
                return RouteResult.Skip();
        }
    }

    /// <summary>One key combo into the target.</summary>
    public static async Task<RouteResult> KeyAsync(InputRoute route, InputTarget target, KeyCombo combo, CancellationToken cancellationToken)
    {
        switch (route)
        {
            case InputRoute.PostKeys:
                return PostedInput.Key(target.Control, combo.VirtualKey) ? RouteResult.Done(InputPlanner.Describe(route)) : RouteResult.Skip("Windows refused the message");
            case InputRoute.PostEditCommand:
                return PostedInput.EditCommand(target.Control, combo.Key) ? RouteResult.Done(InputPlanner.Describe(route)) : RouteResult.Skip("the text box did not answer");
            case InputRoute.PostClose:
                return PostedInput.Close(target.Top) ? RouteResult.Done(InputPlanner.Describe(route)) : RouteResult.Skip("Windows refused the message");
            case InputRoute.UiaInvoke or InputRoute.UiaToggle:
                var result = await PointerRoutes.TryAsync(route, target, MachineChat.Standalone(), PointerAction.Click, default, cancellationToken)
                    .ConfigureAwait(false);
                return result.Outcome == RouteOutcome.Done ? RouteResult.Done(InputPlanner.Describe(route)) : result;
            default:
                return RouteResult.Skip();
        }
    }
}
