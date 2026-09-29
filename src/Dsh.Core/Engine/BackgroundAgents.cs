namespace Dsh.Core;

// MARK: - Background subagents
//
// agent with run_in_background: true starts a subagent that works while the main agent carries on
// (several can run in parallel — "explore the API layer" + "audit the tests" at once). Each chat owns
// one pool. The main agent checks on them with agent_status (optionally waiting), stops one with
// agent_stop, and is told automatically — as an automatic message in its next step — when one
// finishes. Stopping the chat stops its background agents too.

public enum BackgroundAgentStatus { Running, Done, Failed, Stopped }

/// <summary>One background subagent, as the pool reports it.</summary>
public sealed record BackgroundAgentJob(string Id, string Description, DateTimeOffset StartedAt)
{
    public DateTimeOffset? FinishedAt { get; init; }
    public BackgroundAgentStatus Status { get; init; } = BackgroundAgentStatus.Running;
    /// <summary>The subagent's final report (or the failure).</summary>
    public string? Report { get; init; }

    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.Now) - StartedAt;

    /// <summary>"running", "done", ... — the word the tools and notices use.</summary>
    public string StatusWord => Status.ToString().ToLowerInvariant();
}

public sealed class BackgroundAgents
{
    /// <summary>How many may run at once — a local model server has finite throughput.</summary>
    public int MaxConcurrent { get; }

    /// <summary>Raised (on any thread) whenever a job starts, finishes or is stopped.</summary>
    public event Action<BackgroundAgentJob>? Changed;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, BackgroundAgentJob> _jobs = new();
    private readonly List<string> _order = [];
    private readonly Dictionary<string, CancellationTokenSource> _cancellers = new();
    /// <summary>Finished jobs whose result the main agent hasn't been given yet.</summary>
    private readonly List<string> _unreported = [];
    private int _counter;

    public BackgroundAgents(int maxConcurrent = 4) => MaxConcurrent = maxConcurrent;

    public IReadOnlyList<BackgroundAgentJob> All
    {
        get
        {
            lock (_lock) return _order.Select(id => _jobs[id]).ToList();
        }
    }

    public IReadOnlyList<BackgroundAgentJob> Running => All.Where(j => j.Status == BackgroundAgentStatus.Running).ToList();

    public BackgroundAgentJob? Job(string id)
    {
        lock (_lock) return _jobs.GetValueOrDefault(id);
    }

