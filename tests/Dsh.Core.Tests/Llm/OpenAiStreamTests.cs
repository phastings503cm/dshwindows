using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from OpenAIStreamTests.swift: what counts as a whole reply, errors reported inside a
/// stream, servers that answer in plain JSON, and an outage ridden out end to end.</summary>
public sealed class OpenAiStreamTests
{
    private static OpenAiClient Client(FakeHttpHandler handler) =>
        new(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "m"), handler);

    private static LlmRequest Request() => new("s", [LlmMessage.User("hi")], [], "m", null, null, null);

    /// <summary>An SSE body streaming <paramref name="text"/> as a few deltas, then finish + [DONE].</summary>
    private static string Sse(string text, bool finish = true, bool done = true)
    {
        var body = new StringBuilder();
        for (var i = 0; i < text.Length; i += 3)
        {
            var piece = text.Substring(i, Math.Min(3, text.Length - i));
            var json = new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { ["content"] = piece } }) };
            body.Append("data: ").Append(json.ToJsonString()).Append("\n\n");
        }
        if (finish) body.Append("""data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""").Append("\n\n");
        if (done) body.Append("data: [DONE]\n\n");
        return body.ToString();
    }

    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK,
                                             string contentType = "text/event-stream") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, contentType) };

    private static async Task<string> CollectAsync(OpenAiClient client)
    {
        var text = new StringBuilder();
        await foreach (var ev in client.StreamAsync(Request()))
        {
            if (ev is LlmStreamEvent.Text t) text.Append(t.Delta);
        }
        return text.ToString();
    }

    [Fact]
    public async Task CompleteStreamIsReturned()
    {
        var text = await CollectAsync(Client(new FakeHttpHandler(_ => Reply(Sse("Hello there")))));
        Assert.Equal("Hello there", text);
    }

    [Fact]
    public async Task StreamCutOffMidReplyIsAnError()
    {
        // The server went away mid-reply: no finish_reason, no [DONE].
        var error = await Assert.ThrowsAsync<LlmException>(() =>
            CollectAsync(Client(new FakeHttpHandler(_ => Reply(Sse("Hel", finish: false, done: false))))));
        Assert.Equal(LlmErrorKind.Sse, error.Kind);
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(error));
    }

    [Fact]
    public async Task EmptyReplyWithoutAnEndIsAnError()
    {
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(_ => Reply("")))));
        Assert.Equal(LlmErrorKind.Sse, error.Kind);
        Assert.Contains("without replying", error.Message);
    }

    [Fact]
    public async Task MidStreamErrorPayloadSurfaces()
    {
        var body = Sse("par", finish: false, done: false)
            + """data: {"object":"error","message":"Internal worker died","type":"InternalServerError","code":503}""" + "\n\n";
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(_ => Reply(body)))));
        Assert.Equal(LlmErrorKind.Http, error.Kind);
        Assert.Equal(503, error.StatusCode);
        Assert.Contains("worker died", error.Body);
    }

    [Fact]
    public async Task PlainJsonReplyIsAccepted()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","content":"plain answer"},"finish_reason":"stop"}]}""";
        var client = Client(new FakeHttpHandler(_ => Reply(body, contentType: "application/json")));
        var text = new StringBuilder();
        var finished = false;
        await foreach (var ev in client.StreamAsync(Request()))
        {
            if (ev is LlmStreamEvent.Text t) text.Append(t.Delta);
            if (ev is LlmStreamEvent.Done) finished = true;
        }
        Assert.True(finished);
        Assert.Equal("plain answer", text.ToString());
    }

    [Fact]
    public async Task PlainJsonToolCallsAreRead()
    {
        const string body = """{"choices":[{"message":{"role":"assistant","content":"","tool_calls":[{"id":"t1","type":"function","function":{"name":"read_file","arguments":"{\"file_path\":\"a.txt\"}"}}]},"finish_reason":"tool_calls"}]}""";
        var client = Client(new FakeHttpHandler(_ => Reply(body, contentType: "application/json")));
        LlmStreamEvent.Done? done = null;
        await foreach (var ev in client.StreamAsync(Request()))
        {
            if (ev is LlmStreamEvent.Done d) done = d;
        }
        var call = Assert.Single(done!.Calls);
        Assert.Equal("read_file", call.Name);
        Assert.Contains("a.txt", call.Arguments);
    }

    [Fact]
    public async Task ServerDownThenUpIsRetriedEndToEnd()
    {
        // 1st: gateway error (nginx in front of a restarting server). 2nd: connection refused.
        // 3rd: the model answers.
        var n = 0;
        var handler = new FakeHttpHandler(request =>
        {
            if (!request.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Interlocked.Increment(ref n) switch
            {
                1 => Reply("<html>Bad Gateway</html>", HttpStatusCode.BadGateway, "text/html"),
                2 => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"),
                _ => Reply(Sse("back online")),
            };
        });
        using var root = new TempDirectory("dsh-stream");
        var engine = new Engine(Client(handler), new ToolRegistry([]), "s",
            new EngineConfig("m") { Retry = new RetryPolicy { Delay = _ => TimeSpan.Zero } },
            root.Path, new PermissionPolicy(PermissionPreset.FullAccess, root.Path), Gates.Allow);
        var result = await engine.RunAsync([], "hi");
        Assert.Equal("back online", result.FinalText);
        Assert.Equal(3, n);
    }

    [Fact]
    public void StreamErrorShapes()
    {
        static JsonObject O(string json) => (JsonObject)JsonNode.Parse(json)!;
        Assert.Equal(400, OpenAiClient.StreamError(O("""{"error":{"message":"x","code":400}}"""))?.Code);
        Assert.Equal(429, OpenAiClient.StreamError(O("""{"error":{"message":"x","code":"429"}}"""))?.Code);
        Assert.Equal("plain", OpenAiClient.StreamError(O("""{"error":"plain"}"""))?.Message);
        Assert.Equal(500, OpenAiClient.StreamError(O("""{"object":"error","message":"m"}"""))?.Code);
        Assert.Null(OpenAiClient.StreamError(O("""{"choices":[]}""")));
    }

    [Fact]
    public async Task FinishReasonErrorIsAFailureNotAReply()
    {
        var body = """data: {"choices":[{"delta":{"content":"par"}}]}""" + "\n\n"
            + """data: {"error":{"message":"provider failed","code":502},"choices":[{"delta":{},"finish_reason":"error"}]}""" + "\n\n";
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(_ => Reply(body)))));
        Assert.Equal(502, error.StatusCode);
    }

    [Fact]
    public async Task ANonModelReplyIsNotRetriedForever()
    {
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(
            _ => Reply("<html>Welcome to nginx</html>", contentType: "text/html")))));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(error));
        Assert.Contains("OpenAI-compatible", error.Message);
    }

    [Fact]
    public async Task RequestTooLargeIsAnOverflow()
    {
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(
            _ => Reply("<html>413 Request Entity Too Large</html>", HttpStatusCode.RequestEntityTooLarge, "text/html")))));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
        Assert.Equal(0, error.Limit);
        Assert.Contains("too long for the server", error.Message);
    }

    [Fact]
    public void SGLangOverflowWordingIsRecognised()
    {
        Assert.Equal(262_144, OpenAiClient.OverflowLimit("The input (270000 tokens) is longer than the model's context length (262144 tokens)."));
        Assert.Equal(131_072, OpenAiClient.OverflowLimit("Requested token count exceeds the model's maximum context length of 131072 tokens"));
    }

    [Fact]
    public void EveryToolSpecParametersAreJsonObjects()
    {
        // SGLang (Pydantic) rejects anything else: five 0.8.0 specs once shipped corrupted.
        using var dir = new TempDirectory("dsh-specs");
        var registry = ToolRegistry.Standard().Adding(ToolRegistry.BackgroundAgentTools())
            .Adding([new QueueAddTool((_, _, _, _) => Task.FromResult("")), new VaultSearchTool(new CredentialVault(dir.Path, new MemoryBlobStore()))]);
        foreach (var spec in registry.Specs)
            Assert.True(JsonNode.Parse(spec.Parameters) is JsonObject { } schema && JsonArgs.String(schema, "type") == "object", spec.Name);
    }

    [Fact]
    public void AnUnparseableSpecStillShipsAnObjectSchema()
    {
        var client = Client(new FakeHttpHandler(_ => Reply(Sse("x"))));
        var body = client.MakeBody(new LlmRequest("s", [LlmMessage.User("hi")],
            [new ToolSpec("broken", "d", "\"{}\""), new ToolSpec("bad", "d", "{not json")], "m", null, null, null));
        var tools = JsonNode.Parse(body)!["tools"]!.AsArray();
        foreach (var tool in tools)
            Assert.IsType<JsonObject>(tool!["function"]!["parameters"]);
    }
}
