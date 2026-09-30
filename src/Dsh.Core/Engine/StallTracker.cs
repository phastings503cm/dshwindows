using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Stall detection
//
// A run that never stops on its own (a chat turn or /goal round that keeps going after the step
// limit instead of asking the user to say "continue") needs a way out when the model is stuck: the
// same tool call, with the same arguments, giving the same result, over and over. That is not
// progress — no amount of retrying changes the answer.
//
// The tracker remembers the last few finished calls and counts how often the latest one has come up in
// that window — not only back to back, because stuck models rarely repeat one call cleanly: they
// alternate (edit fails, read the file, edit fails ...), interleave a todo update, or run the same two
// searches every turn. At the first threshold the harness tells the model, at the second it ends the run
// and says why. A loop of four or more different calls (edit, build, edit back, build ...) cannot fit eight
// turns in that window, so a second, longer one counts more slowly. The same failure coming back from the same tool
// with different arguments each time (an edit that never matches) counts too, at higher thresholds.
//
// A file that really changed (an edit or a write that worked) is progress: a check that ran before it — a quiet
// `go build`, `tsc --noEmit`, a linter that prints nothing — can honestly give the same answer after the next edit,
// so the checks seen so far are forgotten. Writes themselves and failures are not: the same write again and again,
// or the same error again and again, is still going in circles. A script or a generator run for the first time counts as a
// change too (see ShellHeuristics): what it does is unseen, but it is not the same as the last thing tried.
//
// Waiting is different: a sleep, a long poll of a process or an agent, looks the same until it finishes, which is what
// polling is for. Those calls neither count towards the ordinary thresholds nor break up a pattern around them — but they
// have a ceiling of their own, much further out, because a wait that never ends is a hang. A wait that FAILS is not waiting.

public enum StallLevel { None, Nudge, Stop }

/// <summary>What was repeating when the tracker spoke up.</summary>
public enum StallKind
{
    /// <summary>The same call with the same result.</summary>
    Call,
    /// <summary>One tool failing with the same message, whatever the arguments.</summary>
    Failure,
    /// <summary>The same waiting or polling call, always with the same answer.</summary>
    Waiting,
}

/// <summary>Counts how often the same tool call gave the same result within the last few calls.</summary>
public sealed partial class StallTracker
{
    /// <summary>How many recent calls are remembered for the ordinary thresholds.</summary>
    public const int Window = 24;

    /// <summary>How many are remembered for the slower count that catches a longer cycle.</summary>
    public const int LongWindow = 96;

    /// <summary>How many waiting calls are remembered.</summary>
    private const int WaitWindow = 64;

    /// <summary>How many times further out than the ordinary thresholds a wait is warned about and stopped.</summary>
    private const int WaitFactor = 5;

    /// <summary>Tools that ask about something that is running: alike until it finishes. A todo list is handled apart
    /// (see <see cref="ObserveTodo"/>).</summary>
    private static readonly IReadOnlySet<string> Polls = new HashSet<string>(StringComparer.Ordinal)
    {
        "agent_status", "process_read", "process_list",
    };

    private readonly int _nudgeAt;
    private readonly int _stopAt;
    private readonly ShellKind _shell;
    private readonly List<Entry> _recent = [];
    /// <summary>The waiting calls that came back with an answer, lately.</summary>
    private readonly Queue<string> _waiting = new();
    /// <summary>The shell commands that changed files, lately: one seen again is not new progress.</summary>
    private readonly Queue<string> _recentWrites = new();
    /// <summary>The identical todo update sent again and again with nothing else in between: a changed list is fine,
    /// but the same list sent over and over is not.</summary>
    private string? _lastTodo;
    private int _todoStreak;

    private readonly record struct Entry(string Call, string? Failure, bool Wrote);

    /// <param name="nudgeAt">Identical repeats before the model is warned (0 = never).</param>
    /// <param name="stopAt">Identical repeats before the run ends (0 = never).</param>
    /// <param name="shell">The shell the commands are written for.</param>
    public StallTracker(int nudgeAt, int stopAt, ShellKind shell = ShellKind.Bash)
    {
        _nudgeAt = nudgeAt;
        _stopAt = stopAt;
        _shell = shell;
    }

    /// <summary>How many times the latest call (or its failure, see <see cref="Kind"/>) has now been seen.</summary>
    public int Repeats { get; private set; }

