using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.Core;

namespace Dsh.App.Model;

// MARK: - Task lists, /goal loop, background agents, vault
//
// Every chat has its own task list. Start works that chat's tasks one at a time, top to bottom, in the
// chat itself, until the model declares each complete (or blocked): a task runs the same goal loop /goal
// does, with "decide and move on" wording, and sees the conversation so far. Lists in different chats
// are independent — several can run at once. Everything here runs on the UI thread, like the rest of
// AgentHost.

public sealed partial class AgentHost
{
    /// <summary>Every chat's task list (saved to %APPDATA%\DSH\task-queue.json).</summary>
    public TaskQueue Queue { get; private set; } = null!;
    /// <summary>Raised on the UI thread whenever a task list changes, or starts or stops running.</summary>
    public event Action? QueueChanged;

    /// <summary>One chat's task list being worked.</summary>
    private sealed class ChatQueueRun
    {
        /// <summary>False once Stop was pressed; the task in flight may still be winding down.</summary>
        public bool Running { get; set; } = true;
        public Task? Loop { get; set; }
        /// <summary>The task being worked right now (null while waiting for the chat to be free).</summary>
        public string? ActiveTaskId { get; set; }
    }

    /// <summary>Chat id → its list's run, while it runs or winds down.</summary>
    private readonly Dictionary<string, ChatQueueRun> _chatQueues = new();
    /// <summary>Session id → the task it is working.</summary>
    private readonly Dictionary<string, string> _queueSessions = new();

    /// <summary>Some chat is working its task list.</summary>
    public bool QueueRunning => _chatQueues.Values.Any(r => r.Running);
    /// <summary>How many chats are working their task lists.</summary>
    public int QueueRunningCount => _chatQueues.Values.Count(r => r.Running);

    /// <summary><paramref name="chatId"/> is working its task list.</summary>
    public bool IsQueueRunning(string? chatId) => chatId is not null && _chatQueues.TryGetValue(chatId, out var run) && run.Running;

    /// <summary>Stop was pressed on <paramref name="chatId"/>'s list and its task in flight is still
    /// winding down; that task goes back in line when done.</summary>
    public bool IsQueueStopping(string? chatId) => chatId is not null && _chatQueues.TryGetValue(chatId, out var run) && !run.Running;

    /// <summary>The task <paramref name="chatId"/>'s list is working right now.</summary>
    public string? QueueActiveTask(string? chatId) =>
        chatId is not null && _chatQueues.TryGetValue(chatId, out var run) ? run.ActiveTaskId : null;

    /// <summary>After this many tasks in a row fail on errors, the chat's list pauses.</summary>
    public const int QueueMaxErroredTasks = 3;
    /// <summary>How many tasks one chat's agent may add to its list (queue_task).</summary>
    public const int MaxAgentQueuedTasksPerChat = 20;
    /// <summary>How many times in a row an idle chat picks itself up after background agents finish.
    /// A guard against a model that keeps relaunching agents for ever, not a limit on real work: a chat that
    /// coordinates subagents needs many hand-backs, and every message of the user's starts the count over.</summary>
    public const int MaxAutoContinuations = 25;

    /// <summary>Rounds in a row that may end because the agent kept repeating one action before a /goal gives
    /// up on it and asks the user for a different direction.</summary>
    public const int MaxStalledRounds = 3;

    /// <summary>How often a task waiting for its chat to be free looks again.</summary>
    public static TimeSpan QueueBusyPoll { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Pause before goal round n+1 after n errors (or empty replies) in a row.</summary>
    public Func<int, TimeSpan> GoalErrorBackoff { get; set; } = n => RequestRetry.Backoff(n + 1);

    // Credentials Vault
    public CredentialVault Vault { get; }
    /// <summary>Bumped whenever the vault changes, so open views reload.</summary>
    [ObservableProperty] private long _vaultRevision;
    private readonly Dictionary<string, VaultGrants> _vaultGrants = new();

    // Background agents
    private readonly Dictionary<string, BackgroundAgents> _backgroundPools = new();
    private readonly Dictionary<string, int> _autoContinuations = new();
    private readonly Dictionary<string, int> _agentQueuedCount = new();
    /// <summary>Chats the user pressed Stop in and hasn't written to since: a background agent that finishes doesn't wake them.</summary>
    private readonly HashSet<string> _stoppedChats = new();
    /// <summary>Chats whose latest run ended by reporting a failure (its hand-back of finished agents is skipped).</summary>
    private readonly HashSet<string> _failedRuns = new();

    /// <summary>Session id → the route a retry moved the run onto (see <see cref="RerouteForRetryAsync"/>).</summary>
    private readonly Dictionary<string, ProviderProfile> _retryRoute = new();

    private readonly string _queueFile;

    /// <summary>The chat title tasks from before task lists were per chat are collected under.</summary>
    public const string EarlierTasksTitle = "📋 Earlier tasks";

    private void InitQueue()
    {
        Queue = new TaskQueue(_queueFile);
        Queue.Changed += () => _dispatcher.BeginInvoke(() => QueueChanged?.Invoke());
        if (Queue.LoadProblem is { } problem) Banner = problem;
        AdoptOrphanTasks();
    }

    /// <summary>Tasks that belong to no chat — queued before task lists were per chat, or whose chat is
    /// gone — get a chat of their own (one per project folder), so they stay on a list that can run them.</summary>
    private void AdoptOrphanTasks()
    {
        var orphans = Queue.Tasks.Where(t => t.SessionId is null || Session(t.SessionId) is null).ToList();
        if (orphans.Count == 0) return;
        var homes = orphans.GroupBy(t => t.Cwd ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var vm = NewSession(group.Key.Length == 0 ? null : group.Key, select: false);
                RenameSession(vm.Id, EarlierTasksTitle);
                return (vm.Id, Tasks: group.ToList());
            })
            .ToList();
        Queue.Batch(q =>
        {
            foreach (var (chatId, tasks) in homes)
                foreach (var task in tasks)
                    q.AttachSession(task.Id, chatId);
        });
    }

