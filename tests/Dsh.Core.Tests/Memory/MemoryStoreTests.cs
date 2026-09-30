namespace Dsh.Core.Tests;

public sealed class MemoryStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new("dsh-memory");

    public void Dispose() => _dir.Dispose();

    private MemoryStore Store(TimeProvider? clock = null, int maxItems = MemoryStore.DefaultMaxItems) =>
        new(_dir.Path, clock, maxItems);

    private static MemoryDraft Note(string title, string body, string? project = null, string kind = MemoryKinds.Note,
                                    bool pinned = false, string[]? tags = null) =>
        new() { Title = title, Body = body, Project = project, Kind = kind, Pinned = pinned, Tags = tags ?? [] };

    // MARK: - Saving and persistence

    [Fact]
    public void SavedNotesSurviveARestart()
    {
        var first = Store();
        var saved = first.Save(Note("Deploy", "Run scripts/deploy.ps1 -Env staging; needs the VPN.", tags: ["Ops", "ci"]));
        Assert.Equal(MemorySaveKind.Created, saved.Kind);
        Assert.Equal(8, saved.Item.Id.Length);

        var second = Store();
        var loaded = Assert.Single(second.All());
        Assert.Equal(saved.Item.Id, loaded.Id);
        Assert.Equal("Deploy", loaded.Title);
        Assert.Equal(["ops", "ci"], loaded.Tags);
        Assert.Equal(saved.Item.CreatedAt, loaded.CreatedAt);
    }

    [Fact]
    public void DeletesAndUsageAreReplayedFromTheLog()
    {
        var first = Store();
        var keep = first.Save(Note("Keep", "The build needs .NET 10.")).Item;
        var drop = first.Save(Note("Drop", "An outdated fact about the old server.")).Item;
        first.NoteUsed([keep.Id, keep.Id]);
        Assert.True(first.Delete(drop.Id));
        Assert.False(first.Delete(drop.Id));

        var second = Store();
        var only = Assert.Single(second.All());
        Assert.Equal(keep.Id, only.Id);
        Assert.Equal(2, only.UseCount);
        Assert.NotNull(only.LastUsedAt);
    }

    [Fact]
    public void ACrashTornLastLineIsIgnored()
    {
        var first = Store();
        first.Save(Note("Whole", "A complete note that was saved properly."));
        File.AppendAllText(first.FilePath, "{\"op\":\"put\",\"item\":{\"id\":\"deadbeef\",\"ti");
        var second = Store();
        Assert.Equal("Whole", Assert.Single(second.All()).Title);
    }

    [Fact]
    public void CompactionKeepsOnlyLiveNotes()
    {
        var store = Store();
        var item = store.Save(Note("Edit me", "first wording of this rather long note")).Item;
        for (var i = 0; i < 5; i++) store.Update(item.Id, n => n with { Body = $"wording number {i} of this rather long note" });
        Assert.True(File.ReadAllLines(store.FilePath).Length >= 6);
        store.Compact();
        Assert.Single(File.ReadAllLines(store.FilePath));
        Assert.Equal("wording number 4 of this rather long note", Store().Get(item.Id)!.Body);
    }

    [Fact]
    public void SavingTheSameThingUpdatesInsteadOfDuplicating()
    {
        var store = Store();
        var first = store.Save(Note("Test command", "Run dotnet test tests/Dsh.Core.Tests to run the unit tests."));
        var again = store.Save(Note("test command", "Run dotnet test tests/Dsh.Core.Tests to run the unit tests quickly.", tags: ["testing"]));
        Assert.Equal(MemorySaveKind.Updated, again.Kind);
        Assert.Equal(first.Item.Id, again.Item.Id);
        Assert.Single(store.All());
        Assert.Contains("testing", again.Item.Tags);

        // Same words under a different title still fold in.
        var reworded = store.Save(Note("How to run unit tests", "Run dotnet test tests/Dsh.Core.Tests to run the unit tests."));
        Assert.Equal(MemorySaveKind.Updated, reworded.Kind);
        Assert.Single(store.All());

        // A genuinely different note is kept.
        store.Save(Note("Deploy", "Deployments go through the staging cluster first."));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void TheSameTitleInAnotherProjectIsADifferentNote()
    {
        var store = Store();
        store.Save(Note("Build", "dotnet build DSH.sln", project: @"C:\code\dsh"));
        store.Save(Note("Build", "npm run build", project: @"C:\code\web"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void FieldsAreCleanedAndBounded()
    {
        var store = Store();
        var item = store.Save(new MemoryDraft
        {
            Title = "",
            Body = new string('x', MemoryStore.MaxBodyLength + 500),
            Tags = ["  Big Tag  ", "big-tag", "", .. Enumerable.Range(0, 20).Select(i => $"t{i}")],
            Kind = "nonsense",
        }).Item;
        Assert.True(item.Body.Length <= MemoryStore.MaxBodyLength + 1);
        Assert.True(item.Title.Length <= MemoryStore.MaxTitleLength + 1);
        Assert.Equal(MemoryKinds.Note, item.Kind);
        Assert.Equal(MemoryStore.MaxTags, item.Tags.Count);
        Assert.Equal("big-tag", item.Tags[0]);
    }

    [Fact]
    public void TheStorePrunesTheLeastUsefulNotesPastItsCap()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(clock, maxItems: 20);
        var pinned = store.Save(Note("Pinned fact", "Always keep this standing fact about the user.", pinned: true)).Item;
        var used = store.Save(Note("Used note", "This one gets used all the time by the agent.")).Item;
        store.NoteUsed([used.Id, used.Id, used.Id]);
        for (var i = 0; i < 40; i++)
        {
            clock.Advance(TimeSpan.FromDays(1));
            store.Save(Note($"Filler {i}", $"Unrelated filler note number {i} about topic{i} and thing{i}."));
        }
        Assert.True(store.Count <= 20);
        Assert.NotNull(store.Get(pinned.Id));
        Assert.NotNull(store.Get(used.Id));
    }

    [Fact]
    public void ImportIsIdempotentPerSourceAndTitle()
    {
        var store = Store();
        var drafts = new[]
        {
            Note("Alpha", "The first imported note has enough words.") with { Source = "import:openclaw/MEMORY.md" },
            Note("Beta", "The second imported note has enough words.") with { Source = "import:openclaw/MEMORY.md" },
        };
        Assert.Equal(2, store.Import(drafts));
        Assert.Equal(0, store.Import(drafts.Select(d => d with { Body = d.Body + " Edited upstream." })));
        Assert.Equal(2, store.Count);
        Assert.All(store.All(), n => Assert.EndsWith("Edited upstream.", n.Body));
    }

    [Fact]
    public void DeleteWhereForgetsAnImportWholesale()
    {
        var store = Store();
        store.Import([Note("A", "imported note about apples and oranges") with { Source = "import:x" }]);
        store.Save(Note("Mine", "a note I wrote myself about pears"));
        Assert.Equal(1, store.DeleteWhere(n => n.Source == "import:x"));
        Assert.Equal("Mine", Assert.Single(store.All()).Title);
    }

    [Fact]
    public void ChangedFiresOnContentChanges()
    {
        var store = Store();
        var fired = 0;
        store.Changed += (_, _) => fired++;
        var revision = store.Revision;
        var item = store.Save(Note("One", "A note that will change soon enough.")).Item;
        store.Update(item.Id, n => n with { Pinned = true });
        store.Delete(item.Id);
        Assert.Equal(3, fired);
        Assert.True(store.Revision > revision);
    }

    [Fact]
    public void UnwritableStorageDegradesToMemory()
    {
        // The directory can't be created because a file sits where it should be.
        var blocker = Path.Combine(_dir.Path, "blocked");
        File.WriteAllText(blocker, "x");
        var store = new MemoryStore(Path.Combine(blocker, "sub"));
        store.Save(Note("Still works", "The note lives in memory even if the disk refuses."));
        Assert.Single(store.All());
        Assert.NotNull(store.StorageProblem);
    }

    // MARK: - Searching

    private MemoryStore Seeded()
    {
        var store = Store();
        store.Save(Note("Deploy procedure", "Run scripts/deploy.ps1 against staging first; production needs the VPN.", kind: MemoryKinds.Procedure, tags: ["deployment", "ops"]));
        store.Save(Note("Coding style", "The user prefers tabs over spaces and file-scoped namespaces in C#.", kind: MemoryKinds.Preference));
        store.Save(Note("Database", "Postgres runs on port 5433 in the dev container, not the default 5432.", kind: MemoryKinds.Fact, tags: ["postgres"]));
        store.Save(Note("Release notes", "Release notes are generated from commit messages by scripts/notes.ps1."));
        store.Save(Note("Team", "Maria owns the payments service; Chen owns identity."));
        for (var i = 0; i < 25; i++)
            store.Save(Note($"Misc {i}", $"Miscellaneous observation {i} about widget{i} and gadget{i}."));
        return store;
    }

    [Theory]
    [InlineData("how do I deploy this to staging?", "Deploy procedure")]
    [InlineData("deployment steps", "Deploy procedure")]
    [InlineData("which port does postgres use", "Database")]
    [InlineData("should I use tabs or spaces here", "Coding style")]
    [InlineData("who owns payments", "Team")]
    [InlineData("generate the release notes", "Release notes")]
    public void FindsTheRightNoteFirst(string query, string expectedTitle)
    {
        var hits = Seeded().Search(query);
        Assert.NotEmpty(hits);
        Assert.Equal(expectedTitle, hits[0].Item.Title);
    }

    [Theory]
    [InlineData("what is the weather like today")]
    [InlineData("write a haiku about autumn leaves")]
    [InlineData("thanks")]
    [InlineData("")]
    public void UnrelatedMessagesMatchNothing(string query)
    {
        Assert.Empty(Seeded().Search(query));
    }

    [Fact]
    public void ALongMessageStillFindsItsNote()
    {
        var hits = Seeded().Search("Please refactor the payment module, rename a few helpers, update the README and, while you're there, " +
                                   "double-check the postgres connection string uses the right port for the dev container.");
        Assert.Contains(hits, h => h.Item.Title == "Database");
    }

    [Fact]
    public void ProjectNotesStayInTheirProject()
    {
        var store = Store();
        store.Save(Note("Build", "Build with dotnet build DSH.sln", project: @"C:\code\dsh"));
        store.Save(Note("Build", "Build with npm run build please", project: @"C:\code\web"));
        store.Save(Note("Editor", "The user builds everything from the terminal, never from the editor."));

        var inDsh = store.Search("how do I build this", new MemoryQueryOptions { Project = @"C:\code\dsh\" });
        Assert.Contains(inDsh, h => h.Item.Body.Contains("DSH.sln"));
        Assert.DoesNotContain(inDsh, h => h.Item.Body.Contains("npm"));
        // The global note is visible from everywhere.
        Assert.Contains(inDsh, h => h.Item.Title == "Editor");

        var nowhere = store.Search("how do I build this", new MemoryQueryOptions { Project = null });
        Assert.DoesNotContain(nowhere, h => h.Item.Project is not null);
        var all = store.Search("how do I build this", new MemoryQueryOptions { AllProjects = true });
        Assert.Contains(all, h => h.Item.Body.Contains("npm"));
    }

    [Fact]
    public void PinnedNotesAreLeftOutOfPerMessageSearch()
    {
        var store = Store();
        store.Save(Note("Name", "The user is called Sam and lives in Lisbon.", pinned: true));
        Assert.Empty(store.Search("what is the user called"));
        Assert.Single(store.Search("what is the user called", new MemoryQueryOptions { IncludePinned = true }));
        Assert.Single(store.Pinned(null));
    }

    [Fact]
    public void FrequentlyUsedAndFreshNotesRankHigher()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(clock);
        var stale = store.Save(Note("Logging (old)", "Serilog logging to a file sink; legacy configuration from the first prototype, mostly obsolete.")).Item;
        clock.Advance(TimeSpan.FromDays(400));
        var fresh = store.Save(Note("Logging (current)", "Serilog logging to a file sink; rolling daily, keep 14 files, warning level in production.")).Item;
        Assert.Equal(2, store.Count);
        var hits = store.Search("serilog logging file sink");
        Assert.Equal(fresh.Id, hits[0].Item.Id);
        Assert.Equal(stale.Id, hits[1].Item.Id);
    }

    [Fact]
    public void UpdatesAreVisibleToSearchImmediately()
    {
        var store = Store();
        var item = store.Save(Note("Server", "The staging server is called orion.")).Item;
        Assert.Empty(store.Search("pluto"));
        store.Update(item.Id, n => n with { Body = "The staging server is called pluto." });
        Assert.Single(store.Search("pluto"));
        store.Delete(item.Id);
        Assert.Empty(store.Search("pluto"));
    }

    [Fact]
    public void SearchStaysFastOnALargeStore()
    {
        var store = Store(maxItems: 20_000);
        var drafts = Enumerable.Range(0, 8_000).Select(i =>
            Note($"Topic {i}", $"Note {i}: the service{i % 300} talks to queue{i % 97} using protocol{i % 13} on port {5000 + i % 50}.", tags: [$"tag{i % 40}"]));
        store.Import(drafts);
        store.Save(Note("Needle", "The frobnicator lives behind the gateway on the blue rack."));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        store.Search("warm up the index"); // builds the index
        var warm = sw.Elapsed;
        sw.Restart();
        for (var i = 0; i < 50; i++) Assert.Equal("Needle", store.Search("where is the frobnicator")[0].Item.Title);
        var perQuery = sw.Elapsed.TotalMilliseconds / 50;
        Assert.True(perQuery < 25, $"{perQuery:0.0} ms per search over {store.Count} notes (index build {warm.TotalMilliseconds:0} ms)");
    }
}