    /// <summary>Start <paramref name="work"/> in the background. Returns the job, or an error message
    /// when too many are already running. The work gets a token that Stop cancels.</summary>
    public (BackgroundAgentJob? Job, string? Error) Launch(string description,
                                                         Func<CancellationToken, Task<(bool Ok, string Report)>> work)
    {
        BackgroundAgentJob job;
        var cts = new CancellationTokenSource();
        lock (_lock)
        {
            if (_jobs.Values.Count(j => j.Status == BackgroundAgentStatus.Running) >= MaxConcurrent)
            {
                cts.Dispose();
                return (null, TooMany(MaxConcurrent));
            }
            _counter++;
            job = new BackgroundAgentJob($"bg-{_counter}", description, DateTimeOffset.Now);
            _jobs[job.Id] = job;
            _order.Add(job.Id);
            _cancellers[job.Id] = cts;
        }
        Changed?.Invoke(job);
        _ = Task.Run(async () =>
        {
            (bool Ok, string Report) outcome;
            try
            {
                outcome = await work(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                outcome = (false, "Stopped before it finished.");
            }
            catch (Exception ex)
            {
                outcome = (false, ex.Message);
            }
            Finish(job.Id, cts.IsCancellationRequested ? BackgroundAgentStatus.Stopped
                           : outcome.Ok ? BackgroundAgentStatus.Done : BackgroundAgentStatus.Failed, outcome.Report);
        });
        return (job, null);
    }

    public static string TooMany(int max) =>
        $"{max} background agents are already running — wait for one (agent_status with wait_seconds) or stop one first.";

    /// <summary>Stop one job. Returns false when there is no such running job.</summary>
    public bool Stop(string id)
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_jobs.GetValueOrDefault(id)?.Status != BackgroundAgentStatus.Running) return false;
            cts = _cancellers.GetValueOrDefault(id);
        }
        cts?.Cancel();
        Finish(id, BackgroundAgentStatus.Stopped, "Stopped before it finished.");
        return true;
    }

    public void StopAll()
    {
        foreach (var job in Running) Stop(job.Id);
    }

    /// <summary>Wait until the job finishes or <paramref name="timeout"/> passes (cancellable).</summary>
    public async Task<BackgroundAgentJob?> WaitAsync(string id, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.Now + (timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        while (Job(id) is { Status: BackgroundAgentStatus.Running } && DateTimeOffset.Now < deadline
               && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        return Job(id);
    }

    /// <summary>Finished jobs the main agent hasn't seen yet (marks them seen).</summary>
    public IReadOnlyList<BackgroundAgentJob> TakeUnreported()
    {
        lock (_lock)
        {
            var list = _unreported.Select(id => _jobs[id]).ToList();
            _unreported.Clear();
            return list;
        }
    }

    /// <summary>True when finished jobs are waiting to be handed to the main agent.</summary>
    public bool HasUnreported
    {
        get
        {
            lock (_lock) return _unreported.Count > 0;
        }
    }

    /// <summary>The main agent read this job's result (agent_status): don't announce it again.</summary>
    public void MarkReported(string id)
    {
        lock (_lock) _unreported.Remove(id);
    }

    private void Finish(string id, BackgroundAgentStatus status, string report)
    {
        BackgroundAgentJob? job;
        lock (_lock)
        {
            if (_jobs.GetValueOrDefault(id) is not { Status: BackgroundAgentStatus.Running } current) return;
            job = current with { Status = status, FinishedAt = DateTimeOffset.Now, Report = report };
            _jobs[id] = job;
            if (_cancellers.Remove(id, out var cts)) cts.Dispose();
            if (status != BackgroundAgentStatus.Stopped) _unreported.Add(id);
        }
        Changed?.Invoke(job);
    }

    /// <summary>The automatic message the main agent gets when jobs finish.</summary>
    public static string Notice(IReadOnlyList<BackgroundAgentJob> finished)
    {
        var lines = new List<string>
        {
            $"[Automatic message: {(finished.Count == 1 ? "a background agent" : $"{finished.Count} background agents")} finished. Not from the user.]",
        };
        foreach (var job in finished)
        {
            var report = job.Report ?? "";
            var clipped = report.Length > 6_000 ? report[..6_000] + "\n[… report truncated]" : report;
            lines.Add($"\n## {job.Id} “{job.Description}” — {job.StatusWord} after {job.Elapsed.FormattedDuration()}\n{clipped}");
        }
        return string.Join("\n", lines);
    }
}

// MARK: - Tools

public sealed class AgentStatusTool : IToolExecutor
{
    public const string ToolName = "agent_status";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Check on background subagents started with agent(run_in_background: true). Without an id, lists them all. With an id, returns that agent's full report when it has finished; pass wait_seconds to wait for it (up to 600) instead of polling.",
        """{"type":"object","properties":{"id":{"type":"string","description":"A background agent id, e.g. bg-1"},"wait_seconds":{"type":"integer","description":"Wait up to this many seconds for it to finish (max 600)"}}}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundAgents is not { } pool) return "Error: background agents aren't available here.";
        var args = JsonArgs.Object(arguments);
        var id = (JsonArgs.String(args, "id") ?? "").Trim();
        if (id.Length == 0)
        {
            var jobs = pool.All;
            if (jobs.Count == 0) return "No background agents have been started in this chat.";
            var lines = jobs.Select(j =>
            {
                var line = $"- {j.Id} “{j.Description}”: {j.StatusWord}, {j.Elapsed.FormattedDuration()}";
                if (j.Status != BackgroundAgentStatus.Running && j.Report is { } r)
                    line += " — " + TextUtil.Prefix(r.Split('\n')[0], 120);
                return line;
            });
            return "Background agents:\n" + string.Join("\n", lines)
                   + "\nPass an id for a full report (and wait_seconds to wait for a running one).";
        }
        var wait = Math.Clamp(JsonArgs.Int(args, "wait_seconds", 0), 0, 600);
        if (await pool.WaitAsync(id, TimeSpan.FromSeconds(wait), cancellationToken).ConfigureAwait(false) is not { } job)
            return $"Error: no background agent {id}. agent_status without an id lists them.";
        if (job.Status == BackgroundAgentStatus.Running)
            return $"{job.Id} “{job.Description}” is still running ({job.Elapsed.FormattedDuration()}). Carry on with other work, or wait with wait_seconds.";
        pool.MarkReported(job.Id);
        return $"{job.Id} “{job.Description}” {job.StatusWord} after {job.Elapsed.FormattedDuration()}.\n\n{job.Report ?? "(no report)"}";
    }
}

public sealed class AgentStopTool : IToolExecutor
{
    public const string ToolName = "agent_stop";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Stop a running background subagent (e.g. it's no longer needed, or it's going the wrong way).",
        """{"type":"object","properties":{"id":{"type":"string","description":"The background agent id, e.g. bg-2"}},"required":["id"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.BackgroundAgents is not { } pool)
            return Task.FromResult<ToolResult>("Error: background agents aren't available here.");
        var id = (JsonArgs.String(arguments, "id") ?? "").Trim();
        return Task.FromResult<ToolResult>(pool.Stop(id) ? $"Stopped {id}." : $"Error: {id} isn't a running background agent.");
    }
}

/// <summary>Adds work to the app's task queue — background tasks that run unattended, one at a time,
/// each in its own chat, after (or alongside) this one.</summary>
public sealed class QueueAddTool(Func<string, string, bool, bool, Task<string>> add) : IToolExecutor
{
    public const string ToolName = "queue_task";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Add a background task to the user's task queue. Queued tasks run unattended, one at a time, each in its own chat, until the model declares them complete — use it for follow-up work that can happen after (or independently of) this conversation, e.g. 'write tests for the parser'. Give full instructions: the task's chat won't see this conversation. Set start to true to start the queue if it isn't running.",
        """{"type":"object","properties":{"title":{"type":"string","description":"Short title"},"details":{"type":"string","description":"Complete, self-contained instructions"},"front":{"type":"boolean","description":"Put it at the front of the queue"},"start":{"type":"boolean","description":"Start the queue if it isn't running"}},"required":["title","details"]}""");

    /// <summary>(title, details, front, start) → a message for the model.</summary>
    public Func<string, string, bool, bool, Task<string>> Add { get; } = add;

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var title = (JsonArgs.String(args, "title") ?? "").Trim();
        var details = (JsonArgs.String(args, "details") ?? "").Trim();
        if (title.Length == 0) return "Error: title is required.";
        return await Add(title, details, JsonArgs.Bool(args, "front", false), JsonArgs.Bool(args, "start", false))
            .ConfigureAwait(false);
    }
}
