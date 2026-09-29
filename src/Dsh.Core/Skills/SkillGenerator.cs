using System.Text;

namespace Dsh.Core;

// MARK: - Generating skills with the model
//
// "Make me a skill that …", or "turn this conversation into a skill". The model writes a SKILL.md; the
// result is normalized, linted, and saved as a DRAFT — nothing the model writes becomes active without
// approval.

public sealed record SkillGenerationRequest
{
    public SkillGenerationRequest(string goal, IReadOnlyList<LlmMessage>? conversation = null,
                                  IReadOnlyList<string>? existingNames = null, string? improving = null)
    {
        Goal = goal;
        Conversation = conversation ?? [];
        ExistingNames = existingNames ?? [];
        Improving = improving;
    }

    /// <summary>What the skill should do / when it should be used.</summary>
    public string Goal { get; init; }
    /// <summary>A conversation to distil into a skill (optional).</summary>
    public IReadOnlyList<LlmMessage> Conversation { get; init; }
    /// <summary>Names already taken, so the model picks a fresh one.</summary>
    public IReadOnlyList<string> ExistingNames { get; init; }
    /// <summary>The current text, when improving an existing skill.</summary>
    public string? Improving { get; init; }
}

/// <param name="Name">The skill's slug.</param>
/// <param name="Description">Its description.</param>
/// <param name="Text">The full, normalized SKILL.md.</param>
/// <param name="Issues">What the linter found (errors only when the second attempt still had them).</param>
public sealed record GeneratedSkill(string Name, string Description, string Text, IReadOnlyList<SkillIssue> Issues);

public static class SkillGenerator
{
    public const string SystemPrompt = """
        You write agent skills: a SKILL.md file that teaches a coding agent one repeatable job.

        Format — output ONLY the file, starting with the frontmatter, no commentary, no code fence around the whole file:

        ---
        name: lowercase-hyphen-name
        description: One or two sentences saying WHEN to use this skill, with the words a user or task would contain. This line is all the agent sees before deciding to load the skill, so make it specific.
        ---

        # Title

        Then the instructions, in Markdown.

        Rules for a good skill:
        - The description says when to use it (triggers, situations, file types), not just what it is. Under 300 characters.
        - The body is what the agent should DO: a short goal, numbered steps, exact commands and paths, expected output, and the gotchas that actually bite. Be concrete; skip anything a capable agent already knows.
        - Keep it tight: usually 30–150 lines. Put long reference material in separate files and say when to read them ("see reference/api.md when …").
        - Verification: end with how to check the work is really done.
        - Never include secrets, tokens, passwords, or personal data. When a step needs a credential, refer to it from the user's credential vault as {{vault:NAME}} (e.g. `$env:OPENAI_API_KEY = '{{vault:OPENAI_API_KEY}}'`) — the harness fills the value in when the command runs — or to an environment variable.
        - Use only tools and commands that exist; do not invent flags.
        - name: lowercase letters, digits and single hyphens, at most 64 characters.
        """;

    // MARK: Prompt

    internal static string UserPrompt(SkillGenerationRequest request)
    {
        var output = new StringBuilder($"Write a skill for this:\n\n{request.Goal.Trim()}\n");
        if (request.ExistingNames.Count > 0)
            output.Append($"\nThese skill names already exist — choose a different one: {string.Join(", ", request.ExistingNames.Take(80))}.\n");
        if (request.Improving is { } improving)
            output.Append($"\nImprove this existing skill instead of starting over; keep what works, fix what doesn't, keep the same name:\n\n<current-skill>\n{improving}\n</current-skill>\n");
        if (request.Conversation.Count > 0)
            output.Append($"\nDistil the reusable procedure from this conversation. Generalize it — drop one-off details (specific file names, this session's errors) unless they are the point:\n\n<conversation>\n{Digest(request.Conversation)}\n</conversation>\n");
        return output.ToString();
    }

