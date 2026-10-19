namespace Vision.Core.Tests;

/// <summary>A chat's machine session lasts its whole run (with the Mac's 2.5 s linger), ends early when
/// no machine tool has run for 30 s, and the glow's wording names who does what.</summary>
public sealed class MachineActivityTests
{
    private readonly ManualClock _clock = new();

    [Fact]
    public void ASessionStartsWithTheFirstMachineToolAndLastsTheRun()
    {
        var activity = new MachineActivity(_clock);
        var started = 0;
        activity.Started += () => started++;

        using (activity.Begin("A", "Fix login")) { }
        _clock.AdvanceSeconds(12); // the model thinks between two clicks
        Assert.Equal(["A"], activity.Sessions().Select(s => s.ChatId));
        using (activity.Begin("A", "Fix login")) { }
        using (activity.Begin("B", "Build the game")) { }
        Assert.Equal(1, started); // the glow was already up for B

        activity.EndRun("A");
        _clock.AdvanceSeconds(2.4);
        Assert.True(activity.IsActive("A"));
        _clock.AdvanceSeconds(0.1);
        Assert.False(activity.IsActive("A"));
        Assert.Equal(["B"], activity.Sessions().Select(s => s.ChatId));
    }

    [Fact]
    public void ARunThatStoppedUsingTheMachineLetsTheGlowGo()
    {
        var activity = new MachineActivity(_clock);
        using (activity.Begin("A", "a")) { }
        _clock.AdvanceSeconds(29.9);
        Assert.True(activity.IsActive("A"));
        _clock.AdvanceSeconds(0.1);
        Assert.Empty(activity.Sessions());
    }

    [Fact]
    public void ALongToolKeepsTheSessionAlive()
    {
        var activity = new MachineActivity(_clock);
        var use = activity.Begin("A", "a");
        activity.EndRun("A");
        _clock.AdvanceSeconds(120); // waiting two minutes for the mouse
        var session = Assert.Single(activity.Sessions());
        Assert.True(session.ToolRunning);
        use.Dispose();
        use.Dispose(); // twice is once
        _clock.AdvanceSeconds(2.5);
        Assert.Empty(activity.Sessions());
    }

    [Fact]
    public void AToolAfterTheRunEndedRevivesTheSessionAndStartRaisesAgainAfterAGap()
    {
        var activity = new MachineActivity(_clock);
        var started = 0;
        activity.Started += () => started++;
        using (activity.Begin("A", "a")) { }
        activity.EndRun("A");
        _clock.AdvanceSeconds(1);
        using (activity.Begin("A", "renamed")) { } // the next turn began inside the linger
        _clock.AdvanceSeconds(5);
        Assert.Equal("renamed", Assert.Single(activity.Sessions()).Title);
        Assert.Equal(1, started);

        activity.Clear();
        Assert.Empty(activity.Sessions());
        using (activity.Begin("A", "a")) { }
        Assert.Equal(2, started);
    }

    // MARK: Wording (the Mac shows the same sentences)

    private static MachineSessionInfo S(string id, string title) => new(id, title, ManualClock.Epoch, ManualClock.Epoch, false);

    [Fact]
    public void NoSessionNoGlow() => Assert.Null(MachineText.Glow([], ArbiterSnapshot.Idle));

    [Fact]
    public void OneChatInTheBackground()
    {
        var text = MachineText.Glow([S("A", "Fix login")], ArbiterSnapshot.Idle)!;
        Assert.Equal("Machine in use by Vision AI", text.Headline);
        Assert.Equal("“Fix login” is working — your mouse is free", text.Detail); // the Mac's words
        Assert.Equal("Ctrl+Alt+. stops", text.Hint);
    }

    [Fact]
    public void SeveralChatsInTheBackground()
    {
        Assert.Equal("2 chats are working — your mouse is free",
                     MachineText.Glow([S("A", "Fix login"), S("B", "b")], ArbiterSnapshot.Idle)!.Detail);
        Assert.Equal("3 chats are working — your mouse is free",
                     MachineText.Glow([S("A", "Fix login"), S("B", "b"), S("C", "c")], ArbiterSnapshot.Idle)!.Detail);
    }

    [Fact]
    public void TheChatWithTheMouseIsNamed()
    {
        var input = new ArbiterSnapshot("B", "Build the game", true, [new WaitingChat("A", "Fix login")], false);
        Assert.Equal("“Build the game” has the mouse and keyboard · 1 waiting", MachineText.Glow([S("A", "Fix login"), S("B", "Build the game")], input)!.Detail);
        Assert.Equal("“Build the game” has the mouse and keyboard", MachineText.Glow([S("B", "Build the game")], input with { Waiting = [] })!.Detail);
        Assert.Equal("“Fix login” is waiting for the mouse",
                     MachineText.Glow([S("A", "Fix login")], input with { Holder = null, HolderTitle = null })!.Detail);
    }

    [Fact]
    public void ThePersonPausesTheRealMouse()
    {
        var input = new ArbiterSnapshot("B", "b", false, [], PersonActive: true);
        Assert.Equal("“b” is waiting for you to finish with the mouse", MachineText.Glow([S("B", "b")], input)!.Detail);
        Assert.Equal("“a” is waiting for you to finish with the mouse",
                     MachineText.Glow([S("A", "a")], new ArbiterSnapshot(null, null, false, [new WaitingChat("A", "a")], PersonActive: true))!.Detail);
        // Background work isn't paused by the person, so the glow doesn't claim it is.
        Assert.EndsWith("your mouse is free", MachineText.Glow([S("B", "b")], ArbiterSnapshot.Idle with { PersonActive = true })!.Detail);
    }

    [Fact]
    public void LongAndEmptyTitlesAreQuotedTidily()
    {
        Assert.Equal("“a chat”", MachineText.Quote("  "));
        Assert.Equal("“One two”", MachineText.Quote("One\n  two"));
        var quoted = MachineText.Quote(new string('x', 60));
        Assert.Equal(MachineText.TitleLimit + 2, quoted.Length);
        Assert.EndsWith("x…”", quoted);
    }

    [Fact]
    public void WaitingLines()
    {
        Assert.Equal("Waiting for the mouse — “Fix login” is using it. It goes to the next chat when that one is done; Ctrl+Alt+. stops all control.",
                     MachineText.Waiting(WaitReason.OtherChat, "Fix login"));
        Assert.Equal("Waiting for you to finish with the mouse and keyboard…", MachineText.Waiting(WaitReason.Person, null));
        Assert.Equal("Waiting for the mouse — another task in this chat is using it.", MachineText.Waiting(WaitReason.OwnAction, "x"));
        Assert.Null(MachineText.Waiting(WaitReason.None, null));
        Assert.StartsWith("Error: waited 5 minutes for the mouse, and “b” is still using it.", MachineText.ChatTimeout(TimeSpan.FromMinutes(5), "b"));
        Assert.StartsWith("Error: waited 2 minutes for the person", MachineText.PersonTimeout(TimeSpan.FromMinutes(2)));
    }
}