    private void QueueStateChanged()
    {
        OnPropertyChanged(nameof(QueueRunning));
        OnPropertyChanged(nameof(QueueRunningCount));
        QueueChanged?.Invoke();
    }

    /// <summary>At launch: a chat's list that was running when the app quit (or crashed) resumes by itself;
    /// one the user deliberately stopped does not. Needs a configured model — a Spark that's down at
    /// launch must not fail a whole list.</summary>
    public void ResumeQueueIfNeeded()
    {
        if (!Config.IsConfigured) return;
        foreach (var chatId in Config.QueueResumeChats.ToList())
        {
            if (Session(chatId) is null || Queue.NextTaskFor(chatId) is null) Config.SetQueueResume(chatId, false);
            else StartQueue(chatId);
        }
    }

    // MARK: Editing a list

    /// <summary>Add a task to <paramref name="chatId"/>'s list (the back, or in front of its waiting tasks).</summary>
    public QueueTask QueueAdd(string chatId, string title, string details = "", bool atFront = false)
    {
        var cwd = Session(chatId)?.WorkspacePath ?? ProjectContext?.Root;
        var task = Queue.Add(title, details, atFront, cwd, chatId);
        return Queue.Find(task.Id) ?? task;
    }

    public void QueueUpdate(string id, string? title, string? details) => Queue.Update(id, title, details);

    public void QueueRemove(string id)
    {
        // A chat working this task stops it first. The store marks a running task skipped (keeping its
        // record), so the list moves on to the next task rather than pausing.
        var sessionId = _queueSessions.FirstOrDefault(p => p.Value == id).Key;
        Queue.Remove(id);
        if (sessionId is not null) StopSession(sessionId);
    }

    /// <summary>Put a blocked, failed or skipped task back in line — at the front of its chat's list —
    /// without starting it.</summary>
    public void QueueRequeue(string id)
    {
        if (Queue.Find(id) is not { } task || task.Status is QueueTaskStatus.Running or QueueTaskStatus.Queued) return;
        Queue.Requeue(id, toFront: true);
    }

    public void QueueMoveBy(string id, int offset) => Queue.MoveBy(id, offset);
    public void QueueMoveBefore(string id, string? target) => Queue.MoveBefore(id, target);
    public void QueueMoveOnto(string id, string target) => Queue.MoveOnto(id, target);

    // MARK: Running a list

    /// <summary>Work <paramref name="chatId"/>'s task list: its waiting tasks one at a time, top to bottom,
    /// in that chat, until none are left, one is blocked, or the user stops it. A list that is already
    /// running is left alone; one still winding down from a Stop picks back up once that task has stopped.</summary>
    public void StartQueue(string chatId)
    {
        if (Session(chatId) is not { } vm) return;
        if (!Config.IsConfigured)
        {
            ChatNote(vm, "No model is configured — run the setup wizard, then start the task list.", error: true);
            return;
        }
        if (IsQueueRunning(chatId)) return;
        var previous = _chatQueues.GetValueOrDefault(chatId);
        // A task still winding down from a Stop goes back in line, so a stopping list is not empty.
        if (Queue.NextTaskFor(chatId) is null && previous is null)
        {
            ChatNote(vm, "This chat's task list has nothing waiting — add a task first.");
            return;
        }
        var run = new ChatQueueRun();
        _chatQueues[chatId] = run;
        Config.SetQueueResume(chatId, true);
        QueueStateChanged();
        run.Loop = RunAfterAsync(previous?.Loop);

        async Task RunAfterAsync(Task? before)
        {
            // A loop still winding down from a Stop finishes its cleanup first, so two loops never work
            // one chat's list at once.
            if (before is not null)
            {
                try
                {
                    await before;
                }
                catch (Exception)
                {
                    // Its own problem, already reported.
                }
            }
            await QueueRunLoopAsync(chatId, run);
        }
    }

    /// <summary>Stop <paramref name="chatId"/>'s list: cancel its task in flight and put it back in line. A
    /// deliberate stop is remembered, so a relaunch doesn't resume it.</summary>
    public void StopQueue(string chatId)
    {
        if (!_chatQueues.TryGetValue(chatId, out var run) || !run.Running) return;
        run.Running = false;
        Config.SetQueueResume(chatId, false);
        QueueStateChanged();
        // Only the list's own task: a turn the user started while the list waited is theirs to stop.
        if (run.ActiveTaskId is not null) StopSession(chatId);
    }

