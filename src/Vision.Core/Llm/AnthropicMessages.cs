using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Anthropic Messages API: request bodies and streamed replies
//
// The same shaping as the Bedrock Converse path (BedrockConverse), in the Messages API's own block shapes, and from the
// same per-model table (BedrockModels.Traits reads "claude-opus-5-5" as well as "us.anthropic.claude-opus-5-5"):
//   - one top-level system list (the prompt, then any system-role notes such as compaction summaries);
//   - turns alternate user/assistant and start with a user turn: neighbours of the same role are merged, a transcript that
//     starts mid-conversation gets a stub user turn, and one that ends on the assistant gets "Continue." (Claude 4.6 and
//     later refuse a prefilled assistant turn);
//   - tool results are tool_result blocks in the user turn right after the assistant turn that asked, answering every
//     tool_use of it (an interrupted call gets a stub result); the engine's automatic image message joins that turn;
//   - a transcript that holds tool blocks declares tools even when none are offered now;
//   - thinking: adaptive + output_config.effort on Claude 4.6 and later, a budget on 3.7 – 4.5, and where thinking can't
//     be switched off (Opus 5.5, Fable) Off asks for the lowest effort; Sonnet 5.5's Off is "between_tools";
//   - a reply's signed thinking goes back verbatim with its turn (Done.ProviderState holds the reply's content blocks);
//     when Claude refuses it (the history changed under it — "preserved thinking"), the request goes again without it;
//   - prompt caching: one breakpoint at the end of the system prompt (tools and system are the stable prefix) and automatic
//     caching for the growing conversation;
//   - no temperature for Claude 4.7 and later (sampling parameters were removed), nor while thinking;
//   - Fable 5.1, Opus 5.5, Opus 5 and Sonnet 5.5 ask for Anthropic's server-side fallback (fallbacks: "default"): a request
//     their safety checks decline re-runs on the model Anthropic recommends, in the same call. A fallback block marks the
//     hand-over; the declined model's tool calls and thinking before it never ran and are dropped.
// Wire reference: platform.claude.com/docs — Messages, streaming, extended thinking, prompt caching, errors (2026-10-19).

public static class AnthropicMessages
{
    /// <summary>The anthropic-version header every request carries.</summary>
    public const string Version = "2023-06-01";

    /// <summary>The beta that turns on <c>fallbacks: "default"</c> (a declined request re-runs on the model Anthropic recommends
    /// for that refusal category, inside the same call).</summary>
    public const string FallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>The models that take <c>fallbacks: "default"</c> on Anthropic's own API: Claude Fable 5.1, Opus 5.5, Opus 5 and
    /// Sonnet 5.5.</summary>
    public static bool TakesFallbacks(string model) => BedrockModels.ClaudeVersion(model) switch
    {
        ("fable", 5.1) => true,
        ("opus", 5.5 or 5.0) => true,
        ("sonnet", 5.5) => true,
        _ => false,
    };

    /// <summary>What a turn may write when neither the request nor the route says: room to think as well as answer (streaming,
    /// so a large limit costs nothing until it is used).</summary>
    public const int AdaptiveDefaultMaxTokens = 64_000;

    /// <summary>Where Vision keeps a reply's content blocks on the message (<see cref="LlmMessage.ProviderState"/>), told apart
    /// from the Bedrock client's state and the chat-completions reasoning echo.</summary>
    public const string StatePrefix = "anthropic.content:";

    public static string ModelId(LlmRequest request, ProviderProfile profile) =>
        string.IsNullOrEmpty(request.Model) ? profile.Model : request.Model;

    /// <summary>What this request asks of the model's thinking. <see cref="Active"/>: the model may think (so signed thinking
    /// is replayed and temperature is not sent); <see cref="Room"/>: output tokens the thinking may use.</summary>
    private sealed record ThinkingPlan(JsonObject? Config, string? Effort, bool Active, int Room)
    {
        public static readonly ThinkingPlan None = new(null, null, false, 0);
    }

