namespace Dsh.Core.Tests;

public sealed class MemoryRecallTests : IDisposable
{
    private readonly TempDirectory _dir = new("dsh-recall");

    public void Dispose() => _dir.Dispose();

    private MemoryStore Store()
    {
        var store = new MemoryStore(_dir.Path);
        store.Save(new MemoryDraft { Title = "Deploy procedure", Body = "Run scripts/deploy.ps1 against staging first; production needs the VPN.", Kind = "procedure" });
        store.Save(new MemoryDraft { Title = "Database", Body = "Postgres runs on port 5433 in the dev container." });
        store.Save(new MemoryDraft { Title = "Style", Body = "The user prefers tabs over spaces.", Kind = "preference" });
        return store;
    }

    [Fact]
    public void ARelevantMessageCarriesItsNotes()
    {
        var store = Store();
        var recall = MemoryRecall.Build(store, "how do I deploy to staging?", null);
        Assert.NotNull(recall);
        Assert.StartsWith(MemoryRecall.OpenTag, recall.Block);
        Assert.EndsWith(MemoryRecall.CloseTag, recall.Block);
        Assert.Contains("Deploy procedure", recall.Block);
        Assert.Contains("scripts/deploy.ps1", recall.Block);
        var item = Assert.Single(recall.Items);
        Assert.Contains($"[{item.Id}]", recall.Block);
        // Being shown counts as a use.
        Assert.Equal(1, store.Get(item.Id)!.UseCount);
    }

    [Fact]
    public void AnUnrelatedMessageCostsNothing()
    {
        Assert.Null(MemoryRecall.Build(Store(), "please summarise this paragraph about gardening", null));
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("thanks!")]
    [InlineData("/goal deploy the thing")]
    [InlineData("  ")]
    [InlineData("yes please")]
    public void TrivialMessagesAreNotSearched(string message)
    {
        Assert.False(MemoryRecall.Worthwhile(message));
        Assert.Null(MemoryRecall.Build(Store(), message, null));
    }

    [Fact]
    public void AnEmptyStoreIsFree()
    {
        Assert.Null(MemoryRecall.Build(new MemoryStore(Path.Combine(_dir.Path, "empty")), "how do I deploy to staging", null));
    }

    [Fact]
    public void ANoteIsNotRepeatedWithinTheCooldownAndReturnsAfterIt()
    {
        var store = Store();
        var session = new MemoryRecallSession { Cooldown = 3 };
        Assert.NotNull(MemoryRecall.Build(store, "how do I deploy to staging", null, session));
        // Same question straight away: it was just shown.
        Assert.Null(MemoryRecall.Build(store, "how do I deploy to staging again", null, session));
        Assert.Null(MemoryRecall.Build(store, "deploy staging once more", null, session));
        // Cooldown over.
        Assert.NotNull(MemoryRecall.Build(store, "what about deploying staging now", null, session));
    }

    [Fact]
    public void ResetLetsANoteBeShownAgainAfterCompaction()
    {
        var store = Store();
        var session = new MemoryRecallSession();
        Assert.NotNull(MemoryRecall.Build(store, "how do I deploy to staging", null, session));
        Assert.Null(MemoryRecall.Build(store, "how do I deploy to staging", null, session));
        session.Reset();
        Assert.NotNull(MemoryRecall.Build(store, "how do I deploy to staging", null, session));
    }

    [Fact]
    public void TheBlockRespectsItsBudget()
    {
        var store = new MemoryStore(_dir.Path);
        // (Enough other notes that "widget" is a distinctive word: in a store where every note has it, it says nothing.)
        for (var i = 0; i < 60; i++)
            store.Save(new MemoryDraft { Title = $"Topic {i}", Body = $"Topic{i} covers subsystem{i} and queue{i % 7} using protocol{i % 5}." });
        for (var i = 0; i < 12; i++)
            store.Save(new MemoryDraft { Title = $"Widget rule {i}", Body = $"Widget number {i} must always be configured with {new string('z', 300)} and validated." });
        Assert.Equal(72, store.Count);
        var recall = MemoryRecall.Build(store, "how should I configure a widget", null, options: new MemoryRecallOptions { MaxItems = 3, MaxChars = 900 });
        Assert.NotNull(recall);
        Assert.True(recall.Items.Count <= 3);
        Assert.True(recall.Block.Length <= 900 + 60, $"{recall.Block.Length} chars");
        // Even when the first note alone busts the budget it is still delivered (clipped), not dropped.
        var tiny = MemoryRecall.Build(store, "how should I configure a widget", null, options: new MemoryRecallOptions { MaxItems = 3, MaxChars = 50, BodyChars = 100 });
        Assert.NotNull(tiny);
        Assert.Single(tiny.Items);
    }

