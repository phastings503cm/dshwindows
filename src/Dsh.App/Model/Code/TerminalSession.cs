using System.IO;
using System.Text;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;

namespace Dsh.App.Model.Code;

/// <summary>One integrated-terminal tab: a shell on a pseudo console, plus the screen it draws to.
/// Output is parsed on the reader thread (under a lock on <see cref="Emulator"/>) so a noisy build
/// never queues work on the UI thread; the view redraws at most once per frame.</summary>
public sealed partial class TerminalSession : ObservableObject, IDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Cwd { get; }
    public TerminalEmulator Emulator { get; } = new();

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int? _exitCode;

    /// <summary>New output landed (raised on a background thread).</summary>
    public event Action? Updated;

    private readonly Dispatcher _dispatcher;
    private PseudoConsoleSession? _console;
    private bool _started;
    private string _lastTitle = "";

    public TerminalSession(string cwd, Dispatcher dispatcher)
    {
        Cwd = cwd;
        _dispatcher = dispatcher;
        _title = Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : cwd;
        Emulator.OnReply = reply => _console?.Write(reply);
        Emulator.OnBell = () => _dispatcher.BeginInvoke(() => System.Media.SystemSounds.Beep.Play());
    }

    /// <summary>Launch the shell. Safe to call repeatedly; only the first call runs.</summary>
    public void Start(int cols, int rows, string commandLine)
    {
        if (_started) return;
        _started = true;
        lock (Emulator) Emulator.Resize(rows, cols);
        if (!OperatingSystem.IsWindows())
        {
            Write("The integrated terminal needs Windows.\r\n");
            return;
        }
        try
        {
            _console = PseudoConsoleSession.Start(commandLine, Cwd, cols, rows, output: OnOutput, exited: OnExited);
            IsRunning = true;
        }
        catch (Exception ex)
        {
            Write($"Could not start a shell ({commandLine}): {ex.Message}\r\n");
        }
    }

    private void OnOutput(byte[] buffer, int count)
    {
        string? title = null;
        lock (Emulator)
        {
            Emulator.Feed(buffer.AsSpan(0, count));
            if (Emulator.Title.Length > 0 && Emulator.Title != _lastTitle) title = _lastTitle = Emulator.Title;
        }
        if (title is not null) _dispatcher.BeginInvoke(() => Title = ShortTitle(title));
        Updated?.Invoke();
    }

    /// <summary>Shells put the full path, or "Administrator: Windows PowerShell", in the title; the
    /// tab only has room for the last part.</summary>
    private static string ShortTitle(string title)
    {
        var trimmed = title.Trim();
        if (trimmed.Contains('\\') || trimmed.Contains('/'))
        {
            var last = trimmed.TrimEnd('\\', '/').Split('\\', '/').Last();
            if (last.Length > 0) return last;
        }
        return trimmed.Length > 40 ? trimmed[..40] + "…" : trimmed;
    }

    private void OnExited(int code)
    {
        Write($"\r\n\u001B[2m[process exited with code {code}]\u001B[0m\r\n");
        _dispatcher.BeginInvoke(() =>
        {
            IsRunning = false;
            ExitCode = code;
        });
    }

    /// <summary>Draw text locally (status lines), not sent to the shell.</summary>
    private void Write(string text)
    {
        lock (Emulator) Emulator.Feed(text);
        Updated?.Invoke();
    }

    public void Send(byte[] data) => _console?.Write(data);
    public void Send(string text) => _console?.Write(Encoding.UTF8.GetBytes(text));

    public bool BracketedPaste
    {
        get { lock (Emulator) return Emulator.BracketedPaste; }
    }

    public bool ApplicationCursorKeys
    {
        get { lock (Emulator) return Emulator.ApplicationCursorKeys; }
    }

    public void Resize(int cols, int rows)
    {
        if (cols <= 0 || rows <= 0) return;
        lock (Emulator)
        {
            if (cols == Emulator.Cols && rows == Emulator.Rows) return;
            Emulator.Resize(rows, cols);
        }
        _console?.Resize(cols, rows);
        Updated?.Invoke();
    }

    /// <summary>Clear the screen and history; the shell redraws its prompt.</summary>
    public void Clear()
    {
        lock (Emulator) Emulator.Reset();
        // ^L: PSReadLine and readline both clear and redraw the prompt.
        if (IsRunning) Send([0x0C]);
        Updated?.Invoke();
    }

    public void Terminate()
    {
        _console?.Kill();
        IsRunning = false;
    }

    /// <summary>Everything on screen and in history, as text.</summary>
    public string Transcript
    {
        get { lock (Emulator) return Emulator.Transcript; }
    }

    public void Dispose()
    {
        if (_console is { } console)
        {
            console.Output -= OnOutput;
            console.Exited -= OnExited;
            console.Dispose();
        }
        _console = null;
    }
}
