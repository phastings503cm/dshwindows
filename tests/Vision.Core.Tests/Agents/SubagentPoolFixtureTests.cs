using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>Item 21's shared fixtures (Fixtures/SubagentPool, byte-identical with Vision for macOS): the same handles,
/// labels, pools, placements, tool wording, `server` matching and Claude Code report on both apps. The manifest test fails
/// when a file here differs from the copy the Mac checks.</summary>
public sealed class SubagentPoolFixtureTests
{
    private static string Folder => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SubagentPool");

    private static JsonObject Load(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder, name)))!.AsObject();

    private static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Num(JsonNode? node) => node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    private static bool Bool(JsonNode? node, bool fallback = false) => node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static DateTimeOffset? Time(JsonNode? node) => Num(node) is { } seconds ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(seconds * 1000)) : null;

    private static SubagentRouteFacts Facts(JsonNode node) => node.Deserialize<SubagentRouteFacts>()!;

    private static SubagentTier TierOf(string word) => Enum.GetValues<SubagentTier>().Single(t => t.Word() == word);

    private static SubagentSpeed SpeedOf(string word) => Enum.GetValues<SubagentSpeed>().Single(s => s.Word() == word);

    /// <summary>A pool as the fixture writes one: its entries (and what it dropped) as they are.</summary>
    private static SubagentPool PoolOf(JsonObject pool)
    {
        var entries = pool["entries"]!.AsArray().Select(n => n!.AsObject()).Select(e => new SubagentPool.Entry
        {
            Id = Str(e["id"])!,
            RouteId = Str(e["routeID"])!,
            Label = Str(e["label"])!,
            Provider = Str(e["provider"])!,
            Model = Str(e["model"])!,
            Context = Str(e["context"]),
            IsPrivate = Bool(e["private"]),
            Tier = TierOf(Str(e["tier"])!),
            Speed = SpeedOf(Str(e["speed"])!),
            Images = Bool(e["images"]),
            OwnAgent = Bool(e["ownAgent"]),
            Parallel = (int)Num(e["parallel"])!.Value,
            Machine = Str(e["machine"]) ?? "route:" + Str(e["routeID"]),
            Kind = Bool(e["ownAgent"]) ? SubagentKind.ClaudeCode : SubagentKind.OpenAICompatible,
            IsPrimary = Bool(e["primary"]),
        }).ToList();
        var dropped = (pool["dropped"] as JsonArray ?? []).Select(n => n!.AsObject())
            .Select(d => new SubagentPool.DroppedRoute(Str(d["id"])!, Str(d["routeID"]) ?? "", Str(d["label"]) ?? "", Str(d["reason"])!)).ToList();
        return new SubagentPool(entries, dropped, Bool(pool["overflow"], true));
    }

    [Fact]
    public void TheFixturesAreTheOnesInTheManifest()
    {
        var listed = File.ReadAllLines(Path.Combine(Folder, "MANIFEST.sha256")).Where(l => l.Trim().Length > 0).Select(line =>
        {
            var space = line.IndexOf(' ');
            return (Hash: line[..space], Name: line[space..].Trim().TrimStart('*'));
        }).ToList();
        foreach (var (hash, name) in listed)
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(Folder, name))));
            Assert.True(hash == actual, $"{name} differs from the manifest (the Mac's copy): copy the folder from vision-program/bowl/subagent-pool-fixtures.");
        }
        var present = Directory.GetFiles(Folder).Select(f => Path.GetFileName(f)).Where(n => n != "MANIFEST.sha256").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(listed.Select(l => l.Name).Order(StringComparer.Ordinal).ToList(), present);
    }

    [Fact]
    public void LabelsHandlesMachinesAndRetryWords()
    {
        var labels = Load("labels.json");
        foreach (var c in labels["handles"]!.AsArray().Select(n => n!.AsObject()))
        {
            var routes = c["routes"]!.AsArray().Select(r => Facts(r!)).ToList();
            Assert.True(c["expect"]!.AsArray().Select(Str).SequenceEqual(SubagentPool.Handles(routes)),
                $"{Str(c["name"])}: {string.Join(", ", SubagentPool.Handles(routes))}");
        }
        foreach (var c in labels["context"]!.AsArray().Select(n => n!.AsObject()))
            Assert.Equal(Str(c["expect"]), SubagentPool.ContextLabel((int)Num(c["tokens"])!.Value));
        foreach (var c in labels["speed"]!.AsArray().Select(n => n!.AsObject()))
            Assert.True(Str(c["expect"]) == SubagentPool.Speed(Str(c["model"])!).Word(), Str(c["model"]));
        foreach (var c in labels["tier"]!.AsArray().Select(n => n!.AsObject()))
        {
            var kind = SubagentWords.KindOf(Str(c["kind"]))!.Value;
            bool? plan = c["planSubscription"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
            Assert.Equal(Str(c["expect"]), SubagentPool.Tier(kind, plan, Bool(c["privateAddress"])).Word());
        }
        foreach (var c in labels["provider"]!.AsArray().Select(n => n!.AsObject()))
            Assert.Equal(Str(c["expect"]), SubagentPool.Provider(SubagentWords.KindOf(Str(c["kind"]))!.Value, Str(c["planProvider"])));
        foreach (var c in labels["machine"]!.AsArray().Select(n => n!.AsObject()))
            Assert.Equal(Str(c["expect"]), SubagentPool.Machine(SubagentWords.KindOf(Str(c["kind"]))!.Value, Str(c["baseURL"])!, Str(c["routeID"])!, Bool(c["selfHosted"])));
        foreach (var c in labels["retry"]!.AsArray().Select(n => n!.AsObject()))
            Assert.Equal(Str(c["expect"]), SubagentPool.RetryWords(Num(c["seconds"])!.Value));
    }

    [Fact]
    public void EachChatsPool()
    {
        foreach (var c in Load("pool-cases.json")["cases"]!.AsArray().Select(n => n!.AsObject()))
        {
            var name = Str(c["name"]);
            var pool = SubagentPool.Build(c["routes"]!.AsArray().Select(r => Facts(r!)).ToList(), Facts(c["chat"]!),
                Bool(c["guardianSignedIn"]), Bool(c["sensitive"]), Bool(c["shieldOn"]), Bool(c["unattended"]), Bool(c["overflow"], true));
            var expect = c["expect"]!.AsObject();
            Assert.True(Bool(expect["chatOwnAgent"]) == pool.ChatOwnAgent, name);
            var entries = expect["entries"]!.AsArray().Select(n => n!.AsObject()).ToList();
            Assert.True(entries.Count == pool.Entries.Count, $"{name}: {string.Join(", ", pool.Entries.Select(e => e.Id))}");
            for (var i = 0; i < entries.Count; i++)
            {
                var want = entries[i];
                var got = pool.Entries[i];
                var where = $"{name} — entry {i} ({got.Id})";
                Assert.True(Str(want["id"]) == got.Id, where);
                Assert.True(Str(want["routeID"]) == got.RouteId, where);
                Assert.True(Str(want["label"]) == got.Label, where);
                Assert.True(Str(want["provider"]) == got.Provider, $"{where}: provider {got.Provider}");
                Assert.True(Str(want["model"]) == got.Model, where);
                Assert.True(Str(want["context"]) == got.Context, $"{where}: context {got.Context}");
                Assert.True(Bool(want["private"]) == got.IsPrivate, $"{where}: private");
                Assert.True(Str(want["tier"]) == got.Tier.Word(), $"{where}: tier {got.Tier.Word()}");
                Assert.True(Str(want["speed"]) == got.Speed.Word(), $"{where}: speed {got.Speed.Word()}");
                Assert.True(Bool(want["images"]) == got.Images, $"{where}: images");
                Assert.True(Bool(want["ownAgent"]) == got.OwnAgent, $"{where}: ownAgent");
                Assert.True((int)Num(want["parallel"])!.Value == got.Parallel, $"{where}: parallel {got.Parallel}");
                if (Str(want["machine"]) is { } machine) Assert.True(machine == got.Machine, $"{where}: machine {got.Machine}");
                Assert.True(Bool(want["primary"]) == got.IsPrimary, $"{where}: primary");
            }
            var dropped = expect["dropped"]!.AsArray().Select(n => n!.AsObject()).ToList();
            Assert.True(dropped.Count == pool.Dropped.Count, $"{name}: dropped {string.Join(", ", pool.Dropped.Select(d => d.Id + " (" + d.Reason + ")"))}");
            for (var i = 0; i < dropped.Count; i++)
            {
                Assert.True(Str(dropped[i]["id"]) == pool.Dropped[i].Id, $"{name}: dropped {i} {pool.Dropped[i].Id}");
                Assert.True(Str(dropped[i]["reason"]) == pool.Dropped[i].Reason, $"{name}: dropped {i} reason {pool.Dropped[i].Reason}");
            }
        }
    }

    [Fact]
    public void WhereOneTaskRuns()
    {
        var fixture = Load("placement-cases.json");
        var pinWait = TimeSpan.FromSeconds(Num(fixture["pinWait"]) ?? 120);
        foreach (var c in fixture["cases"]!.AsArray().Select(n => n!.AsObject()))
        {
            var name = Str(c["name"]);
            var r = c["request"]!.AsObject();
            var request = new SubagentPlacement.Request
            {
                Pinned = Str(r["pinned"]),
                AgentFile = Str(r["agentFile"]),
                Sensitive = Bool(r["sensitive"]),
                Unattended = Bool(r["unattended"]),
                PinDeadline = Time(r["pinDeadline"]),
                Tried = new HashSet<string>((r["tried"] as JsonArray ?? []).Select(t => Str(t)!), StringComparer.Ordinal),
                Overflow = Bool(r["overflow"], true),
                LastError = Str(r["lastError"]),
                PinWait = pinWait,
            };
            var candidates = c["candidates"]!.AsArray().Select(n => n!.AsObject()).Select(x => new SubagentPlacement.Candidate
            {
                Id = Str(x["id"])!,
                Handle = Str(x["handle"]) ?? Str(x["id"])!,
                Label = Str(x["label"]) ?? Str(x["handle"]) ?? Str(x["id"])!,
                IsPrimary = Bool(x["primary"]),
                IsPrivate = Bool(x["private"], true),
                InteractiveOnly = Bool(x["interactiveOnly"]),
                InFlight = (int)(Num(x["inFlight"]) ?? 0),
                Parallel = (int)(Num(x["parallel"]) ?? 2),
                HealthyAt = Time(x["healthyAt"]),
                Error = Str(x["error"]),
            }).ToList();
            var decision = SubagentPlacement.Choose(request, candidates, Time(c["now"])!.Value);
            var expect = c["expect"]!.AsObject();
            if (expect.ContainsKey("run"))
            {
                var run = Assert.IsType<SubagentPlacement.Decision.Run>(decision);
                Assert.True(Str(expect["run"]) == run.Id, $"{name}: ran on {run.Id}");
                Assert.True(Str(expect["reason"]) == run.Reason.Code(), $"{name}: reason {run.Reason.Code()}");
                Assert.True(Str(expect["note"]) == run.Note, $"{name}: note {run.Note}");
            }
            else if (expect.ContainsKey("wait"))
            {
                var wait = Assert.IsType<SubagentPlacement.Decision.Wait>(decision);
                Assert.True(Time(expect["wait"]) == wait.Until, $"{name}: waits until {wait.Until}");
            }
            else
            {
                var unavailable = Assert.IsType<SubagentPlacement.Decision.Unavailable>(decision);
                Assert.True(Str(expect["unavailable"]) == unavailable.Message, $"{name}: {unavailable.Message}");
            }
        }
    }

    [Fact]
    public void TheToolsSayTheSameWordsAsTheMac()
    {
        var fixture = Load("tool-specs.json");
        var types = AgentCatalog.Default.Describe();
        Assert.True(JsonNode.DeepEquals(fixture["agentParameters"], JsonNode.Parse(AgentTool.Parameters)), "agent parameters");
        Assert.True(JsonNode.DeepEquals(fixture["delegateParameters"], JsonNode.Parse(DelegateTool.Parameters)), "delegate parameters");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(new AgentTool().Spec.Parameters), JsonNode.Parse(AgentTool.Parameters)));
        Assert.Equal(Str(fixture["delegateDescription"])!.Replace("{types}", types), new DelegateTool().Spec.Description);
        Assert.Equal(Str(fixture["agentStatusDescription"]), new AgentStatusTool().Spec.Description);
        foreach (var c in fixture["cases"]!.AsArray().Select(n => n!.AsObject()))
        {
            var pool = PoolOf(c["pool"]!.AsObject());
            var catalogue = Str(c["catalogue"]);
            Assert.Equal(catalogue, SubagentPool.Describe(pool));
            var description = Str(fixture["agentDescription"])!.Replace("{types}", types) + (catalogue is null ? "" : "\n\n" + catalogue);
            Assert.Equal(description, AgentTool.SpecFor(AgentCatalog.Default, pool).Description);
            foreach (var l in c["loads"]!.AsArray().Select(n => n!.AsObject()))
            {
                var loads = l["loads"]!.AsObject().ToDictionary(kv => kv.Key, kv => new SubagentPool.Load(
                    (int)(Num(kv.Value!["inFlight"]) ?? 0), Bool(kv.Value!["healthy"], true), Num(kv.Value!["retryIn"])));
                Assert.Equal(Str(l["expect"]), SubagentPool.LoadLine(pool, loads));
                // Load never reaches the description (the prompt cache holds).
                Assert.Equal(description, new AgentTool(AgentCatalog.Default, pool).Spec.Description);
            }
        }
    }

    [Fact]
    public void TheServerArgument()
    {
        var fixture = Load("server-arg-cases.json");
        var pools = fixture["pools"]!.AsObject().ToDictionary(kv => kv.Key, kv => PoolOf(kv.Value!.AsObject()));
        foreach (var c in fixture["cases"]!.AsArray().Select(n => n!.AsObject()))
        {
            var choice = SubagentPool.Match(Str(c["value"])!, pools[Str(c["pool"])!]);
            var expect = c["expect"]!.AsObject();
            Assert.True(Str(expect["id"]) == choice.Entry?.Id, $"“{Str(c["value"])}”: {choice.Entry?.Id ?? choice.Error}");
            Assert.True(Str(expect["error"]) == choice.Error, $"“{Str(c["value"])}”: {choice.Error}");
        }
    }

    [Fact]
    public async Task AClaudeCodeJobsReport()
    {
        var fixture = Load("claude-code-report.json");
        foreach (var c in fixture["reportCases"]!.AsArray().Select(n => n!.AsObject()))
            Assert.Equal(Str(c["expect"]), ClaudeCodeSubagent.Report(Str(c["reply"])!, c["toolLog"]!.AsArray().Select(t => Str(t)!).ToList(), Bool(c["hitTurnLimit"])));

        // The real CLI's capture, replayed: the report, whether it changed things, and its tokens.
        var lines = File.ReadAllLines(Path.Combine(Folder, Str(fixture["stream"])!)).Where(l => l.Trim().Length > 0).ToList();
        var process = ScriptedClaudeProcess.Replay(lines);
        var subagent = new ClaudeCodeSubagent(ClaudeCodeLaunch.Direct("/usr/local/bin/claude"), "sonnet") { Starter = ScriptedClaudeProcess.Starter(process) };
        var result = await subagent.RunAsync(new ExternalSubagentJob
        {
            Prompt = "Edit hello.txt",
            SystemNote = ClaudeCodeSubagent.Note,
            Workspace = Str(fixture["workspace"])!,
            RequestPermission = (_, _, _) => Task.FromResult(true),
        }, default);
        var expect = fixture["expect"]!.AsObject();
        Assert.Equal(Bool(expect["ok"]), result.Ok);
        Assert.Equal(Str(expect["report"]), result.Report);
        Assert.Equal(Bool(expect["mutated"]), result.Mutated);
        Assert.Equal((long)Num(expect["promptTokens"])!.Value, result.PromptTokens);
        Assert.Equal((long)Num(expect["completionTokens"])!.Value, result.CompletionTokens);
    }
}
