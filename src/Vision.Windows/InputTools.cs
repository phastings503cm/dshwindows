using System.Globalization;
using System.Runtime.InteropServices;
using Vision.Core;

namespace Vision.Windows;

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
        PersonInput.Shared.ExpectCursor(x, y);
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