    /// <summary>What the count is of.</summary>
    public StallKind Kind { get; private set; }

    /// <summary>The repeat counted is one tool failing with the same message, whatever the arguments.</summary>
    public bool SameFailure => Kind == StallKind.Failure;

    /// <summary>How many of the last calls the count covers.</summary>
    public int Span { get; private set; } = Window;

    /// <summary>Record one finished tool call and say whether the run is stalling.</summary>
    public StallLevel Observe(string tool, string arguments, string output) =>
        Observe(tool, arguments, ResultKey(tool, output), Failed(tool, output), bareFailure: BareFailure(tool, output));

    /// <summary>The same, with the result already reduced to its <see cref="ResultKey(string,string)"/> — so the caller can
    /// stand one result in for another (a cached "unchanged" note for the text it points back to).
    /// <paramref name="changedFiles"/>: the call wrote or edited a file (it worked, and files changed).
    /// <paramref name="bareFailure"/>: the failure says nothing but that it failed (see <see cref="BareFailure"/>).</summary>
    public StallLevel Observe(string tool, string arguments, string resultKey, bool failed, bool changedFiles = false, bool bareFailure = false)
    {
        if (tool == "todo_write") return ObserveTodo(arguments, resultKey);
        _lastTodo = null;
        _todoStreak = 0;

        var shell = tool == "run_shell_command" ? ShellHeuristics.Read(CommandOf(arguments), _shell) : default;

        // Something changed for real: what the checks said before is no longer what they would say now.
        var wrote = !failed && (changedFiles || ((shell.Writes || shell.MightWrite) && NewShellWrite(arguments)));
        if (wrote) ForgetChecks();

        // A wait that worked looks the same as the last one until it finishes; one that failed is not waiting.
        if (!failed && (Polls.Contains(tool) || shell.Waits)) return ObserveWaiting(tool, arguments, resultKey);

        var call = Signature(tool, arguments, resultKey);
        // An error is the same error whatever was tried: "old_string not found" for ten different guesses. Unless it says
        // nothing (a silent exit code 1 is what every failing grep gives): then only the same call fails the same way.
        var failure = failed ? Signature(tool, bareFailure ? arguments : "", resultKey) : null;
        _recent.Add(new Entry(call, failure, wrote));
        if (_recent.Count > LongWindow) _recent.RemoveRange(0, _recent.Count - LongWindow);

        int calls = 0, longCalls = 0, failures = 0, longFailures = 0;
        var firstRecent = _recent.Count - Window;
        for (var i = 0; i < _recent.Count; i++)
        {
            var entry = _recent[i];
            var recent = i >= firstRecent;
            if (entry.Call == call)
            {
                longCalls++;
                if (recent) calls++;
            }
            if (failure is not null && entry.Failure == failure)
            {
                longFailures++;
                if (recent) failures++;
            }
        }

        // A repeated failure is a weaker sign than a repeated call (the arguments differ), so it gets more rope; the
        // longer window, which only exists to catch a slow cycle, gets more still.
        var best = StallLevel.None;
        Repeats = calls;
        Kind = StallKind.Call;
        Span = Window;
        Consider(Level(calls, _nudgeAt, _stopAt), calls, StallKind.Call, Window);
        Consider(Level(longCalls, _nudgeAt * 2, _stopAt * 3 / 2), longCalls, StallKind.Call, LongWindow);
        Consider(Level(failures, _nudgeAt * 3 / 2, _stopAt * 3 / 2), failures, StallKind.Failure, Window);
        Consider(Level(longFailures, _nudgeAt * 3, _stopAt * 3), longFailures, StallKind.Failure, LongWindow);
        return best;

        void Consider(StallLevel level, int count, StallKind kind, int span)
        {
            if (level <= best) return;
            best = level;
            Repeats = count;
            Kind = kind;
            Span = span;
        }
    }

    private StallLevel ObserveTodo(string arguments, string resultKey)
    {
        var key = Signature("todo_write", arguments, resultKey);
        _todoStreak = key == _lastTodo ? _todoStreak + 1 : 1;
        _lastTodo = key;
        Repeats = _todoStreak;
        Kind = StallKind.Call;
        Span = Window;
        return Level(_todoStreak, _nudgeAt, _stopAt);
    }

