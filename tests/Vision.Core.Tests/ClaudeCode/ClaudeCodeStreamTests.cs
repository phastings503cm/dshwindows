using System.Text.Json.Nodes;
using Vision.Core;

namespace Vision.Core.Tests;

/// <summary>Claude Code's stream-json, as the real CLI wrote it (Fixtures/ClaudeCode), becomes Vision's engine events: the
/// main conversation's text, thinking, tool cards, file changes, todo lists and plans; the turn's session, model, usage,
/// cost and window; and the failures it reports, in plain words.</summary>
public sealed class ClaudeCodeStreamTests
{
    private sealed class Recorder
    {
        public List<EngineEvent> Events { get; } = [];
        public List<string> Notices { get; } = [];
        public List<string?> Activity { get; } = [];
        public List<ClaudeCodePermissionRequest> Questions { get; } = [];
        public List<(string Id, string Subtype)> Unhandled { get; } = [];
        public ClaudeCodeStream Stream { get; }

        public Recorder(string? cwd = "/work/proj2") =>
            Stream = new ClaudeCodeStream(Events.Add, cwd, Activity.Add, Notices.Add);

        public Recorder Feed(IEnumerable<string> lines)
        {
            foreach (var line in lines)
            {
                if (Stream.Feed(line, out var unhandled) is { } question) Questions.Add(question);
                if (unhandled is { } u) Unhandled.Add(u);
            }
            return this;
        }

        public T Single<T>() where T : EngineEvent => Assert.Single(Events.OfType<T>());
    }

    private static Recorder Replay(string fixture) => new Recorder().Feed(ClaudeFixtures.Lines(fixture));

    [Fact]
    public void AWriteTurnBecomesTextThinkingToolCardsAFileChangeAndUsage()
    {
        var r = Replay("write-partial.jsonl");
        var s = r.Stream;
        Assert.Equal("1bbd318c-7964-4079-9816-4fbe5b039d85", s.SessionId);
        Assert.Equal("claude-sonnet-5-5", s.Model);
        Assert.Equal("2.1.299", s.Version);
        Assert.Equal("ANTHROPIC_API_KEY", s.ApiKeySource);

        // Streamed text arrives as deltas, then each finished text block closes its bubble with the whole text.
        Assert.Equal("I'll create the file.", string.Concat(r.Events.TakeWhile(e => e is not EngineEvent.ToolStarted).OfType<EngineEvent.TextDelta>().Select(d => d.Text)));
        Assert.Equal("The user wants a file. I'll write it.", string.Concat(r.Events.OfType<EngineEvent.ReasoningDelta>().Select(d => d.Text)));
        var replies = r.Events.OfType<EngineEvent.AssistantMessage>().Select(m => m.Text).ToList();
        Assert.Equal(["I'll create the file.", "Done — the file is written."], replies);
        Assert.Equal("Done — the file is written.", s.LastReply);

        // Tool cards: the todo list (this CLI refused TodoWrite — the card says so) and the Write that created the file.
        var started = r.Events.OfType<EngineEvent.ToolStarted>().ToList();
        Assert.Equal(["todo_write", "write_file"], started.Select(t => t.Name));
        Assert.Equal("2 steps", started[0].Preview);
        Assert.Equal("hello.txt", started[1].Preview); // relative to the chat's folder
        var todos = r.Single<EngineEvent.Todos>();
        Assert.Equal([("Write hello.txt", TodoStatus.InProgress), ("Check it", TodoStatus.Pending)], todos.Items.Select(t => (t.Content, t.Status)));
        var finished = r.Events.OfType<EngineEvent.ToolFinished>().ToList();
        Assert.False(finished[0].Ok);
        Assert.StartsWith("Error: No such tool available: TodoWrite", finished[0].Summary);
        Assert.True(finished[1].Ok);
        Assert.Equal("Created hello.txt", finished[1].Summary);
        Assert.Contains("File created successfully", finished[1].Output);
        var change = Assert.Single(r.Single<EngineEvent.FilesChanged>().Changes);
        Assert.Equal(new FileChange("/work/proj2/hello.txt", FileChangeKind.Created), change);

        // The result: this turn's tokens (input + cached + new cache), the CLI's cost estimate and the model's window.
        var result = s.Result!;
        Assert.False(result.IsError);
        Assert.Equal(3, result.NumTurns);
        Assert.Equal(0.00794, result.TotalCostUsd);
        Assert.Equal(1_000_000, result.ContextWindow);
        Assert.Equal(new LlmUsage(2400 + 8000 + 600, 84, 8000), result.Usage);
        Assert.Equal(1200 + 4000 + 300, s.LastPromptTokens);
        Assert.Equal(result.Usage, r.Single<EngineEvent.Finished>().Usage);
        // Waiting on Claude shows as the working line, cleared when the turn ends.
        Assert.Contains("Waiting for Claude…", r.Activity);
        Assert.Null(r.Activity[^1]);
    }

