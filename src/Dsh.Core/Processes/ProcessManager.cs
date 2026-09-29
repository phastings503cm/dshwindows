namespace Dsh.Core;

// MARK: - The registry of live processes

/// <summary>Shared across engines and subagents: a process started by the main agent is readable by a
/// subagent, and survives engine rebuilds between turns. The app calls <see cref="StopAll"/> on exit so
/// nothing it started outlives it.</summary>
public sealed class ProcessManager
{
    public static ProcessManager Shared { get; } = new();

    /// <summary>The default output ring per process.</summary>
    public const int DefaultRingLimit = 512_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, BackgroundProcess> _processes = new(StringComparer.Ordinal);
    private readonly IProcessBackend _backend;
    private int _nextId = 1;

    public ProcessManager(IProcessBackend? backend = null)
    {
        _backend = backend ?? ProcessBackends.ForThisPlatform();
    }

    /// <summary>Output bytes kept per process; older output is dropped.</summary>
    public int RingLimit { get; init; } = DefaultRingLimit;

    /// <summary>Exited processes stay listed (and readable) for this long, then are dropped.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long process_start waits for first output before returning (less when the process
    /// exits sooner).</summary>
    public TimeSpan StartSettle { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>How often a process_read `until` wait looks for new output.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Launch <paramref name="command"/> in <paramref name="shell"/> on its own terminal of the
    /// given size. Throws when the process cannot be started.</summary>
    public BackgroundProcess Start(string command, string workingDirectory,
                                   IReadOnlyDictionary<string, string>? environment = null,
                                   int cols = 160, int rows = 50, AgentShell? shell = null, string? description = null)
    {
        string id;
        lock (_lock) id = $"p{_nextId++}";
        var process = new BackgroundProcess(id, command, description, RingLimit);
        var launch = new ProcessLaunch(command, workingDirectory, environment ?? new Dictionary<string, string>(),
                                       cols, rows, shell ?? AgentShell.Default);
        // The process is the sink, so output that arrives before Start returns is kept.
        var handle = _backend.Start(launch, process);
        process.Attach(handle);
        lock (_lock)
        {
            PruneLocked();
            _processes[id] = process;
        }
        return process;
    }

    public BackgroundProcess? Get(string id)
    {
        lock (_lock) return _processes.GetValueOrDefault(id.Trim());
    }

    /// <summary>Every listed process, oldest first.</summary>
    public IReadOnlyList<BackgroundProcess> All()
    {
        lock (_lock)
        {
            PruneLocked();
            return _processes.Values.OrderBy(p => p.StartedAt).ThenBy(p => NumericId(p.Id)).ToList();
        }
    }

    /// <summary>Stop one process; false when there is no such id.</summary>
    public bool Stop(string id, bool kill)
    {
        if (Get(id) is not { } process) return false;
        process.Stop(kill);
        return true;
    }

    /// <summary>End every running process at once (the app is quitting).</summary>
    public void StopAll()
    {
        List<BackgroundProcess> all;
        lock (_lock) all = [.. _processes.Values];
        foreach (var process in all) process.Stop(kill: true);
    }

    private void PruneLocked()
    {
        var now = DateTimeOffset.Now;
        foreach (var (id, process) in _processes.ToList())
        {
            if (process.ExitedAt is { } exited && now - exited > Retention) _processes.Remove(id);
        }
    }

    private static int NumericId(string id) => int.TryParse(id.AsSpan(1), out var n) ? n : int.MaxValue;
}