    [Fact]
    public void ProjectNotesAreOnlyRecalledInTheirProject()
    {
        var store = new MemoryStore(_dir.Path);
        store.Save(new MemoryDraft { Title = "Build", Body = "Build with dotnet build DSH.sln", Project = @"C:\code\dsh" });
        Assert.NotNull(MemoryRecall.Build(store, "how do we build the solution", @"C:\code\dsh"));
        Assert.Null(MemoryRecall.Build(store, "how do we build the solution", @"C:\code\other"));
        Assert.Null(MemoryRecall.Build(store, "how do we build the solution", null));
    }

    [Fact]
    public void ALineDoesNotRepeatATitleThatOpensItsBody()
    {
        var item = new MemoryItem { Id = "abcd1234", Title = "Use tabs", Body = "Use tabs for indentation in every C# file." };
        Assert.Equal("- [abcd1234] Use tabs for indentation in every C# file.", MemoryRecall.Format(item, 200));
        var other = new MemoryItem { Id = "abcd1234", Title = "Indentation", Body = "Use tabs." };
        Assert.Equal("- [abcd1234] Indentation — Use tabs.", MemoryRecall.Format(other, 200));
    }

    // MARK: - The system prompt section

    [Fact]
    public void PromptSectionTeachesTheToolsAndListsPinnedNotes()
    {
        var store = new MemoryStore(_dir.Path);
        var bare = MemoryPrompt.Section(store, null);
        Assert.Contains("memory_save", bare);
        Assert.Contains("memory_search", bare);
        Assert.Contains("memory_forget", bare);
        Assert.Contains("Credentials Vault", bare);
        Assert.DoesNotContain("Always-on notes", bare);

        store.Save(new MemoryDraft { Title = "Name", Body = "The user is called Sam.", Pinned = true });
        store.Save(new MemoryDraft { Title = "Other project", Body = "Only matters elsewhere.", Pinned = true, Project = @"C:\elsewhere" });
        store.Save(new MemoryDraft { Title = "Not pinned", Body = "Searched for, not always loaded." });
        var withPinned = MemoryPrompt.Section(store, @"C:\code\dsh");
        Assert.Contains("Always-on notes", withPinned);
        Assert.Contains("The user is called Sam.", withPinned);
        Assert.DoesNotContain("Only matters elsewhere", withPinned);
        Assert.DoesNotContain("Not pinned", withPinned);
    }

    [Fact]
    public void ThePromptSectionIsStableWhileNothingPinnedChanges()
    {
        var store = Store();
        var before = MemoryPrompt.Section(store, null);
        store.Save(new MemoryDraft { Title = "Something new", Body = "An ordinary note that is not pinned." });
        MemoryRecall.Build(store, "how do I deploy to staging", null);
        Assert.Equal(before, MemoryPrompt.Section(store, null)); // byte for byte: the server's prompt cache stays warm
    }
}

public sealed class MemoryToolTests : IDisposable
{
    private readonly ToolTestContext _tools = new();
    private readonly MemoryStore _store;

    public MemoryToolTests()
    {
        _store = new MemoryStore(Path.Combine(_tools.Root.Path, "memory"));
    }

    public void Dispose() => _tools.Dispose();

    [Fact]
    public async Task SaveSearchAndForget()
    {
        var saved = await _tools.Output(new MemorySaveTool(_store),
            """{"title":"Test command","content":"Run dotnet test tests/Dsh.Core.Tests for the unit tests.","kind":"procedure","tags":["testing"]}""");
        Assert.StartsWith("Saved note [", saved);
        Assert.Contains("this project", saved); // procedures belong to the project by default
        var item = Assert.Single(_store.All());
        Assert.Equal(_tools.Root.Path, item.Project);

        var found = await _tools.Output(new MemorySearchTool(_store), """{"query":"how to run the unit tests"}""");
        Assert.Contains(item.Id, found);
        Assert.Contains("dotnet test", found);

        var forgot = await _tools.Output(new MemoryForgetTool(_store), $$"""{"id":"[{{item.Id}}]"}""");
        Assert.Contains("Forgot", forgot);
        Assert.Equal(0, _store.Count);
        Assert.StartsWith("Error: there is no note", await _tools.Output(new MemoryForgetTool(_store), $$"""{"id":"{{item.Id}}"}"""));
    }

    [Fact]
    public async Task PreferencesAreGlobalUnlessTheModelSaysOtherwise()
    {
        await _tools.Output(new MemorySaveTool(_store), """{"title":"Tabs","content":"The user prefers tabs over spaces.","kind":"preference"}""");
        await _tools.Output(new MemorySaveTool(_store), """{"title":"Local rule","content":"This repo forbids force pushes to main.","kind":"preference","scope":"project"}""");
        await _tools.Output(new MemorySaveTool(_store), """{"title":"Editor","content":"The user runs Neovim on every machine.","kind":"fact","scope":"global"}""");
        var byTitle = _store.All().ToDictionary(i => i.Title);
        Assert.Null(byTitle["Tabs"].Project);
        Assert.Equal(_tools.Root.Path, byTitle["Local rule"].Project);
        Assert.Null(byTitle["Editor"].Project);
    }

