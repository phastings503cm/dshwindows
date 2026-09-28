using System.Net;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from the ThinkingTests class in ThinkingAndGoalTests.swift. Request bodies read the
/// process-wide <see cref="ReasoningEffortCache.Shared"/>, so these run serially and reset it around
/// every test.</summary>
[Collection(SerialStaticState.Name)]
public sealed class ThinkingTests : IDisposable
{
    private static readonly ProviderProfile Spark =
        new(ProviderKind.OpenAI, "Spark", "https://192.168.68.69:11443/v1", "qwen3.8-27b-sglang");

    private static readonly ProviderProfile HostedOpenAI =
        new(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "o4-mini");

    public ThinkingTests() => ReasoningEffortCache.Shared.Reset();

    public void Dispose() => ReasoningEffortCache.Shared.Reset();

    private static LlmRequest Request(string model, ThinkingLevel? thinking) =>
        new("", [LlmMessage.User("hi")], [], model, Thinking: thinking);

    private static JsonObject Body(ProviderProfile profile, ThinkingLevel? thinking) =>
        JsonNode.Parse(new OpenAiClient(profile).MakeBody(Request(profile.Model, thinking)))!.AsObject();

    [Fact]
    public void SelfHostedIsDecidedByHostNotKind()
    {
        Assert.True(Spark.IsSelfHosted); // kind OpenAI but a LAN box
        Assert.False(HostedOpenAI.IsSelfHosted);
        Assert.False(new ProviderProfile(ProviderKind.OpenRouter, "r", "https://openrouter.ai/api/v1", "x").IsSelfHosted);
    }