    /// <summary>Stop every chat's list.</summary>
    public void StopAllQueues()
    {
        foreach (var chatId in _chatQueues.Keys.ToList()) StopQueue(chatId);
    }

    /// <summary>Resume a blocked, failed (or skipped) task: it goes to the front of its chat's list and the
    /// list starts — or, if it is already running, the task is next.</summary>
    public void ResumeTask(string taskId)
    {
        if (Queue.Find(taskId) is not { } task || task.Status == QueueTaskStatus.Running || task.SessionId is not { } chatId
            || Session(chatId) is not { } vm) return;
        Queue.Requeue(taskId, toFront: true);
        Hydrate(vm);
        if (IsQueueRunning(chatId)) ChatNote(vm, $"“{task.Title}” is next on this chat's task list.");
        else StartQueue(chatId);
    }

    /// <summary>True while <paramref name="run"/> is still in charge of <paramref name="chatId"/>'s list.</summary>
    private bool QueueActive(string chatId, ChatQueueRun run) =>
        run.Running && _chatQueues.TryGetValue(chatId, out var current) && ReferenceEquals(current, run);

    private enum WorkOutcome { Finished, Errored, Blocked, Stopped }

    private async Task QueueRunLoopAsync(string chatId, ChatQueueRun run)
    {
        // An unattended run must not stall because the PC went to sleep.
        var awake = KeepAwake.Begin();
        try
        {
            var erroredInARow = 0;
            while (QueueActive(chatId, run))
            {
                if (Queue.NextTaskFor(chatId) is not { } task) break;
                var outcome = await WorkAsync(chatId, task.Id, run);
                switch (outcome)
                {
                    case WorkOutcome.Stopped:
                        // Stopped from the chat (Ctrl+.) rather than with the list's Stop: pause the same way.
                        if (QueueActive(chatId, run) && !_shuttingDown)
                            ChatNote(chatId, $"Task list paused — “{task.Title}” was stopped and is back in line. Press Start to continue.");
                        return;
                    case WorkOutcome.Blocked:
                        // The chat is waiting on the user's answer; the next task would talk over it.
                        return;
                    case WorkOutcome.Errored:
                        erroredInARow++;
                        if (erroredInARow >= QueueMaxErroredTasks && QueueActive(chatId, run))
                        {
                            ChatNote(chatId, $"Task list paused — {erroredInARow} tasks in a row failed on errors. " +
                                             "Check the errors above, then press Start to continue.", error: true);
                            return;
                        }
                        break;
                    case WorkOutcome.Finished:
                        erroredInARow = 0;
                        break;
                }
            }
            if (QueueActive(chatId, run) && Queue.Stats(chatId) is { Finished: true } s)
                ChatNote(chatId, $"Task list done: {s.Completed} complete, {s.Failed} failed, {s.Blocked} blocked.");
        }
        catch (Exception error)
        {
            ChatNote(chatId, $"The task list stopped on an error: {Describe(error)}", error: true);
        }
        finally
        {
            KeepAwake.End(awake);
            if (_chatQueues.TryGetValue(chatId, out var current) && ReferenceEquals(current, run))
            {
                _chatQueues.Remove(chatId);
                // Ran dry, paused or stopped: nothing to pick up on relaunch (unless the app is quitting
                // mid-list).
                if (!_shuttingDown) Config.SetQueueResume(chatId, false);
                QueueStateChanged();
            }
        }
    }

    /// <summary>Something else is using this chat (a turn the user started, a command): the list waits
    /// rather than run two things in one chat.</summary>
    private bool IsBusy(SessionVM vm) => vm.Running || _runs.ContainsKey(vm.Id);

    /// <summary>Run one task's unattended goal loop in its chat, to a conclusion.</summary>
    private async Task<WorkOutcome> WorkAsync(string chatId, string taskId, ChatQueueRun run)
    {
        if (Session(chatId) is not { } vm) return WorkOutcome.Stopped;
        // The user may be mid-turn in the chat: their turn finishes first.
        while (IsBusy(vm))
        {
            if (!QueueActive(chatId, run) || !Sessions.Contains(vm)) return WorkOutcome.Stopped;
            await Task.Delay(QueueBusyPoll);
        }
        if (!QueueActive(chatId, run) || !Sessions.Contains(vm)) return WorkOutcome.Stopped;
        if (Queue.Find(taskId) is not { Status: QueueTaskStatus.Queued } task) return WorkOutcome.Finished;

        // A chat the user hasn't opened since launch replays its timeline first.
        Hydrate(vm);
        // A task stopped, interrupted or blocked part-way picks up where it left off — if the model ever
        // got its kickoff (one that never arrived reads "not delivered", and the task starts afresh).
        var resuming = task.HasStarted && vm.Entries.Any(e => e is MessageEntryVM { Role: MessageRole.User } m
                                                              && (m.Text == TaskKickoffDisplay(task.GoalText, false)
                                                                  || m.Text == TaskKickoffDisplay(task.GoalText, true)));
        Queue.Start(taskId, chatId);
        _queueSessions[chatId] = taskId;
        run.ActiveTaskId = taskId;
        QueueStateChanged();

        vm.Running = true;
        vm.Stopping = false;
        var cts = new CancellationTokenSource();
        _runs[chatId] = cts;
        try
        {
            await RunTaskGoalAsync(vm, taskId, resuming, cts.Token);
        }
        finally
        {
            if (_runs.TryGetValue(chatId, out var current) && ReferenceEquals(current, cts)) _runs.Remove(chatId);
            cts.Dispose();
        }

        var status = Queue.Find(taskId)?.Status;
        FinishTaskSession(vm, taskId, run);
        return status switch
        {
            QueueTaskStatus.Running => WorkOutcome.Stopped, // cancelled mid-way; FinishTaskSession requeued it
            QueueTaskStatus.Failed => WorkOutcome.Errored,
            QueueTaskStatus.Blocked => WorkOutcome.Blocked,
            _ => WorkOutcome.Finished,
        };
    }

