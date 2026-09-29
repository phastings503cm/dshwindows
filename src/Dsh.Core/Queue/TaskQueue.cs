namespace Dsh.Core;

// MARK: - Task queue: the store
//
// One queue per app. The runner (async code) and the UI (the WPF dispatcher) both read and change
// it, so every member takes one lock; the tasks inside are immutable records, so what a reader gets
// back is a snapshot nothing can change under it. Every change is saved to one JSON file (written
// atomically) so a week-long queue survives restarts, and every transition appends a timestamped
// line to the task's log so the whole run can be read afterwards.

/// <summary>A persistent, ordered, thread-safe queue of tasks.</summary>
public sealed class TaskQueue
{
    /// <summary>The queue's file name under <see cref="AppPaths.Root"/>.</summary>
    public const string FileName = "task-queue.json";

    /// <summary>%APPDATA%\DSH\task-queue.json (or under DSH_HOME).</summary>
    public static string DefaultFilePath => Path.Combine(AppPaths.Root, FileName);

    /// <summary>The note a task gets when a restart finds it "running": the work in flight was lost.</summary>
    public const string InterruptedNote = "Was running when the app quit — back in the queue; it resumes in the same chat.";

    private readonly Lock _lock = new();
    private readonly Lock _writeLock = new();
    private readonly List<QueueTask> _tasks = [];
    private int _depth;
    private bool _dirty;
    private long _version;
    private long _writtenVersion;
    private bool _readOnly;

    /// <summary>Open the queue stored at <paramref name="filePath"/> (created on the first change), or
    /// an in-memory queue that is never saved when it is null. A file that can't be read is set aside
    /// (see <see cref="LoadProblem"/>) and the queue starts empty; a task a restart finds "running" goes
    /// back in line, in place, keeping its chat.</summary>
    /// <param name="clock">Time source for every timestamp (tests pass a manual one).</param>
    public TaskQueue(string? filePath = null, TimeProvider? clock = null)
    {
        FilePath = filePath;
        Clock = clock ?? TimeProvider.System;
        if (filePath is not null) Load(filePath);
    }

    /// <summary>Where the queue is saved; null for an in-memory queue.</summary>
    public string? FilePath { get; }

    public TimeProvider Clock { get; }

    /// <summary>The clock's current time (UTC).</summary>
    public DateTimeOffset Now => Clock.GetUtcNow();

    /// <summary>Set when the file couldn't be read at open: a sentence for the app's banner.</summary>
    public string? LoadProblem { get; private set; }

    /// <summary>Where an unreadable queue file was moved to, if one was.</summary>
    public string? UnreadableFilePath { get; private set; }

    /// <summary>Raised after every change (once after a <see cref="Batch(Action{TaskQueue})"/>), outside the lock, on
    /// the thread that made the change — the UI marshals to its dispatcher.</summary>
    public event Action? Changed;

    // MARK: - Querying

    /// <summary>A snapshot of every task, in queue order.</summary>
    public IReadOnlyList<QueueTask> Tasks
    {
        get
        {
            lock (_lock) return [.. _tasks];
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _tasks.Count;
        }
    }

    public QueueTask? Find(string id)
    {
        lock (_lock) return FindIndex(id) is var i and >= 0 ? _tasks[i] : null;
    }

    /// <summary>The task's index in queue order, or -1.</summary>
    public int IndexOf(string id)
    {
        lock (_lock) return FindIndex(id);
    }

    /// <summary>The next task to work: the first queued one.</summary>
    public QueueTask? NextTask
    {
        get
        {
            lock (_lock) return _tasks.Find(t => t.Status == QueueTaskStatus.Queued);
        }
    }

    public QueueTask? RunningTask
    {
        get
        {
            lock (_lock) return _tasks.Find(t => t.Status == QueueTaskStatus.Running);
        }
    }

    public bool HasRunning => RunningTask is not null;

