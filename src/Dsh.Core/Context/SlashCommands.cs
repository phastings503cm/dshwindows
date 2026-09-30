using System.Buffers;
using System.Globalization;
using System.Text;

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
    /// <summary>Start (or report) the unattended task queue.</summary>
    public sealed record Queue : SlashCommand;
    /// <summary>"/remember the staging server is orion": save a note to long-term memory.</summary>
    public sealed record Remember(string Text) : SlashCommand;
    /// <summary>"/memory" opens the memory manager; "/memory deploy" searches it.</summary>
    public sealed record Memory(string? Query) : SlashCommand;
    /// <summary>List the kinds of subagent, the model servers they run on, and what is running.</summary>
    public sealed record Agents : SlashCommand;
    public sealed record Help : SlashCommand;

    public sealed record Info(string Usage, string Summary);

    public static IReadOnlyList<Info> Catalog { get; } =
    [
        new("/goal <task>", "Keep working, round after round, until the model signals the goal is complete — no \"continue\" needed (bare /goal resumes; /goal stop drops it)"),
        new("/compact [focus]", "Summarize the conversation now to free up context"),
        new("/think off|low|medium|high|max|default", "Set how hard the model thinks in this chat"),
        new("/context", "Show the model's context window and how much is used"),
        new("/swap [model]", "Show the Spark's models, or switch what it serves (e.g. /swap flash)"),
        new("/skills", "List skills and what is selected for this chat"),
        new("/skill <name>|new <what>", "Select/deselect a skill for this chat, or have the model write a new one"),
        new("/queue", "Start the task queue — it works queued tasks one at a time, unattended"),
        new("/remember <note>", "Save a note to long-term memory (searched automatically when it's relevant)"),
        new("/memory [search]", "Open the memory manager, or search what is remembered"),
        new("/agents", "Show the subagent types, the model servers they run on, and what is running now"),
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
            "/queue" => new Queue(),
            "/remember" or "/note" => new Remember(rest),
            "/memory" or "/memories" => new Memory(arg),
            "/agents" or "/fleet" => new Agents(),
            "/help" or "/?" or "/commands" => new Help(),
            _ => null,
        };
    }
}

// MARK: - Goal mode

public abstract record GoalStatus
{
    public sealed record Working : GoalStatus;
    /// <summary>The goal is done. <see cref="Summary"/> is what the model reported through the
    /// <c>goal_complete</c> tool (null when it wrote the text marker instead).</summary>
    public sealed record Complete : GoalStatus
    {
        public string? Summary { get; init; }
    }
    public sealed record Blocked(string Reason) : GoalStatus;
}

/// <summary>The protocol a /goal run speaks with the model: a kickoff message, a continuation
/// message each round, and two end markers the model writes on a line of their own.
///
/// There is no round cap: a goal runs until the model declares it complete (or blocked), the user
/// stops it, or it fails on an error that retrying cannot fix. Transient model outages are retried
/// inside the engine and never end a goal.</summary>
public static class GoalProtocol
{
    public const string CompleteMarker = "GOAL_COMPLETE";
    public const string BlockedMarker = "GOAL_BLOCKED";
    /// <summary>Rounds in a row that may end in a non-transient error (a malformed request, a context
    /// overflow compaction couldn't fix) before the goal gives up and reports it. Each failed round is
    /// retried after a pause.</summary>
    public const int MaxConsecutiveErrors = 5;

    public static string Kickoff(string goal) =>
        $"GOAL: {goal}\n\n" +
        "Work on this goal autonomously until it is completely done. Use your tools; plan with `todo_write` " +
        "for multi-step work; verify results (build, run, test, re-read files) instead of assuming.\n" +
        "The harness sends you back to work after every reply until you say you are finished, so it is fine to " +
        "stop and resume in steps — but never stop to ask whether you should continue, or for confirmation of " +
        "things you can decide or check yourself.\n\n" +
        "When — and only when — the goal is fully achieved and verified, call the `goal_complete` tool with a " +
        "short summary of what was done. If you cannot call tools, finish your reply with a line " +
        $"containing exactly:\n{CompleteMarker}\n" +
        "If you genuinely cannot proceed without the user (missing credentials, a decision only they can make, " +
        "or an external blocker), call the `goal_blocked` tool with exactly what you need. Without tools, " +
        $"finish with a line:\n{BlockedMarker}: <what you need from the user>\n" +
        "Never signal either in any other situation.\n" +
        "The loop only ends when you signal, so keep working until then.\n" +
        "Do not ask whether to continue; the harness continues for you.";

    /// <summary>The unattended variant (task queue): the user is not at the keyboard, so "blocked" is
    /// reserved for true external walls — anything that can be decided by reading code, running a
    /// build, or checking a state gets decided and worked through instead.</summary>
    public static string KickoffAuto(string goal) => Kickoff(goal) + UnattendedNote;