    private StallLevel ObserveWaiting(string tool, string arguments, string resultKey)
    {
        var key = Signature(tool, arguments, resultKey);
        _waiting.Enqueue(key);
        while (_waiting.Count > WaitWindow) _waiting.Dequeue();
        var count = _waiting.Count(k => k == key);
        Repeats = count;
        Kind = StallKind.Waiting;
        Span = WaitWindow;
        return Level(count, _nudgeAt * WaitFactor, _stopAt * WaitFactor);
    }

    /// <summary>Forget the checks seen so far (they may answer differently now); writes and failures stay.</summary>
    private void ForgetChecks()
    {
        _recent.RemoveAll(r => !(r.Wrote || r.Failure is not null));
        _waiting.Clear();
    }

    private static StallLevel Level(int count, int nudgeAt, int stopAt)
    {
        if (stopAt > 0 && count >= stopAt) return StallLevel.Stop;
        if (nudgeAt > 0 && count >= nudgeAt && (count - nudgeAt) % 2 == 0) return StallLevel.Nudge;
        return StallLevel.None;
    }

    /// <summary>A shell command that changes files (or may: it runs a script) counts as progress the first time it is seen
    /// (lately): running the very same one again is not new — `git checkout .` after every step changes something each time,
    /// but a model looping on it is looping.</summary>
    private bool NewShellWrite(string arguments)
    {
        var key = Signature("run_shell_command", arguments, "");
        var seen = _recentWrites.Contains(key);
        _recentWrites.Enqueue(key);
        while (_recentWrites.Count > Window) _recentWrites.Dequeue();
        return !seen;
    }

    /// <summary>Whether a tool's answer says the call did not do what was asked: an "Error:", a refusal, a subagent that
    /// failed, or — for the shell — a non-zero exit or a timeout (which is how a failing build or test comes back, not as an "Error:").</summary>
    internal static bool Failed(string tool, string output) =>
        output.StartsWith("Error:", StringComparison.Ordinal)
        || output.StartsWith("Permission denied:", StringComparison.Ordinal)
        || (tool == "run_shell_command"
            && (output.StartsWith("Command exited with code ", StringComparison.Ordinal)
                || output.StartsWith("Command timed out", StringComparison.Ordinal)
                || output.StartsWith("Stopped after ", StringComparison.Ordinal)))
        || (tool == AgentTool.ToolName && IsSubagentFailure(output));