    /// <summary>After a task's goal loop: reset the chat's live state and, if the task was cancelled
    /// mid-way, put it back in line exactly once.</summary>
    private void FinishTaskSession(SessionVM vm, string taskId, ChatQueueRun run)
    {
        vm.Running = false;
        vm.Stopping = false;
        vm.Goal = null;
        vm.Activity = null;
        vm.Retry = null;
        vm.RunningTool = null;
        vm.ClearReasoning();
        vm.EndStreaming();
        _queueSessions.Remove(vm.Id);
        // The task is over: nothing it started in the background keeps going.
        if (_backgroundPools.TryGetValue(vm.Id, out var pool)) pool.StopAll();
        if (run.ActiveTaskId == taskId) run.ActiveTaskId = null;
        Log.Touch(vm.Id);
        vm.UpdatedAt = DateTimeOffset.Now;
        SortSessions();
        if (Queue.Find(taskId)?.Status == QueueTaskStatus.Running) Queue.MarkStopped(taskId);
        QueueStateChanged();
    }

    /// <summary>Run the unattended goal loop for a task and record the outcome.</summary>
    private async Task RunTaskGoalAsync(SessionVM vm, string taskId, bool resuming, CancellationToken ct)
    {
        if (Queue.Find(taskId) is not { } task) return;
        try
        {
            var outcome = await GoalLoopAsync(vm, task.GoalText, auto: true, resuming, ct);
            // Deleted (skipped) while the last round finished: keep that verdict.
            if (Queue.Find(taskId)?.Status != QueueTaskStatus.Running) return;
            switch (outcome)
            {
                case GoalOutcome.Complete:
                    Queue.Finish(taskId, QueueTaskStatus.Complete, sessionId: vm.Id);
                    break;
                case GoalOutcome.Blocked blocked:
                    Queue.Finish(taskId, QueueTaskStatus.Blocked, blocked.Reason, vm.Id);
                    break;
                default:
                    return; // cancelled; FinishTaskSession puts it back in line
            }
            Settle(taskId, vm.Id);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user (or the chat was deleted): nothing to record.
        }
        catch (Exception error)
        {
            if (ct.IsCancellationRequested || Queue.Find(taskId)?.Status != QueueTaskStatus.Running) return;
            var why = Describe(error);
            Queue.Finish(taskId, QueueTaskStatus.Failed, why, vm.Id);
            vm.EndStreaming();
            vm.Note(why, MessageRole.Error);
            Log.RecordItem(vm.Id, "error", why, isError: true);
            Settle(taskId, vm.Id);
        }
    }

    /// <summary>Tell the chat how its task ended.</summary>
    private void Settle(string taskId, string sessionId)
    {
        if (Queue.Find(taskId) is not { } task || Session(sessionId) is not { } vm) return;
        string? text = task.Status switch
        {
            QueueTaskStatus.Complete =>
                $"✅ Task complete: “{task.Title}” — {task.Rounds} round{(task.Rounds == 1 ? "" : "s")}, {task.Duration()?.FormattedDuration() ?? "?"}"
                + (task.AvgTokensPerSecond() is { } rate ? $" · {Math.Round(rate):0} tokens/s avg" : "") + ".",
            QueueTaskStatus.Blocked =>
                $"⏸ Task blocked: “{task.Title}” — this chat's task list is paused. Answer here, then press Resume on the task (Task List panel, Ctrl+Shift+Q) to carry on.",
            QueueTaskStatus.Failed =>
                $"Task failed: “{task.Title}”. Fix what's needed in this chat, then press Resume on the task (Task List panel) to retry it.",
            _ => null,
        };
        if (text is null) return;
        ChatNote(vm, text, error: task.Status == QueueTaskStatus.Failed);
    }

    private void ChatNote(string chatId, string text, bool error = false)
    {
        if (Session(chatId) is { } vm) ChatNote(vm, text, error);
    }

