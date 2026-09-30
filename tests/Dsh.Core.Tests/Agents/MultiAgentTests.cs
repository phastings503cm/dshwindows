using System.Runtime.CompilerServices;

namespace Dsh.Core.Tests;

/// <summary>Answers only when <paramref name="parties"/> callers are inside it at once: proof of concurrency.</summary>
internal sealed class RendezvousClient(CountdownEvent meeting, string name) : ILlmClient
{
    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        meeting.Signal();
        var together = await Task.Run(() => meeting.Wait(TimeSpan.FromSeconds(4)), cancellationToken);
        yield return new LlmStreamEvent.Text($"{name}: {(together ? "together" : "alone")}");
        yield return new LlmStreamEvent.Done([], "stop", null);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

/// <summary>Records how many requests were in flight at once.</summary>
internal sealed class ConcurrencyClient : ILlmClient
{
    private int _active;
    public int Peak { get; private set; }

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var now = Interlocked.Increment(ref _active);
        lock (this) Peak = Math.Max(Peak, now);
        await Task.Delay(60, cancellationToken);
        Interlocked.Decrement(ref _active);
        yield return new LlmStreamEvent.Text("done");
        yield return new LlmStreamEvent.Done([], "stop", null);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

public sealed class MultiAgentTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-multiagent");

    public void Dispose() => _root.Dispose();

    private static readonly RetryPolicy Fast = RetryPolicy.Standard with { Delay = _ => TimeSpan.Zero };

    private ToolContext Context(ILlmClient client, AgentFleet? fleet = null, AgentRoster? roster = null, int depth = 0) => new()
    {
        Workspace = _root.Path,
        Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
        Client = client,
        Registry = ToolRegistry.Standard(depth),
        Model = "parent",
        Depth = depth,
        Fleet = fleet,
        Roster = roster,
        Retry = Fast,
    };

    // MARK: - Failover

    [Fact]
    public async Task ATaskMovesToAnotherServerWhenItsServerDies()
    {
        var fleet = new AgentFleet();
        var dead = new FlakyClient(() => LlmException.Connection("connection refused"));
        fleet.Configure([
            FleetTestSupport.Server("main", FleetTestSupport.Reports("report from main"), primary: true),
            FleetTestSupport.Server("spark-2", dead)]);
        var roster = new AgentRoster();

        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet, roster), default);

        Assert.Contains("report from main", result.Output);
        Assert.Contains("on main", result.Output);
        Assert.Contains("after 1 server failure", result.Output);
        Assert.Equal(3, dead.Requests.Count); // the first try plus two quick retries
        var status = fleet.Snapshot().Single(s => s.Id == "spark-2");
        Assert.Equal(1, status.Failed);
        Assert.False(status.Healthy);
        var run = Assert.Single(roster.All);
        Assert.Equal("main", run.Server);
        Assert.Equal(AgentRunStatus.Done, run.Status);

