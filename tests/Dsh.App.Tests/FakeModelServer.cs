using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.App.Tests;

/// <summary>What the fake server does with one chat request.</summary>
public abstract record FakeReply
{
    public sealed record Text(string Content) : FakeReply;
    public sealed record Http(int Status, string Body) : FakeReply;
    /// <summary>The connection fails (refused, reset) — or times out.</summary>
    public sealed record Transport(bool Timeout = false) : FakeReply;
    /// <summary>Answer after a delay (lets a test stop a task mid-request).</summary>
    public sealed record Slow(TimeSpan Delay, string Content) : FakeReply;
    /// <summary>Answer once the test lets it (so a test decides what happens while the model is still "talking").</summary>
    public sealed record Gated(Task Gate, string Content) : FakeReply;
    /// <summary>Call one tool.</summary>
    public sealed record ToolCall(string Name, string Arguments) : FakeReply;
}

/// <summary>One chat request as the fake server saw it. <paramref name="Host"/> is the server it was sent to
/// (tests with two routes tell them apart by it).</summary>
public sealed record SeenRequest(IReadOnlyList<(string Role, string? Content)> Messages, string Host = "", IReadOnlyList<string>? ToolNames = null)
{
    /// <summary>The tools the request offered the model.</summary>
    public IReadOnlyList<string> Tools => ToolNames ?? [];

    public string LastUser => Messages.LastOrDefault(m => m.Role == "user").Content ?? "";
    public string AllUserText => string.Join("\n", Messages.Where(m => m.Role == "user").Select(m => m.Content ?? ""));
    /// <summary>The request answers a user message (not a tool result).</summary>
    public bool Fresh => Messages.Count > 0 && Messages[^1].Role == "user";
    public string System => Messages.FirstOrDefault(m => m.Role == "system").Content ?? "";

    /// <summary>The request is part of the goal <paramref name="title"/> — its kickoff, a resumed kickoff or
    /// a later round — rather than an earlier task in the same chat.</summary>
    public bool WorkingOn(string title) =>
        LastUser.Contains($"GOAL: {title}\n", StringComparison.Ordinal) || LastUser.Contains($"GOAL (unchanged): {title}\n", StringComparison.Ordinal);

    /// <summary>The goal's first request: its kickoff (fresh or resumed).</summary>
    public bool KickoffOf(string title) => Fresh && LastUser.Contains($"GOAL: {title}\n", StringComparison.Ordinal);
}

/// <summary>An OpenAI-compatible model server in memory: /models answers with one model,
/// /chat/completions asks <see cref="Script"/> what to do.</summary>
public sealed class FakeModelServer : HttpMessageHandler
{
    private readonly Lock _lock = new();
    private readonly List<SeenRequest> _seen = [];
    private Func<SeenRequest, int, FakeReply> _script = (_, _) => new FakeReply.Text("Done.\nGOAL_COMPLETE");

    public IReadOnlyList<SeenRequest> Seen
    {
        get
        {
            lock (_lock) return [.. _seen];
        }
    }

    /// <summary>Forget what was seen and answer with <paramref name="script"/> (request, 1-based index).</summary>
    public void Reset(Func<SeenRequest, int, FakeReply> script)
    {
        lock (_lock)
        {
            _seen.Clear();
            _script = script;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? "";
        if (path.EndsWith("/models", StringComparison.Ordinal))
            return Respond(200, """{"data":[{"id":"stub-model","max_model_len":200000}]}""", "application/json");
        if (!path.EndsWith("chat/completions", StringComparison.Ordinal)) return Respond(404, "not found", "text/plain");

        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        var messages = new List<(string, string?)>();
        var parsed = JsonNode.Parse(body);
        var tools = new List<string>();
        if (parsed?["tools"] is JsonArray toolArray)
        {
            foreach (var tool in toolArray)
            {
                if (tool?["function"]?["name"] is JsonValue name && name.TryGetValue<string>(out var toolName)) tools.Add(toolName);
            }
        }
        if (parsed?["messages"] is JsonArray array)
        {
            foreach (var message in array)
            {
                var content = message?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                messages.Add((message?["role"]?.GetValue<string>() ?? "", content));
            }
        }
        var seen = new SeenRequest(messages, request.RequestUri?.Host ?? "", tools);
        int index;
        Func<SeenRequest, int, FakeReply> script;
        lock (_lock)
        {
            _seen.Add(seen);
            index = _seen.Count;
            script = _script;
        }
        switch (script(seen, index))
        {
            case FakeReply.Text text:
                return Respond(200, Sse(text.Content));
            case FakeReply.Http http:
                return Respond(http.Status, http.Body, "application/json");
            case FakeReply.Transport { Timeout: true }:
                throw new TaskCanceledException("The request timed out.", new TimeoutException());
            case FakeReply.Transport:
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Connection refused");
            case FakeReply.Slow slow:
                await Task.Delay(slow.Delay, cancellationToken);
                return Respond(200, Sse(slow.Content));
            case FakeReply.Gated gated:
                await gated.Gate.WaitAsync(cancellationToken);
                return Respond(200, Sse(gated.Content));
            case FakeReply.ToolCall call:
                var delta = new JsonObject
                {
                    ["choices"] = new JsonArray(new JsonObject
                    {
                        ["delta"] = new JsonObject
                        {
                            ["tool_calls"] = new JsonArray(new JsonObject
                            {
                                ["index"] = 0,
                                ["id"] = $"call_{index}",
                                ["type"] = "function",
                                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = call.Arguments },
                            }),
                        },
                    }),
                };
                return Respond(200, $"data: {delta.ToJsonString()}\n\n"
                                    + """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""" + "\n\ndata: [DONE]\n\n");
            default:
                throw new InvalidOperationException("unknown reply");
        }
    }

    private static HttpResponseMessage Respond(int status, string body, string contentType = "text/event-stream") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    public static string Sse(string text)
    {
        var delta = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = text } }) };
        return $"data: {delta.ToJsonString()}\n\n"
               + """data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":100,"completion_tokens":20}}""" + "\n\n"
               + "data: [DONE]\n\n";
    }
}
