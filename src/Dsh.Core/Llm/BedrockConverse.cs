using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Bedrock Converse: request bodies and streamed replies
//
// Converse is stricter than chat completions, and every rule below is a 400 when broken:
//   - one system list (the prompt, then any system-role notes such as compaction summaries);
//   - turns alternate user/assistant and start with a user turn, so neighbours of the same role are
//     merged and a transcript that starts mid-conversation gets a stub user turn;
//   - tool results are toolResult blocks inside a user turn, right after the assistant turn that
//     asked, answering every toolUse of it; the engine's automatic image message joins that turn;
//   - no text block may be blank;
//   - a transcript that holds tool blocks needs toolConfig even when no tools are offered.
//
// Claude's extended thinking adds one more: while it works through a turn's tool calls it must get
// back that turn's signed reasoning, verbatim. The reply records its content blocks, in order, in
// Done.ProviderState; the engine stores that on the assistant message and the next request replays
// it. A transcript rebuilt from text (compaction, a restored chat) has no state; a budget-thinking
// Claude would then reject the request, so for such a request thinking is left off (adaptive
// thinking allows a turn without reasoning, so those models need no fallback).

public static class BedrockConverse
{
    /// <summary>Output tokens kept for the answer on top of a thinking budget.</summary>
    public const int ReplyTokens = 8192;

    /// <summary>Claude's smallest thinking budget.</summary>
    public const int MinThinkingBudget = 1024;

    /// <summary>The thinking budget for a level (Claude 3.7 – 4.5), and the thinking room adaptive
    /// models get on top of their reply.</summary>
    public static int ThinkingBudget(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off => 0,
        ThinkingLevel.Low => 2048,
        ThinkingLevel.Medium => 8192,
        ThinkingLevel.High => 16_000,
        _ => 32_000,
    };

    /// <summary>outputConfig.effort for a level (adaptive Claude).</summary>
    public static string Effort(ThinkingLevel level) => level switch
    {
        ThinkingLevel.Off or ThinkingLevel.Low => "low",
        ThinkingLevel.Medium => "medium",
        ThinkingLevel.High => "high",
        _ => "max",
    };

    public static string ModelId(LlmRequest request, ProviderProfile profile) =>
        string.IsNullOrEmpty(request.Model) ? profile.Model : request.Model;

    /// <summary>What this request asks of the model's thinking. <see cref="Active"/>: the model may
    /// think (so signed reasoning is replayed and temperature is not sent); <see cref="Room"/>: output
    /// tokens the thinking may use.</summary>
    private sealed record ThinkingPlan(JsonObject? Config, string? Effort, bool Active, int Room)
    {
        public static readonly ThinkingPlan None = new(null, null, false, 0);
    }

    /// <summary>The ConverseStream request body (the model id goes in the URL). With
    /// <paramref name="replayReasoning"/> false, stored reasoning is not sent back — the retry after
    /// Claude refused it (a changed prefix invalidates the signature).</summary>
    public static JsonObject Request(LlmRequest request, ProviderProfile profile, bool replayReasoning = true)
    {
        var traits = BedrockModels.Traits(ModelId(request, profile));
        var plan = Plan(traits, request.Thinking ?? profile.Thinking, request.Messages, replayReasoning);
        var replay = replayReasoning && traits.IsClaude && plan.Active;

        var system = new JsonArray();
        void AddSystem(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) system.Add(Text(text));
        }
        AddSystem(request.SystemPrompt);
        foreach (var m in request.Messages)
        {
            if (m.Role == MessageRole.System) AddSystem(m.Content);
        }
        if (system.Count > 0 && traits.CachePoints) system.Add(CachePoint());

        var turns = Turns(request.Messages, traits, replay);
        if (traits.CachePoints) turns[^1].Content.Add(CachePoint());
        var messages = new JsonArray();
        foreach (var (role, content) in turns) messages.Add(new JsonObject { ["role"] = role, ["content"] = content });

        var body = new JsonObject { ["messages"] = messages };
        if (system.Count > 0) body["system"] = system;

