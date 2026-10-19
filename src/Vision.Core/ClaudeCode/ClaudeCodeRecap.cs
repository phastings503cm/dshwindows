using System.Text;

namespace Vision.Core;

// MARK: - Carrying a chat's history into a new Claude Code session

/// <summary>A chat that moves onto Claude Code part-way (or whose Claude Code session was lost, or that was imported)
/// already has a conversation Claude Code never saw. Its first Claude Code turn carries a recap of it — the most recent
/// exchanges that fit, oldest first — so the work continues instead of starting over.</summary>
public static class ClaudeCodeRecap
{
    /// <summary>Roughly 8K tokens of history.</summary>
    public const int DefaultBudget = 32_000;

    public const string Header =
        "[Earlier in this chat — before this Claude Code session. Another model (or an earlier session) answered these; " +
        "carry on from here rather than starting over.]";

    /// <summary>The recap, or null when there is nothing before the new message.</summary>
    public static string? Build(IReadOnlyList<LlmMessage> history, int budget = DefaultBudget)
    {
        var parts = new List<string>();
        var used = 0;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            var message = history[i];
            var text = (message.Content ?? "").Trim();
            if (text.Length == 0) continue;
            var who = message.Role switch
            {
                MessageRole.User => "Person",
                MessageRole.Assistant => "Assistant",
                _ => "Summary",
            };
            var room = budget - used;
            if (room < 200) break;
            if (text.Length > room - 20) text = "…" + text[^(room - 21)..];
            parts.Add($"{who}: {text}");
            used += text.Length + who.Length + 4;
        }
        if (parts.Count == 0) return null;
        parts.Reverse();
        var sb = new StringBuilder(Header).Append("\n\n");
        sb.AppendJoin("\n\n", parts);
        sb.Append("\n\n[End of the earlier conversation. The person's new message follows.]");
        return sb.ToString();
    }
}
