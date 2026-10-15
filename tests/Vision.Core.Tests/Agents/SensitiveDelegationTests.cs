using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>A task marked sensitive runs only on a private server — never on an online service.</summary>
public sealed class SensitiveDelegationTests : IDisposable
{
    private readonly TempDirectory _root = new("vision-sensitive");

    public void Dispose() => _root.Dispose();

    private static FleetServer S(string id, bool primary = false, bool isPrivate = false, int max = 2, bool interactiveOnly = false) => new()
    {
        Id = id,
        Label = id,
        IsPrimary = primary,
        IsPrivate = isPrivate,
        InteractiveOnly = interactiveOnly,
        MaxParallel = max,
        Resolve = _ => Task.FromResult(new FleetTarget(FleetTestSupport.Reports("report from " + id), "model-" + id, 8_000)),
    };

    [Fact]
    public async Task ASensitiveTaskGoesToAPrivateWorker()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("primary", primary: true), S("kimi"), S("spark-2", isPrivate: true)]);
        var lease = await fleet.AcquireAsync(null, default, sensitive: true);
        Assert.Equal("spark-2", lease.Label);
        // An ordinary task still takes the least busy worker, private or not.
        var ordinary = await fleet.AcquireAsync(null, default);
        Assert.Equal("kimi", ordinary.Label);
    }

    [Fact]
    public async Task WithNoPrivateWorkerItRunsOnAPrivatePrimaryThatMayTakeSubagents()
    {
        var fleet = new AgentFleet { UsePrimaryWhenWorkersBusy = true };
        fleet.Configure([S("spark", primary: true, isPrivate: true), S("kimi")]);
        Assert.Equal("spark", (await fleet.AcquireAsync(null, default, sensitive: true)).Label);
    }

    [Fact]
    public async Task ThePrimaryIsNotUsedWhenItIsKeptForTheMainAgent()
    {
        var fleet = new AgentFleet { UsePrimaryWhenWorkersBusy = false };
        fleet.Configure([S("spark", primary: true, isPrivate: true), S("kimi")]);
        var error = await Assert.ThrowsAsync<NoPrivateServerException>(() => fleet.AcquireAsync(null, default, sensitive: true));
        Assert.Equal("No private server is free for a sensitive task. Do it yourself, or rewrite it without the sensitive details and try again.",
            error.Message);
        // With no workers at all, the only server is the one to use.
        var alone = new AgentFleet { UsePrimaryWhenWorkersBusy = false };
        alone.Configure([S("spark", primary: true, isPrivate: true)]);
        Assert.Equal("spark", (await alone.AcquireAsync(null, default, sensitive: true)).Label);
    }

    [Fact]
    public async Task NoPrivateServerAtAllMeansTheModelIsTold()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("kimi-primary", primary: true), S("openai")]);
        await Assert.ThrowsAsync<NoPrivateServerException>(() => fleet.AcquireAsync(null, default, sensitive: true));
    }

    [Fact]
    public async Task ASensitiveTaskWaitsForABusyPrivateServerRatherThanGoingElsewhere()
    {
        var fleet = new AgentFleet { UsePrimaryWhenWorkersBusy = false };
        fleet.Configure([S("kimi-primary", primary: true), S("spark-2", isPrivate: true, max: 1), S("openai")]);
        var held = await fleet.AcquireAsync(null, default, sensitive: true);
        var waiting = fleet.AcquireAsync(null, default, sensitive: true);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted);
        held.Dispose();
        Assert.Equal("spark-2", (await waiting.WaitAsync(TimeSpan.FromSeconds(3))).Label);
    }

    [Fact]
    public async Task UnattendedWorkKeepsOffInteractiveOnlyPlans()
    {
        // The Qwen Coding Plan as a worker: a chat's subagents may use it, the task queue's may not.
        var fleet = new AgentFleet { UsePrimaryWhenWorkersBusy = true };
        fleet.Configure([S("spark", primary: true, isPrivate: true), S("qwen-coding", interactiveOnly: true)]);
        Assert.Equal("qwen-coding", (await fleet.AcquireAsync(null, default)).Label);
        Assert.Equal("spark", (await fleet.AcquireAsync(null, default, unattended: true)).Label);
        // With no worker it may use, unattended work runs on the primary, as in a fleet without workers…
        var only = new AgentFleet { UsePrimaryWhenWorkersBusy = false };
        only.Configure([S("spark", primary: true), S("qwen-coding", interactiveOnly: true)]);
        Assert.Equal("spark", (await only.AcquireAsync(null, default, unattended: true)).Label);
        // …and with nowhere at all to go, the model is told rather than the plan's terms broken.
        var none = new AgentFleet();
        none.Configure([S("qwen-coding", primary: true, interactiveOnly: true)]);
        var error = await Assert.ThrowsAsync<NoUnattendedServerException>(() => none.AcquireAsync(null, default, unattended: true));
        Assert.Contains("interactive use only", error.Message);
    }

    private ToolContext Context(AgentFleet fleet) => new()
    {
        Workspace = _root.Path,
        Policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path),
        Client = new ScriptedClient(),
        Registry = ToolRegistry.Standard(),
        Model = "parent",
        Fleet = fleet,
        Retry = RetryPolicy.Standard with { Delay = _ => TimeSpan.Zero },
    };

    [Fact]
    public async Task TheAgentToolRoutesASensitiveTaskAndExplainsWhenItCant()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("primary", primary: true), S("kimi"), S("spark-2", isPrivate: true)]);
        var result = await new AgentTool().ExecuteAsync("""{"description":"invoice","prompt":"bill Ann Lee","sensitive":true}""", Context(fleet), default);
        Assert.Contains("report from spark-2", result.Output);

        var hosted = new AgentFleet();
        hosted.Configure([S("primary", primary: true), S("kimi")]);
        var refused = await new AgentTool().ExecuteAsync("""{"description":"invoice","prompt":"bill Ann Lee","sensitive":true}""", Context(hosted), default);
        Assert.Contains("No private server is free for a sensitive task. Do it yourself, or rewrite it without the sensitive details and try again.", refused.Output);
    }

    [Fact]
    public async Task DelegateMarksTasksSensitiveOneByOneOrAllAtOnce()
    {
        var fleet = new AgentFleet();
        fleet.Configure([S("primary", primary: true), S("kimi", max: 4), S("spark-2", isPrivate: true, max: 4)]);
        var one = await new DelegateTool().ExecuteAsync("""
            {"tasks":[{"description":"a","prompt":"general research"},{"description":"b","prompt":"client file","sensitive":true}]}
            """, Context(fleet), default);
        Assert.Contains("report from kimi", one.Output);
        Assert.Contains("report from spark-2", one.Output);

        var all = await new DelegateTool().ExecuteAsync("""
            {"sensitive":true,"tasks":[{"description":"a","prompt":"x"},{"description":"b","prompt":"y"}]}
            """, Context(fleet), default);
        Assert.DoesNotContain("report from kimi", all.Output);
    }

    [Fact]
    public void TheToolsDescribeTheServersAndTheSensitiveParameter()
    {
        IReadOnlyList<SubagentServer> servers = [new("Spark 2", true), new("Kimi Code", false)];
        Assert.Equal("Subagent servers: Spark 2 (private), Kimi Code (not private)", SubagentServers.Describe(servers));
        var agent = new AgentTool(null, servers).Spec;
        Assert.Contains("Subagent servers: Spark 2 (private), Kimi Code (not private).", agent.Description);
        Assert.Contains("Servers that aren't private must not receive sensitive data. Describe their tasks in general terms — no names, credentials, personal or client details; the app also masks what it detects.", agent.Description);
        var schema = JsonNode.Parse(agent.Parameters)!;
        Assert.Equal("boolean", schema["properties"]!["sensitive"]!["type"]!.GetValue<string>());
        Assert.Equal("Set true when the task needs sensitive data (personal data, credentials, client details). It then runs only on a private server.",
            schema["properties"]!["sensitive"]!["description"]!.GetValue<string>());
        var delegateSchema = JsonNode.Parse(new DelegateTool(null, servers).Spec.Parameters)!;
        Assert.Equal("boolean", delegateSchema["properties"]!["tasks"]!["items"]!["properties"]!["sensitive"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", delegateSchema["properties"]!["sensitive"]!["type"]!.GetValue<string>());
        Assert.Contains("Kimi Code (not private)", new DelegateTool(null, servers).Spec.Description);
        // Only private servers: no warning needed.
        Assert.DoesNotContain("must not receive", new AgentTool(null, [new("Spark 2", true)]).Spec.Description);
        Assert.Equal("", SubagentServers.Section([]));
        Assert.DoesNotContain("Subagent servers", new AgentTool().Spec.Description);
    }
}