    /// <summary>The Messages request body (streaming). With <paramref name="replayReasoning"/> false, stored thinking is not
    /// sent back — the retry after Claude refused it; with <paramref name="fallbacks"/> false, no server-side fallback is asked
    /// for — the retry after the key turned the beta down.</summary>
    public static JsonObject Request(LlmRequest request, ProviderProfile profile, bool replayReasoning = true, bool fallbacks = true)
    {
        var model = ModelId(request, profile);
        var traits = BedrockModels.Traits(model);
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

        var turns = Turns(request.Messages, replay, AttachmentRoute.Anthropic(profile with { Model = model }));
        var messages = new JsonArray();
        foreach (var (role, content) in turns) messages.Add(new JsonObject { ["role"] = role, ["content"] = content });

        var maxTokens = request.MaxTokens ?? profile.MaxOutputTokens
                        ?? (traits.Thinking == ClaudeThinking.Adaptive ? AdaptiveDefaultMaxTokens : traits.DefaultMaxTokens);
        maxTokens = Math.Max(maxTokens, plan.Room);
        if (traits.MaxTokensCeiling is { } ceiling) maxTokens = Math.Min(maxTokens, ceiling);

        var body = new JsonObject { ["model"] = model, ["max_tokens"] = maxTokens };
        var tools = Tools(request.Tools, turns);
        if (tools is not null) body["tools"] = tools;
        if (system.Count > 0) body["system"] = system;
        body["messages"] = messages;
        if (traits.CachePoints)
        {
            // The stable prefix (tools, then system) gets its own read point; automatic caching follows the conversation.
            if (system.Count > 0) ((JsonObject)system[^1]!)["cache_control"] = Ephemeral();
            else if (tools is { Count: > 0 }) ((JsonObject)tools[^1]!)["cache_control"] = Ephemeral();
            body["cache_control"] = Ephemeral();
        }
        if (traits.Sampling && !plan.Active && (request.Temperature ?? profile.Temperature) is { } temperature)
            body["temperature"] = Math.Clamp(temperature, 0, 1);
        if (plan.Config is { } thinking)
        {
            // budget_tokens must stay below max_tokens, which the model's ceiling may have capped.
            if (thinking["budget_tokens"] is not null)
                thinking["budget_tokens"] = Math.Max(BedrockConverse.MinThinkingBudget,
                    Math.Min(plan.Room - BedrockConverse.ReplyTokens, maxTokens - BedrockConverse.ReplyTokens));
            body["thinking"] = thinking;
        }
        if (plan.Effort is { } effort) body["output_config"] = new JsonObject { ["effort"] = effort };
        // Not beside Sonnet 5.5's between_tools (that thinking setting takes nothing else).
        if (fallbacks && TakesFallbacks(model) && body["thinking"]?["type"]?.GetValue<string>() != "between_tools")
            body["fallbacks"] = "default";
        body["stream"] = true;
        return body;
    }

    private static JsonObject Ephemeral() => new() { ["type"] = "ephemeral" };