    public int QueuedCount
    {
        get
        {
            lock (_lock) return CountQueued(_tasks.Count);
        }
    }

    public int CompleteCount
    {
        get
        {
            lock (_lock) return _tasks.Count(t => t.Status == QueueTaskStatus.Complete);
        }
    }

    /// <summary>1-based position among queued tasks (the "long view" number): how many queued tasks
    /// come before it, plus one. Null for an unknown id.</summary>
    public int? Position(string id)
    {
        lock (_lock) return PositionLocked(id);
    }

    public QueueStats Stats()
    {
        lock (_lock) return QueueStats.Of(_tasks);
    }

    /// <summary>All log lines across all tasks, oldest first — the queue's global log.</summary>
    public IReadOnlyList<QueueLogLine> AllLogLines
    {
        get
        {
            lock (_lock) return _tasks.SelectMany(t => t.Log).OrderBy(l => l.At).ToList();
        }
    }

    private int FindIndex(string id) => _tasks.FindIndex(t => t.Id == id);

    private int CountQueued(int before)
    {
        var n = 0;
        for (var i = 0; i < before; i++)
            if (_tasks[i].Status == QueueTaskStatus.Queued) n++;
        return n;
    }

    private int? PositionLocked(string id) => FindIndex(id) is var i and >= 0 ? CountQueued(i) + 1 : null;

    // MARK: - Mutations

    /// <summary>Run several reads and changes as one step: nothing else touches the queue in between,
    /// and it is saved and <see cref="Changed"/> raised once, at the end. Keep the body short and never
    /// wait on another thread inside it (the UI thread may be waiting for the lock).</summary>
    public void Batch(Action<TaskQueue> body) => Mutate(() => { body(this); return true; });

    /// <inheritdoc cref="Batch(Action{TaskQueue})"/>
    public T Batch<T>(Func<TaskQueue, T> body) => Mutate(() => body(this));

    /// <summary>Add a task at the back of the queue, or at the front of the waiting tasks. Title and
    /// details are trimmed; an empty <paramref name="cwd"/> means none. Returns the task as stored.</summary>
    public QueueTask Add(string title, string details = "", bool atFront = false, string? cwd = null) => Mutate(() =>
    {
        var now = Now;
        var task = new QueueTask
        {
            Title = title.Trim(),
            Details = details.Trim(),
            EnteredAt = now,
            Cwd = string.IsNullOrEmpty(cwd) ? null : cwd,
        };
        task = task.AppendingLog(QueueLogKind.Entered, $"Entered the queue at #{(atFront ? 1 : CountQueued(_tasks.Count) + 1)}", now);
        if (atFront) _tasks.Insert(FrontOfQueued(), task);
        else _tasks.Add(task);
        _dirty = true;
        return task;
    });

    /// <summary>Append a fully-formed task (restoration and tests; the normal path is <see cref="Add"/>,
    /// which logs the entry).</summary>
    public void AppendTask(QueueTask task) => Mutate(() =>
    {
        _tasks.Add(task);
        return _dirty = true;
    });

    /// <summary>Edit a task's title (ignored when blank) and/or details, in any state.</summary>
    public bool Update(string id, string? title, string? details) => Change(id, t =>
    {
        if (title?.Trim() is { Length: > 0 } trimmed) t = t with { Title = trimmed };
        if (details is not null) t = t with { Details = details.Trim() };
        return Log(t, QueueLogKind.Note, "Details updated.");
    });

    /// <summary>Delete a task. A running task is never lost: it stays as a record, marked skipped (the
    /// runner then moves on to the next task).</summary>
    public bool Remove(string id) => Mutate(() =>
    {
        var i = FindIndex(id);
        if (i < 0) return false;
        var t = _tasks[i];
        if (t.Status == QueueTaskStatus.Running)
            _tasks[i] = Log(t with { Status = QueueTaskStatus.Skipped, FinishedAt = Now }, QueueLogKind.Skipped,
                "Deleted while running — skipped.");
        else
            _tasks.RemoveAt(i);
        return _dirty = true;
    });

