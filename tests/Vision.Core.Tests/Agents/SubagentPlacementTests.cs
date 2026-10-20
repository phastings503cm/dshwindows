namespace Vision.Core.Tests;

/// <summary>Patrick 10-18: "I can't tell what models/agents are working on what, or if they're even being used properly."
/// The audit's fixes, checked at the far end — the request the subagent's server receives: it runs on the server it was
/// leased with that route's own settings, and the roster says where it runs, why, and what it used.</summary>
public sealed class SubagentPlacementTests : IDisposable
{
    private readonly TempDirectory _root = new("vision-placement");

    public void Dispose() => _root.Dispose();

    private ToolContext Context(ILlmClient client, AgentFleet fleet, AgentRoster roster, AgentCatalog? catalog = null) => new()
    {
        Workspace = _root.Path,
        Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
        Client = client,
        Registry = ToolRegistry.Standard(0),
        Model = "parent-model",
        Temperature = 0.7,
        MaxOutputTokens = 32_000,
        Thinking = ThinkingLevel.High,
        Fleet = fleet,
        Roster = roster,
        AgentTypes = catalog,
        Retry = RetryPolicy.Standard with { Delay = _ => TimeSpan.Zero },
    };

    private static ScriptedClient Worker(string report = "worker report") =>
        new() { Intercept = _ => new Turn(report) { Usage = new LlmUsage(1_200, 300) } };

    [Fact]
    public async Task ASubagentOnAnotherRouteSendsThatRoutesSettingsNotTheParents()
    {
        var worker = Worker();
        var fleet = new AgentFleet { UsePrimaryWhenWorkersBusy = false };
        fleet.Configure([
            FleetTestSupport.Server("main", Worker("from main"), primary: true),
            FleetTestSupport.Server("kimi", worker, resolve: () => Task.FromResult(new FleetTarget(worker, "kimi-k2", 128_000)
            {
                OwnSettings = true, Temperature = null, MaxOutputTokens = null, Thinking = null,
            })),
        ]);
        var roster = new AgentRoster();
        var result = await new AgentTool().ExecuteAsync("""{"description":"review","prompt":"review the diff"}""", Context(new ScriptedClient(), fleet, roster), default);

        Assert.Contains("worker report", result.Output);
        var request = Assert.Single(worker.Requests);
        // The worker's own model and none of the main route's settings (a Kimi or GLM server answers 400 to some of them).
        Assert.Equal(("kimi-k2", (double?)null, (int?)null, (ThinkingLevel?)null), (request.Model, request.Temperature, request.MaxTokens, request.Thinking));

        var run = Assert.Single(roster.All);
        Assert.Equal(("kimi", "kimi-k2", "kimi-k2 on kimi"), (run.Route, run.Model, run.PlacementLabel));
        Assert.Null(run.Placement);
        Assert.Equal((1_200L, 300L), (run.PromptTokens, run.CompletionTokens));
        Assert.Equal("review the diff", run.Prompt);
        Assert.Equal(["Started (general)", "waiting for a free server", "Running on kimi-k2 · kimi — least busy", "writing the report", "Finished — report delivered"],
            run.Log.Select(l => l.Text));
    }

