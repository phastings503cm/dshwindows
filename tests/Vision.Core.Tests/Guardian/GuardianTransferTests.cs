using System.Net;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>What travels to and from Guardian: chats in the Mac's transcript shape, project manifests, skills, and Guardian
/// as a model route (its proxy's refusals in plain words).</summary>
public sealed class GuardianTransferTests
{
    // MARK: Chats

    /// <summary>A chat exported by Vision for macOS 0.15.1 (GuardianSettings.chatJSON, Swift's default date encoding:
    /// seconds since 2001-01-01).</summary>
    private const string MacPayload = """
        {"id":"9F1C2B7E-1111-4A4A-9999-000000000001","title":"Fix the build","cwd":"/Users/pat/code/inventory","rows":[
         {"kind":"user","text":"Fix the build","isError":false,"at":782550000.25},
         {"kind":"tool","text":"ran dotnet build","toolName":"run_command","argSummary":"dotnet build","output":"Build succeeded.","isError":false,"at":782550010},
         {"kind":"todos","text":"Build\nTest","isError":false,"at":782550011},
         {"kind":"compaction","text":"Earlier turns summarised","argSummary":"12","isError":false,"at":782550012},
         {"kind":"assistant","text":"Done — it builds.","isError":false,"at":782550020},
         {"kind":"something-new","text":"?","isError":false,"at":782550021}
        ]}
        """;

    [Fact]
    public void AMacExportedChatImportsHere()
    {
        var chat = GuardianChats.FromPayload(MacPayload)!;
        Assert.Equal("Fix the build", chat.Title);
        Assert.Equal(["user", "tool", "notice", "compaction", "assistant"], chat.Rows.Select(r => r.Kind));
        Assert.Equal(new DateTimeOffset(2025, 10, 19, 7, 0, 0, 250, TimeSpan.Zero), chat.Rows[0].At);
        Assert.Equal("Plan:\nBuild\nTest", chat.Rows[2].Text);
        Assert.Equal(("run_command", "dotnet build", "Build succeeded."), (chat.Rows[1].ToolName, chat.Rows[1].ArgSummary, chat.Rows[1].Output));
        Assert.Equal(Enumerable.Range(0, 5), chat.Rows.Select(r => r.Seq));
    }

