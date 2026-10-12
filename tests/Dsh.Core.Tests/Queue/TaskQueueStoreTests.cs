namespace Dsh.Core.Tests;

/// <summary>New for the port: the queue file (atomic writes, restarts, tolerant decoding, corrupt-file
/// backup — the store half of 0.10.0's queue fixes, which upstream kept in AppTransport), the Changed
/// event and thread safety.</summary>
public sealed class TaskQueueStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new("dsh-queue");
    private readonly ManualClock _clock = new();

    public void Dispose() => _dir.Dispose();

    private string QueueFile => _dir[TaskQueue.FileName];

    private TaskQueue Open() => new(QueueFile, _clock);

    [Fact]
    public void DefaultFileLivesUnderTheAppRoot()
    {
        Assert.Equal(Path.Combine(AppPaths.Root, "task-queue.json"), TaskQueue.DefaultFilePath);
    }

    [Fact]
    public void EveryChangeIsSavedAtomically()
    {
        var q = Open();
        Assert.False(File.Exists(QueueFile));   // nothing written until something changes
        var a = q.Add("A", details: "do it", cwd: _dir["proj"]);
        Assert.True(File.Exists(QueueFile));
        Assert.Single(new TaskQueue(QueueFile, _clock).Tasks);

        q.Start(a.Id, "chat");
        q.Note(a.Id, "halfway");
        q.Finish(a.Id, QueueTaskStatus.Complete);
        Assert.Equal(q.Tasks, Open().Tasks);
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
    }

    [Fact]
    public void TheFolderIsCreatedOnTheFirstSave()
    {
        var path = _dir["not/yet/there/task-queue.json"];
        var q = new TaskQueue(path, _clock);
        Assert.Empty(q.Tasks);
        q.Add("A");
        Assert.Equal(["A"], new TaskQueue(path, _clock).Tasks.Select(t => t.Title));
    }

    [Fact]
    public void FileUsesCamelCaseNamesAndLowerCaseStatuses()
    {
        var q = Open();
        var a = q.Add("A");
        q.Start(a.Id, "chat-1");
        q.Finish(a.Id, QueueTaskStatus.Blocked, "needs a key");
        var json = File.ReadAllText(QueueFile);
        Assert.Contains("\"tasks\":", json);
        Assert.Contains("\"status\": \"blocked\"", json);
        Assert.Contains("\"sessionId\": \"chat-1\"", json);
        Assert.Contains("\"kind\": \"entered\"", json);
        Assert.Contains("\"enteredAt\": \"2026-09-24T10:32:05+00:00\"", json);
        Assert.DoesNotContain("\"cwd\"", json);   // nulls are left out
    }

    [Fact]
    public void InMemoryQueueWritesNothing()
    {
        var q = new TaskQueue(clock: _clock);
        q.Add("A");
        Assert.Null(q.FilePath);
        Assert.Empty(Directory.GetFileSystemEntries(_dir.Path));
    }

    [Fact]
    public void RestartPutsARunningTaskBackInPlaceInItsChat()
    {
        var q = Open();
        var a = q.Add("A");
        var b = q.Add("B");
        q.Start(a.Id, "chat-a");
        q.RecordRound(a.Id, 3, 10, 10);

        // Simulate a quit: a second store reads the same file.
        var relaunched = Open();
        var t = relaunched.Find(a.Id)!;
        Assert.Equal(QueueTaskStatus.Queued, t.Status);
        Assert.Equal("chat-a", t.SessionId);
        Assert.Null(t.StartedAt);
        Assert.Equal(TaskQueue.InterruptedNote, t.Log[^1].Text);
        Assert.Equal(
            "Was running when the app quit — back in line; it resumes where it stopped.", t.Log[^1].Text);
        Assert.Equal(a.Id, relaunched.NextTask?.Id);   // still first in line
        Assert.Equal([a.Id, b.Id], relaunched.Tasks.Select(x => x.Id));
        Assert.Null(relaunched.LoadProblem);

        // ...and that is saved: the next launch doesn't flip or log it again.
        var third = Open();
        Assert.Equal(QueueTaskStatus.Queued, third.Find(a.Id)?.Status);
        Assert.Equal(t.Log.Count, third.Find(a.Id)?.Log.Count);
    }

    [Fact]
    public void QueueFilesWithoutNewFieldsStillDecode()
    {
        // Ported: a file written before cwd (and most other fields) existed.
        File.WriteAllText(QueueFile, """{"tasks":[{"id":"1","title":"Old","status":"queued","log":[]}]}""");
        var q = Open();
        var t = Assert.Single(q.Tasks);
        Assert.Equal("Old", t.Title);
        Assert.Null(t.Cwd);
        Assert.Equal("", t.Details);
        Assert.Equal(0, t.Rounds);
        Assert.Equal(0, t.TotalTokens);
        Assert.Equal(_clock.GetUtcNow(), t.EnteredAt);   // a missing date reads as "now"
        Assert.Null(q.LoadProblem);
    }

    [Fact]
    public void MissingFieldsTakeTheirDefaults()
    {
        File.WriteAllText(QueueFile, """{"tasks":[{"id":"x","someFutureField":{"a":1}}]}""");
        var t = Assert.Single(Open().Tasks);
        Assert.Equal("Untitled task", t.Title);
        Assert.Equal(QueueTaskStatus.Queued, t.Status);
        Assert.Empty(t.Log);
    }

    [Fact]
    public void UnknownStatusReadsAsBlockedAndUnknownLogKindAsNote()
    {
        File.WriteAllText(QueueFile, """
            {"tasks":[{"id":"1","title":"From a newer build","status":"paused","enteredAt":"2026-09-20T08:00:00Z",
              "log":[{"id":"3F2504E0-4F89-11D3-9A0C-0305E82C3301","at":"2026-09-20T08:00:00Z","kind":"teleported","text":"hm"}]}]}
            """);
        var q = Open();
        var t = Assert.Single(q.Tasks);
        // Needs a person to look, and must never be re-run unattended.
        Assert.Equal(QueueTaskStatus.Blocked, t.Status);
        Assert.Null(q.NextTask);
        var line = Assert.Single(t.Log);
        Assert.Equal(QueueLogKind.Note, line.Kind);
        Assert.Equal("hm", line.Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero), line.At);
    }

    [Fact]
    public void AnUnreadableLogIsDroppedButTheTaskKept()
    {
        File.WriteAllText(QueueFile, """
            {"tasks":[
              {"id":"1","title":"Bad log","log":[{"id":"not-a-guid","at":"2026-09-20T08:00:00Z","kind":"note","text":"x"}]},
              {"id":"2","title":"Line without text","log":[{"id":"3F2504E0-4F89-11D3-9A0C-0305E82C3301","at":"2026-09-20T08:00:00Z","kind":"note"}]},
              {"id":"3","title":"Log not a list","log":"oops"}]}
            """);
        var q = Open();
        Assert.Equal(["Bad log", "Line without text", "Log not a list"], q.Tasks.Select(t => t.Title));
        Assert.All(q.Tasks, t => Assert.Empty(t.Log));
        Assert.Null(q.LoadProblem);
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{"tasks":[{"title":"no id"}]}""")]
    [InlineData("""{"tasks":[{"id":"1","rounds":"three"}]}""")]
    [InlineData("""{"items":[]}""")]
    public void AnUnreadableFileIsSetAsideNotOverwritten(string contents)
    {
        File.WriteAllText(QueueFile, contents);
        var q = Open();
        Assert.Empty(q.Tasks);
        Assert.NotNull(q.UnreadableFilePath);
        var aside = q.UnreadableFilePath!;
        Assert.Equal($"task-queue.unreadable-{_clock.GetUtcNow().ToUnixTimeSeconds()}.json", Path.GetFileName(aside));
        Assert.Equal(contents, File.ReadAllText(aside));
        Assert.Equal(
            $"The task queue file couldn't be read, so the queue starts empty. The old file was kept as {Path.GetFileName(aside)}.",
            q.LoadProblem);

        // The queue works on; the old file stays as it was.
        q.Add("Fresh");
        Assert.Equal(["Fresh"], Open().Tasks.Select(t => t.Title));
        Assert.Equal(contents, File.ReadAllText(aside));
    }

    [Fact]
    public void ASecondUnreadableFileInTheSameSecondGetsItsOwnName()
    {
        File.WriteAllText(QueueFile, "garbage 1");
        var first = Open().UnreadableFilePath!;
        File.WriteAllText(QueueFile, "garbage 2");
        var second = Open().UnreadableFilePath!;
        Assert.NotEqual(first, second);
        Assert.Equal("garbage 1", File.ReadAllText(first));
        Assert.Equal("garbage 2", File.ReadAllText(second));
    }

    // MARK: - Changed and threading

    [Fact]
    public void ChangedIsRaisedForChangesOnly()
    {
        var q = Open();
        var raised = 0;
        q.Changed += () => raised++;

        var a = q.Add("A");
        Assert.Equal(1, raised);
        q.Start(a.Id);
        Assert.Equal(2, raised);
        q.Start(a.Id);                 // already running: no change
        q.Requeue(a.Id);               // running: no change
        q.Finish("nope", QueueTaskStatus.Complete);
        q.MoveOnto(a.Id, a.Id);
        Assert.Equal(2, raised);
    }

    [Fact]
    public void ChangedSeesTheNewStateAndMayChangeItAgain()
    {
        var q = Open();
        var seen = new List<int>();
        q.Changed += () =>
        {
            seen.Add(q.Count);
            // A handler runs outside the lock, so it can call back in.
            if (q.Count == 1) q.Add("second");
        };
        q.Add("first");
        Assert.Equal([1, 2], seen);
    }

    [Fact]
    public void BatchIsOneStepOneSaveOneEvent()
    {
        var q = Open();
        var a = q.Add("A");
        var raised = 0;
        q.Changed += () => raised++;

        // Check-then-act without another thread slipping in between.
        var finished = q.Batch(x => x.Find(a.Id)?.Status == QueueTaskStatus.Queued
                                    && x.Start(a.Id, "chat")
                                    && x.Note(a.Id, "first note")
                                    && x.Finish(a.Id, QueueTaskStatus.Complete));
        Assert.True(finished);
        Assert.Equal(1, raised);
        Assert.Equal(QueueTaskStatus.Complete, Open().Find(a.Id)?.Status);

        // A batch that changes nothing raises nothing.
        q.Batch(x => { _ = x.Stats(); });
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ConcurrentWritersLoseNothing()
    {
        var q = new TaskQueue(QueueFile);   // real clock
        var changed = 0;
        q.Changed += () => Interlocked.Increment(ref changed);
        const int threads = 8, perThread = 25;

        Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, n =>
        {
            for (var i = 0; i < perThread; i++)
            {
                var t = q.Add($"{n}-{i}", atFront: i % 2 == 0);
                q.Note(t.Id, "noted");
                _ = q.Tasks;
                _ = q.Stats();
                _ = q.Position(t.Id);
            }
        });

        Assert.Equal(threads * perThread, q.Count);
        Assert.Equal(threads * perThread * 2, changed);
        Assert.All(q.Tasks, t => Assert.Equal(2, t.Log.Count));
        // The file holds the last state, not an older one that finished writing late.
        Assert.Equal(q.Tasks, new TaskQueue(QueueFile).Tasks);
    }

    [Fact]
    public void SnapshotsDoNotChangeUnderTheReader()
    {
        var q = new TaskQueue(clock: _clock);
        var a = q.Add("A");
        var before = q.Tasks;
        var task = q.Find(a.Id)!;
        q.Start(a.Id);
        q.Add("B");
        Assert.Single(before);
        Assert.Equal(QueueTaskStatus.Queued, task.Status);
        Assert.Single(task.Log);
    }
}