    /// <summary>Move a queued task <paramref name="offset"/> places (−1 = up), stepping over finished
    /// tasks so it stays inside the queued region.</summary>
    public bool MoveBy(string id, int offset) => Mutate(() =>
    {
        var from = FindIndex(id);
        if (from < 0 || _tasks[from].Status != QueueTaskStatus.Queued) return false;
        // Clamped first: upstream indexes past the ends when the first task moves up or the last down.
        var to = Math.Clamp(from + offset, 0, _tasks.Count - 1);
        while (to > 0 && _tasks[to - 1].Status != QueueTaskStatus.Queued) to--;
        while (to < _tasks.Count - 1 && _tasks[to + 1].Status != QueueTaskStatus.Queued) to++;
        return to != from && MoveLocked(from, to);
    });

    /// <summary>Move a queued task so it sits just before <paramref name="targetId"/> (null, or an
    /// unknown id, = the end).</summary>
    public bool MoveBefore(string id, string? targetId) => Mutate(() =>
    {
        var from = FindIndex(id);
        if (from < 0 || _tasks[from].Status != QueueTaskStatus.Queued || targetId == id) return false;
        var to = targetId is null || FindIndex(targetId) is not (var target and >= 0) ? _tasks.Count : target;
        if (to > from) to--;
        return to != from && MoveLocked(from, to);
    });

    /// <summary>Drag-and-drop: dropping a queued task onto another queued task puts it in that task's
    /// place — after it when dragged down, before it when dragged up — so every position, first and last
    /// included, is reachable.</summary>
    public bool MoveOnto(string id, string targetId) => Mutate(() =>
    {
        int from = FindIndex(id), to = FindIndex(targetId);
        if (from < 0 || to < 0 || from == to
            || _tasks[from].Status != QueueTaskStatus.Queued || _tasks[to].Status != QueueTaskStatus.Queued) return false;
        return MoveLocked(from, to);
    });

    private bool MoveLocked(int from, int to)
    {
        var t = _tasks[from];
        _tasks.RemoveAt(from);
        _tasks.Insert(to, t);
        _tasks[to] = Log(t, QueueLogKind.Reordered, $"Moved to position #{PositionLocked(t.Id) ?? 1}");
        return _dirty = true;
    }

    /// <summary>Start work on a task: a queued one, or a blocked/failed one being retried. Stats start
    /// fresh each run. The chat is recorded now, not at the end, so a task stopped or interrupted
    /// mid-way resumes in the same conversation, with its context.</summary>
    public bool Start(string id, string? sessionId = null) => Change(id, t =>
    {
        if (t.Status == QueueTaskStatus.Running) return null;
        t = t with
        {
            Status = QueueTaskStatus.Running,
            StartedAt = Now,
            FinishedAt = null,
            Rounds = 0,
            PromptTokens = 0,
            CompletionTokens = 0,
            SessionId = sessionId ?? t.SessionId,
        };
        return Log(t, QueueLogKind.Started, $"Work started — {t.Title}");
    });

    /// <summary>Record the chat a task will run in (before it starts).</summary>
    public bool AttachSession(string id, string sessionId) => Change(id, t => t with { SessionId = sessionId });

    /// <summary>Forget a deleted chat: tasks that pointed at it (and aren't running) get a fresh one
    /// next time.</summary>
    public bool DetachSession(string sessionId) => Mutate(() =>
    {
        var changed = false;
        for (var i = 0; i < _tasks.Count; i++)
        {
            if (_tasks[i].SessionId != sessionId || _tasks[i].Status == QueueTaskStatus.Running) continue;
            _tasks[i] = _tasks[i] with { SessionId = null };
            changed = _dirty = true;
        }
        return changed;
    });

