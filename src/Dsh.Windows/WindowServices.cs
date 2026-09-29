using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace Dsh.Windows;

// MARK: - Geometry

/// <summary>A rectangle in physical screen pixels, in virtual-screen coordinates: the primary
/// display's top-left is 0,0 and a monitor to its left or above has negative coordinates.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>The overlap of two rectangles (empty when they don't touch).</summary>
    public ScreenRect Intersect(ScreenRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(Right, other.Right);
        var bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new ScreenRect(left, top, right - left, bottom - top) : default;
    }

    internal static ScreenRect From(Native.RECT r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    /// <summary>"800×600 at 100,200", the shape list_windows prints.</summary>
    public override string ToString() => $"{Width}×{Height} at {X},{Y}";
}

// MARK: - Windows

/// <summary>One top-level window on the desktop. <see cref="Chrome"/> marks the taskbar, tool
/// palettes, overlays and untitled helper windows — the Windows counterpart of the window
/// server's non-zero layers on macOS.</summary>
public sealed record WindowInfo(long Id, string Owner, string Title, int ProcessId, ScreenRect Bounds)
{
    public string ClassName { get; init; } = "";
    public bool Chrome { get; init; }
    /// <summary>Minimized windows have no on-screen pixels; capture needs focus_app first.</summary>
    public bool Minimized { get; init; }

    public IntPtr Handle => new(Id);

    /// <summary>"app — title", the way every tool names a window.</summary>
    public string Label => $"{Owner} — {(Title.Length == 0 ? "(untitled)" : Title)}";
}

