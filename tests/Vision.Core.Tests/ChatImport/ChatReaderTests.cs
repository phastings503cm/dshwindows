namespace Vision.Core.Tests;

/// <summary>Each source's reader against a file in that tool's real shape (see <see cref="ChatFixtures"/>): the rows a chat
/// gets, in order, with their times — and what is left out.</summary>
public sealed class ChatReaderTests
{
    private static string Line(LogItemRow r) => r.Kind switch
    {
        "tool" => $"tool {r.ToolName} [{r.ArgSummary}] → {r.Text}{(r.IsError ? " (error)" : "")}",
        _ => $"{r.Kind}: {r.Text}",
    };

    // MARK: Claude Code

    [Fact]
    public void ClaudeCodeSessionBecomesTheMainLineOfItsConversation()
    {
        using var temp = new TempDirectory();
        ChatFixtures.WriteClaudeProfile(temp);
        var path = temp[$"projects/C--code-shop/{ChatFixtures.ClaudeSessionId}.jsonl"];

        var chat = ClaudeCodeChats.Read(path)!;

        Assert.Equal(
        [
            "user: Fix the login bug in auth.ts",
            "assistant: Let me look at the code.",
            "tool Bash [grep -n login src/auth.ts] → 12: export function login() {",
            @"tool Read [C:\code\shop\src\auth.ts] → 1 export function login() {}",
            "tool Grep [session] → failed: No matches found (error)",
            "assistant: Fixed: login now checks the session.",
            "user: /model opus",
            "notice: Set model to opus",
            "notice: Background task completed: Tests passed",
            "compaction: This session is being continued from a previous conversation. Summary: fixed login.",
            "user: What is wrong in this screenshot?\n\n[image]",
            "error: API Error: 529 Overloaded",
            "notice: Interrupted by the user.",
            "user: try again",
            "assistant: The button is hidden behind the banner.",
        ], chat.Rows.Select(Line).ToArray());
        Assert.Equal(ChatSource.ClaudeCode, chat.Source);
        Assert.Equal(ChatFixtures.ClaudeSessionId, chat.SourceId);
        Assert.Equal("Login bug fix", chat.Title); // the person's title beats Claude's own
        Assert.Equal(@"C:\code\shop", chat.Project);
        Assert.Equal("claude-opus-4-5", chat.Model);
        Assert.Equal(7, chat.Messages);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:00:01Z"), chat.StartedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:12:30Z"), chat.UpdatedAt);
        // Times are the source's, row by row.
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:05:00Z"), chat.Rows[6].At);
        // Tool output kept without the harness's reminders; the compaction counts what it folded.
        Assert.Equal("12: export function login() {", chat.Rows[2].Output);
        Assert.Equal("4", chat.Rows[9].ArgSummary);
        Assert.Equal(["1 subagent transcript left out (its result is in the chat)", "1 image not imported"], chat.Notes);
        // Nothing from the rewound answer or the sidechain.
        Assert.DoesNotContain(chat.Rows, r => r.Text?.Contains("ABANDONED") == true || r.Text?.Contains("SIDECHAIN") == true);
    }

    [Fact]
    public void ClaudeCodeScanListsSessionsWithConversationsOnly()
    {
        using var temp = new TempDirectory();
        ChatFixtures.WriteClaudeProfile(temp);

        var found = ChatImportScanner.ClaudeCode(new ChatImportLocations(temp.Path, ClaudeConfigDir: temp.Path));

        var only = Assert.Single(found); // the title-only file and the subagent transcript aren't chats
        Assert.Equal("Login bug fix", only.Title);
        Assert.Equal(7, only.Messages);
        Assert.Equal(@"C:\code\shop", only.Project);
        Assert.Equal("1 subagent transcript left out (its result is in the chat)", only.Note);
        Assert.True(only.SelectedByDefault);
    }

    [Fact]
    public void AClaudeCodeChainThatBreaksOffKeepsEveryMainLineEntry()
    {
        using var temp = new TempDirectory();
        var path = temp.Write("s.jsonl", string.Join("\n",
            """{"parentUuid":"missing-from-this-file","isSidechain":false,"type":"user","message":{"role":"user","content":"first"},"uuid":"a","timestamp":"2026-10-01T09:00:00Z"}""",
            """{"parentUuid":"a","isSidechain":false,"type":"assistant","message":{"id":"m","model":"m","role":"assistant","content":[{"type":"text","text":"one"}]},"uuid":"b","timestamp":"2026-10-01T09:00:01Z"}"""));

        var chat = ClaudeCodeChats.Read(path)!;

        Assert.Equal(["user: first", "assistant: one"], chat.Rows.Select(Line).ToArray());
    }

    // MARK: Codex

    [Fact]
    public void CodexRolloutReadsThePersonsPromptsAndTheModelsItems()
    {
        using var temp = new TempDirectory();
        var home = ChatFixtures.WriteCodexHome(temp);
        var path = Assert.Single(CodexChats.SessionFiles(home), f => !f.Contains("archived_sessions"));

        var chat = CodexChats.Read(path, CodexChats.ThreadNames(home))!;

        Assert.Equal(
        [
            "user: List the files",
            "assistant: I'll list them.",
            "tool exec_command [ls] → README.md",
            "tool exec_command [cat missing.txt] → failed: cat: missing.txt: No such file or directory (error)",
            "tool apply_patch [*** Begin Patch *** Add File: notes.md +hi *** End Patch] → Success. Updated the following files:",
            "assistant: There are two entries: README.md and src.",
            "compaction: Summary: listed the files.",
            "user: Now delete notes.md\n\n[image]",
            "notice: Interrupted by the user.",
        ], chat.Rows.Select(Line).ToArray());
        Assert.Equal(ChatFixtures.CodexId, chat.SourceId);
        Assert.Equal("Shop file listing", chat.Title); // the last thread name given
        Assert.Equal(@"C:\code\shop", chat.Project);
        Assert.Equal("gpt-5.5", chat.Model);
        // Codex's header is taken off the command's output.
        Assert.Equal("README.md\nsrc", chat.Rows[2].Output);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T10:00:05Z"), chat.Rows[2].At); // a tool row is at its call, as in a live chat
    }

    [Fact]
    public void AnOldCodexRolloutWithoutThePayloadWrapperStillReads()
    {
        using var temp = new TempDirectory();
        var home = ChatFixtures.WriteCodexHome(temp);
        var path = Assert.Single(CodexChats.SessionFiles(home), f => f.Contains("archived_sessions"));

        var chat = CodexChats.Read(path)!;

        Assert.Equal(["user: hello", "assistant: hi", "tool shell [ls] → a.txt"], chat.Rows.Select(Line).ToArray());
        Assert.Equal("0a1b2c3d-1111-2222-3333-444455556666", chat.SourceId);
        Assert.Equal("hello", chat.Title);
        Assert.Equal(DateTimeOffset.Parse("2025-05-01T08:00:00Z"), chat.StartedAt);
    }

    [Fact]
    public void CodexScanFindsBothFolders()
    {
        using var temp = new TempDirectory();
        ChatFixtures.WriteCodexHome(temp);

        var found = ChatImportScanner.Codex(new ChatImportLocations(temp.Path));

        Assert.Equal(["Shop file listing", "hello"], found.Select(c => c.Title).ToArray()); // newest first
        Assert.All(found, c => Assert.Equal(ChatSource.Codex, c.Source));
    }

    // MARK: OpenClaw

    [Fact]
    public void OpenClawSessionFileFollowsItsCurrentBranch()
    {
        using var temp = new TempDirectory();
        var state = ChatFixtures.WriteOpenClawJsonl(temp);
        var (path, key, label) = Assert.Single(OpenClawChats.ListJsonl(Path.Combine(state, "agents", "main", "sessions")), f => f.Path.EndsWith("oc-1111.jsonl"));

        var chat = OpenClawChats.ReadJsonl(path, key, label)!;

        Assert.Equal(
        [
            "user: Check the disk on the build box",
            "assistant: Checking.",
            "tool exec [df -h] → Filesystem Size Used",
            "assistant: The disk is 80% full.",
            "compaction: Checked the disk: 80% full.",
            "notice: Heads up: the box reboots at 2 am.",
            "error: Bedrock throttled the request",
        ], chat.Rows.Select(Line).ToArray());
        Assert.Equal("oc-1111", chat.SourceId);
        Assert.Equal("Build box disk", chat.Title);
        Assert.Null(chat.Project); // OpenClaw's own workspace isn't a project
        Assert.Equal("claude-opus-4-5", chat.Model);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T12:00:00Z"), chat.StartedAt);
    }

    [Fact]
    public void OpenClawScanLeavesOutSubagentsAndResetCopies()
    {
        using var temp = new TempDirectory();
        var state = ChatFixtures.WriteOpenClawJsonl(temp);

        var found = ChatImportScanner.OpenClaw(new ChatImportLocations(temp.Path, OpenClawStateDirs: [state]));

        var only = Assert.Single(found);
        Assert.Equal("oc-1111", only.SourceId);
        Assert.False(only.Automated);
    }

    [SqliteFact]
    public void OpenClawSqliteStoreListsChatsAndMarksScheduledRuns()
    {
        using var temp = new TempDirectory();
        var state = ChatFixtures.WriteOpenClawSqlite(temp);

        var found = ChatImportScanner.OpenClaw(new ChatImportLocations(temp.Path, OpenClawStateDirs: [state]));

        Assert.Equal(["Cron: ticket sweep", "Count the tickets"], found.Select(c => c.Title).ToArray()); // the subagent's is left out
        var cron = found[0];
        Assert.True(cron.Automated);
        Assert.False(cron.SelectedByDefault);
        Assert.Equal("Scheduled run", cron.Note);
        var chat = found[1].Load!(default).GetAwaiter().GetResult()!;
        Assert.Equal(["user: How many tickets are open?", "assistant: Twelve are open."], chat.Rows.Select(Line).ToArray());
        Assert.Equal(@"C:\code\desk", chat.Project);
        Assert.Equal(DateTimeOffset.Parse("2026-09-10T12:01:05Z"), chat.UpdatedAt);
    }

    [SqliteFact]
    public void SqliteOpensReadOnlyAndAsImmutableWhenItMust()
    {
        using var temp = new TempDirectory();
        var state = ChatFixtures.WriteOpenClawSqlite(temp);
        var database = Path.Combine(state, "agents", "main", "agent", "openclaw-agent.sqlite");

        using var db = SqliteReader.OpenReadOnly(database);
        Assert.True(db.HasTable("transcript_events"));
        Assert.Throws<IOException>(() => db.Query("INSERT INTO transcript_events VALUES ('x', 1, '{}', 1)", _ => { }));
        if (OperatingSystem.IsWindows()) Assert.Equal("file:///C:/Users/pat/a%20b%23.sqlite?immutable=1", SqliteReader.ImmutableUri(@"C:\Users\pat\a b#.sqlite"));
        else Assert.Equal("file:///tmp/a%20b%23.sqlite?immutable=1", SqliteReader.ImmutableUri("/tmp/a b#.sqlite"));
    }

    // MARK: Claude.ai and ChatGPT exports

    [Fact]
    public void ClaudeAiExportZipReadsEachConversation()
    {
        using var temp = new TempDirectory();
        var zip = ChatFixtures.WriteExportZip(temp, "data-2026-08-03.zip", ChatFixtures.ClaudeAiConversations);

        var (chats, problem) = ChatExportFiles.Read(zip);

        Assert.Null(problem);
        var chat = Assert.Single(chats); // the empty conversation isn't a chat
        Assert.Equal(
        [
            "user: Plan a trip to Bend\n\n[Attached: notes.txt]\nLeave Friday\n\n[File: map.png]",
            "assistant: Here is a plan.",
            "tool artifacts [Bend itinerary] → # Day 1",
            "assistant: Enjoy!",
            "user: Thanks",
        ], chat.Rows.Select(Line).ToArray());
        Assert.Equal("# Day 1\nHike", chat.Rows[2].Output); // the artifact's content is kept
        Assert.Equal(ChatSource.ClaudeAi, chat.Source);
        Assert.Equal("c-1", chat.SourceId);
        Assert.Equal("Trip plan", chat.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:00:30Z"), chat.Rows[1].At);
    }

    [Fact]
    public void ChatGptExportFollowsTheCurrentNodesPath()
    {
        using var temp = new TempDirectory();
        var zip = ChatFixtures.WriteExportZip(temp, "chatgpt.zip", ChatFixtures.ChatGptConversations);

        var (chats, problem) = ChatExportFiles.Read(zip);

        Assert.Null(problem);
        var chat = Assert.Single(chats);
        Assert.Equal(
        [
            "user: [image]\n\nSum 2 and 3 in Python",
            "tool python [print(2+3)] → 5",
            "assistant: The sum is 5.",
            "user: thanks",
        ], chat.Rows.Select(Line).ToArray());
        Assert.Equal("g-1", chat.SourceId);
        Assert.Equal("Python help", chat.Title);
        Assert.Equal("gpt-4o", chat.Model);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1756720000500), chat.StartedAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1756720003000), chat.Rows[1].At); // the call's time
    }

    [Fact]
    public void AZipWithoutConversationsSaysWhatToChoose()
    {
        using var temp = new TempDirectory();
        var path = temp["other.zip"];
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create)) zip.CreateEntry("readme.txt");

        var (chats, problem) = ChatExportFiles.Read(path);

        Assert.Empty(chats);
        Assert.Contains("has no conversations.json", problem);
    }

    [Fact]
    public void ASessionFileChosenByHandIsRecognised()
    {
        using var temp = new TempDirectory();
        var claude = temp.Write("a.jsonl", ChatFixtures.ClaudeSession);
        var codex = temp.Write("b.jsonl", ChatFixtures.CodexRollout);
        var openClaw = temp.Write("c.jsonl", ChatFixtures.OpenClawSession);

        Assert.Equal(ChatSource.ClaudeCode, Assert.Single(ChatExportFiles.Read(claude).Chats).Source);
        Assert.Equal(ChatSource.Codex, Assert.Single(ChatExportFiles.Read(codex).Chats).Source);
        Assert.Equal(ChatSource.OpenClaw, Assert.Single(ChatExportFiles.Read(openClaw).Chats).Source);
    }

    // MARK: Words

    [Theory]
    [InlineData("""{"command":"git status","description":"x"}""", "git status")]
    [InlineData("""{"file_path":"/a/b.cs"}""", "/a/b.cs")]
    [InlineData("""{"command":["bash","-lc","ls -la"]}""", "ls -la")]
    [InlineData("""{"url":"https://example.test"}""", "https://example.test")]
    [InlineData("""{"x":1}""", """{"x":1}""")]
    [InlineData("\"{\\\"cmd\\\":\\\"pwd\\\"}\"", "pwd")]
    public void ArgumentsAreTheThingAPersonRecognisesACallBy(string json, string expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, ChatRowBuilder.Arguments(doc.RootElement));
    }

    [Fact]
    public void LongToolOutputIsCutWithANote()
    {
        var rows = new ChatRowBuilder(DateTimeOffset.UnixEpoch);
        rows.ToolCall("1", "Bash", "cat big", null);
        rows.ToolResult("1", null, new string('x', ChatRowBuilder.MaxToolOutput + 10), false, null);

        var output = rows.Rows[0].Output!;
        Assert.StartsWith(new string('x', ChatRowBuilder.MaxToolOutput), output);
        Assert.EndsWith("[10 more characters were not imported]", output);
    }
}

/// <summary>A fact that needs the system's SQLite (winsqlite3 on Windows, libsqlite3 elsewhere).</summary>
public sealed class SqliteFactAttribute : FactAttribute
{
    public SqliteFactAttribute()
    {
        if (!SqliteReader.IsAvailable) Skip = "No system SQLite library here.";
    }
}
