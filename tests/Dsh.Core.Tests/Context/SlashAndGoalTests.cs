namespace Dsh.Core.Tests;

/// <summary>Ported from the SlashAndGoalTests class in ThinkingAndGoalTests.swift.</summary>
public sealed class SlashAndGoalTests
{
    [Fact]
    public void Parse()
    {
        Assert.Equal(new SlashCommand.Compact(null), SlashCommand.Parse("/compact"));
        Assert.Equal(new SlashCommand.Compact("keep the API design"), SlashCommand.Parse("/compact keep the API design"));
        Assert.Equal(new SlashCommand.Goal("ship the build"), SlashCommand.Parse("  /goal ship the build  "));
        Assert.Equal(new SlashCommand.Think("high"), SlashCommand.Parse("/think high"));
        Assert.Equal(new SlashCommand.Context(), SlashCommand.Parse("/context"));
        Assert.Null(SlashCommand.Parse("/Users/me/project/file.swift is broken"));
        Assert.Null(SlashCommand.Parse("please /compact later"));
    }

    [Fact]
    public void GoalMarkers()
    {
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All tests pass.\n\nGOAL_COMPLETE"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("Done.\n**GOAL_COMPLETE**\n"));
        Assert.Equal(new GoalStatus.Blocked("the OpenAI API key"),
            GoalProtocol.Status("Need a key.\nGOAL_BLOCKED: the OpenAI API key"));
        // Mentioning the protocol mid-reply is not completion.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(
            "I'll write GOAL_COMPLETE when everything is verified.\nNext I will run the tests."));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(""));
    }

    [Fact]
    public void GoalPromptsCarryTheGoal()
    {
        Assert.Contains("fix the build", GoalProtocol.Kickoff("fix the build"));
        var c = GoalProtocol.Continuation("fix the build", 3, 40, hitIterationLimit: true);
        Assert.Contains("fix the build", c);
        Assert.Contains("round 3", c);
        Assert.Contains("cut off", c);
    }
}

/// <summary>New: aliases, arguments and marker edge cases (CRLF replies from Windows tools).</summary>
public sealed class SlashCommandParsingTests
{
    [Fact]
    public void AliasesAndCaseAreAccepted()
    {
        Assert.Equal(new SlashCommand.Compact(null), SlashCommand.Parse("/SUMMARIZE"));
        Assert.Equal(new SlashCommand.Think(null), SlashCommand.Parse("/effort"));
        Assert.Equal(new SlashCommand.Context(), SlashCommand.Parse("/ctx"));
        Assert.Equal(new SlashCommand.Swap("flash"), SlashCommand.Parse("/model flash"));
        Assert.Equal(new SlashCommand.Help(), SlashCommand.Parse("/?"));
        Assert.Equal(new SlashCommand.Skills(), SlashCommand.Parse("/skills"));
        Assert.Equal(new SlashCommand.Skill("new write release notes"), SlashCommand.Parse("/skill new write release notes"));
        Assert.Equal(new SlashCommand.Goal(""), SlashCommand.Parse("/goal"));
        Assert.Null(SlashCommand.Parse("/unknown thing"));
        Assert.Null(SlashCommand.Parse(@"C:\Users\me is not a command"));
    }

    [Fact]
    public void CatalogListsEveryCommand()
    {
        var usages = SlashCommand.Catalog.Select(i => i.Usage.Split(' ')[0]).ToList();
        foreach (var command in new[] { "/goal", "/compact", "/think", "/context", "/swap", "/skills", "/skill", "/help" })
            Assert.Contains(command, usages);
    }

    [Fact]
    public void MarkersAreCaseInsensitiveAndSurviveCrlf()
    {
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("Verified.\r\ngoal_complete\r\n"));
        Assert.Equal(new GoalStatus.Blocked("The agent needs your input."), GoalProtocol.Status("GOAL_BLOCKED"));
        Assert.Equal(new GoalStatus.Blocked("which port?"), GoalProtocol.Status("> GOAL_BLOCKED: which port?"));
        // Only the last three non-empty lines count.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("GOAL_COMPLETE\nstill\nworking\non it"));
    }

    [Fact]
    public void ContinuationExplainsWhyTheRoundContinues()
    {
        var notDone = GoalProtocol.Continuation("ship it", 2, 40, hitIterationLimit: false);
        Assert.Contains("[Goal round 2 of 40]", notDone);
        Assert.Contains("not declared the goal complete", notDone);
        Assert.Contains(GoalProtocol.CompleteMarker, notDone);
        Assert.Contains(GoalProtocol.BlockedMarker, GoalProtocol.Kickoff("ship it"));
    }
}
