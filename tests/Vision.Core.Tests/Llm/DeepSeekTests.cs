using System.Net;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>DeepSeek as a first-class provider: what its models take on the wire (thinking on/off beside low/high/max, every
/// earlier turn's reasoning sent back, 384K output), its errors in plain words (bodies as DeepSeek sends them), the one-turn
/// fallback when reasoning a chat no longer has is demanded, and the plan the guide sets up. From api-docs.deepseek.com,
/// 2026-10-19. The same cases as DeepSeekTests.swift in Vision for macOS.</summary>
public sealed class DeepSeekTests
{
    private const string Api = "https://api.deepseek.com";

    // Error bodies DeepSeek returns (recorded in public reports; the 402's "type" and "code" really are that way round).
    private const string Unauthorized = """{"error":{"message":"Authentication Fails, Your api key: ****abcd is invalid","type":"authentication_error","param":null,"code":"invalid_request_error"}}""";
    private const string NoBalance = """{"error":{"message":"Insufficient Balance","type":"unknown_error","param":null,"code":"invalid_request_error"}}""";
    private const string ReasoningMissing = """{"error":{"message":"The `reasoning_content` in the thinking mode must be passed back to the API.","type":"invalid_request_error","param":null,"code":"invalid_request_error"}}""";
    private const string NoSuchModel = """{"error":{"message":"Model Not Exist","type":"invalid_request_error","param":null,"code":"invalid_request_error"}}""";

    private static ProviderProfile Route(string model = "deepseek-v4-pro", int? maxTokens = null, double? temperature = null) =>
        new(ProviderKind.OpenAICompat, "DeepSeek API", Api, model) { MaxOutputTokens = maxTokens, Temperature = temperature };

    private static JsonObject Body(ProviderProfile profile, ThinkingLevel? thinking = null, IReadOnlyList<LlmMessage>? messages = null) =>
        JsonNode.Parse(new OpenAiClient(profile, adaptations: new RequestAdaptationStore())
            .MakeBody(new LlmRequest("", messages ?? [LlmMessage.User("hi")], [], profile.Model, Thinking: thinking)))!.AsObject();

    private static List<JsonNode> Assistants(JsonObject body) =>
        body["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "assistant").Select(m => m!).ToList();

    // MARK: What it takes

    /// <summary>Off is thinking.type "disabled" with no effort; every other level is "enabled" plus the nearest of low/high/max
    /// (DeepSeek maps medium to high itself; Vision sends what it really does). No level: the server's default (thinking, high).</summary>
    [Theory]
    [InlineData(ThinkingLevel.Off, "disabled", null)]
    [InlineData(ThinkingLevel.Low, "enabled", "low")]
    [InlineData(ThinkingLevel.Medium, "enabled", "high")]
    [InlineData(ThinkingLevel.High, "enabled", "high")]
    [InlineData(ThinkingLevel.Max, "enabled", "max")]
    public void ThinkingIsAToggleBesideLowHighMax(ThinkingLevel level, string type, string? effort)
    {
        var body = Body(Route(), level);
        Assert.Equal(type, body["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(effort, body["reasoning_effort"]?.GetValue<string>());
        Assert.Null(body["chat_template_kwargs"]); // a hosted API, not a chat template
        var rules = ModelRequestRules.For(Route());
        Assert.Equal(KnownProvider.DeepSeek, rules.Provider);
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max], rules.ThinkingLevels);
        Assert.False(rules.AlwaysThinks);
        var none = Body(Route());
        Assert.Null(none["thinking"]);
        Assert.Null(none["reasoning_effort"]);
    }

    /// <summary>With tools (every Vision turn) DeepSeek wants every earlier assistant message's reasoning back — tool call or
    /// not — or it answers 400. Kept reasoning goes back as it was; a turn without any (another model's, a compacted chat) gets
    /// a non-empty stand-in. Thinking off needs no stand-in.</summary>
    [Fact]
    public void EveryEarlierTurnCarriesItsReasoning()
    {
        var kept = LlmMessage.Assistant("", [new ToolCall("c1", "read_file", "{}")]) with { ProviderState = ReasoningEcho.State("Read it first.") };
        var lost = LlmMessage.Assistant("Here it is.");
        var messages = new[] { LlmMessage.User("go"), kept, LlmMessage.ToolOutput("c1", "read_file", "x"), lost, LlmMessage.User("next") };
        var on = Assistants(Body(Route(), ThinkingLevel.High, messages));
        Assert.Equal("Read it first.", on[0]["reasoning_content"]!.GetValue<string>());
        Assert.Equal(ReasoningEcho.Placeholder, on[1]["reasoning_content"]!.GetValue<string>());
        Assert.NotEmpty(ReasoningEcho.Placeholder.Trim()); // DeepSeek refuses an empty one
        var defaulted = Assistants(Body(Route(), null, messages));
        Assert.Equal(ReasoningEcho.Placeholder, defaulted[1]["reasoning_content"]!.GetValue<string>()); // the default is thinking
        var off = Assistants(Body(Route(), ThinkingLevel.Off, messages));
        Assert.Null(off[1]["reasoning_content"]);
    }

    /// <summary>384K (393,216) output at most, 1M windows, Flash reads images and V4 Pro doesn't, temperature within 0…2 (it is
    /// ignored while thinking, not refused), no forced tool_choice.</summary>
    [Fact]
    public void LimitsWindowsAndImages()
    {
        Assert.Equal(393_216, Body(Route(maxTokens: 500_000))["max_tokens"]!.GetValue<int>());
        Assert.Equal(64_000, Body(Route(maxTokens: 64_000))["max_tokens"]!.GetValue<int>());
        Assert.Equal(0.3, Body(Route(temperature: 0.3))["temperature"]!.GetValue<double>());
        Assert.Equal(2.0, Body(Route(temperature: 2.6))["temperature"]!.GetValue<double>());
        Assert.False(ModelRequestRules.For(Route()).ForcedToolChoice);
        Assert.False(ModelRequestRules.For(Route("deepseek-v4-pro")).AcceptsImages);
        Assert.True(ModelRequestRules.For(Route("deepseek-flash")).AcceptsImages);
        Assert.True(ModelRequestRules.For(Route("deepseek-v4-flash")).AcceptsImages);
        Assert.Null(ModelRequestRules.For(Route("deepseek-chat")).AcceptsImages); // not known either way
        Assert.False(AttachmentRoute.OpenAi(Route("deepseek-v4-pro")).Vision);
        Assert.Equal(1_048_576, FallbackContextWindow.Limit("deepseek-v4-pro"));
        Assert.Equal(1_048_576, FallbackContextWindow.Limit("deepseek-flash"));
        Assert.Equal(1_048_576, FallbackContextWindow.Limit("deepseek-v4-flash"));
        Assert.Equal(65_536, FallbackContextWindow.Limit("deepseek-r1:32b")); // the local distills keep theirs
        Assert.False(Route().IsSelfHosted);
        Assert.False(Route().IsPrivateRoute);
    }

    /// <summary>The reasoning DeepSeek streams rides on Done, so the next request can send it back.</summary>
    [Fact]
    public async Task AReplysReasoningIsKeptForTheNextRequest()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Sse(
            """{"id":"1","object":"chat.completion.chunk","model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"role":"assistant","content":null,"reasoning_content":"The user greets."},"finish_reason":null}]}""",
            """{"id":"1","object":"chat.completion.chunk","model":"deepseek-v4-pro","choices":[{"index":0,"delta":{"content":"Hello!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":12,"completion_tokens":9,"prompt_cache_hit_tokens":8,"prompt_cache_miss_tokens":4}}"""));
        var events = new List<LlmStreamEvent>();
        var profile = Route();
        profile.ApiKey = "sk-test";
        await foreach (var e in new OpenAiClient(profile, handler, new RequestAdaptationStore()).StreamAsync(new LlmRequest("", [LlmMessage.User("hi")], [], "deepseek-v4-pro")))
            events.Add(e);
        Assert.Equal("The user greets.", string.Concat(events.OfType<LlmStreamEvent.Reasoning>().Select(r => r.Delta)));
        var done = events.OfType<LlmStreamEvent.Done>().Single();
        Assert.Equal("The user greets.", ReasoningEcho.Reasoning(done.ProviderState));
        Assert.Equal(8, done.Usage!.CachedTokens);
        Assert.Equal(Api + "/chat/completions", handler.Requests[0].Url.ToString());
        Assert.Equal("Bearer sk-test", handler.Requests[0].Headers["Authorization"]);
    }

    // MARK: Errors

    /// <summary>When DeepSeek still wants reasoning the chat no longer has, this one turn goes without thinking (where nothing has
    /// to come back) and the chat says so — instead of failing every turn from then on.</summary>
    [Fact]
    public async Task DemandedReasoningTheChatLacksRunsTheTurnWithoutThinking()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json(ReasoningMissing, HttpStatusCode.BadRequest)
            : FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var notices = new List<string>();
        var client = new OpenAiClient(Route(), handler, new RequestAdaptationStore(), notices.Add);
        var request = new LlmRequest("", [LlmMessage.User("go"), LlmMessage.Assistant("", [new ToolCall("c1", "read_file", "{}")]),
            LlmMessage.ToolOutput("c1", "read_file", "x")], [], "deepseek-v4-pro", Thinking: ThinkingLevel.High);
        var text = "";
        await foreach (var e in client.StreamAsync(request)) if (e is LlmStreamEvent.Text t) text += t.Delta;
        Assert.Equal("ok", text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("enabled", JsonNode.Parse(handler.Requests[0].Body)!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal("disabled", JsonNode.Parse(handler.Requests[1].Body)!["thinking"]!["type"]!.GetValue<string>());
        Assert.Equal(DeepSeekErrors.ReasoningRetryNotice, Assert.Single(notices));
    }

    /// <summary>A refused key and an empty balance fail at once, in words that name the fix; a missing model names the current
    /// ones. Rate limits and overload are still waited out.</summary>
    [Fact]
    public async Task ErrorsAreSaidInPlainWords()
    {
        async Task<LlmException> Fail(HttpStatusCode status, string body) => await Assert.ThrowsAsync<LlmException>(async () =>
        {
            var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(body, status));
            await foreach (var _ in new OpenAiClient(Route(), handler, new RequestAdaptationStore()).StreamAsync(new LlmRequest("", [LlmMessage.User("hi")], [], "deepseek-v4-pro"))) { }
        });

        var key = await Fail(HttpStatusCode.Unauthorized, Unauthorized);
        Assert.Equal("DeepSeek didn't accept the API key. Check it's a key from platform.deepseek.com/api_keys (it starts with sk-), copied in one piece.", key.Message);
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(key));

        var balance = await Fail((HttpStatusCode)402, NoBalance);
        Assert.Equal("The DeepSeek account has no balance left (402 Insufficient Balance). Top it up at platform.deepseek.com/top_up, then try again.", balance.Message);
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(balance));

        var model = await Fail(HttpStatusCode.BadRequest, NoSuchModel);
        Assert.Equal("DeepSeek doesn't serve `deepseek-v4-pro`. The current models are deepseek-flash and deepseek-v4-pro.", model.Message);

        var busy = await Fail(HttpStatusCode.ServiceUnavailable, """{"error":{"message":"Server Overloaded","type":"server_error","param":null,"code":"server_error"}}""");
        Assert.Equal("DeepSeek's servers are overloaded right now (503) — Vision waits and tries again.", busy.Message);
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(busy));
        var slow = await Fail((HttpStatusCode)429, """{"error":{"message":"Rate Limit Reached","type":"rate_limit_error","param":null,"code":"rate_limit_error"}}""");
        Assert.Equal("DeepSeek's rate limit was reached (429) — Vision waits and tries again.", slow.Message);
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(slow));
    }

    /// <summary>DeepSeek's reasoning refusal served somewhere Vision doesn't know (a proxy, a router) turns the echo on there —
    /// on every earlier assistant message, as DeepSeek wants it, so a plain reply from before the lesson (its reasoning never
    /// kept) gets the stand-in too and the retry goes through; reasoning is kept from then on. (The one-turn thinking-off
    /// retry stays DeepSeek's own API's: elsewhere Vision has no sure way to switch DeepSeek's thinking off.)</summary>
    [Fact]
    public async Task ADeepSeekReasoningRefusalElsewhereTurnsTheEchoOnForEveryTurn()
    {
        Assert.True(DeepSeekErrors.WantsReasoningBack(ReasoningMissing));
        Assert.False(DeepSeekErrors.WantsReasoningBack(NoBalance));
        Assert.Equal(new RequestChange.EchoReasoning(EveryTurn: true),
            ParameterRejection.Change(400, ReasoningMissing, new HashSet<string> { "model", "messages", "stream", "tools" }));

        var store = new RequestAdaptationStore();
        var calls = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json(ReasoningMissing, HttpStatusCode.BadRequest)
            : FakeHttpHandler.Sse("""{"choices":[{"delta":{"reasoning_content":"Next step."}}]}""",
                                  """{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var notices = new List<string>();
        var proxy = new ProviderProfile(ProviderKind.OpenAICompat, "Router", "https://llm-router.example.com/v1", "deepseek/deepseek-v4-pro");
        var client = new OpenAiClient(proxy, handler, store, notices.Add);
        var request = new LlmRequest("", [LlmMessage.User("hi"), LlmMessage.Assistant("Hello."), LlmMessage.User("next")], [],
            proxy.Model, Thinking: ThinkingLevel.High);
        var events = new List<LlmStreamEvent>();
        await foreach (var e in client.StreamAsync(request)) events.Add(e);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Null(JsonNode.Parse(handler.Requests[0].Body)!["messages"]![1]!["reasoning_content"]);
        Assert.Equal(ReasoningEcho.Placeholder, JsonNode.Parse(handler.Requests[1].Body)!["messages"]![1]!["reasoning_content"]!.GetValue<string>());
        var learned = store.Adaptation(RequestAdaptationStore.Key(proxy.BaseUrl, proxy.Model))!;
        Assert.True(learned.EchoReasoning && learned.EchoReasoningEveryTurn);
        Assert.Equal("its reasoning sent back with every reply", learned.Summary);
        Assert.Equal("Router (deepseek/deepseek-v4-pro) wants the model's reasoning sent back with every reply — Vision now does that for this model.",
            Assert.Single(notices));
        Assert.Equal("Next step.", ReasoningEcho.Reasoning(events.OfType<LlmStreamEvent.Done>().Single().ProviderState));

        // On DeepSeek's own API the rules already send it every turn: the same refusal after the thinking-off retry teaches
        // nothing (no lesson, no second line) and is said in DeepSeek's words.
        var own = new RequestAdaptationStore();
        var refused = new FakeHttpHandler(_ => FakeHttpHandler.Json(ReasoningMissing, HttpStatusCode.BadRequest));
        var ownNotices = new List<string>();
        var ownClient = new OpenAiClient(Route(), refused, own, ownNotices.Add);
        await Assert.ThrowsAsync<LlmException>(async () =>
        {
            await foreach (var _ in ownClient.StreamAsync(new LlmRequest("", [LlmMessage.User("hi"), LlmMessage.Assistant("Hello."), LlmMessage.User("next")],
                               [], "deepseek-v4-pro", Thinking: ThinkingLevel.High))) { }
        });
        Assert.Equal(2, refused.Requests.Count);
        Assert.Null(own.Adaptation(RequestAdaptationStore.Key(Api, "deepseek-v4-pro")));
        Assert.Equal(DeepSeekErrors.ReasoningRetryNotice, Assert.Single(ownNotices));
    }

    // MARK: The plan guide

    [Fact]
    public void ThePlanSetsUpAHostedRouteWithTheWindowKnown()
    {
        var plan = SubscriptionPlans.Find(SubscriptionPlans.DeepSeek)!;
        Assert.Equal("DeepSeek API", plan.Name);
        Assert.Equal("DeepSeek|deepseek-v4-pro", plan.Route(null, "deepseek-v4-pro").RouteId); // the Mac's route name too
        Assert.Equal("deepseek-v4-pro", plan.Recommended.Id);
        Assert.Contains(plan.Models, m => m.Id == "deepseek-flash");
        Assert.True(plan.ListsModels);
        var route = plan.Route(null, "deepseek-flash");
        Assert.Equal(ProviderKind.OpenAICompat, route.Kind);
        Assert.Equal(Api, route.BaseUrl);
        Assert.Equal(1_048_576, route.ContextWindow);
        Assert.Equal("https://platform.deepseek.com/api_keys", plan.DefaultRegion.KeyPage);
        Assert.Equal(SubscriptionPlans.DeepSeek, SubscriptionPlans.ForBaseUrl(Api + "/")?.Plan.Id);
        Assert.Null(plan.KeyMistake("sk-0123456789abcdef"));
        Assert.Equal("That's an Anthropic key. Choose “Anthropic API (Claude, with an API key)” for it, or copy a DeepSeek key from platform.deepseek.com/api_keys.",
            plan.KeyMistake("sk-ant-api03-abc"));
        Assert.Equal("DeepSeek keys start with sk-. Copy the key from platform.deepseek.com/api_keys.", plan.KeyMistake("abc123"));
        Assert.StartsWith("The DeepSeek account has no balance left", SubscriptionPlans.ExplainError(plan, plan.DefaultRegion, 402, NoBalance));
    }
}
