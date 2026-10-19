using System.Runtime.InteropServices;

namespace Vision.Windows;

// MARK: - What the machine-in-use glow needs from Windows

/// <summary>One monitor, in physical pixels (virtual-screen coordinates).</summary>
public sealed record MonitorInfo(ScreenRect Bounds, ScreenRect WorkArea, double Scale, bool Primary);

public static class Monitors
{
    /// <summary>Every monitor, the primary one first.</summary>
    public static IReadOnlyList<MonitorInfo> All()
    {
        var output = new List<MonitorInfo>();
        if (!OperatingSystem.IsWindows()) return output;
        using var dpi = DpiScope.PerMonitor();
        Native.MonitorEnumProc callback = (IntPtr monitor, IntPtr hdc, ref Native.RECT rect, IntPtr data) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>(), szDevice = "" };
            if (Native.GetMonitorInfo(monitor, ref info))
                output.Add(new MonitorInfo(ScreenRect.From(info.rcMonitor), ScreenRect.From(info.rcWork), ScaleOf(monitor), (info.dwFlags & 1) != 0));
            return true;
        };
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return output.OrderByDescending(m => m.Primary).ToList();
    }

    private static double ScaleOf(IntPtr monitor)
    {
        try
        {
            return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out var x, out _) == 0 && x > 0 ? x / 96.0 : 1.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    /// <summary>This session is shown over Remote Desktop.</summary>
    public static bool RemoteSession => OperatingSystem.IsWindows() && Native.GetSystemMetrics(Native.SM_REMOTESESSION) != 0;

    /// <summary>WDA_EXCLUDEFROMCAPTURE exists (Windows 10 2004). Before it, Windows treats the value as
    /// WDA_MONITOR, which shows the window as a black box in every capture — worse than no affinity.</summary>
    public static bool CanExcludeFromCapture => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);
}

/// <summary>The glow's windows: click-through, never activated, above everything, and left out of
/// screenshots where Windows allows.</summary>
public static class OverlayWindows
{
    /// <summary>Takes no input at all (clicks fall through to what is under it), never takes the focus,
    /// has no taskbar button and stays above ordinary windows.</summary>
    public static void MakeClickThrough(IntPtr window)
    {
        var style = Native.GetWindowLong(window, Native.GWL_EXSTYLE);
        Native.SetWindowLong(window, Native.GWL_EXSTYLE, style | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW
                                                         | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST);
    }

    /// <summary>The extended styles that make a window click-through are set.</summary>
    public static bool IsClickThrough(IntPtr window)
    {
        var style = Native.GetWindowLong(window, Native.GWL_EXSTYLE);
        const int needed = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_NOACTIVATE;
        return (style & needed) == needed;
    }

    /// <summary>Leave the window out of screenshots, recordings and screen sharing (true when Windows
    /// agreed), or put it back in.</summary>
    public static bool ExcludeFromCapture(IntPtr window, bool exclude)
    {
        if (exclude && !Monitors.CanExcludeFromCapture) return false;
        return Native.SetWindowDisplayAffinity(window, exclude ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE) && exclude;
    }

    /// <summary>The window's display affinity (0 = none, 0x11 = excluded from capture).</summary>
    public static uint Affinity(IntPtr window) => Native.GetWindowDisplayAffinity(window, out var affinity) ? affinity : 0;

    /// <summary>Put the window exactly over <paramref name="rect"/> (physical pixels), topmost, shown,
    /// without activating it.</summary>
    public static void Place(IntPtr window, ScreenRect rect)
    {
        using var dpi = DpiScope.PerMonitor();
        Native.SetWindowPos(window, Native.HWND_TOPMOST, rect.X, rect.Y, rect.Width, rect.Height,
                            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>Move the window's top-left to (x, y) keeping its size.</summary>
    public static void Move(IntPtr window, int x, int y)
    {
        using var dpi = DpiScope.PerMonitor();
        Native.SetWindowPos(window, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    /// <summary>The window's rectangle in physical pixels.</summary>
    public static ScreenRect Rect(IntPtr window)
    {
        using var dpi = DpiScope.PerMonitor();
        return Native.GetWindowRect(window, out var rect) ? ScreenRect.From(rect) : default;
    }

    /// <summary>Hide (false) or show without activating (true), from any thread.</summary>
    public static void SetVisible(IntPtr window, bool visible) => ShowWindowAsync(window, visible ? 4 /* SW_SHOWNOACTIVATE */ : 0 /* SW_HIDE */);

    /// <summary>Wait for the compositor to put the last changes on screen.</summary>
    public static void Flush()
    {
        try
        {
            DwmFlush();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // no DWM: changes are on screen already
        }
    }

    /// <summary>The window under a screen point, as a click would find it (the glow must never be it).</summary>
    public static IntPtr WindowAt(int x, int y) => WindowServices.WindowAt(x, y);

    // MARK: The stop key

    /// <summary>Ctrl+Alt+. for <paramref name="window"/>, wherever the focus is. False when another app owns it.</summary>
    public static bool RegisterStopKey(IntPtr window, int id) =>
        Native.RegisterHotKey(window, id, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_OEM_PERIOD);

    public static void UnregisterStopKey(IntPtr window, int id) => Native.UnregisterHotKey(window, id);

    /// <summary>WM_HOTKEY.</summary>
    public const int HotKeyMessage = (int)Native.WM_HOTKEY;

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();
}

/// <summary>Screen captures ask the glow to step aside when it can't be left out of them (over Remote
/// Desktop, where the viewer must see it, and before Windows 10 2004).</summary>
public static class CaptureShield
{
    /// <summary>Hides what must not be in a capture; disposing shows it again. Null = nothing to hide.</summary>
    public static Func<IDisposable?>? Hide { get; set; }

    internal static IDisposable? Raise()
    {
        try
        {
            return Hide?.Invoke();
        }
        catch (Exception)
        {
            return null; // a capture never fails because the glow couldn't step aside
        }
    }
}

/// <summary>The screen's pixels, for checks that the glow is (or isn't) in a capture.</summary>
public static class ScreenGrab
{
    /// <summary>BGRX pixels of <paramref name="area"/> (top-down, stride = width × 4), or null.</summary>
    public static (byte[] Pixels, int Width, int Height)? Region(ScreenRect area) =>
        ScreenCapture.Screen(area) is { } capture ? (capture.Pixels, capture.Width, capture.Height) : null;
}
