namespace Dsh.Core.Tests;

/// <summary>Ported from TaskQueueTests.swift: the queue's ordering, statuses, stats and logs. These run
/// on an in-memory queue with a manual clock; TaskQueueStoreTests covers the file and threading.</summary>
public sealed class TaskQueueTests
{
    private readonly ManualClock _clock = new();

    private TaskQueue NewQueue() => new(clock: _clock);

    private static string[] Titles(TaskQueue q) => q.Tasks.Select(t => t.Title).ToArray();

    [Fact]
    public void AddOrderingAndPositions()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        var c = q.Add("C");
        var front = q.Add("D", atFront: true);

        Assert.Equal(["D", "A", "B", "C"], Titles(q));
        Assert.Equal(1, q.Position(front.Id));
        Assert.Equal(2, q.Position(a.Id));
        Assert.Equal(front.Id, q.NextTask?.Id);
        // entered-logs record the position it was added at
        Assert.Equal("Entered the queue at #1", front.Log[^1].Text);
        Assert.Equal("Entered the queue at #3", c.Log[^1].Text);
        Assert.Equal(QueueLogKind.Entered, c.Log[^1].Kind);
        Assert.Equal(_clock.GetUtcNow(), c.EnteredAt);
    }

    [Fact]
    public void MoveKeepsFinishedTasksOutOfTheWay()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        var c = q.Add("C");
        q.Start(a.Id);
        q.Finish(a.Id, QueueTaskStatus.Complete);

        // Move C to the front: finished A must not block the renumbering.
        q.MoveBefore(c.Id, a.Id);
        Assert.Equal(["C", "A", "B"], Titles(q));
        Assert.Equal(1, q.Position(c.Id));

        // Move B one up (before finished A) → B lands right after A's slot.
        q.MoveBy(q.Tasks[2].Id, -1);
        Assert.Equal(["C", "B", "A"], Titles(q));
        Assert.Equal(2, q.Position(q.Tasks[1].Id));
    }

    [Fact]
    public void MoveByStopsAtTheEnds()
    {
        // Upstream indexed past the array here; the port clamps.
        var q = NewQueue();
        var a = q.Add("A");
        var b = q.Add("B");
        Assert.False(q.MoveBy(a.Id, -1));
        Assert.False(q.MoveBy(b.Id, 1));
        Assert.False(q.MoveBy(b.Id, 50));
        Assert.Equal(["A", "B"], Titles(q));
        Assert.True(q.MoveBy(a.Id, 50));
        Assert.Equal(["B", "A"], Titles(q));
        Assert.Equal("Moved to position #2", q.Find(a.Id)!.Log[^1].Text);
    }

    [Fact]
    public void RemoveRunningTaskMarksItSkipped()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        q.Start(a.Id);
        q.Remove(a.Id);

        // Running task is kept as a record, marked skipped.
        Assert.Equal(2, q.Count);
        Assert.Equal(QueueTaskStatus.Skipped, q.Find(a.Id)?.Status);
        Assert.Equal(QueueLogKind.Skipped, q.Find(a.Id)?.Log[^1].Kind);
        Assert.Equal("Deleted while running — skipped.", q.Find(a.Id)?.Log[^1].Text);
        Assert.NotNull(q.Find(a.Id)?.FinishedAt);

        // A queued task really disappears.
        var b = q.Tasks.First(t => t.Title == "B");
        q.Remove(b.Id);
        Assert.All(q.Tasks, t => Assert.Equal(QueueTaskStatus.Skipped, t.Status));
    }

    [Fact]
    public void UpdateKeepsStatusAndLogs()
    {
        var q = NewQueue();
        var a = q.Add("Old title", details: "d1");
        q.Update(a.Id, "New title", "d2");
        Assert.Equal("New title", q.Find(a.Id)?.Title);
        Assert.Equal("d2", q.Find(a.Id)?.Details);
        Assert.Equal(QueueTaskStatus.Queued, q.Find(a.Id)?.Status);
        Assert.Equal(QueueLogKind.Note, q.Find(a.Id)?.Log[^1].Kind);
        Assert.Equal("Details updated.", q.Find(a.Id)?.Log[^1].Text);

        // A blank title is ignored; details are trimmed.
        q.Update(a.Id, "   ", "  d3 \n");
        Assert.Equal("New title", q.Find(a.Id)?.Title);
        Assert.Equal("d3", q.Find(a.Id)?.Details);
    }

    [Fact]
    public void StatsAndTokensPerSecond()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        q.Start(a.Id);
        _clock.AdvanceSeconds(10);
        q.RecordRound(a.Id, round: 3, prompt: 900, completion: 100);
        q.Finish(a.Id, QueueTaskStatus.Complete, sessionId: "s1");

        var s = q.Stats();
        Assert.Equal(1, s.Completed);
        Assert.Equal(1, s.Queued);
        Assert.Equal(900, s.PromptTokens);
        Assert.Equal(100, s.CompletionTokens);
        Assert.Equal(TimeSpan.FromSeconds(10), s.TotalDuration);
        Assert.Equal(100, s.TokensPerSecond);
        Assert.False(s.Finished);

        var t = q.Find(a.Id)!;
        Assert.Equal(1000, t.TotalTokens);
        Assert.Equal(3, t.Rounds);
        Assert.Equal("s1", t.SessionId);
        Assert.Equal(TimeSpan.FromSeconds(10), t.Duration());
        Assert.Equal(100, t.AvgTokensPerSecond());

        // Drain the queue → finished flag.
        var b = q.NextTask!;
        q.Start(b.Id);
        q.Finish(b.Id, QueueTaskStatus.Complete);
        Assert.True(q.Stats().Finished);
        Assert.False(q.HasRunning);
        Assert.Equal(2, q.CompleteCount);
    }

    [Fact]
    public void RunningTaskDurationCountsUpToNow()
    {
        var q = NewQueue();
        var a = q.Add("A");
        Assert.Null(q.Find(a.Id)!.Duration(_clock.GetUtcNow()));
        q.Start(a.Id);
        q.RecordRound(a.Id, 1, 50, 50);
        _clock.AdvanceSeconds(20);
        var t = q.Find(a.Id)!;
        Assert.Equal(TimeSpan.FromSeconds(20), t.Duration(_clock.GetUtcNow()));
        Assert.Equal(5, t.AvgTokensPerSecond(_clock.GetUtcNow()));
        // Running tasks' time isn't in the totals until they finish.
        Assert.Equal(TimeSpan.Zero, q.Stats().TotalDuration);
        Assert.Null(q.Stats().TokensPerSecond);
    }

    [Fact]
    public void FinishLogCarriesRoundsAndRate()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Start(a.Id);
        _clock.AdvanceSeconds(4);
        q.RecordRound(a.Id, round: 2, prompt: 100, completion: 100);
        q.Finish(a.Id, QueueTaskStatus.Complete);
        var line = q.Find(a.Id)!.Log[^1];
        Assert.Equal(QueueLogKind.Complete, line.Kind);
        Assert.Equal("Complete after 2 rounds, 4s · 50 tokens/s avg.", line.Text);

        // One round, no runtime → no rate.
        var b = q.Add("B");
        q.Start(b.Id);
        q.RecordRound(b.Id, round: 1, prompt: 10, completion: 10);
        q.Finish(b.Id, QueueTaskStatus.Complete);
        Assert.Equal("Complete after 1 round, 0s.", q.Find(b.Id)!.Log[^1].Text);
    }

    [Fact]
    public void BlockedAndFailedReasons()
    {
        var q = NewQueue();
        var a = q.Add("A");
        var b = q.Add("B");
        var c = q.Add("C");
        q.Start(a.Id);
        q.Finish(a.Id, QueueTaskStatus.Blocked, reason: "missing API key");
        q.Start(b.Id);
        q.Finish(b.Id, QueueTaskStatus.Failed, reason: "server 500");
        q.Finish(c.Id, QueueTaskStatus.Skipped);
        Assert.Equal(QueueLogKind.Blocked, q.Find(a.Id)!.Log[^1].Kind);
        Assert.Equal("Blocked — missing API key.", q.Find(a.Id)!.Log[^1].Text);
        Assert.Equal(QueueLogKind.Failed, q.Find(b.Id)!.Log[^1].Kind);
        Assert.Equal("Failed — server 500.", q.Find(b.Id)!.Log[^1].Text);
        Assert.Equal("Skipped.", q.Find(c.Id)!.Log[^1].Text);

        // Without a reason.
        var d = q.Add("D");
        var e = q.Add("E");
        q.Finish(d.Id, QueueTaskStatus.Blocked);
        q.Finish(e.Id, QueueTaskStatus.Failed);
        Assert.Equal("Blocked — needs the user.", q.Find(d.Id)!.Log[^1].Text);
        Assert.Equal("Failed — unknown error.", q.Find(e.Id)!.Log[^1].Text);
    }

    [Fact]
    public void PersistenceRoundTrip()
    {
        using var dir = new TempDirectory("dsh-queue");
        var path = dir["task-queue.json"];
        var q = new TaskQueue(path, _clock);
        var a = q.Add("A", details: "do a");
        q.Add("B");
        q.Start(a.Id);
        q.RecordRound(a.Id, round: 4, prompt: 50, completion: 60);
        q.Finish(a.Id, QueueTaskStatus.Complete, sessionId: "s");
        q.MoveBefore(q.Tasks[1].Id, a.Id);

        var decoded = new TaskQueue(path, _clock);
        Assert.Equal(q.Tasks, decoded.Tasks);   // every field, log lines included
        Assert.Equal(["B", "A"], Titles(decoded));
        Assert.Equal(4, decoded.Find(a.Id)?.Rounds);
        Assert.Equal("s", decoded.Find(a.Id)?.SessionId);
        Assert.Null(decoded.LoadProblem);
    }

    [Fact]
    public void BlockedTaskCanBeRetried()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        q.Start(a.Id);
        q.RecordRound(a.Id, round: 7, prompt: 70, completion: 70);
        q.Finish(a.Id, QueueTaskStatus.Blocked, reason: "needs you");
        // Retry: start must accept a blocked task and reset its stats.
        q.Start(a.Id);
        Assert.Equal(QueueTaskStatus.Running, q.Find(a.Id)?.Status);
        Assert.Equal(0, q.Find(a.Id)?.Rounds);
        Assert.Equal(0, q.Find(a.Id)?.TotalTokens);
        Assert.Null(q.Find(a.Id)?.FinishedAt);
        q.RecordRound(a.Id, round: 1, prompt: 5, completion: 5);
        q.Finish(a.Id, QueueTaskStatus.Complete);
        Assert.Equal(1, q.Find(a.Id)?.Rounds);
        Assert.Equal(5, q.Find(a.Id)?.PromptTokens);
    }

    [Fact]
    public void StartIgnoresARunningTask()
    {
        var q = NewQueue();
        var a = q.Add("A");
        Assert.True(q.Start(a.Id));
        q.RecordRound(a.Id, 2, 10, 10);
        Assert.False(q.Start(a.Id));
        Assert.Equal(2, q.Find(a.Id)?.Rounds);
        Assert.Equal("Work started — A", q.Find(a.Id)?.Log[^1].Text);
    }

    [Fact]
    public void RequeueMovesBlockedToFrontOfQueuedRegion()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        q.Add("C");
        q.Start(a.Id);
        q.Finish(a.Id, QueueTaskStatus.Failed, reason: "boom");
        q.Requeue(a.Id, toFront: true);
        Assert.Equal(QueueTaskStatus.Queued, q.Find(a.Id)?.Status);
        Assert.Equal("Re-queued to retry.", q.Find(a.Id)?.Log[^1].Text);
        // A is now position #1 of the queued region.
        Assert.Equal(a.Id, q.NextTask?.Id);
        Assert.Equal(1, q.Position(a.Id));

        // To the back instead.
        q.Start(a.Id);
        q.Finish(a.Id, QueueTaskStatus.Blocked);
        q.Requeue(a.Id, toFront: false);
        Assert.Equal(["B", "C", "A"], Titles(q));
    }

    [Fact]
    public void RequeueIgnoresRunningTask()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Start(a.Id);
        Assert.False(q.Requeue(a.Id));   // no-op while running
        Assert.Equal(QueueTaskStatus.Running, q.Find(a.Id)?.Status);
    }

    [Fact]
    public void MarkStoppedOnlyFromRunning()
    {
        var q = NewQueue();
        var a = q.Add("A");
        Assert.False(q.MarkStopped(a.Id));   // not running → no-op
        Assert.Equal(QueueTaskStatus.Queued, q.Find(a.Id)?.Status);
        q.Start(a.Id);
        q.MarkStopped(a.Id);
        Assert.Equal(QueueTaskStatus.Queued, q.Find(a.Id)?.Status);
        Assert.Null(q.Find(a.Id)?.StartedAt);
        Assert.Equal(QueueLogKind.Note, q.Find(a.Id)?.Log[^1].Kind);
        Assert.Equal("Stopped by the user — back in the queue.", q.Find(a.Id)?.Log[^1].Text);
    }

    [Fact]
    public void GoalTextFoldsTitleAndDetails()
    {
        var q = NewQueue();
        var t = q.Add("Short title", details: "Full instructions here.");
        Assert.StartsWith("Short title", t.GoalText);
        Assert.Contains("Full instructions here.", t.GoalText);
        Assert.Equal("Short title\n\nFull instructions here.", t.GoalText);
        // No details → title only.
        var bare = q.Add("Just a title");
        Assert.Equal("Just a title", bare.GoalText);
    }

    [Fact]
    public void GlobalLogIsTimeOrdered()
    {
        var q = NewQueue();
        var a = q.Add("A");
        _clock.AdvanceSeconds(1);
        var b = q.Add("B");
        _clock.AdvanceSeconds(1);
        q.Start(a.Id);
        _clock.AdvanceSeconds(1);
        q.Note(b.Id, "B waits");
        _clock.AdvanceSeconds(1);
        q.RecordRound(a.Id, round: 1, prompt: 10, completion: 10);
        q.Finish(a.Id, QueueTaskStatus.Complete);
        var lines = q.AllLogLines;
        Assert.Equal(q.Tasks.Sum(t => t.Log.Count), lines.Count);
        foreach (var (earlier, later) in lines.Zip(lines.Skip(1))) Assert.True(earlier.At <= later.At);
        Assert.Equal(QueueLogKind.Entered, lines[0].Kind);
        Assert.Equal(QueueLogKind.Complete, lines[^1].Kind);
        Assert.Equal(["Entered the queue at #1", "Entered the queue at #2", "Work started — A", "B waits"],
            lines.Take(4).Select(l => l.Text));
    }

    [Fact]
    public void DurationFormatting()
    {
        Assert.Equal("59s", 59.4.FormattedDuration());
        Assert.Equal("2m 5s", 125.0.FormattedDuration());
        Assert.Equal("2m", 120.0.FormattedDuration());
        Assert.Equal("1h 2m", 3723.0.FormattedDuration());
        Assert.Equal("2h", 7200.0.FormattedDuration());
        Assert.Equal("2m 42s", TimeSpan.FromSeconds(162).FormattedDuration());
        Assert.Equal("1m", 59.5.FormattedDuration());   // halves round away from zero, like Swift
    }

    // MARK: - Runner support

    [Fact]
    public void DragOntoReachesEveryPositionIncludingFirstAndLast()
    {
        var q = NewQueue();
        var a = q.Add("A");
        var b = q.Add("B");
        var c = q.Add("C");
        // The first task can be dragged down (it used to be stuck).
        q.MoveOnto(a.Id, c.Id);
        Assert.Equal(["B", "C", "A"], Titles(q));
        // ... and the last can be dragged to the top.
        q.MoveOnto(a.Id, b.Id);
        Assert.Equal(["A", "B", "C"], Titles(q));
        // Onto a finished task: ignored.
        q.Start(c.Id);
        q.Finish(c.Id, QueueTaskStatus.Complete);
        Assert.False(q.MoveOnto(a.Id, c.Id));
        Assert.Equal(["A", "B", "C"], Titles(q));
        Assert.Equal(QueueLogKind.Reordered, q.Find(a.Id)?.Log[^1].Kind);
        Assert.Equal("Moved to position #1", q.Find(a.Id)?.Log[^1].Text);
    }

    [Fact]
    public void MoveBeforeCanMoveTheFirstTask()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Add("B");
        var c = q.Add("C");
        q.MoveBefore(a.Id, c.Id);
        Assert.Equal(["B", "A", "C"], Titles(q));
        q.MoveBefore(a.Id, null);
        Assert.Equal(["B", "C", "A"], Titles(q));
        // Already there / onto itself: nothing to do, nothing logged.
        var logged = q.Find(a.Id)!.Log.Count;
        Assert.False(q.MoveBefore(a.Id, null));
        Assert.False(q.MoveBefore(a.Id, a.Id));
        Assert.Equal(logged, q.Find(a.Id)!.Log.Count);
    }

    [Fact]
    public void StartRecordsTheSessionSoAStoppedTaskResumesInItsChat()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Start(a.Id, sessionId: "chat-1");
        Assert.Equal("chat-1", q.Find(a.Id)?.SessionId);
        q.MarkStopped(a.Id);
        Assert.Equal(QueueTaskStatus.Queued, q.Find(a.Id)?.Status);
        Assert.Equal("chat-1", q.Find(a.Id)?.SessionId);
        // Started again without naming a chat: it keeps the one it had.
        q.Start(a.Id);
        Assert.Equal("chat-1", q.Find(a.Id)?.SessionId);
    }

    [Fact]
    public void AttachAndDetachSession()
    {
        var q = NewQueue();
        var a = q.Add("A");
        var b = q.Add("B");
        q.AttachSession(a.Id, "chat");
        q.AttachSession(b.Id, "chat");
        q.Start(b.Id);
        // A deleted chat is forgotten by waiting tasks, never by the one running in it.
        Assert.True(q.DetachSession("chat"));
        Assert.Null(q.Find(a.Id)?.SessionId);
        Assert.Equal("chat", q.Find(b.Id)?.SessionId);
        Assert.False(q.DetachSession("chat"));
    }

    [Fact]
    public void TaskRemembersItsProjectAndTrimsInput()
    {
        var q = NewQueue();
        var a = q.Add("  Fix it \n", details: "\n steps \n", cwd: "/tmp/proj");
        Assert.Equal("Fix it", a.Title);
        Assert.Equal("steps", a.Details);
        Assert.Equal("/tmp/proj", a.Cwd);
        Assert.Null(q.Add("B", cwd: "").Cwd);
    }

    [Fact]
    public void NotesAndRequeueOfSkippedTasks()
    {
        var q = NewQueue();
        var a = q.Add("A");
        q.Start(a.Id);
        q.Note(a.Id, "Model unavailable — retrying.");
        Assert.Equal("Model unavailable — retrying.", q.Find(a.Id)?.Log[^1].Text);
        q.Remove(a.Id);   // running → skipped
        Assert.Equal(QueueTaskStatus.Skipped, q.Find(a.Id)?.Status);
        q.Requeue(a.Id);
        Assert.Equal(QueueTaskStatus.Queued, q.Find(a.Id)?.Status);
        Assert.Null(q.Find(a.Id)?.FinishedAt);
    }

    [Fact]
    public void RecordRoundOnlyCountsARunningTask()
    {
        var q = NewQueue();
        var a = q.Add("A");
        Assert.False(q.RecordRound(a.Id, 1, 10, 10));
        q.Start(a.Id);
        q.RecordRound(a.Id, 1, 10, 20);
        q.RecordRound(a.Id, 2, 30, 40);
        Assert.Equal(2, q.Find(a.Id)?.Rounds);
        Assert.Equal(40, q.Find(a.Id)?.PromptTokens);
        Assert.Equal(60, q.Find(a.Id)?.CompletionTokens);
        q.Finish(a.Id, QueueTaskStatus.Complete);
        Assert.False(q.RecordRound(a.Id, 3, 1, 1));
    }

    [Fact]
    public void UnknownIdsAreIgnored()
    {
        var q = NewQueue();
        q.Add("A");
        Assert.False(q.Start("nope"));
        Assert.False(q.Finish("nope", QueueTaskStatus.Complete));
        Assert.False(q.Remove("nope"));
        Assert.False(q.Note("nope", "x"));
        Assert.False(q.MoveOnto("nope", "nope2"));
        Assert.Null(q.Find("nope"));
        Assert.Null(q.Position("nope"));
        Assert.Equal(-1, q.IndexOf("nope"));
    }

    [Fact]
    public void StatusNamesAndLabels()
    {
        Assert.Equal(["queued", "running", "complete", "blocked", "failed", "skipped"],
            QueueTaskStatuses.All.Select(s => s.RawValue()));
        Assert.Equal(["Queued", "Running", "Complete", "Blocked", "Failed", "Skipped"],
            QueueTaskStatuses.All.Select(s => s.Label()));
        Assert.All(QueueTaskStatuses.All, s => Assert.Equal(s, QueueTaskStatuses.FromRaw(s.RawValue())));
        Assert.Null(QueueTaskStatuses.FromRaw("paused"));
        Assert.All(Enum.GetValues<QueueLogKind>(), k => Assert.Equal(k, QueueLogKinds.FromRaw(k.RawValue())));
    }
}
