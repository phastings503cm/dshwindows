namespace Dsh.Core.Tests;

/// <summary>With the step limit gone as a stopping point, a run must still be able to end when it is stuck: the
/// repeat detector sees loops however they are dressed up (alternating calls, todo updates in between, cached
/// reads, refused calls, the same error with different arguments), a reply cut off mid-call is not run, "done"
/// counts only when the model has seen its other results, and a backstop bounds what nothing else catches.</summary>
public sealed class EngineSafetyNetTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-safety");

    public void Dispose() => _root.Dispose();

    private Engine MakeEngine(ILlmClient client, EngineConfig config, IEnumerable<IToolExecutor> tools,
                              PermissionPreset preset = PermissionPreset.WorkspaceWrite, PermissionGate? gate = null,
                              ToolCache? cache = null, CredentialVault? vault = null) =>
        new(client, new ToolRegistry(tools), "system", config, _root.Path, new PermissionPolicy(preset, _root.Path), gate ?? Gates.Allow)
        {
            Cache = cache,
            Vault = vault,
        };

    private static EngineConfig Config(int maxIterations = 100) =>
        new("test") { MaxIterations = maxIterations, ToolTimeout = TimeSpan.FromSeconds(5) };

    private static ToolCall Echo(int n, string text = "again") => new($"c{n}", "echo", $$"""{"text":"{{text}}"}""");

    /// <summary>A tool that counts how often it really ran and answers with fixed text.</summary>
    private sealed class FixedTool(string name, string output) : IToolExecutor
    {
        public int Runs;
        public string Name => name;
        public ToolSpec Spec => new(name, name, """{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Runs);
            return Task.FromResult(new ToolResult(output));
        }
    }

    // MARK: - Loops the detector must see

    [Fact]
    public async Task RereadingOneUnchangedFileForeverIsAStallEvenThoughTheCacheAlternatesItsAnswer()
    {
        File.WriteAllText(Path.Combine(_root.Path, "a.txt"), "one\ntwo\nthree\n");
        var turns = Enumerable.Range(1, 40).Select(n => Turn.Calling(new ToolCall($"c{n}", "read_file", """{"file_path":"a.txt"}""")));
        var client = new ScriptedClient(turns);

        var result = await MakeEngine(client, Config(), [new ReadFileTool()], cache: new ToolCache()).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(8, client.Requests.Count);
        var answers = result.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content ?? "").ToList();
        Assert.Contains(answers, a => ToolCache.IsUnchangedNote(a)); // the cache really did answer some of them
    }

    [Fact]
    public async Task TwoCallsTakingTurnsAreStillAStall()
    {
        var turns = Enumerable.Range(1, 60).Select(n => Turn.Calling(Echo(n, n % 2 == 1 ? "a" : "b")));
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(), [new EchoTool()]).RunAsync([], "go");

        Assert.True(result.Stalled);
        // "a" is the 8th time round on the 15th call.
        Assert.Equal(15, client.Requests.Count);
    }

    [Fact]
    public async Task ATodoUpdateBetweenTwoRepeatsDoesNotHideThem()
    {
        var todo = new ToolCall("t", "todo_write", """{"todos":[{"content":"x","status":"in_progress"}]}""");
        var turns = Enumerable.Range(1, 40).SelectMany(n => new[] { Turn.Calling(Echo(n)), Turn.Calling(todo with { Id = $"t{n}" }) });
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(), [new EchoTool(), new TodoWriteTool()]).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(15, client.Requests.Count); // 8 echoes and the 7 todo updates between them
    }

    [Fact]
    public async Task TheSameErrorForDifferentArgumentsIsAStallToo()
    {
        var failing = new FixedTool("edit", "Error: old_string not found in file.");
        var turns = Enumerable.Range(1, 40).Select(n => Turn.Calling(new ToolCall($"c{n}", "edit", $$"""{"old_string":"guess {{n}}"}""")));
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(), [failing]).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(12, client.Requests.Count); // more rope than for an identical call: the arguments did change
        Assert.Contains("failed with the same error", result.LastReplyText);
        var answers = result.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content ?? "").ToList();
        Assert.Contains("Harness note", answers[5]); // nudged at the 6th
        Assert.DoesNotContain("Harness note", answers[4]);
    }

    [Fact]
    public async Task ACallTheUserKeepsRefusingIsCounted()
    {
        var writer = new FixedTool("write_file", "written");
        var turns = Enumerable.Range(1, 40).Select(n => Turn.Calling(new ToolCall($"c{n}", "write_file", """{"file_path":"x.txt","content":"y"}""")));
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(), [writer], PermissionPreset.Plan, Gates.Deny).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(0, writer.Runs);
        Assert.Equal(8, client.Requests.Count);
        Assert.Equal(8, result.Messages.Count(m => m.Role == MessageRole.Tool)); // every refusal was still answered
    }

    [Fact]
    public async Task ACallForACredentialThatDoesNotExistIsCounted()
    {
        var shell = new FixedTool("run_shell_command", "ran");
        var turns = Enumerable.Range(1, 40).Select(n => Turn.Calling(new ToolCall($"c{n}", "run_shell_command", """{"command":"curl -H {{vault:NOPE}} x"}""")));
        var client = new ScriptedClient(turns);
        var vault = new CredentialVault(Path.Combine(_root.Path, "vault"), new MemoryBlobStore());

        var result = await MakeEngine(client, Config(), [shell], PermissionPreset.FullAccess, vault: vault).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(0, shell.Runs);
        Assert.Equal(8, client.Requests.Count);
    }

    [Fact]
    public void PollingWithAWaitIsNotAStall()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 19; i++)
            Assert.Equal(StallLevel.None, tracker.Observe("run_shell_command", """{"command":"Start-Sleep 60; gh run view 123"}""", "in progress"));
        // The same command without a wait is a hot loop.
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++) level = tracker.Observe("run_shell_command", """{"command":"gh run view 123"}""", "in progress");
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void OldRepeatsAgeOutOfTheWindow()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 3; i++) tracker.Observe("read_file", "{}", "same");
        // A long stretch of different work: the three repeats are forgotten.
        for (var i = 0; i < StallTracker.Window; i++) tracker.Observe("grep", $$"""{"pattern":"p{{i}}"}""", $"result {i}");
        Assert.Equal(StallLevel.None, tracker.Observe("read_file", "{}", "same"));
        Assert.Equal(1, tracker.Repeats);
    }

    // MARK: - Progress is not a loop

    /// <summary>A tool that reports a changed file the way write and edit do.</summary>
    private sealed class WriterTool(string name) : IToolExecutor
    {
        public string Name => name;
        public ToolSpec Spec => new(name, name, """{"type":"object","properties":{}}""");

        public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new ToolResult("edited") { Files = [new FileChange(Path.Combine(context.Workspace, "f.txt"), FileChangeKind.Modified)] });
    }

    private static StallLevel Check(StallTracker tracker, string command = "go build ./...", string output = "(no output)") =>
        tracker.Observe("run_shell_command", $$"""{"command":"{{command}}"}""", StallTracker.ResultKey(output), failed: false);

    [Fact]
    public void AQuietCheckAfterEachRealEditIsNotALoop()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(StallLevel.None, tracker.Observe("edit_file", $$"""{"file_path":"f{{i}}.go","old_string":"a","new_string":"b"}""",
                StallTracker.ResultKey("edited"), failed: false, changedFiles: true));
            Assert.Equal(StallLevel.None, Check(tracker)); // "go build" prints nothing when it works, every time
        }
    }

    [Fact]
    public void TheSameQuietCheckWithNothingChangedBetweenStillStops()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++) level = Check(tracker);
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void EditsMadeThroughTheShellAreProgressToo()
    {
        var tracker = new StallTracker(4, 8);
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(StallLevel.None, Check(tracker, $$"""cat > part{{i}}.py <<'EOF'\nprint({{i}})\nEOF"""));
            Assert.Equal(StallLevel.None, Check(tracker, "python -m pyflakes ."));
            Assert.Equal(StallLevel.None, Check(tracker, $"sed -i s/a/b/ part{i}.py"));
            Assert.Equal(StallLevel.None, Check(tracker, "python -m pyflakes ."));
        }
    }

    [Theory]
    [InlineData("git status --short")]
    [InlineData("ls -la")]
    [InlineData("dotnet build -v q")]
    [InlineData("npm run lint")]
    [InlineData("grep -rn foo src 2>&1")]
    [InlineData("cat notes.txt > /dev/null")]
    public void OrdinaryChecksAreNotTakenForEdits(string command)
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++) level = Check(tracker, command);
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void TheSameWriteAgainAndAgainIsStillALoop()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++)
        {
            level = tracker.Observe("write_file", """{"file_path":"x.txt","content":"y"}""", StallTracker.ResultKey("written"), failed: false, changedFiles: true);
            if (i < 7) Assert.NotEqual(StallLevel.Stop, level);
            tracker.Observe("read_file", """{"file_path":"x.txt"}""", StallTracker.ResultKey("y"), failed: false);
        }
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void AnEditThatFailsIsNotProgress()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++)
        {
            tracker.Observe("edit_file", $$"""{"old_string":"guess {{i}}"}""", StallTracker.ResultKey("Error: not found"), failed: true, changedFiles: false);
            level = Check(tracker);
        }
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void TheSameTodoListSentOverAndOverIsALoopButAChangedOneIsNot()
    {
        var same = """{"todos":[{"content":"x","status":"in_progress"}]}""";
        var key = StallTracker.ResultKey("Todos updated");
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++) level = tracker.Observe("todo_write", same, key, failed: false);
        Assert.Equal(StallLevel.Stop, level);

        var changing = new StallTracker(4, 8);
        for (var i = 0; i < 30; i++)
            Assert.Equal(StallLevel.None, changing.Observe("todo_write", $$"""{"todos":[{"content":"x{{i}}","status":"in_progress"}]}""", key, failed: false));

        // Anything else in between starts the count again.
        var interleaved = new StallTracker(4, 8);
        for (var i = 0; i < 30; i++)
        {
            interleaved.Observe("todo_write", same, key, failed: false);
            interleaved.Observe("read_file", $$"""{"file_path":"f{{i}}.txt"}""", StallTracker.ResultKey($"text {i}"), failed: false);
        }
        Assert.Equal(StallLevel.None, interleaved.Observe("todo_write", same, key, failed: false));
    }

    [Fact]
    public void AWordInAnotherFieldDoesNotMakeACommandAWait()
    {
        var tracker = new StallTracker(4, 8);
        var level = StallLevel.None;
        for (var i = 0; i < 8; i++)
            level = tracker.Observe("run_shell_command", """{"command":"gh run view 123","description":"watch the build until it is done"}""",
                StallTracker.ResultKey("in progress"), failed: false);
        Assert.Equal(StallLevel.Stop, level);
    }

    [Fact]
    public void SpacingBetweenTokensDoesNotMatterButSpacingInsideAStringDoes()
    {
        Assert.Equal(StallTracker.Normalize("""{"a":1,"b":"x y"}"""), StallTracker.Normalize("""{ "a" : 1, "b" : "x y" }"""));
        Assert.NotEqual(StallTracker.Normalize("""{"pattern":"new Foo"}"""), StallTracker.Normalize("""{"pattern":"newFoo"}"""));
        Assert.Equal("{\"a\":1", StallTracker.Normalize("{ \"a\" : 1")); // not JSON (a truncated call): compared without its whitespace
    }

    [Fact]
    public async Task ACheckAfterEachEditInARealRunIsNotStoppedAsALoop()
    {
        var turns = Enumerable.Range(1, 14).SelectMany(n => new[]
        {
            Turn.Calling(new ToolCall($"e{n}", "edit_file", $$"""{"file_path":"f{{n}}.go"}""")),
            Turn.Calling(new ToolCall($"b{n}", "run_shell_command", """{"command":"go build ./..."}""")),
        }).Append(new Turn("All done."));
        var client = new ScriptedClient(turns);
        var build = new FixedTool("run_shell_command", "(no output)");

        var result = await MakeEngine(client, Config(), [new WriterTool("edit_file"), build]).RunAsync([], "port the module");

        Assert.False(result.Stalled);
        Assert.Equal("All done.", result.FinalText);
        Assert.Equal(14, build.Runs);
        Assert.DoesNotContain(result.Messages, m => m.Role == MessageRole.Tool && (m.Content ?? "").Contains("Harness note"));
    }

    // MARK: - A reply cut off in the middle of a call

    [Fact]
    public async Task ACallCutOffByTheOutputLimitIsNotRun()
    {
        var writer = new FixedTool("write_file", "written");
        var cutOff = new Turn("", [new ToolCall("w1", "write_file", """{"file_path":"big.txt","content":"line 1\nline 2""")]) { Finish = "length" };
        var client = new ScriptedClient(cutOff, new Turn("I'll write it in parts."));
        var result = await MakeEngine(client, Config(), [writer]).RunAsync([], "write the big file");

        Assert.Equal(0, writer.Runs);
        Assert.Equal("I'll write it in parts.", result.FinalText);
        // The model was told why, and the broken JSON never went back to the server.
        var answer = result.Messages.Single(m => m.Role == MessageRole.Tool);
        Assert.Contains("cut off by the output-token limit", answer.Content);
        Assert.Equal("{}", result.Messages.Single(m => m.ToolCalls is { Count: > 0 }).ToolCalls![0].Arguments);
    }

    [Fact]
    public async Task ACompleteCallInATruncatedReplyStillRuns()
    {
        var echo = new Turn("", [Echo(1)]) { Finish = "length" };
        var client = new ScriptedClient(echo, new Turn("done"));
        var result = await MakeEngine(client, Config(), [new EchoTool()]).RunAsync([], "go");

        Assert.Contains(result.Messages, m => m.Role == MessageRole.Tool && (m.Content ?? "").StartsWith("echoed:"));
    }

    [Fact]
    public async Task ACompleteCallBesideACutOffOneIsNotRunEitherAndIsToldSo()
    {
        var writer = new FixedTool("write_file", "written");
        var echo = new EchoTool();
        var reply = new Turn("", [Echo(1), new ToolCall("w1", "write_file", """{"file_path":"big.txt","content":"line 1""")]) { Finish = "length" };
        var events = new List<EngineEvent>();
        var client = new ScriptedClient(reply, new Turn("ok"));

        await MakeEngine(client, Config(), [echo, writer]).RunAsync([], "go", sink: events.Add);

        Assert.Equal(0, writer.Runs);
        var answers = client.Requests[1].Messages.Where(m => m.Role == MessageRole.Tool).ToDictionary(m => m.ToolCallId!, m => m.Content ?? "");
        Assert.Contains("another call in the same reply was cut off", answers["c1"]); // it was fine; it just wasn't run
        Assert.Contains("in the middle of this call", answers["w1"]);
        Assert.DoesNotContain(events, e => e is EngineEvent.ToolRunning); // nothing ran
    }

    [Fact]
    public async Task OnlyACallThatReallyRunsIsAnnouncedAsRunning()
    {
        var events = new List<EngineEvent>();
        var client = new ScriptedClient(
            Turn.Calling(Echo(1)),
            Turn.Calling(new ToolCall("w", "write_file", """{"file_path":"x.txt","content":"y"}""")),
            new Turn("done"));

        // The write is refused (a planning chat may not write): it is shown, but never "running".
        await MakeEngine(client, Config(), [new EchoTool(), new FixedTool("write_file", "written")], PermissionPreset.Plan, Gates.Deny)
            .RunAsync([], "go", sink: events.Add);

        Assert.Contains(events, e => e is EngineEvent.ToolRunning { Name: "echo" });
        Assert.Contains(events, e => e is EngineEvent.ToolStarted { Name: "write_file" });
        Assert.DoesNotContain(events, e => e is EngineEvent.ToolRunning { Name: "write_file" });
    }

    [Fact]
    public async Task RepeatedCutOffsEndTheRunInsteadOfLooping()
    {
        var cutOff = () => new Turn("", [new ToolCall("w", "write_file", """{"file_path":"big.txt","content":"unfinished""")]) { Finish = "length" };
        var client = new ScriptedClient(Enumerable.Range(0, 30).Select(_ => cutOff()));
        var result = await MakeEngine(client, Config(), [new FixedTool("write_file", "written")]).RunAsync([], "go");

        Assert.True(result.Stalled);
        Assert.Equal(7, client.Requests.Count); // the default 6 continuations, then it gives up
        Assert.Contains("output-token limit", result.LastReplyText);
        // Every call was still closed, so the transcript is a valid request.
        Assert.Equal(7, result.Messages.Count(m => m.Role == MessageRole.Tool));
    }

    // MARK: - "Done" only counts once the model has seen the rest

    [Fact]
    public async Task GoalCompleteBatchedWithOtherCallsIsNotRecorded()
    {
        var batch = new Turn("", [Echo(1), new ToolCall("g1", "goal_complete", """{"summary":"tests pass"}""")]);
        var alone = Turn.Calling(new ToolCall("g2", "goal_complete", """{"summary":"tests pass, verified"}"""));
        var client = new ScriptedClient(batch, alone);
        var result = await MakeEngine(client, Config(), [new EchoTool(), new GoalCompleteTool()]).RunAsync([], "go");

        Assert.Equal(2, client.Requests.Count); // it had to come back and say it again
        var first = result.Messages.First(m => m.Role == MessageRole.Tool && m.ToolCallId == "g1");
        Assert.Contains("Not recorded", first.Content);
        var done = Assert.IsType<GoalStatus.Complete>(result.Goal);
        Assert.Equal("tests pass, verified", done.Summary);
    }

    [Fact]
    public async Task CallsAfterAGoalIsBlockedAreNotRun()
    {
        var echo = new FixedTool("echo", "ran");
        var batch = new Turn("", [new ToolCall("b", "goal_blocked", """{"reason":"need the token"}"""), new ToolCall("e", "echo", "{}")]);
        var client = new ScriptedClient(batch);
        var result = await MakeEngine(client, Config(), [new GoalBlockedTool(), echo]).RunAsync([], "go");

        Assert.IsType<GoalStatus.Blocked>(result.Goal);
        Assert.Equal(0, echo.Runs);
        Assert.Contains("Not run", result.Messages.Single(m => m.ToolCallId == "e").Content);
    }

    // MARK: - The backstops

    [Fact]
    public async Task ARunThatExploresForeverEndsAtTheCheckpointLimit()
    {
        var turns = Enumerable.Range(1, 100).Select(n => Turn.Calling(Echo(n, $"different {n}")));
        var client = new ScriptedClient(turns);
        var result = await MakeEngine(client, Config(3) with { ContinueAfterLimit = true, MaxCheckpoints = 2 }, [new EchoTool()]).RunAsync([], "go");

        Assert.True(result.HitIterationLimit);
        Assert.Equal(6, client.Requests.Count); // two segments of three steps
    }

    [Fact]
    public async Task ASummariserThatKeepsFailingIsNotAskedOnEveryStep()
    {
        var attempts = 0;
        var turns = Enumerable.Range(1, 12).Select(n => Turn.Calling(Echo(n, $"different {n}"))).Append(new Turn("done"));
        var engine = new Engine(new ScriptedClient(turns), new ToolRegistry([new EchoTool()]), "system",
            Config() with { ContinueAfterLimit = true, ContextWindow = 100 }, _root.Path,
            new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow)
        {
            Compactor = (_, messages, _) =>
            {
                attempts++;
                return Task.FromResult<IReadOnlyList<LlmMessage>>(messages); // could not shrink anything
            },
        };
        var result = await engine.RunAsync([], new string('x', 600));

        Assert.Equal("done", result.FinalText);
        Assert.Equal(2, attempts);
    }

    [Theory]
    [InlineData("{{vault:PATTERN}}")]
    [InlineData("{{ vault:PATTERN }}")] // the vault reads it too
    public async Task VaultPlaceholdersInAParallelBatchGoThroughTheOneAtATimePath(string placeholder)
    {
        // The parallel path does no substitution: a lookup that names a credential must not be sent through it.
        var vault = new CredentialVault(Path.Combine(_root.Path, "vault"), new MemoryBlobStore());
        vault.Add("PATTERN", "needle", VaultKind.Token, "search term", [], access: VaultAccess.Allowed);
        File.WriteAllText(Path.Combine(_root.Path, "hay.txt"), "a needle in a haystack\n");
        var client = new ScriptedClient(
            new Turn("", [new ToolCall("g1", "grep", $$"""{"pattern":"{{placeholder}}"}"""), new ToolCall("g2", "grep", """{"pattern":"haystack"}""")]),
            new Turn("done"));

        var result = await MakeEngine(client, Config(), [new GrepTool()], vault: vault).RunAsync([], "go");

        var first = result.Messages.First(m => m.ToolCallId == "g1").Content ?? "";
        Assert.DoesNotContain("No matches", first); // the placeholder was replaced before the search ran
        Assert.Contains("hay.txt", first);
    }

    [Fact]
    public async Task ANewTurnDoesNotTrustSearchesFromTheLastOne()
    {
        var grep = new FixedTool("grep", "a.cs:1: TODO");
        var call = new ToolCall("g", "grep", """{"pattern":"TODO"}""");
        var cache = new ToolCache();

        // Within one run a repeated search is answered from the cache (the tool ran once)...
        await MakeEngine(new ScriptedClient(Turn.Calling(call), Turn.Calling(call with { Id = "g2" }), new Turn("done")), Config(), [grep], cache: cache).RunAsync([], "first");
        Assert.Equal(1, grep.Runs);

        // ...but the user may have edited files before the next message, so a new run searches again.
        await MakeEngine(new ScriptedClient(Turn.Calling(call with { Id = "g3" }), new Turn("done")), Config(), [grep], cache: cache).RunAsync([], "second");
        Assert.Equal(2, grep.Runs);
    }
}
