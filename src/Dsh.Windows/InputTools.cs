using System.Globalization;
using System.Runtime.InteropServices;
using Dsh.Core;

namespace Dsh.Windows;

// MARK: - Key names

/// <summary>One parsed key combo: a virtual key plus the modifiers held around it.</summary>
internal readonly record struct KeyCombo(string Key, ushort VirtualKey, bool Ctrl = false, bool Alt = false, bool Shift = false,
                                         bool Win = false);

/// <summary>Named keys → virtual-key codes, and the event sequences that type them.</summary>
internal static class KeyNames
{
    public const ushort VkBack = 0x08, VkTab = 0x09, VkReturn = 0x0D, VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12,
                        VkLWin = 0x5B;

    /// <summary>What the "unknown key" error lists.</summary>
    public const string Known =
        "enter, tab, esc, space, backspace, delete, insert, up/down/left/right, home, end, pgup, pgdn, f1–f24, a–z, 0–9, plus, minus, comma, period (modifiers: ctrl, alt, shift, win)";

    internal static readonly IReadOnlyDictionary<string, ushort> Codes = Build();

    private static Dictionary<string, ushort> Build()
    {
        var map = new Dictionary<string, ushort>(StringComparer.Ordinal)
        {
            ["enter"] = VkReturn, ["return"] = VkReturn, ["tab"] = VkTab, ["esc"] = 0x1B, ["escape"] = 0x1B,
            ["space"] = 0x20, ["backspace"] = VkBack, ["back"] = VkBack, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["insert"] = 0x2D, ["ins"] = 0x2D,
            ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
            ["home"] = 0x24, ["end"] = 0x23, ["pgup"] = 0x21, ["pageup"] = 0x21, ["pgdn"] = 0x22, ["pagedown"] = 0x22,
            ["capslock"] = 0x14, ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["pause"] = 0x13, ["apps"] = 0x5D,
            ["shift"] = VkShift, ["ctrl"] = VkControl, ["control"] = VkControl, ["alt"] = VkMenu,
            ["win"] = VkLWin, ["windows"] = VkLWin,
            // US-layout positions of the punctuation keys, for shortcuts such as ctrl+plus (zoom).
            ["plus"] = 0xBB, ["+"] = 0xBB, ["="] = 0xBB, ["minus"] = 0xBD, ["-"] = 0xBD,
            ["comma"] = 0xBC, [","] = 0xBC, ["period"] = 0xBE, ["."] = 0xBE,
        };
        for (var i = 1; i <= 24; i++) map[$"f{i}"] = (ushort)(0x70 + i - 1);
        for (var c = 'a'; c <= 'z'; c++) map[c.ToString()] = char.ToUpperInvariant(c);
        for (var c = '0'; c <= '9'; c++) map[c.ToString()] = c;
        return map;
    }

    /// <summary>"ctrl+shift+s", "ctrl,s", "F5", "enter" → a combo; null for an unknown key or
    /// modifier. "cmd" is read as ctrl, the Windows counterpart of the macOS shortcut.</summary>
    public static KeyCombo? Parse(string combo)
    {
        var text = combo.Trim();
        if (text.Length == 0) return null;
        var parts = new List<string>();
        if (text.Length == 1)
        {
            parts.Add(text);
        }
        else
        {
            parts.AddRange(text.Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            // "ctrl++" and "shift+,": the separator character is itself the key.
            if (text[^1] is '+' or ',' && text[^2] is '+' or ',') parts.Add(text[^1].ToString());
        }
        if (parts.Count == 0 || !Codes.TryGetValue(parts[^1].ToLowerInvariant(), out var vk)) return null;
        var result = new KeyCombo(parts[^1].ToLowerInvariant(), vk);
        foreach (var modifier in parts.Take(parts.Count - 1))
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd" or "command": result = result with { Ctrl = true }; break;
                case "alt" or "option" or "opt": result = result with { Alt = true }; break;
                case "shift": result = result with { Shift = true }; break;
                case "win" or "windows" or "super" or "meta": result = result with { Win = true }; break;
                default: return null;
            }
        }
        return result;
    }

    /// <summary>Modifiers down, the key down and up, modifiers up in reverse order.</summary>
    public static IReadOnlyList<(ushort VirtualKey, bool Up)> Sequence(KeyCombo combo)
    {
        var modifiers = new List<ushort>();
        if (combo.Ctrl) modifiers.Add(VkControl);
        if (combo.Alt) modifiers.Add(VkMenu);
        if (combo.Shift) modifiers.Add(VkShift);
        if (combo.Win) modifiers.Add(VkLWin);
        modifiers.Remove(combo.VirtualKey);
        var output = new List<(ushort, bool)>();
        foreach (var modifier in modifiers) output.Add((modifier, false));
        output.Add((combo.VirtualKey, false));
        output.Add((combo.VirtualKey, true));
        for (var i = modifiers.Count - 1; i >= 0; i--) output.Add((modifiers[i], true));
        return output;
    }

    /// <summary>Keys on the extended part of the keyboard (the navigation cluster, Windows and
    /// application keys). Their scan codes carry the E0 prefix; without the flag, games that read
    /// scan codes see the numeric keypad instead.</summary>
    public static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D;

    /// <summary>The events that type <paramref name="text"/>: line breaks and tabs as the Enter and
    /// Tab keys (what editors and forms expect), everything else as layout-independent Unicode
    /// characters. A CRLF pair is one Enter.</summary>
    public static IReadOnlyList<(ushort Code, bool IsVirtualKey)> TextUnits(string text)
    {
        var output = new List<(ushort, bool)>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    break;
                case '\r' or '\n':
                    output.Add((VkReturn, true));
                    break;
                case '\t':
                    output.Add((VkTab, true));
                    break;
                default:
                    output.Add((text[i], false));
                    break;
            }
        }
        return output;
    }
}