    /// <summary>Kickoff for a /goal picked back up in the same chat (a bare /goal after a stop, a
    /// block, or a failure).</summary>
    public static string Resume(string goal) => ResumePreface + Kickoff(goal);

    /// <summary>Kickoff for a goal that stopped because the model needed the user, once the user has
    /// replied: the reply is handed over and the work carries on without another <c>/goal</c>.</summary>
    public static string ResumeWithReply(string goal, string reply) =>
        "[Resuming] This goal paused because you needed the user. They have now replied:\n\n" +
        $"<user_reply>\n{reply.Trim()}\n</user_reply>\n\n" +
        "Carry on with the goal using their answer — do not start over.\n\n\n" + Kickoff(goal);

    /// <summary>Kickoff for a queue task picked back up in its own chat — after an app restart, a Stop,
    /// or a block the user has since answered. The model sees its earlier work above and continues
    /// rather than starting over.</summary>
    public static string ResumeAuto(string goal) => ResumePreface + KickoffAuto(goal);

    private const string ResumePreface =
        "[Resuming] You worked on this goal earlier in this conversation and were interrupted " +
        "(a restart, a stop, or a block the user may have answered above). Do not start over: check the " +
        "current state — files, builds, tests, and any replies from the user — and continue from where you left off.\n\n\n";

    private const string UnattendedNote =
        "\n\nThis goal is running unattended in the task queue. The user will not see this " +
        "conversation until it finishes, so never stop to ask a question you can answer " +
        "yourself: read the code, run the build, test it, and make the call. If something " +
        $"truly cannot proceed without them, finish with `{BlockedMarker}: <exactly what you " +
        "need>` and stop — it will be picked back up later.";

    public static string ContinuationAuto(string goal, int round, bool hitIterationLimit,
                                          string? error = null, bool markerMisplaced = false, bool stalled = false) =>
        Continuation(goal, round, hitIterationLimit, error, markerMisplaced, stalled) +
        $"\nReminder: unattended run — decide and move on; only `{BlockedMarker}` stops it.";

    public static string Continuation(string goal, int round, bool hitIterationLimit,
                                      string? error = null, bool markerMisplaced = false, bool stalled = false)
    {
        string why;
        if (markerMisplaced)
            why = $"Your last reply mentioned {CompleteMarker}/{BlockedMarker} but not as its final line, so it did not count. " +
                  $"If the goal is done, call the `goal_complete` tool (or reply with a short summary ending in a line that contains only {CompleteMarker}).";
        else if (stalled)
            why = "Your last round was stopped because you kept making the same call and getting the same result. " +
                  "Take a genuinely different approach this time, or say exactly what is blocking you.";
        else if (error is not null)
            why = $"The previous round was cut short by an error ({TextUtil.Prefix(error, 300)}). Check what state things are in and carry on.";
        else if (hitIterationLimit)
            why = "You were cut off mid-work by the per-turn step limit. Pick up exactly where you left off.";
        else
            why = "You have not declared the goal complete yet.";
        return $"[Goal round {round}] {why}\n" +
               $"GOAL (unchanged): {goal}\n\n" +
               "Check what remains against the goal and continue. If everything is done, verify it one last time and " +
               $"call the `goal_complete` tool (or finish with a line containing exactly `{CompleteMarker}`) — the loop keeps going until you do. " +
               $"If you are blocked on the user, call `goal_blocked` (or finish with `{BlockedMarker}: <what you need>`).";
    }

    /// <summary>Read the end marker from the model's reply. A marker counts only on a line of its own
    /// (markdown like **…**, backticks, bullets, emoji and trailing punctuation are ignored, as is a
    /// short label such as "**Status:**"), so the model explaining the protocol ("I'll write
    /// GOAL_COMPLETE when…") doesn't end the loop. Only the reply's closing lines (the protocol asks
    /// for the marker last) and its very first line (models that lead with the verdict) are read, so
    /// a recap of an earlier marker mid-reply doesn't count; the last of those wins. Code fences and
    /// &lt;think&gt; blocks are ignored.</summary>
    public static GoalStatus Status(string reply)
    {
        var lines = CandidateLines(reply);
        if (lines.Count == 0) return new GoalStatus.Working();
        var considered = lines.TakeLast(3).ToList();
        if (lines.Count > 3) considered.Insert(0, lines[0]);
        for (var i = considered.Count - 1; i >= 0; i--)
        {
            if (Marker(considered[i]) is { } status) return status;
        }
        return new GoalStatus.Working();
    }

    /// <summary>True when the reply names an end marker somewhere without it counting (buried
    /// mid-reply, inline in prose): the next round asks for it plainly.</summary>
    public static bool MentionsMarker(string reply)
    {
        var upper = StripThinking(reply).ToUpperInvariant();
        return upper.Contains(CompleteMarker, StringComparison.Ordinal) || upper.Contains(BlockedMarker, StringComparison.Ordinal);
    }