    /// <summary>A free-form line in a task's history (model outages, recoveries, …).</summary>
    public bool Note(string id, string text) => Change(id, t => Log(t, QueueLogKind.Note, text));

    /// <summary>Return a running task to the queue, in place (the user stopped the run mid-way). It
    /// keeps its chat.</summary>
    public bool MarkStopped(string id, string note = "Stopped by the user — back in the queue.") => Change(id, t =>
        t.Status != QueueTaskStatus.Running
            ? null
            : Log(t with { Status = QueueTaskStatus.Queued, StartedAt = null }, QueueLogKind.Note, note));

    /// <summary>Put a finished (blocked, failed, skipped, complete) task back in line — at the front of
    /// the waiting tasks by default, so it's retried next — keeping its chat so work resumes with its
    /// context. A running task is left alone.</summary>
    public bool Requeue(string id, bool toFront = true) => Mutate(() =>
    {
        var at = FindIndex(id);
        if (at < 0 || _tasks[at].Status == QueueTaskStatus.Running) return false;
        var t = _tasks[at];
        var wasFinished = t.Status != QueueTaskStatus.Queued;
        t = t with { Status = QueueTaskStatus.Queued, StartedAt = null, FinishedAt = null };
        if (wasFinished) t = Log(t, QueueLogKind.Note, "Re-queued to retry.");
        _tasks.RemoveAt(at);
        if (toFront) _tasks.Insert(FrontOfQueued(), t);
        else _tasks.Add(t);
        return _dirty = true;
    });

    /// <summary>A goal round finished: <paramref name="round"/> is the round count so far; the tokens
    /// add to the task's totals. Ignored unless the task is running.</summary>
    public bool RecordRound(string id, int round, long prompt, long completion) => Change(id, t =>
        t.Status != QueueTaskStatus.Running
            ? null
            : t with { Rounds = round, PromptTokens = t.PromptTokens + prompt, CompletionTokens = t.CompletionTokens + completion });

    /// <summary>End a task with <paramref name="status"/> and log the outcome — rounds, duration and
    /// rate for a complete one, the reason for a blocked or failed one.</summary>
    public bool Finish(string id, QueueTaskStatus status, string? reason = null, string? sessionId = null) => Change(id, t =>
    {
        t = t with { Status = status, FinishedAt = Now, SessionId = sessionId ?? t.SessionId };
        switch (status)
        {
            case QueueTaskStatus.Complete:
                var d = t.Duration()?.FormattedDuration() ?? "?";
                var rate = t.AvgTokensPerSecond() is { } r ? $" · {QueueLog.Rounded(r)} tokens/s avg" : "";
                return Log(t, QueueLogKind.Complete, $"Complete after {t.Rounds} round{(t.Rounds == 1 ? "" : "s")}, {d}{rate}.");
            case QueueTaskStatus.Blocked:
                return Log(t, QueueLogKind.Blocked, $"Blocked — {Sentence(reason ?? "needs the user")}");
            case QueueTaskStatus.Failed:
                return Log(t, QueueLogKind.Failed, $"Failed — {Sentence(reason ?? "unknown error")}");
            case QueueTaskStatus.Skipped:
                return Log(t, QueueLogKind.Skipped, "Skipped.");
            default:
                return t;
        }
    });

    /// <summary>End with a full stop unless the text already ends a sentence.</summary>
    private static string Sentence(string text)
    {
        text = text.TrimEnd();
        return text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";
    }

    // MARK: - Mutation plumbing

    private QueueTask Log(QueueTask task, QueueLogKind kind, string text) => task.AppendingLog(kind, text, Now);

    /// <summary>Index where "the front of the queue" is: before the first waiting task, or the end.</summary>
    private int FrontOfQueued()
    {
        var i = _tasks.FindIndex(t => t.Status == QueueTaskStatus.Queued);
        return i < 0 ? _tasks.Count : i;
    }

