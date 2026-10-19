using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Running the configured MCP servers
//
// Every server that is on is connected when Vision starts (and when Settings changes the list): its program started or its
// URL reached, initialized, and its tools listed. A chat's engine is built with the tools of the servers that apply to its
// project and are connected at that moment; the manager's revision changes whenever that set does, so engines rebuild. A
// server that stops is shown as failed with its last error output; using one of its tools tries once to reconnect.

public enum McpState { Off, Connecting, Connected, Failed }

/// <summary>A server as the settings page shows it.</summary>
public sealed record McpServerStatus(McpServerConfig Config, McpState State, IReadOnlyList<McpToolInfo> Tools, string? Error,
                                     string? ServerName, string? ServerVersion)
{
    /// <summary>"Connected", "Failed: …", "Off", "Connecting…" — the same words on every harness.</summary>
    public string Headline => State switch
    {
        McpState.Off => "Off",
        McpState.Connecting => "Connecting…",
        McpState.Connected => "Connected",
        _ => "Failed: " + (Error ?? "it stopped"),
    };
}

/// <summary>One server's live connection.</summary>
public sealed class McpConnection
{
    private readonly Func<McpServerConfig, IMcpTransport> _transportFor;
    private readonly SemaphoreSlim _connect = new(1, 1);
    private McpSession? _session;

    internal McpConnection(McpServerConfig config, Func<McpServerConfig, IMcpTransport> transportFor)
    {
        Config = config;
        _transportFor = transportFor;
    }

    /// <summary>Its settings (updated in place when only "ask before use" or its name changed — no restart for those).</summary>
    public McpServerConfig Config { get; internal set; }
    public McpState State { get; private set; } = McpState.Off;
    public IReadOnlyList<McpToolInfo> Tools { get; private set; } = [];
    public string? Error { get; private set; }
    public string? ServerName => _session?.ServerName;
    public string? ServerVersion => _session?.ServerVersion;
    /// <summary>The server offers resources (a mcp__&lt;server&gt;__resources tool reads them).</summary>
    public bool HasResources => _session?.HasResources == true;
    internal Action? Changed { get; set; }

    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public McpServerStatus Status => new(Config.Copy(), State, Tools, Error, ServerName, ServerVersion);

