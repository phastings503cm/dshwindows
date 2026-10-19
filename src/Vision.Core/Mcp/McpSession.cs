using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Vision.Core;

// MARK: - MCP: the client side of the protocol
//
// JSON-RPC 2.0 over a transport (a program's stdio, or Streamable HTTP): initialize, then tools/list and tools/call. The
// server may ask things of the client too — ping is answered, roots/list gets no roots, anything else "method not found" —
// and may say its tools changed (notifications/tools/list_changed), which makes Vision list them again.

/// <summary>An error from an MCP server (a JSON-RPC error), or from talking to it.</summary>
public sealed class McpException(string message, int? code = null) : Exception(message)
{
    public int? Code { get; } = code;
}

/// <summary>A tool an MCP server offers.</summary>
public sealed record McpToolInfo(string Name, string Description, string InputSchema, bool ReadOnly = false, string? Title = null);

/// <summary>How messages travel between Vision and one MCP server.</summary>
public interface IMcpTransport : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    /// <summary>Send one JSON-RPC message.</summary>
    Task SendAsync(JsonObject message, CancellationToken cancellationToken);
    /// <summary>Messages from the server, in order. Completes when the connection is gone.</summary>
    ChannelReader<JsonObject> Incoming { get; }
    /// <summary>What the server last said on its error output (stdio), or the last HTTP failure — for the settings page.</summary>
    string? Diagnostics { get; }
    /// <summary>Set once the session has agreed a protocol version (HTTP sends it as a header).</summary>
    string? ProtocolVersion { get; set; }
}

/// <summary>One live conversation with an MCP server.</summary>
public sealed class McpSession : IAsyncDisposable
{
    /// <summary>The newest protocol version Vision speaks; older servers answer with theirs.</summary>
    public const string LatestProtocolVersion = "2025-06-18";
    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    private readonly IMcpTransport _transport;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private long _nextId;
    private Task? _reader;

    public McpSession(IMcpTransport transport) => _transport = transport;

    public IMcpTransport Transport => _transport;
    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? Instructions { get; private set; }
    public string? ProtocolVersion { get; private set; }
    /// <summary>The server offers resources (its initialize answer said so).</summary>
    public bool HasResources { get; private set; }
    public bool Closed { get; private set; }

    /// <summary>The server said its tool list changed.</summary>
    public event Action? ToolsChanged;
    /// <summary>The connection is gone.</summary>
    public event Action? Disconnected;

