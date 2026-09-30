namespace Dsh.Core.Tests;

/// <summary>What an import changes about the prompt: your instructions everywhere, saved notes only in
/// the project they belong to.</summary>
public sealed class ExternalContextTests : IDisposable
{
    private readonly ProfileFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public void YourInstructionsApplyInEveryProjectAndComeFirst()
    {
        var app = _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        _fx.Write("AGENTS.md", "Project rules.", app);
        var other = _fx.Project("other");

        var context = ProjectContext.Load(app, _fx.Dsh, SkillSources.None);
        Assert.Equal(["claude-code.md", "AGENTS.md", "MEMORY.md (saved notes)"], context.Instructions.Select(i => i.Label));
        Assert.Equal([InstructionScope.User, InstructionScope.Project, InstructionScope.Notes], context.Instructions.Select(i => i.Scope));

        var elsewhere = ProjectContext.Load(other, _fx.Dsh, SkillSources.None);
        Assert.Equal(["claude-code.md"], elsewhere.Instructions.Select(i => i.Label)); // no notes: they belong to my-app
    }

    [Fact]
    public void ThePromptIntroducesEachKindByWhereItCameFrom()
    {
        var app = _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        var prompt = ProjectContext.Load(app, _fx.Dsh, SkillSources.None).PromptSupplement("ENV");

        Assert.Contains("--- claude-code.md (your instructions, for every project — follow these) ---\n# About me\nI prefer tabs.", prompt.Replace("\r\n", "\n"));
        Assert.Contains("--- Notes saved for this project (MEMORY.md (saved notes)) ---", prompt);
        Assert.Contains(ProjectContext.ProjectNotesFolder(app, _fx.Dsh), prompt); // where the model reads the notes from
        Assert.Contains("read the ones that apply with `read_file`", prompt);
        Assert.Contains("[Testing](feedback/testing.md)", prompt);
        Assert.DoesNotContain("A data scientist.", prompt); // the notes themselves are read on demand, not pasted in
    }

    [Fact]
    public void ALongNotesIndexIsCutInThePromptButNotInTheFile()
    {
        var app = _fx.Project("big");
        var folder = ProjectContext.ProjectNotesFolder(app, _fx.Dsh);
        Directory.CreateDirectory(folder);
        var lines = Enumerable.Range(1, ProjectContext.NotesIndexLines + 40).Select(n => $"- [n{n}](n{n}.md) — note {n}");
        File.WriteAllText(Path.Combine(folder, "MEMORY.md"), string.Join("\n", lines));

        var context = ProjectContext.Load(app, _fx.Dsh, SkillSources.None);
        var file = Assert.Single(context.Instructions);
        Assert.Contains("n240.md", file.Text); // the editor keeps every line, so saving never cuts the file
        var prompt = context.PromptSupplement("");
        Assert.Contains("n200.md", prompt);
        Assert.DoesNotContain("n201.md", prompt);
        Assert.Contains("40 more lines", prompt);
    }

    [Fact]
    public void YourInstructionsAreOnlyTheMarkdownFilesInTheirFolder()
    {
        Directory.CreateDirectory(_fx.Dsh.UserInstructions);
        File.WriteAllText(Path.Combine(_fx.Dsh.UserInstructions, "b-second.md"), "Second.");
        File.WriteAllText(Path.Combine(_fx.Dsh.UserInstructions, "a-first.md"), "First.");
        File.WriteAllText(Path.Combine(_fx.Dsh.UserInstructions, "empty.md"), "  \n");
        File.WriteAllText(Path.Combine(_fx.Dsh.UserInstructions, "notes.txt"), "Not markdown.");
        File.WriteAllText(Path.Combine(_fx.Dsh.UserInstructions, "a-first.md.bak"), "Backup.");
        Directory.CreateDirectory(Path.Combine(_fx.Dsh.UserInstructions, "nested"));
        File.WriteAllText(Path.Combine(_fx.Dsh.UserInstructions, "nested", "deep.md"), "Deep.");

        Assert.Equal(["a-first.md", "b-second.md"], ProjectContext.UserInstructionFiles(_fx.Dsh).Select(f => f.Label));
    }

    [Fact]
    public void AChatWithNoFolderStillGetsYourInstructions()
    {
        _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        var context = ProjectContext.UserOnly(_fx.Dsh);
        Assert.Equal(["claude-code.md"], context.Instructions.Select(i => i.Label));
        Assert.Contains("I prefer tabs.", context.PromptSupplement("ENV"));
        Assert.Empty(context.Skills);
    }

    [Fact]
    public void AChatWithNoFolderAndNoInstructionsGetsExactlyTheEnvironment()
    {
        Assert.Equal("ENV", ProjectContext.UserOnly(_fx.Dsh).PromptSupplement("ENV", includeSkills: false));
    }

    [Fact]
    public void WithNothingImportedThePromptIsAsItWas()
    {
        var project = _fx.Project("plain");
        _fx.Write("AGENTS.md", "Follow the house style.", project);
        var context = ProjectContext.Load(project, _fx.Dsh, SkillSources.None);
        Assert.Equal(["AGENTS.md"], context.Instructions.Select(i => i.Label));
        Assert.Contains("--- AGENTS.md (project instructions — follow these) ---", context.PromptSupplement(""));
        Assert.DoesNotContain("Notes saved", context.PromptSupplement(""));
    }
}
