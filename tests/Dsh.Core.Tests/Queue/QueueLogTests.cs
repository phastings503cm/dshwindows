namespace Dsh.Core.Tests;

/// <summary>Ported from QueueLogTests.swift, plus exact renderings pinned by a manual clock.</summary>
public sealed class QueueLogTests
{
    private readonly ManualClock _clock = new();

    private static QueueTask MakeTask(DateTimeOffset at) => new QueueTask
    {
        Title = "Fix the login crash",
        Details = "repro then patch",
        Status = QueueTaskStatus.Complete,
        EnteredAt = at,
        StartedAt = at,
        FinishedAt = at,
        Rounds = 3,
        PromptTokens = 40_000,
        CompletionTokens = 6_000,
    }.AppendingLog(QueueLogKind.Complete, "Complete after 3 rounds, 42s · 1142 tokens/s avg.", at);

    [Fact]
    public void ReportIsReadableAndComplete()
    {
        var q = new TaskQueue(clock: _clock);
        q.Add("B");
        q.AppendTask(MakeTask(_clock.GetUtcNow()));

        var text = QueueLog.Report(q);
        Assert.Contains("Fix the login crash", text);
        Assert.Contains("queued", text);
        Assert.Contains("3 rounds", text);
        Assert.Contains("tokens/s avg", text);
        Assert.Contains($"{Fmt.N(46_000)} tokens", text);   // "46,000 tokens" in en-US
        // header stats line
        Assert.Contains("2 tasks — 1 complete, 1 queued", text);
        // emoji marks
        Assert.Contains("✅", text);
        Assert.Contains("•", text);
    }

    [Fact]
    public void RunningTaskShowsElapsed()
    {
        var q = new TaskQueue(clock: _clock);
        var t = q.Add("Live");
        q.Start(t.Id);
        _clock.AdvanceSeconds(125);
        var text = QueueLog.Report(q);
        Assert.Contains("\u25B6\uFE0F", text);
        Assert.Contains("running 2m 5s", text);
    }

    [Fact]
    public void EmptyReport()
    {
        Assert.Contains("empty", QueueLog.Report(new TaskQueue()));
        Assert.Equal("The task queue is empty. Add a task to get started.", QueueLog.Report(new TaskQueue()));
    }

    [Fact]
    public void WhenRelativeBuckets()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        Assert.Equal("now", QueueLog.When(now.AddSeconds(-10), now));
        Assert.Equal("5m ago", QueueLog.When(now.AddSeconds(-300), now));
        Assert.Equal("1h ago", QueueLog.When(now.AddSeconds(-3600), now));
        Assert.Equal("yesterday", QueueLog.When(now.AddSeconds(-90_000), now));
        Assert.Equal("3d ago", QueueLog.When(now.AddSeconds(-3 * 86_400), now));
        var old = QueueLog.When(now.AddSeconds(-30 * 86_400), now);
        Assert.DoesNotContain("ago", old);   // falls back to an absolute stamp
        Assert.Equal("Oct 15, 22:13", QueueLog.When(now.AddSeconds(-30 * 86_400), now, TimeZoneInfo.Utc));
    }

    [Fact]
    public void StatusMarks()
    {
        Assert.Equal("✅", QueueLog.StatusMark(QueueTaskStatus.Complete));
        Assert.Equal("\u25B6\uFE0F", QueueLog.StatusMark(QueueTaskStatus.Running));   // ▶ + variation selector
        Assert.Equal("•", QueueLog.StatusMark(QueueTaskStatus.Queued));
        Assert.Equal("⏸", QueueLog.StatusMark(QueueTaskStatus.Blocked));
        Assert.Equal("✖", QueueLog.StatusMark(QueueTaskStatus.Failed));
        Assert.Equal("↦", QueueLog.StatusMark(QueueTaskStatus.Skipped));
    }

    [Fact]
    public void StampIsAbsoluteInTheGivenZone()
    {
        Assert.Equal("Sep 24 10:32:05", QueueLog.Stamp(ManualClock.Epoch, TimeZoneInfo.Utc));
        var local = new DateTimeOffset(new DateTime(2026, 3, 4, 7, 8, 9, DateTimeKind.Local));
        Assert.Equal("Mar 4 07:08:09", QueueLog.Stamp(local));
    }

    [Fact]
    public void ReportRendersEveryPartExactly()
    {
        var q = new TaskQueue(clock: _clock);                 // 10:32:05
        var done = q.Add("Ship it", details: "all of it");
        var waiting = q.Add("Then this");
        _clock.AdvanceSeconds(60);                             // 10:33:05
        q.Start(done.Id, "chat");
        _clock.AdvanceSeconds(120);                            // 10:35:05
        q.RecordRound(done.Id, 1, 1_000, 200);
        q.Finish(done.Id, QueueTaskStatus.Complete);
        _clock.Advance(TimeSpan.FromHours(3));                 // 13:35:05

        var expected = string.Join("\n",
            "2 tasks — 1 complete, 1 queued",
            "",
            "✅ Ship it",
            "  queued 3h ago · started 3h ago · finished 3h ago (2m, 1 round) · " + Fmt.N(1_200) + " tokens · 10 tokens/s avg",
            "  Sep 24 10:32:05  (3h ago)  Entered the queue at #1",
            "  Sep 24 10:33:05  (3h ago)  Work started — Ship it",
            "  Sep 24 10:35:05  (3h ago)  Complete after 1 round, 2m · 10 tokens/s avg.",
            "",
            "• Then this",
            "  queued 3h ago",
            "  Sep 24 10:32:05  (3h ago)  Entered the queue at #2");
        Assert.Equal(expected, QueueLog.Report(q));
        Assert.Equal(QueueLog.Report(q), QueueLog.Report(q.Tasks, _clock.GetUtcNow(), TimeZoneInfo.Utc));
        _ = waiting;
    }

    [Fact]
    public void HeaderCountsEveryOutcome()
    {
        var q = new TaskQueue(clock: _clock);
        var ids = new[] { "A", "B", "C", "D", "E" }.Select(t => q.Add(t).Id).ToArray();
        q.Start(ids[0]);
        q.Finish(ids[0], QueueTaskStatus.Failed, "boom");
        q.Start(ids[1]);
        q.Finish(ids[1], QueueTaskStatus.Blocked, "key");
        q.Finish(ids[2], QueueTaskStatus.Skipped);
        q.Start(ids[3]);
        var report = QueueLog.Report(q);
        Assert.StartsWith("5 tasks — 1 running, 1 queued, 1 failed, 1 blocked, 1 skipped\n\n", report);
        Assert.Contains("✖ A", report);
        Assert.Contains("⏸ B", report);
        Assert.Contains("↦ C", report);
        Assert.Contains("  Sep 24 10:32:05  (now)  Failed — boom.", report);

        var single = new TaskQueue(clock: _clock);
        single.Add("Only");
        Assert.StartsWith("1 task — 1 queued\n", QueueLog.Report(single));
    }

    [Fact]
    public void SectionOfOneTask()
    {
        var t = MakeTask(ManualClock.Epoch);
        var section = QueueLog.Section(t, ManualClock.Epoch.AddDays(1), TimeZoneInfo.Utc);
        Assert.Equal(
            "✅ Fix the login crash\n"
            + $"  queued yesterday · started yesterday · finished yesterday (0s, 3 rounds) · {Fmt.N(46_000)} tokens\n"
            + "  Sep 24 10:32:05  (yesterday)  Complete after 3 rounds, 42s · 1142 tokens/s avg.",
            section);
    }
}
