using System.Net;
using System.Text.Json.Nodes;

namespace Vision.Core.Tests;

/// <summary>What each model takes on the wire (Kimi, Z.ai, OpenAI), the safety net that learns from a server's 400, and
/// Z.ai's errors in plain words. Error bodies are the providers' own (recorded, or quoted from their docs). The same cases
/// as ProviderRulesTests.swift in Vision for macOS.</summary>
public sealed class ProviderRulesTests
{
    private const string Moonshot = "https://api.moonshot.ai/v1";
    private const string KimiCode = "https://api.kimi.com/coding/v1";
    private const string ZaiCoding = "https://api.z.ai/api/coding/paas/v4";
    private const string ZaiGeneral = "https://api.z.ai/api/paas/v4";

    private static ProviderProfile Route(string baseUrl, string model, double? temperature = null, int? maxTokens = null) =>
        new(ProviderKind.OpenAICompat, "Route", baseUrl, model) { Temperature = temperature, MaxOutputTokens = maxTokens };

    private static JsonObject Body(ProviderProfile profile, ThinkingLevel? thinking = null, IReadOnlyList<LlmMessage>? messages = null,
                                   RequestAdaptationStore? store = null) =>
        JsonNode.Parse(new OpenAiClient(profile, adaptations: store ?? new RequestAdaptationStore())
            .MakeBody(new LlmRequest("", messages ?? [LlmMessage.User("hi")], [], profile.Model, Thinking: thinking)))!.AsObject();

    private static string? ThinkingType(JsonObject body) => body["thinking"]?["type"]?.GetValue<string>();
    private static string? Effort(JsonObject body) => body["reasoning_effort"]?.GetValue<string>();

    // MARK: Kimi

    /// <summary>Moonshot's K2.6 fixes the temperature (1.0 thinking, 0.6 not) and switches thinking with thinking.type; it
    /// takes no reasoning_effort.</summary>
    [Fact]
    public void KimiK26GetsNoTemperatureAndAThinkingToggle()
    {
        var k26 = Route(Moonshot, "kimi-k2.6", temperature: 0.7);
        var off = Body(k26, ThinkingLevel.Off);
        Assert.Null(off["temperature"]); // the cause of the 400: "invalid temperature: only 1 is allowed for this model"
        Assert.Equal("disabled", ThinkingType(off));
        Assert.Null(off["reasoning_effort"]);
        Assert.Null(off["chat_template_kwargs"]);
        var on = Body(k26, ThinkingLevel.Medium);
        Assert.Equal("enabled", ThinkingType(on));
        Assert.Null(on["reasoning_effort"]);
        Assert.Null(Body(k26)["thinking"]); // no level chosen: the server's default
    }

    /// <summary>K2.7 Code always thinks: no thinking setting, no effort, no temperature — whatever the chat asks.</summary>
    [Fact]
    public void KimiK27CodeIsSentNoThinkingSettingAtAll()
    {
        foreach (var level in ThinkingLevels.All)
        {
            var body = Body(Route(Moonshot, "kimi-k2.7-code", temperature: 0.2), level);
            Assert.Null(body["thinking"]);
            Assert.Null(body["reasoning_effort"]);
            Assert.Null(body["temperature"]);
        }
        var rules = ModelRequestRules.For(Route(Moonshot, "kimi-k2.7-code-highspeed"));
        Assert.Empty(rules.ThinkingLevels); // the picker offers nothing to choose
        Assert.True(rules.AlwaysThinks);
        Assert.False(rules.ForcedToolChoice); // tool_choice: required is refused
    }

    /// <summary>K3: effort low/high/max (default max), never off.</summary>
    [Theory]
    [InlineData(ThinkingLevel.Off, "low")]
    [InlineData(ThinkingLevel.Low, "low")]
    [InlineData(ThinkingLevel.Medium, "high")]
    [InlineData(ThinkingLevel.High, "high")]
    [InlineData(ThinkingLevel.Max, "max")]
    public void KimiK3TakesLowHighMax(ThinkingLevel level, string word)
    {
        var k3 = Route(Moonshot, "kimi-k3", temperature: 0.4);
        var body = Body(k3, level);
        Assert.Equal(word, Effort(body));
        Assert.Null(body["thinking"]);
        Assert.Null(body["temperature"]);
        Assert.Equal([ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max], ModelRequestRules.For(k3).ThinkingLevels);
    }

