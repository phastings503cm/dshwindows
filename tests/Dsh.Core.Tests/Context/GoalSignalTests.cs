namespace Dsh.Core.Tests;

public sealed class GoalSignalTests
{
    [Fact]
    public void KickoffOffersTheSignalToolsAndTheTextFallback()
    {
        var k = GoalProtocol.Kickoff("ship the release");
        Assert.Contains("goal_complete", k);
        Assert.Contains("goal_blocked", k);
        Assert.Contains("GOAL_COMPLETE", k);
        Assert.Contains("GOAL_BLOCKED", k);
        // The model is told not to ask whether to keep going.
        Assert.Contains("never stop to ask whether you should continue", k);
    }

    [Fact]
    public void PromptsDoNotReadAsVerdictsWhenEchoed()
    {
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(GoalProtocol.Kickoff("g")));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(GoalProtocol.Continuation("g", 2, false)));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(GoalProtocol.Continuation("g", 2, false, stalled: true)));
        Assert.Equal(new GoalStatus.Working(), GoalProtocol.Status(GoalProtocol.ResumeWithReply("g", "use the staging DB")));
    }

    [Fact]
    public void ContinuationExplainsAStall()
    {
        var c = GoalProtocol.Continuation("fix it", 5, hitIterationLimit: false, stalled: true);
        Assert.Contains("same call", c);
        Assert.Contains("different approach", c);
        Assert.Contains("goal_complete", c);
        var auto = GoalProtocol.ContinuationAuto("fix it", 5, false, stalled: true);
        Assert.Contains("unattended", auto);
        Assert.Contains("same call", auto);
    }

    [Fact]
    public void ResumeWithReplyHandsOverTheAnswer()
    {
        var text = GoalProtocol.ResumeWithReply("migrate the database", "  Use the staging server.\n");
        Assert.StartsWith("[Resuming]", text);
        Assert.Contains("<user_reply>\nUse the staging server.\n</user_reply>", text);
        Assert.Contains("GOAL: migrate the database", text);
        Assert.Contains("do not start over", text);
    }

    [Fact]
    public void ACompleteVerdictKeepsItsSummaryOutOfEquality()
    {
        // Markers carry no summary; equality with a bare Complete still holds for them.
        Assert.Equal(new GoalStatus.Complete(), GoalProtocol.Status("done\nGOAL_COMPLETE"));
        Assert.NotEqual(new GoalStatus.Complete(), new GoalStatus.Complete { Summary = "did it" });
    }

    [Fact]
    public async Task TheSignalToolsReturnVerdicts()
    {
        using var t = new ToolTestContext();
        var done = await t.Run(new GoalCompleteTool(), """{"summary":"  shipped  "}""");
        Assert.Equal(new GoalStatus.Complete { Summary = "shipped" }, done.Goal);
        var blank = await t.Run(new GoalCompleteTool(), "{}");
        Assert.Equal(new GoalStatus.Complete(), blank.Goal);
        var stuck = await t.Run(new GoalBlockedTool(), """{"reason":"need the password"}""");
        Assert.Equal(new GoalStatus.Blocked("need the password"), stuck.Goal);
        var vague = await t.Run(new GoalBlockedTool(), "{}");
        Assert.Equal(new GoalStatus.Blocked("The agent needs your input."), vague.Goal);
    }

    [Fact]
    public void SignalToolsNeverGetVaultValues()
    {
        // A summary or reason is prose the harness shows the user and logs; it must keep any placeholder.
        Assert.False(VaultPlaceholders.SubstitutesInto("goal_complete"));
        Assert.False(VaultPlaceholders.SubstitutesInto("goal_blocked"));
        Assert.False(VaultPlaceholders.SubstitutesInto("memory_save"));
        Assert.False(VaultPlaceholders.SubstitutesInto("delegate"));
    }
}
