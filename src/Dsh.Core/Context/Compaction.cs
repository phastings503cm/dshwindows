using System.Text;

namespace Dsh.Core;

// MARK: - Token estimation
//
// Character-based (≈ 4 chars/token) — the standard heuristic when the server won't report prompt
// tokens. Good enough for budgeting; the exact figure arrives from usage the moment a real turn runs.

public static class TokenEstimate
{
    /// <summary>Rough token count for one message: text + tool calls + attachment payloads.</summary>
    public static int Message(LlmMessage m)
    {
        var chars = m.Content?.Length ?? 0;
        foreach (var call in m.ToolCalls ?? [])
            chars += call.Name.Length + call.Arguments.Length;
        var tokens = 0;
        foreach (var attachment in m.Attachments ?? [])
        {
            if (attachment.Kind == AttachmentKind.Image)
            {
                // A vision model pays per patch, not per byte: a 1400x900 screenshot is ~1.6K tokens
                // although its base64 is ~1M characters.
                tokens += ImageSize.Tokens(attachment.Data);
            }
            else
            {
                chars += attachment.Data.Length * 4 / 3; // base64 payload
            }
        }
        return Math.Max(0, chars / 4) + tokens;
    }

    /// <summary>Rough token count for a full request: system prompt + messages.</summary>
    public static int Request(string systemPrompt, IEnumerable<LlmMessage> messages)
    {
        var total = systemPrompt.Length / 4;
        foreach (var m in messages) total += Message(m);
        return total;
    }
}

// MARK: - Conversation compaction

/// <summary>A compaction plan: the older part of a transcript to summarize and the recent tail to
/// keep verbatim (which always starts at a message boundary, so no tool result is orphaned).</summary>
public sealed record CompactionPlan(IReadOnlyList<LlmMessage> ToSummarize, IReadOnlyList<LlmMessage> ToKeep,
                                    int UsedTokens, int Limit);

/// <summary>When a transcript grows toward the model's context limit, the older part is replaced by a
/// summary the model writes, and the most recent messages are kept verbatim.</summary>
public static class Compaction
{
    /// <summary>Compact once a request reaches this fraction of the window, so the next turn (prompt +
    /// output) still fits with headroom.</summary>
    public const double TriggerFraction = 0.75;
    /// <summary>Keep this fraction of the window of recent conversation verbatim…</summary>
    public const double KeepFraction = 0.25;
    /// <summary>…but never more than this: on a 1M window a quarter is 250K tokens, which would barely
    /// shrink anything and keep every turn slow.</summary>
    public const int MaxKeepTokens = 96_000;
    /// <summary>Even for small windows, keep at least this much recent context.</summary>
    public const int MinKeepTokens = 4_000;
    /// <summary>A manual /compact keeps only a short recent tail.</summary>
    public const double ManualKeepFraction = 0.08;
    public const int ManualMaxKeepTokens = 24_000;
    public const int ManualMinKeepTokens = 2_000;
    /// <summary>Summarizing fewer than this many messages is not worth the round trip.</summary>
    public const int MinSummarizable = 4;

    /// <summary>Prepended to a compacted transcript's summary message so the model (and a human reading
    /// it back from storage) knows what it is.</summary>
    public const string SummaryHeader = "[Earlier conversation, compacted]\n";

    /// <summary>A compaction plan when the request is over budget and the transcript can actually be
    /// split; null otherwise.
    ///
    /// The split lands on a user-message boundary: everything before it is a finished conversation,
    /// and the kept tail begins where the user spoke again. <paramref name="allowAssistantBoundary"/>
    /// also allows the cut to land on an assistant message (kept together with the tool results that
    /// follow it). A subagent's whole run is one user message then many tool round-trips, so it needs
    /// that. <paramref name="force"/> (a manual /compact) skips the trigger threshold, keeps a much
    /// shorter tail, and accepts as few as two messages to fold. When the whole budget is taken by one
    /// long turn, the cut falls back to an assistant boundary rather than giving up.</summary>
    public static CompactionPlan? MakePlan(int usedTokens, int limit, IReadOnlyList<LlmMessage> transcript,
                                           bool allowAssistantBoundary = false, bool force = false)
    {
        if (limit <= 0 || transcript.Count == 0) return null;
        if (!force && usedTokens < (int)(limit * TriggerFraction)) return null;
        var keepBudget = force
            ? Math.Min(Math.Max((int)(limit * ManualKeepFraction), ManualMinKeepTokens), ManualMaxKeepTokens)
            : Math.Min(Math.Max((int)(limit * KeepFraction), MinKeepTokens), MaxKeepTokens);
        var minimum = force ? 2 : MinSummarizable;

        CompactionPlan? Split(bool assistantToo)
        {
            bool IsBoundary(LlmMessage m) => m.Role == MessageRole.User || (assistantToo && m.Role == MessageRole.Assistant);
            var tokens = 0;
            int? boundary = null;
            for (var i = transcript.Count - 1; i >= 0; i--)
            {
                var m = transcript[i];
                tokens += TokenEstimate.Message(m);
                if (IsBoundary(m)) boundary = i;
                if (tokens >= keepBudget) break;
            }
            // A forced compaction of a short chat: keep just the last exchange.
            if (force && (boundary is null || boundary == 0))
            {
                boundary = null;
                for (var i = transcript.Count - 1; i >= 0; i--)
                {
                    if (IsBoundary(transcript[i]))
                    {
                        boundary = i;
                        break;
                    }
                }
            }
            if (boundary is not > 0) return null;
            var toSummarize = transcript.Take(boundary.Value).ToList();
            var toKeep = transcript.Skip(boundary.Value).ToList();
            // Only a summary so far and nothing new to fold: not worth a call.
            if (toSummarize.Count < minimum || toSummarize.All(IsSummary)) return null;
            return new CompactionPlan(toSummarize, toKeep, usedTokens, limit);
        }

        return Split(allowAssistantBoundary) ?? (allowAssistantBoundary ? null : Split(true));
    }

