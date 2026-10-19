using System.Runtime.InteropServices;

namespace Vision.Windows;

/// <summary>Notices when the person uses the mouse or keyboard while Vision is working, so the real
/// mouse is never fought over: low-level hooks see every input event, and the ones Windows marks as
/// injected (SendInput — ours or another program's) are not the person.
///
/// The hooks live on a thread of their own with its own message loop: a hook callback runs on the
/// thread that installed it, and one stuck behind a busy UI thread would make the whole desktop's
/// input lag (and Windows drops hooks that keep it waiting). They are only installed while a chat is
/// using the machine.</summary>
public sealed class PersonInput
{
    public static PersonInput Shared { get; } = new();

    /// <summary>Mouse moves shorter than this (in pixels, from the last point the person was seen at)
    /// are a desk being bumped, not someone taking the mouse.</summary>
    public const int Jitter = 3;

    private readonly Lock _lock = new();
    private Thread? _thread;
    private uint _threadId;
    private long _lastHuman; // Environment.TickCount64 of the person's last input; 0 = not seen
    private int _lastX = int.MinValue, _lastY = int.MinValue;
    private long _expectUntil;
    private int _expectX = int.MinValue, _expectY = int.MinValue;
    private Native.HookProc? _mouseProc, _keyboardProc;

    /// <summary>The hooks are installed.</summary>
    public bool Watching { get; private set; }

    /// <summary>When the person last moved the mouse, clicked, scrolled or pressed a key (null when not
    /// watching, or not since watching began).</summary>
    public DateTimeOffset? LastHuman
    {
        get
        {
            var at = Interlocked.Read(ref _lastHuman);
            return at == 0 ? null : DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64 - at));
        }
    }

    /// <summary>Install the hooks (no-op when they are, or off Windows).</summary>
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        lock (_lock)
        {
            if (_thread is not null) return;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "person input watch" };
            thread.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
            _thread = thread;
            Seed();
        }
    }

    /// <summary>Fresh hooks have seen nothing: start from Windows' last-input time when it is recent, so the first grant of
    /// a session still waits for a person who is typing right now.</summary>
    private void Seed()
    {
        var info = new Native.LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<Native.LASTINPUTINFO>() };
        if (!Native.GetLastInputInfo(ref info)) return;
        if (Vision.Core.PersonSeed.Age(info.dwTime, unchecked((uint)Environment.TickCount), MachineControl.Arbiter.Timings.PersonQuiet) is not { } age) return;
        var at = Math.Max(1, Environment.TickCount64 - (long)age.TotalMilliseconds);
        // A hook event that already arrived is newer: keep it.
        if (Interlocked.Read(ref _lastHuman) < at) Interlocked.Exchange(ref _lastHuman, at);
    }

    /// <summary>Remove the hooks.</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_lock)
        {
            thread = _thread;
            _thread = null;
            if (thread is null) return;
            Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
        thread.Join(TimeSpan.FromSeconds(3));
        Interlocked.Exchange(ref _lastHuman, 0);
    }

    /// <summary>Vision is about to put the cursor at (<paramref name="x"/>, <paramref name="y"/>) itself
    /// (SetCursorPos, which some Windows builds report to the hooks without the injected flag): that
    /// move is not the person.</summary>
    public void ExpectCursor(int x, int y)
    {
        Volatile.Write(ref _expectX, x);
        Volatile.Write(ref _expectY, y);
        Interlocked.Exchange(ref _expectUntil, Environment.TickCount64 + 250);
    }

    /// <summary>Count an input as the person's now (tests, and a hook event that is theirs).</summary>
    public void NoteHuman() => Interlocked.Exchange(ref _lastHuman, Environment.TickCount64);

    private void Loop(ManualResetEventSlim ready)
    {
        _threadId = Native.GetCurrentThreadId();
        _mouseProc = OnMouse;
        _keyboardProc = OnKeyboard;
        var module = Native.GetModuleHandle(null);
        var mouse = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        var keyboard = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyboardProc, module, 0);
        Watching = mouse != IntPtr.Zero && keyboard != IntPtr.Zero;
        ready.Set();
        try
        {
            while (Native.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref message);
                Native.DispatchMessage(ref message);
            }
        }
        finally
        {
            if (mouse != IntPtr.Zero) Native.UnhookWindowsHookEx(mouse);
            if (keyboard != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboard);
            Watching = false;
            GC.KeepAlive(_mouseProc);
            GC.KeepAlive(_keyboardProc);
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if (IsHuman(data.flags, mouse: true) && CountsAsTakingTheMouse((uint)wParam, data.pt.X, data.pt.Y)) NoteHuman();
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr OnKeyboard(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if (IsHuman(data.flags, mouse: false)) NoteHuman();
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>Whether a hook event with these flags came from a real device (not SendInput).</summary>
    internal static bool IsHuman(uint flags, bool mouse) =>
        mouse ? (flags & (Native.LLMHF_INJECTED | Native.LLMHF_LOWER_IL_INJECTED)) == 0
              : (flags & (Native.LLKHF_INJECTED | Native.LLKHF_LOWER_IL_INJECTED)) == 0;

    /// <summary>A button, a wheel turn, or a move past the jitter, and not the cursor move Vision just made.</summary>
    private bool CountsAsTakingTheMouse(uint message, int x, int y)
    {
        if (message == Native.WM_MOUSEMOVE)
        {
            if (Environment.TickCount64 <= Interlocked.Read(ref _expectUntil) && x == Volatile.Read(ref _expectX) && y == Volatile.Read(ref _expectY))
            {
                _lastX = x;
                _lastY = y;
                return false;
            }
            var moved = _lastX == int.MinValue || Math.Abs(x - _lastX) >= Jitter || Math.Abs(y - _lastY) >= Jitter;
            if (!moved) return false;
        }
        _lastX = x;
        _lastY = y;
        return true;
    }
}