    [Fact]
    public void AChatExportedHereIsOneTheMacCanRead()
    {
        var at = new DateTimeOffset(2026, 10, 19, 1, 2, 3, TimeSpan.FromHours(-7));
        LogItemRow[] rows =
        [
            new("c1", 0, "user", "Hello", null, null, null, false, at),
            new("c1", 1, "error", "The model server replied 500", null, null, null, true, at.AddSeconds(5)),
        ];
        var payload = GuardianChats.ToPayload("c1", "Hello chat", null, rows);
        var json = JsonNode.Parse(payload)!;
        // The fields the Mac's Codable needs, with the date as a number of reference-date seconds.
        Assert.Equal("c1", json["id"]!.GetValue<string>());
        Assert.Null(json["cwd"]);
        var first = json["rows"]![0]!;
        Assert.False(first["isError"]!.GetValue<bool>());
        Assert.Equal((at - new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds, first["at"]!.GetValue<double>(), 3);
        Assert.True(json["rows"]![1]!["isError"]!.GetValue<bool>());

        var back = GuardianChats.FromPayload(payload)!;
        Assert.Equal(rows.Select(r => (r.Kind, r.Text, r.IsError, r.At.ToUnixTimeMilliseconds())),
            back.Rows.Select(r => (r.Kind, r.Text, r.IsError, r.At.ToUnixTimeMilliseconds())));
    }

    [Theory]
    [InlineData("""{"title":"no rows"}""")]
    [InlineData("""not json""")]
    [InlineData("""{"id":"../../evil","rows":[]}""")]
    public void APayloadThatIsntAChatOrWouldEscapeTheFolderIsRefused(string payload) => Assert.Null(GuardianChats.FromPayload(payload));

    // MARK: Projects

    [Fact]
    public void AProjectUploadTakesTextAndLeavesOutBuildsBinariesAndSecrets()
    {
        using var project = new TempDirectory();
        project.Write("README.md", "# Inventory\n");
        project.Write("src/Stock.cs", "﻿class Stock {}");
        project.Write("node_modules/left/index.js", "x");
        project.Write("bin/Debug/app.deps.json", "{}");
        project.Write(".git/config", "[core]");
        project.Write(".env", "API_KEY=sk-live-123");
        project.Write(".env.example", "API_KEY=");
        project.Write("certs/server.pem", "-----BEGIN PRIVATE KEY-----");
        File.WriteAllBytes(project["logo.png"], [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        File.WriteAllBytes(project["latin1.txt"], [0x63, 0x61, 0x66, 0xE9]); // not UTF-8
        project.Write("big.log", new string('x', GuardianProjects.MaxFileBytes));

        var manifest = GuardianProjects.Collect(project.Path);

        Assert.Equal([".env.example", "README.md", "src/Stock.cs"], manifest.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("class Stock {}", manifest.Files["src/Stock.cs"]);
        Assert.Equal((2, 1, 2), (manifest.SkippedBinary, manifest.SkippedLarge, manifest.SkippedSecret));
        Assert.False(manifest.Truncated);
    }

    [Fact]
    public void ChunksStayUnderTheCapAndKeepEveryFile()
    {
        var files = Enumerable.Range(0, 10).ToDictionary(i => $"f{i}", i => new string((char)('a' + i), 1_000) + "\"quoted\"\n");
        var chunks = GuardianProjects.Chunk(files, 3_500);
        Assert.True(chunks.Count >= 3);
        Assert.Equal(files.Keys.Order(), chunks.SelectMany(c => c.Keys).Order());
        Assert.Single(GuardianProjects.Chunk(new Dictionary<string, string>(), 10));
    }

    // MARK: Skills

    [Fact]
    public void ASkillIsGatheredWithItsDescriptionAndWithoutItsSecrets()
    {
        using var root = new TempDirectory();
        root.Write("deploy/SKILL.md", "---\nname: deploy\ndescription: \"Ship the service\"\n---\nRun it.");
        root.Write("deploy/scripts/run.ps1", "dotnet publish");
        root.Write("deploy/.env", "TOKEN=abc");
        root.Write("deploy/key.pem", "-----BEGIN");
        var skill = GuardianSkills.Gather(root["deploy"])!;
        Assert.Equal(("deploy", "Ship the service"), (skill.Name, skill.Description));
        Assert.Equal(["SKILL.md", "scripts/run.ps1"], skill.Files.Keys);
        Assert.Null(GuardianSkills.Gather(root.Path));
    }

    [Fact]
    public void TakingASkillAddsItOnceAndNeverOverwritesALocalEditUnlessAsked()
    {
        using var root = new TempDirectory();
        var skill = new GuardianSkill("Deploy Service", "Ship", new Dictionary<string, string> { ["SKILL.md"] = "v1", ["scripts/run.ps1"] = "go" });

        Assert.Equal(GuardianSkills.TakeOutcome.Added, GuardianSkills.Take(skill, root.Path, replace: false, out var folder));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "SKILL.md")));
        Assert.Equal(GuardianSkills.TakeOutcome.Unchanged, GuardianSkills.Take(skill, root.Path, replace: false, out _));

        File.WriteAllText(Path.Combine(folder, "SKILL.md"), "my local edit");
        Assert.Equal(GuardianSkills.TakeOutcome.Skipped, GuardianSkills.Take(skill, root.Path, replace: false, out _));
        Assert.Equal("my local edit", File.ReadAllText(Path.Combine(folder, "SKILL.md")));
        Assert.Equal(GuardianSkills.TakeOutcome.Updated, GuardianSkills.Take(skill, root.Path, replace: true, out _));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(folder, "SKILL.md")));