    [Fact]
    public async Task SavingTwiceUpdates()
    {
        await _tools.Output(new MemorySaveTool(_store), """{"title":"Port","content":"Postgres listens on 5433 in the dev container."}""");
        var again = await _tools.Output(new MemorySaveTool(_store), """{"title":"Port","content":"Postgres listens on 5433 in the dev container, not 5432."}""");
        Assert.StartsWith("Updated note", again);
        Assert.Equal(1, _store.Count);
    }

    [Theory]
    [InlineData("The OpenAI key is sk-abcdefghijklmnopqrstuvwxyz123456")]
    [InlineData("aws key AKIAABCDEFGHIJKLMNOP is in use")]
    [InlineData("password: hunter2hunter2")]
    [InlineData("token = ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIE")]
    public async Task CredentialsAreRefused(string content)
    {
        var output = await _tools.Output(new MemorySaveTool(_store), Args.Json(new { title = "Key", content }));
        Assert.StartsWith("Error: that looks like a credential", output);
        Assert.Equal(0, _store.Count);
    }

    [Theory]
    [InlineData("The vault entry named OPENAI_API_KEY holds the OpenAI key.")]
    [InlineData("Passwords are rotated every 90 days by the ops team.")]
    [InlineData("Use the API key from the vault when calling the billing service.")]
    public async Task TalkingAboutSecretsIsFine(string content)
    {
        var output = await _tools.Output(new MemorySaveTool(_store), Args.Json(new { title = "Secrets policy", content }));
        Assert.StartsWith("Saved note", output);
    }

    [Fact]
    public async Task MissingArgumentsAreReported()
    {
        Assert.StartsWith("Error:", await _tools.Output(new MemorySaveTool(_store), """{"title":"x"}"""));
        Assert.StartsWith("Error:", await _tools.Output(new MemorySearchTool(_store), "{}"));
        Assert.StartsWith("Error:", await _tools.Output(new MemoryForgetTool(_store), "{}"));
        Assert.StartsWith("No saved notes match", await _tools.Output(new MemorySearchTool(_store), """{"query":"anything at all"}"""));
    }

    [Fact]
    public void ToolsNeverReceiveVaultValues()
    {
        foreach (var tool in MemoryTools.All(_store)) Assert.False(VaultPlaceholders.SubstitutesInto(tool.Name), tool.Name);
    }
}

public sealed class MemoryFilesTests
{
    [Fact]
    public void HeadingsBecomeNotes()
    {
        const string text = "# Project memory\n\nintro line that is long enough\n\n## Deploy\nRun the deploy script against staging.\n\n## Database\nPostgres is on port 5433.\n- second bullet with detail\n";
        var notes = MemoryFiles.Chunk(text, "import:x/MEMORY.md", "MEMORY.md");
        Assert.Equal(["Deploy", "Database"], notes.Select(n => n.Title));
        Assert.All(notes, n => Assert.Equal("import:x/MEMORY.md", n.Source));
        Assert.Contains("second bullet", notes[1].Body);
    }

    [Fact]
    public void LongSectionsAreSplitAtBullets()
    {
        var bullets = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"- fact number {i} is a reasonably long statement about the system"));
        var notes = MemoryFiles.Chunk("## Facts\n" + bullets, "s", "MEMORY.md");
        Assert.True(notes.Count > 2);
        Assert.All(notes, n => Assert.True(n.Body.Length < 900));
        Assert.Equal(notes.Count, notes.Select(n => n.Title).Distinct().Count());
        Assert.StartsWith("Facts (1)", notes[0].Title);
    }

    [Fact]
    public void DailyLogsKeepTheirDate()
    {
        var notes = MemoryFiles.Chunk("Talked to Maria about the payments rollout.\nDecided to ship on Friday.", "s", "2026-03-14.md");
        var note = Assert.Single(notes);
        Assert.Equal(new DateTimeOffset(2026, 3, 14, 0, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 3, 14))), note.CreatedAt);
        Assert.Contains("daily", note.Tags);
        Assert.Equal("2026-03-14", note.Title);
        Assert.Null(MemoryFiles.DayOf("MEMORY.md"));
    }

    [Fact]
    public void FrontmatterAndTinyFragmentsAreDropped()
    {
        var notes = MemoryFiles.Chunk("---\nname: x\n---\n## A\nok\n\n## B\nThis one has real content in it.", "s", "f.md");
        Assert.Equal(["B"], notes.Select(n => n.Title));
    }

    [Fact]
    public void RepeatedHeadingsGetDistinctTitles()
    {
        var notes = MemoryFiles.Chunk("## Notes\nfirst set of notes here\n\n## Notes\nsecond set of notes here", "s", "f.md");
        Assert.Equal(["Notes", "Notes #2"], notes.Select(n => n.Title));
    }

    [Fact]
    public void PlainTextWithoutHeadingsBecomesNotesNamedAfterTheFile()
    {
        var notes = MemoryFiles.Chunk("Just a paragraph of durable text with no headings at all.", "s", "memory.md");
        Assert.Equal("memory", Assert.Single(notes).Title);
    }
}