    [Fact]
    public void EventsComeInTheOrderTheChatShowsThem()
    {
        var kinds = Replay("write-partial.jsonl").Events.Select(e => e.GetType().Name).Distinct().ToList();
        // The first reply streams and closes before any tool card; the turn ends with Finished.
        Assert.True(kinds.IndexOf("TextDelta") < kinds.IndexOf("ToolStarted"));
        Assert.True(kinds.IndexOf("AssistantMessage") < kinds.IndexOf("ToolStarted"));
        Assert.Equal("Finished", kinds[^1]);
    }

    [Fact]
    public void AReadThenEditShowsLineCountsAndTheDiffAndAsksPermissionForTheEdit()
    {
        var r = Replay("read-edit.jsonl");
        var finished = r.Events.OfType<EngineEvent.ToolFinished>().ToList();
        Assert.Equal(["read_file", "edit"], finished.Select(f => f.Name));
        Assert.Equal("Read 2 lines of hello.txt", finished[0].Summary);
        Assert.Equal("Edited hello.txt (+1 −1)", finished[1].Summary);
        Assert.Equal(new FileChange("/work/proj2/hello.txt", FileChangeKind.Modified), Assert.Single(r.Single<EngineEvent.FilesChanged>().Changes));
        var question = Assert.Single(r.Questions);
        Assert.Equal("Edit", question.ToolName);
        Assert.Equal("toolu_fixture_2_0", question.ToolUseId);
        Assert.Equal("hello there", (string?)question.Input?["new_string"]);
        Assert.Equal("claude-haiku-5-5", r.Stream.Model);
    }

    [Fact]
    public void ACommandThatNeedsPermissionComesBackAsAQuestionAndADenialIsCounted()
    {
        var allowed = Replay("bash-allow.jsonl");
        var question = Assert.Single(allowed.Questions);
        Assert.Equal("Bash", question.ToolName);
        Assert.Equal("mkdir -p made-by-allow && touch made-by-allow/a.txt", (string?)question.Input?["command"]);
        Assert.Equal("subcommandResults", question.DecisionReasonType);
        var ran = allowed.Events.OfType<EngineEvent.ToolFinished>().Single();
        Assert.True(ran.Ok);
        Assert.Equal("Done (no output)", ran.Summary);
        Assert.Equal(0, allowed.Stream.Result!.Denials);

        var denied = Replay("bash-deny.jsonl");
        var refused = denied.Events.OfType<EngineEvent.ToolFinished>().Single();
        Assert.False(refused.Ok);
        Assert.Equal("The person said no in Vision.", refused.Summary);
        Assert.Equal(1, denied.Stream.Result!.Denials);
    }