        var escaping = new GuardianSkill("evil", "", new Dictionary<string, string> { ["SKILL.md"] = "x", ["../../outside.txt"] = "x" });
        Assert.Equal(GuardianSkills.TakeOutcome.Skipped, GuardianSkills.Take(escaping, root.Path, replace: true, out _));
        Assert.False(File.Exists(Path.Combine(root.Path, "..", "outside.txt")));
    }

    // MARK: Guardian as a model route

    [Fact]
    public void AGuardianRouteIsTheProxyWithTheOrgHeader()
    {
        var route = GuardianRoutes.Route("https://guardian.503cm.com/", new GuardianModel("guardian/default", ContextWindow: 200_000, InteractiveOnly: true), "org-1");
        Assert.Equal("https://guardian.503cm.com/api/harness/v1", route.BaseUrl);
        Assert.Equal(ProviderKind.OpenAICompat, route.Kind);
        Assert.Equal("Guardian|guardian/default", route.RouteId);
        Assert.Equal("org-1", GuardianRoutes.OrgOf(route));
        Assert.True(route.IsInteractiveOnly);
        Assert.True(GuardianRoutes.IsGuardian(route));
        Assert.Equal("https://guardian.503cm.com", GuardianRoutes.AddressOf(route));
        // An online service: masked by the shield, and no self-hosted thinking switches.
        Assert.False(route.IsPrivateRoute);
        Assert.False(route.IsSelfHosted);

        GuardianRoutes.SetOrg(route, null);
        Assert.Null(route.CustomHeaders);
        Assert.False(GuardianRoutes.IsGuardian(new ProviderProfile(ProviderKind.OpenAICompat, "Spark", "http://10.0.0.5:8002/v1", "m")));
    }

    [Fact]
    public async Task TheProxysRefusalReachesTheChatInGuardiansWordsAndIsReported()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"error":"forbidden","status":403,"reason":"model_not_allowed","needed":"us.anthropic.claude-opus-5-5","message":"The model \"us.anthropic.claude-opus-5-5\" is not available to you in this Guardian org. Ask an admin of 503CM to allow it, or pick one from the model list."}
            """, HttpStatusCode.Forbidden));
        var route = GuardianRoutes.Route("https://guardian.example.test", new GuardianModel("us.anthropic.claude-opus-5-5"), null) with { ApiKey = "tok" };
        GuardianError? reported = null;
        void Listen(ProviderProfile _, GuardianError error) => reported = error;
        GuardianProxyRefusals.Reported += Listen;
        try
        {
            var client = new OpenAiClient(route, handler);
            var error = await Assert.ThrowsAsync<LlmException>(async () =>
            {
                await foreach (var _ in client.StreamAsync(new LlmRequest("", [LlmMessage.User("hi")], [], route.Model))) { }
            });
            Assert.True(error.Permanent);
            Assert.Equal(403, error.StatusCode);
            Assert.StartsWith("Guardian doesn't allow this yet: The model \"us.anthropic.claude-opus-5-5\" is not available", error.Message);
            Assert.DoesNotContain("replied 403", error.Message);
            Assert.Equal("model_not_allowed", reported?.Reason);
            Assert.Equal("Bearer tok", handler.Requests[0].Headers["Authorization"]);
            Assert.Equal("https://guardian.example.test/api/harness/v1/chat/completions", handler.Requests[0].Url.ToString());
        }
        finally
        {
            GuardianProxyRefusals.Reported -= Listen;
        }
    }

    [Fact]
    public void OnlyASlowDownIsWorthRetrying()
    {
        var slow = GuardianProxyRefusals.ToLlmException(GuardianError.FromResponse(429, """{"error":"rate_limited","status":429,"message":"Slow down"}"""), "");
        var budget = GuardianProxyRefusals.ToLlmException(GuardianError.FromResponse(402, """{"error":"budget_exceeded","status":402,"message":"Over"}"""), "");
        var tokens = GuardianProxyRefusals.ToLlmException(GuardianError.FromResponse(429, """{"error":"limit_reached","status":429,"message":"Tokens"}"""), "");
        Assert.False(slow.Permanent);
        Assert.True(budget.Permanent);
        Assert.True(tokens.Permanent);
        Assert.Null(GuardianProxyRefusals.From(new ProviderProfile(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt"), 402, "{}", null));
    }
}
