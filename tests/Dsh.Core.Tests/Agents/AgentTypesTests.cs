namespace Dsh.Core.Tests;

public sealed class AgentTypesTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-agents");

    public void Dispose() => _root.Dispose();

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    // MARK: - Definitions

    [Fact]
    public void TheBuiltInTypesCoverTheUsualDivisionOfLabour()
    {
        var catalog = AgentCatalog.Default;
        Assert.Equal(["general", "explore", "plan", "review", "worker"], catalog.All.Select(a => a.Name));
        Assert.Null(catalog.Find(null)!.Tools);
        Assert.Same(catalog.Find("general"), catalog.Find(""));
        Assert.Same(catalog.Find("Explore"), catalog.Find(" explore "));
        Assert.Null(catalog.Find("wizard"));
        // Read-only types cannot change anything.
        foreach (var name in new[] { "explore", "plan", "review" })
        {
            var tools = catalog.Find(name)!.Tools!;
            Assert.DoesNotContain("write_file", tools);
            Assert.DoesNotContain("edit", tools);
        }
        Assert.DoesNotContain("run_shell_command", catalog.Find("explore")!.Tools!);
        Assert.Contains("run_shell_command", catalog.Find("review")!.Tools!);
        Assert.Contains("explore:", catalog.Describe());
    }

    [Fact]
    public void AnAgentFileIsParsedLikeClaudeCodesFormat()
    {
        var def = AgentDefinition.Parse("---\nname: Test Runner\ndescription: Runs the tests and summarises failures\ntools: Read, Grep, Bash, Nonsense\nmodel: spark-2\nmax_steps: 25\n---\nRun `dotnet test` and report failures.\n",
            "fallback", "project", "x.md")!;
        Assert.Equal("test-runner", def.Name);
        Assert.Equal("Runs the tests and summarises failures", def.Description);
        Assert.Equal(["read_file", "grep", "run_shell_command"], def.Tools);
        Assert.Equal("spark-2", def.Server);
        Assert.Equal(25, def.MaxSteps);
        Assert.Equal("Run `dotnet test` and report failures.", def.Prompt);
        Assert.Equal("project", def.Origin);
    }

    [Fact]
    public void ListStyleToolsAndTheFileNameAsFallbackName()
    {
        var def = AgentDefinition.Parse("---\ndescription: Looks around\ntools:\n  - read_file\n  - glob\nreadonly: true\n---\nLook.", "scout", "user", null)!;
        Assert.Equal("scout", def.Name);
        Assert.Equal(["read_file", "glob"], def.Tools);
        Assert.True(def.ReadOnly);
    }

    [Fact]
    public void ReadOnlyWithoutAToolListMeansTheReadOnlyTools()
    {
        var def = AgentDefinition.Parse("---\nname: peek\ndescription: d\nreadonly: true\n---\nb", "peek", "user", null)!;
        Assert.Equal(AgentDefinition.ReadOnlyTools, def.Tools);
    }

    [Theory]
    [InlineData("tools: Foo, Bar")]
    [InlineData("tools:")]
    [InlineData("tools: []")]
    [InlineData("tools: \"\"")]
    public void ToolNamesThatMeanNothingHereLeaveOnlyTheReadingToolsNeverEverything(string line)
    {
        // "tools: Task" in a Claude agent file means "restricted" — reading it as "unrestricted" would hand that agent the shell.
        // A line left blank says the same thing more quietly.
        var def = AgentDefinition.Parse($"---\nname: odd\ndescription: d\n{line}\n---\nb", "odd", "user", null)!;
        Assert.Equal(AgentDefinition.ReadOnlyTools, def.Tools);
        Assert.True(def.IsReadOnly);
    }

    [Theory]
    [InlineData("tools:\n  - Read\n  - Grep\n  # - Bash")]
    [InlineData("tools:\n  # - Bash")]
    [InlineData("tools:\n  # none of the shell tools")]
    [InlineData("tools:\n  - Read # just this\n  # - Bash")]
    public void ACommentedOutToolInAnIndentedListNeverGrantsTheShell(string block)
    {
        var def = AgentDefinition.Parse($"---\nname: careful\ndescription: d\n{block}\n---\nb", "careful", "user", null)!;
        Assert.NotNull(def.Tools);
        Assert.DoesNotContain("run_shell_command", def.Tools!);
        Assert.DoesNotContain("write_file", def.Tools!);
        Assert.DoesNotContain("edit", def.Tools!);
    }

    [Fact]
    public void ACommentInsideAListIsNotAnItemAndDoesNotLoseTheList()
    {
        var def = AgentDefinition.Parse("---\nname: careful\ndescription: d\ntools:\n  - Read\n  # - Bash\n  - Grep\n---\nb", "careful", "user", null)!;
        Assert.Equal(["read_file", "grep"], def.Tools);
        var doc = SkillDocument.Parse("---\nname: x\nallowed-tools:\n  - Read\n  # - Bash\n  - Grep\n---\nb");
        Assert.Equal(["Read", "Grep"], doc.List("allowed-tools"));
    }

    [Fact]
    public void AFileWithoutAToolsListKeepsEveryTool()
    {
        var def = AgentDefinition.Parse("---\nname: free\ndescription: d\n---\nb", "free", "user", null)!;
        Assert.Null(def.Tools);
        Assert.False(def.IsReadOnly);
    }

    [Fact]
    public void DisallowedToolsAreReadAndApplied()
    {
        var def = AgentDefinition.Parse("---\nname: careful\ndescription: d\ndisallowedTools: Bash, Write\n---\nb", "careful", "user", null)!;
        Assert.Null(def.Tools);
        Assert.Equal(["run_shell_command", "write_file"], def.DisallowedTools);
    }

    [Fact]
    public void ReadOnlyIsJudgedFromTheToolsNotTheName()
    {
        Assert.True(AgentDefinition.BuiltIn.Single(a => a.Name == "explore").IsReadOnly);
        Assert.False(AgentDefinition.BuiltIn.Single(a => a.Name == "review").IsReadOnly); // it can run commands
        Assert.False(AgentDefinition.BuiltIn.Single(a => a.Name == "general").IsReadOnly);
        // A project file that replaces "explore" with one that has every tool is not read-only.
        var replaced = AgentDefinition.Parse("---\nname: explore\ndescription: mine\n---\nDo anything.", "explore", "project", null)!;
        Assert.False(replaced.IsReadOnly);
    }

    [Theory]
    [InlineData("---\nname: x\nmodel: sonnet\n---\nbody", null)]
    [InlineData("---\nname: x\nmodel: inherit\n---\nbody", null)]
    [InlineData("---\nname: x\nserver: spark-3\n---\nbody", "spark-3")]
    public void ClaudeModelShortcutsAreNotServers(string text, string? server) =>
        Assert.Equal(server, AgentDefinition.Parse(text, "x", "user", null)!.Server);

    [Fact]
    public void EmptyFilesAreIgnored()
    {
        Assert.Null(AgentDefinition.Parse("---\nname: nothing\n---\n", "nothing", "user", null));
        Assert.Null(AgentDefinition.Parse("body", "!!!", "user", null));
    }

    // MARK: - Catalog files

    [Fact]
    public void ProjectAgentsOverrideUserAgentsWhichOverrideBuiltIns()
    {
        var user = Path.Combine(_root.Path, "user-agents");
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "explore.md"), "---\nname: explore\ndescription: My own explorer\n---\nBe thorough.");
        File.WriteAllText(Path.Combine(user, "helper.md"), "---\ndescription: User helper\n---\nHelp.");
        Write("proj/.dsh/agents/helper.md", "---\ndescription: Project helper\n---\nHelp in this project.");
        Write("proj/.claude/agents/docs.md", "---\ndescription: Writes docs\ntools: Read, Write\n---\nWrite docs.");
        Write("proj/.dsh/agents/broken.md", "---\n---\n");

        var catalog = AgentCatalog.Load(Path.Combine(_root.Path, "proj"), [user]);
        Assert.Equal("My own explorer", catalog.Find("explore")!.Description);
        Assert.Equal("user", catalog.Find("explore")!.Origin);
        Assert.Equal("Project helper", catalog.Find("helper")!.Description);
        Assert.Equal("project", catalog.Find("helper")!.Origin);
        Assert.Equal(["read_file", "write_file"], catalog.Find("docs")!.Tools);
        Assert.Null(catalog.Find("broken"));
        // Built-ins come first, in their usual order; the rest follow alphabetically.
        Assert.Equal(["general", "explore", "plan", "review", "worker"], catalog.All.Take(5).Select(a => a.Name));
        Assert.Equal(["docs", "helper"], catalog.All.Skip(5).Select(a => a.Name));
    }

    [Fact]
    public void MissingFoldersAreFine()
    {
        var catalog = AgentCatalog.Load(Path.Combine(_root.Path, "nowhere"), [Path.Combine(_root.Path, "also-nowhere")]);
        Assert.Equal(AgentDefinition.BuiltIn.Count, catalog.All.Count);
    }

    // MARK: - The agent tool with types

    private ToolContext Context(ILlmClient client, AgentCatalog? catalog = null, AgentFleet? fleet = null, AgentRoster? roster = null) => new()
    {
        Workspace = _root.Path,
        Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
        Client = client,
        Registry = ToolRegistry.Standard(0, null, catalog).Adding([new EchoTool(), new MemorySearchTool(new MemoryStore(Path.Combine(_root.Path, "mem")))]),
        Model = "parent-model",
        ContextWindow = 8_000,
        AgentTypes = catalog,
        Fleet = fleet,
        Roster = roster,
    };

    [Fact]
    public async Task ASpecialistOnlyGetsItsToolsAndItsPrompt()
    {
        var client = new ScriptedClient(new Turn("Found it: Program.cs:12"));
        var result = await new AgentTool().ExecuteAsync("""{"description":"find main","prompt":"where is main?","agent_type":"explore"}""",
            Context(client), default);

        Assert.StartsWith("Subagent 'find main' finished.\n(explore agent · ", result.Output);
        Assert.Contains("Found it: Program.cs:12", result.Output);
        var request = Assert.Single(client.Requests);
        var offered = request.Tools.Select(t => t.Name).ToList();
        Assert.Contains("read_file", offered);
        Assert.Contains("grep", offered);
        Assert.DoesNotContain("write_file", offered);
        Assert.DoesNotContain("run_shell_command", offered);
        Assert.DoesNotContain("echo", offered);
        Assert.DoesNotContain("agent", offered);
        Assert.DoesNotContain("delegate", offered);
        Assert.Contains("read-only investigator", request.SystemPrompt);
        Assert.Contains("You are a focused subagent", request.SystemPrompt);
    }

    [Fact]
    public async Task TheGeneralWorkerKeepsEveryToolExceptTheAgentOnes()
    {
        var client = new ScriptedClient(new Turn("ok"));
        await new AgentTool().ExecuteAsync("""{"description":"d","prompt":"p"}""", Context(client), default);
        var offered = client.Requests[0].Tools.Select(t => t.Name).ToList();
        Assert.Contains("write_file", offered);
        Assert.Contains("echo", offered);
        Assert.Contains("memory_search", offered);
        Assert.DoesNotContain("agent", offered);
        Assert.DoesNotContain("delegate", offered);
    }

    [Fact]
    public async Task AnUnknownTypeIsRefusedWithTheChoices()
    {
        var client = new ScriptedClient();
        var result = await new AgentTool().ExecuteAsync("""{"description":"d","prompt":"p","agent_type":"wizard"}""", Context(client), default);
        Assert.StartsWith("Error: unknown agent_type 'wizard'. Available: general:", result.Output);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void TheToolDescriptionListsTheTypes()
    {
        var catalog = new AgentCatalog([.. AgentDefinition.BuiltIn, new AgentDefinition { Name = "docs", Description = "Writes documentation" }]);
        Assert.Contains("docs: Writes documentation", AgentTool.SpecFor(catalog).Description);
        Assert.Contains("explore:", new DelegateTool(catalog).Spec.Description);
        Assert.Contains("agent_type", ToolRegistry.Standard().Tool("agent")!.Spec.Parameters);
    }

    [Fact]
    public async Task AStepBudgetAskedOfATypeIsHonouredAndWrappedUp()
    {
        var catalog = new AgentCatalog([new AgentDefinition { Name = "brief", Description = "d", MaxSteps = 2 }]);
        var client = new ScriptedClient(
            Turn.Calling(new ToolCall("a", "echo", """{"text":"1"}""")),
            Turn.Calling(new ToolCall("b", "echo", """{"text":"2"}""")),
            new Turn("Ran out of steps but here is what I found."));
        var result = await new AgentTool().ExecuteAsync("""{"description":"d","prompt":"p","agent_type":"brief"}""",
            Context(client, catalog), default);
        Assert.Contains("Ran out of steps but here is what I found.", result.Output);
        Assert.Equal(3, client.Requests.Count);
    }

    // MARK: - Roster

    [Fact]
    public async Task TheRosterFollowsARunFromStartToFinish()
    {
        var roster = new AgentRoster();
        var seen = new List<AgentRunInfo>();
        roster.Changed += seen.Add;
        var client = new ScriptedClient(Turn.Calling(new ToolCall("a", "echo", """{"text":"look"}""")), new Turn("the report"));
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(client, roster: roster), default);

        var run = Assert.Single(roster.All);
        Assert.Equal("scan", run.Description);
        Assert.Equal("general", run.AgentType);
        Assert.Equal(AgentRunStatus.Done, run.Status);
        Assert.Equal(1, run.Steps);
        Assert.Equal("the report", run.Report);
        Assert.NotNull(run.FinishedAt);
        Assert.Contains(seen, r => r.Activity.StartsWith("echo:"));
        Assert.Empty(roster.Running);
    }

    [Fact]
    public async Task AFailedRunIsRecordedAsFailed()
    {
        var roster = new AgentRoster();
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""",
            Context(new FlakyClient(() => LlmException.Http(401, "no key")), roster: roster), default);
        var run = Assert.Single(roster.All);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Contains("no key", run.Report);
    }

    [Fact]
    public async Task StoppingMarksTheRunStopped()
    {
        var roster = new AgentRoster();
        using var cts = new CancellationTokenSource();
        var blocking = new BlockingClient();
        var task = new AgentTool().ExecuteAsync("""{"description":"long","prompt":"p"}""", Context(blocking, roster: roster), cts.Token);
        await blocking.Started.WaitAsync(TimeSpan.FromSeconds(3));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(AgentRunStatus.Stopped, Assert.Single(roster.All).Status);
    }

    [Fact]
    public async Task ARosterListenerThatThrowsCostsNeitherTheRunNorTheServerSlot()
    {
        var roster = new AgentRoster();
        roster.Changed += _ => throw new InvalidOperationException("listener bug");
        var heard = new List<AgentRunInfo>();
        roster.Changed += heard.Add; // a later listener still hears everything
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("a", FleetTestSupport.Reports("the report"), primary: true), FleetTestSupport.Server("b", FleetTestSupport.Reports("the report"))]);

        var result = await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet: fleet, roster: roster), default);

        Assert.Contains("the report", result.Output);
        Assert.Equal(AgentRunStatus.Done, Assert.Single(roster.All).Status);
        Assert.Contains(heard, r => r.Status == AgentRunStatus.Done);
        Assert.All(fleet.Snapshot(), server => Assert.Equal(0, server.InFlight)); // no slot leaked
    }

    [Fact]
    public void TheRosterKeepsItsListBounded()
    {
        var roster = new AgentRoster();
        for (var i = 0; i < 100; i++) roster.Finish(roster.Begin($"t{i}", "general", false), AgentRunStatus.Done, "r");
        Assert.True(roster.All.Count <= 60);
        Assert.Equal("t99", roster.All[^1].Description);
        var running = roster.Begin("live", "general", true);
        roster.ClearFinished();
        Assert.Equal(running, Assert.Single(roster.All).Id);
    }

    [Fact]
    public void TheStampChangesWhenAnAgentFileIsAddedEditedOrRemoved()
    {
        using var root = new TempDirectory();
        var user = Path.Combine(root.Path, "agents");
        var project = Path.Combine(root.Path, "project");
        Directory.CreateDirectory(user);
        var first = AgentCatalog.Stamp(project, [user]);
        Assert.Equal(first, AgentCatalog.Stamp(project, [user])); // unchanged: same stamp

        File.WriteAllText(Path.Combine(user, "reviewer.md"), "---\nname: reviewer\ndescription: d\n---\nbody");
        var added = AgentCatalog.Stamp(project, [user]);
        Assert.NotEqual(first, added);

        File.WriteAllText(Path.Combine(user, "reviewer.md"), "---\nname: reviewer\ndescription: a longer description\n---\nbody");
        var edited = AgentCatalog.Stamp(project, [user]);
        Assert.NotEqual(added, edited);

        Directory.CreateDirectory(Path.Combine(project, ".claude", "agents"));
        File.WriteAllText(Path.Combine(project, ".claude", "agents", "local.md"), "---\nname: local\ndescription: d\n---\nbody");
        Assert.NotEqual(edited, AgentCatalog.Stamp(project, [user]));

        File.Delete(Path.Combine(user, "reviewer.md"));
        File.Delete(Path.Combine(project, ".claude", "agents", "local.md"));
        Assert.Equal(first, AgentCatalog.Stamp(project, [user]));
    }
}

/// <summary>Waits for its token to be cancelled.</summary>
internal sealed class BlockingClient : ILlmClient
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task Started => _started.Task;

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _started.TrySetResult();
        await Task.Delay(Timeout.Infinite, cancellationToken);
        yield break;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}
