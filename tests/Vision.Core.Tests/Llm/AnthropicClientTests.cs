using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>Anthropic's API with a key: the Messages request (headers, system and its cache breakpoint, tools, alternating
/// turns, tool results, pictures and PDFs, thinking said the way each Claude takes it), the streamed reply (text, thinking and
/// its signature, tool calls, usage with the cache), signed thinking sent back with its turn — and again without it when Claude
/// refuses it — and its errors in plain words. Wire shapes from Anthropic's Messages and streaming references; error bodies as
/// the API sends them. The same cases as AnthropicClientTests.swift in Vision for macOS.</summary>
public sealed class AnthropicClientTests
{
    private static ProviderProfile Route(string model = "claude-opus-5-5", string baseUrl = AnthropicClient.DefaultBaseUrl, ThinkingLevel? thinking = null,
                                         double? temperature = null) =>
        new(ProviderKind.Anthropic, "Anthropic API (key)", baseUrl, model) { ApiKey = "sk-ant-api03-test", Thinking = thinking, Temperature = temperature };

    private static JsonObject Body(ProviderProfile profile, IReadOnlyList<LlmMessage>? messages = null, ThinkingLevel? thinking = null,
                                   IReadOnlyList<ToolSpec>? tools = null, string system = "You are Vision.") =>
        AnthropicMessages.Request(new LlmRequest(system, messages ?? [LlmMessage.User("hi")], tools ?? [], profile.Model, Thinking: thinking), profile);

    private static string Sse(params string[] events) =>
        string.Concat(events.Select(e => $"event: {JsonNode.Parse(e)!["type"]!.GetValue<string>()}\ndata: {e}\n\n"));