    /// <summary>A compact transcript: what was asked, what the agent did, what came of it. Keeps the
    /// most recent part that fits in <paramref name="limit"/> characters.</summary>
    public static string Digest(IReadOnlyList<LlmMessage> messages, int limit = 24_000)
    {
        var lines = new List<string>();
        var used = 0;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            var m = messages[i];
            string line;
            switch (m.Role)
            {
                case MessageRole.System:
                    continue;
                case MessageRole.User:
                    if (m.ImageSource is not null) continue;
                    line = $"User: {Clip(m.Content ?? "", 1_500)}";
                    break;
                case MessageRole.Assistant:
                    var text = Clip(m.Content ?? "", 1_200);
                    foreach (var call in m.ToolCalls ?? []) text += $"\n[ran {call.Name} {Clip(call.Arguments, 240)}]";
                    line = $"Assistant: {text}";
                    break;
                default:
                    line = $"  → {Clip(m.Content ?? "", 300)}";
                    break;
            }
            used += line.Length;
            if (used > limit) break;
            lines.Add(line);
        }
        lines.Reverse();
        return string.Join("\n", lines);
    }

    internal static string Clip(string s, int n)
    {
        var t = s.Trim();
        return t.Length <= n ? t : TextUtil.Prefix(t, n) + " …";
    }

    // MARK: Reply handling

    /// <summary>Pull the skill out of a reply: drop a &lt;think&gt; block and a wrapping code fence.</summary>
    public static string Extract(string reply)
    {
        var text = Compaction.StripThinking(reply).Trim();
        // A whole-file fence: ```markdown\n---\n…\n```
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var lines = text.Split('\n').ToList();
            lines.RemoveAt(0);
            if (lines.Count > 0 && lines[^1].Trim() == "```") lines.RemoveAt(lines.Count - 1);
            text = string.Join("\n", lines).Trim();
        }
        // Chatter before the frontmatter.
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            var at = text.IndexOf("\n---\nname:", StringComparison.Ordinal);
            if (at >= 0) text = text[(at + 1)..];
        }
        return text;
    }

    // MARK: Generation

    /// <summary>Ask the model for a skill. A reply that isn't a usable SKILL.md is sent back once with
    /// the reason; a second failure throws <see cref="SkillException"/>.</summary>
    public static async Task<GeneratedSkill> GenerateAsync(ILlmClient client, string model, SkillGenerationRequest request,
                                                           ThinkingLevel thinking = ThinkingLevel.Low,
                                                           CancellationToken ct = default)
    {
        var lastError = "The model did not return a skill.";
        string? feedback = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prompt = UserPrompt(request);
            if (feedback is not null) prompt += $"\nYour previous attempt was rejected: {feedback}\nReturn only the corrected SKILL.md file.";
            var llm = new LlmRequest(SystemPrompt, [LlmMessage.User(prompt)], [], model,
                                     Temperature: null, MaxTokens: 6_000, Thinking: thinking);
            var text = new StringBuilder();
            await foreach (var ev in client.StreamAsync(llm, ct).ConfigureAwait(false))
            {
                if (ev is LlmStreamEvent.Text t) text.Append(t.Delta);
            }
            var file = Extract(text.ToString());
            try
            {
                var (slug, normalized) = SkillDrafts.Normalize(file);
                var issues = SkillLint.Check(normalized);
                if (attempt == 0 && issues.FirstOrDefault(i => i.Severity == SkillIssueSeverity.Error) is { } blocker)
                {
                    lastError = blocker.Message;
                    feedback = blocker.Message;
                    continue;
                }
                var doc = SkillDocument.Parse(normalized);
                return new GeneratedSkill(slug, doc["description"] ?? "", normalized, issues);
            }
            catch (SkillException ex)
            {
                lastError = ex.Message;
                feedback = ex.Message;
            }
        }
        throw SkillException.Invalid(lastError);
    }
}
