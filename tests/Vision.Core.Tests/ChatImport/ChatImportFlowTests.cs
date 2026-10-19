using System.Net;
using System.Text.Json;

namespace Vision.Core.Tests;

/// <summary>Guardian's chats over a scripted transport, and the import itself: chats written as real chats with their own
/// times and origin, a re-import that adds only what is new, the scan's memory, export (Markdown, JSON that imports again)
/// and the one search over every chat.</summary>
public sealed class ChatImportFlowTests
{
    private const string Address = "https://guardian.example.test";

    private static GuardianSession Signed() => new("u1", "ada@503cm.com", "Ada", "tok-1", DateTimeOffset.Now);

    // MARK: Guardian

    private static FakeHttpHandler GuardianServer() => new(request =>
    {
        var url = request.RequestUri!.PathAndQuery;
        return url switch
        {
            "/api/chats" => FakeHttpHandler.Json("""
                [{"id":"c1","title":"Quarterly numbers","createdAt":"2026-09-01T10:00:00.000Z","updatedAt":"2026-09-01T11:00:00.000Z","modelId":"claude-opus","sharedWithMe":false,"owner":{"id":"u1","email":"ada@503cm.com","name":"Ada"}},
                 {"id":"c2","title":"","createdAt":"2026-09-02T10:00:00.000Z","updatedAt":"2026-09-02T10:00:00.000Z","sharedWithMe":true,"owner":{"id":"u2","email":"bob@503cm.com","name":"Bob"}}]
                """),
            "/api/chats/c1?messages=latest&limit=200" => FakeHttpHandler.Json("""
                {"id":"c1","title":"Quarterly numbers","messages":[
                  {"id":"m3","role":"USER","content":"And Q4?","createdAt":"2026-09-01T10:30:00.000Z","attachments":[]},
                  {"id":"m4","role":"ASSISTANT","content":"Q4 was up 4%.","createdAt":"2026-09-01T10:30:10.000Z","modelId":"claude-opus","attachments":[],
                   "toolCalls":[{"id":"t1","name":"run_sql","args":{"query":"select 1"},"output":"4%","status":"done","startedAt":"2026-09-01T10:30:05.000Z","completedAt":"2026-09-01T10:30:06.000Z"}]}],
                 "paging":{"hasMore":true,"before":"m3","olderCount":2,"hasNewer":false,"after":null,"newerCount":0,"limit":200}}
                """),
            "/api/chats/c1/messages?before=m3&limit=200" => FakeHttpHandler.Json("""
                {"messages":[
                  {"id":"m0","role":"USER","origin":"system","content":"Daily numbers check started","createdAt":"2026-09-01T09:59:00.000Z","attachments":[]},
                  {"id":"m1","role":"USER","content":"Q3 numbers?","createdAt":"2026-09-01T10:00:00.000Z","attachments":[{"attachment":{"id":"a1","name":"q3.xlsx","contentType":"application/vnd.ms-excel"}}]},
                  {"id":"m2","role":"ASSISTANT","content":"Q3 was flat.","createdAt":"2026-09-01T10:00:20.000Z","attachments":[],
                   "toolCalls":[{"id":"t0","name":"read_file","args":{"path":"q3.xlsx"},"output":"locked","status":"error","startedAt":"2026-09-01T10:00:05.000Z"}]}],
                 "hasMore":false,"before":null,"olderCount":0,"hasNewer":true,"after":"m2","newerCount":null,"limit":200}
                """),
            "/api/chats/c1?messages=latest&limit=1" => FakeHttpHandler.Json("""
                {"id":"c1","messages":[{"id":"m4","role":"ASSISTANT","content":"Q4 was up 4%."}],"paging":{"hasMore":true,"before":"m4","olderCount":3,"limit":1}}
                """),
            _ => FakeHttpHandler.Json("""{"error":"not_found","message":"chat not found"}""", HttpStatusCode.NotFound),
        };
    });

