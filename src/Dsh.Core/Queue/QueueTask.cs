using System.Globalization;

namespace Dsh.Core;

// MARK: - Task queue: the data
//
// A durable, ordered list of autonomous work items, each on one chat's task list. Each task is run
// exactly like a /goal, in its chat: the harness works it round after round until the model declares
// it complete (or blocked / failed).
// The records here are immutable, so a snapshot handed to the UI can never change under it while the
// runner moves on; TaskQueue swaps in new copies under its lock.

/// <summary>Where a task stands. Stored as its lower-case name ("queued", "running", …).</summary>
public enum QueueTaskStatus
{
    Queued,
    Running,
    Complete,
    Blocked,
    Failed,
    Skipped,
}

public static class QueueTaskStatuses
{
    /// <summary>Every status, in declaration order.</summary>
    public static IReadOnlyList<QueueTaskStatus> All { get; } =
    [
        QueueTaskStatus.Queued, QueueTaskStatus.Running, QueueTaskStatus.Complete,
        QueueTaskStatus.Blocked, QueueTaskStatus.Failed, QueueTaskStatus.Skipped,
    ];

    /// <summary>The stored name: "queued", "running", "complete", "blocked", "failed", "skipped".</summary>
    public static string RawValue(this QueueTaskStatus status) => status switch
    {
        QueueTaskStatus.Running => "running",
        QueueTaskStatus.Complete => "complete",
        QueueTaskStatus.Blocked => "blocked",
        QueueTaskStatus.Failed => "failed",
        QueueTaskStatus.Skipped => "skipped",
        _ => "queued",
    };

    /// <summary>The status a stored name stands for, or null for a name this build doesn't know.</summary>
    public static QueueTaskStatus? FromRaw(string? raw) => raw switch
    {
        "queued" => QueueTaskStatus.Queued,
        "running" => QueueTaskStatus.Running,
        "complete" => QueueTaskStatus.Complete,
        "blocked" => QueueTaskStatus.Blocked,
        "failed" => QueueTaskStatus.Failed,
        "skipped" => QueueTaskStatus.Skipped,
        _ => null,
    };

    /// <summary>"Queued", "Running", … (the stored name, capitalized).</summary>
    public static string Label(this QueueTaskStatus status)
    {
        var raw = status.RawValue();
        return char.ToUpperInvariant(raw[0]) + raw[1..];
    }
}

/// <summary>What a line of a task's history records. Stored as its lower-case name.</summary>
public enum QueueLogKind
{
    /// <summary>Added to the queue.</summary>
    Entered,
    /// <summary>Moved in the queue.</summary>
    Reordered,
    /// <summary>Work began.</summary>
    Started,
    /// <summary>A goal round finished.</summary>
    Round,
    Complete,
    Blocked,
    Failed,
    Skipped,
    /// <summary>Free-form (edited, archived, a model outage, …).</summary>
    Note,
}

public static class QueueLogKinds
{
    public static string RawValue(this QueueLogKind kind) => kind switch
    {
        QueueLogKind.Entered => "entered",
        QueueLogKind.Reordered => "reordered",
        QueueLogKind.Started => "started",
        QueueLogKind.Round => "round",
        QueueLogKind.Complete => "complete",
        QueueLogKind.Blocked => "blocked",
        QueueLogKind.Failed => "failed",
        QueueLogKind.Skipped => "skipped",
        _ => "note",
    };

    /// <summary>The kind a stored name stands for, or null for a name this build doesn't know.</summary>
    public static QueueLogKind? FromRaw(string? raw) => raw switch
    {
        "entered" => QueueLogKind.Entered,
        "reordered" => QueueLogKind.Reordered,
        "started" => QueueLogKind.Started,
        "round" => QueueLogKind.Round,
        "complete" => QueueLogKind.Complete,
        "blocked" => QueueLogKind.Blocked,
        "failed" => QueueLogKind.Failed,
        "skipped" => QueueLogKind.Skipped,
        "note" => QueueLogKind.Note,
        _ => null,
    };
}

/// <summary>One line of a task's history.</summary>
public sealed record QueueLogLine(DateTimeOffset At, QueueLogKind Kind, string Text)
{
    public Guid Id { get; init; } = Guid.NewGuid();
}

/// <summary>One work item. Immutable: TaskQueue replaces a task with an updated copy.</summary>
public sealed record QueueTask
{
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>Short human title ("Fix the login crash").</summary>
    public required string Title { get; init; }

    /// <summary>Full instructions; the goal text is run with them.</summary>
    public string Details { get; init; } = "";

    public QueueTaskStatus Status { get; init; } = QueueTaskStatus.Queued;
    public DateTimeOffset EnteredAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>How many goal rounds this task took.</summary>
    public int Rounds { get; init; }

    /// <summary>Token totals for the whole task. Long, not int: a week of rounds summed over a whole
    /// queue can pass two billion.</summary>
    public long PromptTokens { get; init; }
    public long CompletionTokens { get; init; }

    /// <summary>The chat this task belongs to: it is on that chat's task list and runs in that chat,
    /// after the tasks before it. Null only for tasks from before task lists were per chat (the app
    /// gives them a chat when it opens the queue).</summary>
    public string? SessionId { get; init; }