        var maxTokens = Math.Max(request.MaxTokens ?? profile.MaxOutputTokens ?? traits.DefaultMaxTokens, plan.Room);
        if (traits.MaxTokensCeiling is { } ceiling) maxTokens = Math.Min(maxTokens, ceiling);
        var inference = new JsonObject { ["maxTokens"] = maxTokens };
        if (traits.Sampling && !plan.Active && (request.Temperature ?? profile.Temperature) is { } temperature)
            inference["temperature"] = temperature;
        body["inferenceConfig"] = inference;

        if (Tools(request.Tools, turns, traits) is { } tools) body["toolConfig"] = new JsonObject { ["tools"] = tools };

        if (plan.Config is { } thinking)
        {
            // budget_tokens must stay below maxTokens, which the model's ceiling may have capped.
            if (thinking["budget_tokens"] is not null)
                thinking["budget_tokens"] = Math.Max(MinThinkingBudget, Math.Min(plan.Room - ReplyTokens, maxTokens - ReplyTokens));
            body["additionalModelRequestFields"] = new JsonObject { ["thinking"] = thinking };
        }
        if (plan.Effort is { } effort) body["outputConfig"] = new JsonObject { ["effort"] = effort };
        return body;
    }

    private static ThinkingPlan Plan(BedrockModelTraits traits, ThinkingLevel? level, IReadOnlyList<LlmMessage> messages,
                                     bool replayReasoning)
    {
        switch (traits.Thinking)
        {
            case ClaudeThinking.Budget:
            {
                if (level is null or ThinkingLevel.Off) return ThinkingPlan.None;
                // The assistant turn whose tool calls this request answers must start with its signed
                // thinking; without it on hand, this request goes without thinking.
                var last = messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                if (last?.ToolCalls is { Count: > 0 } && (!replayReasoning || ReplayBlocks(last) is null)) return ThinkingPlan.None;
                var budget = ThinkingBudget(level.Value);
                return new ThinkingPlan(new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget }, null, true,
                                        budget + ReplyTokens);
            }
            case ClaudeThinking.Adaptive:
            {
                // No level: the model's own default, which is thinking for Claude 5 and later.
                if (level is null) return ThinkingPlan.None with { Active = traits.Off != ThinkingOff.Omit };
                if (level == ThinkingLevel.Off)
                {
                    return traits.Off switch
                    {
                        ThinkingOff.Disabled => new ThinkingPlan(new JsonObject { ["type"] = "disabled" }, null, false, 0),
                        ThinkingOff.BetweenTools => new ThinkingPlan(new JsonObject { ["type"] = "between_tools" }, null, true, 0),
                        ThinkingOff.LowEffort => new ThinkingPlan(null, Effort(ThinkingLevel.Low), true, 0),
                        _ => ThinkingPlan.None,
                    };
                }
                var config = new JsonObject { ["type"] = "adaptive" };
                if (traits.SummarizedThinking) config["display"] = "summarized";
                return new ThinkingPlan(config, Effort(level.Value), true, ThinkingBudget(level.Value) + ReplyTokens);
            }
            default:
                return ThinkingPlan.None;
        }
    }

    // MARK: Turns

    private sealed record Turn(string Role, JsonArray Content);

    private const string InterruptedCall = "Not run — the turn was interrupted before this call executed.";

    private static List<Turn> Turns(IReadOnlyList<LlmMessage> messages, BedrockModelTraits traits, bool replay)
    {
        var turns = new List<Turn>();
        // toolUse ids of the latest assistant turn still waiting for their results.
        var open = new List<string>();

        void Add(string role, JsonArray blocks)
        {
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                foreach (var block in blocks.ToList())
                {
                    blocks.Remove(block);
                    turns[^1].Content.Add(block);
                }
            }
            else
            {
                if (role == "assistant" && turns.Count == 0) turns.Add(new Turn("user", [Text("(continuing the conversation)")]));
                turns.Add(new Turn(role, blocks));
            }
        }

        // Calls nobody answered (an interrupted turn) get a stub result, as Converse demands one each.
        void CloseOpenCalls()
        {
            if (open.Count == 0) return;
            var stubs = new JsonArray();
            foreach (var id in open) stubs.Add(ToolResult(id, InterruptedCall, traits));
            open.Clear();
            Add("user", stubs);
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    CloseOpenCalls();
                    Add("user", UserBlocks(m, traits));
                    break;
                case MessageRole.Tool:
                {
                    var id = ToolUseId(m.ToolCallId ?? "");
                    if (open.Remove(id))
                    {
                        Add("user", [ToolResult(id, m.Content, traits)]);
                    }
                    else
                    {
                        // A result whose call is gone (trimmed history): keep what it said, as text.
                        Add("user", [Text($"[Result of {m.Name ?? "a tool"}]\n{(string.IsNullOrWhiteSpace(m.Content) ? "(no output)" : m.Content)}")]);
                    }
                    break;
                }
                case MessageRole.Assistant:
                {
                    CloseOpenCalls();
                    var blocks = AssistantBlocks(m, replay);
                    open.AddRange(blocks.OfType<JsonObject>()
                        .Select(b => b["toolUse"] is JsonObject use ? JsonArgs.String(use, "toolUseId") : null)
                        .OfType<string>());
                    Add("assistant", blocks);
                    break;
                }
            }
        }
        CloseOpenCalls();
        if (turns.Count == 0) turns.Add(new Turn("user", [Text("(continuing the conversation)")]));
        // Converse wants the model to answer a user turn, and newer Claude models refuse a prefilled
        // assistant turn.
        if (turns[^1].Role == "assistant") turns.Add(new Turn("user", [Text("Continue.")]));
        foreach (var turn in turns.Where(t => t.Role == "user"))
        {
            if (turn.Content.Count == 0) turn.Content.Add(Text("(empty message)"));
            // A document must travel with some text.
            var kinds = turn.Content.OfType<JsonObject>().SelectMany(b => b.Select(p => p.Key)).ToHashSet();
            if (kinds.Contains("document") && !kinds.Contains("text")) turn.Content.Add(Text("(see the attached document)"));
        }
        return turns;
    }

    private static JsonArray UserBlocks(LlmMessage m, BedrockModelTraits traits)
    {
        var blocks = new JsonArray();
        if (!string.IsNullOrWhiteSpace(m.Content)) blocks.Add(Text(m.Content));
        foreach (var a in m.Attachments ?? [])
        {
            if (a.Kind == AttachmentKind.Image && ImageFormat(a.Mime) is { } format)
            {
                blocks.Add(new JsonObject
                {
                    ["image"] = new JsonObject
                    {
                        ["format"] = format,
                        ["source"] = new JsonObject { ["bytes"] = Convert.ToBase64String(a.Data) },
                    },
                });
            }
            else if (a.Mime == "application/pdf" && traits.Documents)
            {
                blocks.Add(new JsonObject
                {
                    ["document"] = new JsonObject
                    {
                        ["format"] = "pdf",
                        ["name"] = DocumentName(a.Name),
                        ["source"] = new JsonObject { ["bytes"] = Convert.ToBase64String(a.Data) },
                    },
                });
            }
            else
            {
                blocks.Add(Text(FileText(a)));
            }
        }
        return blocks;
    }

    private static JsonArray AssistantBlocks(LlmMessage m, bool replay)
    {
        if (replay && ReplayBlocks(m) is { } replayed) return replayed;
        var blocks = new JsonArray();
        if (!string.IsNullOrWhiteSpace(m.Content)) blocks.Add(Text(m.Content));
        foreach (var call in m.ToolCalls ?? [])
        {
            blocks.Add(new JsonObject
            {
                ["toolUse"] = new JsonObject
                {
                    ["toolUseId"] = ToolUseId(call.Id),
                    ["name"] = call.Name,
                    ["input"] = InputObject(call.Arguments),
                },
            });
        }
        if (blocks.Count == 0) blocks.Add(Text("(no reply)"));
        return blocks;
    }

    /// <summary>The assistant turn as the model produced it, from its <see cref="LlmMessage.ProviderState"/>
    /// — reasoning, text and tool calls in their original order, which the signatures depend on. Null
    /// when there is no usable state: none, unreadable, no reasoning in it, or tool calls that no longer
    /// match the message's.</summary>
    internal static JsonArray? ReplayBlocks(LlmMessage m)
    {
        if (string.IsNullOrEmpty(m.ProviderState)) return null;
        JsonArray? stored;
        try
        {
            stored = JsonNode.Parse(m.ProviderState) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
        if (stored is null) return null;
        var blocks = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var reasoning = false;
        foreach (var node in stored)
        {
            if (node is not JsonObject block) return null;
            if (block["reasoningContent"] is JsonObject)
            {
                reasoning = true;
            }
            else if (block["toolUse"] is JsonObject use && JsonArgs.String(use, "toolUseId") is { } id)
            {
                ids.Add(id);
            }
            else if (JsonArgs.String(block, "text") is { } text)
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
            }
            else
            {
                return null;
            }
            blocks.Add(block.DeepClone());
        }
        var expected = (m.ToolCalls ?? []).Select(c => ToolUseId(c.Id)).ToHashSet(StringComparer.Ordinal);
        return reasoning && ids.SetEquals(expected) ? blocks : null;
    }

    private static JsonArray? Tools(IReadOnlyList<ToolSpec> specs, List<Turn> turns, BedrockModelTraits traits)
    {
        var offered = specs.ToList();
        if (offered.Count == 0)
        {
            // Converse refuses tool blocks without a toolConfig: describe the tools the transcript used
            // (every toolResult answers one of these toolUse blocks, orphans having become text).
            offered = turns.SelectMany(t => t.Content.OfType<JsonObject>())
                .Select(b => b["toolUse"] is JsonObject use ? JsonArgs.String(use, "name") : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(name => new ToolSpec(name, "A tool used earlier in this conversation (no longer offered).", ""))
                .ToList();
            if (offered.Count == 0) return null;
        }
        var tools = new JsonArray();
        foreach (var spec in offered)
        {
            var toolSpec = new JsonObject { ["name"] = spec.Name };
            if (!string.IsNullOrWhiteSpace(spec.Description)) toolSpec["description"] = spec.Description;
            toolSpec["inputSchema"] = new JsonObject { ["json"] = Schema(spec.Parameters) };
            tools.Add(new JsonObject { ["toolSpec"] = toolSpec });
        }
        if (traits.ToolCachePoint) tools.Add(CachePoint());
        return tools;
    }

    // MARK: Blocks

    private static JsonObject Text(string text) => new() { ["text"] = text };

    private static JsonObject CachePoint() => new() { ["cachePoint"] = new JsonObject { ["type"] = "default" } };

    private static JsonObject ToolResult(string id, string? output, BedrockModelTraits traits)
    {
        var text = string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
        var result = new JsonObject
        {
            ["toolUseId"] = id,
            ["content"] = new JsonArray(Text(text)),
        };
        if (traits.ToolResultStatus && IsError(text)) result["status"] = "error";
        return new JsonObject { ["toolResult"] = result };
    }

    /// <summary>The engine's failure wording: "Error: …" from tools, "Permission denied: …" when the
    /// user refused the call.</summary>
    internal static bool IsError(string output) =>
        output.StartsWith("Error", StringComparison.Ordinal) || output.StartsWith("Permission denied", StringComparison.Ordinal);

    /// <summary>Converse tool-use ids allow [a-zA-Z0-9_.:-], at most 64 characters; ids from another
    /// provider's transcript are mapped the same way for the call and its result.</summary>
    internal static string ToolUseId(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or ':' or '-' ? c : '_').ToArray();
        var clean = new string(chars);
        if (clean.Length > 64) clean = clean[..64];
        return clean.Length == 0 ? "call" : clean;
    }

    /// <summary>Tool input as a JSON object: a non-object value is wrapped, unparseable text becomes {}.</summary>
    private static JsonObject InputObject(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) switch
            {
                JsonObject obj => obj,
                null => new JsonObject(),
                var other => new JsonObject { ["value"] = other },
            };
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>A tool's parameter schema, or an empty object schema when it isn't one.</summary>
    private static JsonObject Schema(string parameters)
    {
        try
        {
            if (JsonNode.Parse(parameters) is JsonObject schema) return schema;
        }
        catch (JsonException) { }
        return new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
    }

    private static string? ImageFormat(string mime) => mime switch
    {
        "image/png" => "png",
        "image/jpeg" => "jpeg",
        "image/gif" => "gif",
        "image/webp" => "webp",
        _ => null,
    };

    /// <summary>Document names allow letters, digits, single spaces, hyphens, parentheses and square
    /// brackets.</summary>
    private static string DocumentName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var chars = stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '(' or ')' or '[' or ']' ? c : '-');
        var name = string.Join(' ', new string(chars.ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return name.Length == 0 ? "document" : name;
    }

    /// <summary>A non-image file as text: its content when it is UTF-8 text, else a note.</summary>
    private static string FileText(MessageAttachment a)
    {
        try
        {
            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(a.Data).TrimStart('﻿');
            if (!text.Contains('\0')) return $"[Attached file: {a.Name}]\n{text}";
        }
        catch (DecoderFallbackException) { }
        return $"[Attached file: {a.Name} ({a.Data.Length.ToString(CultureInfo.InvariantCulture)} bytes of binary data, not shown)]";
    }
}