    private void ChatNote(SessionVM vm, string text, bool error = false)
    {
        vm.Note(text, error ? MessageRole.Error : MessageRole.Notice);
        Log.RecordItem(vm.Id, error ? "error" : "notice", text, isError: error);
    }

    // MARK: - /goal

    /// <summary>How a goal loop ends. There is no round cap: only the model's verdict, the user's
    /// Stop, or an error retrying can't fix (thrown) end it.</summary>
    public abstract record GoalOutcome
    {
        public sealed record Complete : GoalOutcome;
        public sealed record Blocked(string Reason) : GoalOutcome;
        /// <summary>Cancelled by the user.</summary>
        public sealed record Stopped : GoalOutcome;
    }

    /// <summary>/goal: run rounds until the model writes GOAL_COMPLETE (or GOAL_BLOCKED), re-stating
    /// the goal every round so it survives compaction.</summary>
    private async Task RunGoalAsync(SessionVM vm, string goal, bool resuming, CancellationToken ct, string? reply = null,
                                    IReadOnlyList<MessageAttachment>? attachments = null)
    {
        vm.LastGoal = goal;
        vm.BlockedGoal = null;
        var outcome = await GoalLoopAsync(vm, goal, auto: false, resuming, ct, reply, attachments);
        if (outcome is GoalOutcome.Complete) vm.LastGoal = null;
        // The agent needs the user: their next message is the answer, and the goal picks up from there.
        if (outcome is GoalOutcome.Blocked) vm.BlockedGoal = goal;
        // Stopped between rounds: report it like a Stop mid-round.
        if (outcome is GoalOutcome.Stopped) throw new OperationCanceledException(ct);
    }

