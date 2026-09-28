using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from CompactionTests.swift.</summary>
public sealed class CompactionTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-compact");

    public void Dispose() => _root.Dispose();

    // MARK: - Plans

    /// <summary>A transcript of user/assistant pairs; each message is ~1k estimated tokens (~4k chars).</summary>
    private static List<LlmMessage> Transcript(int turns)
    {
        var output = new List<LlmMessage>();
        for (var i = 0; i < turns; i++)
        {
            output.Add(LlmMessage.User($"Question {i} " + new string('a', 4_000)));
            output.Add(LlmMessage.Assistant($"Answer {i} " + new string('b', 4_000)));
        }
        return output;
    }

    [Fact]
    public void NoPlanUnderTrigger()
    {
        var msgs = Transcript(2); // ~4k tokens
        Assert.Null(Compaction.MakePlan(8_000, 262_144, msgs)); // well under 75% of the window
    }

    [Fact]
    public void PlanSplitsAtUserBoundary()
    {
        var msgs = Transcript(60); // 120 messages, ~120k estimated tokens
        const int limit = 100_000; // trigger ≈ 75k → over budget
        var used = TokenEstimate.Request("system", msgs);
        var plan = Compaction.MakePlan(used, limit, msgs);

        Assert.NotNull(plan);
        // Every kept tail must start with a user message…
        Assert.Equal(MessageRole.User, plan.ToKeep[0].Role);
        // …and nothing is lost or duplicated.
        Assert.Equal(msgs.Count, plan.ToSummarize.Count + plan.ToKeep.Count);
        // The split keeps a meaningful recent tail: not the entire history and not nothing.
        Assert.True(TokenEstimate.Request("", plan.ToKeep) >= Compaction.MinKeepTokens - 1);
        Assert.True(plan.ToSummarize.Count >= Compaction.MinSummarizable);
        Assert.True(plan.ToKeep.Count < msgs.Count);
        Assert.Equal(used, plan.UsedTokens);
        Assert.Equal(limit, plan.Limit);
    }

    [Fact]
    public void PlanNeverOrphansToolResults()
    {
        // The cut must land before the last user message so the assistant's tool call stays paired
        // with its result in the kept tail.
        var call = new ToolCall("c1", "read_file", """{"path":"a"}""");
        List<LlmMessage> msgs =
        [
            LlmMessage.User("one" + new string('a', 8_000)),
            LlmMessage.Assistant("two" + new string('b', 8_000)),
            LlmMessage.User("three" + new string('c', 8_000)),
            LlmMessage.Assistant("four" + new string('d', 8_000)),
            LlmMessage.User("five" + new string('e', 8_000)),
            LlmMessage.Assistant("", [call]),
            LlmMessage.ToolOutput("c1", "read_file", "result"),
        ];
        var plan = Compaction.MakePlan(10_000, 2_000, msgs); // tiny window: everything is over budget

        Assert.NotNull(plan);
        // Keep must begin at a user boundary: at "five" (index 4) or later.
        Assert.Equal(MessageRole.User, plan.ToKeep[0].Role);
        var cut = msgs.FindIndex(m => ReferenceEquals(m, plan.ToKeep[0]));
        Assert.True(cut >= 4, "must not cut between the assistant tool call and its result");
        Assert.Equal(msgs.Count, plan.ToSummarize.Count + plan.ToKeep.Count);
    }

    [Fact]
    public void NoPlanWhenNothingOldEnoughToSummarize()
    {
        // Only two messages: the split would leave fewer than MinSummarizable to fold.
        Assert.Null(Compaction.MakePlan(100_000, 2_000, Transcript(1)));
    }

    [Fact]
    public void PlanHonoursLargeLimit()
    {
        // 1M window (YaRN): the trigger is ~786k, and a ~100k transcript is fine.
        var msgs = Transcript(50);
        Assert.Null(Compaction.MakePlan(TokenEstimate.Request("", msgs), 1_048_576, msgs));
    }

    // MARK: - Estimation

    [Fact]
    public void EstimateMessage()
    {
        Assert.Equal(1_000, TokenEstimate.Message(new LlmMessage(MessageRole.User, new string('x', 4_000))));
    }

    [Fact]
    public void EstimateCountsToolCallsAndAttachments()
    {
        var call = new ToolCall("c", "grep", new string('p', 400));
        var data = new byte[4_000]; // base64 ≈ 5,333 chars
        var m = new LlmMessage(MessageRole.Assistant)
        {
            ToolCalls = [call],
            Attachments = [new MessageAttachment(AttachmentKind.File, "f", data)],
        };
        Assert.True(TokenEstimate.Message(m) > (400 + 4_000 * 4 / 3) / 4);
    }

    [Fact]
    public void EstimateRequestIncludesSystemPrompt()
    {
        Assert.Equal(1_000, TokenEstimate.Request(new string('s', 4_000), [LlmMessage.User("hi")]));
    }

    // MARK: - Summary prompt

    [Fact]
    public void SummaryPromptHasStructureAndClips()
    {
        var huge = new string('z', 100_000);
        var prompt = Compaction.SummaryPrompt([LlmMessage.User(huge), LlmMessage.Assistant("short")], 2_000);
        foreach (var section in new[] { "Task:", "Decisions:", "Work done:", "Open items:", "State:" })
            Assert.Contains(section, prompt);
        // The 100k-char message must have been clipped, not included whole.
        Assert.DoesNotContain(huge, prompt);
        Assert.Contains("…[truncated]", prompt);
        Assert.Contains("<conversation>", prompt);
    }

    // MARK: - Overflow parsing (OpenAiClient helpers)

    [Fact]
    public void OverflowLimitSGLangMessage()
    {
        const string body = """
            {"error":{"message":"This model's maximum context length is 262144 tokens; however, you requested 270000 tokens (255898 in messages + 14102 in the completion). Please reduce the length of the messages or the completion.","type":"InvalidRequestError","code":"context_length_exceeded"}}
            """;
        Assert.Equal(262_144, OpenAiClient.OverflowLimit(body));
    }

    [Fact]
    public void OverflowLimitLlamaCppMessage()
    {
        Assert.Equal(131_072, OpenAiClient.OverflowLimit("The model's maximum context length is 131072 tokens"));
    }

    [Fact]
    public void OverflowLimitNone()
    {
        Assert.Null(OpenAiClient.OverflowLimit("Internal server error"));
    }

    [Fact]
    public void ContextWindowFieldVariants()
    {
        Assert.Equal(262_144, OpenAiClient.ContextWindow(new JsonObject { ["context_len"] = 262_144 }));
        Assert.Equal(1_048_576, OpenAiClient.ContextWindow(new JsonObject { ["max_model_len"] = 1_048_576 }));
        Assert.Null(OpenAiClient.ContextWindow(new JsonObject { ["max_total_tokens"] = 100_000 }));
        Assert.Equal(32_768, OpenAiClient.ContextWindow(new JsonObject { ["context"] = 32_768 }));
        Assert.Null(OpenAiClient.ContextWindow(new JsonObject { ["foo"] = 1 }));
        Assert.Null(OpenAiClient.ContextWindow(new JsonObject { ["context_len"] = 0 }));
    }

    [Fact]
    public void StrippingV1()
    {
        Assert.Equal("http://192.168.1.10:8002", OpenAiClient.StrippingV1("http://192.168.1.10:8002/v1"));
        Assert.Equal("http://192.168.1.10:8002", OpenAiClient.StrippingV1("http://192.168.1.10:8002/v1/"));
        Assert.Equal("http://192.168.1.10:8002", OpenAiClient.StrippingV1("http://192.168.1.10:8002"));
        Assert.Equal("http://h:8002/somepath", OpenAiClient.StrippingV1("http://h:8002/somepath"));
    }

    [Fact]
    public void FallbackKnowsSGLangModels()
    {
        // The native base window is what the fallback table records; the live probe corrects it.
        Assert.Equal(32_768, FallbackContextWindow.Limit("qwen3-30b-a3b"));
        Assert.Equal(131_072, FallbackContextWindow.Limit("glm-4.5"));
        Assert.Equal(131_072, FallbackContextWindow.Limit("GLM-4.5-Air"));
        // Unknown model → null (the default is applied by the caller).
        Assert.Null(FallbackContextWindow.Limit("totally-unknown-model"));
    }

    // MARK: - Engine integration (in-loop compaction)

    private static List<LlmMessage> History(int turns)
    {
        var history = new List<LlmMessage>();
        for (var i = 0; i < turns; i++)
        {
            history.Add(LlmMessage.User($"q{i} " + new string('a', 4_000)));
            history.Add(LlmMessage.Assistant($"r{i} " + new string('b', 4_000)));
        }
        return history;
    }

    /// <summary>Fold everything but the last two messages into a summary.</summary>
    private static readonly CompactionHook KeepLastTwo = (_, transcript, _) =>
        Task.FromResult<IReadOnlyList<LlmMessage>>([LlmMessage.SystemText("SUMMARY"), .. transcript.TakeLast(2)]);

    private Engine MakeEngine(ILlmClient client, EngineConfig config, CompactionHook compactor) =>
        new(client, new ToolRegistry([new EchoTool()]), "system", config, _root.Path,
            new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow)
        {
            Compactor = compactor,
        };

    /// <summary>When the request is over budget, the engine must hand the model the compacted
    /// transcript, not the full one.</summary>
    [Fact]
    public async Task InLoopCompactionShrinksRequest()
    {
        var client = new ScriptedClient(new Turn("final"));
        // trigger ≈ 6k; the request is ~20k
        var config = new EngineConfig("test") { MaxIterations = 3, ToolTimeout = TimeSpan.FromSeconds(5), ContextWindow = 8_000 };
        var result = await MakeEngine(client, config, KeepLastTwo).RunAsync(History(10), "next");

        Assert.Single(client.Requests); // compaction happens before the one model call
        var sent = client.Requests[0].Messages;
        Assert.Equal(3, sent.Count); // summary + kept tail, not the full 21
        Assert.Equal("SUMMARY", sent[0].Content);
        Assert.Equal("final", result.FinalText);
    }

    /// <summary>When the server rejects a request as too long, the engine must compact and retry once
    /// rather than surfacing the error.</summary>
    [Fact]
    public async Task OverflowTriggersCompactionAndRetry()
    {
        var client = new FlakyClient(() => LlmException.Overflow(5_000, "too long"), failures: 1,
            then: new ScriptedClient(new Turn("ok")));
        // A big window: the estimate won't trigger; the overflow will.
        var config = new EngineConfig("test") { MaxIterations = 3, ToolTimeout = TimeSpan.FromSeconds(5), ContextWindow = 100_000 };
        var result = await MakeEngine(client, config, KeepLastTwo).RunAsync(History(8), "go");

        Assert.Equal("ok", result.FinalText); // the retry after compaction must succeed
        var requests = client.Requests;
        Assert.Equal(2, requests.Count);
        Assert.True(requests[1].Messages.Count < requests[0].Messages.Count,
            "the retried request must be the compacted, smaller transcript");
    }

    /// <summary>Compaction is capped so a pathological transcript can't loop forever.</summary>
    [Fact]
    public async Task CompactionIsCapped()
    {
        // A hook that always "shrinks" but never gets below the trigger; the cap must stop the engine
        // compacting beyond MaxCompactions and let the overflow surface.
        var client = new FlakyClient(() => LlmException.Overflow(10, "no"));
        CompactionHook stillTooBig = (_, _, _) => Task.FromResult<IReadOnlyList<LlmMessage>>(
            [LlmMessage.SystemText("S"), LlmMessage.User("x " + new string('a', 4_000))]);
        var config = new EngineConfig("test")
        {
            MaxIterations = 3, ToolTimeout = TimeSpan.FromSeconds(5), ContextWindow = 10, MaxCompactions = 1,
        };
        var engine = MakeEngine(client, config, stillTooBig);

        var error = await Assert.ThrowsAsync<LlmException>(() => engine.RunAsync(History(1), "go"));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
        Assert.Equal(10, error.Limit);
    }
}