    [Fact]
    public async Task GuardianChatsAreListedAndReadAPageAtATimeFromTheNewestBack()
    {
        var handler = GuardianServer();
        var client = new GuardianClient(Address, Signed(), handler) { OrgId = "org-1" };

        var chats = await client.WebChatsAsync();
        var chat = await GuardianChatImport.ReadAsync(client, chats.Single(c => c.Id == "c1"));

        Assert.Equal(["c2", "c1"], chats.Select(c => c.Id).ToArray()); // newest first
        Assert.Equal("Untitled chat", chats[0].Title);
        Assert.True(chats[0].SharedWithMe);
        Assert.Equal("Bob", chats[0].OwnerName);
        Assert.Equal(
        [
            "notice: Daily numbers check started",
            "user: Q3 numbers?\n\n[Attached: q3.xlsx]",
            "tool read_file [q3.xlsx] → failed: locked (error)",
            "assistant: Q3 was flat.",
            "user: And Q4?",
            "tool run_sql [select 1] → 4%",
            "assistant: Q4 was up 4%.",
        ], chat!.Rows.Select(r => r.Kind == "tool" ? $"tool {r.ToolName} [{r.ArgSummary}] → {r.Text}{(r.IsError ? " (error)" : "")}" : $"{r.Kind}: {r.Text}").ToArray());
        Assert.Equal(ChatSource.Guardian, chat.Source);
        Assert.Equal("c1", chat.SourceId);
        Assert.Equal(DateTimeOffset.Parse("2026-09-01T10:30:05Z"), chat.Rows[5].At); // a tool at the time it started
        Assert.Equal(Address, chat.From);
        // Every call carried the token and the org; nothing else was asked for.
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("Bearer tok-1", r.Headers["Authorization"]);
            Assert.Equal("org-1", r.Headers["x-guardian-org"]);
            Assert.Equal(HttpMethod.Get, r.Method);
        });
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AGuardianChatsCountComesFromOneTinyPage()
    {
        var client = new GuardianClient(Address, Signed(), GuardianServer());

        Assert.Equal(4, await GuardianChatImport.CountAsync(client, "c1"));
    }

    [Fact]
    public async Task AGuardianWithoutPagingSendsTheWholeTranscriptAsOnePage()
    {
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => FakeHttpHandler.Json("""
            {"id":"c9","messages":[{"id":"a","role":"USER","content":"hi","createdAt":"2026-01-01T00:00:00Z"},{"id":"b","role":"ASSISTANT","content":"hello","createdAt":"2026-01-01T00:00:01Z"}]}
            """)));

        var chat = await GuardianChatImport.ReadAsync(client, new GuardianWebChat("c9", "Old", null, DateTimeOffset.Parse("2026-01-01T00:00:01Z"), null));

        Assert.Equal(2, chat!.Messages);
        Assert.Equal(2, await GuardianChatImport.CountAsync(client, "c9"));
    }

    [Fact]
    public async Task AVisionChatSavedInGuardianComesBackWithItsId()
    {
        var payload = GuardianChats_Payload("chat-123", "Saved chat");
        var client = new GuardianClient(Address, Signed(), new FakeHttpHandler(_ => FakeHttpHandler.Json(
            JsonSerializer.Serialize(new { id = "chat-123", title = "Saved chat", updatedAt = "2026-09-05T08:00:00Z", payload }))));

        var chat = await GuardianChatImport.ReadSavedAsync(client, new GuardianChatSummary("chat-123", "Saved chat", DateTimeOffset.Parse("2026-09-05T08:00:00Z")));

        Assert.Equal(ChatSource.Vision, chat!.Source);
        Assert.Equal("chat-123", chat.KeepId);
        Assert.Equal(2, chat.Messages);
    }

    private static string GuardianChats_Payload(string id, string title) => Vision.Core.GuardianChats.ToPayload(id, title, null,
    [
        new LogItemRow(id, 0, "user", "ping", null, null, null, false, DateTimeOffset.Parse("2026-09-05T07:59:00Z")),
        new LogItemRow(id, 1, "assistant", "pong", null, null, null, false, DateTimeOffset.Parse("2026-09-05T08:00:00Z")),
    ]);

    // MARK: Writing chats

    private static ImportedChat Sample(string id = "s-1", string? project = null) => new()
    {
        Source = ChatSource.ClaudeCode,
        SourceId = id,
        Title = "Login bug fix",
        Project = project,
        Model = "claude-opus-4-5",
        StartedAt = DateTimeOffset.Parse("2026-10-01T09:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-10-01T09:30:00Z"),
        From = "/x/s-1.jsonl",
        Rows =
        [
            new LogItemRow("", 0, "user", "fix it", null, null, null, false, DateTimeOffset.Parse("2026-10-01T09:00:00Z")),
            new LogItemRow("", 1, "tool", "ok", "Bash", "ls", "a\nb", false, DateTimeOffset.Parse("2026-10-01T09:10:00Z")),
            new LogItemRow("", 2, "assistant", "fixed", null, null, null, false, DateTimeOffset.Parse("2026-10-01T09:30:00Z")),
        ],
    };

    [Fact]
    public void AnImportedChatIsARealChatWithItsOwnTimesAndOrigin()
    {
        using var temp = new TempDirectory();
        var log = new ConversationLog(temp["conversations"]);

        var id = ChatImporter.Write(log, Sample(project: temp.Path), now: DateTimeOffset.Parse("2026-10-19T12:00:00Z"))!;

        // As stored, and as a fresh log reads it back from disk.
        foreach (var store in new[] { log, new ConversationLog(temp["conversations"]) })
        {
            var session = store.Session(id)!;
            Assert.Equal("Login bug fix", session.Title);
            Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:30:00Z"), session.UpdatedAt); // not "now"
            Assert.Equal(temp.Path, session.Cwd); // the project exists here: the chat is in it
            Assert.Equal("claude-code", session.Imported!.Source);
            Assert.Equal("s-1", session.Imported.Id);
            Assert.Equal(2, session.Imported.Messages);
            Assert.Equal(DateTimeOffset.Parse("2026-10-19T12:00:00Z"), session.Imported.ImportedAt);
            var rows = store.LoadItems(id);
            Assert.Equal(["notice", "user", "tool", "assistant"], rows.Select(r => r.Kind).ToArray());
            Assert.StartsWith("Imported from Claude Code on Oct 19, 2026: 2 messages, ", rows[0].Text);
            Assert.EndsWith($", in {temp.Path}. Any model can carry on from here: earlier tool calls are shown as cards and reach the model as a written summary.", rows[0].Text);
            Assert.Equal(DateTimeOffset.Parse("2026-10-01T09:10:00Z"), rows[2].At);
            Assert.Equal("a\nb", rows[2].Output);
            Assert.All(rows, r => Assert.Equal(id, r.SessionId));
            Assert.Equal([0, 1, 2, 3], rows.Select(r => r.Seq).ToArray());
        }
    }

    [Fact]
    public void AProjectThatIsntOnThisPcIsNamedButNotUsed()
    {
        using var temp = new TempDirectory();
        var log = new ConversationLog(temp["conversations"]);

        var id = ChatImporter.Write(log, Sample(project: "/nowhere/on/this/pc"))!;

        Assert.Null(log.Session(id)!.Cwd);
        Assert.Contains(", in /nowhere/on/this/pc (not on this computer, so it continues without a project).", log.LoadItems(id)[0].Text);
        Assert.Equal("/nowhere/on/this/pc", log.Session(id)!.Imported!.Project);
    }

    [Fact]
    public async Task AReimportAddsOnlyTheNewChats()
    {
        using var temp = new TempDirectory();
        var log = new ConversationLog(temp["conversations"]);
        ChatImporter.Write(log, Sample("s-1"));
        var candidates = new[] { Sample("s-1"), Sample("s-2") }.Select(c => c.ToCandidate("/x")).ToList();

        var marked = ChatImporter.WithStatus(candidates, ChatImporter.ImportedKeys(log));
        var result = await ChatImporter.ImportAsync(log, marked);

        Assert.Equal([ChatImportStatus.Imported, ChatImportStatus.New], marked.Select(c => c.Status).ToArray());
        Assert.False(marked[0].SelectedByDefault);
        Assert.Single(result.Imported);
        Assert.Equal(1, result.AlreadyThere);
        Assert.Equal("Imported 1 chat (2 messages). 1 was already in Vision.", result.Summary());
        Assert.Equal(2, log.List().Count);
        // And again: nothing new.
        var again = await ChatImporter.ImportAsync(log, marked);
        Assert.Empty(again.Imported);
        Assert.Equal("Nothing new to import. 2 were already in Vision.", again.Summary());
    }

    [Fact]
    public async Task OneChatThatCantBeReadDoesntStopTheRest()
    {
        using var temp = new TempDirectory();
        var log = new ConversationLog(temp["conversations"]);
        var broken = Sample("bad").ToCandidate("/x", _ => throw new IOException("The file is locked."));

        var result = await ChatImporter.ImportAsync(log, [broken, Sample("good").ToCandidate("/x")]);

        Assert.Single(result.Imported);
        var (chat, reason) = Assert.Single(result.Failed);
        Assert.Equal("bad", chat.SourceId);
        Assert.Equal("The file is locked.", reason);
    }

    [Fact]
    public void AVisionChatKeepsItsIdAndIsRecognisedWhereItCameFrom()
    {
        using var temp = new TempDirectory();
        var here = new ConversationLog(temp["here"]);
        here.Upsert("chat-123", null, "Saved chat", "workspaceWrite");
        here.RecordItem("chat-123", "user", "ping");
        var json = ChatExport.Json(here.Session("chat-123")!, here.LoadItems("chat-123"));
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var chat = Assert.Single(ChatExportFiles.ReadJson(stream, "x.json", default).Item1);

        // On the PC it came from it is already there; on another it arrives under the same id, with no origin of its own.
        Assert.Null(ChatImporter.Write(here, chat));
        var there = new ConversationLog(temp["there"]);
        Assert.Equal("chat-123", ChatImporter.Write(there, chat));
        Assert.Null(there.Session("chat-123")!.Imported);
        Assert.Equal(["user"], there.LoadItems("chat-123").Select(r => r.Kind).ToArray()); // no import notice on a Vision chat
    }

    // MARK: The scan's memory

    [Fact]
    public void TheScanRemembersAFileUntilItChanges()
    {
        using var temp = new TempDirectory();
        ChatFixtures.WriteClaudeProfile(temp);
        var path = temp[$"projects/C--code-shop/{ChatFixtures.ClaudeSessionId}.jsonl"];
        var locations = new ChatImportLocations(temp.Path, ClaudeConfigDir: temp.Path);
        var cachePath = temp["cache/chat-scan.json"];
        var cache = ChatScanCache.Load(cachePath);
        Assert.Equal("Login bug fix", Assert.Single(ChatImportScanner.ClaudeCode(locations, cache)).Title);
        cache.Save();
        Assert.True(File.Exists(cachePath));
        Assert.DoesNotContain("Fix the login bug", File.ReadAllText(cachePath)); // titles and counts only

        // Same size and time: the remembered summary, even though the bytes differ.
        var stamp = File.GetLastWriteTimeUtc(path);
        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("Login bug fix", "Login bug FIX"));
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.Equal("Login bug fix", Assert.Single(ChatImportScanner.ClaudeCode(locations, ChatScanCache.Load(cachePath))).Title);

        // A newer time: read again.
        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(1));
        Assert.Equal("Login bug FIX", Assert.Single(ChatImportScanner.ClaudeCode(locations, ChatScanCache.Load(cachePath))).Title);
    }

    // MARK: Export

    private static (LogSession, IReadOnlyList<LogItemRow>) Exportable(TempDirectory temp)
    {
        var log = new ConversationLog(temp["conversations"]);
        var id = ChatImporter.Write(log, Sample(), now: DateTimeOffset.Parse("2026-10-19T12:00:00Z"))!;
        return (log.Session(id)!, log.LoadItems(id));
    }

    [Fact]
    public void MarkdownReadsLikeTheChat()
    {
        using var temp = new TempDirectory();
        var (session, rows) = Exportable(temp);

        var markdown = ChatExport.Markdown(session, rows);

        Assert.StartsWith("# Login bug fix\n\n- Exported from Vision on ", markdown);
        Assert.Contains("\n- Imported from Claude Code (s-1)\n- 2 messages, ", markdown);
        Assert.Contains("\n---\n\n> Imported from Claude Code on ", markdown);
        Assert.Contains("**You** · ", markdown);
        Assert.Contains("\n\nfix it\n\n", markdown);
        Assert.Contains("**Tool: Bash** `ls` → ok\n\n<details><summary>Output</summary>\n\n```text\na\nb\n```\n\n</details>", markdown);
        Assert.Contains("**Assistant** · ", markdown);
        Assert.Equal("````", ChatExport.Fence("a ``` b")); // a fence the text can't close
    }

    [Fact]
    public void JsonExportIsTheMacsShapeAndImportsAgainKeepingTheOrigin()
    {
        using var temp = new TempDirectory();
        var (session, rows) = Exportable(temp);

        var json = ChatExport.Json(session, rows, DateTimeOffset.Parse("2026-10-19T13:00:00Z"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(["createdAt", "exportedAt", "format", "harness", "id", "rows", "source", "sourceId", "title", "updatedAt", "version"],
            root.EnumerateObject().Select(p => p.Name).ToArray()); // sorted, as the Mac writes them; no cwd here
        Assert.Equal("vision-chat", root.GetProperty("format").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("vision-windows", root.GetProperty("harness").GetString());
        Assert.Equal("2026-10-19T13:00:00.000Z", root.GetProperty("exportedAt").GetString());
        Assert.Equal("2026-10-01T09:30:00.000Z", root.GetProperty("updatedAt").GetString());
        Assert.Equal("claude-code", root.GetProperty("source").GetString());
        Assert.Equal("s-1", root.GetProperty("sourceId").GetString());
        var tool = root.GetProperty("rows")[2];
        Assert.Equal(["argSummary", "at", "isError", "kind", "output", "text", "toolName"], tool.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("2026-10-01T09:10:00.000Z", tool.GetProperty("at").GetString());

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var back = Assert.Single(ChatExportFiles.ReadJson(stream, "x.json", default).Item1);
        Assert.Equal(rows.Select(r => (r.Kind, r.Text, r.ToolName, r.ArgSummary, r.Output, r.At)),
            back.Rows.Select(r => (r.Kind, r.Text, r.ToolName, r.ArgSummary, r.Output, r.At)));
        // Where it really came from travels with it: on another PC the same Claude Code session is one chat.
        Assert.Equal(ChatSource.ClaudeCode, back.Source);
        Assert.Equal("s-1", back.SourceId);
        Assert.Equal(session.Id, back.KeepId);
        var there = new ConversationLog(temp["there"]);
        Assert.Equal(session.Id, ChatImporter.Write(there, back));
        Assert.Contains("claude-code:s-1", ChatImporter.ImportedKeys(there));
        Assert.Equal(rows.Count, there.LoadItems(session.Id).Count); // no second notice on top
        Assert.Null(ChatImporter.Write(there, Sample("s-1")));
    }

    [Fact]
    public void AnImportedChatSentToGuardianSaysWhereItCameFrom()
    {
        using var temp = new TempDirectory();
        var (session, rows) = Exportable(temp);

        using var doc = JsonDocument.Parse(Vision.Core.GuardianChats.ToPayload(session.Id, session.Title, session.Cwd, rows, session.Imported));

        Assert.Equal("claude-code", doc.RootElement.GetProperty("source").GetString());
        Assert.Equal("s-1", doc.RootElement.GetProperty("sourceId").GetString());
        Assert.Equal(812538600, doc.RootElement.GetProperty("rows")[2].GetProperty("at").GetDouble()); // Apple reference seconds
    }

    [Theory]
    [InlineData("Fix: the <login> bug?", "Fix  the  login  bug.md")] // as Vision for macOS names it
    [InlineData("   ", "Chat.md")]
    public void ExportFileNamesAreSafe(string title, string expected) =>
        Assert.Equal(expected, ChatExport.FileName(new LogSession { Id = "x", Title = title }, ".md"));

    // MARK: Search

    [Fact]
    public void OneSearchCoversEveryChatsTitleAndTextWithASourceFilter()
    {
        using var temp = new TempDirectory();
        var log = new ConversationLog(temp["conversations"]);
        log.Upsert("v1", null, "Budget review", null);
        log.RecordItem("v1", "user", "Where did the login budget go?");
        ChatImporter.Write(log, Sample("s-1")); // "Login bug fix": user "fix it", tool ls → a b
        ChatImporter.Write(log, Sample("s-2") with { Source = ChatSource.Codex, Title = "Other", Rows = [new LogItemRow("", 0, "assistant", "the LOGIN page works", null, null, null, false, DateTimeOffset.Parse("2026-10-02T00:00:00Z"))] });

        var all = ChatSearch.Run(log, "login");
        Assert.Equal(3, all.Count);
        Assert.Equal("Login bug fix", all[0].Session.Title); // a title match first

        var codex = Assert.Single(ChatSearch.Run(log, "login", ChatSource.Codex));
        Assert.Equal("Other", codex.Session.Title);
        Assert.Equal("the LOGIN page works", Assert.Single(codex.Matches).Snippet);

        // Every word must be there; a quoted phrase is one.
        Assert.Equal("Budget review", Assert.Single(ChatSearch.Run(log, "login budget")).Session.Title);
        Assert.Empty(ChatSearch.Run(log, "\"budget login\""));
        // Tool calls and their output count.
        var tool = Assert.Single(ChatSearch.Run(log, "Bash"));
        Assert.Equal("tool", Assert.Single(tool.Matches).Kind);
        Assert.Equal(ChatSource.Vision, ChatSearch.Run(log, "budget").Single().Source);
        // Accents and case don't matter.
        log.RecordItem("v1", "assistant", "Lunch at the Café Rouge");
        Assert.Equal("Budget review", Assert.Single(ChatSearch.Run(log, "cafe rouge")).Session.Title);
    }

    [Fact]
    public void ASnippetShowsTheWordsAroundTheMatch()
    {
        var row = new LogItemRow("x", 0, "assistant", new string('a', 100) + " needle " + new string('b', 200), null, null, null, false, DateTimeOffset.Now);

        var snippet = ChatSearch.Snippet(row, ["needle"]);

        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
        Assert.Contains(" needle ", snippet);
        Assert.True(snippet.Length < 220);
    }
}