    [Fact]
    public async Task ASubagentOnThePrimarySharesTheParentsSettings()
    {
        var main = Worker("from main");
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("main", main, primary: true)]);
        var roster = new AgentRoster();
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet, roster), default);
        var request = Assert.Single(main.Requests);
        Assert.Equal(("model-main", (double?)0.7, (int?)32_000, (ThinkingLevel?)ThinkingLevel.High), (request.Model, request.Temperature, request.MaxTokens, request.Thinking));
        // Even with one server — no choice to name — the roster knows where it ran.
        Assert.Equal("model-main on main", roster.All.Single().PlacementLabel);
        Assert.Null(roster.All.Single().Server);
    }

    [Fact]
    public async Task AServerThatCouldNotTakeItIsNamedWithWhy()
    {
        var fleet = new AgentFleet();
        fleet.Configure([
            FleetTestSupport.Server("main", Worker("from main"), primary: true),
            FleetTestSupport.Server("spark-2", Worker(), resolve: () => throw LlmException.Connection("http://spark-2:8000/v1 didn't answer")),
        ]);
        var roster = new AgentRoster();
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", Context(new ScriptedClient(), fleet, roster), default);
        var run = roster.All.Single();
        Assert.Equal("main", run.Route);
        Assert.StartsWith("tried first: spark-2: ", run.Placement);
        Assert.Contains("didn't answer", run.Placement);
        Assert.Contains(run.Log, l => l.Text.StartsWith("Running on model-main · main — least busy — tried first: spark-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAgentFileAskingForAServerThatIsNotSetUpSaysSo()
    {
        var catalog = new AgentCatalog([.. AgentDefinition.BuiltIn, AgentDefinition.BuiltIn[0] with { Name = "gpu", Server = "spark-9" }]);
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("main", Worker(), primary: true), FleetTestSupport.Server("spark-2", Worker())]);
        var roster = new AgentRoster();
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p","agent_type":"gpu"}""", Context(new ScriptedClient(), fleet, roster, catalog), default);
        Assert.Equal("its agent file asks for “spark-9”, but no model server by that name is set up (Settings ▸ AI Models: turn on “Use for subagents” for that route)",
            roster.All.Single().Placement);
        Assert.Equal("fallback", roster.All.Single().PlacementReason);

        // Asking for one that exists but is taken is a different sentence.
        var busy = new AgentFleet { UsePrimaryWhenWorkersBusy = true };
        busy.Configure([FleetTestSupport.Server("main", Worker(), primary: true), FleetTestSupport.Server("spark-9", Worker(), max: 1)]);
        using var held = await busy.AcquireAsync("spark-9", default);
        var roster2 = new AgentRoster();
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p","agent_type":"gpu"}""", Context(new ScriptedClient(), busy, roster2, catalog), default);
        Assert.Equal("its agent file asks for “spark-9”, which was busy or not answering", roster2.All.Single().Placement);
    }

    [Fact]
    public async Task WithoutAFleetItRunsOnThisChatsModel()
    {
        var chat = Worker("chat report");
        var roster = new AgentRoster();
        var context = Context(chat, new AgentFleet(), roster) with { Fleet = null };
        await new AgentTool().ExecuteAsync("""{"description":"scan","prompt":"p"}""", context, default);
        Assert.Equal("parent-model on this chat's model", roster.All.Single().PlacementLabel);
    }

    [Theory]
    [InlineData("http://192.168.1.9:8000/v1", "qwen3", true)]          // your own server: it must list what it serves
    [InlineData("https://api.z.ai/api/coding/paas/v4", "glm-5.3", false)] // a hosted plan: its configured model is the answer
    [InlineData("https://api.kimi.com/coding/v1", "kimi-for-coding", false)]
    [InlineData("https://openrouter.ai/api/v1", "", true)]              // no model configured: nothing to trust
    public void OnlyASelfHostedWorkerMustListItsModels(string baseUrl, string model, bool needs) =>
        Assert.Equal(needs, AgentFleet.WorkerNeedsModelList(new ProviderProfile(ProviderKind.OpenAICompat, "w", baseUrl, model)));

    [Fact]
    public void ARunsLogIsBounded()
    {
        var roster = new AgentRoster();
        var id = roster.Begin("d", "general", false);
        for (var i = 0; i < 250; i++) roster.Note(id, $"step {i}", step: true);
        Assert.Equal(AgentRoster.MaxLogLines, roster.Run(id)!.Log.Count);
        Assert.Equal("step 249", roster.Run(id)!.Log[^1].Text);
        roster.SetAssignment(id, "  ");
        Assert.Null(roster.Run(id)!.Assignment);
        roster.SetAssignment(id, "Write tests");
        Assert.Equal("Write tests", roster.Run(id)!.Assignment);
    }
}
