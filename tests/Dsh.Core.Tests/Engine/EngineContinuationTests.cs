namespace Dsh.Core.Tests;

/// <summary>A run never makes the user say "continue": it goes on past the step limit, continues replies
/// cut off by the output limit, ends on the model's own goal signal, and stops only when it is going in
/// circles.</summary>
public sealed class EngineContinuationTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-continue");

    public void Dispose() => _root.Dispose();

    private Engine MakeEngine(ILlmClient client, EngineConfig config, params IToolExecutor[] tools)
    {
        var registry = new ToolRegistry(tools.Length == 0 ? [new EchoTool()] : tools);
        return new Engine(client, registry, "system", config, _root.Path,
            new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow);
    }

    private static EngineConfig Config(int maxIterations = 3) =>
        new("test") { MaxIterations = maxIterations, ToolTimeout = TimeSpan.FromSeconds(5) };

    private static ToolCall Echo(int n) => new($"c{n}", "echo", $$"""{"text":"step {{n}}"}""");

    // MARK: - Step limit

    [Fact]
    public async Task WithoutContinueTheStepLimitEndsTheRun()
    {
        var turns = Enumerable.Range(1, 10).Select(n => Turn.Calling(Echo(n))).Append(new Turn("finished")).ToArray();
        var result = await MakeEngine(new ScriptedClient(turns), Config(3)).RunAsync([], "go");

        Assert.True(result.HitIterationLimit);
        Assert.NotEqual("finished", result.FinalText);
    }

    [Fact]
    public async Task ContinueAfterLimitKeepsWorkingUntilTheModelIsDone()
    {
        var turns = Enumerable.Range(1, 10).Select(n => Turn.Calling(Echo(n))).Append(new Turn("finished")).ToArray();
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(3) with { ContinueAfterLimit = true }).RunAsync([], "go");

        Assert.False(result.HitIterationLimit);
        Assert.Equal("finished", result.FinalText);
        Assert.Equal(11, client.Requests.Count);
        // Every call got its result: the transcript is a valid request.
        Assert.Equal(10, result.Messages.Count(m => m.Role == MessageRole.Tool));
    }

    [Fact]
    public async Task ACheckpointAllowsCompactionAgain()
    {
        // The compaction allowance is per segment, so a long run can compact again after a checkpoint. (Counted by the
        // compactions that actually shrank the transcript: with MaxCompactions = 1, a second one can only come after a reset.)
        var shrinks = 0;
        var turns = Enumerable.Range(1, 8).Select(n => Turn.Calling(Echo(n))).Append(new Turn("finished")).ToArray();
        var engine = new Engine(new ScriptedClient(turns), new ToolRegistry([new EchoTool()]), "system",
            Config(3) with { ContinueAfterLimit = true, ContextWindow = 100, MaxCompactions = 1 },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow)
        {
            Compactor = (used, messages, _) =>
            {
                IReadOnlyList<LlmMessage> smaller = [messages[0], .. messages.Skip(messages.Count - 2)];
                if (smaller.Count < messages.Count) shrinks++;
                return Task.FromResult(smaller);
            },
        };
        var result = await engine.RunAsync([], new string('x', 400));

        Assert.Equal("finished", result.FinalText);
        Assert.True(shrinks > 1, $"compacted {shrinks} time(s)");
    }

    [Fact]
    public async Task ASummariserThatKeepsFailingIsAskedAtMostTwiceAPerSegment()
    {
        // Each failed attempt costs a model call: it must not be paid on every step of a long run.
        var asked = 0;
        var turns = Enumerable.Range(1, 8).Select(n => Turn.Calling(Echo(n))).Append(new Turn("finished")).ToArray();
        var engine = new Engine(new ScriptedClient(turns), new ToolRegistry([new EchoTool()]), "system",
            Config(3) with { ContinueAfterLimit = true, ContextWindow = 100, MaxCompactions = 5 },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow)
        {
            Compactor = (used, messages, _) =>
            {
                asked++;
                return Task.FromResult<IReadOnlyList<LlmMessage>>(messages); // never any smaller
            },
        };
        var result = await engine.RunAsync([], new string('x', 400));

        Assert.Equal("finished", result.FinalText);
        Assert.InRange(asked, 2, 6); // twice in each of the three segments at most (9 steps of 3)
    }

    [Fact]
    public async Task WrapUpAsksForAReportWhenTheBudgetRunsOut()
    {
        // Two work turns use the whole budget; the third call is the wrap-up.
        var client = new ScriptedClient(Turn.Calling(Echo(1)), Turn.Calling(Echo(2)), new Turn("Report: touched three files."));
        var result = await MakeEngine(client, Config(2) with { WrapUpAtLimit = true }).RunAsync([], "go");

        Assert.True(result.HitIterationLimit);
        Assert.Equal("Report: touched three files.", result.FinalText);
        Assert.Equal("Report: touched three files.", result.LastReplyText);
        // Two work turns, then the wrap-up call, which sees the request for a report.
        Assert.Equal(3, client.Requests.Count);
        Assert.Contains(client.Requests[^1].Messages, m => m.Role == MessageRole.User && m.Content!.Contains("final report"));
        Assert.Equal(MessageRole.Assistant, result.Messages[^1].Role);
    }

    // MARK: - Truncated replies

    [Fact]
    public async Task AReplyCutOffByTheOutputLimitIsContinuedAutomatically()
    {
        var client = new ScriptedClient(new Turn("The first half, ") { Finish = "length" }, new Turn("and the second half."));
        var result = await MakeEngine(client, Config()).RunAsync([], "write it");

        Assert.Equal("and the second half.", result.FinalText);
        var second = client.Requests[1].Messages;
        Assert.Equal("The first half, ", second.Last(m => m.Role == MessageRole.Assistant).Content);
        Assert.Contains("cut off by the output-token limit", second[^1].Content);
        Assert.Equal(MessageRole.User, second[^1].Role);
    }

    [Fact]
    public async Task ContinuingATruncatedReplyIsBounded()
    {
        var turns = Enumerable.Range(0, 30).Select(_ => new Turn("more ") { Finish = "length" }).ToArray();
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config() with { MaxTruncationContinuations = 2 }).RunAsync([], "go");

        Assert.Equal(3, client.Requests.Count); // the first reply + two continuations
        Assert.False(result.HitIterationLimit);
    }

    [Fact]
    public async Task ATruncatedReplyWithToolCallsIsNotContinued()
    {
        var client = new ScriptedClient(new Turn("calling", [Echo(1)]) { Finish = "length" }, new Turn("done"));
        var result = await MakeEngine(client, Config()).RunAsync([], "go");

        Assert.Equal("done", result.FinalText);
        Assert.DoesNotContain(client.Requests[1].Messages, m => m.Content?.Contains("cut off") == true);
    }

    // MARK: - Goal signals

    [Fact]
    public async Task GoalCompleteEndsTheRunWithoutAnotherModelCall()
    {
        var call = new ToolCall("g1", GoalCompleteTool.ToolName, """{"summary":"Everything builds and passes."}""");
        var client = new ScriptedClient(new Turn("verified it all", [call]), new Turn("should never be asked"));
        var result = await MakeEngine(client, Config(), new EchoTool(), new GoalCompleteTool(), new GoalBlockedTool())
            .RunAsync([], "goal");

        var complete = Assert.IsType<GoalStatus.Complete>(result.Goal);
        Assert.Equal("Everything builds and passes.", complete.Summary);
        Assert.Single(client.Requests);
        // The call was answered, so the transcript remains a valid request for the next round.
        Assert.Equal(MessageRole.Tool, result.Messages[^1].Role);
        Assert.Equal("g1", result.Messages[^1].ToolCallId);
    }

    [Fact]
    public async Task GoalBlockedCarriesTheReason()
    {
        var call = new ToolCall("g1", GoalBlockedTool.ToolName, """{"reason":"I need the production API key"}""");
        var client = new ScriptedClient(Turn.Calling(call));
        var result = await MakeEngine(client, Config(), new GoalCompleteTool(), new GoalBlockedTool()).RunAsync([], "goal");

        Assert.Equal(new GoalStatus.Blocked("I need the production API key"), result.Goal);
    }

    [Fact]
    public async Task ARunWithoutASignalHasNoVerdict()
    {
        var result = await MakeEngine(new ScriptedClient(new Turn("just chatting")), Config()).RunAsync([], "hi");
        Assert.Null(result.Goal);
        Assert.False(result.Stalled);
    }

    // MARK: - Stalls

    [Fact]
    public async Task RepeatingTheSameCallGetsANudgeThenStops()
    {
        var turns = Enumerable.Range(1, 30).Select(n => Turn.Calling(new ToolCall($"c{n}", "echo", """{"text":"again"}"""))).ToArray();
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(100)).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(8, client.Requests.Count); // default StallStopAt
        var tools = result.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(8, tools.Count);
        Assert.DoesNotContain("Harness note", tools[2].Content);
        Assert.Contains("Harness note", tools[3].Content); // the 4th identical call is nudged
        Assert.Contains("made the same `echo` call", result.LastReplyText);
    }

    [Fact]
    public async Task DifferentArgumentsAreNotAStall()
    {
        var turns = Enumerable.Range(1, 12).Select(n => Turn.Calling(Echo(n))).Append(new Turn("done")).ToArray();
        var result = await MakeEngine(new ScriptedClient(turns), Config(100)).RunAsync([], "go");

        Assert.False(result.Stalled);
        Assert.Equal("done", result.FinalText);
    }

    [Fact]
    public void StallTrackerIgnoresWaitingTools()
    {
        var tracker = new StallTracker(2, 3);
        for (var i = 0; i < 9; i++) // (a wait has a ceiling of its own, five times further out)
            Assert.Equal(StallLevel.None, tracker.Observe("process_read", "{}", "no output yet"));
        Assert.Equal(StallLevel.None, tracker.Observe("read_file", "{}", "a"));
        Assert.Equal(StallLevel.Nudge, tracker.Observe("read_file", " { } ", "a")); // whitespace-insensitive
        Assert.Equal(StallLevel.Stop, tracker.Observe("read_file", "{}", "a"));
        Assert.Equal(StallLevel.None, tracker.Observe("read_file", "{}", "changed"));
    }

    // MARK: - Parallel tools

    /// <summary>Completes only if <paramref name="parties"/> of them are running at the same moment.</summary>
    private sealed class MeetingTool(string name, CountdownEvent meeting) : IToolExecutor
    {
        public string Name => name;
        public ToolSpec Spec => new(name, "meets", "{}");

        public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
        {
            meeting.Signal();
            var together = await Task.Run(() => meeting.Wait(TimeSpan.FromSeconds(3)), cancellationToken);
            return together ? $"{name}: together" : $"{name}: alone";
        }
    }

    [Fact]
    public async Task IndependentLookupsRunSideBySideAndAreRecordedInOrder()
    {
        var meeting = new CountdownEvent(2);
        var calls = new[] { new ToolCall("a", "glob", "{}"), new ToolCall("b", "grep", "{}") };
        var client = new ScriptedClient(Turn.Calling(calls), new Turn("done"));
        var events = new EventRecorder();
        var result = await MakeEngine(client, Config(), new MeetingTool("glob", meeting), new MeetingTool("grep", meeting))
            .RunAsync([], "go", sink: events.Record);

        var tools = result.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal(["a", "b"], tools.Select(t => t.ToolCallId));
        Assert.Equal(["glob: together", "grep: together"], tools.Select(t => t.Content));
        // Both started before either finished.
        var order = events.Events.Where(e => e is EngineEvent.ToolStarted or EngineEvent.ToolFinished).Select(e => e.GetType().Name).ToList();
        Assert.Equal(["ToolStarted", "ToolStarted", "ToolFinished", "ToolFinished"], order);
    }

    [Fact]
    public async Task CallsThatNeedApprovalStayInOrder()
    {
        // run_shell_command asks the gate and is never run alongside others.
        var meeting = new CountdownEvent(2);
        var calls = new[] { new ToolCall("a", "glob", "{}"), new ToolCall("b", "echo", """{"text":"x"}""") };
        var client = new ScriptedClient(Turn.Calling(calls), new Turn("done"));
        var result = await MakeEngine(client, Config(), new MeetingTool("glob", meeting), new EchoTool())
            .RunAsync([], "go");

        var tools = result.Messages.Where(m => m.Role == MessageRole.Tool).ToList();
        Assert.Equal("glob: alone", tools[0].Content); // nobody joined it: sequential
        Assert.Equal("echoed: x", tools[1].Content);
    }

    // MARK: - Usage

    [Fact]
    public async Task CachedTokensAddUpAcrossTheRun()
    {
        var client = new ScriptedClient(
            new Turn("", [Echo(1)], new LlmUsage(100, 10, 60)),
            new Turn("done", null, new LlmUsage(150, 20, 100)));
        var result = await MakeEngine(client, Config()).RunAsync([], "go");

        Assert.Equal(new LlmUsage(250, 30, 160), result.Usage);
        Assert.Equal(150, result.LastPromptTokens);
        Assert.Equal(160 / 250.0, result.Usage!.CacheHitRatio);
    }

    [Fact]
    public void UsageWithoutCacheInfoStaysUnknown()
    {
        Assert.Equal(new LlmUsage(3, 4), LlmUsage.Sum(new LlmUsage(1, 2), new LlmUsage(2, 2)));
        Assert.Null(new LlmUsage(10, 2).CacheHitRatio);
        Assert.Equal(new LlmUsage(1, 2), LlmUsage.Sum(new LlmUsage(1, 2), null));
        Assert.Null(LlmUsage.Sum(null, null));
    }
}
