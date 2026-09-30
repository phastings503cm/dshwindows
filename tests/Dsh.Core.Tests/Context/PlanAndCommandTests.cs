namespace Dsh.Core.Tests;

public sealed class PlanExtractorTests
{
    [Fact]
    public void ReadsANumberedPlanWithALeadIn()
    {
        const string text = """
            I'll start by looking at the failing test.

            Here's my plan:
            1. Reproduce the failure with `dotnet test`
            2. Find where the total is computed
            3. **Fix the rounding** in `Invoice.Total`
            4. Add a regression test

            Let me begin.
            """;
        Assert.Equal(["Reproduce the failure with dotnet test", "Find where the total is computed", "Fix the rounding in Invoice.Total", "Add a regression test"],
            PlanExtractor.FromAssistantText(text));
    }

    [Theory]
    [InlineData("## Plan\n- read the config\n- change the port\n- restart the service")]
    [InlineData("**Approach:**\n* read the config\n* change the port\n* restart the service")]
    [InlineData("My plan is:\n\n1) read the config\n2) change the port\n3) restart the service")]
    [InlineData("Next steps:\n- [ ] read the config\n- [ ] change the port\n- [x] restart the service")]
    public void HandlesTheCommonWaysModelsWriteThem(string text) =>
        Assert.Equal(["read the config", "change the port", "restart the service"], PlanExtractor.FromAssistantText(text));

    [Fact]
    public void SubStepsBelongToTheirStep()
    {
        var steps = PlanExtractor.FromAssistantText("Plan:\n1. Set up the project\n   - install packages\n   - create folders\n2. Write the parser\n3. Test it");
        Assert.Equal(["Set up the project", "Write the parser", "Test it"], steps);
    }

    [Fact]
    public void AListWithoutALeadInIsNotAPlan()
    {
        Assert.Empty(PlanExtractor.FromAssistantText("The options are:\n- use a queue\n- use a lock\n- use a semaphore"));
        Assert.Empty(PlanExtractor.FromAssistantText("- one\n- two\n- three"));
    }

    [Fact]
    public void TooFewStepsIsNotAPlan()
    {
        Assert.Empty(PlanExtractor.FromAssistantText("Plan:\n1. do it\n2. done"));
    }

    [Fact]
    public void ProseAfterTheListEndsIt()
    {
        var steps = PlanExtractor.FromAssistantText("Steps:\n1. a step\n2. another step\n3. one more\nThat's all of it, and 4. is not a step.");
        Assert.Equal(3, steps.Count);
    }

    [Fact]
    public void ThinkingBlocksAreIgnored()
    {
        Assert.Empty(PlanExtractor.FromAssistantText("<think>Plan:\n1. a\n2. b\n3. c</think>\nDone."));
    }

    [Fact]
    public void PlanTextFromExitPlanModeIsSplitIntoItsListItems()
    {
        Assert.Equal(["Add the endpoint", "Wire the route", "Cover it with tests"],
            PlanExtractor.FromPlanText("Add a health endpoint.\n\n1. Add the endpoint\n2. Wire the route\n3. Cover it with tests"));
        Assert.Empty(PlanExtractor.FromPlanText("Just do the obvious thing in one go."));
    }

    [Fact]
    public void BoundedLengthAndCount()
    {
        var text = "Plan:\n" + string.Join("\n", Enumerable.Range(1, 60).Select(i => $"{i}. step {i} " + new string('x', 400)));
        var steps = PlanExtractor.FromAssistantText(text);
        Assert.Equal(PlanExtractor.MaxSteps, steps.Count);
        Assert.All(steps, s => Assert.True(s.Length <= 241));
    }
}

public sealed class NewSlashCommandTests
{
    [Fact]
    public void ParseRememberMemoryAndAgents()
    {
        Assert.Equal(new SlashCommand.Remember("the staging server is orion"), SlashCommand.Parse("/remember the staging server is orion"));
        Assert.Equal(new SlashCommand.Remember(""), SlashCommand.Parse("/remember"));
        Assert.Equal(new SlashCommand.Remember("x"), SlashCommand.Parse("/note x"));
        Assert.Equal(new SlashCommand.Memory(null), SlashCommand.Parse("/memory"));
        Assert.Equal(new SlashCommand.Memory("deploy staging"), SlashCommand.Parse("/memory deploy staging"));
        Assert.Equal(new SlashCommand.Agents(), SlashCommand.Parse("/agents"));
        Assert.Equal(new SlashCommand.Agents(), SlashCommand.Parse("/fleet"));
    }

    [Fact]
    public void TheHelpListCoversEveryCommand()
    {
        var usages = SlashCommand.Catalog.Select(c => c.Usage).ToList();
        Assert.Contains(usages, u => u.StartsWith("/remember"));
        Assert.Contains(usages, u => u.StartsWith("/memory"));
        Assert.Contains(usages, u => u == "/agents");
        Assert.Contains(usages, u => u.StartsWith("/goal"));
    }
}

public sealed class PlanEventTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-plan");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task ExitPlanModeSendsThePlanToTheUi()
    {
        var call = new ToolCall("p1", "exit_plan_mode", Args.Json(new { plan = "1. Read\n2. Change\n3. Test" }));
        var client = new ScriptedClient(Turn.Calling(call), new Turn("waiting for approval"));
        var events = new EventRecorder();
        var engine = new Engine(client, ToolRegistry.Standard(), "system", new EngineConfig("test"), _root.Path,
            new PermissionPolicy(PermissionPreset.Plan, _root.Path), Gates.Allow);
        await engine.RunAsync([], "plan it", sink: events.Record);

        var proposed = Assert.Single(events.Of<EngineEvent.PlanProposed>());
        Assert.Equal("1. Read\n2. Change\n3. Test", proposed.Text);
    }

    [Theory]
    [InlineData("All five steps are done.\n\nNext steps:\n1. Deploy it\n2. Tell the team\n3. Close the ticket")]
    [InlineData("Some things you could do next:\n- Add tests\n- Update the docs\n- Tag a release")]
    public void AListOfSuggestionsAtTheEndIsNotAPlan(string reply) => Assert.Empty(PlanExtractor.FromAssistantText(reply));

    [Fact]
    public void AnnouncedStepsAreStillAPlan()
    {
        var steps = PlanExtractor.FromAssistantText("Here's my plan:\n1. Read the config\n2. Change the port\n3. Run the tests");
        Assert.Equal(3, steps.Count);
        Assert.Equal(3, PlanExtractor.FromAssistantText("Steps:\n- read\n- write\n- test").Count);
    }
}
