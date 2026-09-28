namespace Dsh.Core.Tests;

/// <summary>Ported from the ToolImageEngineTests class in ComputerCoreTests.swift (a fake "screenshot"
/// tool; the macOS-only TerminalGuardTests are not ported).</summary>
public sealed class ToolImageEngineTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-images");

    public void Dispose() => _root.Dispose();

    private Engine MakeEngine(ScriptedClient client, PermissionPreset preset = PermissionPreset.FullAccess,
                              bool vision = true, int keep = 3, PermissionGate? gate = null,
                              ComputerGrants? grants = null) =>
        new(client, new ToolRegistry([new FakeShotTool(), new EchoTool()]), "s",
            new EngineConfig("t")
            {
                MaxIterations = 8, ToolTimeout = TimeSpan.FromSeconds(5), MaxToolImageMessages = keep, VisionEnabled = vision,
            },
            _root.Path, new PermissionPolicy(preset, _root.Path), gate ?? Gates.Allow)
        {
            ComputerGrants = grants ?? new ComputerGrants(),
        };

    private static ToolCall Shot(string id) => new(id, FakeShotTool.ToolName, "{}");

    [Fact]
    public async Task ToolImagesReachTheModelOnAFollowUpUserMessage()
    {
        var client = new ScriptedClient(new Turn("looking", [Shot("c1")]), new Turn("I see a blue box"));
        var events = new EventRecorder();
        var result = await MakeEngine(client).RunAsync([], "what's on screen?", sink: events.Record);

        // Second request: assistant call, its tool result, THEN the image message.
        var second = client.Requests[1].Messages;
        Assert.Equal([MessageRole.User, MessageRole.Assistant, MessageRole.Tool, MessageRole.User], second.Select(m => m.Role));
        Assert.Single(second[^1].Attachments ?? []);
        Assert.Equal("screenshot", second[^1].ImageSource);
        Assert.Equal("I see a blue box", result.FinalText);
        Assert.Single(Assert.Single(events.Of<EngineEvent.ToolImages>()).Images);
    }

    [Fact]
    public async Task OldToolImagesArePrunedButNewestKept()
    {
        var turns = Enumerable.Range(0, 5).Select(i => new Turn($"s{i}", [Shot($"c{i}")])).Append(new Turn("done"));
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, keep: 2).RunAsync([], "go");

        var images = result.Messages.Where(m => m.ImageSource is not null).ToList();
        Assert.Equal(5, images.Count);
        Assert.Equal(2, images.Count(m => m.Attachments is { Count: > 0 })); // only the newest 2 keep pixels
        Assert.Contains("removed to save context", images[0].Content);

        // A user's own attachment is never pruned.
        var mine = new List<LlmMessage> { LlmMessage.User("mine", [new MessageAttachment(AttachmentKind.Image, "x.png", [1])]) };
        Engine.PruneToolImages(mine, 0);
        Assert.Single(mine[0].Attachments ?? []);
    }

    [Fact]
    public async Task NoVisionModelGetsTextInsteadOfImages()
    {
        var client = new ScriptedClient(Turn.Calling(Shot("c1")), new Turn("ok"));
        var result = await MakeEngine(client, vision: false).RunAsync([], "look");

        Assert.DoesNotContain(result.Messages, m => m.ImageSource is not null);
        Assert.Contains("can't take images", result.Messages.First(m => m.Role == MessageRole.Tool).Content);
    }

    [Fact]
    public async Task FirstScreenshotAsksOnceThenHoldsForTheChat()
    {
        var asked = new LockedList();
        var grants = new ComputerGrants();
        var client = new ScriptedClient(Turn.Calling(Shot("c0"), Shot("c1"), Shot("c2")), new Turn("done"));
        await MakeEngine(client, PermissionPreset.WorkspaceWrite,
                gate: (_, name, detail) => { asked.Add($"{name}: {detail}"); return Task.FromResult(true); },
                grants: grants)
            .RunAsync([], "go");

        Assert.Contains("Screen access", Assert.Single(asked.Items));
        Assert.True(grants.Has(ComputerAccess.Observe));
        Assert.False(grants.Has(ComputerAccess.Control));
    }

    [Fact]
    public async Task DeclinedScreenAccessReturnsDenial()
    {
        var client = new ScriptedClient(Turn.Calling(Shot("c1")), new Turn("ok"));
        var result = await MakeEngine(client, PermissionPreset.WorkspaceWrite, gate: Gates.Deny).RunAsync([], "go");

        Assert.Equal(1, result.DeniedCount);
        Assert.DoesNotContain(result.Messages, m => m.ImageSource is not null);
    }

    [Fact]
    public async Task PlanModeNeverDrivesTheMachine()
    {
        var client = new ScriptedClient(Turn.Calling(new ToolCall("c1", "mouse", "{}")), new Turn("ok"));
        var engine = new Engine(client, new ToolRegistry([new StubTool("mouse", "clicked")]), "s", new EngineConfig("t"),
            _root.Path, new PermissionPolicy(PermissionPreset.Plan, _root.Path), Gates.Allow);
        var result = await engine.RunAsync([], "click");

        Assert.Equal(1, result.DeniedCount);
        Assert.Contains("Plan mode", result.Messages.First(m => m.Role == MessageRole.Tool).Content);
    }

    [Fact]
    public async Task BackgroundProcessStartIsGatedLikeShell()
    {
        var asked = new LockedList();
        var calls = new[]
        {
            new ToolCall("a", "process_start", """{"command":"godot --path ."}"""),
            new ToolCall("b", "process_start", """{"command":"rm -rf build"}"""),
        };
        var client = new ScriptedClient(Turn.Calling(calls), new Turn("ok"));
        var engine = new Engine(client, new ToolRegistry([new StubTool("process_start", "started")]), "s",
            new EngineConfig("t"), _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
            (_, _, detail) => { asked.Add(detail); return Task.FromResult(false); });
        await engine.RunAsync([], "go");

        // The harmless command runs; the rm asks.
        Assert.Contains("rm -rf build", Assert.Single(asked.Items));
    }
}