    [Fact]
    public void OffDisablesThinkingViaTemplateKwargs()
    {
        var body = Body(Spark, ThinkingLevel.Off);
        Assert.False(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(body["reasoning_effort"]);
    }

    [Fact]
    public void EffortGoesTopLevelAndIntoTemplateKwargs()
    {
        var body = Body(Spark, ThinkingLevel.Medium);
        var kwargs = body["chat_template_kwargs"]!;
        Assert.True(kwargs["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("medium", kwargs["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("medium", body["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void MaxMapsToXhigh()
    {
        Assert.Equal("xhigh", Body(Spark, ThinkingLevel.Max)["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void HostedApiGetsOnlyTopLevelEffortAndNothingWhenOff()
    {
        var on = Body(HostedOpenAI, ThinkingLevel.High);
        Assert.Equal("high", on["reasoning_effort"]!.GetValue<string>());
        Assert.Null(on["chat_template_kwargs"]);
        var off = Body(HostedOpenAI, ThinkingLevel.Off);
        Assert.Null(off["reasoning_effort"]);
        Assert.Null(off["chat_template_kwargs"]);
    }

    [Fact]
    public void ProfileDefaultAppliesWhenRequestHasNone()
    {
        var profile = Spark.DeepCopy();
        profile.Thinking = ThinkingLevel.Low;
        Assert.Equal("low", Body(profile, null)["reasoning_effort"]!.GetValue<string>());
        Assert.Null(Body(Spark, null)["chat_template_kwargs"]); // nothing set: the server's default
    }

    [Fact]
    public void LearnedEffortIsUsedOnTheWire()
    {
        ReasoningEffortCache.Shared.Learn($"{Spark.BaseUrl}|{Spark.Model}", "high", "xhigh");
        Assert.Equal("xhigh", Body(Spark, ThinkingLevel.High)["reasoning_effort"]!.GetValue<string>());
    }

    // New: ApplyThinking on a body directly, the local-server branch, and the learning round trip.

    [Fact]
    public void ApplyThinkingWritesIntoTheGivenObject()
    {
        var body = new JsonObject { ["model"] = "m" };
        new OpenAiClient(Spark).ApplyThinking(Request(Spark.Model, ThinkingLevel.Low), body);

        Assert.Equal("m", body["model"]!.GetValue<string>());
        Assert.True(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal("low", body["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("low", body["reasoning_effort"]!.GetValue<string>());

        var untouched = new JsonObject();
        new OpenAiClient(Spark).ApplyThinking(Request(Spark.Model, null), untouched);
        Assert.Empty(untouched);
    }

    [Fact]
    public void LocalServersGetTheSwitchOnlyWhenOffAndTheEffortOtherwise()
    {
        var ollama = new ProviderProfile(ProviderKind.Ollama, "Ollama", "http://127.0.0.1:11434/v1", "qwen3:8b");
        var off = Body(ollama, ThinkingLevel.Off);
        Assert.False(off["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(off["reasoning_effort"]);

        var high = Body(ollama, ThinkingLevel.High);
        Assert.Null(high["chat_template_kwargs"]);
        Assert.Equal("high", high["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public void LearningThatNoEffortIsAcceptedSendsNoneButKeepsThinkingOn()
    {
        ReasoningEffortCache.Shared.Learn($"{Spark.BaseUrl}|{Spark.Model}", "medium", "");
        var body = Body(Spark, ThinkingLevel.Medium);
        Assert.Null(body["reasoning_effort"]);
        Assert.True(body["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Null(body["chat_template_kwargs"]!["reasoning_effort"]);
    }

    [Fact]
    public void EffortWordIsNullWhenThinkingIsOffOrUnset()
    {
        var client = new OpenAiClient(Spark);
        Assert.Null(client.EffortWord(Request(Spark.Model, ThinkingLevel.Off)));
        Assert.Null(client.EffortWord(Request(Spark.Model, null)));
        Assert.Equal("high", client.EffortWord(Request(Spark.Model, ThinkingLevel.High)));
    }

    /// <summary>The template rejects "high" with a 400: the client learns the nearest accepted word,
    /// retries once with it, and remembers it for the route.</summary>
    [Fact]
    public async Task RejectedEffortIsLearnedAndRetriedOnce()
    {
        var calls = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref calls) == 1
            ? FakeHttpHandler.Json(
                """{"object":"error","message":"Unexpected reasoning effort high. Supported types are xhigh (default), medium, and low.","code":400}""",
                HttpStatusCode.BadRequest)
            : FakeHttpHandler.Sse("""{"choices":[{"delta":{"content":"391"}}]}"""));
        var client = new OpenAiClient(Spark, handler);

        var text = "";
        await foreach (var ev in client.StreamAsync(Request(Spark.Model, ThinkingLevel.High)))
            if (ev is LlmStreamEvent.Text t) text += t.Delta;

        Assert.Equal("391", text);
        var bodies = handler.Requests.Select(r => JsonNode.Parse(r.Body)!["reasoning_effort"]!.GetValue<string>()).ToList();
        Assert.Equal(["high", "xhigh"], bodies);
        Assert.Equal("xhigh", ReasoningEffortCache.Shared.Accepted($"{Spark.BaseUrl}|{Spark.Model}", "high"));
    }
}

/// <summary>Ported: effort-word correction and user-input parsing (pure functions, no shared state),
/// plus new checks of the level vocabulary and a private cache instance.</summary>
public sealed class ThinkingLevelTests
{
    /// <summary>The exact 400 Qwen3.8's template raises for "high".</summary>
    [Fact]
    public void EffortCorrectionFromQwenTemplateError()
    {
        const string error = """{"object":"error","message":"Unexpected reasoning effort high. Supported types are xhigh (default), medium, and low.","type":"BadRequest","code":400}""";
        Assert.Equal("xhigh", OpenAiClient.EffortCorrection("high", error));
        Assert.Equal("low", OpenAiClient.EffortCorrection("minimal",
            "Unexpected reasoning effort minimal. Supported types are xhigh (default), medium, and low."));
        Assert.Null(OpenAiClient.EffortCorrection("high", "model not found"));
    }

    [Fact]
    public void UserInputLevels()
    {
        Assert.Equal(ThinkingLevel.Off, ThinkingLevels.ParseUserInput("fast"));
        Assert.Equal(ThinkingLevel.Max, ThinkingLevels.ParseUserInput("XHIGH"));
        Assert.Equal(ThinkingLevel.Medium, ThinkingLevels.ParseUserInput("medium"));
        Assert.Null(ThinkingLevels.ParseUserInput("banana"));
    }

    [Fact]
    public void ModernQwenFallbackIsNot32K()
    {
        Assert.Equal(262_144, FallbackContextWindow.Limit("qwen3.8-27b-sglang"));
        Assert.Equal(262_144, FallbackContextWindow.Limit("qwen3.8-flash-next"));
        Assert.Equal(32_768, FallbackContextWindow.Limit("qwen3:8b"));
    }

    [Fact]
    public void EffortCorrectionPicksTheNearestWordAndPrefersTheStrongerOnATie()
    {
        Assert.Equal("high", OpenAiClient.EffortCorrection("xhigh",
            "Invalid reasoning_effort 'xhigh'. Must be one of: low, medium, high"));
        Assert.Equal("high", OpenAiClient.EffortCorrection("medium",
            "Unexpected reasoning effort medium. Supported types are low and high."));
        // It is about the effort, but no known word is offered: send none.
        Assert.Equal("", OpenAiClient.EffortCorrection("high", "reasoning_effort is not supported for this model"));
    }

    [Fact]
    public void RawValuesRoundTripAndWireWordsMatchTheTemplates()
    {
        foreach (var level in ThinkingLevels.All)
            Assert.Equal(level, ThinkingLevels.FromRaw(level.RawValue()));
        Assert.Null(ThinkingLevels.FromRaw(null));
        Assert.Null(ThinkingLevels.FromRaw("xhigh"));
        Assert.Null(ThinkingLevel.Off.WireEffort());
        Assert.Equal("xhigh", ThinkingLevel.Max.WireEffort());
        Assert.Equal(ThinkingLevel.High, ThinkingLevels.ParseUserInput("  3 "));

        var profile = new ProviderProfile { Thinking = ThinkingLevel.High };
        Assert.Equal("high", profile.ReasoningEffort);
        profile.Thinking = null;
        Assert.Null(profile.ReasoningEffort);
    }

    [Fact]
    public void ReasoningEffortCacheLearnsPerRouteAndResets()
    {
        var cache = new ReasoningEffortCache();
        cache.Learn("route-a", "high", "xhigh");
        Assert.Equal("xhigh", cache.Accepted("route-a", "high"));
        Assert.Null(cache.Accepted("route-b", "high"));
        Assert.Null(cache.Accepted("route-a", "low"));
        cache.Reset();
        Assert.Null(cache.Accepted("route-a", "high"));
    }
}