    /// <summary>Connect (again): start the transport, initialize, list the tools. Never throws: a failure is the state.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connect.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
            State = McpState.Connecting;
            Error = null;
            Changed?.Invoke();
            IMcpTransport transport;
            try
            {
                transport = _transportFor(Config);
            }
            catch (McpException error)
            {
                Fail(error.Message);
                return;
            }
            var session = new McpSession(transport);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(ConnectTimeout);
            try
            {
                await session.ConnectAsync(limit.Token).ConfigureAwait(false);
                var tools = await session.ListToolsAsync(limit.Token).ConfigureAwait(false);
                _session = session;
                Tools = tools;
                State = McpState.Connected;
                session.ToolsChanged += () => _ = RelistAsync(session);
                session.Disconnected += () =>
                {
                    if (!ReferenceEquals(_session, session)) return;
                    Fail("The server stopped" + (session.Transport.Diagnostics is { Length: > 0 } d ? ": " + TextUtil.Suffix(d.Trim(), 400) : "."));
                };
                Changed?.Invoke();
            }
            catch (Exception error) when (error is McpException or OperationCanceledException or IOException or InvalidOperationException
                                              or System.Text.Json.JsonException or HttpRequestException)
            {
                var why = error is OperationCanceledException && !cancellationToken.IsCancellationRequested
                    ? "The server didn't answer “initialize” in time."
                    : error.Message;
                if (transport.Diagnostics is { Length: > 0 } diagnostics && !why.Contains(diagnostics, StringComparison.Ordinal))
                    why += " " + TextUtil.Suffix(diagnostics.Trim(), 400);
                await session.DisposeAsync().ConfigureAwait(false);
                Fail(why);
            }
        }
        finally
        {
            _connect.Release();
        }
    }

    private void Fail(string why)
    {
        State = McpState.Failed;
        Error = why.Trim();
        Tools = [];
        Changed?.Invoke();
    }

    private async Task RelistAsync(McpSession session)
    {
        try
        {
            var tools = await session.ListToolsAsync(CancellationToken.None).ConfigureAwait(false);
            if (!ReferenceEquals(_session, session)) return;
            Tools = tools;
            Changed?.Invoke();
        }
        catch (McpException)
        {
            // Keep the list it had.
        }
    }

    /// <summary>Call one of its tools — reconnecting once first when the server stopped.</summary>
    public async Task<JsonObject> CallAsync(string tool, JsonObject arguments, CancellationToken cancellationToken) =>
        await (await LiveAsync(cancellationToken).ConfigureAwait(false)).CallToolAsync(tool, arguments, cancellationToken).ConfigureAwait(false);

    /// <summary>Its resources (a list), or the one at <paramref name="uri"/>.</summary>
    public async Task<string> ResourcesAsync(string? uri, CancellationToken cancellationToken) =>
        await (await LiveAsync(cancellationToken).ConfigureAwait(false)).ResourcesAsync(uri, cancellationToken).ConfigureAwait(false);

    private async Task<McpSession> LiveAsync(CancellationToken cancellationToken)
    {
        if (_session is { Closed: false } live) return live;
        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return _session is { Closed: false } again ? again : throw new McpException(Error ?? "the server isn't connected.");
    }

    public async Task StopAsync()
    {
        await _connect.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseSessionAsync().ConfigureAwait(false);
            State = McpState.Off;
            Tools = [];
            Error = null;
            Changed?.Invoke();
        }
        finally
        {
            _connect.Release();
        }
    }

    private async Task CloseSessionAsync()
    {
        if (_session is not { } session) return;
        _session = null;
        await session.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The configured servers and their connections.</summary>
public sealed class McpManager : IAsyncDisposable
{
    private readonly McpServerStore _store;
    private readonly Dictionary<string, McpConnection> _connections = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private int _revision;

    /// <summary>Tests and the self-test: a transport of their own for a server (null = the real one).</summary>
    public static Func<McpServerConfig, IMcpTransport?>? TransportOverride { get; set; }

    /// <summary>HTTP servers' handler (tests).</summary>
    public HttpMessageHandler? HttpHandler { get; init; }

    public McpManager(McpServerStore store)
    {
        _store = store;
    }

    public McpServerStore Store => _store;

    /// <summary>Changes whenever a server connects, fails, stops or lists different tools (engines key on it).</summary>
    public int Revision => Volatile.Read(ref _revision);

    /// <summary>Raised (on any thread) when <see cref="Revision"/> changes.</summary>
    public event Action? Changed;

    private void Bump()
    {
        Interlocked.Increment(ref _revision);
        Changed?.Invoke();
    }

    public IReadOnlyList<McpServerStatus> Statuses
    {
        get
        {
            var configs = _store.Servers;
            lock (_lock)
                return configs.Select(c => _connections.TryGetValue(c.Id, out var live) && SameLaunch(live.Config, c)
                    ? live.Status with { Config = c }
                    : new McpServerStatus(c, c.Enabled ? McpState.Connecting : McpState.Off, [], null, null, null)).ToList();
        }
    }

    /// <summary>Bring the connections in line with the list: connect servers that are on and not connected (or whose settings
    /// changed), stop those that are off or gone. Returns when every connection attempt has finished.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        var configs = _store.Servers;
        var stop = new List<McpConnection>();
        var start = new List<McpConnection>();
        var updated = false;
        lock (_lock)
        {
            foreach (var (id, live) in _connections.ToList())
            {
                var config = configs.FirstOrDefault(c => c.Id == id);
                if (config is { Enabled: true } && SameLaunch(live.Config, config))
                {
                    if (live.Config.Name != config.Name || live.Config.Trust != config.Trust || live.Config.Note != config.Note
                        || !live.Config.DisabledTools.SequenceEqual(config.DisabledTools))
                    {
                        live.Config = config;
                        updated = true;
                    }
                    continue;
                }
                stop.Add(live);
                _connections.Remove(id);
            }
            foreach (var config in configs.Where(c => c.Enabled && !_connections.ContainsKey(c.Id)))
            {
                var connection = new McpConnection(config, TransportFor) { Changed = Bump };
                _connections[config.Id] = connection;
                start.Add(connection);
            }
        }
        foreach (var connection in stop) await connection.StopAsync().ConfigureAwait(false);
        if (stop.Count > 0 || updated) Bump();
        await Task.WhenAll(start.Select(c => c.ConnectAsync(cancellationToken))).ConfigureAwait(false);
    }

    /// <summary>Reconnect every server that is on (/mcp reload).</summary>
    public async Task ReconnectAllAsync()
    {
        List<McpConnection> all;
        lock (_lock) all = _connections.Values.ToList();
        await Task.WhenAll(all.Select(c => c.ConnectAsync())).ConfigureAwait(false);
        await SyncAsync().ConfigureAwait(false);
    }

    /// <summary>Reconnect one server (the settings page's Reconnect).</summary>
    public async Task RestartAsync(string id)
    {
        McpConnection? connection;
        lock (_lock) _connections.TryGetValue(id, out connection);
        if (connection is null) await SyncAsync().ConfigureAwait(false);
        else await connection.ConnectAsync().ConfigureAwait(false);
    }

    /// <summary>The tools a chat working in <paramref name="project"/> is offered: those of every connected server that applies
    /// there (but the ones switched off in Settings), and each one's resources tool, except names in <paramref name="taken"/>
    /// (built-ins always win).</summary>
    public IReadOnlyList<IToolExecutor> ToolsFor(string? project, IReadOnlyCollection<string>? taken = null)
    {
        var output = new List<IToolExecutor>();
        var names = new HashSet<string>(taken ?? [], StringComparer.Ordinal);
        foreach (var connection in Live(project))
        {
            foreach (var info in connection.Tools.Where(t => !connection.Config.DisabledTools.Contains(t.Name)))
            {
                var tool = new McpTool(connection, info);
                if (names.Add(tool.Name)) output.Add(tool);
            }
            if (connection.HasResources && new McpTool(connection, null) is var resources && names.Add(resources.Name)) output.Add(resources);
        }
        return output;
    }

    private List<McpConnection> Live(string? project)
    {
        lock (_lock)
            return _connections.Values.Where(c => c.State == McpState.Connected && c.Config.Enabled && c.Config.AppliesTo(project))
                .OrderBy(c => c.Config.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>For the system prompt: which MCP servers are connected (null when none) — Vision for macOS's words.</summary>
    public string? PromptSection(string? project)
    {
        var live = Live(project);
        if (live.Count == 0) return null;
        return "--- MCP servers ---\nMCP servers connected (their tools are named mcp__<server>__<tool>): " +
               string.Join(", ", live.Select(c => $"{c.Config.Name} ({c.Tools.Count} tool{(c.Tools.Count == 1 ? "" : "s")})")) +
               ". Each call asks the user first unless they allowed that server.";
    }

    /// <summary>Whether two settings start the same connection (its name or "ask before use" changing needs no restart).</summary>
    private static bool SameLaunch(McpServerConfig a, McpServerConfig b) =>
        a.Transport == b.Transport && a.Command == b.Command && a.Url == b.Url && a.Cwd == b.Cwd && a.Project == b.Project
        && a.Args.SequenceEqual(b.Args) && a.EnvNames.SequenceEqual(b.EnvNames) && a.HeaderNames.SequenceEqual(b.HeaderNames);

    private IMcpTransport TransportFor(McpServerConfig config)
    {
        if (TransportOverride?.Invoke(config) is { } custom) return custom;
        var env = McpServerStore.EnvOf(config).ToDictionary(e => e.Key, e => McpVariables.Expand(e.Value), StringComparer.Ordinal);
        switch (config.Transport)
        {
            case McpTransportKind.Stdio:
                if (string.IsNullOrWhiteSpace(config.Command)) throw new McpException("no command is set.");
                var cwd = config.Cwd is { Length: > 0 } c ? McpVariables.Expand(c) : config.Project;
                return new StdioMcpTransport(McpVariables.Expand(config.Command), config.Args.Select(a => McpVariables.Expand(a)).ToList(), cwd, env);
            default:
                if (!Uri.TryCreate(McpVariables.Expand(config.Url ?? ""), UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                    throw new McpException("its URL isn't an http(s) address.");
                var headers = McpServerStore.HeadersOf(config).ToDictionary(h => h.Key, h => McpVariables.Expand(h.Value), StringComparer.OrdinalIgnoreCase);
                // A server that only speaks the older SSE transport is reached through it.
                return new HttpMcpTransport(url, headers, allowLegacy: true, HttpHandler);
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<McpConnection> all;
        lock (_lock)
        {
            all = _connections.Values.ToList();
            _connections.Clear();
        }
        foreach (var connection in all) await connection.StopAsync().ConfigureAwait(false);
    }
}