/// <summary>New: the approvals model behind screen and input control.</summary>
public sealed class ComputerAccessTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-computer");

    public void Dispose() => _root.Dispose();

    [Theory]
    [InlineData("screenshot", ComputerAccess.Observe)]
    [InlineData("list_windows", ComputerAccess.Observe)]
    [InlineData("ui_tree", ComputerAccess.Observe)]
    [InlineData("inspect_process", ComputerAccess.Observe)]
    [InlineData("mouse", ComputerAccess.Control)]
    [InlineData("keyboard", ComputerAccess.Control)]
    [InlineData("focus_app", ComputerAccess.Control)]
    public void ToolsMapToTheAccessTheyNeed(string tool, ComputerAccess access)
    {
        Assert.Equal(access, ComputerAccessInfo.ForTool(tool));
    }

    [Fact]
    public void OtherToolsNeedNoComputerAccess()
    {
        Assert.Null(ComputerAccessInfo.ForTool("read_file"));
        Assert.Null(ComputerAccessInfo.ForTool("run_shell_command"));
    }

    [Fact]
    public void ControlImpliesObserveButNotTheOtherWayRound()
    {
        var grants = new ComputerGrants();
        grants.Grant(ComputerAccess.Observe);
        Assert.False(grants.Has(ComputerAccess.Control));

        grants.Grant(ComputerAccess.Control);
        Assert.True(grants.Has(ComputerAccess.Observe));

        grants.RevokeAll();
        Assert.False(grants.Has(ComputerAccess.Observe));
        Assert.False(grants.Has(ComputerAccess.Control));
    }

    [Fact]
    public void PromptsSayWhatIsBeingAllowed()
    {
        Assert.StartsWith("Screen access", ComputerAccess.Observe.Prompt());
        Assert.StartsWith("Computer control", ComputerAccess.Control.Prompt());
        Assert.Contains("Ctrl+.", ComputerAccess.Control.Prompt());
    }

    /// <summary>An approval of control covers the screenshots it needs, so the user is asked once.</summary>
    [Fact]
    public async Task GrantedControlCoversLaterScreenshots()
    {
        var asked = new LockedList();
        var client = new ScriptedClient(
            Turn.Calling(new ToolCall("c1", "mouse", "{}"), new ToolCall("c2", FakeShotTool.ToolName, "{}")),
            new Turn("ok"));
        var engine = new Engine(client, new ToolRegistry([new StubTool("mouse", "clicked"), new FakeShotTool()]), "s",
            new EngineConfig("t"), _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
            (_, name, _) => { asked.Add(name); return Task.FromResult(true); });
        var result = await engine.RunAsync([], "go");

        Assert.Equal(["mouse"], asked.Items);
        Assert.Equal(0, result.DeniedCount);
    }
}
