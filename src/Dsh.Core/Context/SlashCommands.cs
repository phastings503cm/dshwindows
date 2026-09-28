namespace Dsh.Core;

// MARK: - Slash commands
//
// Typed into the composer. Only these exact words are commands — anything else starting with "/"
// goes to the model as-is (or runs a skill of that name).

public abstract record SlashCommand
{
    /// <summary>Summarize the conversation now; optional focus for the summary.</summary>
    public sealed record Compact(string? Focus) : SlashCommand;
    /// <summary>Work on a task in a loop until the model declares it complete.</summary>
    public sealed record Goal(string Text) : SlashCommand;
    /// <summary>Show or set this chat's thinking level ("default" clears the override).</summary>
    public sealed record Think(string? Argument) : SlashCommand;
    /// <summary>Show the detected context window and usage.</summary>
    public sealed record Context : SlashCommand;
    /// <summary>Show the Spark's models, or switch the Spark to one ("/swap flash").</summary>
    public sealed record Swap(string? Argument) : SlashCommand;
    /// <summary>List skills and what is selected for this chat.</summary>
    public sealed record Skills : SlashCommand;
    /// <summary>"/skill &lt;name&gt;" toggles a skill for this chat; "/skill new &lt;what&gt;" writes one.</summary>
    public sealed record Skill(string? Argument) : SlashCommand;
    public sealed record Help : SlashCommand;

    public sealed record Info(string Usage, string Summary);

    public static IReadOnlyList<Info> Catalog { get; } =
    [
        new("/goal <task>", "Keep working until the task is done (the model says GOAL_COMPLETE)"),
        new("/compact [focus]", "Summarize the conversation now to free up context"),
        new("/think off|low|medium|high|max|default", "Set how hard the model thinks in this chat"),
        new("/context", "Show the model's context window and how much is used"),
        new("/swap [model]", "Show the Spark's models, or switch what it serves (e.g. /swap flash)"),
        new("/skills", "List skills and what is selected for this chat"),
        new("/skill <name>|new <what>", "Select/deselect a skill for this chat, or have the model write a new one"),
        new("/help", "List commands"),
    ];

    public static SlashCommand? Parse(string raw)
    {
        var text = raw.Trim();
        if (!text.StartsWith('/')) return null;
        var headLength = 0;
        while (headLength < text.Length && !char.IsWhiteSpace(text[headLength])) headLength++;
        var head = text[..headLength].ToLowerInvariant();
        var rest = text[headLength..].Trim();
        string? arg = rest.Length == 0 ? null : rest;
        return head switch
        {
            "/compact" or "/compress" or "/summarize" => new Compact(arg),
            "/goal" => new Goal(rest),
            "/think" or "/thinking" or "/effort" or "/reasoning" => new Think(arg),
            "/context" or "/ctx" => new Context(),
            "/swap" or "/model" or "/models" or "/serve" => new Swap(arg),
            "/skills" => new Skills(),
            "/skill" => new Skill(arg),
            "/help" or "/?" or "/commands" => new Help(),
            _ => null,
        };
    }
}

// MARK: - Goal mode

public abstract record GoalStatus
{
    public sealed record Working : GoalStatus;
    public sealed record Complete : GoalStatus;
    public sealed record Blocked(string Reason) : GoalStatus;
}

/// <summary>The protocol a /goal run speaks with the model: a kickoff message, a continuation
/// message each round, and two end markers the model writes on a line of their own.</summary>
public static class GoalProtocol
{
    public const string CompleteMarker = "GOAL_COMPLETE";
    public const string BlockedMarker = "GOAL_BLOCKED";
    public const int DefaultMaxRounds = 40;

    public static string Kickoff(string goal) =>
        $"GOAL: {goal}\n\n" +
        "Work on this goal autonomously until it is completely done. Use your tools; plan with `todo_write` " +
        "for multi-step work; verify results (build, run, test, re-read files) instead of assuming.\n" +
        "You will be prompted to continue after every reply, so it is fine to stop and resume in steps — " +
        "but do not stop to ask for confirmation of things you can decide or check yourself.\n\n" +
        "When — and only when — the goal is fully achieved and verified, finish your reply with a line " +
        $"containing exactly:\n{CompleteMarker}\n" +
        "If you genuinely cannot proceed without the user (missing credentials, a decision only they can make, " +
        $"or an external blocker), finish with a line:\n{BlockedMarker}: <what you need from the user>\n" +
        "Never write either marker in any other situation.";

    public static string Continuation(string goal, int round, int maxRounds, bool hitIterationLimit)
    {
        var why = hitIterationLimit
            ? "You were cut off mid-work by the per-turn step limit. Pick up exactly where you left off."
            : "You have not declared the goal complete yet.";
        return $"[Goal round {round} of {maxRounds}] {why}\n" +
               $"GOAL (unchanged): {goal}\n\n" +
               "Check what remains against the goal and continue. If everything is done, verify it one last time and " +
               $"finish with a line `{CompleteMarker}`. If you are blocked on the user, finish with " +
               $"`{BlockedMarker}: <what you need>`.";
    }

    private static readonly char[] MarkerTrim = [' ', '\t', '*', '`', '_', '#', '>', '-', '.', '!'];

    /// <summary>Read the end marker from the model's final reply. Markers only count on a line of their
    /// own near the end, so the model explaining the protocol doesn't end the loop.</summary>
    public static GoalStatus Status(string reply)
    {
        var lines = reply.Split(['\n', '\r', (char)0x2028, (char)0x2029, (char)0x0085])
            .Select(l => l.Trim(MarkerTrim))
            .Where(l => l.Length > 0)
            .ToList();
        foreach (var line in lines.TakeLast(3).Reverse())
        {
            var upper = line.ToUpperInvariant();
            if (upper == CompleteMarker) return new GoalStatus.Complete();
            if (upper.StartsWith(BlockedMarker, StringComparison.Ordinal))
            {
                var reason = line[BlockedMarker.Length..].Trim(':', ' ', '\t');
                return new GoalStatus.Blocked(reason.Length == 0 ? "The agent needs your input." : reason);
            }
        }
        return new GoalStatus.Working();
    }
}