// MARK: - One streamed reply

/// <summary>Accumulates a ConverseStream reply from its event frames: text and reasoning deltas are
/// passed on as they come, tool calls and the reply's content blocks are kept until the end.</summary>
internal sealed class ConverseReply(Func<string, string, LlmException> exceptionFrame)
{
    private sealed class Block
    {
        public string Kind = "text";
        public readonly StringBuilder Text = new();
        public string? ToolUseId;
        public string? Name;
        public string? Signature;
        public List<byte[]>? Redacted;
    }

    private readonly SortedDictionary<int, Block> _blocks = new();
    private bool _stopped;
    private string? _stopReason;
    private LlmUsage? _usage;

    private Block At(int index)
    {
        if (!_blocks.TryGetValue(index, out var block)) _blocks[index] = block = new Block();
        return block;
    }

    /// <summary>Take one frame; the delta to show, if it carried one. Exception frames throw.</summary>
    public LlmStreamEvent? Handle(AwsEventMessage message)
    {
        switch (message.MessageType)
        {
            case "exception":
                throw exceptionFrame(message.ExceptionType ?? "", MessageOf(message.PayloadText));
            case "error":
                throw exceptionFrame(message.Headers.GetValueOrDefault(":error-code") ?? "",
                                     message.Headers.GetValueOrDefault(":error-message") ?? message.PayloadText);
            case "event" or null:
                break;
            default:
                return null;
        }
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(message.Payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (payload is null) return null;
        var index = JsonNumbers.TryGetInt(payload["contentBlockIndex"], out var i) ? i : 0;
        switch (message.EventType)
        {
            case "contentBlockStart":
                if (payload["start"] is JsonObject start && start["toolUse"] is JsonObject use)
                {
                    var block = At(index);
                    block.Kind = "toolUse";
                    block.ToolUseId = JsonArgs.String(use, "toolUseId");
                    block.Name = JsonArgs.String(use, "name");
                }
                return null;
            case "contentBlockDelta":
            {
                if (payload["delta"] is not JsonObject delta) return null;
                if (JsonArgs.String(delta, "text") is { } text)
                {
                    At(index).Text.Append(text);
                    return text.Length > 0 ? new LlmStreamEvent.Text(text) : null;
                }
                if (delta["toolUse"] is JsonObject toolUse)
                {
                    var block = At(index);
                    block.Kind = "toolUse";
                    block.Text.Append(JsonArgs.String(toolUse, "input") ?? "");
                    return null;
                }
                if (delta["reasoningContent"] is JsonObject reasoning)
                {
                    var block = At(index);
                    block.Kind = "reasoning";
                    if (JsonArgs.String(reasoning, "signature") is { } signature) block.Signature = block.Signature + signature;
                    if (JsonArgs.String(reasoning, "redactedContent") is { } redacted)
                    {
                        try
                        {
                            (block.Redacted ??= []).Add(Convert.FromBase64String(redacted));
                        }
                        catch (FormatException) { }
                    }
                    if (JsonArgs.String(reasoning, "text") is { Length: > 0 } thought)
                    {
                        block.Text.Append(thought);
                        return new LlmStreamEvent.Reasoning(thought);
                    }
                }
                return null;
            }
            case "messageStop":
                _stopped = true;
                _stopReason = JsonArgs.String(payload, "stopReason");
                return null;
            case "metadata":
                if (payload["usage"] is JsonObject u)
                {
                    int Count(string key) => JsonNumbers.TryGetInt(u[key], out var n) ? n : 0;
                    // Cached input is still input: the gauge wants the whole prompt.
                    _usage = new LlmUsage(Count("inputTokens") + Count("cacheReadInputTokens") + Count("cacheWriteInputTokens"),
                                          Count("outputTokens"), u["cacheReadInputTokens"] is null ? null : Count("cacheReadInputTokens"));
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>The reply is over: its calls, finish reason, usage and replay state. A stream that
    /// never reached messageStop was cut off.</summary>
    public LlmStreamEvent.Done Finish()
    {
        if (!_stopped)
        {
            throw LlmException.Sse(_blocks.Count > 0
                ? "the reply was cut off before it finished"
                : "the server closed the connection without replying");
        }
        if (_stopReason == "model_context_window_exceeded")
            throw LlmException.Overflow(0, "the model ran out of context window (model_context_window_exceeded)");
        var calls = _blocks.Where(b => b.Value.Kind == "toolUse")
            .Select(b => new ToolCall(b.Value.ToolUseId is { Length: > 0 } id ? id : $"call-{b.Key}", b.Value.Name ?? "",
                                      b.Value.Text.Length == 0 ? "{}" : b.Value.Text.ToString()))
            .ToList();
        var finish = _stopReason switch
        {
            null or "end_turn" or "stop_sequence" => "stop",
            "tool_use" => "tool_calls",
            "max_tokens" => "length",
            var other => other,
        };
        return new LlmStreamEvent.Done(calls, finish, _usage) { ProviderState = State() };
    }

    /// <summary>The reply's content blocks in order, when it holds signed (or redacted) reasoning that
    /// must go back with it; null otherwise (unsigned reasoning, as from DeepSeek, is never replayed).</summary>
    private string? State()
    {
        if (!_blocks.Values.Any(b => b.Kind == "reasoning" && (b.Signature is not null || b.Redacted is not null))) return null;
        var blocks = new JsonArray();
        foreach (var block in _blocks.Values)
        {
            switch (block.Kind)
            {
                case "reasoning" when block.Redacted is { } redacted:
                    blocks.Add(new JsonObject
                    {
                        ["reasoningContent"] = new JsonObject
                        {
                            ["redactedContent"] = Convert.ToBase64String(redacted.SelectMany(r => r).ToArray()),
                        },
                    });
                    break;
                case "reasoning" when block.Signature is not null:
                    blocks.Add(new JsonObject
                    {
                        ["reasoningContent"] = new JsonObject
                        {
                            ["reasoningText"] = new JsonObject { ["text"] = block.Text.ToString(), ["signature"] = block.Signature },
                        },
                    });
                    break;
                case "toolUse":
                    blocks.Add(new JsonObject
                    {
                        ["toolUse"] = new JsonObject
                        {
                            ["toolUseId"] = block.ToolUseId ?? "",
                            ["name"] = block.Name ?? "",
                            ["input"] = JsonArgs.Object(block.Text.Length == 0 ? "{}" : block.Text.ToString()),
                        },
                    });
                    break;
                case "text" when !string.IsNullOrWhiteSpace(block.Text.ToString()):
                    blocks.Add(new JsonObject { ["text"] = block.Text.ToString() });
                    break;
            }
        }
        return blocks.ToJsonString();
    }

    /// <summary>The "message" of an exception payload ({"message": "..."}), or the payload itself.</summary>
    internal static string MessageOf(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is JsonObject obj)
                return JsonArgs.String(obj, "message") ?? JsonArgs.String(obj, "Message") ?? payload;
        }
        catch (JsonException) { }
        return payload;
    }
}