/// <summary>Window enumeration straight from user32/DWM: no UI Automation, no permissions. Cheap
/// (a few ms) and safe on hung apps, because titles are read without sending messages.</summary>
public static class WindowServices
{
    /// <summary>Window classes that are shell furniture rather than an app's window.</summary>
    private static readonly HashSet<string> ChromeClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "tooltips_class32",
    };

    /// <summary>The desktop itself (wallpaper, icons) — never a capture or focus target.</summary>
    private static readonly HashSet<string> DesktopClasses = new(StringComparer.OrdinalIgnoreCase) { "Progman", "WorkerW" };

    /// <summary>All visible, uncloaked top-level windows, topmost first. Minimized app windows are
    /// included (flagged) so "why can't I see it" has an answer; chrome only when asked.</summary>
    public static IReadOnlyList<WindowInfo> OnScreenWindows(bool includeChrome = false)
    {
        var handles = new List<IntPtr>();
        Native.EnumWindowsProc callback = (hWnd, _) =>
        {
            handles.Add(hWnd);
            return true;
        };
        using var dpi = DpiScope.PerMonitor();
        Native.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        var names = new Dictionary<int, string>();
        var output = new List<WindowInfo>();
        foreach (var hWnd in handles)
        {
            if (Describe(hWnd, names) is not { } info) continue;
            if (!includeChrome && info.Chrome) continue;
            output.Add(info);
        }
        return output;
    }

    /// <summary>The window a screenshot/screen_watch <c>window</c> argument names: a window id from
    /// list_windows, else the first window whose app or title contains the text — preferring
    /// ordinary on-screen windows over chrome, and both over minimized ones.</summary>
    public static WindowInfo? Match(string needle) => Match(OnScreenWindows(includeChrome: true), needle);

    internal static WindowInfo? Match(IReadOnlyList<WindowInfo> windows, string needle)
    {
        needle = needle.Trim();
        if (needle.Length == 0) return null;
        if (TryParseId(needle, out var id) && windows.FirstOrDefault(w => w.Id == id) is { } byId) return byId;
        bool Hit(WindowInfo w) =>
            w.Owner.Contains(needle, StringComparison.OrdinalIgnoreCase) || w.Title.Contains(needle, StringComparison.OrdinalIgnoreCase);
        return windows.FirstOrDefault(w => !w.Minimized && !w.Chrome && Hit(w))
            ?? windows.FirstOrDefault(w => !w.Minimized && Hit(w))
            ?? windows.FirstOrDefault(Hit);
    }

    /// <summary>The front window of an app named the way a person would name it: an exact process
    /// name ("notepad", "Godot_v4.3-stable_win64.exe") first, then any app or title substring.</summary>
    public static WindowInfo? FindApp(string app) => FindApp(OnScreenWindows(includeChrome: true), app);

    internal static WindowInfo? FindApp(IReadOnlyList<WindowInfo> windows, string app)
    {
        var name = StripExe(app.Trim());
        if (name.Length == 0) return null;
        bool Exact(WindowInfo w) => w.Owner.Equals(name, StringComparison.OrdinalIgnoreCase);
        return windows.FirstOrDefault(w => Exact(w) && !w.Chrome && !w.Minimized)
            ?? windows.FirstOrDefault(w => Exact(w) && !w.Chrome)
            ?? windows.FirstOrDefault(Exact)
            ?? Match(windows, name);
    }

    /// <summary>The window under a screen point (physical pixels), or zero.</summary>
    public static IntPtr WindowAt(int x, int y)
    {
        using var dpi = DpiScope.PerMonitor();
        return Native.WindowFromPoint(new Native.POINT { X = x, Y = y });
    }

    /// <summary>Whether any process has this name (with or without ".exe").</summary>
    public static bool ProcessExists(string app)
    {
        var processes = Process.GetProcessesByName(StripExe(app.Trim()));
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    internal static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static bool TryParseId(string text, out long id)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id);
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    // MARK: Describing one window

    /// <summary>A window's facts, or null when it isn't something a person could see.</summary>
    internal static WindowInfo? Describe(IntPtr hWnd, Dictionary<int, string>? names = null)
    {
        if (!Native.IsWindowVisible(hWnd) || IsCloaked(hWnd)) return null;
        var className = ClassName(hWnd);
        if (DesktopClasses.Contains(className)) return null;
        var title = Title(hWnd);
        var minimized = Native.IsIconic(hWnd);
        var bounds = Bounds(hWnd);
        if (!minimized && bounds.IsEmpty) return null;
        var chrome = IsChrome(Native.GetWindowLong(hWnd, Native.GWL_EXSTYLE), title, className);
        if (minimized && chrome) return null;
        var pid = ProcessIdOf(hWnd, className);
        string owner;
        if (names is null) owner = ProcessName(pid);
        else if (!names.TryGetValue(pid, out owner!)) names[pid] = owner = ProcessName(pid);
        return new WindowInfo(hWnd.ToInt64(), owner, title, pid, minimized ? default : bounds)
        {
            ClassName = className,
            Chrome = chrome,
            Minimized = minimized,
        };
    }

    /// <summary>Tool windows, no-activate overlays, untitled helpers and shell furniture.</summary>
    internal static bool IsChrome(int extendedStyle, string title, string className) =>
        (extendedStyle & (Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE)) != 0
        || string.IsNullOrWhiteSpace(title)
        || ChromeClasses.Contains(className);

    internal static string Title(IntPtr hWnd)
    {
        var buffer = new char[512];
        var length = Native.InternalGetWindowText(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    internal static string ClassName(IntPtr hWnd)
    {
        var buffer = new char[256];
        var length = Native.GetClassName(hWnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : "";
    }

    /// <summary>The visible frame (DWM extended bounds, without the invisible resize border),
    /// falling back to the window rectangle where DWM can't say.</summary>
    internal static ScreenRect Bounds(IntPtr hWnd)
    {
        try
        {
            if (Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT frame,
                                             Marshal.SizeOf<Native.RECT>()) == 0
                && frame.Right > frame.Left && frame.Bottom > frame.Top)
            {
                return ScreenRect.From(frame);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // No DWM (very old or stripped-down systems): the window rectangle is what is drawn.
        }
        return Native.GetWindowRect(hWnd, out var rect) ? ScreenRect.From(rect) : default;
    }

    /// <summary>Cloaked windows (other virtual desktops, suspended Store apps) are "visible" to
    /// user32 but not on screen.</summary>
    private static bool IsCloaked(IntPtr hWnd)
    {
        try
        {
            return Native.DwmGetWindowAttribute(hWnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The process behind a window. Store apps draw inside a frame owned by
    /// ApplicationFrameHost; the app's own process owns the core window inside it.</summary>
    internal static int ProcessIdOf(IntPtr hWnd, string? className = null)
    {
        Native.GetWindowThreadProcessId(hWnd, out var pid);
        if ((className ?? ClassName(hWnd)) == "ApplicationFrameWindow")
        {
            var core = Native.FindWindowEx(hWnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);
            if (core != IntPtr.Zero)
            {
                Native.GetWindowThreadProcessId(core, out var inner);
                if (inner != 0) pid = inner;
            }
        }
        return (int)pid;
    }

    // MARK: Processes

    /// <summary>A process's executable name without ".exe" ("?" when it is gone).</summary>
    public static string ProcessName(int pid)
    {
        if (ImagePath(pid) is { } path) return Path.GetFileNameWithoutExtension(path);
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>The full executable path, readable for any process the user could open in Task
    /// Manager (limited-information access works on elevated processes too).</summary>
    internal static string? ImagePath(int pid)
    {
        if (pid <= 0) return null;
        using var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new char[1024];
        var size = buffer.Length;
        return Native.QueryFullProcessImageName(handle, 0, buffer, ref size) && size > 0 ? new string(buffer, 0, size) : null;
    }
}
