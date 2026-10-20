namespace Vision.Core.Tests;

/// <summary>Item 21 at the far end: the primary picks a server (waited for, or a fallback it is told about), unknown names
/// stop before anything runs, results carry how busy the servers are, a chat's own model and the same route serving another
/// chat share one count, and Claude Code works a task as one `claude -p` job whose report comes back like any subagent's.</summary>
public sealed class SubagentPinTests : IDisposable
{
    private readonly TempDirectory _root = new("vision-pins");

    public void Dispose() => _root.Dispose();

    private static readonly ClaudeCodeLaunch Launch = ClaudeCodeLaunch.Direct("/usr/local/bin/claude");

    private static FleetServer Server(string id, ILlmClient client, string? handle = null, int max = 2, bool isPrivate = true,
                                      Func<Task<FleetTarget>>? resolve = null) => new()
    {
        Id = id,
        Label = id,
        Handle = handle,
        MaxParallel = max,
        IsPrivate = isPrivate,
        Resolve = _ => resolve is null ? Task.FromResult(new FleetTarget(client, "model-" + id, 8_000)) : resolve(),
    };

    private ToolContext Context(AgentFleet fleet, AgentRoster? roster = null, SubagentPool? pool = null, FleetServer? home = null,
                                PermissionGate? gate = null, PermissionPreset preset = PermissionPreset.WorkspaceWrite) => new()
    {
        Workspace = _root.Path,
        Policy = new PermissionPolicy(preset, _root.Path),
        Client = new ScriptedClient(),
        Registry = ToolRegistry.Standard(0),
        Model = "parent-model",
        Fleet = fleet,
        FleetHome = home,
        FleetPool = pool,
        Roster = roster,
        RequestPermission = gate ?? ((_, _, _) => Task.FromResult(true)),
        Retry = RetryPolicy.Standard with { Delay = _ => TimeSpan.Zero },
    };

    // MARK: The fleet

