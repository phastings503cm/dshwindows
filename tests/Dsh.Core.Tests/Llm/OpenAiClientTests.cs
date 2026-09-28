using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from OpenAIClientTests.swift: system messages on the wire.</summary>
public sealed class OpenAiClientTests
{
    private static OpenAiClient Client() =>
        new(new ProviderProfile(ProviderKind.OpenAICompat, "test", "http://127.0.0.1:8000/v1", "test-model"));

    private static JsonArray WireMessages(LlmRequest request) =>
        JsonNode.Parse(Client().MakeBody(request))!["messages"]!.AsArray();

    private static string? Str(JsonNode? node) => node?.GetValue<string>();

    [Fact]
    public void SystemPromptIsTheOnlySystemMessage()
    {
        var messages = WireMessages(new LlmRequest("You are a helpful agent.", [LlmMessage.User("hi")], [], "test-model"));
        Assert.Equal(2, messages.Count);
        Assert.Equal("system", Str(messages[0]!["role"]));
        Assert.Equal("You are a helpful agent.", Str(messages[0]!["content"]));
        Assert.Equal("user", Str(messages[1]!["role"]));
    }

    /// <summary>Regression: compaction inserts a system-role continuity note mid-transcript. A second
    /// system message anywhere but index 0 makes strict servers (SGLang on a DGX Spark) reply 400, so
    /// the prompt and every inline system message collapse into one leading system message.</summary>
    [Fact]
    public void CompactedSystemNoteMergesIntoLeadingSystemMessage()
    {
        var messages = WireMessages(new LlmRequest("You are a helpful agent.",
        [
            LlmMessage.SystemText(Compaction.SummaryHeader + "Earlier work: did X, Y, Z."),
            LlmMessage.User("keep going"),
            LlmMessage.Assistant("sure", []),
        ], [], "test-model"));

        Assert.Equal(["system", "user", "assistant"], messages.Select(m => Str(m!["role"])));
        var content = Str(messages[0]!["content"]) ?? "";
        Assert.Contains("You are a helpful agent.", content);
        Assert.Contains("Earlier work: did X, Y, Z.", content);
    }

    [Fact]
    public void NoSystemPromptStillMergesInlineSystemMessages()
    {
        var messages = WireMessages(new LlmRequest("",
            [LlmMessage.SystemText("note one"), LlmMessage.SystemText("note two"), LlmMessage.User("hi")], [], "test-model"));
        Assert.Equal(2, messages.Count); // the two system notes merge into one
        Assert.Equal("system", Str(messages[0]!["role"]));
        Assert.Equal("note one\n\nnote two", Str(messages[0]!["content"]));
    }
}

/// <summary>New: the rest of the request body, and the streaming parser against an in-memory server
/// (the Swift suite only covered these against a live one).</summary>
public sealed class OpenAiClientWireTests
{
    private const string BaseUrl = "http://127.0.0.1:8000/v1";

    private static ProviderProfile Profile(ProviderKind kind = ProviderKind.OpenAICompat) =>
        new(kind, "test", BaseUrl, "test-model");

    private static JsonObject Body(LlmRequest request, ProviderProfile? profile = null) =>
        JsonNode.Parse(new OpenAiClient(profile ?? Profile()).MakeBody(request))!.AsObject();

    private static LlmRequest Hi(string model = "test-model", double? temperature = null, int? maxTokens = null,
                                 IReadOnlyList<ToolSpec>? tools = null) =>
        new("", [LlmMessage.User("hi")], tools ?? [], model, temperature, maxTokens);

