namespace Dsh.Core.Tests;

public sealed class ToolCacheTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-cache");
    private readonly ManualClock _clock = new();

    public void Dispose() => _root.Dispose();

    private ToolCache Cache() => new(_clock);

    private string File1(string name = "a.txt", string text = "one\ntwo\nthree\n")
    {
        var path = Path.Combine(_root.Path, name);
        File.WriteAllText(path, text);
        return path;
    }

    private string Resolve(string raw) => Path.IsPathRooted(raw) ? raw : Path.Combine(_root.Path, raw);

    private ToolResult? Get(ToolCache cache, string tool, string args, int index = 10) =>
        cache.TryGet(tool, args, _root.Path, Resolve, index);

    private void Ran(ToolCache cache, string tool, string args, string output, int index = 10, IReadOnlyList<FileChange>? files = null) =>
        cache.Observe(tool, args, _root.Path, Resolve, new ToolResult(output) { Files = files ?? [] }, index);

    private const string ReadA = """{"file_path":"a.txt"}""";

    // MARK: - File reads

    [Fact]
    public void ARepeatedReadOfAnUnchangedFileIsAnswerWithANote()
    {
        var cache = Cache();
        File1();
        Assert.Null(Get(cache, "read_file", ReadA)); // nothing known yet
        Ran(cache, "read_file", ReadA, "1 | one\n2 | two\n3 | three", index: 10);

        var stub = Get(cache, "read_file", ReadA, index: 14);
        Assert.NotNull(stub);
        Assert.StartsWith("[Unchanged] a.txt", stub.Output);
        Assert.Contains("4 messages ago", stub.Output);
        Assert.Equal(1, cache.Stats.Reads);
        Assert.True(cache.Stats.CharsSaved > 0);
    }

    [Fact]
    public void AskingAgainRightAfterTheNoteGetsTheFullText()
    {
        var cache = Cache();
        File1();
        Ran(cache, "read_file", ReadA, "1 | one", index: 10);
        Assert.NotNull(Get(cache, "read_file", ReadA, index: 12)); // the note
        Assert.Null(Get(cache, "read_file", ReadA, index: 14)); // it insists: run the read
        Ran(cache, "read_file", ReadA, "1 | one", index: 14);
        Assert.NotNull(Get(cache, "read_file", ReadA, index: 16)); // and the next repeat is a note again
    }

    [Fact]
    public void AChangedFileIsReadAgain()
    {
        var cache = Cache();
        var path = File1();
        Ran(cache, "read_file", ReadA, "1 | one", index: 10);
        File.WriteAllText(path, "one\ntwo\nthree\nfour and more\n");
        Assert.Null(Get(cache, "read_file", ReadA, index: 12));
        // Same size, later timestamp: also a change.
        Ran(cache, "read_file", ReadA, "1 | one", index: 12);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));
        Assert.Null(Get(cache, "read_file", ReadA, index: 14));
    }

    [Fact]
    public void ADeletedFileIsReadAgain()
    {
        var cache = Cache();
        File.Delete(File1());
        Ran(cache, "read_file", ReadA, "1 | one", index: 10);
        Assert.Null(Get(cache, "read_file", ReadA, index: 12));
    }

    [Fact]
    public void AnOldReadIsNotVouchedFor()
    {
        var cache = new ToolCache(_clock) { RecentWindow = 10 };
        File1();
        Ran(cache, "read_file", ReadA, "1 | one", index: 10);
        Assert.Null(Get(cache, "read_file", ReadA, index: 25)); // far back in the conversation now
    }

    [Fact]
    public void CompactionForgetsWhatWasRead()
    {
        var cache = Cache();
        File1();
        Ran(cache, "read_file", ReadA, "1 | one", index: 10);
        cache.NoteCompaction();
        Assert.Null(Get(cache, "read_file", ReadA, index: 12));
    }

    [Fact]
    public void ADifferentRangeIsADifferentRead()
    {
        var cache = Cache();
        File1();
        Ran(cache, "read_file", """{"file_path":"a.txt","start_line":1,"end_line":2}""", "1 | one\n2 | two", index: 10);
        Assert.Null(Get(cache, "read_file", """{"file_path":"a.txt","start_line":2,"end_line":3}""", index: 12));
        Assert.NotNull(Get(cache, "read_file", """{"file_path":"a.txt","start_line":1,"end_line":2}""", index: 12));
    }

    [Fact]
    public void FailedReadsAreNeverRemembered()
    {
        var cache = Cache();
        File1();
        Ran(cache, "read_file", ReadA, "Error: cannot read a.txt", index: 10);
        Assert.Null(Get(cache, "read_file", ReadA, index: 12));
    }

    // MARK: - Lookups

    [Fact]
    public void SearchesAreReusedUntilTheWorkspaceChanges()
    {
        var cache = Cache();
        const string grep = """{"pattern":"TODO"}""";
        Ran(cache, "grep", grep, "a.cs:1: TODO");

        // Whitespace in the arguments doesn't matter.
        var hit = Get(cache, "grep", """{ "pattern" : "TODO" }""");
        Assert.Equal("a.cs:1: TODO", hit!.Output);
        Assert.Equal(1, cache.Stats.Hits);

        // Asked a third time it runs again, and the fresh answer is what is kept.
        Assert.Null(Get(cache, "grep", grep));
        Ran(cache, "grep", grep, "a.cs:1: TODO");
        Assert.NotNull(Get(cache, "grep", grep));

        // Anything that may write invalidates it.
        Ran(cache, "run_shell_command", """{"command":"sed -i ..."}""", "(no output)");
        Assert.Null(Get(cache, "grep", grep));
    }

    [Theory]
    [InlineData("grep", """{"pattern":"x"}""")]
    [InlineData("glob", """{"pattern":"*.cs"}""")]
    [InlineData("list_directory", """{"path":"dist"}""")]
    public void AListingAskedOverAndOverIsRunAgainEverySecondTime(string tool, string args)
    {
        // What a background build is doing to the folder goes on unseen: a model polling for it gets a fresh answer
        // at most one repeat late.
        var cache = Cache();
        Ran(cache, tool, args, "before");
        Assert.Equal("before", Get(cache, tool, args)!.Output);
        Assert.Null(Get(cache, tool, args));
        Ran(cache, tool, args, "after");
        Assert.Equal("after", Get(cache, tool, args)!.Output);
        Assert.Null(Get(cache, tool, args));
    }

    [Theory]
    [InlineData("write_file")]
    [InlineData("edit")]
    [InlineData("process_start")]
    [InlineData("some_plugin_tool")]
    public void MutatingToolsInvalidateSearches(string mutator)
    {
        var cache = Cache();
        Ran(cache, "glob", """{"pattern":"*.cs"}""", "a.cs");
        Ran(cache, mutator, "{}", "done");
        Assert.Null(Get(cache, "glob", """{"pattern":"*.cs"}"""));
    }

    [Theory]
    [InlineData("read_file")]
    [InlineData("todo_write")]
    [InlineData("memory_save")]
    [InlineData("vault_search")]
    public void ReadOnlyToolsLeaveSearchesAlone(string reader)
    {
        var cache = Cache();
        File1();
        Ran(cache, "glob", """{"pattern":"*.cs"}""", "a.cs");
        Ran(cache, reader, ReadA, "whatever");
        Assert.NotNull(Get(cache, "glob", """{"pattern":"*.cs"}"""));
    }

    [Fact]
    public void AToolReportingFileChangesInvalidates()
    {
        var cache = Cache();
        Ran(cache, "list_directory", """{"path":"."}""", "a.cs");
        Ran(cache, "todo_write", "{}", "ok", files: [new FileChange("x", FileChangeKind.Created)]);
        Assert.Null(Get(cache, "list_directory", """{"path":"."}"""));
    }

    [Fact]
    public void SearchesExpire()
    {
        var cache = Cache();
        Ran(cache, "grep", """{"pattern":"x"}""", "hit");
        _clock.AdvanceSeconds(59);
        Assert.NotNull(Get(cache, "grep", """{"pattern":"x"}"""));
        _clock.AdvanceSeconds(2);
        Assert.Null(Get(cache, "grep", """{"pattern":"x"}"""));
    }

    [Fact]
    public void WebPagesLastLongerAndIgnoreWorkspaceChanges()
    {
        var cache = Cache();
        const string fetch = """{"url":"https://example.com/docs"}""";
        Ran(cache, "web_fetch", fetch, "URL: https://example.com/docs\n\nhello");
        Ran(cache, "write_file", "{}", "wrote");
        _clock.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(Get(cache, "web_fetch", fetch));
        _clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(Get(cache, "web_fetch", fetch));
    }

    [Theory]
    [InlineData("http://localhost:3000/")]
    [InlineData("http://localhost:5173/")]
    [InlineData("http://localhost./")]
    [InlineData("http://127.0.0.1:8000/health")]
    [InlineData("http://printer.local/")]
    [InlineData("http://172.20.1.1/")]
    [InlineData("http://spark/v1/models")]
    [InlineData("http://127.0.0.1:8000/")]
    [InlineData("http://[::1]:8000/")]
    [InlineData("http://[::ffff:192.168.1.5]:8000/")]
    [InlineData("http://0.0.0.0:3000/")]
    [InlineData("http://192.168.1.20/status")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.20.0.9/")]
    [InlineData("http://100.101.102.103/")] // Tailscale / carrier-grade NAT
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://spark/")]
    [InlineData("http://spark./status")]
    [InlineData("http://spark-3.local:8002/v1/models")]
    public void PagesOnThisPcOrTheLocalNetworkAreNeverCached(string url)
    {
        var cache = Cache();
        var fetch = $$"""{"url":"{{url}}"}""";
        Ran(cache, "web_fetch", fetch, "page");
        Assert.Null(Get(cache, "web_fetch", fetch));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://100.63.0.1/")] // just below the shared range
    [InlineData("http://100.128.0.1/")] // and just above it
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://[2001:4860:4860::8888]/")]
    public void PublicPagesAreCached(string url)
    {
        var cache = Cache();
        var fetch = $$"""{"url":"{{url}}"}""";
        Ran(cache, "web_fetch", fetch, "page");
        Assert.NotNull(Get(cache, "web_fetch", fetch));
    }

    [Fact]
    public void ErrorsAreNeverCached()
    {
        var cache = Cache();
        Ran(cache, "web_fetch", """{"url":"https://example.com"}""", "Error: fetch failed: timeout");
        Assert.Null(Get(cache, "web_fetch", """{"url":"https://example.com"}"""));
    }

    [Fact]
    public void CallsThatMentionAVaultPlaceholderAreNeverCached()
    {
        var cache = Cache();
        const string fetch = """{"url":"https://api.example.com?key={{vault:API_KEY}}"}""";
        Ran(cache, "web_fetch", fetch, "secret answer");
        Assert.Null(Get(cache, "web_fetch", fetch));
    }

    [Fact]
    public void OtherToolsAreNotHandled()
    {
        var cache = Cache();
        Assert.False(ToolCache.Handles("run_shell_command"));
        Ran(cache, "run_shell_command", """{"command":"ls"}""", "x");
        Assert.Null(Get(cache, "run_shell_command", """{"command":"ls"}"""));
    }

    // MARK: - Subagents

    [Fact]
    public void AnIdenticalReadOnlySubagentTaskIsReused()
    {
        var cache = Cache();
        const string call = """{"description":"scan","prompt":"find usages of Foo","agent_type":"explore"}""";
        Ran(cache, "agent", call, "Subagent 'scan' finished.\n\nFoo is used in 3 places.");
        _clock.Advance(TimeSpan.FromMinutes(3));
        var again = Get(cache, "agent", call);
        Assert.StartsWith("(Reused the report of an identical subagent run from 3 min ago", again!.Output);
        Assert.EndsWith("Foo is used in 3 places.", again.Output);
        _clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(Get(cache, "agent", call));
    }

    [Fact]
    public void ASubagentThatMayHaveEditedThingsInvalidatesLookups()
    {
        var cache = Cache();
        Ran(cache, "grep", """{"pattern":"x"}""", "hit");
        Ran(cache, "agent", """{"description":"d","prompt":"implement it","agent_type":"worker"}""", "Subagent 'd' finished.\n\ndone");
        Assert.Null(Get(cache, "grep", """{"pattern":"x"}"""));
        // A read-only subagent doesn't.
        Ran(cache, "grep", """{"pattern":"x"}""", "hit");
        Ran(cache, "agent", """{"description":"d","prompt":"look","agent_type":"explore"}""", "Subagent 'd' finished.\n\nlooked");
        Assert.NotNull(Get(cache, "grep", """{"pattern":"x"}"""));
    }

    [Fact]
    public void ASubagentReportIsInvalidatedByALaterWrite()
    {
        var cache = Cache();
        const string call = """{"description":"scan","prompt":"find usages","agent_type":"explore"}""";
        Ran(cache, "agent", call, "Subagent 'scan' finished.\n\nreport");
        Ran(cache, "edit", "{}", "edited");
        Assert.Null(Get(cache, "agent", call));
    }

    [Fact]
    public void FailedSubagentsAreNotReused()
    {
        var cache = Cache();
        const string call = """{"description":"scan","prompt":"p"}""";
        Ran(cache, "agent", call, "Subagent 'scan' failed: server down");
        Assert.Null(Get(cache, "agent", call));
    }

    // MARK: - Housekeeping

    [Fact]
    public void ItStaysWithinItsBounds()
    {
        var cache = new ToolCache(_clock) { MaxEntries = 5, MaxChars = 100 };
        for (var i = 0; i < 20; i++)
        {
            _clock.AdvanceSeconds(1);
            Ran(cache, "grep", $$"""{"pattern":"p{{i}}"}""", new string('x', 30));
        }
        // The newest is kept, the oldest gone; total size stays under the cap.
        Assert.NotNull(Get(cache, "grep", """{"pattern":"p19"}"""));
        Assert.Null(Get(cache, "grep", """{"pattern":"p0"}"""));
    }

    [Fact]
    public void ClearDropsEverything()
    {
        var cache = Cache();
        File1();
        Ran(cache, "grep", """{"pattern":"x"}""", "hit");
        Ran(cache, "read_file", ReadA, "1 | one", index: 10);
        cache.Clear();
        Assert.Null(Get(cache, "grep", """{"pattern":"x"}"""));
        Assert.Null(Get(cache, "read_file", ReadA, index: 12));
    }

    [Fact]
    public void StatsDescribeWhatWasSaved()
    {
        var cache = Cache();
        Assert.Equal("", cache.Stats.Describe());
        Ran(cache, "grep", """{"pattern":"x"}""", new string('x', 4_000));
        Get(cache, "grep", """{"pattern":"x"}""");
        Assert.Contains("1 repeated lookup answered from cache", cache.Stats.Describe());
        Assert.Contains($"{Fmt.N(1_000)} tokens", cache.Stats.Describe()); // (grouped by the machine's own culture)
    }

    // MARK: - Review fixes: what must never be replayed

    [Fact]
    public void AWorkersReportIsNeverReplayed()
    {
        // "Run the tests: 3 failed" is stale the moment the user fixes something outside the app.
        var cache = Cache();
        const string call = """{"description":"run tests","prompt":"Run dotnet test and report"}""";
        Ran(cache, "agent", call, "Subagent 'run tests' finished.\n\n3 failed");
        Assert.Null(Get(cache, "agent", call));
    }

    [Fact]
    public void AReadOnlyAgentIsJudgedByWhatItMayDoNotByItsName()
    {
        var cache = Cache();
        const string call = """{"description":"scan","prompt":"find usages","agent_type":"explore"}""";
        Ran(cache, "agent", call, "Subagent 'scan' finished.\n\nfound");
        Assert.NotNull(Get(cache, "agent", call)); // the built-in explorer only reads

        // This chat's project replaced "explore" with a file that has every tool: it can change things.
        var other = Cache();
        other.AgentIsReadOnly = _ => false;
        Ran(other, "agent", call, "Subagent 'scan' finished.\n\nfound");
        Assert.Null(Get(other, "agent", call));
    }

    [Fact]
    public void AnEmptyReportIsNotKept()
    {
        var cache = Cache();
        const string call = """{"description":"scan","prompt":"look","agent_type":"explore"}""";
        Ran(cache, "agent", call, "Subagent 'scan' finished.\n\nSubagent completed without a final report. Its tool work (if any) has already been applied in the workspace.");
        Assert.Null(Get(cache, "agent", call));
    }

    [Fact]
    public void APublicPageIsStillCached()
    {
        var cache = Cache();
        const string call = """{"url":"https://example.com/docs"}""";
        Ran(cache, "web_fetch", call, "URL: https://example.com/docs\n\nhello");
        Assert.NotNull(Get(cache, "web_fetch", call));
    }

    [Fact]
    public void SpacesInsideAnArgumentMakeADifferentSearch()
    {
        var cache = Cache();
        Ran(cache, "grep", """{"pattern":"new Foo"}""", "a.cs:1: new Foo()");
        Assert.NotNull(Get(cache, "grep", """{ "pattern" : "new Foo" }""")); // spacing between tokens is not a difference
        Assert.Null(Get(cache, "grep", """{"pattern":"newFoo"}"""));
    }

    [Fact]
    public void ANewTurnDistrustsEarlierSearches()
    {
        var cache = Cache();
        Ran(cache, "grep", """{"pattern":"TODO"}""", "a.cs:1: TODO");
        Assert.NotNull(Get(cache, "grep", """{"pattern":"TODO"}"""));
        cache.NoteExternalChange(); // the user may have edited files since
        Assert.Null(Get(cache, "grep", """{"pattern":"TODO"}"""));
    }

    [Theory]
    [InlineData("process_read")]
    [InlineData("process_list")]
    [InlineData("agent_status")]
    public void LookingAtSomethingThatIsStillWorkingCountsAsAPossibleChange(string tool)
    {
        var cache = Cache();
        Ran(cache, "glob", """{"pattern":"**/*.dll"}""", "No files found");
        Ran(cache, tool, "{}", "still running");
        Assert.Null(Get(cache, "glob", """{"pattern":"**/*.dll"}"""));
    }
}

/// <summary>The cache inside a real engine run.</summary>
public sealed class ToolCacheEngineTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-cache-engine");

    public void Dispose() => _root.Dispose();

    [Fact]
    public async Task ARepeatedReadIsCheapAndAChangeIsSeen()
    {
        var path = Path.Combine(_root.Path, "notes.txt");
        File.WriteAllText(path, "alpha\nbeta\ngamma\n");
        var read = new ToolCall("r1", "read_file", """{"file_path":"notes.txt"}""");
        var again = new ToolCall("r2", "read_file", """{"file_path":"notes.txt"}""");
        var third = new ToolCall("r3", "read_file", """{"file_path":"notes.txt"}""");
        var client = new ScriptedClient(Turn.Calling(read), Turn.Calling(again), Turn.Calling(third), new Turn("done"));
        var cache = new ToolCache();
        var engine = new Engine(client, ToolRegistry.Standard(), "system", new EngineConfig("test") { MaxIterations = 10 },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow) { Cache = cache };
        var result = await engine.RunAsync([], "read it three times");

        var outputs = result.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content!).ToList();
        Assert.Contains("alpha", outputs[0]);
        Assert.StartsWith("[Unchanged] notes.txt", outputs[1]);
        Assert.Contains("alpha", outputs[2]); // asking a third time in a row gets the text
        Assert.Equal(1, cache.Stats.Reads);
    }

    [Fact]
    public async Task AWriteBetweenReadsIsNotHiddenByTheCache()
    {
        File.WriteAllText(Path.Combine(_root.Path, "notes.txt"), "alpha\n");
        var client = new ScriptedClient(
            Turn.Calling(new ToolCall("r1", "read_file", """{"file_path":"notes.txt"}""")),
            Turn.Calling(new ToolCall("w1", "write_file", """{"file_path":"notes.txt","content":"alpha\nbeta\n"}""")),
            Turn.Calling(new ToolCall("r2", "read_file", """{"file_path":"notes.txt"}""")),
            new Turn("done"));
        var engine = new Engine(client, ToolRegistry.Standard(), "system", new EngineConfig("test") { MaxIterations = 10 },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow) { Cache = new ToolCache() };
        var result = await engine.RunAsync([], "go");

        var outputs = result.Messages.Where(m => m.Role == MessageRole.Tool).Select(m => m.Content!).ToList();
        Assert.Contains("beta", outputs[2]);
    }

    [Fact]
    public async Task WithoutACacheEveryReadRuns()
    {
        File.WriteAllText(Path.Combine(_root.Path, "notes.txt"), "alpha\n");
        var read = new ToolCall("r1", "read_file", """{"file_path":"notes.txt"}""");
        var client = new ScriptedClient(Turn.Calling(read), Turn.Calling(read with { Id = "r2" }), new Turn("done"));
        var engine = new Engine(client, ToolRegistry.Standard(), "system", new EngineConfig("test") { MaxIterations = 10 },
            _root.Path, new PermissionPolicy(PermissionPreset.WorkspaceWrite, _root.Path), Gates.Allow);
        var result = await engine.RunAsync([], "go");
        Assert.All(result.Messages.Where(m => m.Role == MessageRole.Tool), m => Assert.Contains("alpha", m.Content));
    }
}