/// <summary>Ported from the CompactionForceTests class in ThinkingAndGoalTests.swift.</summary>
public sealed class CompactionForceTests
{
    private static string Big(int tokens) => new('x', tokens * 4);

    [Fact]
    public void ForcedCompactionIgnoresThreshold()
    {
        List<LlmMessage> t =
        [
            LlmMessage.User("a"), LlmMessage.Assistant(Big(500)), LlmMessage.User("b"),
            LlmMessage.Assistant(Big(500)), LlmMessage.User("c"), LlmMessage.Assistant("ok"),
        ];
        Assert.Null(Compaction.MakePlan(1_000, 1_000_000, t));
        var plan = Compaction.MakePlan(1_000, 1_000_000, t, force: true);
        Assert.NotNull(plan);
        Assert.Equal(MessageRole.User, plan.ToKeep[0].Role);
    }

    [Fact]
    public void OneLongTurnFallsBackToAssistantBoundary()
    {
        var t = new List<LlmMessage> { LlmMessage.User("do the thing") };
        for (var i = 0; i < 30; i++)
        {
            t.Add(LlmMessage.Assistant($"step {i}", [new ToolCall($"c{i}", "read_file", "{}")]));
            t.Add(LlmMessage.ToolOutput($"c{i}", "read_file", Big(2_000)));
        }
        var plan = Compaction.MakePlan(TokenEstimate.Request("", t), 64_000, t);
        Assert.NotNull(plan); // a single huge turn must still be compactable
        Assert.Equal(MessageRole.Assistant, plan.ToKeep[0].Role);
    }