    /// <summary>Non-empty lines outside code fences and thinking, decoration trimmed.</summary>
    private static List<string> CandidateLines(string reply)
    {
        var output = new List<string>();
        var inFence = false;
        foreach (var raw in StripThinking(reply).Split(['\n', '\r', (char)0x2028, (char)0x2029, (char)0x0085]))
        {
            var trimmed = raw.Trim();
            foreach (var fence in new[] { "```", "~~~" })
            {
                // "```sh build```" opens and closes on one line.
                if (trimmed.StartsWith(fence, StringComparison.Ordinal) && CountOf(trimmed, fence) < 2) inFence = !inFence;
            }
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal) || inFence)
                continue;
            var line = TrimDecoration(trimmed.Replace("*", "").Replace("`", ""));
            if (line.Length > 0) output.Add(line);
        }
        return output;
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>Drop &lt;think&gt;…&lt;/think&gt; blocks (servers without a reasoning parser leave them
    /// inline), including an unterminated leading one.</summary>
    public static string StripThinking(string text)
    {
        var s = text;
        while (s.IndexOf("<think>", StringComparison.Ordinal) is var open and >= 0)
        {
            var close = s.IndexOf("</think>", open + 7, StringComparison.Ordinal);
            s = close >= 0 ? s.Remove(open, close + 8 - open) : s[..open];
        }
        if (s.IndexOf("</think>", StringComparison.Ordinal) is var end and >= 0) s = s[(end + 8)..];
        return s;
    }

    /// <summary>Whitespace, punctuation, symbols (emoji included), combining marks and zero-width
    /// joiners, trimmed from both ends of a line before matching. Works on runes so an emoji's
    /// surrogate pair is recognised as a symbol.</summary>
    private static string TrimDecoration(string line)
    {
        static bool Decoration(Rune r) =>
            Rune.IsWhiteSpace(r) || Rune.IsPunctuation(r) || Rune.IsSymbol(r) || r.Value is 0x200B or 0x200D
            || Rune.GetUnicodeCategory(r) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark;

        var start = 0;
        while (start < line.Length && Rune.DecodeFromUtf16(line.AsSpan(start), out var r, out var used) == OperationStatus.Done
               && Decoration(r))
            start += used;
        var end = line.Length;
        while (end > start && Rune.DecodeLastFromUtf16(line.AsSpan(start, end - start), out var r, out var used) == OperationStatus.Done
               && Decoration(r))
            end -= used;
        return line[start..end];
    }

    /// <summary>Markup that may sit between a label and the marker ("Status: __GOAL_COMPLETE").</summary>
    private static bool IsMarkup(char c) => char.IsWhiteSpace(c) || "*_`~\"'[](){}<>".Contains(c);

    private static string TrimMarkup(string s) => TrimWhere(s, IsMarkup);

    private static string TrimWhere(string s, Func<char, bool> trim)
    {
        var start = 0;
        while (start < s.Length && trim(s[start])) start++;
        var end = s.Length;
        while (end > start && trim(s[end - 1])) end--;
        return s[start..end];
    }

    private static GoalStatus? Marker(string line)
    {
        var upper = line.ToUpperInvariant();
        // "GOAL COMPLETE" / "GOAL-COMPLETE" read as the complete marker too.
        var canonical = upper.Replace("GOAL COMPLETE", CompleteMarker).Replace("GOAL-COMPLETE", CompleteMarker);
        if (canonical == CompleteMarker) return new GoalStatus.Complete();
        if (canonical.EndsWith(CompleteMarker, StringComparison.Ordinal) && IsLabel(canonical[..^CompleteMarker.Length]))
            return new GoalStatus.Complete();

        // Blocked: the exact token only, alone or followed by its reason.
        var at = upper.IndexOf(BlockedMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        var prefix = upper[..at];
        if (prefix.Length > 0 && !IsLabel(prefix)) return null;
        var offset = at + BlockedMarker.Length;
        var rest = TrimMarkup(offset <= line.Length ? line[offset..] : "");
        // "GOAL_BLOCKED on the …" is prose.
        if (rest.Length > 0 && !":-—–=".Contains(rest[0])) return null;
        var reason = TrimWhere(rest, c => ":-—–= \t".Contains(c) || IsMarkup(c));
        string[] negative = ["no", "none", "n/a", "na", "false", "nothing", "not blocked", "-"];
        if (negative.Contains(reason.ToLowerInvariant())) return null;
        return new GoalStatus.Blocked(reason.Length == 0 ? "The agent needs your input." : reason);
    }

    private static bool IsLabel(string prefix)
    {
        var t = TrimMarkup(prefix);
        if (t.Length == 0 || t[^1] is not (':' or '-' or '—' or '=')) return false;
        var words = t[..^1].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return t.Length <= 24 && words.Length <= 3;
    }
}
