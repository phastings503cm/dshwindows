using System.Runtime.InteropServices;

namespace Dsh.Windows;

/// <summary>Pixels captured from the desktop: 32-bit BGRX rows, top-down, stride = Width × 4.
/// <see cref="Area"/> is where they came from, in screen pixels.</summary>
internal sealed record Capture(byte[] Pixels, int Width, int Height, ScreenRect Area)
{
    /// <summary>Same size and same pixels: the frame-diff test screen_watch uses.</summary>
    public bool SamePixels(Capture other) =>
        Width == other.Width && Height == other.Height && Pixels.AsSpan().SequenceEqual(other.Pixels);
}

/// <summary>GDI capture. The whole screen or an area is a BitBlt from the screen DC; one window
/// is PrintWindow with PW_RENDERFULLCONTENT, which also works when the window is covered, with a
/// fallback to the on-screen pixels for GPU-presented windows that print black.</summary>
internal static class ScreenCapture
{
    /// <summary>Every monitor together.</summary>
    public static ScreenRect VirtualScreen()
    {
        using var dpi = DpiScope.PerMonitor();
        return new ScreenRect(Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                              Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));
    }

    /// <summary>The primary monitor ("main display"), whose top-left is the origin.</summary>
    public static ScreenRect PrimaryScreen()
    {
        using var dpi = DpiScope.PerMonitor();
        return new ScreenRect(0, 0, Native.GetSystemMetrics(Native.SM_CXSCREEN), Native.GetSystemMetrics(Native.SM_CYSCREEN));
    }

    /// <summary>What is on screen inside <paramref name="area"/> (clipped to the monitors), or null
    /// when nothing could be copied — a locked workstation, the secure desktop, a service session.</summary>
    public static Capture? Screen(ScreenRect area)
    {
        using var dpi = DpiScope.PerMonitor();
        var clipped = area.Intersect(VirtualScreen());
        if (clipped.IsEmpty) return null;
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        try
        {
            // CAPTUREBLT includes layered (translucent, overlay) windows in the copy.
            var capture = Render(screen, clipped.Width, clipped.Height,
                dc => Native.BitBlt(dc, 0, 0, clipped.Width, clipped.Height, screen, clipped.X, clipped.Y,
                                    Native.SRCCOPY | Native.CAPTUREBLT));
            return capture is null ? null : capture with { Area = clipped };
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>One window's visible frame. <paramref name="fromScreen"/> reports the fallback,
    /// whose copy includes whatever overlaps the window.</summary>
    public static Capture? Window(WindowInfo window, out bool fromScreen)
    {
        fromScreen = false;
        using var dpi = DpiScope.PerMonitor();
        var handle = window.Handle;
        if (!Native.IsWindow(handle) || Native.IsIconic(handle)) return null;
        if (!Native.GetWindowRect(handle, out var rect)) return null;
        var whole = ScreenRect.From(rect);
        if (whole.IsEmpty) return null;
        var frame = WindowServices.Bounds(handle).Intersect(whole);
        if (frame.IsEmpty) frame = whole;

        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero) return null;
        Capture? printed;
        try
        {
            // PrintWindow renders the whole window rectangle, invisible resize border included.
            printed = Render(screen, whole.Width, whole.Height, dc => Native.PrintWindow(handle, dc, Native.PW_RENDERFULLCONTENT));
        }
        finally
        {
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
        if (printed is not null && !IsBlank(printed.Pixels)) return Crop(printed with { Area = whole }, frame);

        // Flip-model swap chains (Vulkan/D3D12 games, some browsers) can print as solid black:
        // take the pixels the compositor shows instead.
        fromScreen = true;
        return Screen(frame);
    }

    /// <summary>Draw into a fresh bitmap compatible with <paramref name="reference"/> and read its
    /// pixels back.</summary>
    private static Capture? Render(IntPtr reference, int width, int height, Func<IntPtr, bool> draw)
    {
        var dc = Native.CreateCompatibleDC(reference);
        if (dc == IntPtr.Zero) return null;
        var bitmap = Native.CreateCompatibleBitmap(reference, width, height);
        if (bitmap == IntPtr.Zero)
        {
            Native.DeleteDC(dc);
            return null;
        }
        try
        {
            var previous = Native.SelectObject(dc, bitmap);
            bool drawn;
            try
            {
                drawn = draw(dc);
            }
            finally
            {
                // GetDIBits needs the bitmap out of the DC.
                Native.SelectObject(dc, previous);
            }
            if (!drawn) return null;
            var info = new Native.BITMAPINFO
            {
                bmiHeader = new Native.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative: top-down rows
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = Native.BI_RGB,
                },
            };
            var pixels = new byte[checked(width * height * 4)];
            if (Native.GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref info, Native.DIB_RGB_COLORS) != height) return null;
            // GDI leaves the fourth byte undefined; pin it so identical frames compare equal.
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
            return new Capture(pixels, width, height, default);
        }
        finally
        {
            Native.DeleteObject(bitmap);
            Native.DeleteDC(dc);
        }
    }

    /// <summary>Every pixel black: what PrintWindow returns for content it can't reach.</summary>
    internal static bool IsBlank(ReadOnlySpan<byte> pixels)
    {
        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        foreach (var word in words)
        {
            if ((word & 0x00FF_FFFF) != 0) return false;
        }
        return true;
    }

    /// <summary>The part of <paramref name="capture"/> inside <paramref name="target"/> (screen
    /// coordinates).</summary>
    internal static Capture Crop(Capture capture, ScreenRect target)
    {
        var part = target.Intersect(capture.Area);
        if (part.IsEmpty || part == capture.Area) return capture;
        var dx = part.X - capture.Area.X;
        var dy = part.Y - capture.Area.Y;
        var output = new byte[part.Width * part.Height * 4];
        for (var row = 0; row < part.Height; row++)
        {
            Buffer.BlockCopy(capture.Pixels, ((dy + row) * capture.Width + dx) * 4, output, row * part.Width * 4, part.Width * 4);
        }
        return new Capture(output, part.Width, part.Height, part);
    }
}
