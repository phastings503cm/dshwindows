namespace Dsh.Core;

public enum AgentRunStatus { Running, Done, Failed, Stopped }

/// <summary>One subagent run, as the UI lists it.</summary>
public sealed record AgentRunInfo(string Id, string Description, string AgentType, bool Background, DateTimeOffset StartedAt)
{
    /// <summary>The model server it is (or was) running on, once it has one.</summary>
    public string? Server { get; init; }
    public AgentRunStatus Status { get; init; } = AgentRunStatus.Running;
    /// <summary>What it is doing right now ("grep: TODO", "waiting for a free server").</summary>
    public string Activity { get; init; } = "starting";
    /// <summary>Tool calls made so far.</summary>
    public int Steps { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? Report { get; init; }

    public TimeSpan Elapsed(DateTimeOffset now) => (FinishedAt ?? now) - StartedAt;
    public bool IsRunning => Status == AgentRunStatus.Running;
}

/// <summary>Every subagent a chat has started — waiting for a server, working, or finished — so a side
/// panel can show who is doing what. Foreground and background runs both land here.</summary>
public sealed class AgentRoster
{
    private const int Retained = 60;

    private readonly Lock _lock = new();
    private readonly List<AgentRunInfo> _runs = [];
    private readonly TimeProvider _clock;
    private int _counter;

    public AgentRoster(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>Raised (on any thread) whenever a run starts, moves or ends.</summary>
    public event Action<AgentRunInfo>? Changed;

    public IReadOnlyList<AgentRunInfo> All
    {
        get
        {
            lock (_lock) return [.. _runs];
        }
    }

    public IReadOnlyList<AgentRunInfo> Running => All.Where(r => r.IsRunning).ToList();

    public AgentRunInfo? Run(string id)
    {
        lock (_lock) return _runs.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Register a new run; its id.</summary>
    public string Begin(string description, string agentType, bool background)
    {
        AgentRunInfo run;
        lock (_lock)
        {
            run = new AgentRunInfo($"a{++_counter}", description, agentType, background, _clock.GetUtcNow());
            _runs.Add(run);
            // Keep the list bounded: the oldest finished runs go first.
            while (_runs.Count > Retained && _runs.FirstOrDefault(r => !r.IsRunning) is { } old) _runs.Remove(old);
        }
        Listeners.Raise(Changed, run);
        return run.Id;
    }

    public void Update(string id, Func<AgentRunInfo, AgentRunInfo> change)
    {
        AgentRunInfo updated;
        lock (_lock)
        {
            var index = _runs.FindIndex(r => r.Id == id);
            if (index < 0) return;
            updated = change(_runs[index]);
            _runs[index] = updated;
        }
        Listeners.Raise(Changed, updated);
    }

    public void SetServer(string id, string label) => Update(id, r => r with { Server = label });

    /// <summary>What the run is doing now; <paramref name="step"/> counts it as a tool call.</summary>
    public void Note(string id, string activity, bool step = false) =>
        Update(id, r => r.IsRunning ? r with { Activity = activity, Steps = r.Steps + (step ? 1 : 0) } : r);

    public void Finish(string id, AgentRunStatus status, string report) =>
        Update(id, r => r.IsRunning
            ? r with
            {
                Status = status,
                FinishedAt = _clock.GetUtcNow(),
                Report = report,
                Activity = status switch { AgentRunStatus.Done => "done", AgentRunStatus.Stopped => "stopped", _ => "failed" },
            }
            : r);

    /// <summary>Forget the runs that are over.</summary>
    public void ClearFinished()
    {
        lock (_lock) _runs.RemoveAll(r => !r.IsRunning);
        Listeners.Raise(Changed, new AgentRunInfo("", "", "", false, _clock.GetUtcNow()) { Status = AgentRunStatus.Done });
    }
}
