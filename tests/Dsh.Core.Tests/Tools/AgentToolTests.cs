namespace Dsh.Core.Tests;

/// <summary>Ported from AgentToolTests.swift.</summary>
public sealed class AgentToolTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-agenttool");

    public void Dispose() => _root.Dispose();

    // MARK: - Compaction.MakePlan: the assistant-boundary escape hatch

    /// <summary>One user message, then many assistant(+tool call)/tool-result rounds — exactly the
    /// shape of a subagent's entire run (it never gets a second user message).</summary>
    private static List<LlmMessage> ToolMarathon(int rounds)
    {
        var output = new List<LlmMessage> { LlmMessage.User("start " + new string('a', 4_000)) };
        for (var i = 0; i < rounds; i++)
        {
            var call = new ToolCall($"c{i}", "read_file", $$"""{"path":"f{{i}}"}""");
            output.Add(LlmMessage.Assistant($"round {i} " + new string('b', 4_000), [call]));
            output.Add(LlmMessage.ToolOutput($"c{i}", "read_file", $"result {i}"));
        }
        return output;
    }

    [Fact]
    public void PlanWithoutFlagFallsBackToAssistantBoundaryForASingleTurnToolMarathon()
    {
        // With only one user message the user-only rule has no boundary to cut at; rather than run
        // into the server's hard limit, the plan falls back to an assistant boundary.
        var msgs = ToolMarathon(20);
        var plan = Compaction.MakePlan(TokenEstimate.Request("", msgs), 8_000, msgs);
        Assert.NotNull(plan);
        Assert.Equal(MessageRole.Assistant, plan.ToKeep[0].Role);
    }

    [Fact]
    public void PlanWithAssistantBoundaryCompactsASingleTurnToolMarathon()
    {
        var msgs = ToolMarathon(20);
        var plan = Compaction.MakePlan(TokenEstimate.Request("", msgs), 8_000, msgs, allowAssistantBoundary: true);
        Assert.NotNull(plan);
        // The kept tail must start at a complete round: the assistant's call together with its own
        // result, never split from it.
        Assert.Equal(MessageRole.Assistant, plan.ToKeep[0].Role);
        var call = Assert.Single(plan.ToKeep[0].ToolCalls ?? []);
        Assert.Equal(call.Id, plan.ToKeep[1].ToolCallId);
        Assert.Equal(msgs.Count, plan.ToSummarize.Count + plan.ToKeep.Count);
        Assert.True(plan.ToSummarize.Count >= Compaction.MinSummarizable);
    }

    // MARK: - Compaction.SummarizeAsync (generic over any ILlmClient)

    private static CompactionPlan SmallPlan() => new(
        [LlmMessage.User("old"), LlmMessage.Assistant("a"), LlmMessage.User("more"), LlmMessage.Assistant("b")],
        [LlmMessage.User("recent")], 1_000, 10_000);

    [Fact]
    public async Task CompactionSummarizeReturnsTrimmedModelText()
    {
        var client = new ScriptedClient(new Turn("  a continuity note  "));
        var summary = await Compaction.SummarizeAsync(client, SmallPlan());

        Assert.Equal("a continuity note", summary);
        var request = Assert.Single(client.Requests);
        Assert.Empty(request.Tools);
        Assert.Equal(ThinkingLevel.Off, request.Thinking); // summaries are written with thinking off
    }

    [Fact]
    public async Task CompactionSummarizeReturnsNullOnClientFailure()
    {
        var client = new FlakyClient(() => LlmException.Sse("boom"));
        Assert.Null(await Compaction.SummarizeAsync(client, SmallPlan()));
    }

    // MARK: - Engine → ToolContext wiring

    [Fact]
    public async Task ToolContextCarriesTheEnginesContextWindow()
    {
        var tool = new CapturingTool();
        var client = new ScriptedClient(new Turn("checking", [new ToolCall("c1", CapturingTool.ToolName, "{}")]), new Turn("done"));
        var engine = new Engine(client, new ToolRegistry([tool]), "system",
            new EngineConfig("test") { ContextWindow = 12_345 }, _root.Path,
            new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow);
        await engine.RunAsync([], "go");

        // A tool (and any subagent it spawns) must see the engine's resolved window.
        Assert.Equal(12_345, tool.Captured?.ContextWindow);
    }

    // MARK: - AgentTool: subagents actually auto-compact

    /// <summary>A summarization request has no tools, thinking off, and exactly one user message.</summary>
    private static bool IsSummaryRequest(LlmRequest request) =>
        request.Tools.Count == 0 && request.Thinking == ThinkingLevel.Off
        && request.Messages.Count == 1 && request.Messages[0].Role == MessageRole.User;

    [Fact]
    public async Task AgentToolAutoCompactsALongSubagentRun()
    {
        // Each round: a large assistant text + a todo_write call, so the subagent's own transcript
        // grows across its iterations exactly like the top-level session's does — but a subagent never
        // gets a second user message, which used to mean it could never compact at all.
        var bigText = new string('a', 4_000);
        var turns = new List<Turn>();
        for (var i = 0; i < 12; i++)
            turns.Add(new Turn(bigText, [new ToolCall($"t{i}", TodoWriteTool.ToolName, """{"todos":[]}""")]));
        turns.Add(new Turn("final report"));
        // One client serves both the subagent's turns and its internal summarization calls (as a real
        // OpenAiClient would), telling them apart by shape rather than by call order.
        var client = new ScriptedClient(turns) { Intercept = r => IsSummaryRequest(r) ? new Turn("SUMMARY") : null };

        var context = new ToolContext
        {
            Workspace = _root.Path,
            Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
            Client = client,
            Registry = ToolRegistry.Standard(),
            Depth = 0,
            Model = "test",
            ContextWindow = 8_000,
            RequestPermission = Gates.Allow,
        };
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"go look at things"}""",
            context, CancellationToken.None);

        Assert.Contains("final report", result.Output); // the subagent must finish despite a tiny window

        // Real turns always carry the tool specs; the interleaved summarization calls never do.
        // At least one request must shrink after compaction fired.
        var sizes = client.Requests.Where(r => r.Tools.Count > 0).Select(r => r.Messages.Count).ToList();
        Assert.Contains(sizes.Zip(sizes.Skip(1)), pair => pair.Second < pair.First);
        Assert.Contains(client.Requests, IsSummaryRequest);
    }
}

/// <summary>New: AgentTool argument checks, depth limit and failure reporting.</summary>
public sealed class AgentToolGuardTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-agentguard");

    public void Dispose() => _root.Dispose();

    private ToolContext Context(ILlmClient client, int depth = 0) => new()
    {
        Workspace = _root.Path,
        Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
        Client = client,
        Registry = ToolRegistry.Standard(depth),
        Depth = depth,
        Model = "test",
    };

    [Fact]
    public async Task DepthOneRefusesNestedAgents()
    {
        var client = new ScriptedClient();
        var result = await new AgentTool().ExecuteAsync("""{"description":"d","prompt":"p"}""", Context(client, depth: 1),
            CancellationToken.None);

        Assert.Equal("Error: nested subagents are not allowed (depth limit).", result.Output);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public async Task PromptIsRequired()
    {
        var result = await new AgentTool().ExecuteAsync("""{"description":"d"}""", Context(new ScriptedClient()),
            CancellationToken.None);
        Assert.Equal("Error: prompt is required.", result.Output);
    }

    [Fact]
    public async Task ClientFailureIsReportedNotThrown()
    {
        // A permanent error: transient ones (a cut-off stream) are retried until the server answers.
        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""",
            Context(new FlakyClient(() => LlmException.Http(401, "boom"))), CancellationToken.None);
        Assert.StartsWith("Subagent 'scan' failed: ", result.Output);
        Assert.Contains("boom", result.Output);
    }

    [Fact]
    public async Task EmptyFinalTextStillProducesAReport()
    {
        var result = await new AgentTool().ExecuteAsync("""{"prompt":"p"}""", Context(new ScriptedClient(new Turn(""))),
            CancellationToken.None);
        Assert.StartsWith("Subagent 'subagent' finished.", result.Output);
        Assert.Contains("without a final report", result.Output);
    }

    [Fact]
    public void RegistryOnlyOffersTheAgentToolAtTheTopLevel()
    {
        Assert.Contains(AgentTool.ToolName, ToolRegistry.Standard(0).Names);
        Assert.DoesNotContain(AgentTool.ToolName, ToolRegistry.Standard(1).Names);
    }

    [Fact]
    public void SubagentPromptStatesWorkspacePresetAndShell()
    {
        var shell = new AgentShell(ShellKind.PowerShell, "pwsh.exe", "PowerShell 7.5 (pwsh)");
        var prompt = AgentTool.SubagentPrompt(_root.Path, PermissionPreset.Plan, shell);
        Assert.Contains($"Workspace: {_root.Path}", prompt);
        Assert.Contains("Permission preset: plan", prompt);
        Assert.Contains("PowerShell 7.5 (pwsh)", prompt);
    }
}
