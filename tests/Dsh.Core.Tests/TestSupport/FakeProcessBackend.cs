using System.Text;

namespace Dsh.Core.Tests;

/// <summary>A scripted <see cref="IProcessBackend"/>: nothing is launched, and the test decides what each
/// "process" prints and when it exits, so the manager's cursor, `until`, ring-buffer and exit logic can
/// be checked without timing races.</summary>
public sealed class FakeProcessBackend : IProcessBackend
{
    private readonly Lock _lock = new();
    private readonly List<FakeProcess> _started = [];
    private int _nextPid = 4242;

    /// <summary>Runs inside <see cref="Start"/>, before the handle is returned (output emitted here
    /// arrives before the manager has attached the handle, as a fast real process's can).</summary>
    public Action<FakeProcess>? OnStart { get; set; }

    /// <summary>When set, <see cref="Start"/> throws it (the program could not be launched).</summary>
    public Exception? FailWith { get; set; }

    public IReadOnlyList<FakeProcess> Started
    {
        get
        {
            lock (_lock) return [.. _started];
        }
    }

    public FakeProcess Last => Started[^1];

    public IProcessHandle Start(ProcessLaunch launch, IProcessSink sink)
    {
        if (FailWith is { } error) throw error;
        FakeProcess process;
        lock (_lock)
        {
            process = new FakeProcess(launch, sink, _nextPid++);
            _started.Add(process);
        }
        OnStart?.Invoke(process);
        return process;
    }
}

/// <summary>One scripted process. Emit output with <see cref="Emit(string)"/>, end it with
/// <see cref="Exit"/>; what the manager sends it is recorded.</summary>
public sealed class FakeProcess(ProcessLaunch launch, IProcessSink sink, int pid) : IProcessHandle
{
    private readonly Lock _lock = new();
    private readonly List<string> _writes = [];
    private readonly List<bool> _stops = [];
    private int _exited;

    public ProcessLaunch Launch { get; } = launch;
    public int ProcessId { get; } = pid;

    /// <summary>Typed text comes back as output, like `cat` on a terminal.</summary>
    public bool Echo { get; set; }

    /// <summary>When set, a Ctrl+C (0x03) in a write ends the process with this code.</summary>
    public int? CtrlCExitCode { get; set; }

    /// <summary>Whether a stop ends the process (143 polite, 137 forced, like SIGTERM/SIGKILL).</summary>
    public bool ExitsOnStop { get; set; } = true;

    public (int Cols, int Rows)? LastResize { get; private set; }

    public bool HasExited => Volatile.Read(ref _exited) != 0;

    public IReadOnlyList<string> Writes
    {
        get
        {
            lock (_lock) return [.. _writes];
        }
    }

    /// <summary>Everything written, concatenated.</summary>
    public string Written => string.Concat(Writes);

    /// <summary>Each stop request: true when forced.</summary>
    public IReadOnlyList<bool> Stops
    {
        get
        {
            lock (_lock) return [.. _stops];
        }
    }

    public void Emit(string text) => sink.Output(Encoding.UTF8.GetBytes(text));

    public void Emit(byte[] bytes) => sink.Output(bytes);

    public void Exit(int code)
    {
        if (Interlocked.Exchange(ref _exited, 1) == 0) sink.Exited(code);
    }

    public void Write(string text)
    {
        lock (_lock) _writes.Add(text);
        if (Echo) Emit(text);
        if (CtrlCExitCode is { } code && text.Contains('\u0003')) Exit(code);
    }

    public void Resize(int cols, int rows) => LastResize = (cols, rows);

    public void Stop(bool kill)
    {
        lock (_lock) _stops.Add(kill);
        if (ExitsOnStop) Exit(kill ? 137 : 143);
    }
}
