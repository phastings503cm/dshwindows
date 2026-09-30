namespace Dsh.Core.Tests;

internal static class FleetTestSupport
{
    public static FleetServer Server(string id, ILlmClient client, bool primary = false, int max = 2, Func<Task<FleetTarget>>? resolve = null) => new()
    {
        Id = id,
        Label = id,
        IsPrimary = primary,
        MaxParallel = max,
        Resolve = _ => resolve is null ? Task.FromResult(new FleetTarget(client, "model-" + id, 8_000)) : resolve(),
    };

    /// <summary>A client that answers every request with a fixed report.</summary>
    public static ScriptedClient Reports(string text) => new() { Intercept = _ => new Turn(text) };
}

public sealed class AgentFleetTests
{
    private static FleetServer S(string id, bool primary = false, int max = 2) =>
        FleetTestSupport.Server(id, FleetTestSupport.Reports(id), primary, max);

    [Fact]
    public async Task ASingleServerIsAConcurrencyLimit()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("spark-1", primary: true, max: 2)]);
        var a = await fleet.AcquireAsync(null, default);
        var b = await fleet.AcquireAsync(null, default);
        var third = fleet.AcquireAsync(null, default);
        await Task.Delay(50);
        Assert.False(third.IsCompleted);
        Assert.Equal(2, fleet.Snapshot()[0].InFlight);

        a.Dispose();
        var lease = await third.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("spark-1", lease.Label);
        b.Dispose();
        lease.Dispose();
        var status = fleet.Snapshot()[0];
        Assert.Equal(0, status.InFlight);
        Assert.Equal(3, status.Completed);
    }

    [Fact]
    public async Task WorkersTakeSubagentsBeforeThePrimary()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("primary", primary: true), S("worker-a", max: 1), S("worker-b", max: 1)]);
        var first = await fleet.AcquireAsync(null, default);
        var second = await fleet.AcquireAsync(null, default);
        Assert.Equal(["worker-a", "worker-b"], new[] { first.Label, second.Label }.Order());
        // Both workers full: the primary takes the overflow.
        var third = await fleet.AcquireAsync(null, default);
        Assert.Equal("primary", third.Label);
    }

    [Fact]
    public async Task ThePrimaryCanBeKeptFreeForTheMainAgent()
    {
        var fleet = new AgentFleet { UsePrimaryWhenWorkersBusy = false };
        fleet.Configure([S("primary", primary: true), S("worker", max: 1)]);
        var held = await fleet.AcquireAsync(null, default);
        var waiting = fleet.AcquireAsync(null, default);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted); // does not spill onto the primary
        held.Dispose();
        Assert.Equal("worker", (await waiting.WaitAsync(TimeSpan.FromSeconds(3))).Label);
    }

    [Fact]
    public async Task TheLeastLoadedWorkerIsChosen()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("a", max: 4), S("b", max: 4)]);
        var leases = new List<FleetLease>();
        for (var i = 0; i < 6; i++) leases.Add(await fleet.AcquireAsync(null, default));
        Assert.Equal(3, leases.Count(l => l.Label == "a"));
        Assert.Equal(3, leases.Count(l => l.Label == "b"));
    }

    [Fact]
    public async Task APreferredServerIsUsedWhenItHasRoom()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("spark-1", primary: true), S("spark-2"), S("spark-3")]);
        Assert.Equal("spark-3", (await fleet.AcquireAsync("SPARK-3", default)).Label);
        // A preference is not a demand: when that server is full, others serve.
        var b = await fleet.AcquireAsync("spark-3", default);
        Assert.Equal("spark-3", b.Label);
        Assert.NotEqual("spark-3", (await fleet.AcquireAsync("spark-3", default)).Label);
    }

    [Fact]
    public async Task AFailedServerIsAvoidedThenForgiven()
    {
        var clock = new ManualClock();
        var fleet = new AgentFleet(clock) { UnhealthyFor = TimeSpan.FromSeconds(60) };
        fleet.Configure([S("primary", primary: true), S("flaky", max: 4)]);
        var lease = await fleet.AcquireAsync(null, default);
        Assert.Equal("flaky", lease.Label);
        lease.Fail(LlmException.Connection("refused"));
        lease.Dispose();

        Assert.False(fleet.Snapshot().Single(s => s.Id == "flaky").Healthy);
        Assert.Equal("primary", (await fleet.AcquireAsync(null, default)).Label);

        clock.AdvanceSeconds(61);
        Assert.True(fleet.Snapshot().Single(s => s.Id == "flaky").Healthy);
        Assert.Equal("flaky", (await fleet.AcquireAsync(null, default)).Label);
    }

    [Fact]
    public async Task ARequestsOwnFaultDoesNotBlacklistAServer()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("only", max: 4)]);
        var lease = await fleet.AcquireAsync(null, default);
        lease.Fail(LlmException.Http(400, "bad request")); // the request was wrong, not the server
        lease.Dispose();
        Assert.True(fleet.Snapshot()[0].Healthy);
    }

    [Fact]
    public async Task AnUnreachableServerIsSkippedAtLeaseTime()
    {
        var fleet = new AgentFleet();
        var dead = FleetTestSupport.Server("dead", new ScriptedClient(), resolve: () => throw LlmException.Connection("no route to host"));
        fleet.Configure([dead, S("alive")]);
        var lease = await fleet.AcquireAsync(null, default);
        Assert.Equal("alive", lease.Label);
        Assert.Equal(0, fleet.Snapshot().Single(s => s.Id == "dead").InFlight); // its slot was given back
        Assert.False(fleet.Snapshot().Single(s => s.Id == "dead").Healthy);
    }

    [Fact]
    public async Task WhenNoServerAnswersTheErrorSaysSo()
    {
        var fleet = new AgentFleet();
        fleet.Configure([FleetTestSupport.Server("dead", new ScriptedClient(), resolve: () => throw LlmException.Connection("refused"))]);
        var error = await Assert.ThrowsAsync<FleetUnavailableException>(() => fleet.AcquireAsync(null, default));
        Assert.Contains("No model server answered", error.Message);
        await Assert.ThrowsAsync<FleetUnavailableException>(() => new AgentFleet().AcquireAsync(null, default));
    }

    [Fact]
    public async Task WaitingIsCancellable()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("only", max: 1)]);
        using var held = await fleet.AcquireAsync(null, default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fleet.AcquireAsync(null, cts.Token));
        Assert.Equal(1, fleet.Snapshot()[0].InFlight);
    }

    [Fact]
    public async Task ReconfiguringKeepsWorkInFlightAndWakesWaiters()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("a", max: 1)]);
        var held = await fleet.AcquireAsync(null, default);
        var waiting = fleet.AcquireAsync(null, default);
        await Task.Delay(30);
        fleet.Configure([S("a", max: 1), S("b", max: 1)]); // a second Spark was added
        Assert.Equal("b", (await waiting.WaitAsync(TimeSpan.FromSeconds(3))).Label);
        Assert.Equal(1, fleet.Snapshot().Single(s => s.Id == "a").InFlight);
        held.Dispose();
    }

    [Fact]
    public async Task ChangedFiresWhenLeasesMove()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("a")]);
        var fired = 0;
        fleet.Changed += () => Interlocked.Increment(ref fired);
        (await fleet.AcquireAsync(null, default)).Dispose();
        Assert.True(fired >= 2);
    }

    [Theory]
    [InlineData(typeof(HttpRequestException), true)]
    [InlineData(typeof(TimeoutException), true)]
    [InlineData(typeof(InvalidOperationException), false)]
    [InlineData(typeof(OperationCanceledException), false)]
    public void ServerFaultClassification(Type type, bool expected) =>
        Assert.Equal(expected, AgentFleet.IsServerFault((Exception)Activator.CreateInstance(type)!));

    [Fact]
    public void LlmErrorsAreClassifiedByKindAndStatus()
    {
        Assert.True(AgentFleet.IsServerFault(LlmException.Connection("x")));
        Assert.True(AgentFleet.IsServerFault(LlmException.Http(503, "busy")));
        Assert.True(AgentFleet.IsServerFault(LlmException.Http(429, "slow down")));
        // Credentials one server rejects may be fine on another.
        Assert.True(AgentFleet.IsServerFault(LlmException.Http(401, "no")));
        Assert.True(AgentFleet.IsServerFault(LlmException.Rejected(403, "denied", "", permanent: true)));
        // A server mid-swap says the model isn't there (or is still loading): the retry policy waits that out, so it is the server's.
        Assert.True(AgentFleet.IsServerFault(LlmException.Http(404, "The model `qwen` does not exist")));
        Assert.True(AgentFleet.IsServerFault(LlmException.Http(400, "model is loading, try again")));
        // The request's own faults follow it wherever it goes.
        Assert.False(AgentFleet.IsServerFault(LlmException.Http(400, "bad")));
        Assert.False(AgentFleet.IsServerFault(LlmException.Http(422, "unprocessable")));
        Assert.False(AgentFleet.IsServerFault(LlmException.NoModel()));
        Assert.False(AgentFleet.IsServerFault(new OperationCanceledException()));
    }

    [Fact]
    public async Task AWaiterIsWokenWhenABlacklistedServerRecovers()
    {
        var fleet = new AgentFleet { UnhealthyFor = TimeSpan.FromMilliseconds(300) };
        fleet.Configure([S("a", max: 1), S("b", max: 1)]);
        var a = await fleet.AcquireAsync("a", default);
        a.Fail(new HttpRequestException("down"));
        a.Dispose(); // a is out of rotation for 300 ms
        var b = await fleet.AcquireAsync("b", default); // and b is busy
        var waiting = fleet.AcquireAsync(null, default);

        // Nothing releases b, yet the waiter must not sit there for ever: a comes back by itself.
        var lease = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("a", lease.Label);
        lease.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task AStoppedLeaseIsNotCountedAsWork()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("a", max: 1)]);
        var lease = await fleet.AcquireAsync(null, default);
        lease.Abandon();
        lease.Dispose();
        var status = fleet.Snapshot()[0];
        Assert.Equal(0, status.InFlight);
        Assert.Equal(0, status.Completed);
    }

    [Fact]
    public async Task AListenerThatThrowsCannotLoseASlot()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("a", max: 1)]);
        fleet.Changed += () => throw new InvalidOperationException("listener bug");
        var lease = await fleet.AcquireAsync(null, default);
        Assert.Equal(1, fleet.Snapshot()[0].InFlight);
        lease.Dispose();
        Assert.Equal(0, fleet.Snapshot()[0].InFlight);
    }

    [Fact]
    public async Task ALaterListenerStillHearsWhenAnEarlierOneThrows()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("a", max: 1)]);
        var heard = 0;
        fleet.Changed += () => throw new InvalidOperationException("first listener bug");
        fleet.Changed += () => Interlocked.Increment(ref heard);
        (await fleet.AcquireAsync(null, default)).Dispose();
        Assert.True(heard >= 2); // granted, then released
    }
}
