using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>The Mac review's MCP rules (10-19 #20): a repository's servers come unchecked and switched off unless approved in
/// Claude Code, keys in a command line and missing variables are flagged, and Plan only asks for every call but a read-only
/// tool on a trusted server.</summary>
[Collection(SerialStaticState.Name)]
public sealed class McpReviewTests : IDisposable
{
    private readonly TempDirectory _dir = new("vision-mcp-review");
    private readonly bool _memoryOnly = SecretStore.UseMemoryOnly;

    public McpReviewTests() => SecretStore.UseMemoryOnly = true;

    public void Dispose()
    {
        SecretStore.UseMemoryOnly = _memoryOnly;
        McpManager.TransportOverride = null;
        _dir.Dispose();
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ARepositorysServersComeUncheckedAndSwitchedOffUnlessApproved()
    {
        var home = _dir["home"];
        var project = _dir["work/app"];
        Directory.CreateDirectory(project);
        Write(Path.Combine(home, ".claude.json"), """{"projects": {PROJECT: {"enabledMcpjsonServers": ["approved"], "disabledMcpjsonServers": ["declined"]}}}"""
            .Replace("PROJECT", System.Text.Json.JsonSerializer.Serialize(project)));
        Write(Path.Combine(project, ".mcp.json"), """
            {"mcpServers": {
              "approved": {"command": "npx", "args": ["tool"]},
              "declined": {"command": "npx", "args": ["other"]},
              "unknown": {"command": "npx", "args": ["--api-key", "abc123", "${VISION_TEST_SURELY_UNSET_VAR}"]}
            }}
            """);
        Write(Path.Combine(home, ".cursor", "mcp.json"), """{"mcpServers":{"mine":{"command":"npx","args":["mine"]}}}""");

        var found = McpImport.Scan(new ExternalLocations(home), project).ToDictionary(c => c.Server.Name);

        Assert.True(found["mine"].Preselected);
        Assert.Empty(found["mine"].Warnings);
        Assert.True(found["approved"].Preselected);
        Assert.Equal([McpImport.ApprovedWarning], found["approved"].Warnings);
        Assert.False(found["declined"].Preselected);
        Assert.Equal([McpImport.DeclinedWarning], found["declined"].Warnings);
        var unknown = found["unknown"];
        Assert.False(unknown.Preselected);
        Assert.Equal(["VISION_TEST_SURELY_UNSET_VAR"], unknown.Needs);
        Assert.True(unknown.SecretInPlainText);
        Assert.Equal(McpImport.RepositoryWarning, unknown.Warnings[0]);
        Assert.StartsWith("Needs VISION_TEST_SURELY_UNSET_VAR: Vision doesn't have it", unknown.Warnings[1]);
        Assert.Equal(McpImport.PlainTextSecretWarning, unknown.Warnings[2]);

        var store = new McpServerStore(_dir["mcp-servers.json"]);
        var imported = new List<McpServerConfig>();
        McpImport.Import(store, [found["mine"], found["approved"]], imported);
        Assert.False(store.Servers.Single(s => s.Name == "approved").Enabled);
        Assert.True(store.Servers.Single(s => s.Name == "mine").Enabled);
        Assert.Equal("Imported mine — connecting now. approved switched off — turn it on in the list once you've checked what it runs.",
            McpImport.Summary(imported));
        Assert.Equal("Nothing new to import.", McpImport.Summary([]));
    }

    [Theory]
    [InlineData(new[] { "--api-key", "abc123" }, true)]
    [InlineData(new[] { "--token=xyz" }, true)]
    [InlineData(new[] { "ghp_abcdefghijklmnop1234" }, true)]
    [InlineData(new[] { "--key-file", "/tmp/k" }, false)]
    [InlineData(new[] { "--token", "${GITHUB_TOKEN}" }, false)]
    [InlineData(new[] { "-y", "@modelcontextprotocol/server-github" }, false)]
    public void KeysInACommandLineAreNoticed(string[] args, bool secret) =>
        Assert.Equal(secret, McpImport.HoldsSecretInPlainText(new McpServerConfig { Name = "s", Command = "npx", Args = [.. args] }));

    [Theory]
    [InlineData("https://mcp.example.com/mcp", false)]
    [InlineData("https://mcp.example.com/mcp?api_key=abc", true)]
    [InlineData("https://user:hunter2@mcp.example.com/mcp", true)]
    [InlineData("https://mcp.example.com/s/a1b2c3d4e5f6a7b8c9d0e1f2a3b4/mcp", true)]
    public void KeysInAnAddressAreNoticed(string url, bool secret) =>
        Assert.Equal(secret, McpImport.HoldsSecretInPlainText(new McpServerConfig { Name = "s", Transport = McpTransportKind.Http, Url = url }));

    [Fact]
    public async Task PlanOnlyAsksForEveryCallButAReadOnlyToolOnATrustedServer()
    {
        var store = new McpServerStore(_dir["mcp-servers.json"]);
        store.Save(new McpServerConfig { Id = "d", Name = "demo", Command = "x", Trust = McpTrust.Always });
        McpManager.TransportOverride = _ => new LoopbackMcpTransport(new McpLoopbackServer());
        await using var manager = new McpManager(store);
        await manager.SyncAsync();
        var asked = new List<string>();
        using var tools = new ToolTestContext();
        var context = tools.Context with
        {
            Policy = new PermissionPolicy(PermissionPreset.Plan, tools.Root.Path),
            RequestPermission = (_, _, detail) =>
            {
                asked.Add(detail);
                return Task.FromResult(true);
            },
        };

        await manager.ToolsFor(null).Single(t => t.Name == "mcp__demo__echo").ExecuteAsync("""{"text":"x"}""", context, default);
        Assert.Empty(asked);
        await manager.ToolsFor(null).Single(t => t.Name == "mcp__demo__add").ExecuteAsync("""{"a":1,"b":2}""", context, default);
        Assert.Single(asked);
        store.SetTrust("d", McpTrust.Ask);
        await manager.SyncAsync();
        await manager.ToolsFor(null).Single(t => t.Name == "mcp__demo__echo").ExecuteAsync("""{"text":"x"}""", context, default);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public void ASchemaWithoutATypeOrPropertiesGetsThem()
    {
        Assert.Equal("""{"type":"object","properties":{}}""", McpSession.Schema(null));
        Assert.Equal("""{"required":["a"],"type":"object","properties":{}}""", McpSession.Schema(JsonNode.Parse("""{"required":["a"]}""")!.AsObject()));
    }
}