    /// <summary>Kimi Code: low/high/max, and "none" turns thinking off.</summary>
    [Fact]
    public void KimiCodeOffIsNone()
    {
        var kfc = Route(KimiCode, "kimi-for-coding", temperature: 0.5);
        Assert.Equal("none", Effort(Body(kfc, ThinkingLevel.Off)));
        Assert.Equal("max", Effort(Body(kfc, ThinkingLevel.Max)));
        Assert.Null(Body(kfc, ThinkingLevel.Off)["temperature"]);
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max], ModelRequestRules.For(kfc, "k3").ThinkingLevels);
        Assert.Equal(KnownProvider.KimiCode, KnownProviders.Of("https://api.kimi.ai/coding/v1"));
        Assert.Equal(KnownProvider.Moonshot, KnownProviders.Of("https://api.moonshot.cn/v1"));
        Assert.Equal(KnownProvider.Other, KnownProviders.Of("http://192.168.1.4:8000/v1"));
    }

    /// <summary>The retired K2 previews and moonshot-v1 take a temperature (0…1) and no thinking setting.</summary>
    [Fact]
    public void OlderMoonshotModelsKeepATemperatureInRange()
    {
        var body = Body(Route(Moonshot, "moonshot-v1-128k", temperature: 1.6), ThinkingLevel.High);
        Assert.Equal(1.0, body["temperature"]!.GetValue<double>());
        Assert.Null(body["reasoning_effort"]);
        Assert.Null(body["thinking"]);
    }

    /// <summary>Kimi's thinking models want each tool-calling assistant message to carry its reasoning.</summary>
    [Fact]
    public void KimiReasoningGoesBackWithToolCalls()
    {
        var k26 = Route(Moonshot, "kimi-k2.6");
        var kept = LlmMessage.Assistant("", [new ToolCall("c1", "read_file", "{}")]) with { ProviderState = ReasoningEcho.State("I should read the file first.") };
        var lost = LlmMessage.Assistant("", [new ToolCall("c2", "read_file", "{}")]);
        var plain = LlmMessage.Assistant("All done.");
        var body = Body(k26, messages:
        [
            LlmMessage.User("go"), kept, LlmMessage.ToolOutput("c1", "read_file", "x"),
            lost, LlmMessage.ToolOutput("c2", "read_file", "y"), plain, LlmMessage.User("thanks"),
        ]);
        var assistants = body["messages"]!.AsArray().Where(m => m!["role"]!.GetValue<string>() == "assistant").ToList();
        Assert.Equal("I should read the file first.", assistants[0]!["reasoning_content"]!.GetValue<string>());
        Assert.Equal(ReasoningEcho.Placeholder, assistants[1]!["reasoning_content"]!.GetValue<string>()); // a stand-in, not a 400
        Assert.Null(assistants[2]!["reasoning_content"]);
        // Other servers never see it.
        var other = Body(Route("https://api.openai.com/v1", "gpt-4.1"), messages: [LlmMessage.User("go"), kept]);
        Assert.Null(other["messages"]![1]!["reasoning_content"]);
        // K2.6 with thinking switched off wants no stand-in (it keeps real reasoning it was given).
        var off = Body(k26, ThinkingLevel.Off, [LlmMessage.User("go"), lost, LlmMessage.ToolOutput("c2", "read_file", "y"), LlmMessage.User("next")]);
        Assert.Null(off["messages"]!.AsArray().First(m => m!["role"]!.GetValue<string>() == "assistant")!["reasoning_content"]);
    }

    /// <summary>Kimi's and the plans' context windows, when the server doesn't report one.</summary>
    [Theory]
    [InlineData("kimi-k3", 1_000_000)]
    [InlineData("kimi-k2.7-code", 262_144)]
    [InlineData("kimi-k2.6", 262_144)]
    [InlineData("moonshotai/kimi-k2.6-preview", 262_144)]
    [InlineData("glm-5.3", 1_000_000)]
    [InlineData("k3", 1_000_000)]
    [InlineData("k3-256k", 262_144)]
    [InlineData("kimi-for-coding", 1_000_000)]
    public void KimiAndGlmWindowsAreKnown(string model, int window) => Assert.Equal(window, FallbackContextWindow.Limit(model));

    /// <summary>The reasoning a thinking model streamed rides on the Done event, so the engine stores it on the message.</summary>
    [Fact]
    public async Task AKimiReplysReasoningIsKeptForTheNextRequest()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"reasoning_content":"Look first."}}]}""",
            """{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}"""));
        var done = (await Collect(new OpenAiClient(Route(Moonshot, "kimi-k2.6"), handler, new RequestAdaptationStore()), Hi("kimi-k2.6")))
            .OfType<LlmStreamEvent.Done>().Single();
        Assert.Equal("Look first.", ReasoningEcho.Reasoning(done.ProviderState));
        // A model that doesn't want it back gets none kept.
        var other = (await Collect(new OpenAiClient(Route("http://10.0.0.5:8000/v1", "qwen3.8"), handler, new RequestAdaptationStore()), Hi("qwen3.8")))
            .OfType<LlmStreamEvent.Done>().Single();
        Assert.Null(other.ProviderState);
    }

    // MARK: Z.ai

    /// <summary>GLM-5.3: always thinks (low/high/max), temperature 0…1, at most 131072 output tokens, plain-string content.</summary>
    [Fact]
    public void Glm53OnZaiGetsWhatZaiTakes()
    {
        var glm = Route(ZaiCoding, "glm-5.3", temperature: 1.4, maxTokens: 200_000);
        var image = new MessageAttachment(AttachmentKind.Image, "shot.png", [0x89, 0x50]);
        var notes = new MessageAttachment(AttachmentKind.File, "notes.md", "# Notes\nhello"u8.ToArray());
        var body = Body(glm, ThinkingLevel.Off, [LlmMessage.User("look", [image, notes])]);
        Assert.Equal("low", Effort(body)); // never off
        Assert.Null(body["thinking"]);
        Assert.Equal(1.0, body["temperature"]!.GetValue<double>());
        Assert.Equal(131_072, body["max_tokens"]!.GetValue<int>());
        Assert.Null(body["chat_template_kwargs"]); // a hosted API never gets template kwargs
        var text = body["messages"]![0]!["content"]!.GetValue<string>(); // Z.ai's text models take a string, not parts
        Assert.StartsWith("look", text);
        Assert.Contains("[Attached file: notes.md]\n# Notes\nhello", text); // the text Vision read, never a file part
        Assert.DoesNotContain("image_url", body.ToJsonString()); // a picture goes as its text (OCR), never as a part
        Assert.Contains("shot.png", text);
        var rules = ModelRequestRules.For(glm);
        Assert.Equal([ThinkingLevel.Low, ThinkingLevel.High, ThinkingLevel.Max], rules.ThinkingLevels);
        Assert.False(rules.AcceptsImages);
        Assert.False(rules.ForcedToolChoice); // tool_choice: only auto
    }

    [Fact]
    public void OlderGlmsToggleAndGlm52SkipsWithNone()
    {
        Assert.Equal("disabled", ThinkingType(Body(Route(ZaiGeneral, "glm-4.7"), ThinkingLevel.Off)));
        Assert.Equal("enabled", ThinkingType(Body(Route(ZaiGeneral, "glm-5.1"), ThinkingLevel.High)));
        Assert.Null(Effort(Body(Route(ZaiGeneral, "glm-4.6"), ThinkingLevel.High))); // reasoning_effort is GLM-5.2 and later only
        Assert.Equal("On", ModelRequestRules.For(Route(ZaiGeneral, "glm-4.7")).Label(ThinkingLevel.High));
        Assert.Equal("none", Effort(Body(Route(ZaiGeneral, "glm-5.2"), ThinkingLevel.Off)));
        Assert.Equal("high", Effort(Body(Route(ZaiGeneral, "glm-5.2"), ThinkingLevel.Low)));
        // The vision model takes parts.
        var flash = Body(Route(ZaiGeneral, "glm-5.3-flash"), messages: [LlmMessage.User("see", [new MessageAttachment(AttachmentKind.Image, "a.png", [1])])]);
        Assert.IsType<JsonArray>(flash["messages"]![0]!["content"]);
    }

    /// <summary>A GLM-5 anywhere else always thinks with its own words; on a server you run, the word also goes to the
    /// template (as Vision for macOS sends it).</summary>
    [Fact]
    public void Glm5ElsewhereKeepsItsOwnWords()
    {
        var local = Body(new ProviderProfile(ProviderKind.OpenAICompat, "Spark", "http://192.168.1.42:8000/v1", "glm-5.3"), ThinkingLevel.Off);
        Assert.Equal("low", Effort(local));
        Assert.Equal("low", local["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Null(local["chat_template_kwargs"]!["enable_thinking"]); // never "switch thinking off"
        var qwenPlan = Body(Route("https://coding-intl.dashscope.aliyuncs.com/v1", "glm-5"), ThinkingLevel.Medium);
        Assert.Equal("high", Effort(qwenPlan));
        Assert.Null(qwenPlan["chat_template_kwargs"]);
    }

    // MARK: OpenAI

    [Fact]
    public void OpenAiReasoningModelsWantMaxCompletionTokensAndNoTemperature()
    {
        var body = Body(Route("https://api.openai.com/v1", "gpt-5.2", temperature: 0.3, maxTokens: 4000), ThinkingLevel.High);
        Assert.Null(body["temperature"]);
        Assert.Null(body["max_tokens"]);
        Assert.Equal(4000, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("high", Effort(body));
        var classic = Body(Route("https://api.openai.com/v1", "gpt-4.1", temperature: 0.3, maxTokens: 4000));
        Assert.Equal(0.3, classic["temperature"]!.GetValue<double>());
        Assert.Equal(4000, classic["max_tokens"]!.GetValue<int>());
    }

    // MARK: The picker

    [Fact]
    public void NearestLevelsAndCaveats()
    {
        var glm = ModelRequestRules.For(Route(ZaiCoding, "glm-5.3"));
        Assert.Equal(ThinkingLevel.Low, glm.NearestLevel(ThinkingLevel.Off));
        Assert.Equal(ThinkingLevel.High, glm.NearestLevel(ThinkingLevel.Medium)); // a tie goes to the stronger
        Assert.Equal(" This model offers Low, High, Max, so it gets High.", glm.Caveat(ThinkingLevel.Medium));
        Assert.Equal("", glm.Caveat(ThinkingLevel.Max));
        var k27 = ModelRequestRules.For(Route(Moonshot, "kimi-k2.7-code"));
        Assert.Null(k27.NearestLevel(ThinkingLevel.High));
        Assert.Equal(" This model always thinks and takes no setting, so it changes nothing here.", k27.Caveat(ThinkingLevel.High));
        Assert.Equal(" This model has no thinking setting.", ModelRequestRules.For(Route(Moonshot, "moonshot-v1-8k")).Caveat(ThinkingLevel.Low));
        var toggle = ModelRequestRules.For(Route(Moonshot, "kimi-k2.6"));
        Assert.Equal([ThinkingLevel.Off, ThinkingLevel.High], toggle.ThinkingLevels);
        Assert.Equal(" This model offers Off, On, so it gets On.", toggle.Caveat(ThinkingLevel.Low));
        Assert.Equal(ThinkingLevels.All, ModelRequestRules.For(Route("http://192.168.1.42:8000/v1", "qwen3.8")).ThinkingLevels);
    }

    // MARK: Reading a 400

    private static RequestChange? Change(string body, IReadOnlySet<string> sent, int status = 400) =>
        ParameterRejection.Change(status, body, sent);

    private static readonly IReadOnlySet<string> SentAll = new HashSet<string>
    {
        "model", "messages", "stream", "stream_options", "temperature", "max_tokens", "reasoning_effort", "thinking", "tools",
    };

    [Fact]
    public void RefusalsThatNameAParameter()
    {
        // Moonshot (quoted in cline/cline#10544 and microsoft/vscode#321514).
        Assert.Equal(new RequestChange.Drop("temperature"),
            Change("""{"error":{"message":"invalid temperature: only 1 is allowed for this model","type":"invalid_request_error"}}""", SentAll));
        // OpenAI's reasoning models.
        Assert.Equal(new RequestChange.Rename("max_tokens", "max_completion_tokens"),
            Change("""{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.","type":"invalid_request_error","param":"max_tokens","code":"unsupported_parameter"}}""", SentAll));
        Assert.Equal(new RequestChange.Drop("temperature"),
            Change("""{"error":{"message":"Unsupported value: 'temperature' does not support 0.7 with this model. Only the default (1) value is supported.","type":"invalid_request_error","param":"temperature","code":"unsupported_value"}}""", SentAll));
        // vLLM refusing a field it doesn't know.
        Assert.Equal(new RequestChange.Drop("chat_template_kwargs"),
            Change("""{"object":"error","message":"[{'type': 'extra_forbidden', 'loc': ('body', 'chat_template_kwargs'), 'msg': 'Extra inputs are not permitted', 'input': {'enable_thinking': False}}]","type":"BadRequestError","param":null,"code":400}""",
                new HashSet<string> { "model", "messages", "chat_template_kwargs", "chat_template_kwargs.enable_thinking" }));
        // FastAPI-style detail list.
        Assert.Equal(new RequestChange.Drop("stream_options"),
            Change("""{"detail":[{"loc":["body","stream_options"],"msg":"Extra inputs are not permitted","type":"extra_forbidden"}]}""", SentAll, 422));
        // Z.ai 1214 names its field.
        Assert.Equal(new RequestChange.Drop("max_tokens"),
            Change("""{"error":{"code":"1214","message":"Parameter `max_tokens` is invalid. Please check the documentation."}}""", SentAll));
        // A model that can't stop thinking, refusing the toggle.
        Assert.Equal(new RequestChange.Drop("thinking"),
            Change("""{"error":{"message":"invalid thinking type: only enabled is allowed for this model","type":"invalid_request_error"}}""", SentAll));
        Assert.Equal(new RequestChange.Drop("reasoning_effort"),
            Change("""{"error":{"message":"reasoning_effort is not supported for this model","type":"invalid_request_error"}}""", SentAll));
        // A server that doesn't know tool_choice / parallel_tool_calls / response_format.
        var more = new HashSet<string>(SentAll) { "tool_choice", "parallel_tool_calls", "response_format" };
        Assert.Equal(new RequestChange.Drop("tool_choice"),
            Change("""{"error":{"message":"tool_choice 'required' is not supported for this model","type":"invalid_request_error"}}""", more));
        Assert.Equal(new RequestChange.Drop("parallel_tool_calls"),
            Change("""{"error":{"message":"Unrecognized request argument supplied: parallel_tool_calls"}}""", more));
        Assert.Equal(new RequestChange.Drop("response_format"),
            Change("""{"error":{"message":"response_format is not supported by this model"}}""", more));
        Assert.Equal(new RequestChange.Drop("stream_options"),
            Change("""{"error":{"message":"Unknown parameter: stream_options.include_usage"}}""", SentAll));
        Assert.Equal(new RequestChange.Drop("chat_template_kwargs.enable_thinking"),
            Change("""{"object":"error","message":"enable_thinking is not supported by this chat template"}""",
                new HashSet<string> { "model", "messages", "chat_template_kwargs", "chat_template_kwargs.enable_thinking" }));
    }

    [Fact]
    public void KimiMissingReasoningAsksForTheEcho()
    {
        // Kimi Code's error reference.
        Assert.Equal(new RequestChange.EchoReasoning(),
            Change("""{"error":{"message":"thinking is enabled but reasoning_content is missing in assistant tool call message at index 2","type":"invalid_request_error"}}""", SentAll));
    }

    [Fact]
    public void AnUnnamedParameterRefusalFallsBackToTheBasics()
    {
        Assert.Equal(new RequestChange.Minimal(),
            Change("""{"error":{"code":"1210","message":"Invalid API parameter, please check the documentation."}}""", SentAll));
        Assert.Null(Change("""{"error":{"code":"1210","message":"Invalid API parameter, please check the documentation."}}""",
            new HashSet<string> { "model", "messages", "stream" })); // nothing optional to leave out
    }

    [Fact]
    public void RefusalsThatAreNotAboutParameters()
    {
        Assert.Null(Change("""{"error":{"code":"1211","message":"Unknown Model, please check the model code."}}""", SentAll));
        Assert.Null(Change("""{"error":{"message":"Invalid 'messages[1].content': string too long.","type":"invalid_request_error"}}""", SentAll));
        Assert.Null(Change("""{"error":{"message":"invalid temperature: only 1 is allowed for this model"}}""", SentAll, 500));
        Assert.Null(Change("""{"error":{"message":"invalid temperature: only 1 is allowed"}}""", new HashSet<string> { "model", "messages" })); // not sent
    }

    [Fact]
    public void ARefusalAboutTheMessagesToolsOrModelIsNeverADrop()
    {
        // FastAPI/vLLM validation errors point into the messages or tools: what was in them was wrong. Learning "drop
        // messages" would strip them from every later request to that server and model.
        var sent = new HashSet<string> { "model", "messages", "tools", "stream", "temperature" };
        Assert.Null(Change("""{"detail":[{"loc":["body","messages",1,"content"],"msg":"Field required","type":"missing"}]}""", sent, 422));
        Assert.Null(Change("""{"detail":[{"loc":["body","tools",0,"function","parameters"],"msg":"Input should be a valid dictionary","type":"dict_type"}]}""", sent, 422));
        // OpenAI-style param errors.
        Assert.Null(Change("""{"error":{"message":"Invalid value for 'content': expected a string, got null.","type":"invalid_request_error","param":"messages","code":null}}""", sent));
        Assert.Null(Change("""{"error":{"param":"messages"}}""", sent));
        Assert.Null(Change("""{"error":{"message":"The model `qwen9` does not exist","type":"invalid_request_error","param":"model","code":"model_not_found"}}""", sent));
        Assert.Null(Change("""{"error":{"param":"model"}}""", sent));
        Assert.Null(Change("""{"error":{"param":"stream","message":"stream is not supported"}}""", sent));
        // An optional parameter named next to them is still learned.
        Assert.Equal(new RequestChange.Drop("temperature"),
            Change("""{"detail":[{"loc":["body","temperature"],"msg":"Extra inputs are not permitted","type":"extra_forbidden"}]}""", sent, 422));
        Assert.True(RequestBody.Learnable("chat_template_kwargs.enable_thinking"));
        Assert.False(RequestBody.Learnable("messages"));
        Assert.False(RequestBody.Learnable("tools"));
        Assert.False(RequestBody.Learnable("some_new_field"));
    }

    [Fact]
    public void NoticesSayWhatChangedInOneLine()
    {
        Assert.Equal("Moonshot (kimi-k2.6) doesn't take `temperature` — Vision stopped sending it for this model and carried on.",
            ParameterRejection.Notice(new RequestChange.Drop("temperature"), "Moonshot", "kimi-k2.6"));
        Assert.Equal("Spark (qwen) doesn't take `enable_thinking` — Vision stopped sending it for this model and carried on.",
            ParameterRejection.Notice(new RequestChange.Drop("chat_template_kwargs.enable_thinking"), "Spark", "qwen"));
        Assert.Equal("OpenAI (o9) wants `max_completion_tokens` instead of `max_tokens` — Vision switched and carried on.",
            ParameterRejection.Notice(new RequestChange.Rename("max_tokens", "max_completion_tokens"), "OpenAI", "o9"));
    }

    // MARK: The safety net, end to end

    private static LlmRequest Hi(string model) => new("", [LlmMessage.User("hi")], [], model);

    private static async Task<List<LlmStreamEvent>> Collect(OpenAiClient client, LlmRequest request)
    {
        var events = new List<LlmStreamEvent>();
        await foreach (var e in client.StreamAsync(request)) events.Add(e);
        return events;
    }

    private static FakeHttpHandler Scripted(params (HttpStatusCode Status, string Body)[] replies)
    {
        var calls = 0;
        return new FakeHttpHandler(_ =>
        {
            var index = Interlocked.Increment(ref calls) - 1;
            return index < replies.Length
                ? FakeHttpHandler.Json(replies[index].Body, replies[index].Status)
                : FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"ok"},"finish_reason":"stop"}]}""");
        });
    }

    private static async Task<string> Run(ProviderProfile profile, FakeHttpHandler handler, RequestAdaptationStore store, List<string> notices)
    {
        var text = "";
        var client = new OpenAiClient(profile, handler, store, notice => { lock (notices) notices.Add(notice); });
        foreach (var e in await Collect(client, Hi(profile.Model)))
            if (e is LlmStreamEvent.Text t) text += t.Delta;
        return text;
    }

    private static JsonObject Sent(FakeHttpHandler handler, int index) => JsonNode.Parse(handler.Requests[index].Body)!.AsObject();

    /// <summary>A model the table doesn't know refuses the temperature: one retry without it, one line in the chat, and the
    /// lesson is kept on disk — the next launch sends it right the first time.</summary>
    [Fact]
    public async Task ARefusedTemperatureIsDroppedRememberedAndAnnounced()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "request-adaptations.json");
        var store = new RequestAdaptationStore(file);
        var handler = Scripted((HttpStatusCode.BadRequest, """{"error":{"message":"invalid temperature: only 1 is allowed for this model","type":"invalid_request_error"}}"""));
        var profile = Route(Moonshot, "kimi-k9-preview", temperature: 0.7);
        var notices = new List<string>();

        Assert.Equal("ok", await Run(profile, handler, store, notices));
        Assert.Equal(2, handler.Requests.Count); // one retry
        Assert.Equal(0.7, Sent(handler, 0)["temperature"]!.GetValue<double>());
        Assert.Null(Sent(handler, 1)["temperature"]);
        Assert.Equal(["Moonshot (kimi-k9-preview) doesn't take `temperature` — Vision stopped sending it for this model and carried on."], notices);

        // Remembered across a relaunch, per server and model.
        var reloaded = new RequestAdaptationStore(file);
        Assert.Equal(["temperature"], reloaded.Adaptation(RequestAdaptationStore.Key(Moonshot, "kimi-k9-preview"))!.Dropped);
        Assert.Null(reloaded.Adaptation(RequestAdaptationStore.Key(Moonshot, "kimi-k10")));
        Assert.Contains("\"dropped\"", File.ReadAllText(file)); // the shape Vision for macOS writes
        var again = Scripted();
        await Run(profile, again, reloaded, notices);
        Assert.Single(again.Requests);
        Assert.Null(Sent(again, 0)["temperature"]);
        Assert.Single(notices); // nothing new to say
    }

    /// <summary>Two refused parameters in a row are each dropped once; a third refusal of something already dropped is
    /// reported, not looped on.</summary>
    [Fact]
    public async Task EachParameterIsRetriedOnceAndThenTheRefusalSurfaces()
    {
        var store = new RequestAdaptationStore();
        const string temperature = """{"error":{"message":"invalid temperature: only 1 is allowed for this model","type":"invalid_request_error"}}""";
        const string usage = """{"error":{"message":"stream_options is not supported","type":"invalid_request_error"}}""";
        var profile = Route("https://llm.example.com/v1", "mystery", temperature: 0.4);
        var handler = Scripted((HttpStatusCode.BadRequest, temperature), (HttpStatusCode.BadRequest, usage), (HttpStatusCode.BadRequest, temperature));
        var notices = new List<string>();

        var error = await Assert.ThrowsAsync<LlmException>(() => Run(profile, handler, store, notices));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, notices.Count);
        Assert.Null(Sent(handler, 2)["stream_options"]);
        Assert.Null(Sent(handler, 2)["temperature"]);
        Assert.StartsWith("Route (mystery) doesn't take `temperature`", notices[0]);
    }

    /// <summary>Kimi's "reasoning_content is missing" turns the echo on for that model and the request goes again with it.</summary>
    [Fact]
    public async Task AMissingReasoningRefusalTurnsTheEchoOn()
    {
        var store = new RequestAdaptationStore();
        var handler = Scripted((HttpStatusCode.BadRequest,
            """{"error":{"message":"thinking is enabled but reasoning_content is missing in assistant tool call message at index 1","type":"invalid_request_error"}}"""));
        var profile = Route("https://kimi-proxy.example.com/v1", "kimi-k3");
        var client = new OpenAiClient(profile, handler, store);
        var request = new LlmRequest("", [LlmMessage.User("go"), LlmMessage.Assistant("", [new ToolCall("c1", "read_file", "{}")]),
            LlmMessage.ToolOutput("c1", "read_file", "x")], [], "kimi-k3");
        await foreach (var _ in client.StreamAsync(request)) { }
        Assert.Null(Sent(handler, 0)["messages"]![1]!["reasoning_content"]);
        Assert.Equal(ReasoningEcho.Placeholder, Sent(handler, 1)["messages"]![1]!["reasoning_content"]!.GetValue<string>());
        Assert.True(store.Adaptation(RequestAdaptationStore.Key(profile.BaseUrl, "kimi-k3"))!.EchoReasoning);
    }

    /// <summary>Z.ai's "no balance" is HTTP 429 but not a rate limit: it fails at once with the fix, instead of retrying for
    /// ever. A real rate limit (1302) is still waited out.</summary>
    [Fact]
    public async Task ZaiNoBalanceFailsAtOnceAndRateLimitsAreStillRetried()
    {
        var noBalance = Scripted(((HttpStatusCode)429, """{"error":{"code":"1113","message":"Insufficient balance or no resource package. Please recharge."}}"""));
        var error = await Assert.ThrowsAsync<LlmException>(() => Run(Route(ZaiGeneral, "glm-5.3"), noBalance, new RequestAdaptationStore(), []));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(error));
        Assert.Contains("switch the endpoint to Coding Plan", error.Message);
        Assert.Equal(429, error.StatusCode);

        var rateLimited = Scripted(((HttpStatusCode)429, """{"error":{"code":"1302","message":"Rate limit reached for requests"}}"""));
        var limit = await Assert.ThrowsAsync<LlmException>(() => Run(Route(ZaiCoding, "glm-5.3"), rateLimited, new RequestAdaptationStore(), []));
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(limit));
    }

    [Fact]
    public async Task ZaiPromptTooLongAndKimiLimitsAreOverflows()
    {
        var tooLong = Scripted((HttpStatusCode.BadRequest, """{"error":{"code":"1261","message":"Prompt too long"}}"""));
        var error = await Assert.ThrowsAsync<LlmException>(() => Run(Route(ZaiCoding, "glm-5.3"), tooLong, new RequestAdaptationStore(), []));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);

        var kimi = Scripted((HttpStatusCode.BadRequest, """{"error":{"message":"Your request exceeded model token limit: 262144","type":"invalid_request_error"}}"""));
        var overflow = await Assert.ThrowsAsync<LlmException>(() => Run(Route(KimiCode, "kimi-for-coding"), kimi, new RequestAdaptationStore(), []));
        Assert.Equal(LlmErrorKind.Overflow, overflow.Kind);
        Assert.Equal(262_144, overflow.Limit);

        var size = Scripted((HttpStatusCode.BadRequest, """{"error":{"message":"total message size 5943865 exceeds limit 2097152","type":"invalid_request_error"}}"""));
        Assert.Equal(LlmErrorKind.Overflow,
            (await Assert.ThrowsAsync<LlmException>(() => Run(Route(KimiCode, "kimi-for-coding"), size, new RequestAdaptationStore(), []))).Kind);
    }

    /// <summary>Z.ai ends a reply with its own finish reasons: a full window is an overflow, a network failure is retried.</summary>
    [Fact]
    public async Task ZaiFinishReasonsAreUnderstood()
    {
        var full = new FakeHttpHandler(_ => FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"par"},"finish_reason":"model_context_window_exceeded"}]}"""));
        var error = await Assert.ThrowsAsync<LlmException>(() => Run(Route(ZaiCoding, "glm-5.3"), full, new RequestAdaptationStore(), []));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
        var dropped = new FakeHttpHandler(_ => FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"par"},"finish_reason":"network_error"}]}"""));
        var cut = await Assert.ThrowsAsync<LlmException>(() => Run(Route(ZaiCoding, "glm-5.3"), dropped, new RequestAdaptationStore(), []));
        Assert.IsType<RetryDisposition.UntilAvailable>(RequestRetry.Disposition(cut));
        var filtered = new FakeHttpHandler(_ => FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":""},"finish_reason":"sensitive"}]}"""));
        var refused = await Assert.ThrowsAsync<LlmException>(() => Run(Route(ZaiCoding, "glm-5.3"), filtered, new RequestAdaptationStore(), []));
        Assert.IsType<RetryDisposition.Fail>(RequestRetry.Disposition(refused));
        Assert.Contains("content filter", refused.Message);
    }

    [Fact]
    public void ZaiErrorsInPlainWords()
    {
        var expired = ZaiErrors.Explain(429, """{"error":{"code":"1309","message":"Your GLM Coding Plan package has expired"}}""", ZaiEndpoint.Coding, "glm-5.3");
        Assert.True(expired!.Permanent);
        Assert.Contains("z.ai/subscribe", expired.Message);
        var limit = ZaiErrors.Explain(429, """{"error":{"code":"1308","message":"Usage limit reached for 5 hour. Your limit will reset at 2026-10-19 03:00:00"}}""",
            ZaiEndpoint.Coding, "glm-5.3");
        Assert.Equal("The Z.ai plan's usage limit is used up (1308). It resets at 2026-10-19 03:00:00. Wait for it, or switch this chat to another model.", limit!.Message);
        var unknown = ZaiErrors.Explain(400, """{"error":{"code":"1211","message":"Unknown Model, please check the model code."}}""", ZaiEndpoint.General, "glm-5.4");
        Assert.Equal("Z.ai doesn't know the model `glm-5.4` (1211). Pick one of its models — glm-5.3 is the current one.", unknown!.Message);
        Assert.Null(ZaiErrors.Explain(429, """{"error":{"code":"1305","message":"The service may be temporarily overloaded"}}""", ZaiEndpoint.Coding, "glm-5.3")); // waited out
        // The real body api.z.ai sends without a key (recorded 2026-10-18).
        Assert.True(ZaiErrors.Explain(401, """{"error":{"code":"1001","message":"Authentication parameter not received in Header, unable to authenticate"}}""",
            ZaiEndpoint.General, "glm-5.3")!.Permanent);
        Assert.Equal(ZaiEndpoint.ChinaCoding, ZaiEndpoints.Of("https://open.bigmodel.cn/api/coding/paas/v4/"));
        Assert.Equal(ZaiEndpoint.Coding, ZaiEndpoints.Of(ZaiCoding));
        Assert.Equal(ZaiEndpoint.General, ZaiEndpoints.Of(ZaiGeneral));
        Assert.Null(ZaiEndpoints.Of("https://api.openai.com/v1"));
        Assert.Equal(ZaiEndpoint.ChinaGeneral, ZaiEndpoints.FromRaw("chinaGeneral"));
    }

    [Fact]
    public void KimiErrorsInPlainWords()
    {
        Assert.Contains("Kimi Code console", KimiErrors.Explain(401, """{"error":{"message":"Invalid Authentication","type":"invalid_authentication_error"}}""",
            KnownProvider.KimiCode, "kimi-for-coding")!.Message);
        Assert.Contains("doesn't include `k3`", KimiErrors.Explain(401, """{"error":{"message":"Your current subscription does not have access to k3"}}""",
            KnownProvider.KimiCode, "k3")!.Message);
        Assert.Contains("usage limit", KimiErrors.Explain(403, """{"error":{"message":"You've reached your 5-hour usage limit"}}""",
            KnownProvider.KimiCode, "k3")!.Message);
        Assert.Contains("kimi-k3", KimiErrors.Explain(404, """{"error":{"message":"Not found the model kimi-latest or Permission denied"}}""",
            KnownProvider.Moonshot, "kimi-latest")!.Message);
        Assert.Null(KimiErrors.Explain(429, """{"error":{"message":"The engine is currently overloaded"}}""", KnownProvider.KimiCode, "k3"));
    }

    // MARK: Testing a Z.ai key

    [Fact]
    public async Task ACodingPlanKeyOnTheGeneralEndpointIsRecognised()
    {
        var noBalance = LlmException.Rejected(429, "x", """{"error":{"code":"1113","message":"Insufficient balance or no resource package. Please recharge."}}""", permanent: true);
        var result = await ZaiDiagnosis.RunAsync(ZaiEndpoint.General, "glm-5.3", (endpoint, _) =>
            endpoint != ZaiEndpoint.Coding ? throw noBalance : Task.CompletedTask);
        Assert.False(result.Ok);
        Assert.Equal(ZaiEndpoint.Coding, result.WorksOn);
        Assert.StartsWith("This looks like a GLM Coding Plan key — switch the endpoint to Coding Plan.", result.Message);
    }

    [Fact]
    public async Task AChinaKeyOnTheInternationalEndpointIsRecognised()
    {
        var refused = LlmException.Http(401, """{"error":{"code":"401","message":"token expired or incorrect"}}""");
        var result = await ZaiDiagnosis.RunAsync(ZaiEndpoint.Coding, "glm-5.3", (endpoint, _) =>
            endpoint != ZaiEndpoint.ChinaCoding ? throw refused : Task.CompletedTask);
        Assert.Equal(ZaiEndpoint.ChinaCoding, result.WorksOn);
        Assert.Equal("This key is from open.bigmodel.cn — switch the endpoint to China — Coding Plan.", result.Message);
    }

    [Fact]
    public async Task AKeyThatWorksNowhereGetsTheFirstExplanation()
    {
        var refused = LlmException.Http(401, """{"error":{"code":"401","message":"token expired or incorrect"}}""");
        var result = await ZaiDiagnosis.RunAsync(ZaiEndpoint.Coding, "glm-5.3", (_, _) => throw refused);
        Assert.Null(result.WorksOn);
        Assert.StartsWith("Z.ai didn't accept the key (401: token expired or incorrect).", result.Message);
        var ok = await ZaiDiagnosis.RunAsync(ZaiEndpoint.Coding, "glm-5.3", (_, _) => Task.CompletedTask);
        Assert.True(ok.Ok);
        Assert.Equal("✔ The key works on Coding Plan with glm-5.3.", ok.Message);
        // A wrong model isn't a key problem: no other endpoint is tried.
        var tried = new List<ZaiEndpoint>();
        var model = await ZaiDiagnosis.RunAsync(ZaiEndpoint.Coding, "glm-9", (endpoint, _) =>
        {
            tried.Add(endpoint);
            throw LlmException.Http(400, """{"error":{"code":"1211","message":"Unknown Model, please check the model code."}}""");
        });
        Assert.Equal([ZaiEndpoint.Coding], tried);
        Assert.Contains("1211", model.Message);
    }

    // MARK: The memory

    [Fact]
    public void TheStoreForgetsByServerAndSurvivesADamagedFile()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "request-adaptations.json");
        File.WriteAllText(file, "{ not json");
        var store = new RequestAdaptationStore(file);
        Assert.Empty(store.All);
        Assert.True(store.Learn(new RequestChange.Drop("temperature"), RequestAdaptationStore.Key("https://API.moonshot.ai/v1/", "kimi-k3")));
        Assert.False(store.Learn(new RequestChange.Drop("temperature"), RequestAdaptationStore.Key("https://api.moonshot.ai/v1", "kimi-k3")));
        store.Learn(new RequestChange.Minimal(), RequestAdaptationStore.Key("https://api.z.ai/api/paas/v4", "glm-5"));
        Assert.True(store.Adaptation(RequestAdaptationStore.Key("https://api.z.ai/api/paas/v4", "glm-5"))!.Contains(new RequestChange.Drop("top_p")));
        Assert.Equal("only the basic settings", store.Adaptation(RequestAdaptationStore.Key("https://api.z.ai/api/paas/v4", "glm-5"))!.Summary);
        store.Forget("https://api.moonshot.ai/v1/");
        Assert.Single(new RequestAdaptationStore(file).All);
        store.Learn(new RequestChange.Drop("thinking"), RequestAdaptationStore.Key("https://api.moonshot.ai/v1", "kimi-k2.6-x"));
        store.Learn(new RequestChange.Drop("thinking"), RequestAdaptationStore.Key("https://api.moonshot.ai/v1", "kimi-k2.6"));
        store.ForgetKey(RequestAdaptationStore.Key("https://api.moonshot.ai/v1", "kimi-k2.6"));
        Assert.NotNull(new RequestAdaptationStore(file).Adaptation(RequestAdaptationStore.Key("https://api.moonshot.ai/v1", "kimi-k2.6-x")));
        Assert.Null(new RequestAdaptationStore(file).Adaptation(RequestAdaptationStore.Key("https://api.moonshot.ai/v1", "kimi-k2.6")));
    }

    [Fact]
    public void TheStoreNeverLearnsOrKeepsADropOfWhatARequestNeeds()
    {
        using var dir = new TempDirectory();
        var file = Path.Combine(dir.Path, "request-adaptations.json");
        // A file written by the earlier build, after a 422 about a message's content: messages and tools dropped.
        File.WriteAllText(file, """
            {
              "http://spark:8000/v1|qwen": { "dropped": ["messages", "tools", "temperature"], "renamed": { "model": "x" } },
              "http://spark:8000/v1|other": { "dropped": ["model"] }
            }
            """);
        var store = new RequestAdaptationStore(file);
        var kept = store.Adaptation("http://spark:8000/v1|qwen")!;
        Assert.Equal(["temperature"], kept.Dropped);
        Assert.Empty(kept.Renamed);
        Assert.Null(store.Adaptation("http://spark:8000/v1|other")); // nothing left of it
        Assert.False(store.Learn(new RequestChange.Drop("messages"), "http://spark:8000/v1|qwen"));
        Assert.False(store.Learn(new RequestChange.Drop("tools"), "http://spark:8000/v1|qwen"));
        Assert.False(store.Learn(new RequestChange.Rename("model", "model_id"), "http://spark:8000/v1|qwen"));
        var body = new System.Text.Json.Nodes.JsonObject { ["model"] = "qwen", ["messages"] = new System.Text.Json.Nodes.JsonArray(), ["tools"] = new System.Text.Json.Nodes.JsonArray(), ["temperature"] = 0.6 };
        RequestBody.Apply(store.Adaptation("http://spark:8000/v1|qwen")!, body);
        Assert.Equal(["model", "messages", "tools"], body.Select(p => p.Key).ToArray());
    }
}