    [Fact]
    public async Task ABusyPinThatFreesInsideTheWaitRunsTheTaskAsPicked()
    {
        var fleet = new AgentFleet { PinWait = TimeSpan.FromSeconds(10) };
        fleet.Configure([Server("a", FleetTestSupport.Reports("a"), max: 1), Server("b", FleetTestSupport.Reports("b"))]);
        var held = await fleet.AcquireAsync(new FleetRequest { Pinned = "a" }, default);
        Assert.Equal(PlacementReason.Picked, held.Reason);
        var waiting = fleet.AcquireAsync(new FleetRequest { Pinned = "a" }, default);
        await Task.Delay(60);
        Assert.False(waiting.IsCompleted); // b is free, but the primary picked a
        held.Dispose();
        var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("a", PlacementReason.Picked, (string?)null), (lease.Label, lease.Reason, lease.Note));
    }

    [Fact]
    public async Task APinThatStaysBusyFallsBackWithTheReason()
    {
        var fleet = new AgentFleet { PinWait = TimeSpan.FromMilliseconds(200) };
        fleet.Configure([Server("a", FleetTestSupport.Reports("a"), handle: "spark-a", max: 1), Server("b", FleetTestSupport.Reports("b"))]);
        using var held = await fleet.AcquireAsync(new FleetRequest { Pinned = "a" }, default);
        var lease = await fleet.AcquireAsync(new FleetRequest { Pinned = "a" }, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(("b", PlacementReason.Fallback), (lease.Label, lease.Reason));
        Assert.Equal("spark-a stayed busy for 0 seconds", lease.Note);
    }

    [Fact]
    public async Task CancellingDuringAPinWaitLeaksNoSlot()
    {
        var fleet = new AgentFleet { PinWait = TimeSpan.FromSeconds(30) };
        fleet.Configure([Server("a", FleetTestSupport.Reports("a"), max: 1), Server("b", FleetTestSupport.Reports("b"))]);
        var held = await fleet.AcquireAsync(new FleetRequest { Pinned = "a" }, default);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fleet.AcquireAsync(new FleetRequest { Pinned = "a" }, stop.Token));
        Assert.Equal(1, fleet.Snapshot().Single(s => s.Id == "a").InFlight);
        Assert.Equal(0, fleet.Snapshot().Single(s => s.Id == "b").InFlight);
        held.Dispose();
        Assert.All(fleet.Snapshot(), s => Assert.Equal(0, s.InFlight));
    }

    [Fact]
    public async Task OneRouteAsAChatsOwnModelAndAsAnotherChatsWorkerSharesOneCount()
    {
        var fleet = new AgentFleet();
        fleet.Configure([Server("x", FleetTestSupport.Reports("x"), handle: "spark-x", max: 2)]);
        // Chat A runs on x: it is that chat's primary. Chat B (another model) uses x as a subagent server.
        var asHome = await fleet.AcquireAsync(new FleetRequest { Home = Server("x", FleetTestSupport.Reports("x"), max: 4) }, default);
        var asWorker = await fleet.AcquireAsync(new FleetRequest(), default);
        Assert.Equal(("x", "x"), (asHome.Label, asWorker.Label));
        Assert.Equal(2, fleet.Snapshot().Single(s => s.Id == "x").InFlight);
        asHome.Dispose();
        asWorker.Dispose();
    }

    [Fact]
    public async Task AChatsOwnModelIsListedWhileItsSubagentsRunOnIt()
    {
        var fleet = new AgentFleet();
        fleet.Configure([Server("w", FleetTestSupport.Reports("w"), max: 1)]);
        var home = Server("Spark B|q", FleetTestSupport.Reports("home"), max: 4);
        using var worker = await fleet.AcquireAsync(new FleetRequest { Home = home }, default);
        var overflow = await fleet.AcquireAsync(new FleetRequest { Home = home }, default);
        Assert.Equal(("Spark B|q", "primary"), (overflow.Label, overflow.Handle));
        var listed = fleet.Snapshot().Single(s => s.IsHome);
        Assert.Equal(("Spark B|q", 1, 4, "home"), (listed.Id, listed.InFlight, listed.MaxParallel, listed.Role));
        overflow.Dispose();
        Assert.DoesNotContain(fleet.Snapshot(), s => s.IsHome);
    }

    [Fact]
    public async Task AUsageLimitKeepsTasksOffUntilItResets()
    {
        var clock = new ManualClock();
        var fleet = new AgentFleet(clock);
        fleet.Configure([Server("cc", FleetTestSupport.Reports("cc")), Server("spark", FleetTestSupport.Reports("spark"))]);
        var reset = clock.GetUtcNow().AddMinutes(37);
        var lease = await fleet.AcquireAsync(new FleetRequest { Pinned = "cc" }, default);
        lease.Fail(new SubagentServerPausedException("You've reached your Claude plan's 5-hour limit.", reset));
        lease.Dispose();
        Assert.Equal(reset, fleet.Snapshot().Single(s => s.Id == "cc").UnhealthyUntil);
        Assert.True(AgentFleet.IsServerFault(new SubagentServerPausedException("limit", null)));

        // No reset time known: 15 minutes, not the usual minute.
        clock.AdvanceSeconds(38 * 60);
        var again = await fleet.AcquireAsync(new FleetRequest { Pinned = "cc" }, default);
        again.Fail(new SubagentServerPausedException("limit", null));
        again.Dispose();
        Assert.Equal(clock.GetUtcNow() + AgentFleet.UsageLimitPause, fleet.Snapshot().Single(s => s.Id == "cc").UnhealthyUntil);
        Assert.True(AgentFleet.IsServerFault(new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.NotSignedIn, "sign in"))));
        Assert.False(AgentFleet.IsServerFault(new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.Failed, "the task failed"))));
    }

    // MARK: What can be enlisted

    [Fact]
    public void GuardiansModelsTickedInTheMapAreSubagentServers()
    {
        GuardianModel[] models = [new("claude-haiku-5-5"), new("claude-sonnet-5-5"), new("claude-opus-5-5")];
        var map = new Dictionary<string, int> { ["claude-haiku-5-5"] = 0, ["claude-sonnet-5-5"] = 3 };
        var haiku = GuardianRoutes.LiveRoute("Guardian|claude-haiku-5-5", "https://guardian.test", models, null, "tok", subagents: map)!;
        Assert.Equal((true, (int?)null, "tok"), (haiku.SubagentWorker, haiku.SubagentParallel, haiku.ApiKey));
        Assert.Equal(3, GuardianRoutes.LiveRoute("Guardian|claude-sonnet-5-5", "https://guardian.test", models, null, "tok", subagents: map)!.SubagentParallel);
        Assert.False(GuardianRoutes.LiveRoute("Guardian|claude-opus-5-5", "https://guardian.test", models, null, "tok", subagents: map)!.SubagentWorker);

        // Guardian is its own machine per model, private for sensitive tasks, billed to the organisation.
        var facts = SubagentRouteFacts.From(haiku, enlisted: true);
        Assert.Equal((SubagentKind.Guardian, "route:Guardian|claude-haiku-5-5"), (facts.Kind, SubagentPool.Machine(facts)));
        var entry = SubagentPool.EntryFor(facts, "guardian-claude-haiku-5-5", primary: false);
        Assert.Equal((true, SubagentTier.Org, SubagentSpeed.Fast, "Guardian"), (entry.IsPrivate, entry.Tier, entry.Speed, entry.Provider));
        Assert.True(SensitivityPolicy.IsPrivate(haiku));
    }

    [Fact]
    public void ClaudeCodeIsAPerRouteSubscriptionThatIsNeverPrivate()
    {
        var sonnet = ClaudeCodeRoutes.Route("sonnet");
        var opus = ClaudeCodeRoutes.Route("opus");
        var facts = SubagentRouteFacts.From(sonnet, enlisted: true, contextWindow: 200_000);
        Assert.Equal(SubagentKind.ClaudeCode, facts.Kind);
        Assert.NotEqual(SubagentPool.Machine(facts), SubagentPool.Machine(SubagentRouteFacts.From(opus, enlisted: true)));
        var entry = SubagentPool.EntryFor(facts, "claude-code-sonnet", primary: false);
        Assert.Equal((false, SubagentTier.Subscription, true, false, "200K"), (entry.IsPrivate, entry.Tier, entry.OwnAgent, entry.Images, entry.Context));
        Assert.False(SensitivityPolicy.IsPrivate(sonnet));
        // A Sensitive chat's pool leaves it out; a chat on it has no pool (it runs Claude Code's own agent).
        var spark = new SubagentRouteFacts { Name = "Spark", Model = "q", BaseUrl = "http://spark:8000/v1", Private = true, PrivateAddress = true, SelfHosted = true, Enlisted = false };
        var sensitive = SubagentPool.Build([spark, facts], spark, true, sensitive: true, shieldOn: true, unattended: false, overflow: true);
        Assert.Equal(SubagentPool.Reason.Sensitive, sensitive.Dropped.Single().Reason);
        Assert.True(SubagentPool.Build([spark, facts], facts, true, false, true, false, true).ChatOwnAgent);
        // Its plan's provider and subscription come from the plan catalogue for hosted plans.
        var kimi = SubagentRouteFacts.From(new ProviderProfile(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.com/coding/v1", "kimi-for-coding"), enlisted: true);
        Assert.Equal(("Kimi", (bool?)true), (kimi.PlanProvider, kimi.PlanSubscription));
    }

    // MARK: The tools

    [Fact]
    public async Task AnUnknownServerIsAnErrorBeforeAnythingRuns()
    {
        var worker = FleetTestSupport.Reports("report");
        var fleet = new AgentFleet();
        fleet.Configure([Server("Spark 2|q", worker, handle: "spark-2")]);
        var roster = new AgentRoster();
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p","server":"kimi"}""", Context(fleet, roster), default);
        Assert.Equal("Error: “kimi” isn't one of this chat's subagent servers. Use one of: spark-2 — or leave server out.", result.Output);
        Assert.Empty(roster.All);
        Assert.Empty(worker.Requests);

        // delegate checks every task first: a bad server in task 3 means task 1 never runs.
        var batch = await new DelegateTool().ExecuteAsync("""
            {"tasks":[{"description":"a","prompt":"x"},{"description":"b","prompt":"y","server":"spark-2"},{"description":"c","prompt":"z","server":"nope"}]}
            """, Context(fleet, roster), default);
        Assert.Equal("Error: task 3: “nope” isn't one of this chat's subagent servers. Use one of: spark-2 — or leave server out.", batch.Output);
        Assert.Empty(roster.All);
        Assert.Empty(worker.Requests);
    }

    [Fact]
    public async Task APickThatStaysBusyRunsElsewhereAndTheMainAgentIsToldWhyAndHowBusy()
    {
        var spark = FleetTestSupport.Reports("from spark-2");
        var bedrock = FleetTestSupport.Reports("from bedrock");
        var fleet = new AgentFleet { PinWait = TimeSpan.FromSeconds(1) };
        fleet.Configure([Server("Spark 2|q", spark, handle: "spark-2", max: 1), Server("Bedrock|c", bedrock, handle: "bedrock")]);
        var home = new FleetServer
        {
            Id = "Spark 1|q", Label = "Spark 1", IsPrimary = true, IsHome = true, Handle = "primary", MaxParallel = 4,
            Resolve = _ => Task.FromResult(new FleetTarget(FleetTestSupport.Reports("from home"), "qwen", 8_000)),
        };
        SubagentPool.Entry E(string id, string route, int parallel, bool primary = false) => new()
        {
            Id = id, RouteId = route, Label = route.Split('|')[0], Provider = "OpenAI-compatible server", Model = "m", Machine = "route:" + route,
            IsPrivate = true, Parallel = parallel, IsPrimary = primary,
        };
        var pool = new SubagentPool([E("spark-2", "Spark 2|q", 1), E("bedrock", "Bedrock|c", 2), E("primary", "Spark 1|q", 4, primary: true)]);
        using var held = await fleet.AcquireAsync(new FleetRequest { Pinned = "Spark 2|q", Home = home, Pool = pool.WorkerRouteIds }, default);
        var roster = new AgentRoster();

        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p","server":"spark-2"}""", Context(fleet, roster, pool, home), default);

        Assert.Contains("from bedrock", result.Output);
        Assert.Empty(spark.Requests);
        var lines = result.Output.Split('\n');
        Assert.StartsWith("(on bedrock · ", lines[1]); // the handle, so the main agent can name it next time
        Assert.Equal("Ran on bedrock instead of spark-2: spark-2 stayed busy for 1 second.", lines[2]);
        Assert.EndsWith("\n\nSubagent servers now: spark-2 1/1 busy · bedrock 0/2 busy · primary 0/4 busy.", result.Output);
        var run = roster.All.Single();
        Assert.Equal(("fallback", "spark-2 stayed busy for 1 second", "fallback"), (run.PlacementReason, run.Placement, run.PlacementReasonLabel));
        Assert.Contains(run.Log, l => l.Text == "waiting for spark-2, which the primary picked (up to 1 second)");
        Assert.Contains(run.Log, l => l.Text == "Running on model-Bedrock|c · Bedrock|c — fallback — spark-2 stayed busy for 1 second");
        Assert.Equal("Running · fallback — spark-2 stayed busy for 1 second", SubagentPlacement.StatusLine(run.PlacementReason, run.Placement));

        // A free pick runs there, "picked by the primary"; agent_status lists the servers' load too.
        held.Dispose();
        var picked = await new AgentTool().ExecuteAsync("""{"description":"again","prompt":"p","server":"SPARK-2"}""", Context(fleet, roster, pool, home), default);
        Assert.Contains("from spark-2", picked.Output);
        Assert.Equal("picked", roster.All.Last().PlacementReason);
        Assert.DoesNotContain("Ran on", picked.Output);
        var status = await new AgentStatusTool().ExecuteAsync("{}", Context(fleet, roster, pool, home) with { BackgroundAgents = new BackgroundAgents() }, default);
        Assert.EndsWith("\n\nSubagent servers now: spark-2 0/1 busy · bedrock 0/2 busy · primary 0/4 busy.", status.Output);
    }

    [Fact]
    public async Task WithoutAFleetASensitiveTaskRunsOnlyIfTheChatsModelIsPrivate()
    {
        var hosted = new FakeProviderClient(new ProviderProfile(ProviderKind.OpenAICompat, "Kimi", "https://api.kimi.com/coding/v1", "kimi-for-coding"),
            (_, _) => throw new InvalidOperationException("a sensitive task must not reach a model that isn't private"));
        var context = Context(new AgentFleet()) with { Fleet = null, Client = hosted };
        var refused = await new AgentTool().ExecuteAsync("""{"description":"invoice","prompt":"bill Ann Lee","sensitive":true}""", context, default);
        Assert.Contains(NoPrivateServerException.Text, refused.Output);
        Assert.Empty(hosted.Requests);
        // Guardian and Bedrock in your account count as private (the Sensitive policy's own rule).
        var bedrock = new FakeProviderClient(new ProviderProfile(ProviderKind.Bedrock, "Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com", "claude"),
            (_, _) => new FakeStream([new LlmStreamEvent.Text("from bedrock"), new LlmStreamEvent.Done([], "stop", null)]));
        var allowed = await new AgentTool().ExecuteAsync("""{"description":"invoice","prompt":"bill Ann Lee","sensitive":true}""", context with { Client = bedrock }, default);
        Assert.Contains("from bedrock", allowed.Output);
    }

    // MARK: Claude Code as a subagent server

    private FleetServer ClaudeCodeServer(ScriptedClaudeProcess process, string id = "Claude Code|sonnet") => new()
    {
        Id = id,
        Label = "Claude Code",
        Handle = "claude-code",
        IsPrivate = false,
        Resolve = _ => Task.FromResult(new FleetTarget(new ScriptedClient(), "sonnet", 200_000)
        {
            Vision = false, OwnSettings = true,
            External = new ClaudeCodeSubagent(Launch, "sonnet") { Starter = ScriptedClaudeProcess.Starter(process) },
        }),
    };

    [Fact]
    public async Task AClaudeCodeTaskIsOneJobInTheChatsFolderWithItsReportBack()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("read-edit.jsonl"));
        var fleet = new AgentFleet();
        fleet.Configure([ClaudeCodeServer(process)]);
        var roster = new AgentRoster();

        // A read-only agent type: Plan only, whatever the chat's permissions.
        var result = await new AgentTool().ExecuteAsync("""{"description":"look","prompt":"read hello.txt","agent_type":"explore"}""", Context(fleet, roster), default);

        Assert.Contains("Edited hello.txt.", result.Output);
        Assert.Contains("Claude Code's tool calls: read_file ", result.Output);
        Assert.Equal(_root.Path, process.WorkingDirectory);
        var args = process.Args.ToList();
        Assert.Equal("-p", args[0]);
        Assert.Equal("plan", args[args.IndexOf("--permission-mode") + 1]);
        Assert.Equal("40", args[args.IndexOf("--max-turns") + 1]); // explore's step budget
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--resume", args);
        Assert.DoesNotContain("--session-id", args);
        Assert.StartsWith(ClaudeCodeSubagent.Note, args[args.IndexOf("--append-system-prompt") + 1]);
        var run = roster.All.Single();
        Assert.Equal(AgentRunStatus.Done, run.Status);
        Assert.Equal(2, run.Steps); // read, edit
        Assert.Equal((16_500L, 126L), (run.PromptTokens, run.CompletionTokens));
        Assert.Contains(run.Log, l => l.Text == "≈ under $0.01 at API prices (paid by your Claude plan)");
        Assert.Equal(1, fleet.Snapshot().Single().Completed);
    }

    [Fact]
    public async Task AClaudeCodeJobsQuestionsAreAskedOnTheChatsOwnCard()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("read-edit.jsonl"));
        var fleet = new AgentFleet();
        fleet.Configure([ClaudeCodeServer(process)]);
        var asked = new List<(string Name, string Detail)>();
        PermissionGate gate = (_, name, detail) =>
        {
            lock (asked) asked.Add((name, detail));
            return Task.FromResult(false);
        };
        // The capture edits /work/proj2/hello.txt — outside this chat's folder, so the chat's permissions ask.
        await new AgentTool().ExecuteAsync("""{"description":"edit","prompt":"edit hello.txt"}""", Context(fleet, gate: gate), default);
        var question = Assert.Single(asked);
        Assert.Equal("edit", question.Name);
        Assert.Contains("hello.txt", question.Detail);
        var answer = process.Written.Single(w => w.Contains("control_response", StringComparison.Ordinal));
        Assert.Contains("\"deny\"", answer);
    }

    [Fact]
    public async Task StoppingAClaudeCodeJobStopsTheCli()
    {
        var lines = ClaudeFixtures.Lines("interrupt.jsonl");
        var cut = lines.ToList().FindIndex(l => l.Contains("content_block_delta", StringComparison.Ordinal)) + 1;
        bool Interrupted(IReadOnlyList<string> w) => w.Any(x => x.Contains("\"interrupt\"", StringComparison.Ordinal));
        var process = new ScriptedClaudeProcess(lines.Select((l, i) => new ScriptedClaudeProcess.Step(l, i == cut ? Interrupted : null)));
        var subagent = new ClaudeCodeSubagent(Launch, "sonnet") { Starter = ScriptedClaudeProcess.Starter(process) };
        using var stop = new CancellationTokenSource();
        var job = subagent.RunAsync(new ExternalSubagentJob { Prompt = "work", SystemNote = ClaudeCodeSubagent.Note, Workspace = _root.Path }, stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (process.Written.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => job);
        Assert.Contains(process.Written, w => w.Contains("\"interrupt\"", StringComparison.Ordinal));
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task ClaudeCodeSignedOutHandsThePickedTaskToTheNextServerAndSaysSo()
    {
        var process = ScriptedClaudeProcess.Replay(ClaudeFixtures.Lines("not-signed-in.jsonl"));
        var fleet = new AgentFleet();
        fleet.Configure([ClaudeCodeServer(process), Server("Spark|q", FleetTestSupport.Reports("from spark"), handle: "spark")]);
        var roster = new AgentRoster();
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p","server":"claude-code"}""", Context(fleet, roster), default);
        Assert.Contains("from spark", result.Output);
        Assert.Contains("(on spark · after 1 server failure · ", result.Output);
        Assert.Contains("Ran on spark instead of claude-code: claude-code failed (Claude Code isn't signed in on this computer.", result.Output);
        Assert.False(fleet.Snapshot().Single(s => s.Id == "Claude Code|sonnet").Healthy);
        Assert.Equal("fallback", roster.All.Single().PlacementReason);
    }

    [Fact]
    public void AJobWithoutSessionPersistenceSupportGetsAFreshSessionInstead()
    {
        var job = new ExternalSubagentJob { Prompt = "p", SystemNote = ClaudeCodeSubagent.Note, Workspace = _root.Path, MaxSteps = 12 };
        var modern = new ClaudeCodeSubagent(Launch, "haiku", ThinkingLevel.High);
        var args = ClaudeCodeCommand.Args(modern.Options(job, projectSettings: false)).ToList();
        Assert.Equal(["--max-turns", "12"], args.Skip(args.IndexOf("--max-turns")).Take(2));
        Assert.Equal("high", args[args.IndexOf("--effort") + 1]);
        Assert.Contains("--no-session-persistence", args);
        Assert.DoesNotContain("--session-id", args);
        var old = new ClaudeCodeSubagent(Launch, "haiku") { Unsupported = new HashSet<string> { "--no-session-persistence" } };
        var oldArgs = ClaudeCodeCommand.Args(old.Options(job, projectSettings: false)).ToList();
        Assert.DoesNotContain("--no-session-persistence", oldArgs);
        Assert.True(Guid.TryParse(oldArgs[oldArgs.IndexOf("--session-id") + 1], out _));
    }
}
