using System.IO;
using System.Windows.Threading;
using Dsh.App.Model;
using Dsh.Core;
using MessageRole = Dsh.App.Model.MessageRole;

namespace Dsh.App.Tests;

/// <summary>Ported from QueueRunnerTests.swift: the task queue, /goal, background agents and the vault,
/// end to end through AgentHost against a fake model server.</summary>
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

    // MARK: - Queue

    [Fact]
    public void QueueWorksTasksInOrderToCompletion() => Run(async host =>
    {
        var a = host.QueueAdd("Task A", "do a");
        var b = host.QueueAdd("Task B");
        host.StartQueue();
        Assert.True(host.QueueRunning);
        await WaitUntil("queue done", () => !host.QueueRunning);

        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));
        var kickoffs = _server.Seen.Select(r => r.LastUser).Where(u => u.StartsWith("GOAL:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, kickoffs.Count);
        Assert.True(kickoffs[0].Contains("Task A") && kickoffs[1].Contains("Task B"), "worked in order");
        var sa = host.Queue.Find(a.Id)?.SessionId;
        var sb = host.Queue.Find(b.Id)?.SessionId;
        Assert.NotNull(sa);
        Assert.NotNull(sb);
        Assert.NotEqual(sa, sb); // each task gets its own chat
        Assert.DoesNotContain(host.Sessions, s => s.Running);
        Assert.Equal(1, host.Queue.Find(a.Id)?.Rounds);
        Assert.Equal(120, host.Queue.Find(a.Id)?.TotalTokens);
        Assert.False(host.Config.QueuePaused);
        // The queue file on disk matches.
        var onDisk = new TaskQueue(Path.Combine(_dir, "task-queue.json"));
        Assert.Equal(new[] { QueueTaskStatus.Complete, QueueTaskStatus.Complete }, onDisk.Tasks.Select(t => t.Status));
    });

    [Fact]
    public void TaskKeepsGoingPastTheOldRoundCapUntilTheModelSaysComplete() => Run(async host =>
    {
        _server.Reset((_, n) => n < 60 ? new FakeReply.Text("Still working on it.") : new FakeReply.Text("All verified.\nGOAL_COMPLETE"));
        var t = host.QueueAdd("Long task");
        host.StartQueue();
        await WaitUntil("queue done", () => !host.QueueRunning, 60);
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
        var t = host.QueueAdd("Survive an outage");
        host.StartQueue();
        await WaitUntil("queue done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(1, host.Queue.Find(t.Id)?.Rounds); // retries are not extra rounds
        var log = host.Queue.Find(t.Id)?.Log.Select(l => l.Text).ToList() ?? [];
        Assert.Contains(log, l => l.Contains("Model unavailable"));
        Assert.Contains(log, l => l.Contains("answering again"));
        var vm = host.Sessions.Single(s => s.Id == host.Queue.Find(t.Id)?.SessionId);
        Assert.Single(Replies(vm), r => r.Contains("Recovered."));
        Assert.Null(vm.Retry);
    });

    [Fact]
    public void StopReturnsTheTaskAndRestartResumesInTheSameChat() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(5), "late") : new FakeReply.Text("Done.\nGOAL_COMPLETE"));
        var t = host.QueueAdd("Stoppable");
        host.StartQueue();
        await WaitUntil("request in flight", () => _server.Seen.Count == 1);
        var chat = host.Queue.Find(t.Id)?.SessionId;
        Assert.NotNull(chat); // chat recorded at start
        host.StopQueue();
        Assert.True(host.QueueStopping);
        await WaitUntil("stopped", () => !host.QueueRunning && !host.QueueStopping);
        Assert.Equal(QueueTaskStatus.Queued, Status(host, t.Id));
        Assert.True(host.Config.QueuePaused);
        Assert.False(host.Config.QueueResumeOnLaunch);
        Assert.False(host.Session(chat!)?.Running ?? true);

        host.StartQueue();
        Assert.False(host.Config.QueuePaused);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(chat, host.Queue.Find(t.Id)?.SessionId); // resumed in the same chat
        // The stopped attempt never got an answer, so nothing reached the model: the restart is a
        // clean kickoff, and the chat says so.
        Assert.StartsWith("GOAL: Stoppable", _server.Seen[^1].LastUser);
        Assert.Equal(1, _server.Seen[^1].Messages.Count(m => m.Role == "user"));
        Assert.Contains(Notes(host.Session(chat!)!), n => n.StartsWith("Not delivered to the model", StringComparison.Ordinal));
    });

    [Fact]
    public void StartStopStartInOneBreathRunsTheTaskOnce() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromMilliseconds(300), "Done.\nGOAL_COMPLETE"));
        var t = host.QueueAdd("Race");
        var other = host.QueueAdd("Next");
        host.StartQueue();
        host.StopQueue();
        host.StartQueue();
        host.StopQueue();
        host.StartQueue();
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, other.Id));
        Assert.Equal(2, host.Sessions.Where(s => s.Title.StartsWith("🚀", StringComparison.Ordinal)).Select(s => s.Id).Distinct().Count());
        Assert.DoesNotContain(host.Sessions, s => s.Running);
        Assert.DoesNotContain(host.Queue.Tasks, task => task.Status == QueueTaskStatus.Running);
    });

    [Fact]
    public void StartWhileTheStoppedTaskIsStillUnwindingRunsItOnceMore() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(5), "late") : new FakeReply.Text("GOAL_COMPLETE"));
        var t = host.QueueAdd("Unwinding");
        host.StartQueue();
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        host.StopQueue();
        host.StartQueue(); // before the old run has let go of the task
        Assert.True(host.QueueRunning);
        await WaitUntil("done", () => !host.QueueRunning && !host.QueueStopping);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        Assert.Equal(2, _server.Seen.Count);
        Assert.Single(host.Sessions, s => s.Title.StartsWith("🚀", StringComparison.Ordinal));
    });

    [Fact]
    public void StoppingTheTaskFromItsChatPausesTheQueue() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Slow(TimeSpan.FromSeconds(5), "late"));
        var t = host.QueueAdd("A");
        host.QueueAdd("B");
        host.StartQueue();
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        var chat = host.Queue.Find(t.Id)?.SessionId;
        Assert.NotNull(chat);
        host.StopSession(chat!);
        await WaitUntil("paused", () => !host.QueueRunning && !host.QueueStopping);
        Assert.Equal(QueueTaskStatus.Queued, Status(host, t.Id));
        Assert.True(host.Config.QueuePaused);
        Assert.Equal(2, host.Queue.Tasks.Count(task => task.Status == QueueTaskStatus.Queued));
    });

    [Fact]
    public void DeletingTheRunningTaskSkipsToTheNextOne() => Run(async host =>
    {
        _server.Reset((request, _) => request.AllUserText.Contains("GOAL: A")
            ? new FakeReply.Slow(TimeSpan.FromSeconds(5), "late")
            : new FakeReply.Text("GOAL_COMPLETE"));
        var a = host.QueueAdd("A");
        var b = host.QueueAdd("B");
        host.StartQueue();
        await WaitUntil("A in flight", () => Status(host, a.Id) == QueueTaskStatus.Running && _server.Seen.Count == 1);
        host.QueueRemove(a.Id);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Skipped, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));
        Assert.False(host.Config.QueuePaused);
    });

    [Fact]
    public void ErrorsFailATaskAndThreeInARowPauseTheQueue() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Http(400, """{"error":{"message":"bad request","code":400}}"""));
        var ids = Enumerable.Range(1, 4).Select(i => host.QueueAdd($"T{i}").Id).ToList();
        host.StartQueue();
        await WaitUntil("paused", () => !host.QueueRunning, 30);
        Assert.Equal(new QueueTaskStatus?[] { QueueTaskStatus.Failed, QueueTaskStatus.Failed, QueueTaskStatus.Failed, QueueTaskStatus.Queued },
            ids.Select(id => Status(host, id)));
        Assert.True(host.Config.QueuePaused);
        // Each task tried its goal several rounds before giving up.
        Assert.Equal(3 * GoalProtocol.MaxConsecutiveErrors, _server.Seen.Count);
    });

    [Fact]
    public void ANonTransientErrorRoundIsRetriedAndTheGoalCarriesOn() => Run(async host =>
    {
        _server.Reset((_, n) => n == 1
            ? new FakeReply.Http(400, """{"error":{"message":"template error","code":400}}""")
            : new FakeReply.Text("Fine now.\nGOAL_COMPLETE"));
        var t = host.QueueAdd("Flaky request");
        host.StartQueue();
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, t.Id));
        // Round 1 never got going, so round 2 re-sends the kickoff.
        Assert.StartsWith("GOAL:", _server.Seen[^1].LastUser);
    });

    [Fact]
    public void BlockedTaskMovesOnThenResumeRunsJustThatTask() => Run(async host =>
    {
        _server.Reset((request, _) =>
            request.AllUserText.Contains("GOAL: A") && !request.LastUser.StartsWith("[Resuming]", StringComparison.Ordinal)
                ? new FakeReply.Text("I need the API key.\nGOAL_BLOCKED: the API key")
                : new FakeReply.Text("GOAL_COMPLETE"));
        var a = host.QueueAdd("A");
        var b = host.QueueAdd("B");
        host.StartQueue();
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Blocked, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));

        var c = host.QueueAdd("C");
        var chat = host.Queue.Find(a.Id)?.SessionId;
        host.ResumeTask(a.Id);
        Assert.Equal(new[] { a.Id }, host.QueueOnlyTasks!);
        await WaitUntil("resumed task done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        Assert.Equal(chat, host.Queue.Find(a.Id)?.SessionId);
        Assert.Equal(QueueTaskStatus.Queued, Status(host, c.Id)); // Resume works only that task
    });

    [Fact]
    public void ResumeWhileTheQueueRunsPutsTheTaskNextInstead() => Run(async host =>
    {
        _server.Reset((request, _) =>
        {
            if (request.AllUserText.Contains("GOAL: A") && !request.LastUser.StartsWith("[Resuming]", StringComparison.Ordinal))
                return new FakeReply.Text("GOAL_BLOCKED: need input");
            if (request.LastUser.Contains("GOAL: B")) return new FakeReply.Slow(TimeSpan.FromMilliseconds(500), "GOAL_COMPLETE");
            return new FakeReply.Text("GOAL_COMPLETE");
        });
        var a = host.QueueAdd("A");
        var b = host.QueueAdd("B");
        var c = host.QueueAdd("C");
        host.StartQueue();
        await WaitUntil("B running", () => Status(host, b.Id) == QueueTaskStatus.Running);
        Assert.Equal(QueueTaskStatus.Blocked, Status(host, a.Id));
        host.ResumeTask(a.Id);
        Assert.Equal(QueueTaskStatus.Queued, Status(host, a.Id));
        Assert.Equal(a.Id, host.Queue.NextTask?.Id);
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(new QueueTaskStatus?[] { QueueTaskStatus.Complete, QueueTaskStatus.Complete, QueueTaskStatus.Complete },
            new[] { a, b, c }.Select(t => Status(host, t.Id)));
        Assert.DoesNotContain(host.Sessions, s => s.Running);
    });

    [Fact]
    public void ResumingAFailedArchivedTaskKeepsItsHistory() => Run(async host =>
    {
        // A fails (its chat is archived and released), B keeps the queue busy.
        _server.Reset((request, _) =>
        {
            if (request.LastUser.Contains("GOAL: A") || (request.LastUser.Contains("round") && request.AllUserText.Contains("GOAL: A")))
                return new FakeReply.Http(400, """{"error":{"message":"bad","code":400}}""");
            if (request.AllUserText.Contains("GOAL: B")) return new FakeReply.Slow(TimeSpan.FromMilliseconds(600), "GOAL_COMPLETE");
            return new FakeReply.Text("GOAL_COMPLETE");
        });
        var a = host.QueueAdd("A");
        var b = host.QueueAdd("B");
        host.StartQueue();
        await WaitUntil("A failed, B running", () => Status(host, a.Id) == QueueTaskStatus.Failed && Status(host, b.Id) == QueueTaskStatus.Running);
        var chat = host.Queue.Find(a.Id)?.SessionId;
        Assert.NotNull(chat);
        var vm = host.Session(chat!)!;
        var storedBefore = host.Log.LoadItems(chat!).Count;
        Assert.True(storedBefore > 3, $"{storedBefore} rows");

        _server.Reset((_, _) => new FakeReply.Text("GOAL_COMPLETE"));
        host.ResumeTask(a.Id);
        Assert.NotEmpty(vm.Entries);
        // The earlier attempt's timeline is back on screen (its rounds never reached the model, so they
        // read as not delivered).
        Assert.Contains(vm.Entries.OfType<MessageEntryVM>(), m => m.Text.Contains("Not delivered to the model: 🚀 queue: A"));
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        // The disk history grew rather than being replaced.
        Assert.True(host.Log.LoadItems(chat!).Count > storedBefore);
    });

    [Fact]
    public void ResumeDuringAResumeOnlyRunIsNotLost() => Run(async host =>
    {
        _server.Reset((request, _) => !request.LastUser.StartsWith("[Resuming]", StringComparison.Ordinal)
            ? new FakeReply.Text("GOAL_BLOCKED: need input")
            : new FakeReply.Slow(TimeSpan.FromMilliseconds(300), "GOAL_COMPLETE"));
        var a = host.QueueAdd("A");
        var b = host.QueueAdd("B");
        host.StartQueue();
        await WaitUntil("both blocked", () => !host.QueueRunning);
        Assert.Equal(new QueueTaskStatus?[] { QueueTaskStatus.Blocked, QueueTaskStatus.Blocked }, new[] { a, b }.Select(t => Status(host, t.Id)));
        var c = host.QueueAdd("C");
        host.ResumeTask(a.Id);
        host.ResumeTask(b.Id); // while A's Resume run is going
        await WaitUntil("done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, Status(host, a.Id));
        Assert.Equal(QueueTaskStatus.Complete, Status(host, b.Id));
        Assert.Equal(QueueTaskStatus.Queued, Status(host, c.Id));
    });

    [Fact]
    public void TaskRunsInTheProjectItWasQueuedIn() => Run(async host =>
    {
        var first = Project("main");
        var t = host.QueueAdd("Here");
        host.AdoptProject(Project("elsewhere"));
        host.StartQueue();
        await WaitUntil("done", () => !host.QueueRunning);
        var chat = host.Sessions.Single(s => s.Id == host.Queue.Find(t.Id)?.SessionId);
        Assert.True(SamePath(first, chat.Cwd), chat.Cwd);
    });

    [Fact]
    public void QueueDoesNotStealTheSelectionFromAnotherChat() => Run(async host =>
    {
        var mine = host.NewSession(Project("main"));
        mine.AppendMessage(MessageRole.User, "my own work");
        host.SelectedId = mine.Id;
        host.QueueAdd("A");
        host.QueueAdd("B");
        host.StartQueue();
        // First task: followed (the user just pressed Start).
        await WaitUntil("done", () => !host.QueueRunning);
        var queueChats = host.Queue.Tasks.Select(t => t.SessionId).OfType<string>().ToHashSet();
        Assert.Contains(host.SelectedId!, queueChats);

        // Now the user goes back to their chat; the next run must not yank them.
        host.SelectedId = mine.Id;
        host.QueueAdd("C");
        host.QueueAdd("D");
        _server.Reset((_, _) => new FakeReply.Text("GOAL_COMPLETE"));
        host.StartQueue();
        await WaitUntil("done again", () => !host.QueueRunning);
        Assert.NotNull(host.SelectedId);
    });

    [Fact]
    public void RelaunchPutsAnInterruptedTaskBackAndItResumes() => UiThread.Run(async () =>
    {
        var host = MakeHost();
        host.AdoptProject(Project("main"));
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(30), "late") : new FakeReply.Text("GOAL_COMPLETE"));
        var t = host.QueueAdd("Interrupted");
        host.StartQueue();
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        // Simulate a quit: a second host reads the same files.
        var relaunched = MakeHost(_config);
        Assert.Equal(QueueTaskStatus.Queued, relaunched.Queue.Find(t.Id)?.Status);
        var chat = relaunched.Queue.Find(t.Id)?.SessionId;
        Assert.NotNull(chat);
        host.StopAll(); // the old process is gone
        await WaitUntil("old stopped", () => !host.QueueRunning);
        relaunched.Config.QueuePaused = false;
        relaunched.StartQueue();
        await WaitUntil("relaunched done", () => !relaunched.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, relaunched.Queue.Find(t.Id)?.Status);
        Assert.Equal(chat, relaunched.Queue.Find(t.Id)?.SessionId);
        relaunched.StopAll();
        await Task.Delay(50);
    });

    [Fact]
    public void AnInterruptedQueueResumesOnLaunchButAStoppedOneDoesNot() => UiThread.Run(async () =>
    {
        var host = MakeHost();
        host.AdoptProject(Project("main"));
        _server.Reset((_, n) => n == 1 ? new FakeReply.Slow(TimeSpan.FromSeconds(30), "late") : new FakeReply.Text("GOAL_COMPLETE"));
        var t = host.QueueAdd("Interrupted");
        host.StartQueue();
        await WaitUntil("in flight", () => _server.Seen.Count == 1);
        // Quitting the app is not the user pressing Stop.
        host.Shutdown();
        await WaitUntil("old stopped", () => host.Queue.Find(t.Id)?.Status == QueueTaskStatus.Queued);
        Assert.True(host.Config.QueueResumeOnLaunch);
        Assert.False(host.Config.QueuePaused);
        var relaunched = MakeHost(_config);
        relaunched.ResumeQueueIfNeeded();
        Assert.True(relaunched.QueueRunning);
        await WaitUntil("relaunched done", () => !relaunched.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, relaunched.Queue.Find(t.Id)?.Status);

        // A queue the user stopped stays stopped.
        relaunched.QueueAdd("Later");
        relaunched.Config.QueuePaused = true;
        var third = MakeHost(_config);
        third.ResumeQueueIfNeeded();
        Assert.False(third.QueueRunning);
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
    public void AgentQueuesABackgroundTaskAndStartsTheQueue() => Run(async host =>
    {
        _server.Reset((request, n) =>
        {
            if (request.LastUser.StartsWith("GOAL:", StringComparison.Ordinal)) return new FakeReply.Text("Wrote the tests.\nGOAL_COMPLETE");
            if (n == 1 && request.Fresh)
                return new FakeReply.ToolCall("queue_task", """{"title":"Write parser tests","details":"Cover the edge cases.","start":true}""");
            return new FakeReply.Text("Queued it.");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("queue the tests for later", vm.Id);
        await WaitUntil("queued task done", () =>
            host.Queue.Tasks.FirstOrDefault()?.Status == QueueTaskStatus.Complete && !host.QueueRunning && !vm.Running);
        var task = host.Queue.Tasks[0];
        Assert.Equal("Write parser tests", task.Title);
        Assert.True(SamePath(Project("main"), task.Cwd), task.Cwd);
        Assert.Contains(task.Log, l => l.Text.Contains("Queued by the agent"));
        Assert.Contains(Notes(vm), n => n.Contains("The agent queued a task"));
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