    private static bool IsSubagentFailure(string output)
    {
        if (!output.StartsWith("Subagent '", StringComparison.Ordinal)) return false;
        try
        {
            return SubagentFailure.IsMatch(output);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>A shell failure that says nothing but that it failed — `Command exited with code 1.` and nothing after it, which is
    /// all a failing grep or `test -f` gives. Twelve of those for twelve different names are twelve questions, not one error.</summary>
    internal static bool BareFailure(string tool, string output)
    {
        if (tool != "run_shell_command") return false;
        if (!output.StartsWith("Command exited with code ", StringComparison.Ordinal)
            && !output.StartsWith("Command timed out", StringComparison.Ordinal))
            return false;
        var newline = output.IndexOf('\n');
        return newline < 0 || output.AsSpan(newline + 1).IsWhiteSpace();
    }

    /// <summary>The command a shell call runs (its arguments' "command" value), so a word in some other field — a
    /// description that says "watch the build" — is not taken for part of the command.</summary>
    private static string CommandOf(string arguments)
    {
        try
        {
            return JsonNode.Parse(arguments) is JsonObject obj && obj["command"] is JsonValue value && value.TryGetValue<string>(out var command)
                ? command
                : arguments;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return arguments; // (not JSON, or not text that can be: half a surrogate pair is an ArgumentException)
        }
    }

    /// <summary>The note appended to the tool result when the model is repeating itself.</summary>
    public static string NudgeText(string tool, int repeats, StallKind kind = StallKind.Call, int span = Window) => kind switch
    {
        StallKind.Failure =>
            $"\n[Harness note: `{tool}` has now failed with this same error {repeats} times, however you changed the arguments. " +
            "Something you assume is wrong. Stop guessing: read the surrounding code or the actual file, check the error for what it is telling you, " +
            "try a different approach — or, if you truly cannot make progress, say exactly what is blocking you.]",
        StallKind.Waiting =>
            $"\n[Harness note: this is the {Ordinal(repeats)} time you have made this identical `{tool}` call while waiting, and it has given the same answer every time. " +
            "Whatever you are waiting for is not happening. Find out why — its output, whether it is still running, what it is blocked on — " +
            "instead of asking again, or say exactly what is blocking you.]",
        _ =>
            $"\n[Harness note: this is the {Ordinal(repeats)} time in your last {span} steps that you have made this identical `{tool}` call and got the same result. " +
            "Repeating it will not change anything. Change your approach: read the output for what it is telling you, " +
            "try a different command or file, or — if you truly cannot make progress — say exactly what is blocking you.]",
    };

    public static string StopText(string tool, int repeats, StallKind kind = StallKind.Call, int span = Window) => kind switch
    {
        StallKind.Failure =>
            $"Stopped: `{tool}` failed with the same error {repeats} times within the agent's last {span} steps, so it was not getting anywhere. " +
            "Tell it what to try differently and it will carry on.",
        StallKind.Waiting =>
            $"Stopped: the agent waited on the same `{tool}` call {repeats} times and it never gave a different answer, so what it was waiting for is not happening. " +
            "Tell it what to try differently and it will carry on.",
        _ =>
            $"Stopped: the agent made the same `{tool}` call {repeats} times and got the same result each time, so it was not getting anywhere. " +
            "Tell it what to try differently and it will carry on.",
    };

    private static string Ordinal(int n) => n switch
    {
        1 => "1st",
        2 => "2nd",
        3 => "3rd",
        _ => n + "th",
    };

    /// <summary>The arguments re-serialised, so a model that spaces the same call differently still matches — but spacing
    /// inside a string counts ("new Foo" and "newFoo" are different searches). Text that isn't JSON is compared without
    /// its whitespace.</summary>
    internal static string Normalize(string arguments)
    {
        try
        {
            if (JsonNode.Parse(arguments) is { } node)
            {
                // The "description" a call carries says why it is being made, not what it does: the same command with a new
                // description each time is the same command.
                if (node is JsonObject obj) obj.Remove("description");
                return node.ToJsonString();
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Not JSON (a truncated call, say), or not valid text: fall through.
        }
        var compact = new StringBuilder(arguments.Length);
        foreach (var c in arguments)
        {
            if (!char.IsWhiteSpace(c)) compact.Append(c);
        }
        return compact.ToString();
    }

    /// <summary>Times in a command's output that differ run to run without the outcome differing: "Time Elapsed 00:00:01.86",
    /// "Duration: 12 ms", "in 0.12s", a timestamp.</summary>
    private static readonly Regex Timing = new(
        @"\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}(?::\d{2}(?:[.,]\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?|\b\d{1,2}:\d{2}:\d{2}(?:[.,]\d+)?\b|\b\d+(?:[.,]\d+)?\s?(?:ms|µs|us|ns|s|secs?|seconds?|mins?|minutes?)\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));

    /// <summary>A subagent's answer starts "Subagent 'its description' finished." or "… failed: why".</summary>
    private static readonly Regex SubagentHead = new(@"^Subagent '.*?' (?=finished\.|failed:)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private static readonly Regex SubagentFailure = new(@"^Subagent '.*?' failed:", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>The fingerprint of a tool's answer. A shell command's answer is compared without its timings: a build that fails
    /// the same way twice has not printed the same "Time Elapsed" twice. A subagent's is compared without the description it was
    /// given: two subagents failing for the same reason failed the same way.</summary>
    internal static string ResultKey(string tool, string output)
    {
        try
        {
            if (tool == AgentTool.ToolName) return ResultKey(SubagentHead.Replace(output, "Subagent '…' "));
            if (tool == "run_shell_command") return ResultKey(Timing.Replace(output, "⧖"));
        }
        catch (RegexMatchTimeoutException)
        {
            // Compared as it is.
        }
        return ResultKey(output);
    }

    /// <summary>A short fingerprint of a result: its length and its first and last 2,000 characters (the shell keeps the tail
    /// of long output, and the line that differs between two failing runs is usually at the end of it).</summary>
    internal static string ResultKey(string output)
    {
        var shape = output.Length > 4_000 ? output[..2_000] + "\u0002" + output[^2_000..] : output;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output.Length + "\u0001" + shape)), 0, 12);
    }

    private static string Signature(string tool, string arguments, string resultKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(tool + "\u0001" + Normalize(arguments) + "\u0001" + resultKey));
        return Convert.ToHexString(digest, 0, 12);
    }
}