    /// <summary>The goal loop, interactive (/goal) or unattended (<paramref name="auto"/>, the task
    /// queue: "decide and move on" wording, and every round reports its tokens so the queue can log
    /// average speed).
    ///
    /// It keeps going round after round until the model declares the goal complete or blocked. Model
    /// outages never end it — the engine retries those until the server answers. Other errors (a
    /// request the server rejects, an overflow compaction couldn't fix) are retried as a fresh round
    /// after a pause; only <see cref="GoalProtocol.MaxConsecutiveErrors"/> of them in a row end the
    /// goal (thrown).</summary>
    private async Task<GoalOutcome> GoalLoopAsync(SessionVM vm, string goal, bool auto, bool resuming, CancellationToken ct,
                                                  string? reply = null, IReadOnlyList<MessageAttachment>? attachments = null)
    {
        var started = DateTimeOffset.Now;
        vm.Goal = new GoalState(goal, 1, started);
        var awake = KeepAwake.Begin();
        try
        {
            var round = 1;
            var kickedOff = false; // the kickoff reached the model's transcript
            var hitLimit = false;
            string? lastError = null;
            var errorsInARow = 0;
            var emptyInARow = 0; // rounds with no reply at all
            var markerMisplaced = false;
            var stalled = false;
            var stalledInARow = 0;
            while (true)
            {
                if (ct.IsCancellationRequested) return new GoalOutcome.Stopped();
                string modelText, displayText;
                if (!kickedOff)
                {
                    if (auto)
                    {
                        modelText = resuming ? GoalProtocol.ResumeAuto(goal) : GoalProtocol.KickoffAuto(goal);
                        displayText = TaskKickoffDisplay(goal, resuming);
                    }
                    else if (reply is not null)
                    {
                        // The user answered the agent's question: their words are the message.
                        modelText = GoalProtocol.ResumeWithReply(goal, reply);
                        displayText = reply;
                    }
                    else
                    {
                        modelText = resuming ? GoalProtocol.Resume(goal) : GoalProtocol.Kickoff(goal);
                        displayText = resuming ? $"🎯 /goal (resuming) {goal}" : $"🎯 /goal {goal}";
                    }
                }
                else
                {
                    modelText = auto
                        ? GoalProtocol.ContinuationAuto(goal, round, hitLimit, lastError, markerMisplaced, stalled)
                        : GoalProtocol.Continuation(goal, round, hitLimit, lastError, markerMisplaced, stalled);
                    displayText = lastError is null ? $"↻ round {round}: keep going" : $"↻ round {round}: recover and keep going";
                }

                try
                {
                    // The first round looks up notes about the goal (or the user's answer); later ones don't.
                    var result = await TurnAsync(vm, modelText, displayText, kickedOff ? [] : attachments ?? [], ct,
                        kickedOff ? null : reply ?? goal);
                    if (auto && _queueSessions.TryGetValue(vm.Id, out var taskId))
                        Queue.RecordRound(taskId, round, result.Usage?.PromptTokens ?? 0, result.Usage?.CompletionTokens ?? 0);
                    kickedOff = true;
                    errorsInARow = 0;
                    lastError = null;
                    hitLimit = result.HitIterationLimit;
                    stalled = result.Stalled;
                    stalledInARow = stalled ? stalledInARow + 1 : 0;
                    // A run cut off by the step limit is mid-work whatever it said.
                    if (!result.HitIterationLimit)
                    {
                        // The model's own signal (goal_complete / goal_blocked) first; the text markers are the
                        // fallback for models that don't call tools well.
                        var status = result.Goal ?? (stalled ? new GoalStatus.Working() : GoalProtocol.Status(result.LastReplyText));
                        switch (status)
                        {
                            case GoalStatus.Complete done:
                                if (!auto)
                                    Notice($"✅ Goal complete after {round} round{(round == 1 ? "" : "s")}." + (done.Summary is { } summary ? "\n" + summary : ""));
                                return new GoalOutcome.Complete();
                            case GoalStatus.Blocked blocked:
                                if (!auto)
                                    Notice($"⏸ Goal paused — the agent needs you: {blocked.Reason}\nReply here and it carries on by itself (or send `/goal stop` to drop it).");
                                return new GoalOutcome.Blocked(blocked.Reason);
                        }
                    }
                    // Going in circles round after round: ask for a new direction rather than spin for ever.
                    if (stalledInARow >= MaxStalledRounds)
                    {
                        const string why = "I kept repeating the same action without getting anywhere. Tell me what to try differently.";
                        if (!auto) Notice($"⏸ Goal paused — the agent was going in circles.\nReply with a new direction and it carries on (or send `/goal stop` to drop it).");
                        return new GoalOutcome.Blocked(why);
                    }
                    // Named the marker but not where it counts: ask for it plainly.
                    markerMisplaced = !result.HitIterationLimit && GoalProtocol.MentionsMarker(result.LastReplyText);
                    // A model that keeps answering with nothing gets a growing pause between rounds
                    // instead of a hot loop.
                    if (string.IsNullOrWhiteSpace(result.LastReplyText) && !result.HitIterationLimit)
                    {
                        emptyInARow++;
                        if (emptyInARow >= 2) await Task.Delay(NonNegative(GoalErrorBackoff(emptyInARow - 1)), ct);
                    }
                    else
                    {
                        emptyInARow = 0;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    // The failed kickoff may still have reached the transcript (work was salvaged):
                    // carry on with continuations, not a restart.
                    if (!kickedOff && ReachedModel(vm.Id, modelText)) kickedOff = true;
                    markerMisplaced = false;
                    errorsInARow++;
                    var why = Describe(error);
                    if (errorsInARow >= GoalProtocol.MaxConsecutiveErrors)
                    {
                        // The user's answer to a paused goal never reached the model: the goal is still waiting for it. (One that
                        // did reach it is not — the goal failed after that, and a bare /goal picks it back up.)
                        if (!kickedOff && reply is not null) vm.BlockedGoal = goal;
                        throw;
                    }
                    var wait = NonNegative(GoalErrorBackoff(errorsInARow));
                    Notice($"Round {round} failed: {why}\nThe goal carries on — trying again in {(int)wait.TotalSeconds}s " +
                           $"({errorsInARow} of {GoalProtocol.MaxConsecutiveErrors - 1} retries).", error: true);
                    if (_queueSessions.TryGetValue(vm.Id, out var taskId))
                        Queue.Note(taskId, $"Round {round} failed ({TextUtil.Prefix(why, 160)}) — retrying.");
                    // Too long for the window even after in-run compaction: fold the conversation now
                    // so the next round fits.
                    if (error is LlmException { Kind: LlmErrorKind.Overflow })
                    {
                        try
                        {
                            await CompactSessionAsync(vm, null, ct);
                        }
                        catch (Exception) when (!ct.IsCancellationRequested)
                        {
                            // The next round reports whatever is still wrong.
                        }
                    }
                    lastError = why;
                    await Task.Delay(wait, ct);
                }
                round++;
                vm.Goal = new GoalState(goal, round, started);
            }
        }
        finally
        {
            KeepAwake.End(awake);
            // Queue turns run outside RunTurnAsync, so clean up the same flags it does.
            vm.Goal = null;
            vm.Activity = null;
            vm.Retry = null;
            vm.ClearReasoning();
            vm.EndStreaming();
        }

        void Notice(string text, bool error = false)
        {
            vm.Note(text, error ? MessageRole.Error : MessageRole.Notice);
            Log.RecordItem(vm.Id, error ? "error" : "notice", text, isError: error);
        }
    }

    private static TimeSpan NonNegative(TimeSpan span) => span < TimeSpan.Zero ? TimeSpan.Zero : span;

    /// <summary>How a task's kickoff shows in its chat.</summary>
    private static string TaskKickoffDisplay(string goal, bool resuming) => resuming ? $"📋 Task (resuming): {goal}" : $"📋 Task: {goal}";

    /// <summary>The most recent /goal in a timeline (display form "🎯 /goal …"), so a bare /goal can
    /// resume it even after a relaunch.</summary>
    public static string? LastGoalIn(IEnumerable<ChatEntryVM> entries)
    {
        foreach (var entry in entries.Reverse())
        {
            if (entry is not MessageEntryVM message) continue;
            // The latest goal already finished, or was dropped on purpose: nothing to resume.
            if (message.Role == MessageRole.Notice
                && (message.Text.StartsWith("✅ Goal complete", StringComparison.Ordinal) || message.Text.StartsWith("Dropped the goal", StringComparison.Ordinal)))
                return null;
            if (message.Role != MessageRole.User) continue;
            foreach (var prefix in new[] { "🎯 /goal (resuming) ", "🎯 /goal " })
            {
                if (!message.Text.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var goal = message.Text[prefix.Length..].Trim();
                return goal.Length == 0 ? null : goal;
            }
        }
        return null;
    }

    /// <summary>The goal a chat is paused on, read from its timeline (so it survives a restart): the newest goal notice is
    /// "⏸ Goal paused", and nothing came after it — no completion, drop or Stop, and no message from the user (a message sent
    /// while the goal is paused is its answer, so the pause is over even if the app closed before the goal finished).</summary>
    public static string? PausedGoalIn(IReadOnlyList<ChatEntryVM> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (entries[i] is MessageEntryVM { Role: MessageRole.User }) return null;
            if (entries[i] is not MessageEntryVM { Role: MessageRole.Notice or MessageRole.Error } message) continue;
            if (message.Text.StartsWith("⏸ Goal paused", StringComparison.Ordinal)) return LastGoalIn(entries.Take(i));
            if (message.Text.StartsWith("✅ Goal complete", StringComparison.Ordinal)
                || message.Text.StartsWith("Dropped the goal", StringComparison.Ordinal)
                || message.Text.StartsWith("Stopped.", StringComparison.Ordinal))
                return null;
        }
        return null;
    }

    /// <summary>Turn a user entry the model never received into a notice.</summary>
    private void MarkUndelivered(SessionVM vm, string entryId)
    {
        var index = -1;
        for (var i = vm.Entries.Count - 1; i >= 0; i--)
        {
            if (vm.Entries[i].Id != entryId) continue;
            index = i;
            break;
        }
        if (index < 0 || vm.Entries[index] is not MessageEntryVM { Role: MessageRole.User } message) return;
        vm.Entries[index] = new MessageEntryVM(MessageRole.Notice, $"Not delivered to the model: {message.Text}", at: message.At);
        Log.Resync(vm.Id, LogRows(vm.Entries, vm.Id));
        vm.NotifyContentChanged();
    }

    /// <summary>Between retries of a failed model call: ask the server again what it serves (a Spark
    /// swap changes the model id; the user may have fixed the provider in Settings). Returns a client
    /// for the new route when it differs from the one the run is on.</summary>
    private async Task<(ILlmClient Client, string Model)?> RerouteForRetryAsync(string sessionId)
    {
        ProviderProfile? profile;
        try
        {
            profile = await ResolveRouteAsync(force: true);
        }
        catch (Exception)
        {
            return null;
        }
        if (profile is null) return null;
        var current = _retryRoute.TryGetValue(sessionId, out var moved) ? ProfileKey(moved)
            : _engineKeys.TryGetValue(sessionId, out var key) ? key.Profile : null;
        if (ProfileKey(profile) == current) return null;
        _retryRoute[sessionId] = profile;
        return (MakeClient(profile), profile.Model);
    }

    // MARK: - Background agents

    private BackgroundAgents BackgroundPool(string sessionId)
    {
        if (_backgroundPools.TryGetValue(sessionId, out var existing)) return existing;
        var pool = new BackgroundAgents();
        pool.Changed += job => _dispatcher.BeginInvoke(() => BackgroundJobChanged(job, sessionId));
        _backgroundPools[sessionId] = pool;
        return pool;
    }

    private void BackgroundJobChanged(BackgroundAgentJob job, string sessionId)
    {
        if (Session(sessionId) is not { } vm) return;
        vm.UpsertBackgroundJob(job);
        if (job.Status == BackgroundAgentStatus.Running) return;
        var verb = job.Status == BackgroundAgentStatus.Done ? "finished" : job.StatusWord;
        var text = $"🤖 Background agent {job.Id} “{job.Description}” {verb} after {job.Elapsed.FormattedDuration()}.";
        vm.Note(text);
        Log.RecordItem(sessionId, "notice", text);
        // A Stop never wakes the chat back up.
        if (job.Status != BackgroundAgentStatus.Stopped) ContinueAfterBackgroundAgents(vm);
    }

    /// <summary>An idle chat whose background agents have all finished picks the work back up by
    /// itself: the main agent gets their reports and carries on.</summary>
    private void ContinueAfterBackgroundAgents(SessionVM vm)
    {
        // Not while the chat is busy, waiting on the user (a paused goal, a Stop) or on a server that is switching models: the
        // reports stay unread and go to the model with whatever happens next.
        if (vm.Running || _runs.ContainsKey(vm.Id) || _queueSessions.ContainsKey(vm.Id) || vm.RunningBackgroundJobs.Count > 0
            || vm.BlockedGoal is not null || _stoppedChats.Contains(vm.Id)
            || !_backgroundPools.TryGetValue(vm.Id, out var pool) || !pool.HasUnreported
            || !pool.All.Any(j => j.Status is BackgroundAgentStatus.Done or BackgroundAgentStatus.Failed)
            || IsServerSwitching?.Invoke() == true) return;
        // At most a few automatic continuations in a row: a model that keeps relaunching agents must
        // not loop unattended forever. (Said once; the count starts over with the user's next message.)
        var continued = _autoContinuations.GetValueOrDefault(vm.Id);
        if (continued >= MaxAutoContinuations)
        {
            if (continued == MaxAutoContinuations)
            {
                _autoContinuations[vm.Id] = continued + 1;
                vm.Note("Background agents finished — reply to continue.");
            }
            return;
        }
        _autoContinuations[vm.Id] = continued + 1;
        // (The engine hands the reports over itself, at the start of the run: taken there they are put back if the run ends before
        // the model has replied, whatever ended it — a failure, a Stop.)
        const string modelText = "(Automatic — not from the user.) Your background agents finished. Their reports follow; continue the task with them.";
        StartRun(vm, ct => RunTurnAsync(vm, "🤖 Background agents finished — continuing", [], goal: null, modelText: modelText, ct, recall: false));
    }

    /// <summary>Whether a message's text is in the chat's model transcript (a turn that failed part-way may or may not have got it there).</summary>
    private bool ReachedModel(string sessionId, string modelText) =>
        _transcripts.GetValueOrDefault(sessionId) is { } transcript
        && transcript.Any(m => m.Role == Dsh.Core.MessageRole.User && m.Content?.Contains(modelText, StringComparison.Ordinal) == true);

    public void StopBackgroundAgent(string sessionId, string id)
    {
        if (_backgroundPools.TryGetValue(sessionId, out var pool) && pool.Stop(id)) HearFinishedAgentsSoon(sessionId);
    }

    public void StopBackgroundAgents(string sessionId)
    {
        if (_backgroundPools.TryGetValue(sessionId, out var pool))
        {
            pool.StopAll();
            HearFinishedAgentsSoon(sessionId);
        }
    }

    /// <summary>Stopping an agent by hand ends the wait that kept the chat from hearing the others' reports: look again once the
    /// stop has been announced (a Stopped event itself never wakes a chat).</summary>
    private void HearFinishedAgentsSoon(string sessionId) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (Session(sessionId) is { } vm) ContinueAfterBackgroundAgents(vm);
        });

    /// <summary>queue_task: the agent adds a task to its own chat's list.</summary>
    private string AgentQueueTask(string title, string details, bool front, bool start, string sessionId)
    {
        if (Session(sessionId) is not { } vm) return "Error: this chat is gone.";
        var count = _agentQueuedCount.GetValueOrDefault(sessionId);
        if (count >= MaxAgentQueuedTasksPerChat)
            return $"Error: this chat has already added {count} tasks to its list — the limit. Ask the user before adding more.";
        _agentQueuedCount[sessionId] = count + 1;
        var task = QueueAdd(sessionId, title, details, front);
        Queue.Note(task.Id, "Added by the agent.");
        var position = Queue.Position(task.Id) ?? 0;
        var output = $"Added “{task.Title}” to this chat's task list at #{position} ({Queue.QueuedCountFor(sessionId)} waiting).";
        if (IsQueueRunning(sessionId))
        {
            output += " The list is running; it gets to it in order, after the current task.";
        }
        else if (start)
        {
            StartQueue(sessionId);
            output += IsQueueRunning(sessionId)
                ? " Started the list: it begins once this turn is over."
                : " The list couldn't start (no model configured?).";
        }
        else
        {
            output += " The list isn't running — it starts when the user presses Start (or call queue_task with start: true).";
        }
        vm.Note($"📋 The agent added a task to this chat's list: “{task.Title}” (#{position}).");
        return output;
    }

    // MARK: - Vault

    private VaultGrants VaultGrantsFor(string sessionId)
    {
        if (_vaultGrants.TryGetValue(sessionId, out var existing)) return existing;
        var fresh = new VaultGrants();
        _vaultGrants[sessionId] = fresh;
        return fresh;
    }
}

/// <summary>Keeps Windows from going to sleep while unattended work runs (a goal, a task list).
/// Counted: the PC may sleep again once every holder has let go. UI thread only — the execution state
/// belongs to the thread that set it, and the UI thread lives as long as the app.</summary>
internal static class KeepAwake
{
    private static int _holders;

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    public static object Begin()
    {
        if (_holders++ == 0) Set(ExecutionState.Continuous | ExecutionState.SystemRequired);
        return new object();
    }

    public static void End(object token)
    {
        if (_holders == 0) return;
        if (--_holders == 0) Set(ExecutionState.Continuous);
    }

    private static void Set(ExecutionState state)
    {
        try
        {
            SetThreadExecutionState(state);
        }
        catch (Exception)
        {
            // Not fatal: the work just doesn't hold off sleep.
        }
    }
}

/// <summary>Tool sets that live outside the core registry.</summary>
internal static class ExtraTools
{
    /// <summary>process_start/read/write/stop/list.</summary>
    public static IEnumerable<IToolExecutor> Processes() => ProcessTools.All();

    /// <summary>Screenshots, windows, UI trees, clicks and keystrokes.</summary>
    public static IEnumerable<IToolExecutor> Machine() => Dsh.Windows.MachineTools.All();
}