    /// <summary>Start the transport, then initialize. Throws <see cref="McpException"/> when the server doesn't answer as one.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
        _reader = Task.Run(ReadLoopAsync, CancellationToken.None);
        var result = await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = LatestProtocolVersion,
            ["capabilities"] = new JsonObject { ["roots"] = new JsonObject { ["listChanged"] = false } },
            ["clientInfo"] = new JsonObject { ["name"] = "Vision", ["version"] = AppInfo.Version },
        }, cancellationToken).ConfigureAwait(false) as JsonObject ?? throw new McpException("The server's answer to initialize wasn't an object.");
        ProtocolVersion = (result["protocolVersion"] as JsonValue)?.GetValue<string>() ?? LatestProtocolVersion;
        _transport.ProtocolVersion = ProtocolVersion;
        if (result["serverInfo"] is JsonObject info)
        {
            ServerName = Text(info, "name");
            ServerVersion = Text(info, "version");
        }
        Instructions = Text(result, "instructions");
        HasResources = result["capabilities"] is JsonObject capabilities && capabilities["resources"] is JsonObject;
        await NotifyAsync("notifications/initialized", null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Every tool the server offers (following its pages).</summary>
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(CancellationToken cancellationToken)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        for (var page = 0; page < 50; page++)
        {
            var parameters = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor };
            JsonObject? result;
            try
            {
                result = await RequestAsync("tools/list", parameters, cancellationToken).ConfigureAwait(false) as JsonObject;
            }
            catch (McpException error) when (error.Code == -32601)
            {
                break; // a server with no tools at all
            }
            if (result?["tools"] is JsonArray list)
            {
                foreach (var node in list.OfType<JsonObject>())
                {
                    var name = Text(node, "name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var schema = Schema(node["inputSchema"] as JsonObject);
                    var readOnly = node["annotations"] is JsonObject a && a["readOnlyHint"] is JsonValue r && r.TryGetValue<bool>(out var ro) && ro;
                    tools.Add(new McpToolInfo(name, Text(node, "description") ?? "", schema, readOnly, Text(node, "title")));
                }
            }
            cursor = result is null ? null : Text(result, "nextCursor");
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return tools;
    }

    /// <summary>The server's resources, one per line ("uri — name: description"), or the text of the one at <paramref name="uri"/>.</summary>
    public async Task<string> ResourcesAsync(string? uri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            var lines = new List<string>();
            string? cursor = null;
            for (var page = 0; page < 20; page++)
            {
                var result = await RequestAsync("resources/list", cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor },
                    cancellationToken).ConfigureAwait(false) as JsonObject;
                foreach (var item in (result?["resources"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    var description = Text(item, "description") is { Length: > 0 } d ? ": " + d : "";
                    lines.Add($"{Text(item, "uri")} — {Text(item, "name") ?? ""}{description}");
                }
                cursor = result is null ? null : Text(result, "nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return lines.Count == 0 ? "(no resources)" : string.Join("\n", lines);
        }
        var read = await RequestAsync("resources/read", new JsonObject { ["uri"] = uri }, cancellationToken, limited: false).ConfigureAwait(false) as JsonObject;
        var parts = (read?["contents"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(c => Text(c, "text") ?? $"[{Text(c, "uri")} ({Text(c, "mimeType") ?? "binary"}) — not text]").ToList();
        return parts.Count == 0 ? "(empty)" : string.Join("\n", parts);
    }

    /// <summary>Call a tool; the result object ("content", "isError", "structuredContent").</summary>
    public async Task<JsonObject> CallToolAsync(string name, JsonObject arguments, CancellationToken cancellationToken)
    {
        // No limit of its own: the engine's tool timeout (and Stop) cancel it.
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, cancellationToken, limited: false)
            .ConfigureAwait(false);
        return result as JsonObject ?? new JsonObject();
    }

    /// <summary>Send a request and wait for its answer — at most <see cref="RequestTimeout"/> when <paramref name="limited"/>, else
    /// until cancelled.</summary>
    public async Task<JsonNode?> RequestAsync(string method, JsonObject? parameters, CancellationToken cancellationToken, bool limited = true)
    {
        if (Closed) throw new McpException("The server isn't connected.");
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiter;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var limit = RequestTimeout;
        if (limited) linked.CancelAfter(limit);
        try
        {
            await _transport.SendAsync(message, linked.Token).ConfigureAwait(false);
            using (linked.Token.Register(() => waiter.TrySetCanceled(linked.Token)))
                return await waiter.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (Closed) throw new McpException("The server stopped." + Tail());
            // Tell the server to stop working on it.
            try { await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "timeout" }, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw new McpException($"The server didn't answer “{method}” in time.");
        }
        catch (OperationCanceledException)
        {
            try { await NotifyAsync("notifications/cancelled", new JsonObject { ["requestId"] = id, ["reason"] = "cancelled" }, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        return _transport.SendAsync(message, cancellationToken);
    }

    private string Tail() => _transport.Diagnostics is { Length: > 0 } d ? " " + TextUtil.Suffix(d.Trim(), 600) : "";

    private async Task ReadLoopAsync()
    {
        try
        {
            await foreach (var message in _transport.Incoming.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await HandleAsync(message).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is McpException or IOException or InvalidOperationException or JsonException)
                {
                    // One bad message doesn't end the session.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
        catch (ChannelClosedException)
        {
            // The transport closed with an error.
        }
        finally
        {
            Closed = true;
            var reason = new McpException("The server stopped." + Tail());
            foreach (var (_, waiter) in _pending) waiter.TrySetException(reason);
            Disconnected?.Invoke();
        }
    }

    private async Task HandleAsync(JsonObject message)
    {
        var hasId = message.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        var method = Text(message, "method");
        if (method is null)
        {
            // An answer to one of ours.
            if (!hasId || !TryId(idNode!, out var id) || !_pending.TryGetValue(id, out var waiter)) return;
            if (message["error"] is JsonObject error)
            {
                int? code = (error["code"] as JsonValue)?.TryGetValue<int>(out var number) == true ? number : null;
                waiter.TrySetException(new McpException($"The server refused it ({code?.ToString() ?? "?"}): {Text(error, "message") ?? "no reason given"}", code));
            }
            else waiter.TrySetResult(message["result"]?.DeepClone());
            return;
        }
        if (!hasId)
        {
            if (method == "notifications/tools/list_changed") ToolsChanged?.Invoke();
            return;
        }
        // A request from the server.
        JsonObject reply = method switch
        {
            "ping" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode!.DeepClone(), ["result"] = new JsonObject() },
            "roots/list" => new JsonObject { ["jsonrpc"] = "2.0", ["id"] = idNode!.DeepClone(), ["result"] = new JsonObject { ["roots"] = new JsonArray() } },
            _ => new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = idNode!.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Vision doesn't support {method}." },
            },
        };
        await _transport.SendAsync(reply, _stop.Token).ConfigureAwait(false);
    }

    private static bool TryId(JsonNode node, out long id)
    {
        id = 0;
        if (node is not JsonValue value) return false;
        if (value.TryGetValue<long>(out id)) return true;
        if (value.TryGetValue<int>(out var small)) { id = small; return true; }
        if (value.TryGetValue<double>(out var d) && d == Math.Floor(d)) { id = (long)d; return true; }
        return value.TryGetValue<string>(out var s) && long.TryParse(s, out id);
    }

    /// <summary>A tool's input schema as the model APIs want it: an object type with properties, even when the server left them out.</summary>
    internal static string Schema(JsonObject? schema)
    {
        var copy = schema?.DeepClone() as JsonObject ?? new JsonObject();
        if (copy["type"] is null) copy["type"] = "object";
        if (copy["properties"] is null) copy["properties"] = new JsonObject();
        return copy.ToJsonString();
    }

    internal static string? Text(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public async ValueTask DisposeAsync()
    {
        if (!_stop.IsCancellationRequested) await _stop.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        if (_reader is not null)
        {
            try { await _reader.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch (Exception) { /* gone */ }
        }
        _stop.Dispose();
    }
}

/// <summary>${VAR} and ${VAR:-default} in a server's command, arguments, URL, environment and headers — the expansion
/// Claude Code's .mcp.json files are written for.</summary>
public static partial class McpVariables
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-([^}]*))?\}")]
    private static partial Regex Pattern();

    /// <summary>A variable's value: Vision's own environment, else (Windows) the user's environment variables in the registry —
    /// the place a value set after Vision started (or in System Properties) lives. Windows has no login shell to ask.</summary>
    public static string? Lookup(string name)
    {
        if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) return value;
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey("Environment");
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static string Expand(string text, Func<string, string?>? lookup = null)
    {
        if (!text.Contains("${", StringComparison.Ordinal)) return text;
        lookup ??= Lookup;
        return Pattern().Replace(text, m =>
        {
            var value = lookup(m.Groups[1].Value);
            return !string.IsNullOrEmpty(value) ? value : m.Groups[2].Success ? m.Groups[2].Value : "";
        });
    }
}