    private static ThinkingPlan Plan(BedrockModelTraits traits, ThinkingLevel? level, IReadOnlyList<LlmMessage> messages, bool replayReasoning)
    {
        switch (traits.Thinking)
        {
            case ClaudeThinking.Budget:
            {
                if (level is null or ThinkingLevel.Off) return ThinkingPlan.None;
                // The assistant turn whose tool calls this request answers must start with its signed thinking; without it
                // on hand, this request goes without thinking.
                var last = messages.LastOrDefault(m => m.Role == MessageRole.Assistant);
                if (last?.ToolCalls is { Count: > 0 } && (!replayReasoning || ReplayBlocks(last) is null)) return ThinkingPlan.None;
                var budget = BedrockConverse.ThinkingBudget(level.Value);
                return new ThinkingPlan(new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget }, null, true,
                                        budget + BedrockConverse.ReplyTokens);
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
                        // between_tools takes no other field, and effort high or below.
                        ThinkingOff.BetweenTools => new ThinkingPlan(new JsonObject { ["type"] = "between_tools" }, null, true, 0),
                        // Thinking can't be switched off: the lowest effort, with room for the little it still thinks (a short
                        // reply asked for with Off — a hello — would otherwise be all thinking and no text).
                        ThinkingOff.LowEffort => new ThinkingPlan(null, BedrockConverse.Effort(ThinkingLevel.Low), true,
                                                                  BedrockConverse.ThinkingBudget(ThinkingLevel.Low)),
                        _ => ThinkingPlan.None,
                    };
                }
                var config = new JsonObject { ["type"] = "adaptive" };
                // From Claude 4.7 on the reasoning text is omitted unless asked for: the live reasoning pane wants the summary.
                if (traits.SummarizedThinking) config["display"] = "summarized";
                return new ThinkingPlan(config, BedrockConverse.Effort(level.Value), true,
                                        BedrockConverse.ThinkingBudget(level.Value) + BedrockConverse.ReplyTokens);
            }
            default:
                return ThinkingPlan.None;
        }
    }

    // MARK: Turns

    private sealed record Turn(string Role, JsonArray Content);

    private static List<Turn> Turns(IReadOnlyList<LlmMessage> messages, bool replay, AttachmentRoute route)
    {
        var turns = new List<Turn>();
        // tool_use ids of the latest assistant turn still waiting for their results.
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

        // Calls nobody answered (an interrupted turn) get a stub result: the API wants one for every tool_use.
        void CloseOpenCalls()
        {
            if (open.Count == 0) return;
            var stubs = new JsonArray();
            foreach (var id in open) stubs.Add(ToolResult(id, BedrockConverse.InterruptedCall));
            open.Clear();
            Add("user", stubs);
        }

        foreach (var m in messages)
        {
            switch (m.Role)
            {
                case MessageRole.User:
                    CloseOpenCalls();
                    Add("user", UserBlocks(m, route));
                    break;
                case MessageRole.Tool:
                {
                    var id = ToolUseId(m.ToolCallId ?? "");
                    if (open.Remove(id))
                    {
                        Add("user", [ToolResult(id, m.Content)]);
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
                        .Where(b => JsonArgs.String(b, "type") == "tool_use")
                        .Select(b => JsonArgs.String(b, "id"))
                        .OfType<string>());
                    Add("assistant", blocks);
                    break;
                }
            }
        }
        CloseOpenCalls();
        if (turns.Count == 0) turns.Add(new Turn("user", [Text("(continuing the conversation)")]));
        if (turns[^1].Role == "assistant") turns.Add(new Turn("user", [Text("Continue.")]));
        foreach (var turn in turns.Where(t => t.Role == "user"))
        {
            if (turn.Content.Count == 0) turn.Content.Add(Text("(empty message)"));
            // tool_result blocks come first in their user turn.
            var results = turn.Content.OfType<JsonObject>().Where(b => JsonArgs.String(b, "type") == "tool_result").ToList();
            if (results.Count > 0 && results.Count < turn.Content.Count)
            {
                var rest = turn.Content.OfType<JsonObject>().Where(b => JsonArgs.String(b, "type") != "tool_result").ToList();
                turn.Content.Clear();
                foreach (var block in results.Concat(rest)) turn.Content.Add(block);
            }
        }
        return turns;
    }

    /// <summary>The user's text, then each attachment as <see cref="AttachmentWire"/> sends it on this route: images as base64
    /// image blocks, PDFs as document blocks where the model reads them, everything else as the text Vision read out of it.</summary>
    private static JsonArray UserBlocks(LlmMessage m, AttachmentRoute route)
    {
        var blocks = new JsonArray();
        if (!string.IsNullOrWhiteSpace(m.Content)) blocks.Add(Text(m.Content));
        foreach (var part in AttachmentWire.Parts(m.Attachments ?? [], route))
        {
            switch (part)
            {
                case AttachmentWirePart.Text text:
                    if (!string.IsNullOrWhiteSpace(text.Value)) blocks.Add(Text(text.Value));
                    break;
                case AttachmentWirePart.Image image when BedrockConverse.ImageFormat(image.Mime) is not null:
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "image",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64", ["media_type"] = image.Mime, ["data"] = Convert.ToBase64String(image.Data),
                        },
                    });
                    break;
                case AttachmentWirePart.Image image:
                    blocks.Add(Text($"[image of {image.Data.Length.ToString(CultureInfo.InvariantCulture)} bytes, not shown]"));
                    break;
                case AttachmentWirePart.Document document:
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "document",
                        ["source"] = new JsonObject
                        {
                            ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(document.Data),
                        },
                        ["title"] = document.Name,
                    });
                    break;
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
                ["type"] = "tool_use",
                ["id"] = ToolUseId(call.Id),
                ["name"] = call.Name,
                ["input"] = BedrockConverse.InputObject(call.Arguments),
            });
        }
        if (blocks.Count == 0) blocks.Add(Text("(no reply)"));
        return blocks;
    }

    /// <summary>The assistant turn as Claude produced it, from its <see cref="LlmMessage.ProviderState"/> — thinking, text and
    /// tool calls in their original order, which the signatures depend on. Null when there is no usable state: none, another
    /// client's, unreadable, no thinking in it, or tool calls that no longer match the message's.</summary>
    internal static JsonArray? ReplayBlocks(LlmMessage m)
    {
        if (m.ProviderState is not { } state || !state.StartsWith(StatePrefix, StringComparison.Ordinal)) return null;
        JsonArray? stored;
        try
        {
            stored = JsonNode.Parse(state[StatePrefix.Length..]) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
        if (stored is null) return null;
        var blocks = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var thinking = false;
        foreach (var node in stored)
        {
            if (node is not JsonObject block) return null;
            switch (JsonArgs.String(block, "type"))
            {
                case "thinking" or "redacted_thinking":
                    thinking = true;
                    break;
                case "tool_use" when JsonArgs.String(block, "id") is { } id:
                    ids.Add(id);
                    break;
                case "text":
                    if (string.IsNullOrWhiteSpace(JsonArgs.String(block, "text"))) continue;
                    break;
                default:
                    return null;
            }
            blocks.Add(block.DeepClone());
        }
        var expected = (m.ToolCalls ?? []).Select(c => ToolUseId(c.Id)).ToHashSet(StringComparer.Ordinal);
        return thinking && ids.SetEquals(expected) ? blocks : null;
    }

    private static JsonArray? Tools(IReadOnlyList<ToolSpec> specs, List<Turn> turns)
    {
        var offered = specs.ToList();
        if (offered.Count == 0)
        {
            // A transcript with tool blocks must declare tools: describe the ones it used (every tool_result answers one of
            // these tool_use blocks, orphans having become text).
            offered = turns.SelectMany(t => t.Content.OfType<JsonObject>())
                .Where(b => JsonArgs.String(b, "type") == "tool_use")
                .Select(b => JsonArgs.String(b, "name"))
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Select(name => new ToolSpec(name, "A tool used earlier in this conversation (no longer offered).", ""))
                .ToList();
            if (offered.Count == 0) return null;
        }
        var tools = new JsonArray();
        foreach (var spec in offered)
        {
            var tool = new JsonObject { ["name"] = spec.Name };
            if (!string.IsNullOrWhiteSpace(spec.Description)) tool["description"] = spec.Description;
            tool["input_schema"] = BedrockConverse.Schema(spec.Parameters);
            tools.Add(tool);
        }
        return tools;
    }

    // MARK: Blocks

    private static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ToolResult(string id, string? output)
    {
        var text = string.IsNullOrWhiteSpace(output) ? "(no output)" : output;
        var result = new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = id,
            ["content"] = new JsonArray(Text(text)),
        };
        if (BedrockConverse.IsError(text)) result["is_error"] = true;
        return result;
    }

    /// <summary>Tool-use ids allow [a-zA-Z0-9_-]; ids from another provider's transcript ("call_1.2", "xml-0") are mapped the
    /// same way for the call and its result.</summary>
    internal static string ToolUseId(string id)
    {
        var chars = id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        var clean = new string(chars);
        if (clean.Length > 64) clean = clean[..64];
        return clean.Length == 0 ? "call" : clean;
    }
}