    private static async Task<List<LlmStreamEvent>> Collect(ILlmClient client, LlmRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var ev in client.StreamAsync(request)) events.Add(ev);
        return events;
    }

    [Fact]
    public void BodyStreamsAndAsksForUsageOnlyWhereSupported()
    {
        var body = Body(Hi());
        Assert.Equal("test-model", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.True(body["stream_options"]!["include_usage"]!.GetValue<bool>());
        // Ollama 400s on unknown fields.
        Assert.Null(Body(Hi(), Profile(ProviderKind.Ollama))["stream_options"]);
    }

    [Fact]
    public void RequestModelWinsAndEmptyFallsBackToTheProfile()
    {
        Assert.Equal("other", Body(Hi(model: "other"))["model"]!.GetValue<string>());
        Assert.Equal("test-model", Body(Hi(model: ""))["model"]!.GetValue<string>());
    }

    [Fact]
    public void SamplingSettingsComeFromTheRequestThenTheProfile()
    {
        var fromRequest = Body(Hi(temperature: 0.2, maxTokens: 100));
        Assert.Equal(0.2, fromRequest["temperature"]!.GetValue<double>());
        Assert.Equal(100, fromRequest["max_tokens"]!.GetValue<int>());

        var profile = Profile() with { Temperature = 0.7, MaxOutputTokens = 2048 };
        var fromProfile = Body(Hi(), profile);
        Assert.Equal(0.7, fromProfile["temperature"]!.GetValue<double>());
        Assert.Equal(2048, fromProfile["max_tokens"]!.GetValue<int>());

        var neither = Body(Hi());
        Assert.Null(neither["temperature"]);
        Assert.Null(neither["max_tokens"]);
    }

    [Fact]
    public void ToolSchemasAreSentAsObjectsAndBadOnesBecomeEmpty()
    {
        var body = Body(Hi(tools:
        [
            new ToolSpec("good", "a tool", """{"type":"object","properties":{"a":{"type":"string"}}}"""),
            new ToolSpec("bad", "broken schema", "{not json"),
        ]));
        var tools = body["tools"]!.AsArray();
        Assert.Equal("function", tools[0]!["type"]!.GetValue<string>());
        Assert.Equal("good", tools[0]!["function"]!["name"]!.GetValue<string>());
        Assert.Equal("string", tools[0]!["function"]!["parameters"]!["properties"]!["a"]!["type"]!.GetValue<string>());
        Assert.Equal("""{"type":"object","properties":{}}""", tools[1]!["function"]!["parameters"]!.ToJsonString());
        Assert.Null(Body(Hi())["tools"]); // no tools → no field at all
    }

    [Fact]
    public void ToolCallsAndResultsKeepTheirShape()
    {
        var body = Body(new LlmRequest("", [
            LlmMessage.User("go"),
            LlmMessage.Assistant("", [new ToolCall("call_1", "read_file", """{"file_path":"a.txt"}""")]),
            LlmMessage.ToolOutput("call_1", "read_file", "contents"),
        ], [], "test-model"));
        var messages = body["messages"]!.AsArray();

        var call = messages[1]!["tool_calls"]![0]!;
        Assert.Equal("call_1", call["id"]!.GetValue<string>());
        Assert.Equal("function", call["type"]!.GetValue<string>());
        Assert.Equal("read_file", call["function"]!["name"]!.GetValue<string>());
        Assert.Equal("""{"file_path":"a.txt"}""", call["function"]!["arguments"]!.GetValue<string>());

        Assert.Equal("tool", messages[2]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", messages[2]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("contents", messages[2]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void AttachmentsBecomeMultipartContentWithDataUris()
    {
        var png = TestImages.Png(2, 2);
        var pdf = "%PDF-1.4"u8.ToArray();
        var body = Body(new LlmRequest("", [
            LlmMessage.User("look", [
                new MessageAttachment(AttachmentKind.Image, "shot.png", png),
                new MessageAttachment(AttachmentKind.File, "spec.pdf", pdf),
            ]),
        ], [], "test-model"));
        var parts = body["messages"]![0]!["content"]!.AsArray();

        Assert.Equal(3, parts.Count);
        Assert.Equal("look", parts[0]!["text"]!.GetValue<string>());
        Assert.Equal($"data:image/png;base64,{Convert.ToBase64String(png)}", parts[1]!["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("spec.pdf", parts[2]!["file"]!["filename"]!.GetValue<string>());
        Assert.Equal($"data:application/pdf;base64,{Convert.ToBase64String(pdf)}", parts[2]!["file"]!["file_data"]!.GetValue<string>());
    }

    [Fact]
    public async Task StreamYieldsTextReasoningAndUsage()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"reasoning_content":"thinking…"}}]}""",
            """{"choices":[{"delta":{"content":"Hel"}}]}""",
            """{"choices":[{"delta":{"content":"lo"}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":3}}"""));
        var profile = Profile() with { ApiKey = "sk-test", CustomHeaders = new Dictionary<string, string> { ["X-Org"] = "acme" } };
        var events = await Collect(new OpenAiClient(profile, handler), Hi());

        Assert.Equal("thinking…", Assert.Single(events.OfType<LlmStreamEvent.Reasoning>()).Delta);
        Assert.Equal(["Hel", "lo"], events.OfType<LlmStreamEvent.Text>().Select(t => t.Delta));
        var done = Assert.IsType<LlmStreamEvent.Done>(events[^1]);
        Assert.Empty(done.Calls);
        Assert.Equal("stop", done.Finish);
        Assert.Equal(new LlmUsage(12, 3), done.Usage);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{BaseUrl}/chat/completions", sent.Url.ToString());
        Assert.Equal("Bearer sk-test", sent.Headers["Authorization"]);
        Assert.Equal("acme", sent.Headers["X-Org"]);
        Assert.Contains("text/event-stream", sent.Headers["Accept"]);
        Assert.Equal("test-model", JsonNode.Parse(sent.Body)!["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolCallDeltasAreAssembledAcrossChunks()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"read_file","arguments":"{\"file_"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"path\":\"a.txt\"}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_2","function":{"name":"glob"}}]}}]}""",
            """{"choices":[{"delta":{},"finish_reason":"tool_calls"}]}"""));
        var done = (await Collect(new OpenAiClient(Profile(), handler), Hi())).OfType<LlmStreamEvent.Done>().Single();

        Assert.Equal("tool_calls", done.Finish);
        Assert.Equal(
            [new ToolCall("call_1", "read_file", """{"file_path":"a.txt"}"""), new ToolCall("call_2", "glob", "{}")],
            done.Calls);
    }

    [Fact]
    public async Task XmlToolCallsInTheTextAreRecoveredWhenThereAreNoNativeCalls()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"content":"Reading it.\n<function=read_file>\n<parameter=file_path>src/a.cs</parameter>\n</function>"}}]}"""));
        var done = (await Collect(new OpenAiClient(Profile(), handler), Hi())).OfType<LlmStreamEvent.Done>().Single();

        var call = Assert.Single(done.Calls);
        Assert.Equal("xml-0", call.Id);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("src/a.cs", JsonArgs.String(call.Arguments, "file_path"));
    }

    [Fact]
    public async Task ContextOverflowBecomesAnOverflowErrorWithTheLimit()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"object":"error","message":"This model's maximum context length is 32768 tokens; however, you requested 40000 tokens."}""",
            HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<LlmException>(() => Collect(new OpenAiClient(Profile(), handler), Hi()));

        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
        Assert.Equal(32_768, error.Limit);
    }

    [Fact]
    public async Task OtherHttpErrorsKeepTheirStatusAndBody()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"model not found"}""", HttpStatusCode.NotFound));
        var error = await Assert.ThrowsAsync<LlmException>(() => Collect(new OpenAiClient(Profile(), handler), Hi()));

        Assert.Equal(LlmErrorKind.Http, error.Kind);
        Assert.Equal(404, error.StatusCode);
        Assert.Contains("model not found", error.Message);
    }

    [Fact]
    public async Task ListModelsReadsTheDataArray()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"object":"list","data":[{"id":"a"},{"id":"b"}]}"""));
        var models = await new OpenAiClient(Profile(), handler).ListModelsAsync();

        Assert.Equal(["a", "b"], models);
        Assert.Equal($"{BaseUrl}/models", Assert.Single(handler.Requests).Url.ToString());
    }

    /// <summary>The offline counterpart of LiveServerTests.testDetectsWindowAndFollowsTheServedModel: a
    /// box that swapped models serves exactly one, and the probe follows it with its window.</summary>
    [Fact]
    public async Task ModelInfoFollowsTheSingleServedModelAndItsWindow()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"data":[{"id":"qwen3.8-27b","max_model_len":262144}]}"""));
        var profile = Profile() with { Model = "a-model-that-was-swapped-out" };
        var info = await new OpenAiClient(profile, handler).ModelInfoAsync();

        Assert.Equal("qwen3.8-27b", info.Id);
        Assert.Equal(262_144, info.ContextWindow);
        Assert.Equal(["qwen3.8-27b"], info.Served);
    }

    [Fact]
    public async Task ModelInfoFallsBackToSglangRoutesWhenModelsHasNoWindow()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" => FakeHttpHandler.Json("""{"data":[{"id":"test-model"},{"id":"other"}]}"""),
            "/get_model_info" => FakeHttpHandler.Json("""{"context_len":131072}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("nope", Encoding.UTF8) },
        });
        var info = await new OpenAiClient(Profile(), handler).ModelInfoAsync();

        Assert.Equal("test-model", info.Id);
        Assert.Equal(131_072, info.ContextWindow);
        Assert.Equal(["test-model", "other"], info.Served);
    }
}