    [Fact]
    public void APlanModeTurnProposesItsPlan()
    {
        var r = Replay("plan-exit.jsonl");
        Assert.Equal("1. Read Stock.cs\n2. Add a test\n3. Run dotnet test", r.Single<EngineEvent.PlanProposed>().Text);
        var question = Assert.Single(r.Questions);
        Assert.Equal("ExitPlanMode", question.ToolName);
        Assert.True(question.RequiresUserInteraction);
        Assert.Equal("plan", r.Stream.PermissionMode);
        // The CLI's "informational" warning becomes a note in the chat.
        Assert.Contains(r.Notices, n => n.StartsWith("Claude Code: ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInterruptedTurnSaysSoAndKeepsWhatWasWritten()
    {
        var r = Replay("interrupt.jsonl");
        var result = r.Stream.Result!;
        Assert.True(result.IsError);
        Assert.True(result.Interrupted);
        Assert.Equal("error_during_execution", result.Subtype);
        Assert.Equal("Starting a", r.Single<EngineEvent.AssistantMessage>().Text);
        Assert.Empty(r.Events.OfType<EngineEvent.Finished>());
    }

    [Fact]
    public void CompactingSaysHowMuchItFreed()
    {
        var r = Replay("compact.jsonl");
        Assert.Contains(r.Notices, n => n.StartsWith("Claude Code compacted the conversation: ~", StringComparison.Ordinal) && n.Contains("tokens"));
        Assert.Contains("Claude Code is compacting the conversation…", r.Activity);
        Assert.Equal("compact", r.Stream.Result!.LocalCommand);
        // The compaction summary and "<local-command-stdout>" lines are not rows.
        Assert.Empty(r.Events.OfType<EngineEvent.AssistantMessage>());
    }

    [Fact]
    public void AResumedTurnKeepsTheSessionAndItsCostAddsUp()
    {
        var first = Replay("write-partial.jsonl").Stream;
        var second = Replay("resume.jsonl").Stream;
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.True(second.Result!.TotalCostUsd > first.Result!.TotalCostUsd);
        Assert.Equal(new LlmUsage(1200 + 4000 + 300, 42, 4000), second.Result.Usage); // this turn's own tokens
    }

    [Fact]
    public void FailuresAreSaidInPlainWords()
    {
        var notSignedIn = Replay("not-signed-in.jsonl").Stream;
        Assert.Equal("authentication_failed", notSignedIn.ApiError);
        Assert.Empty(Replay("not-signed-in.jsonl").Events.OfType<EngineEvent.AssistantMessage>()); // the CLI's error isn't a reply
        var failure = ClaudeCodeRunner.Classify(notSignedIn, "", 1)!;
        Assert.Equal(ClaudeCodeFailureKind.NotSignedIn, failure.Kind);
        Assert.Contains("claude auth login", failure.Message);

        Assert.Equal(ClaudeCodeFailureKind.ResumeMissing, ClaudeCodeRunner.Classify(Replay("resume-missing.jsonl").Stream, "", 1)!.Kind);

        var empty = new ClaudeCodeStream(_ => { });
        Assert.Equal(ClaudeCodeFailureKind.SessionInUse,
            ClaudeCodeRunner.Classify(empty, "Error: Session ID 1bbd318c-7964-4079-9816-4fbe5b039d85 is already in use.\n", 1)!.Kind);
        var unknown = ClaudeCodeRunner.Classify(empty, "error: unknown option '--effort'\n", 1)!;
        Assert.Equal((ClaudeCodeFailureKind.UnknownOption, "--effort"), (unknown.Kind, unknown.Detail));
        var crashed = ClaudeCodeRunner.Classify(empty, "", 3)!;
        Assert.Equal(ClaudeCodeFailureKind.Failed, crashed.Kind);
        Assert.Contains("exit code 3", crashed.Message);
        Assert.Null(ClaudeCodeRunner.Classify(Replay("write-partial.jsonl").Stream, "", 0));
    }

    [Fact]
    public void ASubscriptionLimitWarnsOnceAndARejectionStopsTheTurnWithItsResetTime()
    {
        var r = new Recorder();
        var resets = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        r.Feed([
            $$"""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.82,"resetsAt":{{resets}}},"uuid":"u","session_id":"s"}""",
            $$"""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","rateLimitType":"five_hour","utilization":0.9,"resetsAt":{{resets}}},"uuid":"u","session_id":"s"}""",
        ]);
        Assert.StartsWith("Your Claude plan: 82% of your five-hour limit used — it resets", Assert.Single(r.Notices));
        r.Feed([
            $$"""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"seven_day","resetsAt":{{resets}}},"uuid":"u","session_id":"s"}""",
            """{"type":"result","subtype":"success","is_error":true,"result":"Claude usage limit reached","session_id":"s","num_turns":1}""",
        ]);
        var failure = ClaudeCodeRunner.Classify(r.Stream, "", 1)!;
        Assert.Equal(ClaudeCodeFailureKind.RateLimited, failure.Kind);
        Assert.Contains("weekly limit", failure.Message);
    }

    [Fact]
    public void ARetryShowsAndRecoversAndSubagentStepsAreOnlyTheWorkingLine()
    {
        var r = new Recorder();
        r.Feed([
            """{"type":"system","subtype":"api_retry","attempt":1,"max_retries":10,"retry_delay_ms":2000,"error_status":529,"error":"overloaded","uuid":"u","session_id":"s"}""",
            """{"type":"stream_event","event":{"type":"message_start","message":{"id":"m"}},"parent_tool_use_id":null,"uuid":"u","session_id":"s"}""",
            """{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"t1","name":"Read","input":{"file_path":"/work/proj2/src/Stock.cs"}}]},"parent_tool_use_id":"toolu_agent","uuid":"u","session_id":"s"}""",
        ]);
        var retry = Assert.IsType<EngineEvent.Retrying>(r.Events[0]);
        Assert.Equal((1, TimeSpan.FromSeconds(2), "Claude Code: overloaded, HTTP 529"), (retry.Attempt, retry.Delay, retry.Reason));
        Assert.Equal(1, Assert.IsType<EngineEvent.Recovered>(r.Events[1]).Attempts);
        Assert.Equal(2, r.Events.Count); // the subagent's Read is not a card of the chat's own
        Assert.Equal("Subagent: Read " + Path.Combine("src", "Stock.cs"), r.Activity[^1]); // relative, in the OS's own separators
    }

    [Fact]
    public void TheNewTaskToolsKeepAPlanList()
    {
        var r = new Recorder();
        r.Feed([
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"c1","name":"TaskCreate","input":{"subject":"Write the test","description":"x"}}]},"parent_tool_use_id":null,"uuid":"a","session_id":"s"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"c1","content":"Task #1 created"}]},"parent_tool_use_id":null,"tool_use_result":{"task":{"id":"1","subject":"Write the test"}},"uuid":"b","session_id":"s"}""",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"c2","name":"TaskUpdate","input":{"taskId":"1","status":"in_progress"}}]},"parent_tool_use_id":null,"uuid":"c","session_id":"s"}""",
        ]);
        var lists = r.Events.OfType<EngineEvent.Todos>().ToList();
        Assert.Equal(2, lists.Count);
        Assert.Equal(("Write the test", TodoStatus.Pending), (lists[0].Items[0].Content, lists[0].Items[0].Status));
        Assert.Equal(TodoStatus.InProgress, lists[1].Items[0].Status);
    }

    [Fact]
    public void ToolCallsLeftWithoutAResultAreClosedAndUnhandledControlRequestsAreReported()
    {
        var r = new Recorder();
        r.Feed([
            """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t9","name":"Bash","input":{"command":"sleep 100"}}]},"parent_tool_use_id":null,"uuid":"a","session_id":"s"}""",
            """{"type":"control_request","request_id":"r1","request":{"subtype":"hook_callback","callback_id":"x"}}""",
            "not json at all",
        ]);
        Assert.Equal(("r1", "hook_callback"), Assert.Single(r.Unhandled));
        Assert.Contains("not json at all", r.Stream.Unparsed.ToString());
        r.Stream.CloseDangling("Stopped before it finished");
        var closed = r.Single<EngineEvent.ToolFinished>();
        Assert.Equal(("t9", false, "Stopped before it finished"), (closed.Id, closed.Ok, closed.Summary));
        r.Stream.CloseDangling("again");
        Assert.Single(r.Events.OfType<EngineEvent.ToolFinished>()); // once only
    }

    [Fact]
    public void WslPathsAreShownAsWindowsPaths()
    {
        var events = new List<EngineEvent>();
        var stream = new ClaudeCodeStream(events.Add, mapPath: ClaudeCodeCli.FromWslPath);
        stream.Feed("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"w","name":"Write","input":{"file_path":"/mnt/c/Users/pat/proj/a.txt","content":"x"}}]},"parent_tool_use_id":null,"uuid":"a","session_id":"s"}""");
        stream.Feed("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"w","content":"ok"}]},"parent_tool_use_id":null,"tool_use_result":{"type":"create","filePath":"/mnt/c/Users/pat/proj/a.txt"},"uuid":"b","session_id":"s"}""");
        Assert.Equal(@"C:\Users\pat\proj\a.txt", Assert.Single(events.OfType<EngineEvent.FilesChanged>()).Changes[0].Path);
        Assert.Equal("/home/pat/x", ClaudeCodeCli.FromWslPath("/home/pat/x"));
    }
}
