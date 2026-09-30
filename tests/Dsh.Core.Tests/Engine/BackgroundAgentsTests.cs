using System.Runtime.CompilerServices;

namespace Dsh.Core.Tests;

/// <summary>Answers main-agent and subagent requests from separate scripts (a subagent's system
/// prompt starts "You are a focused subagent").</summary>
public sealed class RoleScriptedClient(Func<string, string> sub, params Turn[] main) : ILlmClient
{
    private readonly Lock _lock = new();
    private readonly Queue<Turn> _main = new(main);
    private readonly List<LlmRequest> _mainRequests = [];

    public IReadOnlyList<LlmRequest> MainRequests
    {
        get
        {
            lock (_lock) return [.. _mainRequests];
        }
    }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Turn turn;
        lock (_lock)
        {
            if (request.SystemPrompt.StartsWith("You are a focused subagent", StringComparison.Ordinal))
            {
                var task = request.Messages.FirstOrDefault(m => m.Role == MessageRole.User)?.Content ?? "";
                turn = new Turn(sub(task));
            }
            else
            {
                _mainRequests.Add(request);
                turn = _main.Count > 0 ? _main.Dequeue() : new Turn("done");
            }
        }
        await Task.Yield();
        foreach (var ch in turn.Text) yield return new LlmStreamEvent.Text(ch.ToString());
        yield return new LlmStreamEvent.Done(turn.Calls ?? [], "stop", null);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Ported from BackgroundAgentsTests.swift.</summary>
public sealed class BackgroundAgentsTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-bg");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task PoolLifecycle()
    {
        var pool = new BackgroundAgents(maxConcurrent: 2);
        var changes = new List<string>();
        pool.Changed += job =>
        {
            lock (changes) changes.Add($"{job.Id}:{job.StatusWord}");
        };
        var (fast, _) = pool.Launch("fast", _ => Task.FromResult((true, "fast report")));
        var (slow, _) = pool.Launch("slow", async ct =>
        {
            await Task.Delay(5_000, ct);
            return (true, "slow report");
        });
        Assert.NotNull(fast);
        Assert.NotNull(slow);
        var finished = await pool.WaitAsync(fast!.Id, TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundAgentStatus.Done, finished?.Status);
        Assert.Equal("fast report", finished?.Report);
        // Only two at once: a slot freed up when fast finished, then the pool is full again.
        var (third, _) = pool.Launch("third", async ct =>
        {
            await Task.Delay(5_000, ct);
            return (true, "");
        });
        Assert.NotNull(third);
        var (fourth, error) = pool.Launch("fourth", _ => Task.FromResult((true, "")));
        Assert.Null(fourth);
        Assert.Contains("already running", error);
        // Waiting on a running job times out quickly.
        var stillRunning = await pool.WaitAsync(slow!.Id, TimeSpan.FromMilliseconds(200));
        Assert.Equal(BackgroundAgentStatus.Running, stillRunning?.Status);
        Assert.True(pool.Stop(slow.Id));
        pool.Stop(third!.Id);
        Assert.Equal(BackgroundAgentStatus.Stopped, pool.Job(slow.Id)?.Status);
        Assert.False(pool.Stop(slow.Id));
        // Finished (not stopped) jobs are reported once.
        await Task.Delay(100);
        var unreported = pool.TakeUnreported().Select(j => j.Id).ToList();
        Assert.Contains(fast.Id, unreported);
        Assert.DoesNotContain(slow.Id, unreported);
        Assert.Empty(pool.TakeUnreported());
        lock (changes) Assert.Contains($"{fast.Id}:done", changes);
        Assert.Contains("fast report", BackgroundAgents.Notice([finished!]));
    }

    [Fact]
    public async Task AFailingOrThrowingJobIsReportedAsFailed()
    {
        var pool = new BackgroundAgents();
        var (a, _) = pool.Launch("a", _ => Task.FromResult((false, "the server said no")));
        var (b, _) = pool.Launch("b", _ => throw new InvalidOperationException("kaput"));
        Assert.Equal(BackgroundAgentStatus.Failed, (await pool.WaitAsync(a!.Id, TimeSpan.FromSeconds(5)))?.Status);
        var failed = await pool.WaitAsync(b!.Id, TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundAgentStatus.Failed, failed?.Status);
        Assert.Equal("kaput", failed?.Report);
    }

    [Fact]
    public async Task ReportsThatNeverReachedTheModelCanBeAnnouncedAgain()
    {
        var pool = new BackgroundAgents();
        var (done, _) = pool.Launch("done", _ => Task.FromResult((true, "report")));
        var (stopped, _) = pool.Launch("stopped", async ct =>
        {
            await Task.Delay(5_000, ct);
            return (true, "");
        });
        await pool.WaitAsync(done!.Id, TimeSpan.FromSeconds(5));
        pool.Stop(stopped!.Id);
        var taken = pool.TakeUnreported();
        Assert.Equal([done.Id], taken.Select(j => j.Id));
        Assert.False(pool.HasUnreported);

        // The request that carried them failed: they come back — but a stopped job is never announced, and nothing twice.
        pool.Requeue([.. taken, pool.Job(stopped.Id)!]);
        pool.Requeue(taken);
        Assert.True(pool.HasUnreported);
        Assert.Equal([done.Id], pool.TakeUnreported().Select(j => j.Id));
        Assert.False(pool.HasUnreported);
    }

