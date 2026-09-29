using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Transcripts → ConverseStream request bodies: every Converse rule the client enforces, the
/// per-model thinking and caching shapes, and the replay of Claude's signed reasoning.</summary>
public sealed class BedrockConverseTests
{
    private const string Sonnet45 = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";
    private const string Opus41 = "anthropic.claude-opus-4-1-20250805-v1:0";
    private const string Opus46 = "global.anthropic.claude-opus-4-6-v1";
    private const string Opus47 = "us.anthropic.claude-opus-4-7";
    private const string Opus5 = "anthropic.claude-opus-5";
    private const string Sonnet55 = "anthropic.claude-sonnet-5-5";
    private const string Opus55 = "us.anthropic.claude-opus-5-5";
    private const string Haiku3 = "anthropic.claude-3-haiku-20240307-v1:0";
    private const string Llama = "meta.llama3-3-70b-instruct-v1:0";
    private const string Nova = "amazon.nova-pro-v1:0";

    private static readonly ToolSpec Echo = new("echo", "Echo text back", """{"type":"object","properties":{"text":{"type":"string"}}}""");

    private static ProviderProfile Profile(string model) =>
        new(ProviderKind.Bedrock, "Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com", model) { AwsRegion = "us-east-1", AwsProfile = "dsh" };

    private static JsonObject Body(IReadOnlyList<LlmMessage> messages, string model = Llama, IReadOnlyList<ToolSpec>? tools = null,
                                   ThinkingLevel? thinking = null, int? maxTokens = null, double? temperature = null,
                                   bool replay = true, ProviderProfile? profile = null, string system = "You are DSH.") =>
        BedrockConverse.Request(new LlmRequest(system, messages, tools ?? [], model, temperature, maxTokens, thinking),
                                profile ?? Profile(model), replay);

    private static JsonArray Turns(JsonObject body) => body["messages"]!.AsArray();

    private static string Role(JsonObject body, int turn) => Turns(body)[turn]!["role"]!.GetValue<string>();

    private static JsonArray Content(JsonObject body, int turn) => Turns(body)[turn]!["content"]!.AsArray();

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";

    // MARK: System, roles, merging

    [Fact]
    public void SystemPromptAndSystemNotesBecomeSystemBlocks()
    {
        var body = Body([
            LlmMessage.SystemText("[Earlier conversation, compacted] summary"),
            LlmMessage.User("hi"),
            LlmMessage.Assistant("hello"),
            LlmMessage.SystemText("note in the middle"),
            LlmMessage.User("again"),
        ]);
        Assert.Equal("""[{"text":"You are DSH."},{"text":"[Earlier conversation, compacted] summary"},{"text":"note in the middle"}]""",
            Json(body["system"]));
        Assert.Equal(3, Turns(body).Count);
        Assert.Equal(["user", "assistant", "user"], Turns(body).Select(t => t!["role"]!.GetValue<string>()));
    }

    [Fact]
    public void BlankSystemTextIsLeftOut()
    {
        var body = Body([LlmMessage.User("hi")], system: "  ");
        Assert.Null(body["system"]);
    }

    [Fact]
    public void ConsecutiveSameRoleMessagesMerge()
    {
        var body = Body([LlmMessage.User("one"), LlmMessage.User("two"), LlmMessage.Assistant("a"), LlmMessage.Assistant("b")]);
        Assert.Equal(3, Turns(body).Count);
        Assert.Equal("""[{"text":"one"},{"text":"two"}]""", Json(Content(body, 0)));
        Assert.Equal("""[{"text":"a"},{"text":"b"}]""", Json(Content(body, 1)));
        Assert.Equal("user", Role(body, 2)); // never end on an assistant turn
        Assert.Equal("""[{"text":"Continue."}]""", Json(Content(body, 2)));
    }

    [Fact]
    public void ConversationStartingWithTheAssistantGetsAUserTurnFirst()
    {
        var body = Body([LlmMessage.SystemText("summary"), LlmMessage.Assistant("where were we"), LlmMessage.User("go on")]);
        Assert.Equal("user", Role(body, 0));
        Assert.Equal("""[{"text":"(continuing the conversation)"}]""", Json(Content(body, 0)));
        Assert.Equal("assistant", Role(body, 1));
    }