// MARK: - One streamed reply

/// <summary>Accumulates a Messages stream from its server-sent events: text and thinking deltas are passed on as they come,
/// tool calls and the reply's content blocks are kept until the end. A <c>fallback</c> block (server-side fallback) marks
/// where a declined model handed over: the tool calls and thinking before it never ran and are dropped.</summary>
internal sealed class AnthropicReply
{
    private sealed class Block
    {
        public string Type = "text";
        public readonly StringBuilder Text = new();
        public string? Id;
        public string? Name;
        public string? Signature;
        public string? Data;
        /// <summary>A tool call's input as content_block_start gave it, used when no input_json_delta follows.</summary>
        public string? StartInput;
    }

    private readonly SortedDictionary<int, Block> _blocks = new();
    private bool _stopped;
    private string? _stopReason;
    private string? _refusal;
    private int _input, _cacheRead, _cacheWrite, _output;
    private bool _sawUsage;
    private string? _refusalCategory;

    /// <summary>The model that answered (message_start; the fallback model when one took over).</summary>
    public string? Model { get; private set; }

    /// <summary>Each hand-over, for a line in the chat: the model that declined and the one that carried on.</summary>
    public List<(string From, string To)> Handovers { get; } = [];

    /// <summary>The index of the last hand-over block (-1 when none): what came before it isn't this reply's.</summary>
    private int Boundary => _blocks.Where(b => b.Value.Type == "fallback").Select(b => b.Key).DefaultIfEmpty(-1).Max();

