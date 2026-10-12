using System.IO;
using System.Windows.Threading;
using Dsh.App.Model;
using Dsh.Core;
using MessageRole = Dsh.App.Model.MessageRole;

namespace Dsh.App.Tests;

/// <summary>Nobody types "continue". A /goal runs until the model signals it is done, a plain turn runs past
/// the step limit on its own, a goal that paused for an answer picks up on the reply, and a run that goes in
/// circles stops itself with an explanation. Around that: the data behind the plan panel, notes that ride along
/// with a message, and subagents running on another model server — all end to end through AgentHost against a
/// fake model server.</summary>
public sealed class AutoContinueTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dsh-auto-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    public AutoContinueTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Done."));
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

    private AgentHost MakeHost()
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        return new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
            GoalErrorBackoff = _ => TimeSpan.FromMilliseconds(50),
        };
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

    private static List<string> Notes(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role is MessageRole.Notice or MessageRole.Error).Select(m => m.Text).ToList();

    private static List<string> Replies(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role == MessageRole.Assistant).Select(m => m.Text).ToList();

    private static bool IsSubagent(SeenRequest request) =>
        request.System.StartsWith("You are a focused subagent", StringComparison.Ordinal);

    // MARK: - /goal: the model signals, nobody types "continue"

    [Fact]
    public void GoalEndsWhenTheAgentCallsGoalComplete() => Run(async host =>
    {
        _server.Reset((_, n) => n < 3
            ? new FakeReply.Text($"Still on it ({n}).")
            : new FakeReply.ToolCall("goal_complete", """{"summary":"Built it and the tests pass."}"""));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal build the thing", vm.Id);
        await WaitUntil("goal done", () => !vm.Running && _server.Seen.Count >= 3);

        // Three rounds; the tool call was the last request — once it signals there is nothing more to say.
        Assert.Equal(3, _server.Seen.Count);
        Assert.Contains(Notes(vm), n => n.Contains("Goal complete after 3 rounds") && n.Contains("Built it and the tests pass."));
        Assert.Null(vm.Goal);
        Assert.Null(vm.BlockedGoal);
        // The kickoff tells the model how to signal.
        Assert.Contains("goal_complete", _server.Seen[0].LastUser);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APausedGoalPicksUpOnThePlainReply(bool blockWithTool) => Run(async host =>
    {
        _server.Reset((_, n) =>
        {
            if (n > 1) return new FakeReply.ToolCall("goal_complete", """{"summary":"Merged release."}""");
            return blockWithTool
                ? new FakeReply.ToolCall("goal_blocked", """{"reason":"Which branch should I merge?"}""")
                : new FakeReply.Text("Two candidates.\nGOAL_BLOCKED: Which branch should I merge?");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("/goal merge the branch", vm.Id);
        await WaitUntil("paused", () => !vm.Running && vm.BlockedGoal is not null);

        Assert.Equal("merge the branch", vm.BlockedGoal);
        Assert.Null(vm.Goal);
        Assert.Contains(Notes(vm), n => n.Contains("Which branch should I merge") && n.Contains("carries on by itself"));

        // No second /goal: the answer alone is enough.
        host.Send("release", vm.Id);
        await WaitUntil("resumed and done", () => !vm.Running && _server.Seen.Count == 2);

        var resumed = _server.Seen[1];
        Assert.StartsWith("[Resuming]", resumed.LastUser);
        Assert.Contains("<user_reply>\nrelease\n</user_reply>", resumed.LastUser);
        Assert.Contains("GOAL: merge the branch", resumed.AllUserText); // the same conversation, not a restart
        Assert.Contains(Notes(vm), n => n.Contains("Goal complete") && n.Contains("Merged release."));
        Assert.Null(vm.BlockedGoal);
        Assert.Contains(vm.Entries.OfType<MessageEntryVM>(), m => m.Role == MessageRole.User && m.Text == "release");
    });

    [Fact]
    public void AGoalThatKeepsRepeatingItselfAsksForAnotherDirection() => Run(async host =>
    {
        // The same call, the same result, over and over — in every round.
        _server.Reset((_, _) => new FakeReply.ToolCall("read_file", """{"file_path":"missing.txt"}"""));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal read missing.txt", vm.Id);
        await WaitUntil("gave up", () => !vm.Running && vm.BlockedGoal is not null);

        // Eight identical calls end a round; three such rounds end the goal — not a fourth, and not for ever.
        Assert.Equal(3 * 8, _server.Seen.Count);
        Assert.Contains(Notes(vm), n => n.Contains("going in circles"));
        Assert.Equal("read missing.txt", vm.BlockedGoal);
        // The next round told the model why the last one was cut short.
        Assert.Contains("same call", _server.Seen[8].LastUser);
    });

    [Fact]
    public void AQueueTaskEndsOnTheAgentsSignalToo() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.ToolCall("goal_complete", """{"summary":"Done and verified."}"""));
        var chat = host.NewSession(Project("main"));
        var done = host.QueueAdd(chat.Id, "Signal with the tool");
        host.StartQueue(chat.Id);
        await WaitUntil("queue done", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Complete, host.Queue.Find(done.Id)?.Status);

        _server.Reset((_, _) => new FakeReply.ToolCall("goal_blocked", """{"reason":"Need the deploy key."}"""));
        var stuck = host.QueueAdd(chat.Id, "Needs a key");
        host.StartQueue(chat.Id);
        await WaitUntil("queue done again", () => !host.QueueRunning);
        Assert.Equal(QueueTaskStatus.Blocked, host.Queue.Find(stuck.Id)?.Status);
    });

    // MARK: - Plain chat turns

    [Fact]
    public void APlainTurnRunsPastTheStepLimitOnItsOwn() => Run(async host =>
    {
        // The engine checkpoints every 30 model calls; this turn needs 36.
        const int ToolSteps = 35;
        _server.Reset((_, n) => n <= ToolSteps
            ? new FakeReply.ToolCall("todo_write", $$"""{"todos":[{"content":"step {{n}}","status":"in_progress"}]}""")
            : new FakeReply.Text("All finished."));
        var vm = host.NewSession(Project("main"));
        host.Send("do the long job", vm.Id);
        await WaitUntil("finished", () => !vm.Running && _server.Seen.Count == ToolSteps + 1);

        Assert.Equal(new[] { "All finished." }, Replies(vm));
        // One message from the user, answered in one go: nothing was asked of the user, nothing injected.
        Assert.Single(_server.Seen, r => r.Fresh);
        Assert.DoesNotContain(Notes(vm), n => n.Contains("continue", StringComparison.OrdinalIgnoreCase));
        Assert.Single(vm.Todos);
    });

    [Fact]
    public void AnAgentRepeatingOneActionIsWarnedThenStopped() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.ToolCall("read_file", """{"file_path":"nothing-here.txt"}"""));
        var vm = host.NewSession(Project("main"));
        host.Send("find nothing-here.txt", vm.Id);
        await WaitUntil("stopped", () => !vm.Running && _server.Seen.Count >= 8);

        Assert.Equal(8, _server.Seen.Count);
        // The model was told, from the fourth identical result on...
        Assert.Contains(_server.Seen[4].Messages, m => m.Role == "tool" && m.Content?.Contains("Harness note") == true);
        // ...and the user is told why the run ended, rather than left to wonder.
        Assert.Contains(Notes(vm), n => n.Contains("same `read_file` call 8 times"));
    });

    // MARK: - The plan panel

    [Fact]
    public void AProsePlanFillsThePlanAndATodoListTakesOver() => Run(async host =>
    {
        var opened = 0;
        host.PlanAppeared += _ => opened++;
        _server.Reset((_, _) => new FakeReply.Text("Here's my plan:\n1. Read the config\n2. Change the port\n3. Run the tests\n\nStarting with the first step."));
        var vm = host.NewSession(Project("main"));
        host.Send("move the service to port 9000", vm.Id);
        await WaitUntil("planned", () => !vm.Running && _server.Seen.Count == 1);

        Assert.Equal(3, vm.Outline.Count);
        Assert.Contains("Read the config", vm.Outline[0]);
        Assert.Equal(1, opened); // the panel opens by itself, once

        // Once the agent keeps a live todo list, that is the plan.
        _server.Reset((_, n) => n == 1
            ? new FakeReply.ToolCall("todo_write", """{"todos":[{"content":"Read the config","status":"completed"},{"content":"Change the port","status":"in_progress"}]}""")
            : new FakeReply.Text("Working on it."));
        host.Send("go ahead", vm.Id);
        await WaitUntil("todos", () => !vm.Running && _server.Seen.Count == 2);

        Assert.Empty(vm.Outline);
        Assert.Equal(2, vm.Todos.Count);
        Assert.Equal(1, opened); // closing the panel is not undone by every later update
    });

    [Fact]
    public void APlanMadeInAChatNobodyIsLookingAtIsAnnouncedWithTheFirstUpdateSomeoneSees() => Run(async host =>
    {
        var announced = new List<string>();
        host.PlanAppeared += session => announced.Add(session.Id);
        var looking = host.NewSession(Project("main"));
        var background = host.NewSession(Project("main"), select: false);
        Assert.Equal(looking.Id, host.SelectedId);

        _server.Reset((_, _) => new FakeReply.Text("Here's my plan:\n1. Read the config\n2. Change the port\n3. Run the tests\n\nStarting."));
        host.Send("move the service to port 9000", background.Id);
        await WaitUntil("planned", () => !background.Running && background.Outline.Count == 3);
        Assert.Empty(announced); // the window would have declined it — and the once-per-chat chance must not be used up

        host.SelectedId = background.Id;
        host.Send("go ahead", background.Id);
        await WaitUntil("planned again", () => !background.Running && _server.Seen.Count == 2);
        Assert.Equal([background.Id], announced);

        host.Send("and again", background.Id);
        await WaitUntil("third turn", () => !background.Running && _server.Seen.Count == 3);
        Assert.Equal([background.Id], announced); // once
    });

    [Fact]
    public void AnAgentThatFinishesWhileTheGoalIsPausedDoesNotWakeTheChat() => Run(async host =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Reset((request, _) =>
        {
            if (IsSubagent(request)) return new FakeReply.Gated(release.Task, "Report: the flag is in config.yml.");
            if (request.LastUser.StartsWith("[Resuming]", StringComparison.Ordinal)) return new FakeReply.ToolCall("goal_complete", """{"summary":"Done."}""");
            if (request.Fresh) return new FakeReply.ToolCall("agent", """{"description":"flag","prompt":"find it","run_in_background":true}""");
            return new FakeReply.ToolCall("goal_blocked", """{"reason":"Which branch should I use?"}""");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("/goal find the flag", vm.Id);
        await WaitUntil("paused", () => !vm.Running && vm.BlockedGoal is not null);
        await WaitUntil("the agent is working", () => _server.Seen.Any(IsSubagent));
        var requests = _server.Seen.Count;

        // The agent finishes while the chat waits for the user's answer: nothing starts by itself.
        release.SetResult();
        await WaitUntil("the agent finished", () => AgentFinished(vm));
        await Task.Delay(400);
        Assert.False(vm.Running);
        Assert.Equal(requests, _server.Seen.Count);
        Assert.NotNull(vm.BlockedGoal);

        // The answer resumes the goal, and the report goes with it.
        host.Send("main", vm.Id);
        await WaitUntil("resumed", () => !vm.Running && vm.BlockedGoal is null && Notes(vm).Any(n => n.Contains("Goal complete")));
        Assert.Contains("Report: the flag is in config.yml.", _server.Seen[^1].AllUserText);
    });

    [Fact]
    public void ABackgroundAgentLoopStopsWakingTheChatAfterTheCapAndSaysSoOnce() => Run(async host =>
    {
        _server.Reset((request, _) =>
        {
            if (IsSubagent(request)) return new FakeReply.Text("Report.");
            if (request.Fresh) return new FakeReply.ToolCall("agent", """{"description":"again","prompt":"look","run_in_background":true}""");
            return new FakeReply.Text("Launched another.");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("keep looking", vm.Id);

        const string capNote = "Background agents finished — reply to continue.";
        await WaitUntil("the cap", () => !vm.Running && Notes(vm).Contains(capNote), 90);
        Assert.Equal(AgentHost.MaxAutoContinuations, _server.Seen.Count(r => r.Fresh && r.LastUser.Contains("Your background agents finished")));

        // Said once, and nothing more happens by itself.
        var requests = _server.Seen.Count;
        await Task.Delay(500);
        Assert.False(vm.Running);
        Assert.Equal(requests, _server.Seen.Count);
        Assert.Single(Notes(vm), n => n == capNote);

        // Asked to look again (an agent stopped by hand), it stays quiet.
        host.StopBackgroundAgents(vm.Id);
        await Task.Delay(300);
        Assert.False(vm.Running);
        Assert.Equal(requests, _server.Seen.Count);
        Assert.Single(Notes(vm), n => n == capNote);

        // The user's next message starts the count again.
        host.Send("carry on", vm.Id);
        await WaitUntil("woken again", () => _server.Seen.Count(r => r.Fresh && r.LastUser.Contains("Your background agents finished")) > AgentHost.MaxAutoContinuations, 90);
        host.StopSession(vm.Id);
        await WaitUntil("stopped", () => !vm.Running);
    });

    // MARK: - Memory

    private static void SeedNotes(AgentHost host)
    {
        host.Memory.Save(new MemoryDraft { Title = "Deploy procedure", Body = "Run scripts/deploy.ps1 against staging first; production needs the VPN.", Kind = "procedure", Source = "user" });
        host.Memory.Save(new MemoryDraft { Title = "Database", Body = "Postgres runs on port 5433 in the dev container.", Source = "user" });
        host.Memory.Save(new MemoryDraft { Title = "Style", Body = "The user prefers tabs over spaces.", Kind = "preference", Source = "user" });
    }

    [Fact]
    public void NotesThatBearOnAMessageRideAlongAndOthersDoNot() => Run(async host =>
    {
        SeedNotes(host);
        _server.Reset((_, _) => new FakeReply.Text("ok"));
        var vm = host.NewSession(Project("main"));

        host.Send("how do I deploy to staging?", vm.Id);
        await WaitUntil("answered", () => !vm.Running && _server.Seen.Count == 1);
        var first = _server.Seen[0].LastUser;
        Assert.StartsWith("<recalled_memory>", first);
        Assert.Contains("scripts/deploy.ps1", first);
        Assert.DoesNotContain("Postgres", first); // only what bears on the message
        Assert.EndsWith("how do I deploy to staging?", first);
        Assert.Contains(Notes(vm), n => n.Contains("Remembered 1 note") && n.Contains("Deploy procedure"));

        host.Send("please summarise this paragraph about gardening", vm.Id);
        await WaitUntil("answered again", () => !vm.Running && _server.Seen.Count == 2);
        Assert.Equal("please summarise this paragraph about gardening", _server.Seen[1].LastUser);
    });

    [Fact]
    public void WithMemoryOffNothingIsAttached() => Run(async host =>
    {
        SeedNotes(host);
        host.Config.MemoryEnabled = false;
        _server.Reset((_, _) => new FakeReply.Text("ok"));
        var vm = host.NewSession(Project("main"));
        host.Send("how do I deploy to staging?", vm.Id);
        await WaitUntil("answered", () => !vm.Running && _server.Seen.Count == 1);
        Assert.Equal("how do I deploy to staging?", _server.Seen[0].LastUser);
        Assert.DoesNotContain("memory_save", string.Join("\n", _server.Seen[0].Messages.Select(m => m.Content)));
    });

    [Fact]
    public void ANoteTheAgentSavesComesBackInAnotherChat() => Run(async host =>
    {
        SeedNotes(host);
        _server.Reset((_, n) => n == 1
            ? new FakeReply.ToolCall("memory_save", """{"title":"Staging server","content":"The staging server is called orion; deploy it with scripts/ship.ps1.","kind":"fact"}""")
            : new FakeReply.Text("Noted."));
        var first = host.NewSession(Project("main"));
        host.Send("remember that our staging server is called orion", first.Id);
        await WaitUntil("saved", () => !first.Running && _server.Seen.Count == 2);
        Assert.Contains(host.Memory.All(), n => n.Title == "Staging server" && n.Body.Contains("orion"));

        _server.Reset((_, _) => new FakeReply.Text("Use scripts/ship.ps1."));
        var second = host.NewSession(Project("main"));
        host.Send("how do I deploy the staging server?", second.Id);
        await WaitUntil("answered", () => !second.Running && _server.Seen.Count == 1);
        Assert.Contains("<recalled_memory>", _server.Seen[0].LastUser);
        Assert.Contains("scripts/ship.ps1", _server.Seen[0].LastUser);
    });

    [Fact]
    public void RememberSavesANoteAndRefusesASecret() => Run(host =>
    {
        var vm = host.NewSession(Project("main"));
        host.Send("/remember the staging server is called orion", vm.Id);
        var note = Assert.Single(host.Memory.All());
        Assert.Contains("orion", note.Body);
        Assert.Equal("user", note.Source);
        Assert.Contains(Notes(vm), n => n.Contains("Remembered"));

        host.Send("/remember the api key is sk-abcdefghijklmnopqrstuvwxyz123456", vm.Id);
        Assert.Single(host.Memory.All());
        Assert.Contains(Notes(vm), n => n.Contains("looks like a key or password"));
        return Task.CompletedTask;
    });

    // MARK: - Several model servers

    private static ProviderProfile SecondSpark() =>
        new(ProviderKind.OpenAICompat, "Spark 2", "http://spark2.test/v1", "stub-model") { SubagentWorker = true };

    [Fact]
    public void ASubagentRunsOnTheWorkerServerNotThePrimary() => Run(async host =>
    {
        host.Config.Providers.Add(SecondSpark());
        _server.Reset((request, _) =>
        {
            if (IsSubagent(request)) return new FakeReply.Text("Subagent report: 7 files.");
            if (request.Fresh) return new FakeReply.ToolCall("agent", """{"description":"count","prompt":"count the files in src"}""");
            return new FakeReply.Text("There are 7 files.");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("count the files", vm.Id);
        await WaitUntil("done", () => !vm.Running && _server.Seen.Count == 3);

        // The main agent talks to the primary; the subagent went to the other machine.
        Assert.Equal(new[] { "stub.test", "spark2.test", "stub.test" }, _server.Seen.Select(r => r.Host));
        var toolResult = _server.Seen[2].Messages.Last(m => m.Role == "tool").Content ?? "";
        Assert.Contains("Subagent report: 7 files.", toolResult);
        Assert.Contains("on Spark 2", toolResult);
        Assert.Equal(1, host.Fleet.Snapshot().Single(s => !s.IsPrimary).Completed);
        Assert.Equal(new[] { "Spark 2" }, host.WorkerRoutes.Select(r => r.Name));
    });

    [Fact]
    public void ADeadWorkerHandsTheTaskToThePrimary() => Run(async host =>
    {
        host.Config.Providers.Add(SecondSpark());
        _server.Reset((request, _) =>
        {
            if (IsSubagent(request))
                return request.Host == "spark2.test" ? new FakeReply.Transport() : new FakeReply.Text("Subagent report: 7 files.");
            if (request.Fresh) return new FakeReply.ToolCall("agent", """{"description":"count","prompt":"count the files in src"}""");
            return new FakeReply.Text("There are 7 files.");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("count the files", vm.Id);
        await WaitUntil("done", () => !vm.Running && _server.Seen.Count(r => !IsSubagent(r)) == 2 && _server.Seen.Any(r => IsSubagent(r) && r.Host == "stub.test"));

        Assert.NotEmpty(_server.Seen.Where(r => IsSubagent(r) && r.Host == "spark2.test")); // it tried the worker first
        var toolResult = _server.Seen[^1].Messages.Last(m => m.Role == "tool").Content ?? "";
        Assert.Contains("Subagent report: 7 files.", toolResult);
        Assert.Contains("after 1 server failure", toolResult);
        var worker = host.Fleet.Snapshot().Single(s => !s.IsPrimary);
        Assert.Equal(1, worker.Failed);
        Assert.False(worker.Healthy); // left alone for the next task
    });

    // MARK: - Review fixes

    [Fact]
    public void APausedGoalIsStillWaitingAfterARestart() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.ToolCall("goal_blocked", """{"reason":"Which branch should I merge?"}"""));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal merge the branch", vm.Id);
        await WaitUntil("paused", () => !vm.Running && vm.BlockedGoal is not null);

        // The app starts again: the chat is read back from disk, and the answer still resumes the goal.
        var again = MakeHost();
        var loaded = again.Sessions.Single(s => s.Id == vm.Id);
        again.Hydrate(loaded);
        Assert.Equal("merge the branch", loaded.BlockedGoal);

        _server.Reset((_, _) => new FakeReply.ToolCall("goal_complete", """{"summary":"Merged release."}"""));
        again.Send("release", loaded.Id);
        await WaitUntil("resumed", () => !loaded.Running && _server.Seen.Count == 1);
        Assert.StartsWith("[Resuming]", _server.Seen[0].LastUser);
        again.StopAll();
    });

    [Fact]
    public void AMessageSentWhileAGoalWasPausedEndsThePauseInTheRecord()
    {
        var goal = new MessageEntryVM(MessageRole.User, "🎯 /goal merge the branch");
        var paused = new MessageEntryVM(MessageRole.Notice, "⏸ Goal paused — the agent needs you: which branch?\nReply here and it carries on by itself.");
        Assert.Equal("merge the branch", AgentHost.PausedGoalIn([goal, paused]));

        // The answer went out and the app closed before the goal finished: it is not waiting any more.
        Assert.Null(AgentHost.PausedGoalIn([goal, paused, new MessageEntryVM(MessageRole.User, "release")]));

        // An answer that never reached the model is shown as a notice: still waiting for it.
        Assert.Equal("merge the branch", AgentHost.PausedGoalIn([goal, paused, new MessageEntryVM(MessageRole.Notice, "Not delivered to the model: release")]));

        // A newer goal and its own pause replace the old one.
        var second = new MessageEntryVM(MessageRole.User, "🎯 /goal ship it");
        var pausedAgain = new MessageEntryVM(MessageRole.Notice, "⏸ Goal paused — the agent was going in circles.");
        Assert.Equal("ship it", AgentHost.PausedGoalIn([goal, paused, second, pausedAgain]));
    }

    [Fact]
    public void GoalStopDropsAPausedGoal() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Text("Two candidates.\nGOAL_BLOCKED: Which branch should I merge?"));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal merge the branch", vm.Id);
        await WaitUntil("paused", () => !vm.Running && vm.BlockedGoal is not null);

        host.Send("/goal stop", vm.Id);
        Assert.Null(vm.BlockedGoal);
        Assert.Contains(Notes(vm), n => n.StartsWith("Dropped the goal"));

        // What comes next is an ordinary message, not an answer — and a bare /goal has nothing left to resume.
        _server.Reset((_, _) => new FakeReply.Text("Sure."));
        host.Send("what is 2+2", vm.Id);
        await WaitUntil("plain turn", () => !vm.Running && _server.Seen.Count == 1);
        Assert.Equal("what is 2+2", _server.Seen[0].LastUser);
        host.Send("/goal", vm.Id);
        Assert.Contains(Notes(vm), n => n.StartsWith("Usage: `/goal"));
        Assert.Equal(1, _server.Seen.Count);
    });

    [Fact]
    public void AnAnswerThatCouldNotBeDeliveredLeavesTheGoalWaitingForIt() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.ToolCall("goal_blocked", """{"reason":"Which branch should I merge?"}"""));
        var vm = host.NewSession(Project("main"));
        host.Send("/goal merge the branch", vm.Id);
        await WaitUntil("paused", () => !vm.Running && vm.BlockedGoal is not null);

        // The server refuses the request itself, five times over: the round gives up.
        _server.Reset((_, _) => new FakeReply.Http(400, """{"error":{"message":"template error","code":400}}"""));
        host.Send("release", vm.Id);
        await WaitUntil("gave up", () => !vm.Running && _server.Seen.Count >= 1);

        Assert.Equal("merge the branch", vm.BlockedGoal); // the answer can be sent again
    });

    /// <summary>A model that launches a background agent and then keeps talking until the test lets it finish. The agent
    /// reports only once the main agent's second request is in flight, so the engine cannot fold the report in by itself:
    /// it always finds the chat busy. Returns the gate that ends the main agent's reply.</summary>
    private TaskCompletionSource ScriptBackgroundAgent(Func<SeenRequest, FakeReply?> first)
    {
        var mainMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var followUpInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Reset((request, _) =>
        {
            if (first(request) is { } special) return special;
            if (IsSubagent(request)) return new FakeReply.Gated(followUpInFlight.Task, "Report: 3 call sites.");
            if (request.Fresh) return new FakeReply.ToolCall("agent", """{"description":"call sites","prompt":"find them","run_in_background":true}""");
            followUpInFlight.TrySetResult();
            return new FakeReply.Gated(mainMayFinish.Task, "Launched it; waiting.");
        });
        return mainMayFinish;
    }

    private static bool AgentFinished(SessionVM vm) => vm.BackgroundJobs.Any(j => j.Status == BackgroundAgentStatus.Done);

    [Fact]
    public void ABackgroundAgentThatFinishesWhileTheChatIsBusyIsHeardWhenItEnds() => Run(async host =>
    {
        var mainMayFinish = ScriptBackgroundAgent(r =>
            r.LastUser.Contains("Your background agents finished") ? new FakeReply.Text("Using the report: 3 call sites.") : null);
        var vm = host.NewSession(Project("main"));
        host.Send("find the call sites in the background", vm.Id);

        // The agent reports while the main agent is still mid-turn — so its event finds the chat busy and is ignored...
        await WaitUntil("the agent finished", () => AgentFinished(vm));
        Assert.True(vm.Running);
        mainMayFinish.SetResult();

        // ...and it is the end of the run that hands the report over.
        await WaitUntil("handed back", () => !vm.Running && Replies(vm).Contains("Using the report: 3 call sites."), 30);
        Assert.Contains(_server.Seen, r => r.LastUser.Contains("Report: 3 call sites."));
    });

    [Fact]
    public void AStopIsNeverUndoneByAFinishedAgentOrALaterServerSwitch() => Run(async host =>
    {
        ScriptBackgroundAgent(_ => null);
        var vm = host.NewSession(Project("main"));
        host.Send("find the call sites in the background", vm.Id);
        await WaitUntil("the agent finished", () => AgentFinished(vm));

        // Stop while the report is still unread...
        host.StopSession(vm.Id);
        await WaitUntil("stopped", () => !vm.Running);
        var requests = _server.Seen.Count;

        // ...and neither the Spark finishing a switch nor anything else starts the chat again.
        host.ResumeAfterServerSwitch();
        await Task.Delay(400);
        Assert.False(vm.Running);
        Assert.Equal(requests, _server.Seen.Count);

        // The next thing the user sends does carry the report to the model.
        _server.Reset((_, _) => new FakeReply.Text("Noted."));
        host.Send("what did it find?", vm.Id);
        await WaitUntil("next turn", () => !vm.Running && _server.Seen.Count == 1);
        Assert.Contains("Report: 3 call sites.", _server.Seen[0].AllUserText);
    });

    [Fact]
    public void ReportsAreNotLostWhenTheHandBackRequestFails() => Run(async host =>
    {
        var failHandBack = true;
        var mainMayFinish = ScriptBackgroundAgent(r =>
        {
            if (r.LastUser.Contains("Your background agents finished") && failHandBack)
                return new FakeReply.Http(400, """{"error":{"message":"template error","code":400}}""");
            return r.LastUser.Contains("what did it find") ? new FakeReply.Text("It found 3 call sites.") : null;
        });
        var vm = host.NewSession(Project("main"));
        host.Send("find the call sites in the background", vm.Id);
        await WaitUntil("the agent finished", () => AgentFinished(vm));
        mainMayFinish.SetResult();

        // The hand-back is refused by the server; the run reports that and does not try again by itself.
        await WaitUntil("hand-back failed", () => !vm.Running && vm.Entries.OfType<MessageEntryVM>().Any(e => e.Role == MessageRole.Error));
        var afterFailure = _server.Seen.Count;
        await Task.Delay(400);
        Assert.Equal(afterFailure, _server.Seen.Count);
        Assert.Equal(1, _server.Seen.Count(r => r.LastUser.Contains("Your background agents finished"))); // one attempt, not a retry loop

        // The report was kept for the next message, which is the one that delivers it.
        failHandBack = false;
        host.Send("what did it find?", vm.Id);
        await WaitUntil("answered", () => !vm.Running && Replies(vm).Contains("It found 3 call sites."));
        Assert.Contains("Report: 3 call sites.", _server.Seen[^1].AllUserText);
    });

    [Fact]
    public void AStopBeforeTheModelHasRepliedPutsTheReportsBack() => Run(async host =>
    {
        // The hand-back's request is never answered; the user gives up on it. The reports it took were never seen by the model.
        var neverAnswered = new TaskCompletionSource();
        var mainMayFinish = ScriptBackgroundAgent(r =>
            r.LastUser.Contains("Your background agents finished") ? new FakeReply.Gated(neverAnswered.Task, "never") : null);
        var vm = host.NewSession(Project("main"));
        host.Send("find the call sites in the background", vm.Id);
        await WaitUntil("the agent finished", () => AgentFinished(vm));
        mainMayFinish.SetResult();
        await WaitUntil("the hand-back is waiting for the model", () => _server.Seen.Any(r => r.LastUser.Contains("Your background agents finished")));

        host.StopSession(vm.Id);
        await WaitUntil("stopped", () => !vm.Running);

        // The next message carries the report to the model after all.
        _server.Reset((_, _) => new FakeReply.Text("Noted."));
        host.Send("what did it find?", vm.Id);
        await WaitUntil("answered", () => !vm.Running && _server.Seen.Count == 1);
        Assert.Contains("Report: 3 call sites.", _server.Seen[0].AllUserText);
    });

    [Fact]
    public void AStopStaysInForceWhenTheNextMessageIsRefusedBecauseTheSparkIsSwitching() => Run(async host =>
    {
        ScriptBackgroundAgent(_ => null);
        var vm = host.NewSession(Project("main"));
        host.Send("find the call sites in the background", vm.Id);
        await WaitUntil("the agent finished", () => AgentFinished(vm));
        host.StopSession(vm.Id);
        await WaitUntil("stopped", () => !vm.Running);
        var requests = _server.Seen.Count;

        var switching = true;
        host.IsServerSwitching = () => switching;
        host.Send("ok", vm.Id); // refused: it changes nothing, and does not count as writing to the chat
        Assert.Contains(Notes(vm), n => n.Contains("switching models"));

        switching = false;
        host.ResumeAfterServerSwitch(); // the switch ends
        await Task.Delay(400);
        Assert.False(vm.Running);
        Assert.Equal(requests, _server.Seen.Count); // and the stopped chat stays stopped
    });

    [Fact]
    public void StoppingTheLastRunningAgentByHandLetsTheChatHearTheOthersReports() => Run(async host =>
    {
        // Two agents: A reports while the main agent is still talking; B never finishes on its own.
        var neverFinishes = new TaskCompletionSource();
        var mainMayFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lastRequestInFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _server.Reset((request, _) =>
        {
            if (IsSubagent(request))
                return request.AllUserText.Contains("find B") ? new FakeReply.Gated(neverFinishes.Task, "Report B") : new FakeReply.Gated(lastRequestInFlight.Task, "Report A: 3 call sites.");
            if (request.LastUser.Contains("Your background agents finished")) return new FakeReply.Text("Using the report from A.");
            if (request.Fresh) return new FakeReply.ToolCall("agent", """{"description":"A","prompt":"find A","run_in_background":true}""");
            if (request.Messages.Count(m => m.Role == "tool") == 1) return new FakeReply.ToolCall("agent", """{"description":"B","prompt":"find B","run_in_background":true}""");
            lastRequestInFlight.TrySetResult();
            return new FakeReply.Gated(mainMayFinish.Task, "Launched both.");
        });
        var vm = host.NewSession(Project("main"));
        host.Send("find A and B in the background", vm.Id);
        await WaitUntil("A finished", () => vm.BackgroundJobs.Any(j => j.Description == "A" && j.Status == BackgroundAgentStatus.Done));
        mainMayFinish.SetResult();
        await WaitUntil("the run is over", () => !vm.Running);
        Assert.DoesNotContain(Replies(vm), r => r.Contains("Using the report")); // B is still running: A's report waits

        var b = vm.BackgroundJobs.Single(j => j.Description == "B");
        host.StopBackgroundAgent(vm.Id, b.Id);

        await WaitUntil("A's report is heard", () => !vm.Running && Replies(vm).Contains("Using the report from A."), 30);
        Assert.Contains(_server.Seen, r => r.LastUser.Contains("Report A: 3 call sites."));
    });

    [Fact]
    public void PlanOnlyModeCanLookThingsUpButNotChangeWhatIsRemembered() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Text("ok"));
        var planning = host.NewSession(Project("main"));
        planning.Preset = PermissionPreset.Plan;
        host.Send("what do you know about deploys", planning.Id);
        await WaitUntil("planning turn", () => !planning.Running && _server.Seen.Count == 1);
        var request = _server.Seen[0];
        Assert.Contains("memory_search", request.Tools);
        Assert.DoesNotContain("memory_save", request.Tools);
        Assert.DoesNotContain("memory_forget", request.Tools);
        Assert.DoesNotContain("memory_save", request.System);

        var working = host.NewSession(Project("main"));
        host.Send("what do you know about deploys", working.Id);
        await WaitUntil("working turn", () => !working.Running && _server.Seen.Count == 2);
        Assert.Contains("memory_save", _server.Seen[1].Tools);
        Assert.Contains("memory_forget", _server.Seen[1].Tools);
    });

    [Fact]
    public void TheGoalToolsAreOfferedOnlyWhileAGoalRuns() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Text("just chatting"));
        var chat = host.NewSession(Project("main"));
        host.Send("hello there", chat.Id);
        await WaitUntil("chat", () => !chat.Running && _server.Seen.Count == 1);
        Assert.DoesNotContain("goal_complete", _server.Seen[0].Tools);
        Assert.DoesNotContain("goal_blocked", _server.Seen[0].Tools);

        _server.Reset((_, _) => new FakeReply.ToolCall("goal_complete", """{"summary":"done"}"""));
        host.Send("/goal do the thing", chat.Id);
        await WaitUntil("goal", () => !chat.Running && _server.Seen.Count == 1);
        Assert.Contains("goal_complete", _server.Seen[0].Tools);
        Assert.Contains("goal_blocked", _server.Seen[0].Tools);

        _server.Reset((_, _) => new FakeReply.Text("back to chat"));
        host.Send("and now?", chat.Id);
        await WaitUntil("chat again", () => !chat.Running && _server.Seen.Count == 1);
        Assert.DoesNotContain("goal_complete", _server.Seen[0].Tools);
    });

    [Fact]
    public void ARememberedNoteBelongsToTheProjectUnlessMadeGlobal() => Run(host =>
    {
        var vm = host.NewSession(Project("main"));
        host.Send("/remember the staging server is called orion", vm.Id);
        host.Send("/remember global: I prefer tabs over spaces", vm.Id);

        var notes = host.Memory.All();
        Assert.Equal(2, notes.Count);
        var project = Assert.Single(notes, n => n.Body.Contains("orion"));
        Assert.NotNull(project.Project);
        var global = Assert.Single(notes, n => n.Body.Contains("tabs"));
        Assert.Null(global.Project);
        Assert.Contains(Notes(vm), n => n.Contains("in this project"));
        Assert.Contains(Notes(vm), n => n.Contains("in every chat"));

        // Another project sees only the global one.
        var other = host.Memory.Search("staging server orion", new MemoryQueryOptions { Project = Project("elsewhere"), MinRelevance = 0.01 });
        Assert.Empty(other);
        Assert.NotEmpty(host.Memory.Search("prefer tabs", new MemoryQueryOptions { Project = Project("elsewhere"), MinRelevance = 0.01 }));
        return Task.CompletedTask;
    });

    [Fact]
    public void ARunThatEndsAtTheSafetyLimitSaysSoOnceAndAMessageCarriesOn() => Run(async host =>
    {
        // Every step is different, so no repeat detector fires; only the backstop can end this run.
        _server.Reset((_, n) => new FakeReply.ToolCall("todo_write", $$"""{"todos":[{"content":"step {{n}}","status":"in_progress"}]}"""));
        var vm = host.NewSession(Project("main"));
        host.Send("never finish", vm.Id);
        await WaitUntil("stopped", () => !vm.Running && _server.Seen.Count >= 360, 60);

        Assert.Equal(12 * 30, _server.Seen.Count);
        Assert.Contains(Notes(vm), n => n.Contains("Stopped after 360 steps"));

        _server.Reset((_, _) => new FakeReply.Text("Carrying on, and done."));
        host.Send("keep going", vm.Id);
        await WaitUntil("carried on", () => !vm.Running && Replies(vm).Contains("Carrying on, and done."));
    });
}
