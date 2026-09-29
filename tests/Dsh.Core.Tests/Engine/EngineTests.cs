namespace Dsh.Core.Tests;

/// <summary>Ported from EngineTests.swift: the agent loop driven by a scripted client.</summary>
public sealed class EngineTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-engine");

    public void Dispose() => _root.Dispose();

    private Engine MakeEngine(ILlmClient client, ToolRegistry? registry = null,
                              PermissionPreset preset = PermissionPreset.WorkspaceWrite,
                              string? workspace = null, PermissionGate? gate = null)
    {
        var root = workspace ?? _root.Path;
        return new Engine(client, registry ?? new ToolRegistry([new EchoTool()]), "system",
            new EngineConfig("test") { MaxIterations = 5, ToolTimeout = TimeSpan.FromSeconds(5) },
            root, new PermissionPolicy(preset, root), gate ?? Gates.Allow);
    }

    [Fact]
    public async Task TextOnlyTurnFinishes()
    {
        var client = new ScriptedClient(new Turn("hello there"));
        var events = new EventRecorder();
        var result = await MakeEngine(client).RunAsync([], "hi", sink: events.Record);

        Assert.Equal("hello there", result.FinalText);
        Assert.Equal(0, result.DeniedCount);
        Assert.Equal(2, result.Messages.Count); // user + assistant
        Assert.Equal(MessageRole.User, result.Messages[0].Role);
        Assert.Equal("hello there", string.Concat(events.Of<EngineEvent.TextDelta>().Select(d => d.Text)));
        Assert.IsType<EngineEvent.Finished>(events.Events[^1]);
    }

    [Fact]
    public async Task ToolCallRoundTrip()
    {
        var call = new ToolCall("c1", "echo", """{"text":"ping"}""");
        var client = new ScriptedClient(new Turn("let me check", [call]), new Turn("all done"));
        var events = new EventRecorder();
        var result = await MakeEngine(client).RunAsync([], "go", sink: events.Record);

        Assert.Equal("all done", result.FinalText);
        // user, assistant+call, tool result, assistant
        Assert.Equal(4, result.Messages.Count);
        Assert.Equal(MessageRole.Tool, result.Messages[2].Role);
        Assert.Equal("echoed: ping", result.Messages[2].Content);
        Assert.Equal("c1", result.Messages[2].ToolCallId);
        Assert.Equal("echo", result.Messages[2].Name);

        Assert.Contains(events.Of<EngineEvent.ToolStarted>(), e => e.Name == "echo" && e.Preview == "ping");
        Assert.Contains(events.Of<EngineEvent.ToolFinished>(), e => e.Ok);
    }

    /// <summary>Opaque provider state from Done (Claude's signed reasoning on Bedrock) is stored on the
    /// assistant message and so reaches the next request with it.</summary>
    [Fact]
    public async Task ProviderStateRidesOnTheAssistantMessageIntoTheNextRequest()
    {
        var call = new ToolCall("c1", "echo", """{"text":"ping"}""");
        var client = new ScriptedClient(new Turn("let me check", [call]) { ProviderState = "[signed]" }, new Turn("all done"));
        var result = await MakeEngine(client).RunAsync([], "go");

        Assert.Equal("[signed]", result.Messages[1].ProviderState);
        Assert.Null(result.Messages[^1].ProviderState);
        var next = client.Requests[1].Messages.Single(m => m.Role == MessageRole.Assistant);
        Assert.Equal("[signed]", next.ProviderState);
        Assert.Equal("let me check", next.Content);
    }

    /// <summary>The engine must forward a tool's file changes so the editor can reload.</summary>
    [Fact]
    public async Task FileChangesAreEmitted()
    {
        var call = new ToolCall("c1", "echo", """{"text":"x"}""");
        var client = new ScriptedClient(Turn.Calling(call), new Turn("ok"));
        var events = new EventRecorder();
        await MakeEngine(client).RunAsync([], "go", sink: events.Record);

        var change = events.Of<EngineEvent.FilesChanged>()[0].Changes[0];
        Assert.Equal(FileChangeKind.Created, change.Kind);
        Assert.Equal(Path.Combine(_root.Path, "out.txt"), change.Path);
    }

    [Fact]
    public async Task FailingToolIsReportedNotFatal()
    {
        var call = new ToolCall("c1", "boom", "{}");
        var client = new ScriptedClient(Turn.Calling(call), new Turn("recovered"));
        var events = new EventRecorder();
        var result = await MakeEngine(client, new ToolRegistry([new FailingTool()]))
            .RunAsync([], "go", sink: events.Record);

        Assert.Equal("recovered", result.FinalText);
        Assert.Contains(events.Of<EngineEvent.ToolFinished>(), e => !e.Ok);
    }

    [Fact]
    public async Task UnknownToolDoesNotCrashTheRun()
    {
        var call = new ToolCall("c1", "nope", "{}");
        var client = new ScriptedClient(Turn.Calling(call), new Turn("moved on"));
        var result = await MakeEngine(client).RunAsync([], "go");

        Assert.Equal("moved on", result.FinalText);
        Assert.Contains("unknown tool", result.Messages[2].Content);
    }

    /// <summary>A denied write must not execute, and the model must be told to stop rather than retry.</summary>
    [Fact]
    public async Task DeniedWriteIsRecordedAndNotExecuted()
    {
        using var elsewhere = new TempDirectory("dsh-outside");
        var outside = Path.Combine(elsewhere.Path, "escaped.txt");
        var call = new ToolCall("c1", "write_file", Args.Json(new { file_path = outside, content = "nope" }));
        var client = new ScriptedClient(Turn.Calling(call), new Turn("understood"));
        var asked = new LockedList();
        var engine = MakeEngine(client, ToolRegistry.Standard(),
            gate: (_, _, detail) => { asked.Add(detail); return Task.FromResult(false); });
        var result = await engine.RunAsync([], "write outside");

        Assert.Equal(1, result.DeniedCount);
        Assert.False(File.Exists(outside));
        Assert.Contains("Permission denied", result.Messages[2].Content);
        Assert.Equal($"Write outside the project: {outside}", Assert.Single(asked.Items));
    }

    [Fact]
    public async Task ApprovedWriteOutsideWorkspaceProceeds()
    {
        using var sibling = new TempDirectory("dsh-sibling");
        var target = Path.Combine(sibling.Path, "allowed.txt");
        var call = new ToolCall("c1", "write_file", Args.Json(new { file_path = target, content = "yes" }));
        var client = new ScriptedClient(Turn.Calling(call), new Turn("written"));
        await MakeEngine(client, ToolRegistry.Standard(), gate: Gates.Allow).RunAsync([], "write");

        Assert.Equal("yes", File.ReadAllText(target));
    }

    [Fact]
    public async Task UsageAccumulatesAcrossIterations()
    {
        var call = new ToolCall("c1", "echo", """{"text":"x"}""");
        var client = new ScriptedClient(
            new Turn("", [call], new LlmUsage(10, 5)),
            new Turn("ok", null, new LlmUsage(20, 7)));
        var result = await MakeEngine(client).RunAsync([], "go");

        Assert.Equal(new LlmUsage(30, 12), result.Usage);
        Assert.Equal(20, result.LastPromptTokens);
    }

    [Fact]
    public async Task IterationBudgetStopsRunawayLoops()
    {
        // Every turn asks for another tool call; the engine must give up.
        var call = new ToolCall("c", "echo", """{"text":"again"}""");
        var client = new ScriptedClient(Enumerable.Repeat(Turn.Calling(call), 50));
        var result = await MakeEngine(client).RunAsync([], "go");

        // 5 iterations × (assistant + tool) + the initial user message.
        Assert.Equal(11, result.Messages.Count);
        Assert.True(result.HitIterationLimit);
        Assert.Equal(5, client.Requests.Count);
    }

    [Fact]
    public async Task ToolSpecsAreOfferedToTheModel()
    {
        var client = new ScriptedClient(new Turn("hi"));
        await MakeEngine(client, ToolRegistry.Standard()).RunAsync([], "go");

        var offered = client.Requests[0].Tools.Select(t => t.Name).ToHashSet();
        // The Qwen Code tool names, which is what makes prompts portable.
        foreach (var expected in new[]
                 {
                     "read_file", "write_file", "edit", "list_directory", "glob", "grep", "read_many_files",
                     "run_shell_command", "web_fetch", "todo_write", "exit_plan_mode",
                 })
        {
            Assert.Contains(expected, offered);
        }
    }

    [Fact]
    public async Task PriorTranscriptIsCarriedForward()
    {
        var client = new ScriptedClient(new Turn("second"));
        LlmMessage[] history = [LlmMessage.User("first question"), LlmMessage.Assistant("first answer")];
        await MakeEngine(client).RunAsync(history, "follow up");

        var sent = client.Requests[0].Messages;
        Assert.Equal(3, sent.Count);
        Assert.Equal("first question", sent[0].Content);
        Assert.Equal("follow up", sent[2].Content);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        var client = new ScriptedClient(Enumerable.Repeat(new Turn("..."), 100));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MakeEngine(client).RunAsync([], "go", cancellationToken: cts.Token));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void PreviewPicksTheInterestingArgument()
    {
        Assert.Equal("a/b.swift", Engine.Preview(new ToolCall("1", "read_file", """{"file_path":"a/b.swift"}""")));
        Assert.Equal("ls -la", Engine.Preview(new ToolCall("2", "run_shell_command", """{"command":"ls -la"}""")));
        Assert.Equal("TODO", Engine.Preview(new ToolCall("3", "grep", """{"pattern":"TODO"}""")));
        // A plugin tool: fall back to the first string argument.
        Assert.Equal("release", Engine.Preview(new ToolCall("4", "custom", """{"target":"release"}""")));
    }
}

