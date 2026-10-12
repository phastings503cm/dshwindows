using System.IO;
using System.Windows.Threading;
using Dsh.App.Model;
using Dsh.Core;
using MessageRole = Dsh.App.Model.MessageRole;

namespace Dsh.App.Tests;

/// <summary>Ported from QueueRunnerTests.swift, then reworked for per-chat task lists: each chat's list,
/// /goal, background agents and the vault, end to end through AgentHost against a fake model server.</summary>
public sealed class QueueRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsh-queue-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    public QueueRunnerTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Done.\nGOAL_COMPLETE"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A log file still closing; the temp folder is cleaned eventually.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // MARK: - Harness

    private string Project(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private AppConfig _config = null!;

    private AgentHost MakeHost(AppConfig? config = null)
    {
        if (config is null)
        {
            config = new AppConfig(Path.Combine(_dir, "settings.json"));
            config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
            config.Preset = PermissionPreset.FullAccess.RawValue();
            config.ComputerToolsEnabled = false;
            _config = config;
        }
        var host = new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
            GoalErrorBackoff = _ => TimeSpan.FromMilliseconds(50),
        };
        return host;
    }

    /// <summary>Run a test body on a UI thread with a fresh host adopted into the "main" project.</summary>
    private void Run(Func<AgentHost, Task> body) => UiThread.Run(async () =>
    {
        var host = MakeHost();
        host.AdoptProject(Project("main"));
        try
        {
            await body(host);
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static QueueTaskStatus? Status(AgentHost host, string id) => host.Queue.Find(id)?.Status;

    private static List<string> Notes(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role is MessageRole.Notice or MessageRole.Error).Select(m => m.Text).ToList();

    private static List<string> Replies(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role == MessageRole.Assistant).Select(m => m.Text).ToList();

    private static bool SamePath(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    // MARK: - Task lists

    private SessionVM Chat(AgentHost host, string project = "main") => host.NewSession(Project(project));

    private List<string> Kickoffs() =>
        _server.Seen.Where(r => r.Fresh && r.LastUser.Contains("GOAL: ", StringComparison.Ordinal))
            .Select(r => r.LastUser).ToList();

    [Fact]
    public void AChatWorksItsTasksInOrderInsideTheChat() => Run(async host =>
    {
        var chat = Chat(host);
        var chats = host.Sessions.Count;
        var a = host.QueueAdd(chat.Id, "Task A", "do a");
        var b = host.QueueAdd(chat.Id, "Task B");
        host.StartQueue(chat.Id);
        Assert.True(host.IsQueueRunning(chat.Id));
        Assert.True(host.QueueRunning);
        Assert.Contains(chat.Id, host.Config.QueueResumeChats);
        await WaitUntil("list done", () => !host.IsQueueRunning(chat.Id));

        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));
        var kickoffs = Kickoffs();
        Assert.Equal(2, kickoffs.Count);
        Assert.True(kickoffs[0].Contains("Task A") && kickoffs[1].Contains("Task B"), "worked in order");
        // Both ran in the chat itself — no new chats — and B saw A's work.
        Assert.Equal(chats, host.Sessions.Count);
        Assert.All(new[] { a, b }, t => Assert.Equal(chat.Id, host.Queue.Find(t.Id)?.SessionId));
        var bKickoff = _server.Seen.First(r => r.KickoffOf("Task B"));
        Assert.Contains("GOAL: Task A", bKickoff.AllUserText);
        Assert.Contains(chat.Entries.OfType<MessageEntryVM>(), m => m.Role == MessageRole.User && m.Text.StartsWith("📋 Task: Task A", StringComparison.Ordinal));
        Assert.Contains(Notes(chat), n => n.StartsWith("✅ Task complete: “Task A”", StringComparison.Ordinal));
        Assert.Contains(Notes(chat), n => n.StartsWith("Task list done: 2 complete", StringComparison.Ordinal));
        Assert.False(chat.Running);
        Assert.Equal(1, host.Queue.Find(a.Id)?.Rounds);
        Assert.Equal(120, host.Queue.Find(a.Id)?.TotalTokens);
        Assert.DoesNotContain(chat.Id, host.Config.QueueResumeChats);
        Assert.False(host.QueueRunning);
        // The file on disk matches.
        var onDisk = new TaskQueue(Path.Combine(_dir, "task-queue.json"));
        Assert.Equal(new[] { QueueTaskStatus.Complete, QueueTaskStatus.Complete }, onDisk.TasksFor(chat.Id).Select(t => t.Status));
    });

    [Fact]
    public void EachChatOnlyRunsItsOwnList() => Run(async host =>
    {
        var x = Chat(host);
        var y = Chat(host);
        var x1 = host.QueueAdd(x.Id, "X1");
        var y1 = host.QueueAdd(y.Id, "Y1");
        var x2 = host.QueueAdd(x.Id, "X2");
        Assert.Equal(new[] { x1.Id, x2.Id }, host.Queue.TasksFor(x.Id).Select(t => t.Id));
        Assert.Equal(new[] { y1.Id }, host.Queue.TasksFor(y.Id).Select(t => t.Id));
        Assert.Equal(2, host.Queue.Position(x2.Id));
        Assert.Equal(1, host.Queue.Position(y1.Id));

        host.StartQueue(x.Id);
        Assert.False(host.IsQueueRunning(y.Id));
        await WaitUntil("x done", () => !host.IsQueueRunning(x.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, x1.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, x2.Id));
        Assert.Equal(QueueTaskStatus.Queued, Status(host, y1.Id)); // another chat's list waits for its own Start
        Assert.DoesNotContain(_server.Seen, r => r.WorkingOn("Y1"));
        Assert.Empty(Notes(y));
    });

    [Fact]
    public void TwoChatsListsRunSideBySide() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromMilliseconds(400), "Done.\nGOAL_COMPLETE"));
        var x = Chat(host);
        var y = Chat(host);
        var x1 = host.QueueAdd(x.Id, "X1");
        var y1 = host.QueueAdd(y.Id, "Y1");
        host.StartQueue(x.Id);
        host.StartQueue(y.Id);
        Assert.Equal(2, host.QueueRunningCount);
        await WaitUntil("both working", () => Status(host, x1.Id) == QueueTaskStatus.Running && Status(host, y1.Id) == QueueTaskStatus.Running);
        Assert.True(x.Running && y.Running);
        await WaitUntil("both done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, x1.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, y1.Id));
        Assert.Equal(0, host.QueueRunningCount);
    });

    [Fact]
    public void TaskKeepsGoingPastTheOldRoundCapUntilTheModelSaysComplete() => Run(async host =>
    {
        _server.Reset((_, n) => n < 60 ? new FakeReply.Text("Still working on it.") : new FakeReply.Text("All verified.\nGOAL_COMPLETE"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Long task");
        host.StartQueue(chat.Id);
        await WaitUntil("list done", () => !host.QueueRunning, 60);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(60, host.Queue.Find(t.Id)?.Rounds);
    });

    [Fact]
    public void ModelOutageMidTaskIsRetriedAndTheTaskFinishes() => Run(async host =>
    {
        _server.Reset((_, n) => n switch
        {
            1 => new FakeReply.Transport(Timeout: true),
            2 => new FakeReply.Http(503, """{"error":"loading"}"""),
            3 => new FakeReply.Transport(),
            _ => new FakeReply.Text("Recovered.\nGOAL_COMPLETE"),
        });
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Survive an outage");
        host.StartQueue(chat.Id);
        await WaitUntil("list done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(1, host.Queue.Find(t.Id)?.Rounds); // retries are not extra rounds
        var log = host.Queue.Find(t.Id)?.Log.Select(l => l.Text).ToList() ?? [];
        Assert.Contains(log, l => l.Contains("Model unavailable"));
        Assert.Contains(log, l => l.Contains("answering again"));
        Assert.Single(Replies(chat), r => r.Contains("Recovered."));
        Assert.Null(chat.Retry);
    });

    [Fact]
    public void StopPutsTheTaskBackAndStartPicksItUp() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(5), "late") : new FakeReply.Text("Done.\nGOAL_COMPLETE"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Stoppable");
        host.StartQueue(chat.Id);
        await WaitUntil("request in flight", () => _server.Seen.Count == 1);
        Assert.Equal(t.Id, host.QueueActiveTask(chat.Id));
        host.StopQueue(chat.Id);
        Assert.True(host.IsQueueStopping(chat.Id));
        Assert.False(host.IsQueueRunning(chat.Id));
        await WaitUntil("stopped", () => !host.IsQueueStopping(chat.Id));
        Assert.Equal(QueueTaskStatus.Queued, Status(host, t.Id));
        Assert.DoesNotContain(chat.Id, host.Config.QueueResumeChats);
        Assert.False(chat.Running);

        host.StartQueue(chat.Id);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        // The stopped attempt never got an answer, so nothing reached the model: the restart is a clean
        // kickoff, and the chat says the first one wasn't delivered.
        Assert.StartsWith("GOAL: Stoppable", _server.Seen[^1].LastUser);
        Assert.Equal(1, _server.Seen[^1].Messages.Count(m => m.Role == "user"));
        Assert.Contains(Notes(chat), n => n.StartsWith("Not delivered to the model", StringComparison.Ordinal));
    });

    [Fact]
    public void StartStopStartInOneBreathRunsEachTaskOnce() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromMilliseconds(300), "Done.\nGOAL_COMPLETE"));
        var chat = Chat(host);
        var chats = host.Sessions.Count;
        var t = host.QueueAdd(chat.Id, "Race");
        var other = host.QueueAdd(chat.Id, "Next");
        host.StartQueue(chat.Id);
        host.StopQueue(chat.Id);
        host.StartQueue(chat.Id);
        host.StopQueue(chat.Id);
        host.StartQueue(chat.Id);
        await WaitUntil("done", () => !host.QueueRunning && !host.IsQueueStopping(chat.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, other.Id));
        Assert.Equal(1, Kickoffs().Count(k => k.Contains("GOAL: Next")));
        Assert.Equal(chats, host.Sessions.Count);
        Assert.False(chat.Running);
        Assert.DoesNotContain(host.Queue.Tasks, task => task.Status == QueueTaskStatus.Running);
    });

    [Fact]
    public void StartWhileTheStoppedTaskIsStillUnwindingRunsItOnceMore() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(5), "late") : new FakeReply.Text("GOAL_COMPLETE"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Unwinding");
        host.StartQueue(chat.Id);
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        host.StopQueue(chat.Id);
        host.StartQueue(chat.Id); // before the old run has let go of the task
        Assert.True(host.IsQueueRunning(chat.Id));
        await WaitUntil("done", () => !host.QueueRunning && !host.IsQueueStopping(chat.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(2, _server.Seen.Count);
    });

    [Fact]
    public void StoppingTheTaskFromTheChatPausesTheList() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromSeconds(5), "late"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "A");
        host.QueueAdd(chat.Id, "B");
        host.StartQueue(chat.Id);
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        host.StopSession(chat.Id);
        await WaitUntil("paused", () => !host.IsQueueRunning(chat.Id) && !host.IsQueueStopping(chat.Id));
        Assert.Equal(QueueTaskStatus.Queued, Status(host, t.Id));
        Assert.Equal(2, host.Queue.TasksFor(chat.Id).Count(task => task.Status == QueueTaskStatus.Queued));
        Assert.Contains(Notes(chat), n => n.StartsWith("Task list paused — “A” was stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Id, host.Config.QueueResumeChats);
    });

    [Fact]
    public void DeletingTheRunningTaskSkipsToTheNextOne() => Run(async host =>
    {
        _server.Reset((request, _) => request.WorkingOn("A")
            ? new FakeReply.Slow(TimeSpan.FromSeconds(5), "late")
            : new FakeReply.Text("GOAL_COMPLETE"));
        var chat = Chat(host);
        var a = host.QueueAdd(chat.Id, "A");
        var b = host.QueueAdd(chat.Id, "B");
        host.StartQueue(chat.Id);
        await WaitUntil("A in flight", () => Status(host, a.Id) == QueueTaskStatus.Running && _server.Seen.Count == 1);
        host.QueueRemove(a.Id);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Skipped, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));
    });

    [Fact]
    public void ErrorsFailATaskAndThreeInARowPauseTheList() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Http(400, """{"error":{"message":"bad request","code":400}}"""));
        var chat = Chat(host);
        var ids = Enumerable.Range(1, 4).Select(i => host.QueueAdd(chat.Id, $"T{i}").Id).ToList();
        host.StartQueue(chat.Id);
        await WaitUntil("paused", () => !host.QueueRunning, 30);
        Assert.Equal(new QueueTaskStatus?[] { QueueTaskStatus.Failed, QueueTaskStatus.Failed, QueueTaskStatus.Failed, QueueTaskStatus.Queued },
            ids.Select(id => Status(host, id)));
        Assert.Contains(Notes(chat), n => n.StartsWith("Task list paused — 3 tasks in a row failed", StringComparison.Ordinal));
        // Each task tried its goal several rounds before giving up.
        Assert.Equal(3 * GoalProtocol.MaxConsecutiveErrors, _server.Seen.Count);
    });

    [Fact]
    public void ANonTransientErrorRoundIsRetriedAndTheGoalCarriesOn() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1
            ? new FakeReply.Http(400, """{"error":{"message":"template error","code":400}}""")
            : new FakeReply.Text("Fine now.\nGOAL_COMPLETE"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Flaky request");
        host.StartQueue(chat.Id);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        // Round 1 never got going, so round 2 re-sends the kickoff.
        Assert.StartsWith("GOAL:", _server.Seen[^1].LastUser);
    });

    [Fact]
    public void ABlockedTaskPausesTheListUntilItIsResumed() => Run(async host =>
    {
        _server.Reset((request, _) =>
            request.WorkingOn("A") && !request.LastUser.StartsWith("[Resuming]", StringComparison.Ordinal)
                ? new FakeReply.Text("I need the API key.\nGOAL_BLOCKED: the API key")
                : new FakeReply.Text("GOAL_COMPLETE"));
        var chat = Chat(host);
        var a = host.QueueAdd(chat.Id, "A");
        var b = host.QueueAdd(chat.Id, "B");
        host.StartQueue(chat.Id);
        await WaitUntil("paused on A", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Blocked, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Queued, Status(host, b.Id)); // B doesn't talk over A's question
        Assert.Contains(Notes(chat), n => n.StartsWith("⏸ Task blocked: “A”", StringComparison.Ordinal));
        Assert.DoesNotContain(chat.Id, host.Config.QueueResumeChats);

        host.ResumeTask(a.Id);
        Assert.True(host.IsQueueRunning(chat.Id));
        await WaitUntil("list done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));
        var kickoffs = Kickoffs();
        Assert.StartsWith("[Resuming]", kickoffs[1]); // A picked up where it stopped, in the same chat
        Assert.Contains("GOAL: A", kickoffs[1]);
        Assert.Contains("GOAL: B", kickoffs[2]);
    });

    [Fact]
    public void ResumeWhileTheListRunsPutsTheTaskNext() => Run(async host =>
    {
        _server.Reset((request, _) =>
        {
            if (request.WorkingOn("A") && !request.LastUser.StartsWith("[Resuming]", StringComparison.Ordinal))
                return new FakeReply.Text("GOAL_BLOCKED: need input");
            if (request.WorkingOn("B")) return new FakeReply.Slow(TimeSpan.FromMilliseconds(500), "GOAL_COMPLETE");
            return new FakeReply.Text("GOAL_COMPLETE");
        });
        var chat = Chat(host);
        var a = host.QueueAdd(chat.Id, "A");
        var b = host.QueueAdd(chat.Id, "B");
        var c = host.QueueAdd(chat.Id, "C");
        host.StartQueue(chat.Id);
        await WaitUntil("paused on A", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Blocked, Status(host, a.Id));
        // Start again without answering: B (the next waiting task) runs; A is resumed while it does.
        host.StartQueue(chat.Id);
        await WaitUntil("B running", () => Status(host, b.Id) == QueueTaskStatus.Running);
        host.ResumeTask(a.Id);
        Assert.Equal(QueueTaskStatus.Queued, Status(host, a.Id));
        Assert.Equal(a.Id, host.Queue.NextTaskFor(chat.Id)?.Id);
        Assert.Contains(Notes(chat), n => n == "“A” is next on this chat's task list.");
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(new QueueTaskStatus?[] { QueueTaskStatus.Complete, QueueTaskStatus.Complete, QueueTaskStatus.Complete },
            new[] { a, b, c }.Select(t => Status(host, t.Id)));
        var order = Kickoffs().Select(k => k.Contains("GOAL: A") ? "A" : k.Contains("GOAL: B") ? "B" : "C").ToList();
        Assert.Equal(["A", "B", "A", "C"], order);
        Assert.False(chat.Running);
    });

    [Fact]
    public void AFailedTaskResumesInItsChatWithItsHistory() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Http(400, """{"error":{"message":"bad","code":400}}"""));
        var chat = Chat(host);
        var a = host.QueueAdd(chat.Id, "A");
        host.StartQueue(chat.Id);
        await WaitUntil("A failed", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Failed, Status(host, a.Id));
        Assert.Contains(Notes(chat), n => n.StartsWith("Task failed: “A”", StringComparison.Ordinal));
        var storedBefore = host.Log.LoadItems(chat.Id).Count;

        _server.Reset((_, _) => new FakeReply.Text("GOAL_COMPLETE"));
        host.ResumeTask(a.Id);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        Assert.Equal(chat.Id, host.Queue.Find(a.Id)?.SessionId);
        Assert.True(host.Log.LoadItems(chat.Id).Count > storedBefore); // the history grew rather than being replaced
    });

    [Fact]
    public void TheListWaitsForTheUsersOwnTurn() => Run(async host =>
    {
        _server.Reset((request, _) => request.LastUser == "my question"
            ? new FakeReply.Slow(TimeSpan.FromMilliseconds(500), "My answer.")
            : new FakeReply.Text("GOAL_COMPLETE"));
        var chat = Chat(host);
        host.Send("my question", chat.Id);
        var t = host.QueueAdd(chat.Id, "After you");
        host.StartQueue(chat.Id);
        Assert.True(host.IsQueueRunning(chat.Id));
        Assert.Equal(QueueTaskStatus.Queued, Status(host, t.Id)); // waits for the turn
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        var kickoff = _server.Seen.Single(r => r.KickoffOf("After you"));
        Assert.Contains(kickoff.Messages, m => m.Role == "assistant" && m.Content == "My answer.");
    });

    [Fact]
    public void QueueCommandStartsThisChatsList() => Run(async host =>
    {
        var chat = Chat(host);
        host.Send("/queue", chat.Id);
        Assert.Contains(Notes(chat), n => n.StartsWith("This chat's task list is empty", StringComparison.Ordinal));
        var t = host.QueueAdd(chat.Id, "Via slash");
        host.Send("/queue", chat.Id);
        Assert.True(host.IsQueueRunning(chat.Id));
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
    });

    [Fact]
    public void DeletingAChatDeletesItsListAndStopsIt() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromSeconds(5), "late"));
        var chat = Chat(host);
        var keep = Chat(host);
        host.QueueAdd(chat.Id, "A");
        host.QueueAdd(chat.Id, "B");
        var other = host.QueueAdd(keep.Id, "Kept");
        host.StartQueue(chat.Id);
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        host.DeleteSession(chat.Id);
        await WaitUntil("stopped", () => !host.QueueRunning && !host.IsQueueStopping(chat.Id));
        Assert.Empty(host.Queue.TasksFor(chat.Id));
        Assert.Equal(new[] { other.Id }, host.Queue.Tasks.Select(t => t.Id));
        Assert.DoesNotContain(chat.Id, host.Config.QueueResumeChats);
    });

    [Fact]
    public void TasksFromTheOldGlobalQueueMoveIntoAChat() => UiThread.Run(async () =>
    {
        // A queue file from before lists were per chat: no chats on its tasks.
        var old = new TaskQueue(Path.Combine(_dir, "task-queue.json"));
        var o1 = old.Add("Old one", cwd: Project("main"));
        var o2 = old.Add("Old two", cwd: Project("main"));
        var elsewhere = old.Add("Other project", cwd: Project("other"));
        var host = MakeHost();
        try
        {
            var homes = host.Sessions.Where(s => s.Title == AgentHost.EarlierTasksTitle).ToList();
            Assert.Equal(2, homes.Count); // one per project folder
            var main = homes.Single(s => SamePath(s.Cwd, Project("main")));
            Assert.Equal(new[] { o1.Id, o2.Id }, host.Queue.TasksFor(main.Id).Select(t => t.Id));
            Assert.Equal(new[] { elsewhere.Id }, host.Queue.TasksFor(homes.Single(s => s != main).Id).Select(t => t.Id));
            // ...and they run there.
            host.StartQueue(main.Id);
            await WaitUntil("done", () => !host.QueueRunning);
            Assert.Equal(QueueTaskStatus.Complete, Status(host, o2.Id));
            // A relaunch doesn't make more chats.
            var relaunched = MakeHost(_config);
            Assert.Equal(2, relaunched.Sessions.Count(s => s.Title == AgentHost.EarlierTasksTitle));
        }
        finally
        {
            host.StopAll();
            await Task.Delay(50);
        }
    });

    [Fact]
    public void RelaunchPutsAnInterruptedTaskBackAndItResumes() => UiThread.Run(async () =>
    {
        var host = MakeHost();
        host.AdoptProject(Project("main"));
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(30), "late") : new FakeReply.Text("GOAL_COMPLETE"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Interrupted");
        host.StartQueue(chat.Id);
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        // Simulate a quit: a second host reads the same files.
        var relaunched = MakeHost(_config);
        Assert.Equal(QueueTaskStatus.Queued, relaunched.Queue.Find(t.Id)?.Status);
        Assert.Equal(chat.Id, relaunched.Queue.Find(t.Id)?.SessionId);
        host.StopAll(); // the old process is gone
        await WaitUntil("old stopped", () => !host.QueueRunning && !host.IsQueueStopping(chat.Id));
        relaunched.StartQueue(chat.Id);
        await WaitUntil("relaunched done", () => !relaunched.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, relaunched.Queue.Find(t.Id)?.Status);
        Assert.Equal(chat.Id, relaunched.Queue.Find(t.Id)?.SessionId);
        relaunched.StopAll();
        await Task.Delay(50);
    });

    [Fact]
    public void AnInterruptedListResumesOnLaunchButAStoppedOneDoesNot() => UiThread.Run(async () =>
    {
        var host = MakeHost();
        host.AdoptProject(Project("main"));
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(30), "late") : new FakeReply.Text("GOAL_COMPLETE"));
        var chat = Chat(host);
        var t = host.QueueAdd(chat.Id, "Interrupted");
        host.StartQueue(chat.Id);
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        // Quitting the app is not the user pressing Stop.
        host.Shutdown();
        await WaitUntil("old stopped", () => host.Queue.Find(t.Id)?.Status == QueueTaskStatus.Queued && !host.QueueRunning && !chat.Running);
        Assert.Contains(chat.Id, host.Config.QueueResumeChats);
        var relaunched = MakeHost(_config);
        relaunched.ResumeQueueIfNeeded();
        Assert.True(relaunched.IsQueueRunning(chat.Id),
            $"chat={relaunched.Session(chat.Id) is not null} status={relaunched.Queue.Find(t.Id)?.Status} " +
            $"notes={string.Join(" | ", relaunched.Session(chat.Id) is { } again ? Notes(again) : [])}");
        await WaitUntil("relaunched done", () => !relaunched.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, relaunched.Queue.Find(t.Id)?.Status);
        Assert.DoesNotContain(chat.Id, relaunched.Config.QueueResumeChats);

        // A list the user stopped stays stopped.
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromSeconds(30), "late"));
        relaunched.QueueAdd(chat.Id, "Later");
        relaunched.StartQueue(chat.Id);
        await WaitUntil("later in flight", () => _server.Seen.Count == 1);
        relaunched.StopQueue(chat.Id);
        relaunched.Shutdown();
        await WaitUntil("later stopped", () => !relaunched.IsQueueStopping(chat.Id));
        var third = MakeHost(_config);
        third.ResumeQueueIfNeeded();
        Assert.False(third.QueueRunning);
    });

    // MARK: - The panel

    private AppModel PanelModel(AgentHost host) =>
        new(host.Config, host.Log, Dispatcher.CurrentDispatcher, host);

    [Fact]
    public void AddingATaskSurvivesRefreshesWhileTyping() => Run(async host =>
    {
        var chat = Chat(host);
        var other = Chat(host);
        host.SelectedId = chat.Id;
        var panel = new Dsh.App.Views.QueuePanel(PanelModel(host));
        panel.BeginAdd();
        panel.NewTitleBox.Text = "Write the docs";
        panel.NewDetailsBox.Text = "All of them.";
        // The clock ticks, another chat's list changes, the queue runs: every refresh used to re-parent
        // the form's boxes and throw.
        panel.Refresh();
        panel.Refresh();
        host.QueueAdd(other.Id, "Someone else's");
        await Task.Delay(100);
        panel.Refresh();
        Assert.True(panel.AddFormShowing);
        Assert.Equal("Write the docs", panel.NewTitleBox.Text);
        Assert.Equal("All of them.", panel.NewDetailsBox.Text);

        panel.SubmitAdd();
        var added = Assert.Single(host.Queue.TasksFor(chat.Id));
        Assert.Equal(("Write the docs", "All of them."), (added.Title, added.Details));
        Assert.False(panel.AddFormShowing);
        Assert.Equal("", panel.NewTitleBox.Text);
        Assert.Equal(new[] { added.Id }, panel.RowIds); // only this chat's list shows

        // Another chat: its own list.
        host.SelectedId = other.Id;
        Assert.Equal(host.Queue.TasksFor(other.Id).Select(t => t.Id), panel.RowIds);
    });

    [Fact]
    public void EditingATaskSurvivesRefreshes() => Run(async host =>
    {
        var chat = Chat(host);
        host.SelectedId = chat.Id;
        var task = host.QueueAdd(chat.Id, "Edit me", "old");
        var panel = new Dsh.App.Views.QueuePanel(PanelModel(host));
        panel.EditTask(task.Id);
        Assert.True(panel.EditFormShowing);
        panel.EditDetailsBox.Text = "new instructions";
        host.QueueAdd(chat.Id, "Another");
        await Task.Delay(100);
        panel.Refresh();
        Assert.True(panel.EditFormShowing);
        Assert.Equal("new instructions", panel.EditDetailsBox.Text); // not reset to "old"
        panel.SubmitEdit();
        Assert.Equal("new instructions", host.Queue.Find(task.Id)?.Details);
        Assert.False(panel.EditFormShowing);
    });

    // MARK: - Vault

    [Fact]
    public void VaultSecretReachesTheToolButNeverTheModelOrTheLogs() => Run(async host =>
    {
        const string secret = "tok-SECRET-9f8e7d6c";
        host.Config.AgentShell = AgentShell.CmdPreference;
        host.Vault.Add("DEPLOY_TOKEN", secret, description: "Deploys");
        _server.Reset((_, n) => n == 1
            ? new FakeReply.ToolCall("run_shell_command", """{"command":"echo token={{vault:DEPLOY_TOKEN}}"}""")
            : new FakeReply.Text("Deployed."));
        var vm = host.NewSession(Project("main"));
        host.Send("deploy it", vm.Id);
        await WaitUntil("answered", () => !vm.Running && _server.Seen.Count == 2, 30);

        Assert.Contains("{{vault:DEPLOY_TOKEN}}", _server.Seen[0].System); // the prompt lists the credential
        var toolResult = _server.Seen[1].Messages.FirstOrDefault(m => m.Role == "tool").Content ?? "";
        Assert.Contains("token=[vault:DEPLOY_TOKEN]", toolResult);
        foreach (var request in _server.Seen)
            Assert.DoesNotContain(request.Messages, m => m.Content?.Contains(secret) == true); // the model never sees it
        foreach (var entry in vm.Entries)
        {
            var shown = entry switch
            {
                MessageEntryVM m => m.Text,
                ToolEntryVM tool => $"{tool.Preview} {tool.Summary} {tool.Output}",
                _ => "",
            };
            Assert.DoesNotContain(secret, shown); // not on screen
        }
        foreach (var file in Directory.EnumerateFiles(Path.Combine(_dir, "log"), "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(secret, File.ReadAllText(file));
        Assert.Equal(1, host.Vault.Entry("DEPLOY_TOKEN")?.UseCount);
    });

    // MARK: - Background agents & tasks

    [Fact]
    public void AgentAddsATaskToItsChatsListThatRunsAfterTheTurn() => Run(async host =>
    {
        _server.Reset((request, n) =>
        {
            if (request.WorkingOn("Write parser tests")) return new FakeReply.Text("Wrote the tests.\nGOAL_COMPLETE");
            if (n == 1 && request.Fresh)
                return new FakeReply.ToolCall("queue_task", """{"title":"Write parser tests","details":"Cover the edge cases.","start":true}""");
            return new FakeReply.Text("Queued it.");
        });
        var vm = host.NewSession(Project("main"));
        var chats = host.Sessions.Count;
        host.Send("queue the tests for later", vm.Id);
        await WaitUntil("task done", () =>
            host.Queue.Tasks.FirstOrDefault()?.Status == QueueTaskStatus.Complete && !host.QueueRunning && !vm.Running);
        var task = host.Queue.Tasks[0];
        Assert.Equal("Write parser tests", task.Title);
        Assert.Equal(vm.Id, task.SessionId); // on this chat's list, run in this chat
        Assert.Equal(chats, host.Sessions.Count);
        Assert.True(SamePath(Project("main"), task.Cwd), task.Cwd);
        Assert.Contains(task.Log, l => l.Text.Contains("Added by the agent"));
        Assert.Contains(Notes(vm), n => n.Contains("The agent added a task to this chat's list"));
        Assert.Contains(Replies(vm), r => r.Contains("Wrote the tests."));
        // It began only once the turn that added it was over.
        var kickoff = _server.Seen.Single(r => r.KickoffOf("Write parser tests"));
        Assert.Contains(kickoff.Messages, m => m.Role == "assistant" && m.Content == "Queued it.");
    });

    [Fact]
    public void BackgroundAgentFinishesAndTheIdleChatCarriesOn() => Run(async host =>
    {
        _server.Reset((request, _) =>
        {
            if (request.System.StartsWith("You are a focused subagent", StringComparison.Ordinal))
                return new FakeReply.Slow(TimeSpan.FromMilliseconds(300), "Subagent report: 7 call sites.");
            if (request.LastUser.Contains("Your background agents finished")) return new FakeReply.Text("Using the report: 7 call sites.");
            if (request.LastUser == "find call sites in the background" && request.Fresh)
                return new FakeReply.ToolCall("agent", """{"description":"call sites","prompt":"Find call sites of foo","run_in_background":true}""");
            return new FakeReply.Text("Started it; I'll pick up the results when it's done.");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("find call sites in the background", vm.Id);
        await WaitUntil("auto-continued", () =>
            !vm.Running && _server.Seen.Any(r => r.LastUser.Contains("Your background agents finished"))
            && Replies(vm).Contains("Using the report: 7 call sites."));
        var auto = _server.Seen.First(r => r.LastUser.Contains("Your background agents finished"));
        Assert.Contains("Subagent report: 7 call sites.", auto.LastUser);
        Assert.Contains(Notes(vm), n => n.Contains("Background agent bg-1 “call sites” finished"));
        Assert.Equal(BackgroundAgentStatus.Done, vm.BackgroundJobs.FirstOrDefault()?.Status);
    });

    [Fact]
    public void StopStopsTheChatsBackgroundAgents() => Run(async host =>
    {
        _server.Reset((request, _) =>
        {
            if (request.System.StartsWith("You are a focused subagent", StringComparison.Ordinal))
                return new FakeReply.Slow(TimeSpan.FromSeconds(10), "late");
            if (request.LastUser == "go" && request.Fresh)
                return new FakeReply.ToolCall("agent", """{"description":"slow","prompt":"take forever","run_in_background":true}""");
            return new FakeReply.Text("waiting");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("go", vm.Id);
        await WaitUntil("background running", () => !vm.Running && vm.RunningBackgroundJobs.Count == 1);
        Assert.True(host.AnythingRunning);
        host.StopSession(vm.Id);
        await WaitUntil("stopped", () => vm.RunningBackgroundJobs.Count == 0);
        Assert.Equal(BackgroundAgentStatus.Stopped, vm.BackgroundJobs.FirstOrDefault()?.Status);
        Assert.False(vm.Stopping);
    });

    // MARK: - /goal

    [Fact]
    public void GoalRunsPastFortyRoundsUntilComplete() => Run(async host =>
    {
        _server.Reset((_, n) => n < 45 ? new FakeReply.Text($"progress {n}") : new FakeReply.Text("Verified.\nGOAL_COMPLETE"));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal build the thing", vm.Id);
        await WaitUntil("goal done", () => !vm.Running && _server.Seen.Count >= 45, 60);
        Assert.Contains(Notes(vm), n => n.Contains("Goal complete after 45 rounds"));
        Assert.Null(vm.Goal);
    });

    [Fact]
    public void GoalSurvivesAModelOutage() => Run(async host =>
    {
        _server.Reset((_, n) => n <= 3 ? new FakeReply.Transport() : new FakeReply.Text("GOAL_COMPLETE"));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal ship it", vm.Id);
        await WaitUntil("goal done", () => !vm.Running && _server.Seen.Count >= 4);
        Assert.Contains(Notes(vm), n => n.Contains("Goal complete after 1 round"));
        Assert.Contains(Notes(vm), n => n.Contains("Retrying automatically"));
    });

    [Fact]
    public void StopDuringAnOutageEndsTheGoal() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Transport());
        var vm = host.NewSession(Project("main"));
        host.Send("/goal never answered", vm.Id);
        await WaitUntil("retrying", () => vm.Retry is not null);
        host.StopSession(vm.Id);
        await WaitUntil("stopped", () => !vm.Running);
        Assert.Null(vm.Retry);
        Assert.StartsWith("Stopped.", Notes(vm)[^1]);
    });

    [Fact]
    public void BareGoalResumesTheUnfinishedGoal() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Text("GOAL_BLOCKED: which branch?"));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal merge the branch", vm.Id);
        await WaitUntil("blocked", () => !vm.Running && _server.Seen.Count == 1);
        Assert.Contains(Notes(vm), n => n.Contains("which branch"));

        _server.Reset((_, _) => new FakeReply.Text("GOAL_COMPLETE"));
        host.Send("/goal", vm.Id);
        await WaitUntil("resumed", () => !vm.Running && _server.Seen.Count == 1);
        Assert.StartsWith("[Resuming]", _server.Seen[0].LastUser);
        Assert.Contains("merge the branch", _server.Seen[0].LastUser);
        Assert.Contains(Notes(vm), n => n.Contains("Goal complete"));
    });

    [Fact]
    public void PlainTurnRetriesATimeoutToo() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1 ? new FakeReply.Transport(Timeout: true) : new FakeReply.Text("hello back"));
        var vm = host.NewSession(Project("main"));
        host.Send("hello", vm.Id);
        await WaitUntil("answered", () => !vm.Running && _server.Seen.Count == 2);
        Assert.Equal(new[] { "hello back" }, Replies(vm));
    });

    [Fact]
    public void LastGoalIsReadBackFromTheTimeline()
    {
        var entries = new ChatEntryVM[]
        {
            new MessageEntryVM(MessageRole.User, "🎯 /goal ship the release"),
            new MessageEntryVM(MessageRole.Assistant, "working"),
        };
        Assert.Equal("ship the release", AgentHost.LastGoalIn(entries));
        Assert.Equal("fix it", AgentHost.LastGoalIn([new MessageEntryVM(MessageRole.User, "🎯 /goal (resuming) fix it")]));
        Assert.Null(AgentHost.LastGoalIn([.. entries, new MessageEntryVM(MessageRole.Notice, "✅ Goal complete after 2 rounds.")]));
        Assert.Null(AgentHost.LastGoalIn([new MessageEntryVM(MessageRole.User, "just chatting")]));
    }
}