    [Fact]
    public void KeepBudgetIsCappedOnHugeWindows()
    {
        var t = new List<LlmMessage>();
        for (var i = 0; i < 400; i++)
        {
            t.Add(LlmMessage.User($"q{i}"));
            t.Add(LlmMessage.Assistant(Big(2_000)));
        }
        var plan = Compaction.MakePlan(TokenEstimate.Request("", t), 1_000_000, t);
        Assert.NotNull(plan);
        Assert.True(TokenEstimate.Request("", plan.ToKeep) <= Compaction.MaxKeepTokens + 4_000);
    }

    [Fact]
    public void PreviousSummaryIsCarriedWhole()
    {
        var longText = string.Concat(Enumerable.Repeat("fact ", 2_000));
        List<LlmMessage> msgs =
        [
            LlmMessage.SystemText(Compaction.SummaryHeader + longText), LlmMessage.User("next"), LlmMessage.Assistant("ok"),
        ];
        var prompt = Compaction.SummaryPrompt(msgs, 1_000);
        Assert.Contains(longText, prompt);
        Assert.Contains("<earlier-summary>", prompt);
    }

    [Fact]
    public void StripThinking()
    {
        Assert.Equal("\n\nThe note", Compaction.StripThinking("hmm let me think</think>\n\nThe note"));
        Assert.Equal("plain", Compaction.StripThinking("plain"));
    }
}
