namespace Vision.Core;

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
    /// <summary>The task it was given (the start of it), for its log.</summary>
    public string? Prompt { get; init; }
    /// <summary>The route (model server) it actually runs on — set once it has one, even when there was no choice — and the
    /// model id its requests go to. What the background-activity menu shows, so "is the subagent really on the model I
    /// configured?" has an answer.</summary>
    public string? Route { get; init; }
    public string? Model { get; init; }
    /// <summary>Why it runs where it does when that isn't where it was asked to ("its agent file asks for “spark-2”, …").</summary>
    public string? Placement { get; init; }
    /// <summary>The plan step it is working (see <see cref="PlanAssignment"/>).</summary>
    public string? Assignment { get; init; }
    /// <summary>The plan step the agent tool's <c>step</c> argument named (a number or the step's text), as given.</summary>
    public string? RequestedStep { get; init; }
    /// <summary>Tokens its model calls used (known when its run ends).</summary>
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }
    /// <summary>What it did, stamped: started, where it runs, each tool call, how it ended. Bounded.</summary>
    public IReadOnlyList<AgentRunLine> Log { get; init; } = [];

    public TimeSpan Elapsed(DateTimeOffset now) => (FinishedAt ?? now) - StartedAt;
    public bool IsRunning => Status == AgentRunStatus.Running;
    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>"Qwen3-Coder-30B on DGX Spark" — the model and where; null until it has a server.</summary>
    public string? PlacementLabel => (Model, Route ?? Server) switch
    {
        ({ } model, { } route) => $"{model} on {route}",
        (null, { } route) => route,
        ({ } model, null) => model,
        _ => null,
    };
}

/// <summary>One stamped line of a subagent's log.</summary>
public sealed record AgentRunLine(DateTimeOffset At, string Text);

/// <summary>Every subagent a chat has started — waiting for a server, working, or finished — so a side
/// panel can show who is doing what. Foreground and background runs both land here.</summary>
public sealed class AgentRoster
{
    private const int Retained = 60;
    /// <summary>How many log lines a run keeps.</summary>
    public const int MaxLogLines = 200;

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
    public string Begin(string description, string agentType, bool background, string? prompt = null, string? step = null)
    {
        AgentRunInfo run;
        lock (_lock)
        {
            var at = _clock.GetUtcNow();
            run = new AgentRunInfo($"a{++_counter}", description, agentType, background, at)
            {
                Prompt = prompt is null ? null : TextUtil.Prefix(prompt, 2_000),
                RequestedStep = string.IsNullOrWhiteSpace(step) ? null : step.Trim(),
                Log = [new AgentRunLine(at, $"Started ({agentType}{(background ? ", in the background" : "")})")],
            };
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

    /// <summary>Where it runs now: the route, the model, and — when that isn't where it was asked to run — why.</summary>
    public void SetPlacement(string id, string route, string model, string? note = null)
    {
        var at = _clock.GetUtcNow();
        Update(id, r =>
        {
            var moved = r.Route is not null && r.Route != route;
            var line = (moved ? "Moved to " : "Running on ") + $"{model} · {route}" + (note is null ? "" : " — " + note);
            return r with { Route = route, Model = model, Placement = note ?? r.Placement, Log = Appending(r.Log, new AgentRunLine(at, line)) };
        });
    }

    /// <summary>The plan step it works (set by the host when it starts).</summary>
    public void SetAssignment(string id, string? step)
    {
        if (string.IsNullOrWhiteSpace(step)) return;
        Update(id, r => r with { Assignment = step });
    }

    /// <summary>Tokens a model run of it used.</summary>
    public void AddTokens(string id, long prompt, long completion)
    {
        if (prompt <= 0 && completion <= 0) return;
        Update(id, r => r with { PromptTokens = r.PromptTokens + prompt, CompletionTokens = r.CompletionTokens + completion });
    }

    /// <summary>What the run is doing now; <paramref name="step"/> counts it as a tool call (and writes it to the run's log).</summary>
    public void Note(string id, string activity, bool step = false)
    {
        var at = _clock.GetUtcNow();
        Update(id, r => r.IsRunning
            ? r with
            {
                Activity = activity,
                Steps = r.Steps + (step ? 1 : 0),
                Log = step || r.Log.LastOrDefault()?.Text != activity ? Appending(r.Log, new AgentRunLine(at, activity)) : r.Log,
            }
            : r);
    }

    public void Finish(string id, AgentRunStatus status, string report)
    {
        var at = _clock.GetUtcNow();
        Update(id, r => r.IsRunning
            ? r with
            {
                Status = status,
                FinishedAt = at,
                Report = report,
                Activity = status switch { AgentRunStatus.Done => "done", AgentRunStatus.Stopped => "stopped", _ => "failed" },
                Log = Appending(r.Log, new AgentRunLine(at, status switch
                {
                    AgentRunStatus.Done => "Finished — report delivered",
                    AgentRunStatus.Stopped => "Stopped",
                    _ => "Failed: " + TextUtil.Prefix(report, 200),
                })),
            }
            : r);
    }

    private static IReadOnlyList<AgentRunLine> Appending(IReadOnlyList<AgentRunLine> log, AgentRunLine line)
    {
        var list = new List<AgentRunLine>(log) { line };
        if (list.Count > MaxLogLines) list.RemoveRange(0, list.Count - MaxLogLines);
        return list;
    }

    /// <summary>Forget the runs that are over.</summary>
    public void ClearFinished()
    {
        lock (_lock) _runs.RemoveAll(r => !r.IsRunning);
        Listeners.Raise(Changed, new AgentRunInfo("", "", "", false, _clock.GetUtcNow()) { Status = AgentRunStatus.Done });
    }
}