    [Fact]
    public void EmptyAssistantTextIsNeverSent()
    {
        var call = new ToolCall("c1", "echo", """{"text":"x"}""");
        var body = Body([
            LlmMessage.User("go"),
            LlmMessage.Assistant("  \n", [call]),
            LlmMessage.ToolOutput("c1", "echo", "echoed: x"),
            LlmMessage.Assistant(""),
            LlmMessage.User("and?"),
        ], tools: [Echo]);
        Assert.Equal("""[{"toolUse":{"toolUseId":"c1","name":"echo","input":{"text":"x"}}}]""", Json(Content(body, 1)));
        Assert.Equal("""[{"text":"(no reply)"}]""", Json(Content(body, 3)));
    }

    // MARK: Tools

    [Fact]
    public void ToolResultsAndCapturedImagesShareOneUserTurn()
    {
        var png = TestImages.Png(4, 4);
        var body = Body([
            LlmMessage.User("look"),
            LlmMessage.Assistant("checking", [new ToolCall("c1", "screenshot", "{}"), new ToolCall("c2", "boom", "{}")]),
            LlmMessage.ToolOutput("c1", "screenshot", "Screenshot 4x4"),
            LlmMessage.ToolOutput("c2", "boom", "Error: it broke"),
            new LlmMessage(MessageRole.User, "[Automatic message: 1 image(s) captured by screenshot.]")
            {
                Attachments = [new MessageAttachment(AttachmentKind.Image, "shot.png", png)],
                ImageSource = "screenshot",
            },
        ], model: Sonnet45, tools: [Echo]);

        Assert.Equal(3, Turns(body).Count);
        var turn = Content(body, 2);
        Assert.Equal("""{"toolResult":{"toolUseId":"c1","content":[{"text":"Screenshot 4x4"}]}}""", Json(turn[0]));
        Assert.Equal("""{"toolResult":{"toolUseId":"c2","content":[{"text":"Error: it broke"}],"status":"error"}}""", Json(turn[1]));
        Assert.Equal("[Automatic message: 1 image(s) captured by screenshot.]", turn[2]!["text"]!.GetValue<string>());
        Assert.Equal("png", turn[3]!["image"]!["format"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(png), turn[3]!["image"]!["source"]!["bytes"]!.GetValue<string>());
    }

    [Fact]
    public void ToolResultStatusIsOnlySentToModelsThatKnowIt()
    {
        var messages = new[]
        {
            LlmMessage.User("go"),
            LlmMessage.Assistant("", [new ToolCall("c1", "boom", "{}")]),
            LlmMessage.ToolOutput("c1", "boom", "Permission denied: user declined."),
        };
        Assert.Equal("error", Content(Body(messages, model: Nova, tools: [Echo]), 2)[0]!["toolResult"]!["status"]!.GetValue<string>());
        Assert.Null(Content(Body(messages, model: Llama, tools: [Echo]), 2)[0]!["toolResult"]!["status"]);
    }

    [Fact]
    public void ToolCallArgumentsBecomeJsonObjects()
    {
        var body = Body([
            LlmMessage.User("go"),
            LlmMessage.Assistant("", [new ToolCall("c1", "echo", "not json"), new ToolCall("c2", "echo", "[1,2]")]),
            LlmMessage.ToolOutput("c1", "echo", "a"),
            LlmMessage.ToolOutput("c2", "echo", ""),
        ], tools: [Echo]);
        Assert.Equal("{}", Json(Content(body, 1)[0]!["toolUse"]!["input"]));
        Assert.Equal("""{"value":[1,2]}""", Json(Content(body, 1)[1]!["toolUse"]!["input"]));
        Assert.Equal("""[{"text":"(no output)"}]""", Json(Content(body, 2)[1]!["toolResult"]!["content"]));
    }

    [Fact]
    public void ToolIdsFromOtherProvidersAreMadeValid()
    {
        var id = "call abc/1:" + new string('x', 80);
        var body = Body([LlmMessage.User("go"), LlmMessage.Assistant("", [new ToolCall(id, "echo", "{}")]), LlmMessage.ToolOutput(id, "echo", "ok")],
            tools: [Echo]);
        var useId = Content(body, 1)[0]!["toolUse"]!["toolUseId"]!.GetValue<string>();
        Assert.StartsWith("call_abc_1:xxx", useId);
        Assert.Equal(64, useId.Length);
        Assert.Equal(useId, Content(body, 2)[0]!["toolResult"]!["toolUseId"]!.GetValue<string>());
    }

    [Fact]
    public void ToolSpecsCarrySchemasWithAFallback()
    {
        var body = Body([LlmMessage.User("hi")], tools: [Echo, new ToolSpec("bare", "", "not a schema")]);
        Assert.Equal(
            """[{"toolSpec":{"name":"echo","description":"Echo text back","inputSchema":{"json":{"type":"object","properties":{"text":{"type":"string"}}}}}},""" +
            """{"toolSpec":{"name":"bare","inputSchema":{"json":{"type":"object","properties":{}}}}}]""",
            Json(body["toolConfig"]!["tools"]));
    }

    [Fact]
    public void NoToolsNoToolConfig()
    {
        Assert.Null(Body([LlmMessage.User("hi")])["toolConfig"]);
    }

    [Fact]
    public void ToolHistoryKeepsAToolConfigEvenWithoutTools()
    {
        var body = Body([
            LlmMessage.User("go"),
            LlmMessage.Assistant("", [new ToolCall("c1", "read_file", "{}")]),
            LlmMessage.ToolOutput("c1", "read_file", "contents"),
            LlmMessage.Assistant("done"),
            LlmMessage.User("summarise"),
        ]);
        var tools = body["toolConfig"]!["tools"]!.AsArray();
        Assert.Equal("read_file", Assert.Single(tools)!["toolSpec"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void UnansweredCallsGetStubResultsAndOrphanResultsBecomeText()
    {
        var body = Body([
            LlmMessage.User("go"),
            LlmMessage.Assistant("", [new ToolCall("c1", "echo", "{}"), new ToolCall("c2", "echo", "{}")]),
            LlmMessage.ToolOutput("c1", "echo", "one"),
            LlmMessage.User("stop that"),
            LlmMessage.ToolOutput("zz", "grep", "late result"),
        ], tools: [Echo]);
        var turn = Content(body, 2);
        Assert.Equal("c1", turn[0]!["toolResult"]!["toolUseId"]!.GetValue<string>());
        Assert.Equal("c2", turn[1]!["toolResult"]!["toolUseId"]!.GetValue<string>());
        Assert.StartsWith("Not run", turn[1]!["toolResult"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("stop that", turn[2]!["text"]!.GetValue<string>());
        Assert.Equal("[Result of grep]\nlate result", turn[3]!["text"]!.GetValue<string>());
    }

    // MARK: Attachments

    [Fact]
    public void AttachmentsBecomeImagesDocumentsOrText()
    {
        var body = Body([LlmMessage.User("see these", [
            new MessageAttachment(AttachmentKind.Image, "photo.JPG", [1, 2, 3]),
            new MessageAttachment(AttachmentKind.File, "Q3 report (final).pdf", Encoding.ASCII.GetBytes("%PDF-1.7")),
            new MessageAttachment(AttachmentKind.File, "notes.md", Encoding.UTF8.GetBytes("﻿# Notes\nÜber")),
            new MessageAttachment(AttachmentKind.File, "blob.bin", [0, 159, 146, 150]),
            new MessageAttachment(AttachmentKind.Image, "diagram.bmp", [0x42, 0x4D, 0, 0]),
        ])], model: Sonnet45);
        var content = Content(body, 0);
        Assert.Equal("see these", content[0]!["text"]!.GetValue<string>());
        Assert.Equal("""{"image":{"format":"jpeg","source":{"bytes":"AQID"}}}""", Json(content[1]));
        Assert.Equal("""{"document":{"format":"pdf","name":"Q3 report (final)","source":{"bytes":"JVBERi0xLjc="}}}""", Json(content[2]));
        Assert.Equal("[Attached file: notes.md]\n# Notes\nÜber", content[3]!["text"]!.GetValue<string>());
        Assert.Equal("[Attached file: blob.bin (4 bytes of binary data, not shown)]", content[4]!["text"]!.GetValue<string>());
        Assert.StartsWith("[Attached file: diagram.bmp", content[5]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void PdfsAreTextNotesForModelsWithoutDocuments()
    {
        var body = Body([LlmMessage.User("read", [new MessageAttachment(AttachmentKind.File, "a.pdf", [0xFF, 0xFE, 0x00])])]);
        Assert.Contains("binary data", Content(body, 0)[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void ALoneDocumentTravelsWithText()
    {
        var body = Body([LlmMessage.User("", [new MessageAttachment(AttachmentKind.File, "a.pdf", [1])])], model: Nova);
        Assert.Equal("(see the attached document)", Content(body, 0)[1]!["text"]!.GetValue<string>());
    }

    // MARK: Caching

    [Fact]
    public void ClaudeGetsCachePointsAfterSystemToolsAndConversation()
    {
        var body = Body([LlmMessage.User("hi")], model: Sonnet45, tools: [Echo]);
        const string cache = """{"cachePoint":{"type":"default"}}""";
        Assert.Equal(cache, Json(body["system"]!.AsArray()[^1]));
        Assert.Equal(cache, Json(body["toolConfig"]!["tools"]!.AsArray()[^1]));
        Assert.Equal(cache, Json(Content(body, 0)[^1]));
    }

    [Fact]
    public void NovaCachesSystemAndConversationButNotTools()
    {
        var body = Body([LlmMessage.User("hi")], model: Nova, tools: [Echo]);
        Assert.NotNull(body["system"]!.AsArray()[^1]!["cachePoint"]);
        Assert.Null(body["toolConfig"]!["tools"]!.AsArray()[^1]!["cachePoint"]);
        Assert.NotNull(Content(body, 0)[^1]!["cachePoint"]);
    }

    [Theory]
    [InlineData(Llama)]
    [InlineData(Haiku3)]
    public void OtherModelsGetNoCachePoints(string model)
    {
        var text = Body([LlmMessage.User("hi")], model: model, tools: [Echo]).ToJsonString();
        Assert.DoesNotContain("cachePoint", text);
    }

    // MARK: Inference settings

    [Fact]
    public void MaxTokensComeFromRequestProfileOrTheModel()
    {
        Assert.Equal(2048, Body([LlmMessage.User("hi")], model: Llama)["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
        Assert.Equal(16_000, Body([LlmMessage.User("hi")], model: Sonnet45)["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
        Assert.Equal(1234, Body([LlmMessage.User("hi")], model: Sonnet45, maxTokens: 1234)["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
        var profile = Profile(Haiku3);
        profile.MaxOutputTokens = 10_000;
        // Above the model's ceiling: clamped rather than refused.
        Assert.Equal(4096, Body([LlmMessage.User("hi")], model: Haiku3, profile: profile)["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
    }

    [Fact]
    public void TemperatureIsSentWhereSamplingIsAllowed()
    {
        Assert.Equal(0.3, Body([LlmMessage.User("hi")], model: Llama, temperature: 0.3)["inferenceConfig"]!["temperature"]!.GetValue<double>());
        Assert.Null(Body([LlmMessage.User("hi")], model: Opus47, temperature: 0.3)["inferenceConfig"]!["temperature"]);
    }

    // MARK: Thinking

    [Fact]
    public void BudgetThinkingForClaude45()
    {
        var body = Body([LlmMessage.User("hi")], model: Sonnet45, thinking: ThinkingLevel.High, temperature: 0.2);
        Assert.Equal("""{"thinking":{"type":"enabled","budget_tokens":16000}}""", Json(body["additionalModelRequestFields"]));
        Assert.Equal(16_000 + BedrockConverse.ReplyTokens, body["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
        Assert.Null(body["inferenceConfig"]!["temperature"]); // Claude refuses temperature with thinking
        Assert.Null(body["outputConfig"]);
    }

    [Fact]
    public void ThinkingOffSendsNothingAndAllowsTemperature()
    {
        var body = Body([LlmMessage.User("hi")], model: Sonnet45, thinking: ThinkingLevel.Off, temperature: 0.2);
        Assert.Null(body["additionalModelRequestFields"]);
        Assert.Equal(0.2, body["inferenceConfig"]!["temperature"]!.GetValue<double>());
    }

    [Fact]
    public void TheProfileLevelAppliesWhenTheRequestHasNone()
    {
        var profile = Profile(Sonnet45);
        profile.Thinking = ThinkingLevel.Low;
        var body = Body([LlmMessage.User("hi")], model: Sonnet45, profile: profile);
        Assert.Equal(2048, body["additionalModelRequestFields"]!["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void BudgetStaysBelowTheModelsOutputCeiling()
    {
        var body = Body([LlmMessage.User("hi")], model: Opus41, thinking: ThinkingLevel.Max);
        Assert.Equal(32_000, body["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
        Assert.Equal(32_000 - BedrockConverse.ReplyTokens, body["additionalModelRequestFields"]!["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void NonClaudeModelsIgnoreThinking()
    {
        var body = Body([LlmMessage.User("hi")], model: Llama, thinking: ThinkingLevel.Max, temperature: 0.5);
        Assert.Null(body["additionalModelRequestFields"]);
        Assert.Null(body["outputConfig"]);
        Assert.Equal(0.5, body["inferenceConfig"]!["temperature"]!.GetValue<double>());
    }

    [Theory]
    [InlineData(Opus46, ThinkingLevel.High, """{"thinking":{"type":"adaptive"}}""", "high")]
    [InlineData(Opus47, ThinkingLevel.Max, """{"thinking":{"type":"adaptive","display":"summarized"}}""", "max")]
    [InlineData(Opus55, ThinkingLevel.Low, """{"thinking":{"type":"adaptive","display":"summarized"}}""", "low")]
    [InlineData(Opus47, ThinkingLevel.Off, null, null)]
    [InlineData(Opus5, ThinkingLevel.Off, """{"thinking":{"type":"disabled"}}""", null)]
    [InlineData(Sonnet55, ThinkingLevel.Off, """{"thinking":{"type":"between_tools"}}""", null)]
    [InlineData(Opus55, ThinkingLevel.Off, null, "low")]
    public void AdaptiveClaudeThinksByEffort(string model, ThinkingLevel level, string? fields, string? effort)
    {
        var body = Body([LlmMessage.User("hi")], model: model, thinking: level);
        Assert.Equal(fields ?? "null", Json(body["additionalModelRequestFields"]));
        Assert.Equal(effort ?? "null", body["outputConfig"]?["effort"]?.GetValue<string>() ?? "null");
    }

    [Fact]
    public void AdaptiveThinkingRaisesTheOutputRoom()
    {
        var body = Body([LlmMessage.User("hi")], model: Opus47, thinking: ThinkingLevel.Max);
        Assert.Equal(32_000 + BedrockConverse.ReplyTokens, body["inferenceConfig"]!["maxTokens"]!.GetValue<int>());
    }

    [Fact]
    public void Claude46KeepsTemperatureWhenNotThinking()
    {
        Assert.Equal(0.7, Body([LlmMessage.User("hi")], model: Opus46, temperature: 0.7)["inferenceConfig"]!["temperature"]!.GetValue<double>());
        Assert.Null(Body([LlmMessage.User("hi")], model: Opus46, temperature: 0.7, thinking: ThinkingLevel.Low)["inferenceConfig"]!["temperature"]);
    }

    // MARK: Signed reasoning replay

    private static readonly ToolCall Call = new("tooluse_1", "echo", """{"text":"hi"}""");

    /// <summary>What ConverseReply records for a turn that thought, said a word and called a tool.</summary>
    private const string State =
        """[{"reasoningContent":{"reasoningText":{"text":"let me think","signature":"sig-1"}}},""" +
        """{"text":"Checking."},{"text":"\n\n"},""" +
        """{"toolUse":{"toolUseId":"tooluse_1","name":"echo","input":{"text":"hi"}}}]""";

    private static List<LlmMessage> ToolTurn(string? state) =>
    [
        LlmMessage.User("go"),
        LlmMessage.Assistant("Checking.\n\n", [Call]) with { ProviderState = state },
        LlmMessage.ToolOutput("tooluse_1", "echo", "echoed: hi"),
    ];

    [Fact]
    public void SignedReasoningIsReplayedInItsOriginalOrder()
    {
        var body = Body(ToolTurn(State), model: Sonnet45, tools: [Echo], thinking: ThinkingLevel.High);
        Assert.Equal(
            """[{"reasoningContent":{"reasoningText":{"text":"let me think","signature":"sig-1"}}},{"text":"Checking."},""" +
            """{"toolUse":{"toolUseId":"tooluse_1","name":"echo","input":{"text":"hi"}}}]""",
            Json(Content(body, 1)));
        Assert.NotNull(body["additionalModelRequestFields"]);
    }

    [Fact]
    public void BudgetThinkingIsLeftOffWhenTheToolTurnLostItsReasoning()
    {
        // A transcript rebuilt from text: Claude would refuse thinking without the signed block.
        var body = Body(ToolTurn(null), model: Sonnet45, tools: [Echo], thinking: ThinkingLevel.High, temperature: 0.4);
        Assert.Null(body["additionalModelRequestFields"]);
        Assert.Equal(0.4, body["inferenceConfig"]!["temperature"]!.GetValue<double>());
        Assert.Equal("""{"text":"Checking.\n\n"}""", Json(Content(body, 1)[0]));
    }

    [Fact]
    public void OnlyTheToolTurnInProgressNeedsItsReasoning()
    {
        // An older tool turn without state doesn't matter once the conversation moved on.
        var messages = ToolTurn(null);
        messages.Add(LlmMessage.Assistant("All done."));
        messages.Add(LlmMessage.User("thanks, now more"));
        var body = Body(messages, model: Sonnet45, tools: [Echo], thinking: ThinkingLevel.High);
        Assert.NotNull(body["additionalModelRequestFields"]);
    }

    [Fact]
    public void AdaptiveThinkingNeedsNoFallback()
    {
        var body = Body(ToolTurn(null), model: Opus47, tools: [Echo], thinking: ThinkingLevel.High);
        Assert.NotNull(body["additionalModelRequestFields"]);
    }

    [Fact]
    public void ReasoningIsNotReplayedWhenThinkingIsOffOrRefused()
    {
        var off = Body(ToolTurn(State), model: Sonnet45, tools: [Echo], thinking: ThinkingLevel.Off);
        Assert.DoesNotContain("reasoningContent", off.ToJsonString());
        var refused = Body(ToolTurn(State), model: Sonnet45, tools: [Echo], thinking: ThinkingLevel.High, replay: false);
        Assert.DoesNotContain("reasoningContent", refused.ToJsonString());
        Assert.Null(refused["additionalModelRequestFields"]); // and the budget fallback applies
    }

    [Fact]
    public void StateThatNoLongerMatchesTheCallsIsIgnored()
    {
        var messages = ToolTurn(State);
        messages[1] = messages[1] with { ToolCalls = [Call with { Id = "other" }] };
        messages[2] = LlmMessage.ToolOutput("other", "echo", "x");
        var body = Body(messages, model: Sonnet45, tools: [Echo], thinking: ThinkingLevel.High);
        Assert.DoesNotContain("reasoningContent", body.ToJsonString());
        Assert.Equal("other", Content(body, 1)[1]!["toolUse"]!["toolUseId"]!.GetValue<string>());
    }

    [Fact]
    public void UnreadableStateIsIgnored()
    {
        Assert.Null(BedrockConverse.ReplayBlocks(LlmMessage.Assistant("x") with { ProviderState = "{not json" }));
        Assert.Null(BedrockConverse.ReplayBlocks(LlmMessage.Assistant("x") with { ProviderState = """[{"text":"no reasoning"}]""" }));
        Assert.Null(BedrockConverse.ReplayBlocks(LlmMessage.Assistant("x") with { ProviderState = """[{"video":{}}]""" }));
    }
}