    private static HttpResponseMessage Stream(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Sse(events), Encoding.UTF8, "text/event-stream"),
    };

    private static readonly string[] Hello =
    [
        """{"type":"message_start","message":{"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"stop_reason":null,"usage":{"input_tokens":20,"cache_creation_input_tokens":1000,"cache_read_input_tokens":3000,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}""",
        """{"type":"ping"}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":" there"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":12}}""",
        """{"type":"message_stop"}""",
    ];

    private static async Task<List<LlmStreamEvent>> Collect(IProviderClient client, LlmRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var e in client.StreamAsync(request)) events.Add(e);
        return events;
    }

    private static LlmRequest Hi(string model = "claude-opus-5-5") => new("You are Vision.", [LlmMessage.User("hi")], [], model);

    // MARK: The request

    [Fact]
    public async Task ATurnIsAStreamedMessagesRequestWithTheKeyAndVersion()
    {
        var handler = new FakeHttpHandler(_ => Stream(Hello));
        var client = ProviderClients.Create(Route(), handler);
        Assert.IsType<AnthropicClient>(client);
        var events = await Collect(client, Hi());
        Assert.Equal("Hello there", string.Concat(events.OfType<LlmStreamEvent.Text>().Select(t => t.Delta)));
        var done = events.OfType<LlmStreamEvent.Done>().Single();
        Assert.Equal("stop", done.Finish);
        // Cached input is still input: 20 + 1000 written + 3000 read.
        Assert.Equal(4020, done.Usage!.PromptTokens);
        Assert.Equal(12, done.Usage.CompletionTokens);
        Assert.Equal(3000, done.Usage.CachedTokens);
        Assert.Null(done.ProviderState); // nothing signed to send back

        var sent = handler.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Url.ToString());
        Assert.Equal("sk-ant-api03-test", sent.Headers["x-api-key"]);
        Assert.Equal("2023-06-01", sent.Headers["anthropic-version"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.StartsWith("Vision/", sent.Headers["User-Agent"]);
        var body = JsonNode.Parse(sent.Body)!.AsObject();
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("claude-opus-5-5", body["model"]!.GetValue<string>());
        Assert.Equal(AnthropicMessages.AdaptiveDefaultMaxTokens, body["max_tokens"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://llm-proxy.example.com/anthropic/v1/", "https://llm-proxy.example.com/anthropic/v1/messages")]
    public void TheEndpointTakesTheBaseWithOrWithoutV1(string baseUrl, string expected) =>
        Assert.Equal(expected, new AnthropicClient(Route(baseUrl: baseUrl)).Endpoint("messages"));

    /// <summary>The system prompt (and system-role notes) is one top-level list with a cache breakpoint at its end; automatic
    /// caching follows the conversation; tools carry input_schema.</summary>
    [Fact]
    public void SystemToolsAndCacheBreakpoints()
    {
        var tools = new[] { new ToolSpec("read_file", "Read a file.", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""") };
        var body = Body(Route(), [LlmMessage.SystemText("[Earlier conversation, compacted] We fixed the build."), LlmMessage.User("next?")], tools: tools);
        var system = body["system"]!.AsArray();
        Assert.Equal(2, system.Count);
        Assert.Equal("You are Vision.", system[0]!["text"]!.GetValue<string>());
        Assert.Null(system[0]!["cache_control"]);
        Assert.Equal("ephemeral", system[1]!["cache_control"]!["type"]!.GetValue<string>());
        Assert.Equal("ephemeral", body["cache_control"]!["type"]!.GetValue<string>());
        var tool = body["tools"]!.AsArray().Single()!;
        Assert.Equal("read_file", tool["name"]!.GetValue<string>());
        Assert.Equal("object", tool["input_schema"]!["type"]!.GetValue<string>());
        Assert.Null(tool["cache_control"]); // the system breakpoint covers the tools before it
        Assert.Equal("user", Assert.Single(body["messages"]!.AsArray())!["role"]!.GetValue<string>());
        // No system prompt: the breakpoint goes on the last tool.
        var bare = Body(Route(), tools: tools, system: "");
        Assert.Null(bare["system"]);
        Assert.Equal("ephemeral", bare["tools"]![0]!["cache_control"]!["type"]!.GetValue<string>());
    }

    /// <summary>Turns alternate and start and end with the user; tool results lead their user turn (an error flagged), an
    /// interrupted call gets a stub, ids from another provider are made valid for both the call and its result.</summary>
    [Fact]
    public void TurnsAlternateAndToolResultsAnswerEveryCall()
    {
        var messages = new[]
        {
            LlmMessage.Assistant("Picking up where we left off."),
            LlmMessage.User("Run the tests"),
            LlmMessage.Assistant("Running.", [new ToolCall("call_1.a", "shell", """{"command":"dotnet test"}"""), new ToolCall("call_2", "shell", "not json")]),
            LlmMessage.ToolOutput("call_1.a", "shell", "Error: 2 failed"),
            LlmMessage.User("Here is the screen", [new MessageAttachment(AttachmentKind.Image, "shot.png", TestImages.Png(4, 4))]) with { ImageSource = "screenshot" },
            LlmMessage.Assistant("Two failed."),
        };
        var turns = Body(Route(), messages)["messages"]!.AsArray();
        Assert.Equal(["user", "assistant", "user", "assistant", "user", "assistant", "user"], turns.Select(t => t!["role"]!.GetValue<string>()));
        Assert.Equal("(continuing the conversation)", turns[0]!["content"]![0]!["text"]!.GetValue<string>());
        var calls = turns[3]!["content"]!.AsArray();
        Assert.Equal("call_1_a", calls[1]!["id"]!.GetValue<string>());
        Assert.Equal("dotnet test", calls[1]!["input"]!["command"]!.GetValue<string>());
        Assert.Empty(calls[2]!["input"]!.AsObject()); // unparseable arguments become {}
        var results = turns[4]!["content"]!.AsArray();
        Assert.Equal("tool_result", results[0]!["type"]!.GetValue<string>());
        Assert.Equal("call_1_a", results[0]!["tool_use_id"]!.GetValue<string>());
        Assert.True(results[0]!["is_error"]!.GetValue<bool>());
        Assert.Equal("tool_result", results[1]!["type"]!.GetValue<string>()); // the call nobody answered
        Assert.Equal("call_2", results[1]!["tool_use_id"]!.GetValue<string>());
        Assert.Contains("interrupted", results[1]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("image", results.Last()!["type"]!.GetValue<string>()); // the screenshot joins the results' turn, after them
        Assert.Equal("Continue.", turns[6]!["content"]![0]!["text"]!.GetValue<string>()); // never a prefilled assistant turn
    }

    [Fact]
    public void PicturesAndPdfsGoAsTheirOwnBlocks()
    {
        var png = new MessageAttachment(AttachmentKind.Image, "shot.png", TestImages.Png(4, 4));
        var pdf = new MessageAttachment(AttachmentKind.File, "spec.pdf", "%PDF-1.4 tiny"u8.ToArray());
        var blocks = Body(Route(), [LlmMessage.User("look", [png, pdf])])["messages"]![0]!["content"]!.AsArray();
        Assert.Equal("text", blocks[0]!["type"]!.GetValue<string>());
        var image = blocks.Single(b => b!["type"]!.GetValue<string>() == "image")!;
        Assert.Equal("base64", image["source"]!["type"]!.GetValue<string>());
        Assert.Equal("image/png", image["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(png.Data), image["source"]!["data"]!.GetValue<string>());
        var document = blocks.Single(b => b!["type"]!.GetValue<string>() == "document")!;
        Assert.Equal("application/pdf", document["source"]!["media_type"]!.GetValue<string>());
        Assert.Equal("spec.pdf", document["title"]!.GetValue<string>());
        Assert.True(AttachmentRoute.Of(Route()).Documents);
    }

    /// <summary>Each Claude gets thinking the way it takes it: Opus 5.5 can't switch it off (Off is the lowest effort), Sonnet
    /// 5.5's Off is between_tools, Haiku 4.5 thinks on a budget, Claude 4.7 and later take no temperature.</summary>
    [Fact]
    public void ThinkingIsSaidTheWayEachModelTakesIt()
    {
        var opusOff = Body(Route(), thinking: ThinkingLevel.Off);
        Assert.Null(opusOff["thinking"]);
        Assert.Equal("low", opusOff["output_config"]!["effort"]!.GetValue<string>());

        var opusHigh = Body(Route(temperature: 0.4), thinking: ThinkingLevel.High);
        Assert.Equal("adaptive", opusHigh["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("summarized", opusHigh["thinking"]!["display"]!.GetValue<string>()); // the live reasoning pane wants text
        Assert.Equal("high", opusHigh["output_config"]!["effort"]!.GetValue<string>());
        Assert.Null(opusHigh["temperature"]);
        Assert.Equal("max", Body(Route(), thinking: ThinkingLevel.Max)["output_config"]!["effort"]!.GetValue<string>());

        var sonnetOff = Body(Route("claude-sonnet-5-5"), thinking: ThinkingLevel.Off);
        Assert.Equal("between_tools", sonnetOff["thinking"]!["type"]!.GetValue<string>());
        Assert.Single(sonnetOff["thinking"]!.AsObject()); // between_tools takes no other field
        Assert.Null(sonnetOff["output_config"]);

        var haiku = Body(Route("claude-haiku-4-5", temperature: 0.4), thinking: ThinkingLevel.Medium);
        Assert.Equal("enabled", haiku["thinking"]!["type"]!.GetValue<string>());
        var budget = haiku["thinking"]!["budget_tokens"]!.GetValue<int>();
        Assert.True(budget >= 1024 && budget < haiku["max_tokens"]!.GetValue<int>());
        Assert.Null(haiku["temperature"]); // not while thinking
        Assert.Equal(0.4, Body(Route("claude-haiku-4-5", temperature: 0.4))["temperature"]!.GetValue<double>());

        Assert.Null(Body(Route())["thinking"]); // no level: the model's own default
        Assert.Equal(128_000, Body(Route() with { MaxOutputTokens = 500_000 })["max_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void ModelFactsComeFromTheSameTableAsBedrock()
    {
        Assert.Equal(1_000_000, FallbackContextWindow.Limit("claude-opus-5-5"));
        Assert.Equal(1_000_000, FallbackContextWindow.Limit("claude-sonnet-5-5"));
        Assert.Equal(200_000, FallbackContextWindow.Limit("claude-haiku-4-5"));
        var rules = ModelRequestRules.For(Route());
        Assert.Equal(KnownProvider.Anthropic, rules.Provider);
        Assert.Equal(ThinkingLevels.All, rules.ThinkingLevels);
        Assert.False(rules.SendsTemperature);
        Assert.False(rules.ForcedToolChoice); // Opus 5.5 refuses tool_choice any/tool
        Assert.False(Route().IsPrivateRoute);
        Assert.Equal("\"anthropic\"", JsonSerializer.Serialize(ProviderKind.Anthropic));
    }

    // MARK: Thinking, kept and sent back

    private static readonly string[] ThinkThenCall =
    [
        """{"type":"message_start","message":{"id":"msg_2","type":"message","role":"assistant","model":"claude-opus-5-5","content":[],"usage":{"input_tokens":50,"output_tokens":1}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Check the file "}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"before answering."}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"EqQBCkYIARgCKkCsig=="}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}""",
        """{"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading it."}}""",
        """{"type":"content_block_stop","index":1}""",
        """{"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_01A","name":"read_file","input":{}}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":""}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\": \"READ"}}""",
        """{"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"ME.md\"}"}}""",
        """{"type":"content_block_stop","index":2}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use","stop_sequence":null},"usage":{"output_tokens":80}}""",
        """{"type":"message_stop"}""",
    ];

    [Fact]
    public async Task ThinkingStreamsLiveAndItsSignedBlockGoesBackWithTheTurn()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1 ? Stream(ThinkThenCall) : Stream(Hello));
        var client = new AnthropicClient(Route(), handler);
        var events = await Collect(client, new LlmRequest("You are Vision.", [LlmMessage.User("What's in the README?")],
            [new ToolSpec("read_file", "Read a file.", """{"type":"object"}""")], "claude-opus-5-5", Thinking: ThinkingLevel.High));
        Assert.Equal("Check the file before answering.", string.Concat(events.OfType<LlmStreamEvent.Reasoning>().Select(r => r.Delta)));
        var done = events.OfType<LlmStreamEvent.Done>().Single();
        Assert.Equal("tool_calls", done.Finish);
        var call = Assert.Single(done.Calls);
        Assert.Equal(("toolu_01A", "read_file"), (call.Id, call.Name));
        Assert.Equal("README.md", JsonNode.Parse(call.Arguments)!["path"]!.GetValue<string>());
        Assert.StartsWith(AnthropicMessages.StatePrefix, done.ProviderState);

        // The next request sends the turn back as Claude wrote it: thinking (with its signature), text, tool call.
        var assistant = LlmMessage.Assistant("Reading it.", done.Calls) with { ProviderState = done.ProviderState };
        await Collect(client, new LlmRequest("You are Vision.", [LlmMessage.User("What's in the README?"), assistant,
            LlmMessage.ToolOutput("toolu_01A", "read_file", "# Vision")], [], "claude-opus-5-5", Thinking: ThinkingLevel.High));
        var replayed = JsonNode.Parse(handler.Requests[1].Body)!["messages"]![1]!["content"]!.AsArray();
        Assert.Equal(["thinking", "text", "tool_use"], replayed.Select(b => b!["type"]!.GetValue<string>()));
        Assert.Equal("EqQBCkYIARgCKkCsig==", replayed[0]!["signature"]!.GetValue<string>());
        Assert.Equal("Check the file before answering.", replayed[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("README.md", replayed[2]!["input"]!["path"]!.GetValue<string>());
        // The tools the transcript used are declared even though none are offered now.
        Assert.Equal("read_file", JsonNode.Parse(handler.Requests[1].Body)!["tools"]![0]!["name"]!.GetValue<string>());
    }

    /// <summary>When Claude refuses the thinking sent back (the conversation changed under its signature), the turn goes again
    /// once without it, and the chat hears why.</summary>
    [Fact]
    public async Task RefusedThinkingIsLeftOutAndTheTurnGoesAgain()
    {
        const string bound = """{"type":"error","error":{"type":"invalid_request_error","message":"messages.1.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation. Remove the block, or set `thinking.block_binding.prefix_mismatch_behavior` to \"drop_block\"."},"request_id":"req_011"}""";
        var calls = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1 ? FakeHttpHandler.Json(bound, HttpStatusCode.BadRequest) : Stream(Hello));
        var notices = new List<string>();
        var client = new AnthropicClient(Route(), handler, notices.Add);
        var state = AnthropicMessages.StatePrefix + """[{"type":"thinking","thinking":"x","signature":"sig"},{"type":"text","text":"Done."}]""";
        var events = await Collect(client, new LlmRequest("", [LlmMessage.User("a"), LlmMessage.Assistant("Done.") with { ProviderState = state }, LlmMessage.User("b")],
            [], "claude-opus-5-5", Thinking: ThinkingLevel.High));
        Assert.Single(events.OfType<LlmStreamEvent.Done>());
        Assert.Equal("thinking", JsonNode.Parse(handler.Requests[0].Body)!["messages"]![1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("text", JsonNode.Parse(handler.Requests[1].Body)!["messages"]![1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Single(notices);
        // Another client's state (Bedrock's, the reasoning echo) is never replayed here.
        var bedrock = """[{"reasoningContent":{"reasoningText":{"text":"x","signature":"s"}}},{"text":"Done."}]""";
        Assert.Null(AnthropicMessages.ReplayBlocks(LlmMessage.Assistant("Done.") with { ProviderState = bedrock }));
        Assert.Null(AnthropicMessages.ReplayBlocks(LlmMessage.Assistant("Done.") with { ProviderState = ReasoningEcho.State("x") }));
    }

    // MARK: Errors

    private static async Task<LlmException> Fail(HttpStatusCode status, string body) => await Assert.ThrowsAsync<LlmException>(() =>
        Collect(new AnthropicClient(Route(), new FakeHttpHandler(_ => FakeHttpHandler.Json(body, status))), Hi()));

    [Fact]
    public async Task ErrorsAreSaidInPlainWordsAndRetriedOnlyWhenWaitingHelps()
    {
        var key = await Fail(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"},"request_id":"req_1"}""");
        Assert.StartsWith("Anthropic didn't accept the key.", key.Message);
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(key));

        var credit = await Fail((HttpStatusCode)402, """{"type":"error","error":{"type":"billing_error","message":"Your credit balance is too low to access the Anthropic API."},"request_id":"req_2"}""");
        Assert.Contains("console.anthropic.com → Billing", credit.Message);
        Assert.Contains("credit balance is too low", credit.Message);
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(credit));

        var model = await Fail(HttpStatusCode.NotFound, """{"type":"error","error":{"type":"not_found_error","message":"model: claude-opus-5-5"},"request_id":"req_3"}""");
        Assert.StartsWith("Anthropic doesn't offer `claude-opus-5-5` to this key.", model.Message);
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(model));

        var limited = await Fail((HttpStatusCode)429, """{"type":"error","error":{"type":"rate_limit_error","message":"Number of request tokens has exceeded your per-minute rate limit"},"request_id":"req_4"}""");
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(limited));
        var overloaded = await Fail((HttpStatusCode)529, """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"},"request_id":"req_5"}""");
        Assert.Equal("Anthropic is overloaded right now; Vision waits and tries again.", overloaded.Message);
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(overloaded));

        var tooLong = await Fail(HttpStatusCode.BadRequest, """{"type":"error","error":{"type":"invalid_request_error","message":"prompt is too long: 1012345 tokens > 1000000 maximum"},"request_id":"req_6"}""");
        Assert.Equal(LlmErrorKind.Overflow, tooLong.Kind);
        Assert.Equal(1_000_000, tooLong.Limit);
    }

    [Fact]
    public async Task AnErrorMidStreamAndARefusalEndTheTurn()
    {
        var midStream = await Assert.ThrowsAsync<LlmException>(() => Collect(new AnthropicClient(Route(), new FakeHttpHandler(_ => Stream(
            Hello[0], Hello[1], Hello[3], """{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}"""))), Hi()));
        Assert.Equal(529, midStream.StatusCode);
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(midStream));

        var refusal = await Assert.ThrowsAsync<LlmException>(() => Collect(new AnthropicClient(Route(), new FakeHttpHandler(_ => Stream(
            Hello[0], Hello[1], Hello[5],
            """{"type":"message_delta","delta":{"stop_reason":"refusal","stop_details":{"type":"refusal","category":"cyber","explanation":null}},"usage":{"output_tokens":3}}""",
            Hello[7]))), Hi()));
        Assert.StartsWith("Claude declined this request (its safety checks stopped the reply: cyber).", refusal.Message);
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(refusal));

        var cutOff = await Assert.ThrowsAsync<LlmException>(() => Collect(new AnthropicClient(Route(), new FakeHttpHandler(_ => Stream(Hello[..4]))), Hi()));
        Assert.Equal(LlmErrorKind.Sse, cutOff.Kind);
    }

    // MARK: Models

    [Fact]
    public async Task ModelsAreListedAPageAtATimeAndTheWindowIsAsked()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v1/models" when request.RequestUri.Query.Contains("after_id=claude-opus-5-5") => FakeHttpHandler.Json(
                """{"data":[{"type":"model","id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5"}],"has_more":false,"first_id":"claude-haiku-5-5","last_id":"claude-haiku-5-5"}"""),
            "/v1/models" => FakeHttpHandler.Json(
                """{"data":[{"type":"model","id":"claude-fable-5-1"},{"type":"model","id":"claude-opus-5-5"}],"has_more":true,"first_id":"claude-fable-5-1","last_id":"claude-opus-5-5"}"""),
            "/v1/models/claude-opus-5-5" => FakeHttpHandler.Json(
                """{"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","max_input_tokens":1000000,"max_tokens":128000}"""),
            _ => FakeHttpHandler.Json("""{"type":"error","error":{"type":"not_found_error","message":"Not found"}}""", HttpStatusCode.NotFound),
        });
        var client = new AnthropicClient(Route(), handler);
        Assert.Equal(["claude-fable-5-1", "claude-opus-5-5", "claude-haiku-5-5"], await client.ListModelsAsync());
        Assert.All(handler.Requests, r => Assert.Equal("sk-ant-api03-test", r.Headers["x-api-key"]));
        var info = await client.ModelInfoAsync();
        Assert.Equal(("claude-opus-5-5", 1_000_000, 128_000), (info.Id, info.ContextWindow, info.MaxTokens));
        Assert.Equal(["claude-opus-5-5"], info.Served); // serves its model, so it counts as answering
        // A model the endpoint doesn't describe: the table's figures.
        var fallback = await client.ModelInfoAsync("claude-sonnet-5-5");
        Assert.Equal(1_000_000, fallback.ContextWindow);
    }

    // MARK: The plan guide

    [Fact]
    public void ThePlanSetsUpAnAnthropicRouteAndKnowsASubscriptionSignInIsNotAKey()
    {
        var plan = SubscriptionPlans.Find(SubscriptionPlans.Anthropic)!;
        Assert.Equal(ProviderKind.Anthropic, plan.Kind);
        Assert.Equal("claude-opus-5-5", plan.Recommended.Id);
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"], plan.Models.Select(m => m.Id));
        var route = plan.Route(null, "claude-sonnet-5-5");
        Assert.Equal((ProviderKind.Anthropic, AnthropicClient.DefaultBaseUrl, 1_000_000), (route.Kind, route.BaseUrl, route.ContextWindow));
        Assert.IsType<AnthropicClient>(ProviderClients.Create(route));
        Assert.Null(plan.KeyMistake("sk-ant-api03-abcdef"));
        Assert.StartsWith("That's a Claude subscription sign-in (sk-ant-oat…), not an API key.", plan.KeyMistake("sk-ant-oat01-abcdef"));
        Assert.Contains("Admin key", plan.KeyMistake("sk-ant-admin01-abcdef"));
        Assert.Contains("another service's key", plan.KeyMistake("sk-proj-abcdef"));
        Assert.StartsWith("Anthropic didn't accept the key.", SubscriptionPlans.ExplainError(plan, plan.DefaultRegion, 401,
            """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}"""));
    }
}