        // The dead server is left alone for the next task.
        var again = await new AgentTool().ExecuteAsync("""{"description":"scan2","prompt":"p"}""", Context(new ScriptedClient(), fleet, roster), default);
        Assert.Contains("on main", again.Output);
        Assert.DoesNotContain("server failure", again.Output);
        Assert.Equal(3, dead.Requests.Count);
    }

    [Fact]
    public async Task ARequestsOwnErrorDoesNotBounceBetweenServers()
    {
        var fleet = new AgentFleet();
        var picky = new FlakyClient(() => LlmException.Http(400, "bad key"));
        fleet.Configure([FleetTestSupport.Server("a", picky), FleetTestSupport.Server("b", FleetTestSupport.Reports("never"))]);
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);
        Assert.StartsWith("Subagent 'scan' failed: ", result.Output);
        Assert.Contains("bad key", result.Output);
        Assert.True(fleet.Snapshot().Single(s => s.Id == "a").Healthy);
    }

    [Fact]
    public async Task WhenNoServerWorksTheFailureIsReported()
    {
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("a", new FlakyClient(() => LlmException.Connection("down"))),
                         FleetTestSupport.Server("b", new FlakyClient(() => LlmException.Connection("down")))]);
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);
        Assert.StartsWith("Subagent 'scan' failed: ", result.Output);
    }

    [Fact]
    public async Task ALoneServerIsNotNamedInTheReport()
    {
        // With nothing to choose between, "on spark" and a timing line would be noise.
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("only", FleetTestSupport.Reports("the report"), primary: true)]);
        var roster = new AgentRoster();
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet, roster), default);
        Assert.Equal("Subagent 'scan' finished.\n\nthe report", result.Output);
        Assert.Null(Assert.Single(roster.All).Server);
    }

    [Fact]
    public async Task ASubagentUsesTheLeasedServersModelAndWindow()
    {
        var client = FleetTestSupport.Reports("ok");
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("spark", client)]);
        await new AgentTool().ExecuteAsync("""{"description":"d","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);
        Assert.Equal("model-spark", client.Requests[0].Model);
    }

    // MARK: - delegate

    [Fact]
    public async Task DelegateSpreadsTasksAcrossServers()
    {
        // Each server holds one task at a time and answers only once all three tasks are in flight — which can
        // only happen if the three tasks landed on three different servers.
        var meeting = new CountdownEvent(3);
        var fleet = new AgentFleet();
        fleet.Configure([
            FleetTestSupport.Server("main", new RendezvousClient(meeting, "main"), primary: true, max: 1),
            FleetTestSupport.Server("spark-2", new RendezvousClient(meeting, "spark-2"), max: 1),
            FleetTestSupport.Server("spark-3", new RendezvousClient(meeting, "spark-3"), max: 1)]);
        var roster = new AgentRoster();
        var arguments = Args.Json(new
        {
            tasks = new[]
            {
                new { description = "api layer", prompt = "audit the api", agent_type = "explore" },
                new { description = "tests", prompt = "audit the tests", agent_type = "explore" },
                new { description = "docs", prompt = "audit the docs", agent_type = "explore" },
            },
        });
        var result = await new DelegateTool().ExecuteAsync(arguments, Context(new ScriptedClient(), fleet, roster), default);

        Assert.StartsWith("Delegated 3 tasks: 3 finished, 0 failed.", result.Output);
        Assert.Contains("## 1. api layer — finished", result.Output);
        Assert.Contains("## 3. docs — finished", result.Output);
        Assert.DoesNotContain(": alone", result.Output);
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(result.Output, ": together").Count);
        var servers = roster.All.Select(r => r.Server).ToHashSet();
        Assert.Equal(new HashSet<string?> { "main", "spark-2", "spark-3" }, servers);
        Assert.All(roster.All, r => Assert.Equal("explore", r.AgentType));
    }

    [Fact]
    public async Task DelegateReportsFailuresNextToSuccesses()
    {
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("only", new ScriptedClient(new Turn("first report"), new Turn("second report")), max: 1)]);
        var context = Context(new ScriptedClient(), fleet);
        var ok = await new DelegateTool().ExecuteAsync("""{"tasks":[{"description":"a","prompt":"do a"},{"description":"b","prompt":"do b"}]}""", context, default);
        Assert.StartsWith("Delegated 2 tasks: 2 finished, 0 failed.", ok.Output);

        var failing = new AgentFleet();
        failing.Configure([FleetTestSupport.Server("bad", new FlakyClient(() => LlmException.Http(401, "no key")), max: 2)]);
        var bad = await new DelegateTool().ExecuteAsync("""{"tasks":[{"description":"a","prompt":"do a"}]}""", Context(new ScriptedClient(), failing), default);
        Assert.StartsWith("Delegated 1 task: 0 finished, 1 failed.", bad.Output);
        Assert.Contains("no key", bad.Output);
    }

    [Fact]
    public async Task DelegateRunsSeriallyWhenAskedTo()
    {
        var client = new ConcurrencyClient();
        var arguments = """{"tasks":[{"description":"a","prompt":"1"},{"description":"b","prompt":"2"},{"description":"c","prompt":"3"}],"max_parallel":1}""";
        await new DelegateTool().ExecuteAsync(arguments, Context(client), default);
        Assert.Equal(1, client.Peak);

        var wide = new ConcurrencyClient();
        await new DelegateTool().ExecuteAsync(arguments.Replace("\"max_parallel\":1", "\"max_parallel\":3"), Context(wide), default);
        Assert.Equal(3, wide.Peak);
    }

    [Theory]
    [InlineData("""{}""", "Error: tasks must be a non-empty array")]
    [InlineData("""{"tasks":[]}""", "Error: tasks must be a non-empty array")]
    [InlineData("""{"tasks":["nope"]}""", "Error: task 1 is not an object.")]
    [InlineData("""{"tasks":[{"description":"a"}]}""", "Error: task 1 has no prompt.")]
    [InlineData("""{"tasks":[{"prompt":"p","agent_type":"wizard"}]}""", "Error: task 1 has an unknown agent_type 'wizard'")]
    public async Task DelegateChecksItsArguments(string arguments, string expected)
    {
        var client = new ScriptedClient();
        var result = await new DelegateTool().ExecuteAsync(arguments, Context(client), default);
        Assert.StartsWith(expected, result.Output);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task DelegateHasALimitAndNoNesting()
    {
        var many = Args.Json(new { tasks = Enumerable.Range(0, 13).Select(i => new { description = $"t{i}", prompt = "p" }) });
        Assert.StartsWith("Error: at most 12 tasks", (await new DelegateTool().ExecuteAsync(many, Context(new ScriptedClient()), default)).Output);
        Assert.Equal("Error: nested subagents are not allowed (depth limit).",
            (await new DelegateTool().ExecuteAsync("""{"tasks":[{"prompt":"p"}]}""", Context(new ScriptedClient(), depth: 1), default)).Output);
    }

    [Fact]
    public async Task DelegateAcceptsTasksSentAsAString()
    {
        var client = FleetTestSupport.Reports("ok");
        var arguments = Args.Json(new { tasks = """[{"description":"a","prompt":"p"}]""" });
        var result = await new DelegateTool().ExecuteAsync(arguments, Context(client), default);
        Assert.StartsWith("Delegated 1 task: 1 finished", result.Output);
    }

    [Fact]
    public void RegistryOffersDelegateOnlyAtTheTopLevel()
    {
        Assert.Contains(DelegateTool.ToolName, ToolRegistry.Standard(0).Names);
        Assert.DoesNotContain(DelegateTool.ToolName, ToolRegistry.Standard(1).Names);
    }

    // MARK: - Concurrent agent calls inside one engine turn

    [Fact]
    public async Task TwoAgentCallsInOneTurnRunOnTwoServersAtTheSameTime()
    {
        var meeting = new CountdownEvent(2);
        var fleet = new AgentFleet();
        fleet.Configure([
            FleetTestSupport.Server("spark-2", new RendezvousClient(meeting, "spark-2"), max: 1),
            FleetTestSupport.Server("spark-3", new RendezvousClient(meeting, "spark-3"), max: 1)]);
        var calls = new[]
        {
            new ToolCall("a1", "agent", Args.Json(new { description = "left", prompt = "look left" })),
            new ToolCall("a2", "agent", Args.Json(new { description = "right", prompt = "look right" })),
        };
        var parent = new ScriptedClient(Turn.Calling(calls), new Turn("all reported"));
        var roster = new AgentRoster();
        var engine = new Engine(parent, ToolRegistry.Standard(), "system", new EngineConfig("parent") { Retry = Fast },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow)
        {
            Fleet = fleet,
            Roster = roster,
        };
        var result = await engine.RunAsync([], "check both");

        var tools = result.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(["a1", "a2"], tools.Select(t => t.ToolCallId));
        Assert.All(tools, t => Assert.Contains(": together", t.Content));
        Assert.Contains(tools, t => t.Content!.Contains("spark-2: together"));
        Assert.Contains(tools, t => t.Content!.Contains("spark-3: together"));
        Assert.Equal("all reported", result.FinalText);
        Assert.Equal(2, roster.All.Count(r => r.Status == AgentRunStatus.Done));
    }

    [Fact]
    public async Task BackgroundAgentsUseTheFleetToo()
    {
        var fleet = new AgentFleet();
        fleet.Configure([
            FleetTestSupport.Server("main", FleetTestSupport.Reports("never asked"), primary: true),
            FleetTestSupport.Server("spark-2", FleetTestSupport.Reports("background report"), max: 1)]);
        var pool = new BackgroundAgents();
        var roster = new AgentRoster();
        var context = Context(new ScriptedClient(), fleet, roster) with { BackgroundAgents = pool };
        var started = await new AgentTool().ExecuteAsync("""{"description":"bg","prompt":"p","run_in_background":true}""", context, default);
        Assert.StartsWith("Started background agent bg-1", started.Output);
        var job = await pool.WaitAsync("bg-1", TimeSpan.FromSeconds(5));
        Assert.Equal(BackgroundAgentStatus.Done, job!.Status);
        Assert.Equal("background report", job.Report);
        var run = Assert.Single(roster.All);
        Assert.True(run.Background);
        Assert.Equal("spark-2", run.Server);
    }

    // MARK: - Review fixes

    [Fact]
    public async Task ALoneServerIsWaitedOutNotGivenUpOn()
    {
        // With nowhere else to go, a subagent waits for its server as long as the main agent would.
        var fleet = new AgentFleet();
        var flaky = new FlakyClient(() => LlmException.Connection("restarting"), failures: 6, then: FleetTestSupport.Reports("back again"));
        fleet.Configure([FleetTestSupport.Server("only", flaky, primary: true)]);

        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);

        Assert.StartsWith("Subagent 'scan' finished.", result.Output);
        Assert.Contains("back again", result.Output);
    }

    /// <summary>Counts how often it is asked, and always refuses with the same error.</summary>
    private sealed class AlwaysRefusesClient(Func<Exception> error) : ILlmClient
    {
        public int Asked;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Interlocked.Increment(ref Asked);
            throw error();
#pragma warning disable CS0162 // (an iterator needs a yield)
            yield break;
#pragma warning restore CS0162
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["m"]);
    }

    [Fact]
    public async Task ALoneServerThatRefusesIsAskedOnceNotOverAndOver()
    {
        // There is nowhere to fail over to, so "another attempt" would only be the same request again.
        var refuses = new AlwaysRefusesClient(() => LlmException.Http(401, "bad key"));
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("only", refuses, primary: true)]);

        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);

        Assert.Contains("failed", result.Output);
        Assert.Equal(1, refuses.Asked);
    }

    [Fact]
    public async Task AServerFaultWithSeveralServersStillFailsOver()
    {
        var refuses = new AlwaysRefusesClient(() => LlmException.Http(401, "bad key"));
        var healthy = FleetTestSupport.Reports("all done");
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("a", refuses), FleetTestSupport.Server("b", healthy)]);

        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);

        Assert.Contains("all done", result.Output);
        Assert.Equal(1, refuses.Asked);
    }

    /// <summary>Does one piece of work (an echo), then loses its connection.</summary>
    private sealed class WritesThenDiesClient : ILlmClient
    {
        private int _calls;

        public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Interlocked.Increment(ref _calls) == 1)
            {
                yield return new LlmStreamEvent.Done([new ToolCall("w1", "echo", """{"text":"x"}""")], "stop", null);
                yield break;
            }
            throw LlmException.Connection("the server went away");
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["m"]);
    }

    [Fact]
    public async Task AFailoverAfterChangesTellsTheNextAttemptToLookFirst()
    {
        var fleet = new AgentFleet();
        var healthy = FleetTestSupport.Reports("all done");
        fleet.Configure([FleetTestSupport.Server("a", new WritesThenDiesClient()), FleetTestSupport.Server("b", healthy)]);
        var context = Context(new ScriptedClient(), fleet) with { Registry = new ToolRegistry([new EchoTool()]) };

        var result = await new AgentTool().ExecuteAsync("""{"description":"migrate","prompt":"add the migration"}""", context, default);

        Assert.Contains("all done", result.Output);
        var first = healthy.Requests[0].Messages.First(m => m.Role == MessageRole.User).Content;
        Assert.StartsWith("[Note: an earlier attempt", first);
        Assert.Contains("add the migration", first);
    }

    [Fact]
    public async Task AFailoverBeforeAnyChangeReplaysTheTaskAsWritten()
    {
        var fleet = new AgentFleet();
        var healthy = FleetTestSupport.Reports("all done");
        fleet.Configure([FleetTestSupport.Server("a", new FlakyClient(() => LlmException.Connection("down"))), FleetTestSupport.Server("b", healthy)]);

        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"count the files"}""", Context(new ScriptedClient(), fleet), default);

        Assert.Equal("count the files", healthy.Requests[0].Messages.First(m => m.Role == MessageRole.User).Content);
    }

    [Fact]
    public async Task ASubagentUsesTheLeasedRoutesOwnSettings()
    {
        var client = FleetTestSupport.Reports("ok");
        var fleet = new AgentFleet();
        fleet.Configure([new FleetServer
        {
            Id = "x", Label = "x", MaxParallel = 1,
            Resolve = _ => Task.FromResult(new FleetTarget(client, "model-x", 8_000) { MaxOutputTokens = 777, Temperature = 0.3 }),
        }]);

        await new AgentTool().ExecuteAsync("""{"description":"d","prompt":"p"}""", Context(new ScriptedClient(), fleet), default);

        Assert.Equal(777, client.Requests[0].MaxTokens);
        Assert.Equal(0.3, client.Requests[0].Temperature);
    }

    [Fact]
    public async Task DisallowedToolsAreRemovedFromASubagent()
    {
        var careful = new AgentDefinition { Name = "careful", Description = "no echo", DisallowedTools = ["echo"] };
        var catalog = new AgentCatalog([.. AgentDefinition.BuiltIn, careful]);
        var client = FleetTestSupport.Reports("ok");
        var context = Context(client) with { Registry = new ToolRegistry([new EchoTool(), new ReadFileTool()]), AgentTypes = catalog };

        await new AgentTool(catalog).ExecuteAsync("""{"description":"d","prompt":"p","agent_type":"careful"}""", context, default);

        Assert.DoesNotContain(client.Requests[0].Tools, t => t.Name == "echo");
        Assert.Contains(client.Requests[0].Tools, t => t.Name == "read_file");
    }
}
