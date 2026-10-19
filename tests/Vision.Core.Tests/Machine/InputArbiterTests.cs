namespace Vision.Core.Tests;

/// <summary>The real mouse goes to one chat at a time: first come first served, the holder keeps it for
/// the linger, gives it up after its fair share when others wait, and nobody gets it while the person
/// is using the mouse.</summary>
public sealed class InputArbiterTests
{
    private readonly ManualClock _clock = new();
    private DateTimeOffset? _person;

    private InputArbiter Make(ArbiterTimings? timings = null) => new(timings, _clock, () => _person);

    private static long Grant(InputArbiter arbiter, long request)
    {
        Assert.True(arbiter.TryGrant(request, out var lease, out var reason, out _), $"expected a grant, got {reason}");
        return lease;
    }

    private static (WaitReason Reason, string? Blocker) Refuse(InputArbiter arbiter, long request)
    {
        Assert.False(arbiter.TryGrant(request, out _, out var reason, out var blocker));
        return (reason, blocker);
    }

    [Fact]
    public void TheFirstChatGetsTheMouseAndTheNextWaitsForItsLinger()
    {
        var arbiter = Make();
        var a = arbiter.Enqueue("A", "Fix login");
        var b = arbiter.Enqueue("B", "Build the game");
        var lease = Grant(arbiter, a);
        Assert.Equal((WaitReason.OtherChat, "Fix login"), Refuse(arbiter, b));

        arbiter.Release(lease);
        _clock.AdvanceSeconds(1);
        // A keeps it for the linger after its action…
        Assert.Equal((WaitReason.OtherChat, "Fix login"), Refuse(arbiter, b));
        _clock.AdvanceSeconds(1.6);
        // …then it is free and B is first in line.
        Grant(arbiter, b);
        Assert.Equal("B", arbiter.Snapshot().Holder);
    }

    [Fact]
    public void TheHolderKeepsTheMouseBetweenItsOwnActions()
    {
        var arbiter = Make();
        arbiter.Release(Grant(arbiter, arbiter.Enqueue("A", "a")));
        var b = arbiter.Enqueue("B", "b");
        _clock.AdvanceSeconds(1);
        var again = arbiter.Enqueue("A", "a");
        // B asked first, but A is mid-sequence (inside its linger): A goes on.
        Grant(arbiter, again);
        Assert.Equal(WaitReason.OtherChat, Refuse(arbiter, b).Reason);
    }

    [Fact]
    public void AfterItsFairShareTheHolderGivesTheMouseUpAtItsNextRelease()
    {
        var arbiter = Make();
        var lease = Grant(arbiter, arbiter.Enqueue("A", "a"));
        var b = arbiter.Enqueue("B", "b");
        _clock.AdvanceSeconds(21);
        arbiter.Release(lease);
        // No linger once others have waited out A's fair share.
        Grant(arbiter, b);
        var a = arbiter.Enqueue("A", "a");
        Assert.Equal((WaitReason.OtherChat, "b"), Refuse(arbiter, a));
    }

    [Fact]
    public void AHolderPastItsFairShareYieldsWhenItAsksAgain()
    {
        var arbiter = Make();
        var first = Grant(arbiter, arbiter.Enqueue("A", "a"));
        _clock.AdvanceSeconds(19);
        arbiter.Release(first); // released before its share was up: it lingers
        var b = arbiter.Enqueue("B", "b");
        _clock.AdvanceSeconds(1.5);
        var again = arbiter.Enqueue("A", "a");
        // 20.5 s in, B waiting: A yields instead of starting another action, and goes to the back.
        Assert.Equal((WaitReason.OtherChat, "b"), Refuse(arbiter, again));
        Grant(arbiter, b);
    }

    [Fact]
    public void NobodyGetsTheMouseWhileThePersonUsesIt()
    {
        var arbiter = Make();
        var a = arbiter.Enqueue("A", "a");
        _person = _clock.GetUtcNow();
        Assert.Equal((WaitReason.Person, null), Refuse(arbiter, a));
        Assert.True(arbiter.Snapshot().PersonActive);
        _clock.AdvanceSeconds(1.9);
        Assert.Equal(WaitReason.Person, Refuse(arbiter, a).Reason);
        _clock.AdvanceSeconds(0.2);
        Grant(arbiter, a);
        Assert.False(arbiter.Snapshot().PersonActive);

        // The holder too: its next action waits for the person.
        _person = _clock.GetUtcNow();
        var next = arbiter.Enqueue("A", "a");
        Assert.Equal(WaitReason.Person, Refuse(arbiter, next).Reason);
    }

    [Fact]
    public void TwoStepsOfOneChatTakeTurns()
    {
        var arbiter = Make();
        Grant(arbiter, arbiter.Enqueue("A", "a"));
        var second = arbiter.Enqueue("A", "a");
        Assert.Equal(WaitReason.OwnAction, Refuse(arbiter, second).Reason);
    }

    [Fact]
    public void WaitersAreServedInOrder()
    {
        var arbiter = Make();
        arbiter.Release(Grant(arbiter, arbiter.Enqueue("A", "a")));
        var b = arbiter.Enqueue("B", "b");
        var c = arbiter.Enqueue("C", "c");
        _clock.AdvanceSeconds(3);
        Assert.Equal((WaitReason.OtherChat, "b"), Refuse(arbiter, c));
        Grant(arbiter, b);
        var snapshot = arbiter.Snapshot();
        Assert.Equal("B", snapshot.Holder);
        Assert.True(snapshot.HolderBusy);
        Assert.Equal([new WaitingChat("C", "c")], snapshot.Waiting);
    }

