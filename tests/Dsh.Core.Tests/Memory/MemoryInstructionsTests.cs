namespace Dsh.Core.Tests;

public sealed class MemoryInstructionsTests : IDisposable
{
    private readonly TempDirectory _project = new("dsh-mem-project");
    private readonly TempDirectory _data = new("dsh-mem-data");

    public void Dispose()
    {
        _project.Dispose();
        _data.Dispose();
    }

    private MemoryStore Store() => new(Path.Combine(_data.Path, "memory"));
    private SkillLocations Skills() => new(Path.Combine(_data.Path, "home"), Path.Combine(_data.Path, "appdata"));

    private ProjectContext Load() => ProjectContext.Load(_project.Path, Skills());

    private MemoryQueryOptions Here => new() { Project = _project.Path };

    private static string BigMemory(string marker = "") =>
        "# Project memory\n\n" + string.Join("\n\n", Enumerable.Range(1, 40).Select(i =>
            $"## Topic {i}\nFact number {i} about subsystem{i}: it is configured through the {(i == 27 ? "frobnicator" + marker : "setting" + i)} option and restarted nightly."));

    [Fact]
    public void SmallMemoryFilesAreLeftAlone()
    {
        _project.Write("MEMORY.md", "# Notes\n\n- the build needs .NET 10\n- tests run with dotnet test\n");
        var context = Load();
        var store = Store();
        Assert.Same(context, MemoryInstructions.Slim(context, store));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ABigMemoryFileKeepsItsOpeningAndBecomesSearchable()
    {
        var big = BigMemory();
        Assert.True(big.Length > MemoryInstructions.FullLoadChars * 2);
        _project.Write("MEMORY.md", big);
        var store = Store();
        var slim = MemoryInstructions.Slim(Load(), store);

        var file = Assert.Single(slim.Instructions, f => f.Label == "MEMORY.md");
        Assert.True(file.Text.Length < MemoryInstructions.HeadChars + 400, $"{file.Text.Length} characters");
        Assert.StartsWith("# Project memory", file.Text);
        Assert.Contains("more characters of this file are searchable", file.Text);
        Assert.DoesNotContain("Topic 30", file.Text);

        // The rest is in the store, and a question about it finds it.
        Assert.True(store.Count >= 20);
        Assert.All(store.All(), n => Assert.Equal("file:MEMORY.md", n.Source));
        Assert.All(store.All(), n => Assert.Equal(MemoryStore.NormalizeProject(_project.Path), n.Project));
        var hit = Assert.Single(store.Search("how is the frobnicator configured", new MemoryQueryOptions { Project = _project.Path }).Take(1)).Item;
        Assert.Contains("frobnicator", hit.Body);
    }

    [Fact]
    public void SlimmingTwiceDoesNotPileUpNotes()
    {
        _project.Write("MEMORY.md", BigMemory());
        var store = Store();
        MemoryInstructions.Slim(Load(), store);
        var count = store.Count;
        MemoryInstructions.Slim(Load(), store);
        MemoryInstructions.Slim(Load(), store);
        Assert.Equal(count, store.Count);
    }

    [Fact]
    public void EditingTheFileReplacesItsNotes()
    {
        _project.Write("MEMORY.md", BigMemory());
        var store = Store();
        MemoryInstructions.Slim(Load(), store);
        Assert.NotEmpty(store.Search("frobnicator configured", Here));

        _project.Write("MEMORY.md", BigMemory().Replace("frobnicator", "quuxinator") + "\n\n## Extra\nA brand new fact about the flux capacitor.");
        MemoryInstructions.Slim(Load(), store);
        Assert.Empty(store.Search("frobnicator configured", Here));
        Assert.NotEmpty(store.Search("quuxinator configured", Here));
        Assert.NotEmpty(store.Search("flux capacitor", Here));
    }

    [Fact]
    public void TodaysDailyLogIsSlimmedToo()
    {
        var day = ProjectContext.DayStamp();
        _project.Write($"memory/{day}.md", string.Join("\n", Enumerable.Range(1, 200).Select(i => $"- 10:{i % 60:00} looked at ticket {5000 + i} and left a comment about item{i}")));
        var store = Store();
        var slim = MemoryInstructions.Slim(Load(), store);
        var file = Assert.Single(slim.Instructions, f => f.Label.StartsWith("memory/"));
        Assert.Contains("searchable", file.Text);
        Assert.NotEmpty(store.All());
        Assert.All(store.All(), n => Assert.StartsWith("file:memory/", n.Source));
    }

    [Fact]
    public void OtherInstructionFilesAreNeverTouched()
    {
        var huge = "# Rules\n" + string.Join("\n", Enumerable.Range(1, 400).Select(i => $"- rule {i}: always do the thing number {i} carefully"));
        _project.Write("AGENTS.md", huge);
        _project.Write("MEMORY.md", BigMemory());
        var slim = MemoryInstructions.Slim(Load(), Store());
        Assert.Equal(huge, slim.Instructions.Single(f => f.Label == "AGENTS.md").Text);
    }

    [Fact]
    public void TheCutIsMadeAtAParagraph()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => $"Paragraph {i} " + new string('x', 90)));
        var head = MemoryInstructions.Head(text);
        var kept = head[..head.IndexOf("\n\n[…", StringComparison.Ordinal)];
        Assert.EndsWith(new string('x', 90), kept); // ends on a whole paragraph, not mid-word
        Assert.True(kept.Length <= MemoryInstructions.HeadChars);
    }

    [Fact]
    public void AChineseFileThatShrankUnderTheLimitInCharactersIsNoLongerMirrored()
    {
        // 1,300 characters of Chinese is ~3,900 bytes: under the limit that matters (characters), over it in bytes.
        var small = "# 项目记忆\n\n" + string.Concat(Enumerable.Repeat("部署到测试服务器需要先运行脚本。", 80));
        Assert.InRange(small.Length, 1_000, MemoryInstructions.FullLoadChars);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(small) > MemoryInstructions.FullLoadChars);
        var store = Store();
        // A big version was mirrored earlier.
        store.Import([new MemoryDraft { Title = "old", Body = "an old piece of the big file", Source = "file:MEMORY.md", Project = _project.Path }]);
        _project.Write("MEMORY.md", small);

        var context = MemoryInstructions.Slim(Load(), store);

        Assert.Equal(0, store.Count); // its stale pieces went: the file is loaded whole again
        Assert.Contains("部署到测试服务器", Assert.Single(context.Instructions, f => f.Label == "MEMORY.md").Text);
    }

    [Fact]
    public void AFileWithMoreSectionsThanAreIndexedSaysSo()
    {
        var text = "# Big\n\n" + string.Join("\n\n", Enumerable.Range(1, MemoryInstructions.MaxChunks + 50).Select(i => $"## Topic {i}\nFact number {i} about subsystem{i} is configured nightly and restarted."));
        _project.Write("MEMORY.md", text);
        var store = Store();

        var slim = MemoryInstructions.Slim(Load(), store);

        var head = Assert.Single(slim.Instructions, f => f.Label == "MEMORY.md").Text;
        Assert.Contains($"the first {MemoryInstructions.MaxChunks} sections are searchable", head);
        Assert.True(store.Count <= MemoryInstructions.MaxChunks);
        // And again on a later start, from the notes already there.
        var later = MemoryInstructions.Slim(Load(), store);
        Assert.Contains($"the first {MemoryInstructions.MaxChunks} sections are searchable", Assert.Single(later.Instructions, f => f.Label == "MEMORY.md").Text);
    }

    [Fact]
    public void AFileWithExactlyAsManySectionsAsTheLimitIsCompleteOnEveryStart()
    {
        var text = "# Big\n\n" + string.Join("\n\n", Enumerable.Range(1, MemoryInstructions.MaxChunks).Select(i => $"## Topic {i}\nFact number {i} about subsystem{i} is configured nightly and restarted."));
        _project.Write("MEMORY.md", text);
        var store = Store();
        var first = MemoryInstructions.Slim(Load(), store);
        var later = MemoryInstructions.Slim(Load(), store);

        foreach (var context in new[] { first, later })
        {
            var head = Assert.Single(context.Instructions, f => f.Label == "MEMORY.md").Text;
            Assert.Contains("more characters of this file are searchable", head);
            Assert.DoesNotContain("sections are searchable", head);
        }
    }

    [Fact]
    public void ASectionLeftOutForLookingLikeAKeyMeansTheFileIsNotAllSearchable()
    {
        var sections = Enumerable.Range(1, 60).Select(i => $"## Topic {i}\nFact number {i} about subsystem{i} is configured nightly and restarted.").ToList();
        sections[20] = "## Database\nThe production database password is hunter2hunter2 and lives in the vault too.";
        _project.Write("MEMORY.md", "# Big\n\n" + string.Join("\n\n", sections));
        var store = Store();

        var first = MemoryInstructions.Slim(Load(), store);
        var later = MemoryInstructions.Slim(Load(), store);

        Assert.DoesNotContain(store.All(), n => n.Body.Contains("hunter2hunter2"));
        foreach (var context in new[] { first, later })
        {
            var head = Assert.Single(context.Instructions, f => f.Label == "MEMORY.md").Text;
            Assert.DoesNotContain("more characters of this file are searchable", head); // it is not all searchable: it must not say so
            Assert.Contains("can only be read from the file", head);
        }
    }
}