    [Fact]
    public async Task AListenerThatThrowsCannotLeaveAJobStuckOrHideItsFinish()
    {
        var pool = new BackgroundAgents(maxConcurrent: 1);
        var heard = new List<string>();
        pool.Changed += _ => throw new InvalidOperationException("listener bug");
        pool.Changed += job =>
        {
            lock (heard) heard.Add($"{job.Id}:{job.StatusWord}");
        };

        var (job, _) = pool.Launch("first", _ => Task.FromResult((true, "report")));
        var finished = await pool.WaitAsync(job!.Id, TimeSpan.FromSeconds(5));

        Assert.Equal(BackgroundAgentStatus.Done, finished?.Status);
        await Task.Delay(50);
        lock (heard) Assert.Contains($"{job.Id}:done", heard);
        // The one slot was given back: another can start.
        var (second, error) = pool.Launch("second", _ => Task.FromResult((true, "again")));
        Assert.NotNull(second);
        Assert.Null(error);
    }

    private Engine MakeEngine(ILlmClient client, BackgroundAgents pool) =>
        new(client, new ToolRegistry([new AgentTool(), new EchoTool(), .. ToolRegistry.BackgroundAgentTools()]), "main",
            new EngineConfig("m") { MaxIterations = 8 }, _root.Path,
            new PermissionPolicy(PermissionPreset.FullAccess, _root.Path), Gates.Allow)
        {
            BackgroundAgents = pool,
        };

    [Fact]
    public async Task LaunchInBackgroundThenCollectTheReport()
    {
        var launch = new ToolCall("1", "agent", """{"description":"scan","prompt":"find TODOs","run_in_background":true}""");
        var status = new ToolCall("2", "agent_status", """{"id":"bg-1","wait_seconds":10}""");
        var client = new RoleScriptedClient(task => $"found 3 TODOs for: {task}",
            Turn.Calling(launch), Turn.Calling(status), new Turn("All collected."));
        var pool = new BackgroundAgents();
        var result = await MakeEngine(client, pool).RunAsync([], "go");
        Assert.Equal("All collected.", result.FinalText);
        var toolResults = result.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content ?? "").ToList();
        Assert.StartsWith("Started background agent bg-1", toolResults[0]);
        Assert.Contains("found 3 TODOs for: find TODOs", toolResults[1]);
        Assert.Equal(BackgroundAgentStatus.Done, pool.Job("bg-1")?.Status);
        // Read via agent_status: not announced again.
        Assert.Empty(pool.TakeUnreported());
    }

    [Fact]
    public async Task FinishedAgentsAreAnnouncedAutomatically()
    {
        var pool = new BackgroundAgents();
        pool.Launch("tests", _ => Task.FromResult((true, "12 tests pass")));
        await pool.WaitAsync("bg-1", TimeSpan.FromSeconds(5));
        var client = new RoleScriptedClient(_ => "", new Turn("noted"));
        await MakeEngine(client, pool).RunAsync([], "what's up?");
        var sent = client.MainRequests[0].Messages[^1].Content ?? "";
        Assert.StartsWith("what's up?", sent);
        Assert.Contains("[Automatic message: a background agent finished", sent);
        Assert.Contains("12 tests pass", sent);
    }

    [Fact]
    public async Task BackgroundNeedsAPoolAndSubagentsCantNest()
    {
        var context = new ToolContext
        {
            Workspace = _root.Path,
            Policy = new PermissionPolicy(PermissionPreset.FullAccess, _root.Path),
            Client = new ScriptedClient(),
            Registry = new ToolRegistry([]),
        };
        var output = (await new AgentTool().ExecuteAsync("""{"description":"x","prompt":"y","run_in_background":true}""",
            context, CancellationToken.None)).Output;
        Assert.StartsWith("Error: background subagents aren't available", output);
        Assert.StartsWith("Error", (await new AgentStatusTool().ExecuteAsync("{}", context, CancellationToken.None)).Output);
        var nested = (await new AgentTool().ExecuteAsync("""{"description":"x","prompt":"y"}""",
            context with { Depth = 1 }, CancellationToken.None)).Output;
        Assert.Contains("nested subagents", nested);
    }

    [Fact]
    public async Task QueueTaskToolPassesItsArguments()
    {
        var seen = new List<string>();
        var tool = new QueueAddTool((title, details, front, start) =>
        {
            seen.Add($"{title}|{details}|{front}|{start}");
            return Task.FromResult("queued");
        });
        var context = new ToolContext
        {
            Workspace = _root.Path,
            Policy = new PermissionPolicy(PermissionPreset.FullAccess, _root.Path),
            Client = new ScriptedClient(),
            Registry = new ToolRegistry([]),
        };
        var output = (await tool.ExecuteAsync("""{"title":" Write tests ","details":"for the parser","start":true}""", context, CancellationToken.None)).Output;
        Assert.Equal("queued", output);
        Assert.Equal(["Write tests|for the parser|False|True"], seen);
        Assert.StartsWith("Error", (await tool.ExecuteAsync("""{"details":"x"}""", context, CancellationToken.None)).Output);
    }

    [Fact]
    public void SubagentsDoNotGetTheAgentOrQueueTools()
    {
        var registry = new ToolRegistry([new AgentTool(), new EchoTool(), .. ToolRegistry.BackgroundAgentTools(),
                                         new QueueAddTool((_, _, _, _) => Task.FromResult(""))]);
        var sub = registry.Removing(AgentTool.ToolName, AgentStatusTool.ToolName, AgentStopTool.ToolName, QueueAddTool.ToolName);
        Assert.Equal(["echo"], sub.Names);
    }
}