// MARK: - Input injection

/// <summary>SendInput, the one route for synthesized mouse and keyboard input.</summary>
internal static class InputSender
{
    private const int SmSwapButton = 23;

    public static bool Send(IReadOnlyCollection<Native.INPUT> inputs)
    {
        if (inputs.Count == 0) return true;
        var array = inputs.ToArray();
        return Native.SendInput((uint)array.Length, array, Marshal.SizeOf<Native.INPUT>()) == array.Length;
    }

    public static Native.INPUT Key(ushort vk, bool up)
    {
        var scan = (ushort)Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
        var flags = up ? Native.KEYEVENTF_KEYUP : 0;
        if (KeyNames.IsExtended(vk)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
        return Native.INPUT.Key(vk, scan, flags);
    }

    /// <summary>One UTF-16 unit typed as a character, independent of the keyboard layout.</summary>
    public static IEnumerable<Native.INPUT> Unicode(ushort unit)
    {
        yield return Native.INPUT.Key(0, unit, Native.KEYEVENTF_UNICODE);
        yield return Native.INPUT.Key(0, unit, Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP);
    }

    /// <summary>SendInput's absolute coordinates: 0…65535 across the virtual screen.</summary>
    internal static int Normalize(int value, int origin, int extent) =>
        extent <= 1 ? 0 : Math.Clamp((int)Math.Round((value - origin) * 65535.0 / (extent - 1)), 0, 65535);

    /// <summary>Move the pointer as a real mouse would (so apps and raw-input games see the move),
    /// then pin it to the exact pixel, since the 0…65535 scale can round by one.</summary>
    public static bool MoveTo(int x, int y)
    {
        using var dpi = DpiScope.PerMonitor();
        var screen = ScreenCapture.VirtualScreen();
        var move = Native.INPUT.Mouse(Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
                                      Normalize(x, screen.X, screen.Width), Normalize(y, screen.Y, screen.Height));
        var sent = Send([move]);
        Native.SetCursorPos(x, y);
        return sent;
    }

    /// <summary>Down/up flags for a button name. SendInput speaks physical buttons, so a
    /// left-handed setup (swapped buttons) needs them swapped back.</summary>
    public static (uint Down, uint Up) ButtonFlags(string button)
    {
        var swapped = Native.GetSystemMetrics(SmSwapButton) != 0;
        return button switch
        {
            "right" => swapped ? (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP) : (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            "middle" => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => swapped ? (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP) : (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
        };
    }

    public const string Rejected =
        "Error: Windows rejected the synthesized input (is the PC locked, or a UAC prompt on screen?).";
}

// MARK: - Foreground

/// <summary>Bringing a window to the front. Windows only lets a process take the foreground in
/// narrow cases (it received the last input event, or the foreground process allowed it), so a
/// plain SetForegroundWindow from a background agent usually just flashes the taskbar button.</summary>
internal static class WindowFocus
{
    public static bool Bring(IntPtr window, out string why)
    {
        why = "";
        using var dpi = DpiScope.PerMonitor();
        if (!Native.IsWindow(window))
        {
            why = "its window closed";
            return false;
        }
        if (Native.IsIconic(window)) Native.ShowWindow(window, Native.SW_RESTORE);
        if (TryForeground(window)) return true;

        // An injected no-op mouse event makes this process "the one that received the last input".
        InputSender.Send([Native.INPUT.Mouse(0)]);
        if (TryForeground(window)) return true;

        // Borrow the foreground thread's input state for the switch.
        var foregroundThread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        var thisThread = Native.GetCurrentThreadId();
        if (foregroundThread != 0 && foregroundThread != thisThread && Native.AttachThreadInput(thisThread, foregroundThread, true))
        {
            try
            {
                Native.BringWindowToTop(window);
                Native.SetForegroundWindow(window);
            }
            finally
            {
                Native.AttachThreadInput(thisThread, foregroundThread, false);
            }
            if (IsForeground(window)) return true;
        }

        // Last resort, the classic Alt tap (it can flash the previous window's menu bar).
        InputSender.Send([InputSender.Key(KeyNames.VkMenu, up: false), InputSender.Key(KeyNames.VkMenu, up: true)]);
        if (TryForeground(window)) return true;
        why = "Windows refused to switch the foreground window (focus-stealing prevention; click the app once, or ask the user to)";
        return false;
    }

    private static bool TryForeground(IntPtr window)
    {
        Native.SetForegroundWindow(window);
        return IsForeground(window);
    }

    /// <summary>The window, or a dialog it owns, is in front.</summary>
    public static bool IsForeground(IntPtr window)
    {
        var front = Native.GetForegroundWindow();
        return front != IntPtr.Zero
               && (front == window || Native.GetAncestor(front, Native.GA_ROOT) == window
                   || Native.GetAncestor(front, Native.GA_ROOTOWNER) == window);
    }
}

// MARK: - mouse

public sealed class MouseTool : IToolExecutor
{
    public const string ToolName = "mouse";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Click at screen coordinates (pixels, top-left origin of the main display; monitors to its left or above are negative): " +
        "button=left/right/middle, clicks=1 or 2 (double), or drag with to_x/to_y (press at x,y, move, release). " +
        "Coordinates come from screenshot/list_windows geometry. Apps running as administrator ignore it unless DSH is elevated too. " +
        "Screenshot after clicking to verify.",
        """{"type":"object","properties":{"x":{"type":"integer","description":"Screen x (pixels)"},"y":{"type":"integer","description":"Screen y (pixels)"},"button":{"type":"string","enum":["left","right","middle"],"description":"Default left"},"clicks":{"type":"integer","description":"1 default, 2 for double-click"},"to_x":{"type":"integer","description":"Drag release x"},"to_y":{"type":"integer","description":"Drag release y"}},"required":["x","y"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        if (ToolArgs.Number(args, "x") is not { } fx || ToolArgs.Number(args, "y") is not { } fy)
            return "Error: x and y are required (screen pixels from screenshot/list_windows).";
        var (x, y) = ((int)Math.Round(fx), (int)Math.Round(fy));
        var button = (JsonArgs.String(args, "button") ?? "left").Trim().ToLowerInvariant();
        if (button is not ("left" or "right" or "middle")) return "Error: button must be left, right or middle.";
        var clicks = Math.Clamp(JsonArgs.Int(args, "clicks", 1), 1, 2);
        var screen = ScreenCapture.VirtualScreen();
        if (!screen.Contains(x, y)) return OffScreen(x, y, screen);
        var (down, up) = InputSender.ButtonFlags(button);

        (int X, int Y)? to = null;
        if (ToolArgs.Number(args, "to_x") is { } tx && ToolArgs.Number(args, "to_y") is { } ty)
        {
            to = ((int)Math.Round(tx), (int)Math.Round(ty));
            if (!screen.Contains(to.Value.X, to.Value.Y)) return OffScreen(to.Value.X, to.Value.Y, screen);
        }

        // Right and middle clicks paste the clipboard in most terminals.
        if (button != "left")
        {
            var points = to is { } end ? new[] { (x, y), end } : [(x, y)];
            foreach (var (px, py) in points)
            {
                var target = WindowServices.WindowAt(px, py);
                if (target != IntPtr.Zero && TerminalGuard.IsTerminal(target))
                    return TerminalGuard.ClickRefusal(TerminalGuard.AppName(target), button);
            }
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
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Type into the frontmost app: text=literal string, and/or keys like " +
        "[\"ctrl+s\"] [\"enter\"] [\"ctrl+c\"] [\"esc\"] [\"up\",\"down\"] [\"f5\"] with modifiers ctrl/alt/shift/win. " +
        "`app` focuses that app first (the reliable pattern: keyboard(app:\"Godot\", keys:[\"F5\"])). " +
        "Refused into terminals (Windows Terminal, PowerShell, cmd, …), the Run box and Start search, and DSH itself — " +
        "that route would bypass the shell permission gate.",
        """{"type":"object","properties":{"text":{"type":"string","description":"Literal text to type"},"keys":{"type":"array","items":{"type":"string"},"description":"Key combos, e.g. ['ctrl+s','enter']"},"app":{"type":"string","description":"Focus this app first (process name or window title)"}},"required":[]}""");

    /// <summary>UTF-16 units per SendInput batch: small enough that apps keep up.</summary>
    private const int Chunk = 20;

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var text = JsonArgs.String(args, "text") ?? "";
        // Every combo is checked before anything is typed: a typo must not leave half an input behind.
        var combos = new List<KeyCombo>();
        foreach (var name in ToolArgs.Strings(args, "keys"))
        {
            if (KeyNames.Parse(name) is not { } combo) return $"Error: unknown key '{name}'. Known: {KeyNames.Known}.";
            combos.Add(combo);
        }
        if (text.Length == 0 && combos.Count == 0) return "Error: give text and/or keys.";

        // Terminal guard: resolve the *destination* window and refuse TTYs, launchers and DSH.
        var targetName = "frontmost app";
        var app = JsonArgs.String(args, "app");
        if (!string.IsNullOrWhiteSpace(app))
        {
            targetName = app;
            if (WindowServices.FindApp(app) is not { } hit)
                return $"Error: '{app}' is not running or cannot be focused: no window of it is open.";
            if (TerminalGuard.IsBlocked(hit.Handle)) return TerminalGuard.Refusal(app);
            if (!WindowFocus.Bring(hit.Handle, out var why)) return $"Error: '{app}' is not running or cannot be focused: {why}.";
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }
        if (Guard(0, 0) is { } refusal) return refusal;

        var typed = 0;
        var pressed = 0;
        var units = KeyNames.TextUnits(text);
        for (var start = 0; start < units.Count;)
        {
            var end = ChunkEnd(units, start);
            if (Guard(typed, pressed) is { } stopped) return stopped;
            if (!InputSender.Send(TextInputs(units, start, end))) return InputSender.Rejected;
            typed += end - start;
            start = end;
            await Task.Delay(8, cancellationToken).ConfigureAwait(false);
        }
        foreach (var combo in combos)
        {
            if (Guard(typed, pressed) is { } stopped) return stopped;
            if (!InputSender.Send(ComboInputs(combo))) return InputSender.Rejected;
            pressed++;
            // A shortcut may open something (Win+R, a terminal); give it time to take the
            // foreground before the next check.
            await Task.Delay(combo.Win || combo.Ctrl || combo.Alt ? 200 : 20, cancellationToken).ConfigureAwait(false);
        }
        var characters = text.Length == 0 ? 0 : new StringInfo(text).LengthInTextElements;
        return $"Typed {characters} characters and {pressed} key combo(s) into {targetName}. Screenshot to verify.";

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

// MARK: - focus_app

public sealed class FocusAppTool : IToolExecutor
{
    public const string ToolName = "focus_app";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Bring an application to the front by name (before screenshotting a game that renders behind other windows, or before keyboard/mouse input). Restores it if minimized.",
        """{"type":"object","properties":{"app":{"type":"string","description":"Process name or window title (e.g. 'Godot')"}},"required":["app"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var app = JsonArgs.String(arguments, "app");
        if (string.IsNullOrWhiteSpace(app)) return "Error: app is required.";
        string why;
        if (WindowServices.FindApp(app) is not { } hit) why = "no window of it is open";
        else if (WindowFocus.Bring(hit.Handle, out why))
        {
            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            return $"Focused {app} ({hit.Label}).";
        }
        return $"Error: '{app}' is not running or not focusable ({why}). process_list/inspect_process to check; process_start or `Start-Process \"{app}\"` to launch it.";
    }
}
