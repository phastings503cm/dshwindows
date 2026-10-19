using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Vision.Core;

// MARK: - An MCP server inside Vision, for tests and the self-test
//
// It speaks the real protocol over an in-memory transport: initialize, tools/list, tools/call — with an "echo" tool and an
// "add" tool — so the client, the tool wrapper and the settings page can be exercised without starting a program.

/// <summary>A transport whose other end is a <see cref="McpLoopbackServer"/>.</summary>
public sealed class LoopbackMcpTransport(McpLoopbackServer server) : IMcpTransport
{
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;
    public string? Diagnostics => server.Diagnostics;
    public string? ProtocolVersion { get; set; }
    /// <summary>Every message the client sent, in order (tests read it).</summary>
    public List<JsonObject> Sent { get; } = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (server.FailToStart is { } why) throw new McpException(why);
        return Task.CompletedTask;
    }

    public async Task SendAsync(JsonObject message, CancellationToken cancellationToken)
    {
        lock (Sent) Sent.Add((JsonObject)message.DeepClone());
        foreach (var reply in await server.HandleAsync(message, cancellationToken).ConfigureAwait(false))
            await _incoming.Writer.WriteAsync(reply, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The server speaks first (a notification, or a request of its own).</summary>
    public ValueTask PushAsync(JsonObject message) => _incoming.Writer.WriteAsync(message);

    /// <summary>The server goes away.</summary>
    public void Drop() => _incoming.Writer.TryComplete();

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>A small MCP server: echo and add, plus whatever a test registers.</summary>
public sealed class McpLoopbackServer
{
    public string Name { get; init; } = "loopback";
    /// <summary>Set: starting fails with this.</summary>
    public string? FailToStart { get; init; }
    public string? Diagnostics { get; set; }
    public Dictionary<string, (McpToolInfo Info, Func<JsonObject, JsonObject> Run)> Tools { get; } = new(StringComparer.Ordinal);

    public McpLoopbackServer()
    {
        Add(new McpToolInfo("echo", "Repeat the text back.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""", ReadOnly: true),
            args => Result(JsonArgs.String(args, "text") ?? ""));
        Add(new McpToolInfo("add", "Add two numbers.", """{"type":"object","properties":{"a":{"type":"number"},"b":{"type":"number"}},"required":["a","b"]}"""),
            args => Result((Number(args["a"]) + Number(args["b"])).ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    public void Add(McpToolInfo info, Func<JsonObject, JsonObject> run) => Tools[info.Name] = (info, run);

    public static JsonObject Result(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    /// <summary>A number argument, however it was built (parsed from text or made in memory as an int).</summary>
    private static double Number(JsonNode? node) =>
        node is JsonValue && double.TryParse(node.ToJsonString().Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;

    public Task<IReadOnlyList<JsonObject>> HandleAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var method = McpSession.Text(message, "method");
        var id = message["id"]?.DeepClone();
        if (method is null || id is null) return Task.FromResult<IReadOnlyList<JsonObject>>([]); // answers and notifications
        JsonObject Reply(JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result };
        JsonObject Error(int code, string text) => new() { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = text } };
        var parameters = message["params"] as JsonObject ?? new JsonObject();
        JsonObject reply;
        switch (method)
        {
            case "initialize":
                reply = Reply(new JsonObject
                {
                    ["protocolVersion"] = McpSession.Text(parameters, "protocolVersion") ?? McpSession.LatestProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = true } },
                    ["serverInfo"] = new JsonObject { ["name"] = Name, ["version"] = "1.0" },
                });
                break;
            case "tools/list":
                reply = Reply(new JsonObject
                {
                    ["tools"] = new JsonArray(Tools.Values.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Info.Name,
                        ["description"] = t.Info.Description,
                        ["inputSchema"] = JsonNode.Parse(t.Info.InputSchema),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = t.Info.ReadOnly },
                    }).ToArray()),
                });
                break;
            case "tools/call":
                var name = McpSession.Text(parameters, "name") ?? "";
                reply = Tools.TryGetValue(name, out var tool)
                    ? Reply(tool.Run(parameters["arguments"] as JsonObject ?? new JsonObject()))
                    : Error(-32602, $"Unknown tool: {name}");
                break;
            case "ping":
                reply = Reply(new JsonObject());
                break;
            default:
                reply = Error(-32601, $"Method not found: {method}");
                break;
        }
        return Task.FromResult<IReadOnlyList<JsonObject>>([reply]);
    }
}
