using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>MCP servers (10-19 #20): the protocol, the tools as the model sees them, the transports, the manager, the settings
/// file (Vision for macOS's) and importing what Claude Code, Claude Desktop and Cursor have.</summary>
[Collection(SerialStaticState.Name)]
public sealed class McpTests : IDisposable
{
    private readonly TempDirectory _dir = new("vision-mcp");
    private readonly bool _memoryOnly = SecretStore.UseMemoryOnly;

    public McpTests() => SecretStore.UseMemoryOnly = true;

    public void Dispose()
    {
        SecretStore.UseMemoryOnly = _memoryOnly;
        McpManager.TransportOverride = null;
        _dir.Dispose();
    }

    // MARK: The protocol

    [Fact]
    public async Task ASessionInitializesListsAndCallsTools()
    {
        var transport = new LoopbackMcpTransport(new McpLoopbackServer { Name = "demo" });
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);

        Assert.Equal("demo", session.ServerName);
        Assert.Equal(McpSession.LatestProtocolVersion, transport.ProtocolVersion);
        Assert.Equal("Vision", transport.Sent[0]["params"]!["clientInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("notifications/initialized", transport.Sent[1]["method"]!.GetValue<string>());
        Assert.Null(transport.Sent[1]["id"]);

        var tools = await session.ListToolsAsync(default);
        Assert.Equal(["echo", "add"], tools.Select(t => t.Name));
        Assert.True(tools[0].ReadOnly);
        Assert.Equal("5", McpTool.Present(await session.CallToolAsync("add", new JsonObject { ["a"] = 2, ["b"] = 3 }, default)).Output);
    }

    [Fact]
    public async Task AServerErrorBecomesAnMcpExceptionWithItsMessage()
    {
        await using var session = new McpSession(new LoopbackMcpTransport(new McpLoopbackServer()));
        await session.ConnectAsync(default);
        var error = await Assert.ThrowsAsync<McpException>(() => session.CallToolAsync("missing", new JsonObject(), default));
        Assert.Equal(("Unknown tool: missing", -32602), (error.Message, error.Code));
    }

    [Fact]
    public async Task TheServersPingIsAnsweredAndAToolListChangeIsHeard()
    {
        var transport = new LoopbackMcpTransport(new McpLoopbackServer());
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);
        var changed = new TaskCompletionSource();
        session.ToolsChanged += () => changed.TrySetResult();

        await transport.PushAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "srv-1", ["method"] = "ping" });
        await transport.PushAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 50 && !transport.Sent.Any(m => m["id"]?.ToString() == "srv-1"); i++) await Task.Delay(20);

        Assert.NotNull(transport.Sent.Single(m => m["id"]?.ToString() == "srv-1")["result"]);
    }

    [Fact]
    public async Task AServerThatGoesAwayFailsTheWaitingRequestInsteadOfHanging()
    {
        var transport = new HangingTransport(new LoopbackMcpTransport(new McpLoopbackServer()));
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);
        var call = session.CallToolAsync("echo", new JsonObject(), default);
        transport.Inner.Drop();
        var error = await Assert.ThrowsAsync<McpException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.StartsWith("The server stopped.", error.Message);
        Assert.True(session.Closed);
    }

    /// <summary>A loopback that swallows tools/call (the server never answers it).</summary>
    private sealed class HangingTransport(LoopbackMcpTransport inner) : IMcpTransport
    {
        public LoopbackMcpTransport Inner => inner;
        public System.Threading.Channels.ChannelReader<JsonObject> Incoming => inner.Incoming;
        public string? Diagnostics => inner.Diagnostics;
        public string? ProtocolVersion { get => inner.ProtocolVersion; set => inner.ProtocolVersion = value; }
        public Task StartAsync(CancellationToken cancellationToken) => inner.StartAsync(cancellationToken);
        public Task SendAsync(JsonObject message, CancellationToken cancellationToken) =>
            message["method"]?.GetValue<string>() == "tools/call" ? Task.CompletedTask : inner.SendAsync(message, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // MARK: Tools as the model sees them

    [Theory]
    [InlineData("github", "create_issue", "mcp__github__create_issue")]
    [InlineData("My Server", "files.read", "mcp__My_Server__files_read")]
    [InlineData("_db_", "run query!", "mcp__db__run_query")]
    public void ToolNamesArePrefixedAndSafe(string server, string tool, string expected) => Assert.Equal(expected, McpNames.ToolName(server, tool));

    [Fact]
    public void ALongNameIsCutWithATagSoTwoStayApart()
    {
        var a = McpNames.ToolName("server", new string('a', 80) + "_one");
        var b = McpNames.ToolName("server", new string('a', 80) + "_two");
        Assert.Equal(McpNames.MaxLength, a.Length);
        Assert.NotEqual(a, b);
        Assert.Matches("^mcp__server__a+_[0-9a-f]{6}$", a);
        Assert.Equal(["github", "github-2", "GitHub-3"], new[] { "github", "github", "GitHub" }.Aggregate(new List<string>(), (taken, n) =>
        {
            taken.Add(McpNames.Unique(n, taken));
            return taken;
        }));
    }

    [Fact]
    public void AResultIsPresentedAsTextImagesAndResources()
    {
        var png = Convert.ToBase64String([1, 2, 3]);
        var result = JsonNode.Parse("""
            {"content":[
              {"type":"text","text":"Found 2 issues"},
              {"type":"image","data":"PNG","mimeType":"image/png"},
              {"type":"resource","resource":{"uri":"file:///a.txt","text":"hello"}},
              {"type":"resource_link","uri":"https://x/y","name":"spec"}
            ]}
            """.Replace("PNG", png))!.AsObject();
        var presented = McpTool.Present(result);
        Assert.Equal("Found 2 issues\n[image 2: image/png]\n[resource file:///a.txt]\nhello\n[link: spec https://x/y]", presented.Output);
        Assert.Equal([1, 2, 3], Assert.Single(presented.Images).Data);
        Assert.Equal("Error: boom", McpTool.Present(JsonNode.Parse("""{"content":[{"type":"text","text":"boom"}],"isError":true}""")!.AsObject()).Output);
        Assert.Equal("Error: the tool reported an error.", McpTool.Present(JsonNode.Parse("""{"content":[],"isError":true}""")!.AsObject()).Output);
        Assert.Equal("""{"total":3}""", McpTool.Present(JsonNode.Parse("""{"content":[],"structuredContent":{"total":3}}""")!.AsObject()).Output);
    }

    private async Task<(McpManager Manager, McpServerStore Store)> Connected(params McpServerConfig[] servers)
    {
        var store = new McpServerStore(_dir["mcp-servers.json"]);
        foreach (var server in servers) store.Save(server);
        McpManager.TransportOverride ??= _ => new LoopbackMcpTransport(new McpLoopbackServer());
        var manager = new McpManager(store);
        await manager.SyncAsync();
        return (manager, store);
    }

    private ToolContext Context(List<string> asked, bool answer = true, PermissionPreset preset = PermissionPreset.WorkspaceWrite, bool sensitive = false)
    {
        var tools = new ToolTestContext();
        return tools.Context with
        {
            Policy = new PermissionPolicy(preset, tools.Root.Path),
            Sensitive = sensitive,
            RequestPermission = (_, _, detail) =>
            {
                asked.Add(detail);
                return Task.FromResult(answer);
            },
        };
    }

    [Fact]
    public async Task EveryCallAsksUnlessTheServerIsTrustedOrTheChatHasFullAccess()
    {
        var (manager, store) = await Connected(new McpServerConfig { Id = "d", Name = "demo", Command = "x" });
        await using var _ = manager;
        var echo = manager.ToolsFor(null).Single(t => t.Name == "mcp__demo__echo");
        Assert.StartsWith("[MCP server “demo”] Repeat the text back.", echo.Spec.Description);

        var asked = new List<string>();
        Assert.Equal("Error: the user declined this MCP call. Don't retry it; say what you wanted it for.",
            (await echo.ExecuteAsync("""{"text":"hi"}""", Context(asked, answer: false), default)).Output);
        Assert.Equal("hi", (await echo.ExecuteAsync("""{"text":"hi"}""", Context(asked), default)).Output);
        Assert.Equal(2, asked.Count);
        Assert.Equal("MCP server “demo” wants to run its tool “echo” with {\"text\":\"hi\"}", asked[0]);
        // Full access asks nothing.
        await echo.ExecuteAsync("""{"text":"x"}""", Context(asked, preset: PermissionPreset.FullAccess), default);
        Assert.Equal(2, asked.Count);

        // Read-only tools without asking: echo (read-only) runs, add asks.
        store.SetTrust("d", McpTrust.ReadOnly);
        await manager.SyncAsync();
        var tools = manager.ToolsFor(null);
        await tools.Single(t => t.Name == "mcp__demo__echo").ExecuteAsync("""{"text":"x"}""", Context(asked), default);
        Assert.Equal(2, asked.Count);
        Assert.Equal("3", (await tools.Single(t => t.Name == "mcp__demo__add").ExecuteAsync("""{"a":1,"b":2}""", Context(asked), default)).Output);
        Assert.Equal(3, asked.Count);

        store.SetTrust("d", McpTrust.Always);
        await manager.SyncAsync();
        await manager.ToolsFor(null).Single(t => t.Name == "mcp__demo__add").ExecuteAsync("""{"a":1,"b":2}""", Context(asked), default);
        Assert.Equal(3, asked.Count);
    }

    [Fact]
    public async Task InASensitiveChatAServerOnSomeoneElsesMachineAlwaysAsks()
    {
        var (manager, _) = await Connected(
            new McpServerConfig { Id = "r", Name = "remote", Transport = McpTransportKind.Http, Url = "https://mcp.example.com/mcp", Trust = McpTrust.Always },
            new McpServerConfig { Id = "l", Name = "local", Command = "x", Trust = McpTrust.Always });
        await using var _ = manager;
        var asked = new List<string>();
        var all = manager.ToolsFor(null);
        await all.Single(t => t.Name == "mcp__local__echo").ExecuteAsync("""{"text":"a"}""", Context(asked, sensitive: true, preset: PermissionPreset.FullAccess), default);
        Assert.Empty(asked);
        await all.Single(t => t.Name == "mcp__remote__echo").ExecuteAsync("""{"text":"a"}""", Context(asked, sensitive: true, preset: PermissionPreset.FullAccess), default);
        Assert.EndsWith("This chat is Sensitive: what it is given goes to mcp.example.com.", Assert.Single(asked));
        // Not Sensitive: trusted means trusted.
        await all.Single(t => t.Name == "mcp__remote__echo").ExecuteAsync("""{"text":"a"}""", Context(asked), default);
        Assert.Single(asked);
    }

    // MARK: The manager

    [Fact]
    public async Task TheManagerConnectsWhatIsOnShowsFailuresAndOffersToolsByProjectAndSwitch()
    {
        McpManager.TransportOverride = config => new LoopbackMcpTransport(config.Name == "broken"
            ? new McpLoopbackServer { FailToStart = "Couldn't start x: not found." }
            : new McpLoopbackServer());
        var (manager, store) = await Connected(
            new McpServerConfig { Id = "1", Name = "everywhere", Command = "x" },
            new McpServerConfig { Id = "2", Name = "project", Command = "x", Project = _dir.Path },
            new McpServerConfig { Id = "3", Name = "broken", Command = "x" },
            new McpServerConfig { Id = "4", Name = "off", Command = "x", Enabled = false });
        await using var _ = manager;

        var statuses = manager.Statuses.ToDictionary(s => s.Config.Name);
        Assert.Equal("Connected", statuses["everywhere"].Headline);
        Assert.Equal(2, statuses["everywhere"].Tools.Count);
        Assert.Equal("Failed: Couldn't start x: not found.", statuses["broken"].Headline);
        Assert.Equal("Off", statuses["off"].Headline);

        Assert.Equal(2, manager.ToolsFor(null).Count);
        Assert.Equal(4, manager.ToolsFor(_dir.Path).Count);
        Assert.Single(manager.ToolsFor(null, ["mcp__everywhere__echo"]));
        Assert.Equal("MCP servers connected (their tools are named mcp__<server>__<tool>): everywhere (2 tools). Each call asks the user first unless they allowed that server.",
            manager.PromptSection(null));

        // A tool kept from the model.
        store.SetToolEnabled("1", "add", false);
        await manager.SyncAsync();
        Assert.Equal(["mcp__everywhere__echo"], manager.ToolsFor(null).Select(t => t.Name));

        var revision = manager.Revision;
        store.SetEnabled("1", false);
        await manager.SyncAsync();
        Assert.True(manager.Revision > revision);
        Assert.Empty(manager.ToolsFor(null));
    }

    [Fact]
    public async Task AServerWithResourcesGetsAToolThatListsAndReadsThem()
    {
        var server = new McpLoopbackServer { Resources = { ["file:///notes.md"] = ("Notes", "remember the milk") } };
        McpManager.TransportOverride = _ => new LoopbackMcpTransport(server);
        var (manager, _) = await Connected(new McpServerConfig { Id = "d", Name = "docs", Command = "x", Trust = McpTrust.Always });
        await using var _ = manager;
        var resources = manager.ToolsFor(null).Single(t => t.Name == "mcp__docs__resources");
        var asked = new List<string>();
        Assert.Equal("file:///notes.md — Notes", (await resources.ExecuteAsync("{}", Context(asked), default)).Output);
        Assert.Equal("remember the milk", (await resources.ExecuteAsync("""{"uri":"file:///notes.md"}""", Context(asked), default)).Output);
    }

    // MARK: The settings file and its secrets

    [Fact]
    public void TheFileIsVisionForMacsAndSecretsGoToTheCredentialStore()
    {
        var path = _dir["mcp-servers.json"];
        var store = new McpServerStore(path);
        store.Save(new McpServerConfig { Id = "gh-1", Name = "github", Command = "npx", Args = ["-y", "server-github"], Trust = McpTrust.ReadOnly },
            env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_secret123" }, headers: new Dictionary<string, string>());

        var json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(1, json["version"]!.GetValue<int>());
        var server = json["servers"]![0]!.AsObject();
        Assert.Equal("stdio", server["transport"]!.GetValue<string>());
        Assert.Equal("readOnly", server["trust"]!.GetValue<string>());
        Assert.Equal("GITHUB_TOKEN", server["envNames"]![0]!.GetValue<string>());
        Assert.NotNull(server["headerNames"]);
        Assert.NotNull(server["disabledTools"]);
        Assert.DoesNotContain("ghp_secret123", File.ReadAllText(path));
        Assert.Equal("ghp_secret123", SecretStore.Read("Vision/mcp/gh-1/env/GITHUB_TOKEN"));
        Assert.Equal("ghp_secret123", McpServerStore.EnvOf(new McpServerStore(path).Find("gh-1")!)["GITHUB_TOKEN"]);

        store.Remove("gh-1");
        Assert.Null(SecretStore.Read("Vision/mcp/gh-1/env/GITHUB_TOKEN"));
    }

    [Fact]
    public void AServerSaysWhatIsMissingForItToStart()
    {
        Assert.Equal("It needs a name.", new McpServerConfig { Name = "!!", Command = "x" }.Problem);
        Assert.Equal("It needs a program to run.", new McpServerConfig { Name = "a" }.Problem);
        Assert.Equal("It needs an http(s) address.", new McpServerConfig { Name = "a", Transport = McpTransportKind.Http, Url = "ftp://x" }.Problem);
        Assert.Null(new McpServerConfig { Name = "a", Transport = McpTransportKind.Http, Url = "https://x/mcp" }.Problem);
    }

    [Fact]
    public void VariablesExpandTheWayClaudeCodesFilesExpectThem()
    {
        string? Lookup(string name) => name == "HOME_DIR" ? "/home/me" : null;
        Assert.Equal("/home/me/data", McpVariables.Expand("${HOME_DIR}/data", Lookup));
        Assert.Equal("fallback", McpVariables.Expand("${MISSING:-fallback}", Lookup));
        Assert.Equal("", McpVariables.Expand("${MISSING}", Lookup));
    }

    [Fact]
    public void WindowsScriptArgumentsAreQuotedForCmd()
    {
        Assert.Equal("-y", StdioMcpTransport.CmdQuote("-y"));
        Assert.Equal("\"C:\\My Files\"", StdioMcpTransport.CmdQuote("C:\\My Files"));
        Assert.Equal("\"a&b\"", StdioMcpTransport.CmdQuote("a&b"));
        Assert.Equal("\"say \"\"hi\"\"\"", StdioMcpTransport.CmdQuote("say \"hi\""));
    }

    // MARK: Import

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void ImportFindsClaudeCodeClaudeDesktopCursorAndTheProjectsServers()
    {
        var home = _dir["home"];
        var project = _dir["work/app"];
        var other = _dir["work/api"];
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(other);
        Write(Path.Combine(home, ".claude.json"), """
            {"numStartups": 40,
             "mcpServers": {
               "github": {"type":"stdio","command":"npx","args":["-y","@modelcontextprotocol/server-github"],"env":{"GITHUB_TOKEN":"ghp_1"}},
               "linear": {"type":"http","url":"https://mcp.linear.app/mcp","headers":{"Authorization":"Bearer lin_2"}}
             },
             "projects": {
               OTHER: {"mcpServers": {"db": {"command":"uvx","args":["mcp-db"]}}},
               "C:\\elsewhere": {"mcpServers": {"ignored": {"command":"x"}}}
             }}
            """.Replace("OTHER", System.Text.Json.JsonSerializer.Serialize(other)));
        Write(McpImport.ClaudeDesktopConfig(new ExternalLocations(home)), """{"mcpServers":{"filesystem":{"command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","C:\\Users\\me"]}}}""");
        Write(Path.Combine(home, ".cursor", "mcp.json"), """{"mcpServers":{"figma":{"url":"https://figma.example/sse","type":"sse"},"old":{"command":"x","disabled":true}}}""");
        Write(Path.Combine(project, ".mcp.json"), """{"mcpServers":{"playwright":{"command":"npx","args":["@playwright/mcp@latest"]}}}""");

        var found = McpImport.Scan(new ExternalLocations(home), project, [other]);

        var byName = found.ToDictionary(c => c.Server.Name);
        Assert.Equal(["db", "figma", "filesystem", "github", "linear", "old", "playwright"], byName.Keys.Order());
        Assert.Equal((McpTransportKind.Stdio, McpImport.ClaudeCode, (string?)null), (byName["github"].Server.Transport, byName["github"].Server.Source, byName["github"].Server.Project));
        Assert.Equal("ghp_1", byName["github"].Env["GITHUB_TOKEN"]);
        Assert.Equal(["GITHUB_TOKEN"], byName["github"].Server.EnvNames);
        Assert.Equal(McpTransportKind.Http, byName["linear"].Server.Transport);
        Assert.Equal("Bearer lin_2", byName["linear"].Headers["Authorization"]);
        Assert.Equal(("Claude Code (api)", other), (byName["db"].Server.Source, byName["db"].Server.Project));
        Assert.Equal(McpImport.ClaudeDesktop, byName["filesystem"].Server.Source);
        Assert.Equal((McpTransportKind.Http, McpImport.SseCaveat), (byName["figma"].Server.Transport, byName["figma"].Caveat));
        Assert.Equal(McpImport.SseCaveat, byName["figma"].Server.Note);
        Assert.False(byName["old"].Server.Enabled);
        Assert.Equal(("Claude Code (this project)", project), (byName["playwright"].Server.Source, byName["playwright"].Server.Project));
    }

    [Fact]
    public void ImportingAServerVisionHasUpdatesItAndANameClashGetsANumber()
    {
        var home = _dir["home"];
        Write(Path.Combine(home, ".cursor", "mcp.json"), """{"mcpServers":{"github":{"command":"npx","args":["gh"],"env":{"TOKEN":"t1"}}}}""");
        Write(Path.Combine(home, ".claude.json"), """{"mcpServers":{"github":{"command":"uvx","args":["other"]}}}""");
        var store = new McpServerStore(_dir["mcp-servers.json"]);

        Assert.Equal((2, 0), McpImport.Import(store, McpImport.Scan(new ExternalLocations(home), null)));
        Assert.Equal(["github", "github-2"], store.Servers.Select(s => s.Name).Order());
        var again = McpImport.Scan(new ExternalLocations(home), null);
        Assert.All(again, c => Assert.True(c.IsKnown(store.Servers)));
        Assert.Equal((0, 2), McpImport.Import(store, again));
        Assert.Equal(2, store.Servers.Count);
        var cursor = store.Servers.Single(s => s.Source == McpImport.Cursor);
        Assert.Equal("t1", McpServerStore.EnvOf(cursor)["TOKEN"]);
        Assert.DoesNotContain("t1", File.ReadAllText(_dir["mcp-servers.json"]));
    }

    // MARK: Streamable HTTP

    [Fact]
    public async Task StreamableHttpCarriesTheSessionIdAndReadsJsonAndEventStreamAnswers()
    {
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK); // the session's end
            var message = JsonNode.Parse(request.Content!.ReadAsStringAsync().Result)!.AsObject();
            var method = message["method"]!.GetValue<string>();
            var id = message["id"]?.ToJsonString() ?? "null";
            if (method == "initialize")
            {
                var response = FakeHttpHandler.Json("""{"jsonrpc":"2.0","id":ID,"result":{"protocolVersion":"2025-03-26","serverInfo":{"name":"remote","version":"2"},"capabilities":{}}}""".Replace("ID", id));
                response.Headers.Add("Mcp-Session-Id", "session-42");
                return response;
            }
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            // tools/list answers as an event stream, with a notification before the answer.
            var stream = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n\n" +
                         "data: {\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":{\"tools\":[{\"name\":\"search\",\"description\":\"Search\",\"inputSchema\":{\"type\":\"object\"}}]}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        });
        var transport = new HttpMcpTransport(new Uri("https://mcp.example.com/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }, handler: handler);
        await using (var session = new McpSession(transport))
        {
            await session.ConnectAsync(default);
            Assert.Equal("search", Assert.Single(await session.ListToolsAsync(default)).Name);
            Assert.Equal("remote", session.ServerName);
        }
        var requests = handler.Requests;
        Assert.Equal("Bearer abc", requests[0].Headers["Authorization"]);
        Assert.False(requests[0].Headers.ContainsKey("Mcp-Session-Id"));
        Assert.Equal("session-42", requests[2].Headers["Mcp-Session-Id"]);
        Assert.Equal("2025-03-26", requests[2].Headers["MCP-Protocol-Version"]);
        Assert.Contains("text/event-stream", requests[0].Headers["Accept"]);
        Assert.Equal(HttpMethod.Delete, requests[^1].Method);
    }

    [Fact]
    public async Task ARefusedHttpServerSaysItMayNeedASignInHeader()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("no") });
        await using var session = new McpSession(new HttpMcpTransport(new Uri("https://mcp.example.com/mcp"), new Dictionary<string, string>(), handler: handler));
        var error = await Assert.ThrowsAsync<McpException>(() => session.ConnectAsync(default));
        Assert.Contains("may need a sign-in header", error.Message);
    }

    [Fact]
    public async Task AServerThatOnlySpeaksTheOlderSseTransportIsReachedThroughItsEndpoint()
    {
        var posted = new List<string>();
        var stream = new SseFeed();
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } } };
            if (request.RequestUri!.AbsolutePath == "/sse") return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            var body = request.Content!.ReadAsStringAsync().Result;
            posted.Add(body);
            var message = JsonNode.Parse(body)!.AsObject();
            if (message["id"] is { } id)
            {
                var result = message["method"]!.GetValue<string>() == "initialize"
                    ? """{"protocolVersion":"2024-11-05","serverInfo":{"name":"legacy"},"capabilities":{}}"""
                    : """{"tools":[]}""";
                stream.Push("event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":" + id.ToJsonString() + ",\"result\":" + result + "}\n\n");
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        stream.Push("event: endpoint\ndata: /messages?session=7\n\n");
        var transport = new HttpMcpTransport(new Uri("https://legacy.example.com/sse"), new Dictionary<string, string>(), allowLegacy: true, handler: handler);
        await using (var session = new McpSession(transport))
        {
            await session.ConnectAsync(default);
            await session.ListToolsAsync(default);
            Assert.True(transport.UsingLegacySse);
            Assert.Equal("legacy", session.ServerName);
            Assert.Equal(3, posted.Count); // initialize, initialized, tools/list — all to the endpoint
        }
        stream.End();
    }

    /// <summary>A server-sent event stream the test writes to as it goes.</summary>
    private sealed class SseFeed : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[] _current = [];
        private int _offset;

        public void Push(string text) => _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public void End() => _chunks.Writer.TryComplete();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (_chunks.Reader.TryRead(out var next))
                {
                    _current = next;
                    _offset = 0;
                }
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