    /// <summary>Whether a message is an earlier compaction summary.</summary>
    public static bool IsSummary(LlmMessage m) =>
        m.Role == MessageRole.System && (m.Content ?? "").StartsWith(SummaryHeader, StringComparison.Ordinal);

    /// <summary>The prompt that turns <paramref name="messages"/> into a continuity summary. Each
    /// message is capped at a share of <paramref name="budgetTokens"/> (in characters) so the
    /// summarizing request itself fits inside the window.</summary>
    public static string SummaryPrompt(IReadOnlyList<LlmMessage> messages, int budgetTokens, string? focus = null)
    {
        // Earlier summaries are carried forward whole (they are already dense); everything else
        // shares what is left of the budget.
        var previous = messages.Where(IsSummary).Select(m => (m.Content ?? "")[SummaryHeader.Length..]).ToList();
        var rest = messages.Where(m => !IsSummary(m)).ToList();
        var previousChars = previous.Sum(p => p.Length);
        var perMessage = Math.Max(300, (budgetTokens * 4 - previousChars) / Math.Max(1, rest.Count));
        var body = new StringBuilder();
        foreach (var m in rest)
        {
            switch (m.Role)
            {
                case MessageRole.System:
                    body.Append("System: ").Append(Clip(m.Content ?? "", perMessage / 4)).Append('\n');
                    break;
                case MessageRole.User:
                    body.Append("User: ").Append(Clip(m.Content ?? "", perMessage)).Append('\n');
                    break;
                case MessageRole.Assistant:
                    body.Append("Assistant: ").Append(Clip(m.Content ?? "", perMessage));
                    foreach (var call in m.ToolCalls ?? [])
                        body.Append($" [tool call: {call.Name} {Clip(call.Arguments, perMessage / 3)}]");
                    body.Append('\n');
                    break;
                case MessageRole.Tool:
                    body.Append($"Tool result ({m.Name ?? "unknown"}): ").Append(Clip(m.Content ?? "", perMessage / 2)).Append('\n');
                    break;
            }
        }
        var prior = previous.Count == 0 ? "" :
            "\nThe conversation was already compacted before. Merge this earlier summary in — keep every fact from it that still matters:\n" +
            "<earlier-summary>\n" + string.Join("\n\n", previous) + "\n</earlier-summary>\n";
        var focusLine = string.IsNullOrEmpty(focus) ? "" : $"\nThe user asked this summary to focus on: {focus}\n";
        return
            "The earlier part of this agent conversation is being compacted to fit the context window. " +
            "Write a continuity note the agent can pick up from and keep working without asking the user to repeat anything. " +
            "Use at most ~800 words, with these sections:\n\n" +
            "- Task: what the user asked for and the current goal (quote the user's key requirements verbatim)\n" +
            "- Decisions: important choices, constraints, and why\n" +
            "- Work done: files created/edited (with paths), commands run, key results\n" +
            "- Open items: unfinished steps, unresolved errors, pending questions\n" +
            "- State: exactly where the conversation stands now and the very next step\n" +
            focusLine + prior + "\n" +
            "Be factual and specific (paths, commands, versions, error messages). Omit small talk. Output only the note.\n\n" +
            "<conversation>\n" + body + "</conversation>";
    }

    private static string Clip(string s, int maxChars) =>
        s.Length <= maxChars ? s : TextUtil.Suffix(s, maxChars) + " …[truncated]";