    /// <summary>True once work on the task has begun (it may since have been stopped, blocked or
    /// re-queued): running it again resumes rather than starts over.</summary>
    public bool HasStarted => Log.Any(l => l.Kind == QueueLogKind.Started);

    /// <summary>The project folder the task was queued in; it runs there even if the app has since
    /// switched to another project. Null for tasks queued without one (they run in the current
    /// project).</summary>
    public string? Cwd { get; init; }

    public IReadOnlyList<QueueLogLine> Log { get; init; } = [];

    public long TotalTokens => PromptTokens + CompletionTokens;

    /// <summary>The text a goal loop runs for this task.</summary>
    public string GoalText => Details.Length == 0 ? Title : $"{Title}\n\n{Details}";

    /// <summary>Time from start to finish — to <paramref name="now"/> (default: the system clock) while
    /// still running. Null until the task has started.</summary>
    public TimeSpan? Duration(DateTimeOffset? now = null)
    {
        if (StartedAt is not { } started) return null;
        var end = FinishedAt ?? now ?? DateTimeOffset.UtcNow;
        var d = end - started;
        return d > TimeSpan.Zero ? d : TimeSpan.Zero;
    }

    /// <summary>Average tokens/second over the task's runtime (both directions, as the model server
    /// served them). Null until the task has run for a second — a shorter span gives no real rate.</summary>
    public double? AvgTokensPerSecond(DateTimeOffset? now = null) =>
        Duration(now) is { TotalSeconds: >= 1 } d && TotalTokens > 0 ? TotalTokens / d.TotalSeconds : null;

    /// <summary>A copy with one more line in its history.</summary>
    public QueueTask AppendingLog(QueueLogKind kind, string text, DateTimeOffset at) =>
        this with { Log = [.. Log, new QueueLogLine(at, kind, text)] };

    // Value equality includes the log's lines (a record would compare the list by reference), so a
    // reloaded task equals the one that was saved and the UI can skip rows that didn't change.
    public bool Equals(QueueTask? other) =>
        other is not null
        && Id == other.Id && Title == other.Title && Details == other.Details && Status == other.Status
        && EnteredAt == other.EnteredAt && StartedAt == other.StartedAt && FinishedAt == other.FinishedAt
        && Rounds == other.Rounds && PromptTokens == other.PromptTokens && CompletionTokens == other.CompletionTokens
        && SessionId == other.SessionId && Cwd == other.Cwd
        && Log.SequenceEqual(other.Log);

    public override int GetHashCode() => HashCode.Combine(Id, Title, Status, Rounds, TotalTokens, Log.Count);
}

// MARK: - Stats

/// <summary>Counts and totals over a queue.</summary>
public readonly record struct QueueStats(
    int Total, int Completed, int Failed, int Blocked, int Running, int Queued, int Skipped,
    long PromptTokens, long CompletionTokens, TimeSpan TotalDuration)
{
    /// <summary>All tokens over the finished tasks' runtime; null before anything has finished.</summary>
    public double? TokensPerSecond =>
        TotalDuration.TotalSeconds >= 1 ? (CompletionTokens + PromptTokens) / TotalDuration.TotalSeconds : null;

    /// <summary>Nothing left to run: no queued task and none running.</summary>
    public bool Finished => Total > 0 && Queued == 0 && Running == 0;

    public static QueueStats Of(IEnumerable<QueueTask> tasks)
    {
        int total = 0, completed = 0, failed = 0, blocked = 0, running = 0, queued = 0, skipped = 0;
        long prompt = 0, completion = 0;
        var duration = TimeSpan.Zero;
        foreach (var t in tasks)
        {
            total++;
            switch (t.Status)
            {
                case QueueTaskStatus.Complete: completed++; break;
                case QueueTaskStatus.Failed: failed++; break;
                case QueueTaskStatus.Blocked: blocked++; break;
                case QueueTaskStatus.Running: running++; break;
                case QueueTaskStatus.Queued: queued++; break;
                case QueueTaskStatus.Skipped: skipped++; break;
            }
            prompt += t.PromptTokens;
            completion += t.CompletionTokens;
            if (t.FinishedAt is not null && t.Duration() is { } d) duration += d;
        }
        return new QueueStats(total, completed, failed, blocked, running, queued, skipped, prompt, completion, duration);
    }
}

// MARK: - Durations

public static class DurationFormatting
{
    /// <summary>162 s → "2m 42s"; 3600 s → "1h"; 3723 s → "1h 2m".</summary>
    public static string FormattedDuration(this TimeSpan duration) => FormattedDuration(duration.TotalSeconds);

    /// <summary>A duration given in seconds, formatted as for <see cref="FormattedDuration(TimeSpan)"/>.</summary>
    public static string FormattedDuration(this double seconds)
    {
        // Swift's rounded(): halves away from zero.
        var secs = (long)Math.Round(seconds, MidpointRounding.AwayFromZero);
        if (secs < 60) return secs.ToString(CultureInfo.InvariantCulture) + "s";
        long m = secs / 60, s = secs % 60;
        if (m < 60) return s == 0 ? $"{m}m" : $"{m}m {s}s";
        long h = m / 60, mm = m % 60;
        return mm == 0 ? $"{h}h" : $"{h}h {mm}m";
    }
}