    private Block At(int index)
    {
        if (!_blocks.TryGetValue(index, out var block)) _blocks[index] = block = new Block();
        return block;
    }

    /// <summary>Take one SSE line; the delta to show, if it carried one. An error event throws.</summary>
    public LlmStreamEvent? Handle(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return null;
        var payload = line[5..].Trim();
        if (payload.Length == 0) return null;
        JsonObject? obj;
        try
        {
            obj = JsonNode.Parse(payload) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (obj is null) return null;
        var index = JsonNumbers.TryGetInt(obj["index"], out var i) ? i : 0;
        switch (JsonArgs.String(obj, "type"))
        {
            case "message_start":
                if (obj["message"] is JsonObject message)
                {
                    Model = JsonArgs.String(message, "model");
                    if (message["usage"] is JsonObject usage) ReadUsage(usage);
                }
                return null;
            case "content_block_start":
            {
                if (obj["content_block"] is not JsonObject start) return null;
                var block = At(index);
                block.Type = JsonArgs.String(start, "type") ?? "text";
                switch (block.Type)
                {
                    case "tool_use":
                        block.Id = JsonArgs.String(start, "id");
                        block.Name = JsonArgs.String(start, "name");
                        if (start["input"] is JsonObject input && input.Count > 0) block.StartInput = input.ToJsonString();
                        return null;
                    case "redacted_thinking":
                        block.Data = JsonArgs.String(start, "data");
                        return null;
                    case "fallback":
                        Handovers.Add((start["from"] is JsonObject from ? JsonArgs.String(from, "model") ?? "the model" : "the model",
                                       start["to"] is JsonObject to ? JsonArgs.String(to, "model") ?? "another model" : "another model"));
                        return null;
                    case "thinking":
                        if (JsonArgs.String(start, "signature") is { Length: > 0 } signature) block.Signature = signature;
                        if (JsonArgs.String(start, "thinking") is { Length: > 0 } thought)
                        {
                            block.Text.Append(thought);
                            return new LlmStreamEvent.Reasoning(thought);
                        }
                        return null;
                    case "text":
                        if (JsonArgs.String(start, "text") is { Length: > 0 } text)
                        {
                            block.Text.Append(text);
                            return new LlmStreamEvent.Text(text);
                        }
                        return null;
                    default:
                        return null;
                }
            }
            case "content_block_delta":
            {
                if (obj["delta"] is not JsonObject delta) return null;
                var block = At(index);
                switch (JsonArgs.String(delta, "type"))
                {
                    case "text_delta" when JsonArgs.String(delta, "text") is { } text:
                        block.Text.Append(text);
                        return text.Length > 0 ? new LlmStreamEvent.Text(text) : null;
                    case "thinking_delta" when JsonArgs.String(delta, "thinking") is { } thought:
                        block.Text.Append(thought);
                        return thought.Length > 0 ? new LlmStreamEvent.Reasoning(thought) : null;
                    case "signature_delta" when JsonArgs.String(delta, "signature") is { } signature:
                        block.Signature = (block.Signature ?? "") + signature;
                        return null;
                    case "input_json_delta" when JsonArgs.String(delta, "partial_json") is { } json:
                        block.Text.Append(json);
                        return null;
                    default:
                        return null;
                }
            }
            case "message_delta":
                if (obj["delta"] is JsonObject md)
                {
                    _stopReason = JsonArgs.String(md, "stop_reason") ?? _stopReason;
                    if (md["stop_details"] is JsonObject details)
                    {
                        _refusal = JsonArgs.String(details, "explanation");
                        _refusalCategory = JsonArgs.String(details, "category");
                    }
                }
                if (obj["usage"] is JsonObject delta2) ReadUsage(delta2);
                return null;
            case "message_stop":
                _stopped = true;
                return null;
            case "error":
            {
                // Retried like its HTTP twin (overloaded and rate limits are waited out).
                var error = obj["error"] as JsonObject;
                var status = (error is null ? null : JsonArgs.String(error, "type")) switch
                {
                    "overloaded_error" => 529,
                    "rate_limit_error" => 429,
                    "invalid_request_error" => 400,
                    "authentication_error" => 401,
                    "permission_error" => 403,
                    "not_found_error" => 404,
                    "request_too_large" => 413,
                    _ => 500,
                };
                throw AnthropicClient.Error(status, obj.ToJsonString(), Model ?? "");
            }
            default:
                return null; // ping, and event types added later
        }
    }

    private void ReadUsage(JsonObject usage)
    {
        int? Count(string key) => JsonNumbers.TryGetInt(usage[key], out var n) ? n : null;
        if (Count("input_tokens") is { } input) _input = input;
        if (Count("cache_read_input_tokens") is { } read) _cacheRead = read;
        if (Count("cache_creation_input_tokens") is { } write) _cacheWrite = write;
        if (Count("output_tokens") is { } output) _output = output;
        _sawUsage = true;
    }

    /// <summary>The reply is over: its calls, finish reason, usage and replay state. A stream that never reached message_stop
    /// was cut off.</summary>
    public LlmStreamEvent.Done Finish()
    {
        if (!_stopped)
        {
            throw LlmException.Sse(_blocks.Count > 0
                ? "the reply was cut off before it finished"
                : "the server closed the connection without replying");
        }
        switch (_stopReason)
        {
            case "model_context_window_exceeded":
                throw LlmException.Overflow(0, "the model ran out of context window (model_context_window_exceeded)");
            case "refusal":
                throw AnthropicErrors.Refusal(_refusalCategory, _refusal, Model);
        }
        var from = Boundary + 1;
        var calls = _blocks.Where(b => b.Key >= from && b.Value.Type == "tool_use")
            .Select(b => new ToolCall(b.Value.Id is { Length: > 0 } id ? id : $"call-{b.Key}", b.Value.Name ?? "", Input(b.Value)))
            .ToList();
        var finish = _stopReason switch
        {
            null or "end_turn" or "stop_sequence" or "pause_turn" => "stop",
            "tool_use" => "tool_calls",
            "max_tokens" => "length",
            var other => other,
        };
        // Cached input is still input: the gauge wants the whole prompt.
        var usage = _sawUsage ? new LlmUsage(_input + _cacheRead + _cacheWrite, _output, _cacheRead) : null;
        return new LlmStreamEvent.Done(calls, finish, usage) { ProviderState = State(from) };
    }

    private static string Input(Block block) =>
        block.Text.Length > 0 ? block.Text.ToString() : block.StartInput ?? "{}";

    /// <summary>The reply's content blocks in order, when it holds signed (or redacted) thinking that must go back with it; null
    /// otherwise.</summary>
    private string? State(int from)
    {
        var kept = _blocks.Where(b => b.Key >= from).Select(b => b.Value).ToList();
        if (!kept.Any(b => (b.Type == "redacted_thinking" && b.Data is not null) || (b.Type == "thinking" && b.Signature is not null)))
            return null;
        var blocks = new JsonArray();
        foreach (var block in kept)
        {
            switch (block.Type)
            {
                case "redacted_thinking" when block.Data is { } data:
                    blocks.Add(new JsonObject { ["type"] = "redacted_thinking", ["data"] = data });
                    break;
                case "thinking" when block.Signature is { } signature:
                    blocks.Add(new JsonObject { ["type"] = "thinking", ["thinking"] = block.Text.ToString(), ["signature"] = signature });
                    break;
                case "tool_use":
                    blocks.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = block.Id ?? "",
                        ["name"] = block.Name ?? "",
                        ["input"] = JsonArgs.Object(Input(block)),
                    });
                    break;
                case "text" when !string.IsNullOrWhiteSpace(block.Text.ToString()):
                    blocks.Add(new JsonObject { ["type"] = "text", ["text"] = block.Text.ToString() });
                    break;
            }
        }
        return AnthropicMessages.StatePrefix + blocks.ToJsonString();
    }
}
