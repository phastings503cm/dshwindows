using System.Globalization;
using System.Text;

namespace Dsh.Core;

// MARK: - Queue log rendering
//
// Turns the raw task log into the easy-to-read, timestamped report the user pulls up: a title, a
// stats line, then the lines grouped under their task.

public static class QueueLog
{
    /// <summary>One task's section: header, a meta line, then its log lines.</summary>
    /// <param name="now">What relative times count back from (default: the system clock).</param>
    /// <param name="zone">The zone absolute stamps are shown in (default: the local one).</param>
    public static string Section(QueueTask task, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var lines = new List<string> { $"{StatusMark(task.Status)} {task.Title}", MetaLine(task, at, zone) };
        foreach (var line in task.Log) lines.Add(Format(line, at, zone));
        return string.Join("\n", lines);
    }

    /// <summary>The whole queue, in queue order so the "long view" reads top to bottom, timed by the
    /// queue's own clock unless <paramref name="now"/> is given.</summary>
    public static string Report(TaskQueue queue, DateTimeOffset? now = null) =>
        Report(queue.Tasks, now ?? queue.Now, queue.Clock.LocalTimeZone);

    /// <inheritdoc cref="Report(TaskQueue, DateTimeOffset?)"/>
    public static string Report(IReadOnlyList<QueueTask> tasks, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        if (tasks.Count == 0) return "The task queue is empty. Add a task to get started.";
        var at = now ?? DateTimeOffset.UtcNow;
        var s = QueueStats.Of(tasks);
        var buckets = new List<string>();
        if (s.Completed > 0) buckets.Add($"{s.Completed} complete");
        if (s.Running > 0) buckets.Add($"{s.Running} running");
        if (s.Queued > 0) buckets.Add($"{s.Queued} queued");
        if (s.Failed > 0) buckets.Add($"{s.Failed} failed");
        if (s.Blocked > 0) buckets.Add($"{s.Blocked} blocked");
        if (s.Skipped > 0) buckets.Add($"{s.Skipped} skipped");
        var text = new StringBuilder();
        text.Append($"{tasks.Count} task{(tasks.Count == 1 ? "" : "s")}")
            .Append(buckets.Count == 0 ? " (none finished yet)" : " — " + string.Join(", ", buckets))
            .Append("\n\n");
        foreach (var task in tasks) text.Append(Section(task, at, zone)).Append("\n\n");
        return text.ToString().Trim('\n', '\r');
    }

    public static string StatusMark(QueueTaskStatus status) => status switch
    {
        QueueTaskStatus.Complete => "✅",
        QueueTaskStatus.Running => "\u25B6\uFE0F",   // ▶ plus the emoji variation selector
        QueueTaskStatus.Queued => "•",
        QueueTaskStatus.Blocked => "⏸",
        QueueTaskStatus.Failed => "✖",
        _ => "↦",
    };

    private static string MetaLine(QueueTask task, DateTimeOffset now, TimeZoneInfo? zone)
    {
        var parts = new List<string> { $"queued {When(task.EnteredAt, now, zone)}" };
        if (task.StartedAt is { } started) parts.Add($"started {When(started, now, zone)}");
        if (task.FinishedAt is { } ended)
        {
            var d = ended - (task.StartedAt ?? ended);
            parts.Add($"finished {When(ended, now, zone)} ({d.FormattedDuration()}, {task.Rounds} round{(task.Rounds == 1 ? "" : "s")})");
        }
        else if (task.Status == QueueTaskStatus.Running && task.StartedAt is { } running)
        {
            parts.Add($"running {(now - running).FormattedDuration()}");
        }
        if (task.TotalTokens > 0) parts.Add($"{Fmt.N(task.TotalTokens)} tokens");
        if (task.AvgTokensPerSecond(now) is { } rate) parts.Add($"{Rounded(rate)} tokens/s avg");
        return "  " + string.Join(" · ", parts);
    }

    /// <summary>A log line with an absolute stamp — a multi-day log must still read right when it is
    /// copied out and read later — plus the relative age.</summary>
    private static string Format(QueueLogLine line, DateTimeOffset now, TimeZoneInfo? zone) =>
        $"  {Stamp(line.At, zone)}  ({When(line.At, now, zone)})  {line.Text}";

    /// <summary>"Sep 24 10:32:05", in <paramref name="zone"/> (default: the local one).</summary>
    public static string Stamp(DateTimeOffset date, TimeZoneInfo? zone = null) =>
        TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local).ToString("MMM d HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Compact, readable relative timestamps: "now", "5m ago", "2h ago", "yesterday", "3d ago",
    /// then "Sep 24, 10:32" for anything a week old or more.</summary>
    public static string When(DateTimeOffset date, DateTimeOffset? now = null, TimeZoneInfo? zone = null)
    {
        var secs = ((now ?? DateTimeOffset.UtcNow) - date).TotalSeconds;
        if (secs < 45) return "now";
        var m = (long)secs / 60;
        if (m < 60) return $"{m}m ago";
        var h = m / 60;
        if (h < 24) return $"{h}h ago";
        var days = h / 24;
        if (days == 1) return "yesterday";
        if (days < 7) return $"{days}d ago";
        return TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local).ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>A rate as a whole number, halves away from zero (Swift's <c>Int(x.rounded())</c>).</summary>
    internal static long Rounded(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