/// <summary>New for the Windows port: failure modes of tool execution (exceptions, the watchdog,
/// cancellation), subagents, and the permission/ToolContext plumbing around them.</summary>
public sealed class EngineExecutionTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-engine-exec");

    public void Dispose() => _root.Dispose();

    private Engine MakeEngine(ILlmClient client, ToolRegistry registry, TimeSpan? toolTimeout = null, int depth = 0,
                              PermissionPreset preset = PermissionPreset.WorkspaceWrite, PermissionGate? gate = null) =>
        new(client, registry, "system",
            new EngineConfig("test")
            {
                MaxIterations = 5,
                ToolTimeout = toolTimeout ?? TimeSpan.FromSeconds(5),
                Depth = depth,
            },
            _root.Path, new PermissionPolicy(preset, _root.Path), gate ?? Gates.Allow);

    [Fact]
    public async Task ToolExceptionBecomesAnErrorResult()
    {
        var client = new ScriptedClient(Turn.Calling(new ToolCall("c1", ThrowingTool.ToolName, "{}")), new Turn("recovered"));
        var events = new EventRecorder();
        var result = await MakeEngine(client, new ToolRegistry([new ThrowingTool("kaput")]))
            .RunAsync([], "go", sink: events.Record);

        Assert.Equal("Error: tool failed: kaput", result.Messages[2].Content);
        Assert.Equal("recovered", result.FinalText);
        var finished = Assert.Single(events.Of<EngineEvent.ToolFinished>());
        Assert.False(finished.Ok);
        Assert.Equal("Error: tool failed: kaput", finished.Summary);
    }

    [Fact]
    public async Task ToolTimeoutProducesAnErrorAndCancelsTheTool()
    {
        var tool = new BlockingTool("slow");
        var client = new ScriptedClient(Turn.Calling(new ToolCall("c1", "slow", "{}")), new Turn("moved on"));
        var result = await MakeEngine(client, new ToolRegistry([tool]), toolTimeout: TimeSpan.FromSeconds(1))
            .RunAsync([], "go");

        Assert.Equal("Error: tool timed out after 1s.", result.Messages[2].Content);
        Assert.Equal("moved on", result.FinalText);
        // The watchdog cancels the abandoned tool rather than leaving it running.
        await tool.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellationWhileAToolRunsPropagates()
    {
        var tool = new BlockingTool("block");
        var client = new ScriptedClient(Turn.Calling(new ToolCall("c1", "block", "{}")), new Turn("never reached"));
        using var cts = new CancellationTokenSource();
        var run = MakeEngine(client, new ToolRegistry([tool])).RunAsync([], "go", cancellationToken: cts.Token);

        await tool.Started.WaitAsync(TimeSpan.FromSeconds(10));
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        await tool.Cancelled.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(client.Requests); // no further model turn after the cancel
    }

    [Fact]
    public async Task SubagentRunsWithTheSameClientAndReportsItsFinalText()
    {
        var agentCall = new ToolCall("a1", AgentTool.ToolName,
            Args.Json(new { description = "scout", prompt = "find the config file" }));
        var client = new ScriptedClient(
            new Turn("delegating", [agentCall]),
            new Turn("sub report: it is config/app.json"),
            new Turn("parent done"));
        var result = await MakeEngine(client, ToolRegistry.Standard()).RunAsync([], "go");

        Assert.Equal("parent done", result.FinalText);
        Assert.Equal("Subagent 'scout' finished.\n\nsub report: it is config/app.json", result.Messages[2].Content);

        var requests = client.Requests;
        Assert.Equal(3, requests.Count);
        var sub = requests[1];
        Assert.StartsWith("You are a focused subagent", sub.SystemPrompt);
        Assert.Contains(_root.Path, sub.SystemPrompt);
        Assert.Equal("find the config file", Assert.Single(sub.Messages).Content);
        Assert.Equal("test", sub.Model);
        // Subagents get the same tools minus the agent tool itself.
        Assert.Contains(AgentTool.ToolName, requests[0].Tools.Select(t => t.Name));
        Assert.DoesNotContain(AgentTool.ToolName, sub.Tools.Select(t => t.Name));
        Assert.Contains(ReadFileTool.ToolName, sub.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task EngineAtDepthOneRefusesNestedAgents()
    {
        var agentCall = new ToolCall("a1", AgentTool.ToolName, Args.Json(new { description = "nested", prompt = "go deeper" }));
        var client = new ScriptedClient(Turn.Calling(agentCall), new Turn("ok"));
        var result = await MakeEngine(client, new ToolRegistry([new AgentTool()]), depth: 1).RunAsync([], "go");

        Assert.Equal("Error: nested subagents are not allowed (depth limit).", result.Messages[2].Content);
        Assert.Equal(2, client.Requests.Count); // no subagent request was made
    }

    [Fact]
    public async Task ToolContextCarriesTheEngineSettings()
    {
        var tool = new CapturingTool();
        var client = new ScriptedClient(Turn.Calling(new ToolCall("c1", CapturingTool.ToolName, "{}")), new Turn("done"));
        var shell = new AgentShell(ShellKind.Cmd, "cmd.exe", "Command Prompt (cmd.exe)");
        var engine = new Engine(client, new ToolRegistry([tool]), "system",
            new EngineConfig("the-model") { Depth = 1, ContextWindow = 12_345, Thinking = ThinkingLevel.High, Shell = shell },
            _root.Path, new PermissionPolicy(PermissionPreset.Plan, _root.Path), Gates.Allow);
        await engine.RunAsync([], "go");

        var context = Assert.IsType<ToolContext>(tool.Captured);
        Assert.Equal(_root.Path, context.Workspace);
        Assert.Equal("the-model", context.Model);
        Assert.Equal(1, context.Depth);
        Assert.Equal(12_345, context.ContextWindow);
        Assert.Equal(ThinkingLevel.High, context.Thinking);
        Assert.Same(shell, context.Shell);
        Assert.Same(client, context.Client);
        Assert.Equal(PermissionPreset.Plan, context.Policy.Preset);
    }

    [Fact]
    public async Task MutatingShellCommandAsksAndADenialNeverRunsIt()
    {
        var marker = Path.Combine(_root.Path, "should-not-exist");
        var command = OperatingSystem.IsWindows() ? $"New-Item -ItemType File '{marker}'" : $"mkdir '{marker}'";
        var call = new ToolCall("c1", RunShellCommandTool.ToolName, Args.Json(new { command }));
        var client = new ScriptedClient(Turn.Calling(call), new Turn("ok"));
        var asked = new LockedList();
        var result = await MakeEngine(client, ToolRegistry.Standard(),
                gate: (_, name, detail) => { asked.Add($"{name}: {detail}"); return Task.FromResult(false); })
            .RunAsync([], "go");

        Assert.Equal($"run_shell_command: Run command: {command}", Assert.Single(asked.Items));
        Assert.Equal(1, result.DeniedCount);
        Assert.StartsWith("Permission denied: user declined.", result.Messages[2].Content);
        Assert.False(Path.Exists(marker));
    }

    [Fact]
    public async Task PlanModeAsksBeforeWritingInsideTheProject()
    {
        var target = Path.Combine(_root.Path, "notes.md");
        var call = new ToolCall("c1", WriteFileTool.ToolName, Args.Json(new { file_path = "notes.md", content = "x" }));
        var client = new ScriptedClient(Turn.Calling(call), new Turn("ok"));
        var asked = new LockedList();
        await MakeEngine(client, ToolRegistry.Standard(), preset: PermissionPreset.Plan,
                gate: (_, _, detail) => { asked.Add(detail); return Task.FromResult(true); })
            .RunAsync([], "go");

        Assert.Equal($"Write: {target}", Assert.Single(asked.Items));
        Assert.Equal("x", File.ReadAllText(target)); // approved, so it ran
    }

    [Fact]
    public async Task TodosArePushedToTheCallback()
    {
        var call = new ToolCall("c1", TodoWriteTool.ToolName,
            """{"todos":[{"content":"plan","status":"completed"},{"content":"build","status":"in_progress"}]}""");
        var client = new ScriptedClient(Turn.Calling(call), new Turn("ok"));
        IReadOnlyList<TodoItem>? pushed = null;
        var engine = new Engine(client, ToolRegistry.Standard(), "system", new EngineConfig("test"), _root.Path,
            new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow)
        {
            OnTodos = items => pushed = items,
        };
        await engine.RunAsync([], "go");

        Assert.NotNull(pushed);
        Assert.Equal([TodoStatus.Completed, TodoStatus.InProgress], pushed.Select(t => t.Status));
        Assert.Equal(["plan", "build"], pushed.Select(t => t.Content));
    }

    [Fact]
    public async Task HugeToolResultsAreTruncatedToTheirTail()
    {
        var huge = new string('a', 30_000) + new string('z', 20_000);
        var client = new ScriptedClient(Turn.Calling(new ToolCall("c1", "huge", "{}")), new Turn("ok"));
        var result = await MakeEngine(client, new ToolRegistry([new StubTool("huge", huge)])).RunAsync([], "go");

        var content = result.Messages[2].Content!;
        Assert.EndsWith(new string('z', 20_000) + "\n[result truncated]", content);
        Assert.Equal(40_000 + "\n[result truncated]".Length, content.Length);
    }

    [Fact]
    public void SummaryIsTheFirstNonEmptyLineCapped()
    {
        Assert.Equal("first", Engine.Summary("\n\nfirst\nsecond"));
        Assert.Equal(120, Engine.Summary(new string('x', 500)).Length);
    }

    [Fact]
    public void PreviewCoversTheBuiltInsAndFallsBackToTheToolName()
    {
        Assert.Equal("scan the repo", Engine.Preview(new ToolCall("1", "agent", """{"description":"scan the repo","prompt":"p"}""")));
        Assert.Equal("update task list", Engine.Preview(new ToolCall("2", "todo_write", """{"todos":[]}""")));
        Assert.Equal("https://example.com", Engine.Preview(new ToolCall("3", "web_fetch", """{"url":"https://example.com"}""")));
        Assert.Equal("read_file", Engine.Preview(new ToolCall("4", "read_file", "not json")));
        Assert.Equal("custom", Engine.Preview(new ToolCall("5", "custom", """{"n":3}""")));
    }
}
