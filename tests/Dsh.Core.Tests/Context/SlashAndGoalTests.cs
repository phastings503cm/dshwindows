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
        var c = GoalProtocol.Continuation("fix the build", 3, hitIterationLimit: true);
        Assert.Contains("fix the build", c);
        Assert.Contains("round 3", c);
        Assert.Contains("cut off", c);
        // No round budget: the model is never told it has N rounds.
        Assert.DoesNotContain("round 3 of", c);
        var e = GoalProtocol.Continuation("fix the build", 4, hitIterationLimit: false, error: "HTTP 400: bad");
        Assert.Contains("HTTP 400: bad", e);
        Assert.Contains("round 4", e);
        // Prompts themselves must never read as a verdict if echoed.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(c));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(e));
    }

    [Fact]
    public void GoalMarkerVariantsModelsActuallyWrite()
    {
        // Marker first, summary after — the loop must not spin forever on it.
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("GOAL_COMPLETE\n\nSummary:\n- fixed a\n- fixed b\n- fixed c\n- tests pass"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All green.\n\u2705 GOAL_COMPLETE"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All green.\n`GOAL_COMPLETE`."));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All green.\n## GOAL_COMPLETE"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All green.\nStatus: GOAL_COMPLETE"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All green.\nGOAL COMPLETE"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All green.\n<GOAL_COMPLETE>"));
        Assert.Equal(new GoalStatus.Blocked("which database should I use"),
            GoalProtocol.Status("Stuck.\n**GOAL_BLOCKED**: which database should I use?"));
        Assert.Equal(new GoalStatus.Blocked("The agent needs your input."), GoalProtocol.Status("Stuck.\nGOAL_BLOCKED"));
        // The last marker line wins.
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("GOAL_BLOCKED: need a key\nFound it in .env after all.\nGOAL_COMPLETE"));
        // Prose around the marker is not a marker.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Once the tests pass I will print GOAL_COMPLETE."));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("I am not GOAL_BLOCKED yet, continuing."));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("This is not a GOAL_COMPLETE situation"));
        // A marker inside a code block (e.g. echoing source) doesn't count.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Here's the file:\n```\nGOAL_COMPLETE\n```\nStill working."));
    }

    [Fact]
    public void MarkdownLabelledMarkersFromReview()
    {
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All done.\n**Status:** GOAL_COMPLETE"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All done.\nStatus: **GOAL_COMPLETE**"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("All done.\nFinal status: `GOAL_COMPLETE`"));
        Assert.Equal(new GoalStatus.Blocked("need the API key"), GoalProtocol.Status("Stuck.\n**Status:** GOAL_BLOCKED: need the API key"));
        // Status reports and recaps are not a verdict.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Checklist:\n- tests: pass\n- GOAL_BLOCKED: no"));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Goal blocked: no\nContinuing with the parser."));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Resuming.\nLast round ended with:\nGOAL_BLOCKED: need the DB password\nYou gave it above, so I ran the migration.\nNext: seed data.\nThen the API."));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Recap:\n- Previously: GOAL_BLOCKED on the DB password (answered)"));
        // A one-line code span doesn't swallow the rest of the reply.
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("```sh build```\nAll green.\nGOAL_COMPLETE"));
        // Thinking left inline by a server without a reasoning parser.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("<think>\nWhen everything passes I end with:\nGOAL_COMPLETE\nBut 3 tests fail.\n</think>\nThree tests still fail; fixing next."));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("<think>checking</think>\nAll verified.\nGOAL_COMPLETE"));
        // Buried markers are noticed so the next round can ask for them plainly.
        const string buried = "All tests pass.\nGOAL_COMPLETE\n\nChanges:\n- a\n- b\n- c";
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(buried));
        Assert.True(GoalProtocol.MentionsMarker(buried));
        Assert.Contains("did not count", GoalProtocol.Continuation("g", 2, hitIterationLimit: false, markerMisplaced: true));
    }

    [Fact]
    public void AutoGoalProtocol()
    {
        var k = GoalProtocol.KickoffAuto("refactor the parser");
        Assert.Contains("refactor the parser", k);
        Assert.Contains("GOAL_COMPLETE", k);
        Assert.Contains("unattended", k);
        // Auto prompts must not promise to ask the user for confirmation.
        Assert.DoesNotContain("Stop it any time", k);
        var c = GoalProtocol.ContinuationAuto("refactor the parser", 2, hitIterationLimit: false);
        Assert.Contains("round 2", c);
        Assert.Contains("unattended", c);
        // Blocked marker still recognized after the auto reminder.
        Assert.Equal(new GoalStatus.Blocked("need the API key"), GoalProtocol.Status(c + "\nGOAL_BLOCKED: need the API key"));
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("done\nGOAL_COMPLETE"));
        var r = GoalProtocol.ResumeAuto("refactor the parser");
        Assert.StartsWith("[Resuming]", r);
        Assert.Contains("refactor the parser", r);
        Assert.Contains("unattended", r);
        Assert.StartsWith("[Resuming]", GoalProtocol.Resume("x"));
        Assert.DoesNotContain("unattended", GoalProtocol.Resume("x"));
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
        Assert.Equal(new SlashCommand.Queue(), SlashCommand.Parse("/queue"));
        Assert.Null(SlashCommand.Parse("/unknown thing"));
        Assert.Null(SlashCommand.Parse(@"C:\Users\me is not a command"));
    }

    [Fact]
    public void CatalogListsEveryCommand()
    {
        var usages = SlashCommand.Catalog.Select(i => i.Usage.Split(' ')[0]).ToList();
        foreach (var command in new[] { "/goal", "/compact", "/think", "/context", "/swap", "/skills", "/skill", "/queue", "/help" })
            Assert.Contains(command, usages);
    }

    [Fact]
    public void MarkersAreCaseInsensitiveAndSurviveCrlf()
    {
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("Verified.\r\ngoal_complete\r\n"));
        Assert.Equal(new GoalStatus.Blocked("The agent needs your input."), GoalProtocol.Status("GOAL_BLOCKED"));
        // Trailing punctuation is decoration, like markdown.
        Assert.Equal(new GoalStatus.Blocked("which port"), GoalProtocol.Status("> GOAL_BLOCKED: which port?"));
        // Only the closing lines and the very first line count: a marker buried mid-reply doesn't.
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status("Plan\nGOAL_COMPLETE\nstill\nworking\non it"));
    }

    [Fact]
    public void ContinuationExplainsWhyTheRoundContinues()
    {
        var notDone = GoalProtocol.Continuation("ship it", 2, hitIterationLimit: false);
        Assert.Contains("[Goal round 2]", notDone);
        Assert.Contains("not declared the goal complete", notDone);
        Assert.Contains(GoalProtocol.CompleteMarker, notDone);
        Assert.Contains(GoalProtocol.BlockedMarker, GoalProtocol.Kickoff("ship it"));
    }
}
