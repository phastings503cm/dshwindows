using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using Vision.App.Model;
using Vision.Core;
using MessageRole = Vision.App.Model.MessageRole;

namespace Vision.App.Tests;

/// <summary>Items 6, 11 and 2 of 10-18 end to end through AgentHost against a fake model server: the turn's phases and the
/// Prompts timeline (what was asked and how it ended, on disk), the validation loop-back of a /goal, "Add to tasks" while the
/// agent works, the activity list, and a subagent really running on its worker route.</summary>
public sealed class RunsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"vision-runs-{Guid.NewGuid():N}");
    private readonly FakeModelServer _server = new();

    public RunsTests()
    {
        Directory.CreateDirectory(_dir);
        _server.Reset((_, _) => new FakeReply.Text("Done."));
        AgentHost.QueueBusyPoll = TimeSpan.FromMilliseconds(50);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }

    private AgentHost MakeHost(HttpMessageHandler? handler = null, Action<AppConfig>? configure = null)
    {
        var config = new AppConfig(Path.Combine(_dir, "settings.json"));
        if (config.ActiveProvider is null)
            config.Activate(new ProviderProfile(ProviderKind.OpenAICompat, "stub", "http://stub.test/v1", "stub-model"));
        config.Preset = PermissionPreset.FullAccess.RawValue();
        config.ComputerToolsEnabled = false;
        configure?.Invoke(config);
        return new AgentHost(config, new ConversationLog(Path.Combine(_dir, "log")), Dispatcher.CurrentDispatcher,
            queueFile: Path.Combine(_dir, "task-queue.json"),
            skillLocations: new SkillLocations(_dir, Path.Combine(_dir, "support")),
            vault: new CredentialVault(Path.Combine(_dir, "vault"), new MemoryBlobStore()),
            memory: new MemoryStore(Path.Combine(_dir, "memory")))
        {
            HttpHandlerForTesting = handler ?? _server,
            RetryPolicy = new RetryPolicy { Delay = _ => TimeSpan.FromMilliseconds(50) },
            GoalErrorBackoff = _ => TimeSpan.FromMilliseconds(50),
        };
    }

    private void Run(Func<AgentHost, Task> body, HttpMessageHandler? handler = null) => UiThread.Run(async () =>
    {
        var host = MakeHost(handler);
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

    private SessionVM Chat(AgentHost host)
    {
        var folder = Path.Combine(_dir, "project");
        Directory.CreateDirectory(folder);
        return host.NewSession(folder);
    }

    private static async Task WaitUntil(string what, Func<bool> condition, double seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    private static List<string> UserRows(SessionVM vm) =>
        vm.Entries.OfType<MessageEntryVM>().Where(m => m.Role == MessageRole.User).Select(m => m.Text).ToList();

    // MARK: Phases and the Prompts timeline

    [Fact]
    public void APromptIsRecordedWithHowItEndedAndTheTurnShowsItsResults() => Run(async host =>
    {
        var chat = Chat(host);
        host.Send("What does the report print?", chat.Id);
        await WaitUntil("turn done", () => !chat.Running && chat.PromptHistory.Count == 1 && chat.PromptHistory[0].Outcome != PromptOutcome.Running);

        var record = chat.PromptHistory.Single();
        Assert.Equal(("What does the report print?", PromptSource.Typed, PromptOutcome.Completed), (record.Text, record.Source, record.Outcome));
        Assert.Equal("Completed with results", record.Headline); // the closing summary is its evidence
        Assert.Equal(("stub-model", "stub"), (record.Model, record.Route));
        Assert.Equal("What does the report print?", record.EntryText);
        Assert.True(record.EtaSeconds > 0);
        Assert.Equal((100L, 20L), (record.PromptTokens, record.CompletionTokens));
        Assert.Contains("Validated: the reply matches what was asked.", record.CompletionNote);

        var phase = chat.TurnPhase!;
        Assert.Equal(TurnPhase.Results, phase.Phase);
        Assert.Equal("Completed with results", phase.Headline);
        Assert.Equal(record.TurnId, phase.TurnId);

        // On disk, in the Mac's format, and the ETA cache beside it.
        var file = JsonNode.Parse(File.ReadAllText(host.PromptStore.FilePath(chat.Id)))!;
        Assert.Equal("vision.prompts/1", (string)file["schema"]!);
        Assert.Equal("completed", (string)file["prompts"]![0]!["outcome"]!);
        Assert.True(File.Exists(Path.Combine(_dir, TurnPhaseRunner.FileName)));
        // The jump finds its row.
        Assert.Equal(chat.Entries.OfType<MessageEntryVM>().First(m => m.IsUser).Id, host.EntryIdFor(record, chat));
    });

    [Fact]
    public void AReplyThatReportsAnErrorFinishesWithIssues() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Text("I tried.\nError: the build failed on Stock.cs"));
        var chat = Chat(host);
        host.Send("Build it", chat.Id);
        await WaitUntil("turn done", () => !chat.Running && chat.PromptHistory.SingleOrDefault()?.Outcome is not (null or PromptOutcome.Running));
        var record = chat.PromptHistory.Single();
        Assert.Equal(PromptOutcome.Issues, record.Outcome);
        Assert.Equal("the reply reports an error (Error:)", record.Reason);
        Assert.Equal("Finished with issues", chat.TurnPhase!.Headline);
        Assert.Single(_server.Seen); // a plain turn is never re-run by itself
    });

    [Fact]
    public void AStoppedPromptIsRecordedAsStopped() => Run(async host =>
    {
        var gate = new TaskCompletionSource();
        _server.Reset((_, _) => new FakeReply.Gated(gate.Task, "late"));
        var chat = Chat(host);
        host.Send("Take your time", chat.Id);
        await WaitUntil("request out", () => _server.Seen.Count == 1);
        host.StopSession(chat.Id);
        await WaitUntil("stopped", () => !chat.Running && chat.PromptHistory.Single().Outcome != PromptOutcome.Running);
        Assert.Equal(PromptOutcome.Stopped, chat.PromptHistory.Single().Outcome);
        Assert.Equal("Stopped before finishing.", chat.TurnPhase!.EndedReason);
        gate.SetResult();
    });

    [Fact]
    public void AGoalThatReportsAnErrorIsSentBackUntilItPasses() => Run(async host =>
    {
        _server.Reset((seen, _) => seen.LastUser.Contains("Validation of your work found a problem", StringComparison.Ordinal)
            ? new FakeReply.Text("Fixed it; all 14 tests pass.\nGOAL_COMPLETE")
            : new FakeReply.Text("Error: 2 tests failed\nGOAL_COMPLETE"));
        var chat = Chat(host);
        host.Send("/goal Make the tests pass", chat.Id);
        await WaitUntil("goal done", () => !chat.Running && chat.PromptHistory.SingleOrDefault()?.Outcome is PromptOutcome.Completed or PromptOutcome.Issues);

        Assert.Contains("↻ validation: fix and check again (attempt 2)", UserRows(chat));
        var record = chat.PromptHistory.Single();
        Assert.Equal((PromptSource.Goal, PromptOutcome.Completed, 2), (record.Source, record.Outcome, record.Attempts));
        Assert.Equal("Make the tests pass", record.Text);
        Assert.Null(chat.TurnPhase!.LastValidationFailure);
        Assert.Equal(1, chat.TurnPhase.Loopbacks);
        Assert.Contains(chat.Entries.OfType<MessageEntryVM>(), m => m.Text.StartsWith("⚠️ Validation failed: the reply reports an error (Error:)", StringComparison.Ordinal));
        // /goal's own strings are untouched: a finished goal still reads as finished.
        Assert.Null(AgentHost.LastGoalIn(chat.Entries));
    });

    [Fact]
    public void AGoalThatMakesNoProgressStopsAfterTwoMoreAttempts() => Run(async host =>
    {
        _server.Reset((_, _) => new FakeReply.Text("Error: still failing\nGOAL_COMPLETE"));
        var chat = Chat(host);
        host.Send("/goal Make the tests pass", chat.Id);
        await WaitUntil("goal done", () => !chat.Running && chat.PromptHistory.SingleOrDefault()?.Outcome is not (null or PromptOutcome.Running));
        var record = chat.PromptHistory.Single();
        Assert.Equal((PromptOutcome.Issues, 3), (record.Outcome, record.Attempts));
        Assert.Equal(3, _server.Seen.Count);
        Assert.Equal("Finished with issues", chat.TurnPhase!.Headline);
        Assert.Contains(chat.TurnPhase.Evidence, e => e.Title == "Validation stopped: no progress in two attempts");
        Assert.Contains(chat.Entries.OfType<MessageEntryVM>(), m => m.Text.StartsWith("✅ Goal complete after 3 rounds — but validation still found a problem", StringComparison.Ordinal));
    });

    [Fact]
    public void TheValidationRetryCapIsHonoured() => UiThread.Run(async () =>
    {
        var host = MakeHost(configure: c => c.ValidationRetryCap = 1);
        try
        {
            var replies = 0;
            _server.Reset((_, _) => new FakeReply.Text($"Error: attempt {++replies} failed\nGOAL_COMPLETE"));
            var chat = Chat(host);
            host.Send("/goal Make the tests pass", chat.Id);
            await WaitUntil("goal done", () => !chat.Running && chat.PromptHistory.SingleOrDefault()?.Outcome is not (null or PromptOutcome.Running));
            Assert.Equal(2, _server.Seen.Count); // the first try and one retry
            Assert.Contains(chat.TurnPhase!.Evidence, e => e.Title == "Validation hit the retry cap");
        }
        finally
        {
            host.StopAll();
        }
    });

    [Fact]
    public void AChatFromBeforeTheTimelineIsBackfilledOnce() => UiThread.Run(async () =>
    {
        var first = MakeHost();
        var chat = Chat(first);
        first.Log.RecordItem(chat.Id, "user", "🎯 /goal Ship 1.0");
        first.Log.RecordItem(chat.Id, "assistant", "Shipped.");
        first.Log.RecordItem(chat.Id, "notice", "✅ Goal complete after 1 round.");
        first.Log.RecordItem(chat.Id, "user", "Thanks");
        var id = chat.Id;
        first.StopAll();
        await Task.Delay(20);

        var second = MakeHost();
        var all = second.AllPromptRecords();
        Assert.Equal([("Ship 1.0", PromptSource.Goal, PromptOutcome.Completed), ("Thanks", PromptSource.Typed, PromptOutcome.Unknown)],
            all.Where(r => r.ChatId == id).Select(r => (r.Text, r.Source, r.Outcome)));
        Assert.True(File.Exists(second.PromptStore.FilePath(id)));
        second.StopAll();
    });

    // MARK: Add to tasks

    [Fact]
    public void APromptAddedWhileTheAgentWorksRunsByItselfAfter() => Run(async host =>
    {
        var gate = new TaskCompletionSource();
        _server.Reset((seen, _) => seen.LastUser.Contains("first", StringComparison.Ordinal) ? new FakeReply.Gated(gate.Task, "First done.") : new FakeReply.Text("Second done."));
        var chat = Chat(host);
        host.Send("the first thing", chat.Id);
        await WaitUntil("first out", () => _server.Seen.Count == 1 && chat.Running);

        var task = host.AddToTasks(chat.Id, "the second thing\nwith details")!;
        Assert.True(task.AsPrompt);
        Assert.Equal("the second thing", task.Title);
        Assert.Contains(chat.Entries.OfType<MessageEntryVM>(), m => m.Text == "📋 Added to this chat's tasks (#1) — it starts when the current work finishes.");
        // The chat's first task is the running prompt; the added one waits behind it.
        Assert.Equal(("the first thing", "Prompt"), (host.CurrentWorkFor(chat)!.Title, host.CurrentWorkFor(chat)!.Kind));
        Assert.Equal(QueueTaskStatus.Queued, host.Queue.Find(task.Id)!.Status);

        gate.SetResult();
        await WaitUntil("second ran", () => host.Queue.Find(task.Id)?.Status == QueueTaskStatus.Complete);
        // It was sent as the chat's next prompt, exactly as written — not as an unattended goal.
        var second = _server.Seen.Last(r => r.Fresh);
        Assert.Contains("the second thing\nwith details", second.LastUser);
        Assert.DoesNotContain("GOAL:", second.LastUser);
        Assert.Equal(["the first thing", "the second thing\nwith details"], UserRows(chat));
        await WaitUntil("recorded", () => chat.PromptHistory.Count == 2 && chat.PromptHistory[1].Outcome == PromptOutcome.Completed);
        Assert.Equal(PromptSource.Task, chat.PromptHistory[1].Source);
        await WaitUntil("list stopped", () => !host.IsQueueRunning(chat.Id));
        Assert.DoesNotContain(chat.Id, host.Config.QueueResumeChats);
    });

    [Fact]
    public void QueuedPromptsRunInOrderAndAStopHoldsTheRest() => Run(async host =>
    {
        var gate = new TaskCompletionSource();
        _server.Reset((seen, _) => seen.LastUser.Contains("slow", StringComparison.Ordinal) ? new FakeReply.Gated(gate.Task, "ok") : new FakeReply.Text("ok"));
        var chat = Chat(host);
        host.Send("slow one", chat.Id);
        await WaitUntil("out", () => chat.Running && _server.Seen.Count == 1);
        var a = host.AddToTasks(chat.Id, "task a")!;
        var b = host.AddToTasks(chat.Id, "task b")!;
        host.QueueMoveBy(b.Id, -1); // reorder while it runs: b first
        host.StopSession(chat.Id);
        await WaitUntil("stopped", () => !chat.Running);
        Assert.Equal("Paused — you stopped the current work.", host.ChatTasksPaused(chat.Id));
        await Task.Delay(300);
        Assert.Equal((QueueTaskStatus.Queued, QueueTaskStatus.Queued), (host.Queue.Find(a.Id)!.Status, host.Queue.Find(b.Id)!.Status));
        Assert.Single(_server.Seen);

        host.ResumeChatTasks(chat.Id);
        await WaitUntil("both ran", () => host.Queue.Find(a.Id)?.Status == QueueTaskStatus.Complete && host.Queue.Find(b.Id)?.Status == QueueTaskStatus.Complete);
        // The stopped prompt never reached the model, so its row says so; the queued ones ran in their new order.
        Assert.Equal(["task b", "task a"], UserRows(chat));
        Assert.Contains(chat.Entries.OfType<MessageEntryVM>(), m => m.Text == "Not delivered to the model: slow one");
        // Its record still finds that row.
        var stopped = chat.PromptHistory.First(r => r.Text == "slow one");
        Assert.Equal(PromptOutcome.Stopped, stopped.Outcome);
        Assert.NotNull(host.EntryIdFor(stopped, chat));
        gate.TrySetResult();
    });

    [Fact]
    public void AFailedPromptTaskPausesTheList() => Run(async host =>
    {
        _server.Reset((seen, _) => seen.LastUser.Contains("bad", StringComparison.Ordinal) ? new FakeReply.Http(400, """{"error":{"message":"bad request"}}""") : new FakeReply.Text("ok"));
        var chat = Chat(host);
        var bad = host.AddToTasks(chat.Id, "bad one")!;
        var next = host.AddToTasks(chat.Id, "next one")!;
        await WaitUntil("bad failed", () => host.Queue.Find(bad.Id)?.Status == QueueTaskStatus.Failed);
        await WaitUntil("paused", () => host.ChatTasksPaused(chat.Id) == "Paused — the last task failed.");
        await Task.Delay(200);
        Assert.Equal(QueueTaskStatus.Queued, host.Queue.Find(next.Id)!.Status);
    });

    [Fact]
    public void PromptTasksWaitForResumeAfterARelaunch() => UiThread.Run(async () =>
    {
        var first = MakeHost();
        var chat = Chat(first);
        first.Queue.Add("later", "later", chatId: chat.Id, asPrompt: true);
        var id = chat.Id;
        first.StopAll();
        await Task.Delay(20);
        var second = MakeHost();
        Assert.Equal(ChatTaskRules.PausedAtLaunch, second.ChatTasksPaused(id));
        await Task.Delay(200);
        Assert.Empty(_server.Seen);
        second.StopAll();
    });

    // MARK: Background activity

    [Fact]
    public void TheActivityListShowsTheMainAgentWithItsModelAndWhatItWasGiven() => Run(async host =>
    {
        var gate = new TaskCompletionSource();
        _server.Reset((_, _) => new FakeReply.Gated(gate.Task, "ok"));
        var chat = Chat(host);
        host.Send("/goal Ship the report", chat.Id);
        await WaitUntil("out", () => _server.Seen.Count == 1);
        host.AddToTasks(chat.Id, "then the docs");
        var items = host.ActivityItems();
        var main = items.Single(i => i.Kind == ActivityKind.MainAgent);
        Assert.Equal(("Goal: Ship the report", "stub-model on stub"), (main.Assignment, main.Placement));
        Assert.StartsWith("Planning · round 1", main.Status);
        Assert.Contains(items, i => i.Kind == ActivityKind.QueueRunner && i.Status == "1 waiting");
        Assert.Equal(2, host.ActivityCount()); // the main agent and the chat's task list
        host.StopActivity(main);
        await WaitUntil("stopped", () => !chat.Running);
        gate.TrySetResult();
    });

    // MARK: Subagents on their route

    /// <summary>The fake server, with a hosted worker (api.kimi.com) whose key can't list models — and every request body kept.</summary>
    private sealed class WorkerHandler(FakeModelServer inner) : DelegatingHandler(inner)
    {
        public List<(string Host, string Body)> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? "";
            if (host == "api.kimi.com" && request.RequestUri!.AbsolutePath.Contains("model", StringComparison.Ordinal) && !request.RequestUri.AbsolutePath.EndsWith("completions", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Length > 0)
                lock (Bodies) Bodies.Add((host, body));
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public void ASubagentRunsOnItsHostedWorkerWithThatRoutesSettings() => UiThread.Run(async () =>
    {
        var handler = new WorkerHandler(_server);
        var host = MakeHost(handler, config =>
        {
            config.Providers[0].Temperature = 0.6;
            config.Providers.Add(new ProviderProfile(ProviderKind.OpenAICompat, "Kimi Code", "https://api.kimi.com/coding/v1", "kimi-for-coding") { SubagentWorker = true });
        });
        try
        {
            host.RefreshFleet();
            _server.Reset((seen, _) => seen.Host == "api.kimi.com" ? new FakeReply.Text("worker report")
                : seen.Messages.Any(m => m.Role == "tool") ? new FakeReply.Text("All done.")
                : new FakeReply.ToolCall("agent", """{"description":"Find callers","prompt":"find every caller of Remove"}"""));
            var chat = Chat(host);
            host.Send("Find the callers", chat.Id);
            await WaitUntil("turn done", () => !chat.Running && chat.PromptHistory.SingleOrDefault()?.Outcome == PromptOutcome.Completed);

            // It ran on the worker — the hosted route whose key couldn't list its models — not on the main model.
            var worker = handler.Bodies.Where(b => b.Host == "api.kimi.com").Select(b => JsonNode.Parse(b.Body)!).ToList();
            var request = Assert.Single(worker);
            Assert.Equal("kimi-for-coding", (string)request["model"]!);
            Assert.Null(request["temperature"]);  // the main route's 0.6 is the main route's
            var main = handler.Bodies.Where(b => b.Host == "stub.test").Select(b => JsonNode.Parse(b.Body)!.AsObject()).ToList();
            Assert.True(main.Any(b => b["temperature"] is not null),
                "the main requests carry the main route's temperature; their keys: " + string.Join(" | ", main.Select(b => string.Join(",", b.Select(p => p.Key)))));
            var run = Assert.Single(chat.Agents);
            Assert.Equal(("Kimi Code", "kimi-for-coding", "kimi-for-coding on Kimi Code"), (run.Route, run.Model, run.PlacementLabel));
            Assert.Null(run.Placement);
            Assert.Contains(run.Log, l => l.Text == "Running on kimi-for-coding · Kimi Code");
        }
        finally
        {
            host.StopAll();
        }
    });
}