    /// <summary>Ask any client for a continuity summary of a plan's older half. The request is
    /// budgeted at a fraction of the plan's window so the summarization call itself stays well clear of
    /// the limit it exists to protect. Null on any failure (network, empty result), so the caller keeps
    /// the transcript as-is rather than losing it. Thinking is switched off: it is a writing task, and
    /// a reasoning model at max effort would otherwise spend minutes thinking before writing a word.</summary>
    public static async Task<string?> SummarizeAsync(ILlmClient client, CompactionPlan plan,
                                                     double budgetFraction = 0.4, int maxOutputTokens = 4096,
                                                     string model = "", string? focus = null,
                                                     CancellationToken cancellationToken = default)
    {
        // Big windows don't need a huge summary input: cap it so the call stays quick.
        var budget = Math.Min((int)(plan.Limit * budgetFraction), 200_000);
        var prompt = SummaryPrompt(plan.ToSummarize, budget, focus);
        var request = new LlmRequest("You write concise, factual continuity notes for a coding agent.",
            [LlmMessage.User(prompt)], [], model, null, maxOutputTokens, ThinkingLevel.Off);
        var text = new StringBuilder();
        var finished = false;
        try
        {
            await foreach (var ev in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (ev is LlmStreamEvent.Text t) text.Append(t.Delta);
                if (ev is LlmStreamEvent.Done) finished = true;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
        // Stopped mid-summary: a cut-off summary must never replace history.
        if (!finished || cancellationToken.IsCancellationRequested) return null;
        var trimmed = StripThinking(text.ToString()).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>Drop an inline &lt;think&gt;…&lt;/think&gt; block (servers without a reasoning parser
    /// leave it in the content).</summary>
    public static string StripThinking(string text)
    {
        var close = text.IndexOf("</think>", StringComparison.Ordinal);
        return close < 0 ? text : text[(close + "</think>".Length)..];
    }
}

// MARK: - Image size

/// <summary>Dimensions of an encoded image, read from its header (no decoding), and the vision-token
/// cost that implies.</summary>
public static class ImageSize
{
    /// <summary>Pixels per side of one vision patch after merging (Qwen-VL family: 28).</summary>
    public const int Patch = 28;

    public static (int Width, int Height)? Dimensions(byte[] data)
    {
        var b = data.AsSpan(0, Math.Min(data.Length, 64 * 1024));
        // PNG: 8-byte signature, IHDR width/height at 16..23 (big endian).
        if (b.Length >= 24 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
        {
            var w = b[16] << 24 | b[17] << 16 | b[18] << 8 | b[19];
            var h = b[20] << 24 | b[21] << 16 | b[22] << 8 | b[23];
            return w > 0 && h > 0 ? (w, h) : null;
        }
        // GIF: "GIF8", little-endian width/height at 6..9.
        if (b.Length >= 10 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
        {
            var w = b[6] | b[7] << 8;
            var h = b[8] | b[9] << 8;
            return w > 0 && h > 0 ? (w, h) : null;
        }
        // JPEG: walk segments to the first start-of-frame marker.
        if (b.Length >= 4 && b[0] == 0xFF && b[1] == 0xD8)
        {
            var i = 2;
            while (i + 9 < b.Length)
            {
                if (b[i] != 0xFF) { i++; continue; }
                var marker = b[i + 1];
                if (marker == 0xFF) { i++; continue; }
                if (marker == 0xD8 || marker == 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
                var length = b[i + 2] << 8 | b[i + 3];
                if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                {
                    var h = b[i + 5] << 8 | b[i + 6];
                    var w = b[i + 7] << 8 | b[i + 8];
                    return w > 0 && h > 0 ? (w, h) : null;
                }
                i += 2 + Math.Max(2, length);
            }
            return null;
        }
        // WebP: RIFF....WEBP then a VP8X / VP8L / VP8 chunk.
        if (b.Length >= 30 && b[0] == 0x52 && b[1] == 0x49 && b[8] == 0x57 && b[9] == 0x45)
        {
            var fourCC = Encoding.ASCII.GetString(b.Slice(12, 4));
            if (fourCC == "VP8X")
            {
                var w = 1 + (b[24] | b[25] << 8 | b[26] << 16);
                var h = 1 + (b[27] | b[28] << 8 | b[29] << 16);
                return (w, h);
            }
            if (fourCC == "VP8L")
            {
                var bits = (uint)(b[21] | b[22] << 8 | b[23] << 16 | b[24] << 24);
                return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
            }
            if (fourCC == "VP8 ")
            {
                var w = (b[26] | b[27] << 8) & 0x3FFF;
                var h = (b[28] | b[29] << 8) & 0x3FFF;
                return w > 0 ? (w, h) : null;
            }
        }
        return null;
    }

    /// <summary>Estimated vision tokens for an encoded image (1,500 when the header can't be read).</summary>
    public static int Tokens(byte[] data)
    {
        if (Dimensions(data) is not var (w, h)) return 1_500;
        var cost = ((w + Patch - 1) / Patch) * ((h + Patch - 1) / Patch);
        return Math.Clamp(cost, 64, 8_192);
    }
}