    /// <summary>Replace one task with <paramref name="change"/>'s result; a null result (or an unknown
    /// id) changes nothing.</summary>
    private bool Change(string id, Func<QueueTask, QueueTask?> change) => Mutate(() =>
    {
        var i = FindIndex(id);
        if (i < 0 || change(_tasks[i]) is not { } updated) return false;
        _tasks[i] = updated;
        return _dirty = true;
    });

    /// <summary>Run <paramref name="body"/> under the lock. The outermost call (a Batch nests the
    /// others) saves and raises <see cref="Changed"/> if anything set <c>_dirty</c> — after releasing the
    /// lock, so a slow disk or a handler never holds up readers.</summary>
    private T Mutate<T>(Func<T> body)
    {
        var publish = false;
        string? json = null;
        long version = 0;
        try
        {
            lock (_lock)
            {
                _depth++;
                try
                {
                    return body();
                }
                finally
                {
                    if (--_depth == 0 && _dirty)
                    {
                        _dirty = false;
                        publish = true;
                        version = ++_version;
                        if (FilePath is not null && !_readOnly) json = TaskQueueFile.Encode(_tasks);
                    }
                }
            }
        }
        finally
        {
            if (publish)
            {
                if (json is not null) Write(json, version);
                Changed?.Invoke();
            }
        }
    }

    // MARK: - Persistence

    /// <summary>Write one serialized version, unless a newer one already landed (two threads can leave
    /// the lock in either order).</summary>
    private void Write(string json, long version)
    {
        lock (_writeLock)
        {
            if (version <= _writtenVersion || FilePath is null) return;
            try
            {
                if (Path.GetDirectoryName(Path.GetFullPath(FilePath)) is { } dir) Directory.CreateDirectory(dir);
                var temp = FilePath + ".tmp";
                File.WriteAllText(temp, json, TextUtil.Utf8NoBom);
                File.Move(temp, FilePath, overwrite: true);
                _writtenVersion = version;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Non-fatal; the in-memory queue keeps working for this session.
            }
        }
    }

    private void Load(string path)
    {
        string text;
        try
        {
            if (!File.Exists(path)) return;   // none yet
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // There is a queue but it can't be opened right now (locked by a scanner, say). Saving over
            // it would lose it, so this session works in memory only.
            _readOnly = true;
            LoadProblem = $"The task queue file couldn't be opened ({ex.Message}), so the queue starts empty and changes won't be saved this session.";
            return;
        }

        List<QueueTask> tasks;
        try
        {
            tasks = TaskQueueFile.Decode(text, Now);
        }
        catch (System.Text.Json.JsonException)
        {
            // Never overwrite a queue we can't read: set the file aside so the tasks can be recovered,
            // and start empty.
            SetAside(path);
            return;
        }

        _tasks.AddRange(tasks);
        // A restart mid-task can't leave a task "running" in the file: the work in flight is lost, so
        // it goes back in line (in place — it was first in line when it started) and resumes in the
        // same chat.
        foreach (var t in tasks.Where(t => t.Status == QueueTaskStatus.Running))
            MarkStopped(t.Id, InterruptedNote);
    }

    private void SetAside(string path)
    {
        var stamp = Now.ToUnixTimeSeconds();
        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var aside = Path.Combine(dir, $"{stem}.unreadable-{stamp}.json");
        for (var n = 2; File.Exists(aside); n++) aside = Path.Combine(dir, $"{stem}.unreadable-{stamp}-{n}.json");
        try
        {
            File.Move(path, aside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Couldn't move it aside, so don't save over it either.
            _readOnly = true;
            LoadProblem = "The task queue file couldn't be read, so the queue starts empty and changes won't be saved this session.";
            return;
        }
        UnreadableFilePath = aside;
        LoadProblem = $"The task queue file couldn't be read, so the queue starts empty. The old file was kept as {Path.GetFileName(aside)}.";
    }
}
