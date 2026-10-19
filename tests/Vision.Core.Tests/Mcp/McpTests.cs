using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>MCP servers (10-19 #20): the protocol, the tools as the model sees them, the transports, the manager, and
/// importing what Claude Code, Claude Desktop and Cursor have.</summary>
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
        var server = new McpLoopbackServer { Name = "demo" };
        var transport = new LoopbackMcpTransport(server);
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);

        Assert.Equal("demo", session.ServerName);
        Assert.Equal(McpSession.LatestProtocolVersion, session.ProtocolVersion);
        Assert.Equal(McpSession.LatestProtocolVersion, transport.ProtocolVersion);
        var initialize = transport.Sent[0];
        Assert.Equal("initialize", initialize["method"]!.GetValue<string>());
        Assert.Equal("Vision", initialize["params"]!["clientInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("notifications/initialized", transport.Sent[1]["method"]!.GetValue<string>());
        Assert.Null(transport.Sent[1]["id"]);

        var tools = await session.ListToolsAsync(default);
        Assert.Equal(["echo", "add"], tools.Select(t => t.Name));
        Assert.True(tools[0].ReadOnly);

        var result = await session.CallToolAsync("add", new JsonObject { ["a"] = 2, ["b"] = 3 }, default);
        Assert.Equal("5", McpTool.Present(result).Output);
    }

    [Fact]
    public async Task AServerErrorBecomesAnMcpExceptionWithItsMessage()
    {
        await using var session = new McpSession(new LoopbackMcpTransport(new McpLoopbackServer()));
        await session.ConnectAsync(default);
        var error = await Assert.ThrowsAsync<McpException>(() => session.CallToolAsync("missing", new JsonObject(), default));
        Assert.Equal("Unknown tool: missing", error.Message);
        Assert.Equal(-32602, error.Code);
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

        var pong = transport.Sent.Single(m => m["id"]?.ToString() == "srv-1");
        Assert.NotNull(pong["result"]);
    }

    [Fact]
    public async Task AServerThatGoesAwayFailsTheWaitingRequestInsteadOfHanging()
    {
        var server = new McpLoopbackServer();
        server.Add(new McpToolInfo("hang", "never answers", """{"type":"object"}"""), _ => throw new InvalidOperationException("unreachable"));
        var transport = new HangingTransport(new LoopbackMcpTransport(server));
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);
        var call = session.CallToolAsync("hang", new JsonObject(), default);
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
    [InlineData("my-server", "files.read", "mcp__my-server__files_read")]
    [InlineData("db", "run query!", "mcp__db__run_query_")]
    public void ToolNamesArePrefixedAndSafe(string server, string tool, string expected) =>
        Assert.Equal(expected, McpTool.NameFor(server, tool));

    [Fact]
    public void ALongNameIsCutWithAHashSoTwoStayApart()
    {
        var a = McpTool.NameFor("server", new string('a', 80) + "_one");
        var b = McpTool.NameFor("server", new string('a', 80) + "_two");
        Assert.Equal(McpTool.MaxNameLength, a.Length);
        Assert.NotEqual(a, b);
        Assert.StartsWith("mcp__server__aaa", a);
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
        Assert.Equal("""{"total":3}""", McpTool.Present(JsonNode.Parse("""{"content":[],"structuredContent":{"total":3}}""")!.AsObject()).Output);
    }

    [Fact]
    public async Task AToolAsksOnceInAChatAndThenRuns()
    {
        var store = new McpServerStore(_dir["mcp.json"]);
        store.Save(new McpServerConfig { Id = "demo", Name = "demo", Command = "x" });
        McpManager.TransportOverride = _ => new LoopbackMcpTransport(new McpLoopbackServer());
        await using var manager = new McpManager(store);
        await manager.SyncAsync();
        var grants = new McpGrants();
        var echo = manager.ToolsFor(null, grants).Single(t => t.Name == "mcp__demo__echo");
        Assert.Contains("(MCP server “demo”)", echo.Spec.Description);

        var asked = new List<string>();
        var answer = false;
        using var tools = new ToolTestContext();
        var context = tools.Context with
        {
            RequestPermission = (_, name, detail) =>
            {
                asked.Add(detail);
                return Task.FromResult(answer);
            },
        };
        Assert.Equal("Error: the user declined to run this MCP tool.", (await echo.ExecuteAsync("""{"text":"hi"}""", context, default)).Output);
        answer = true;
        Assert.Equal("hi", (await echo.ExecuteAsync("""{"text":"hi"}""", context, default)).Output);
        Assert.Equal("again", (await echo.ExecuteAsync("""{"text":"again"}""", context, default)).Output);
        Assert.Equal(2, asked.Count);
        Assert.Contains("MCP server “demo” wants to run echo", asked[0]);

        // A server set to run its tools without asking doesn't ask.
        store.SetAskBeforeUse("demo", false);
        await manager.SyncAsync();
        var fresh = manager.ToolsFor(null, new McpGrants()).Single(t => t.Name == "mcp__demo__add");
        Assert.Equal("3", (await fresh.ExecuteAsync("""{"a":1,"b":2}""", context, default)).Output);
        Assert.Equal(2, asked.Count);
    }

    [Fact]
    public async Task InASensitiveChatAServerOnSomeoneElsesMachineAsksFirstWhateverItsSetting()
    {
        var store = new McpServerStore(_dir["mcp.json"]);
        store.Save(new McpServerConfig { Id = "remote", Name = "remote", Transport = McpTransportKind.Http, Url = "https://mcp.example.com/mcp", AskBeforeUse = false });
        store.Save(new McpServerConfig { Id = "local", Name = "local", Command = "x", AskBeforeUse = false });
        McpManager.TransportOverride = _ => new LoopbackMcpTransport(new McpLoopbackServer());
        await using var manager = new McpManager(store);
        await manager.SyncAsync();
        var asked = new List<string>();
        using var tools = new ToolTestContext();
        var context = tools.Context with
        {
            Sensitive = true,
            RequestPermission = (_, _, detail) =>
            {
                asked.Add(detail);
                return Task.FromResult(true);
            },
        };
        var all = manager.ToolsFor(null, new McpGrants());
        await all.Single(t => t.Name == "mcp__local__echo").ExecuteAsync("""{"text":"a"}""", context, default);
        Assert.Empty(asked);
        await all.Single(t => t.Name == "mcp__remote__echo").ExecuteAsync("""{"text":"a"}""", context, default);
        Assert.Contains("This chat is Sensitive: what echo is given goes to mcp.example.com.", Assert.Single(asked));
        // Once allowed in the chat, it runs.
        await all.Single(t => t.Name == "mcp__remote__echo").ExecuteAsync("""{"text":"b"}""", context, default);
        Assert.Single(asked);
    }

    // MARK: The manager

    [Fact]
    public async Task TheManagerConnectsWhatIsOnShowsFailuresAndOffersToolsByProject()
    {
        var store = new McpServerStore(_dir["mcp.json"]);
        store.Save(new McpServerConfig { Id = "everywhere", Name = "everywhere", Command = "x" });
        store.Save(new McpServerConfig { Id = "project", Name = "project", Command = "x", Project = _dir.Path });
        store.Save(new McpServerConfig { Id = "broken", Name = "broken", Command = "x" });
        store.Save(new McpServerConfig { Id = "off", Name = "off", Command = "x", Enabled = false });
        McpManager.TransportOverride = config => new LoopbackMcpTransport(config.Id == "broken"
            ? new McpLoopbackServer { FailToStart = "Couldn't start x: not found." }
            : new McpLoopbackServer());
        await using var manager = new McpManager(store);
        await manager.SyncAsync();

        var statuses = manager.Statuses.ToDictionary(s => s.Config.Id);
        Assert.Equal("Connected · 2 tools", statuses["everywhere"].Headline);
        Assert.Equal(McpState.Failed, statuses["broken"].State);
        Assert.Equal("Failed: Couldn't start x: not found.", statuses["broken"].Headline);
        Assert.Equal("Off", statuses["off"].Headline);

        Assert.Equal(2, manager.ToolsFor(null, new McpGrants()).Count);
        Assert.Equal(4, manager.ToolsFor(_dir.Path, new McpGrants()).Count);
        // Built-ins (and tools added before) keep their names.
        Assert.Single(manager.ToolsFor(null, new McpGrants(), ["mcp__everywhere__echo"]));
        Assert.Contains("everywhere (2 tools)", manager.PromptSection(null));

        var revision = manager.Revision;
        store.SetEnabled("everywhere", false);
        await manager.SyncAsync();
        Assert.True(manager.Revision > revision);
        Assert.Empty(manager.ToolsFor(null, new McpGrants()));
    }

    // MARK: Settings and secrets

    [Fact]
    public void SecretsGoToTheCredentialStoreAndOnlyTheirNamesToTheFile()
    {
        var path = _dir["mcp.json"];
        var store = new McpServerStore(path);
        store.Save(new McpServerConfig { Id = "gh", Name = "GitHub", Command = "npx", Args = ["-y", "server-github"] },
            env: new Dictionary<string, string> { ["GITHUB_TOKEN"] = "ghp_secret123" },
            headers: new Dictionary<string, string>());

        var file = File.ReadAllText(path);
        Assert.Contains("GITHUB_TOKEN", file);
        Assert.DoesNotContain("ghp_secret123", file);
        Assert.Equal("ghp_secret123", SecretStore.Read("Vision/mcp/gh/env/GITHUB_TOKEN"));
        var reloaded = new McpServerStore(path).Find("gh")!;
        Assert.Equal("ghp_secret123", McpServerStore.EnvOf(reloaded)["GITHUB_TOKEN"]);

        store.Remove("gh");
        Assert.Null(SecretStore.Read("Vision/mcp/gh/env/GITHUB_TOKEN"));
    }

    [Fact]
    public void VariablesExpandTheWayClaudeCodesFilesExpectThem()
    {
        string? Lookup(string name) => name == "HOME_DIR" ? "/home/me" : null;
        Assert.Equal("/home/me/data", McpVariables.Expand("${HOME_DIR}/data", Lookup));
        Assert.Equal("fallback", McpVariables.Expand("${MISSING:-fallback}", Lookup));
        Assert.Equal("", McpVariables.Expand("${MISSING}", Lookup));
        Assert.Equal("no vars", McpVariables.Expand("no vars", Lookup));
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

    [Fact]
    public void ImportFindsClaudeCodeClaudeDesktopCursorAndProjectServers()
    {
        var home = _dir["home"];
        var project = _dir["work/app"];
        Directory.CreateDirectory(project);
        Write(Path.Combine(home, ".claude.json"), """
            {"numStartups": 40,
             "mcpServers": {
               "github": {"type":"stdio","command":"npx","args":["-y","@modelcontextprotocol/server-github"],"env":{"GITHUB_TOKEN":"ghp_1"}},
               "linear": {"type":"http","url":"https://mcp.linear.app/mcp","headers":{"Authorization":"Bearer lin_2"}}
             },
             "projects": {"C:\\work\\api": {"mcpServers": {"db": {"command":"uvx","args":["mcp-db"]}}}}}
            """);
        Write(McpImport.ClaudeDesktopConfig(new ExternalLocations(home)), """{"mcpServers":{"filesystem":{"command":"npx","args":["-y","@modelcontextprotocol/server-filesystem","C:\\Users\\me"]}}}""");
        Write(Path.Combine(home, ".cursor", "mcp.json"), """{"mcpServers":{"figma":{"url":"https://figma.example/sse","type":"sse"},"old":{"command":"x","disabled":true}}}""");
        Write(Path.Combine(project, ".mcp.json"), """{"mcpServers":{"playwright":{"command":"npx","args":["@playwright/mcp@latest"]}}}""");

        var found = McpImport.Scan(new ExternalLocations(home), [project]);

        var byName = found.ToDictionary(c => c.Server.Name);
        Assert.Equal(["db", "figma", "filesystem", "github", "linear", "old", "playwright"], byName.Keys.Order());
        Assert.Equal(McpTransportKind.Stdio, byName["github"].Server.Transport);
        Assert.Equal("ghp_1", byName["github"].Env["GITHUB_TOKEN"]);
        Assert.Equal(McpImport.ClaudeCode, byName["github"].Server.Source);
        Assert.Null(byName["github"].Server.Project);
        Assert.Equal(McpTransportKind.Http, byName["linear"].Server.Transport);
        Assert.Equal("Bearer lin_2", byName["linear"].Headers["Authorization"]);
        Assert.Equal("C:\\work\\api", byName["db"].Server.Project);
        Assert.Equal(McpImport.ClaudeDesktop, byName["filesystem"].Server.Source);
        Assert.Equal(McpTransportKind.Sse, byName["figma"].Server.Transport);
        Assert.False(byName["old"].Server.Enabled);
        Assert.Equal(McpImport.ProjectFile, byName["playwright"].Server.Source);
        Assert.Equal(project, byName["playwright"].Server.Project);
    }

    [Fact]
    public void ImportingTwiceUpdatesInsteadOfDuplicatingAndSecretsLeaveTheirFiles()
    {
        var home = _dir["home"];
        Write(Path.Combine(home, ".cursor", "mcp.json"), """{"mcpServers":{"github":{"command":"npx","env":{"TOKEN":"t1"}}}}""");
        Write(Path.Combine(home, ".claude.json"), """{"mcpServers":{"github":{"command":"npx"}}}""");
        var store = new McpServerStore(_dir["mcp.json"]);

        var first = McpImport.Import(store, McpImport.Scan(new ExternalLocations(home), []));
        var second = McpImport.Import(store, McpImport.Scan(new ExternalLocations(home), []));

        Assert.Equal((2, 0), first);
        Assert.Equal((0, 2), second);
        Assert.Equal(["github", "github-2"], store.Servers.Select(s => s.Id).Order());
        var cursor = store.Servers.Single(s => s.Source == McpImport.Cursor);
        Assert.Equal("t1", McpServerStore.EnvOf(cursor)["TOKEN"]);
        Assert.DoesNotContain("t1", File.ReadAllText(_dir["mcp.json"]));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    // MARK: Streamable HTTP

    [Fact]
    public async Task StreamableHttpCarriesTheSessionIdAndReadsJsonAndEventStreamAnswers()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(request =>
        {
            calls++;
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.OK); // the session's end
            var body = request.Content!.ReadAsStringAsync().Result;
            var message = JsonNode.Parse(body)!.AsObject();
            var method = message["method"]!.GetValue<string>();
            var id = message["id"]?.DeepClone();
            if (method == "initialize")
            {
                var response = FakeHttpHandler.Json("""{"jsonrpc":"2.0","id":ID,"result":{"protocolVersion":"2025-03-26","serverInfo":{"name":"remote","version":"2"},"capabilities":{}}}""".Replace("ID", id!.ToJsonString()));
                response.Headers.Add("Mcp-Session-Id", "session-42");
                return response;
            }
            if (method == "notifications/initialized") return new HttpResponseMessage(HttpStatusCode.Accepted);
            // tools/list answers as an event stream, with a notification before the answer.
            var stream = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\",\"params\":{}}\n\n" +
                         $"data: {{\"jsonrpc\":\"2.0\",\"id\":{id!.ToJsonString()},\"result\":{{\"tools\":[{{\"name\":\"search\",\"description\":\"Search\",\"inputSchema\":{{\"type\":\"object\"}}}}]}}}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "text/event-stream") };
        });
        var transport = new HttpMcpTransport(new Uri("https://mcp.example.com/mcp"), new Dictionary<string, string> { ["Authorization"] = "Bearer abc" }, handler: handler);
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);
        var tools = await session.ListToolsAsync(default);

        Assert.Equal("search", Assert.Single(tools).Name);
        Assert.Equal("remote", session.ServerName);
        var requests = handler.Requests;
        Assert.Equal("Bearer abc", requests[0].Headers["Authorization"]);
        Assert.False(requests[0].Headers.ContainsKey("Mcp-Session-Id"));
        Assert.Equal("session-42", requests[2].Headers["Mcp-Session-Id"]);
        Assert.Equal("2025-03-26", requests[2].Headers["MCP-Protocol-Version"]);
        Assert.Contains("text/event-stream", requests[0].Headers["Accept"]);
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
    public async Task AnSseServerThatOnlySpeaksTheOldTransportIsReachedThroughItsEndpoint()
    {
        var posted = new List<string>();
        var stream = new SseFeed();
        var handler = new FakeHttpHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } } };
            var body = request.Content!.ReadAsStringAsync().Result;
            if (request.RequestUri!.AbsolutePath == "/sse") return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            posted.Add(body);
            var message = JsonNode.Parse(body)!.AsObject();
            if (message["id"] is { } id)
            {
                var result = message["method"]!.GetValue<string>() == "initialize"
                    ? """{"protocolVersion":"2024-11-05","serverInfo":{"name":"legacy"},"capabilities":{}}"""
                    : """{"tools":[]}""";
                stream.Push($"event: message\ndata: {{\"jsonrpc\":\"2.0\",\"id\":{id.ToJsonString()},\"result\":{result}}}\n\n");
            }
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        });
        stream.Push("event: endpoint\ndata: /messages?session=7\n\n");
        var transport = new HttpMcpTransport(new Uri("https://legacy.example.com/sse"), new Dictionary<string, string>(), allowLegacy: true, handler: handler);
        await using var session = new McpSession(transport);
        await session.ConnectAsync(default);
        await session.ListToolsAsync(default);

        Assert.True(transport.UsingLegacySse);
        Assert.Equal("legacy", session.ServerName);
        Assert.Equal(3, posted.Count); // initialize, initialized, tools/list — all to the endpoint
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