    [Fact]
    public void ASnapshotListsEachWaitingChatOnceAndNotTheHolder()
    {
        var arbiter = Make();
        Grant(arbiter, arbiter.Enqueue("A", "a"));
        arbiter.Enqueue("A", "a");
        arbiter.Enqueue("B", "b");
        var b2 = arbiter.Enqueue("B", "b");
        var snapshot = arbiter.Snapshot();
        Assert.Equal([new WaitingChat("B", "b")], snapshot.Waiting);
        arbiter.Cancel(b2);
        Assert.Single(arbiter.Snapshot().Waiting);
    }

    [Fact]
    public void AStaleReleaseChangesNothing()
    {
        var arbiter = Make();
        var first = Grant(arbiter, arbiter.Enqueue("A", "a"));
        arbiter.Release(first);
        var second = Grant(arbiter, arbiter.Enqueue("A", "a"));
        arbiter.Release(first); // the first action's lease, released twice
        Assert.True(arbiter.Snapshot().HolderBusy);
        arbiter.Release(second);
        Assert.False(arbiter.Snapshot().HolderBusy);
    }

    [Fact]
    public void TheHolderLapsesAfterTheLinger()
    {
        var arbiter = Make();
        arbiter.Release(Grant(arbiter, arbiter.Enqueue("A", "Fix login")));
        Assert.Equal("Fix login", arbiter.Snapshot().HolderTitle);
        _clock.AdvanceSeconds(2.5);
        var snapshot = arbiter.Snapshot();
        Assert.Null(snapshot.Holder);
        Assert.Null(snapshot.HolderTitle);
        Assert.False(snapshot.HolderBusy);
    }

    // MARK: Waiting for real

    private static readonly ArbiterTimings Quick = new()
    {
        Linger = TimeSpan.FromMilliseconds(30),
        FairShare = TimeSpan.FromSeconds(5),
        PersonQuiet = TimeSpan.FromMilliseconds(200),
        Poll = TimeSpan.FromMilliseconds(10),
        MaxWaitForChats = TimeSpan.FromSeconds(10),
        MaxWaitForPerson = TimeSpan.FromMilliseconds(300),
    };

    [Fact]
    public async Task TwoChatsNeverHoldTheMouseAtOnce()
    {
        var arbiter = new InputArbiter(Quick);
        var inside = 0;
        var overlaps = 0;
        var heard = new List<(string Chat, WaitReason Reason)>();

        async Task Act(string chat)
        {
            for (var i = 0; i < 4; i++)
            {
                using var lease = await arbiter.AcquireAsync(chat, chat, (reason, _) => { lock (heard) heard.Add((chat, reason)); },
                                                             CancellationToken.None);
                if (Interlocked.Increment(ref inside) > 1) Interlocked.Increment(ref overlaps);
                await Task.Delay(15);
                Interlocked.Decrement(ref inside);
            }
        }

        await Task.WhenAll(Act("A"), Act("B"), Act("C"));
        Assert.Equal(0, overlaps);
        // Whoever waited heard why, and heard the wait end.
        lock (heard)
        {
            Assert.Contains(heard, h => h.Reason == WaitReason.OtherChat);
            foreach (var chat in heard.Select(h => h.Chat).Distinct()) Assert.Equal(WaitReason.None, heard.Last(h => h.Chat == chat).Reason);
        }
        Assert.Empty(arbiter.Snapshot().Waiting);
    }

    [Fact]
    public async Task AWaitForAPersonWhoKeepsGoingGivesUpWithAMessageForTheModel()
    {
        var arbiter = new InputArbiter(Quick, lastPersonInput: () => DateTimeOffset.UtcNow);
        var error = await Assert.ThrowsAsync<InputWaitException>(() => arbiter.AcquireAsync("A", "a", null, CancellationToken.None));
        Assert.Equal(MachineText.PersonTimeout(Quick.MaxWaitForPerson), error.Message);
        Assert.StartsWith("Error: waited 0 s for the person", error.Message);
        Assert.Empty(arbiter.Snapshot().Waiting);
    }

    [Fact]
    public async Task ACancelledWaitLeavesTheLine()
    {
        var arbiter = new InputArbiter(Quick);
        using var held = await arbiter.AcquireAsync("A", "a", null, CancellationToken.None);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(60));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => arbiter.AcquireAsync("B", "b", null, cancel.Token));
        Assert.Empty(arbiter.Snapshot().Waiting);
    }

    [Fact]
    public async Task AnActionCanWaitMidwayForThePersonToStop()
    {
        var last = DateTimeOffset.UtcNow;
        var arbiter = new InputArbiter(Quick, lastPersonInput: () => last);
        var reasons = new List<WaitReason>();
        Assert.True(await arbiter.WaitForPersonAsync((r, _) => reasons.Add(r), CancellationToken.None));
        Assert.Equal([WaitReason.Person, WaitReason.None], reasons);
        Assert.True(await arbiter.WaitForPersonAsync(null, CancellationToken.None)); // quiet now: no wait at all
    }
}
