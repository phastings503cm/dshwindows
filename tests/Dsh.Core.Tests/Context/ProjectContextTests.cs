namespace Dsh.Core.Tests;

/// <summary>Ported from the ProjectContextTests class in HarnessTests.swift.</summary>
public sealed class ProjectContextTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-ctx");
    /// <summary>Never read the developer's real ~/.claude, ~/.cursor or %APPDATA% skills.</summary>
    private readonly SkillLocations _isolated;

    public ProjectContextTests() => _isolated = new SkillLocations(_root["fake-home"], _root["fake-support"]);

    public void Dispose() => _root.Dispose();

    [Fact]
    public void LoadsInstructionFiles()
    {
        _root.Write("AGENTS.md", "Follow the house style.");
        _root.Write("MEMORY.md", "Remember the port is 8002.");
        var context = ProjectContext.Load(_root.Path, _isolated);
        Assert.Equal(["AGENTS.md", "MEMORY.md"], context.Instructions.Select(i => i.Label));

        var prompt = context.PromptSupplement("ENV");
        Assert.Contains("Follow the house style.", prompt);
        Assert.Contains("Remember the port is 8002.", prompt);
        Assert.Contains("ENV", prompt);
    }

    [Fact]
    public void EmptyInstructionFileIsIgnored()
    {
        _root.Write("QWEN.md", "   \n");
        Assert.Empty(ProjectContext.Load(_root.Path, _isolated).Instructions);
    }

    [Fact]
    public void SkillCatalogListsNameAndDescriptionOnly()
    {
        _root.Write(".agents/skills/deploy/SKILL.md", """
            ---
            name: deploy
            description: Use when shipping a release build.
            ---

            # Deploy
            Secret detail that should not be in the catalog line.
            """);

        var context = ProjectContext.Load(_root.Path, _isolated);
        Assert.Equal(["deploy"], context.Skills.Select(s => s.Name));
        var prompt = context.PromptSupplement("");
        Assert.Contains("Use when shipping a release build.", prompt);
        Assert.DoesNotContain("Secret detail", prompt);
    }

    [Fact]
    public void ProjectSkillsWinOverUserSkillsOnNameClash()
    {
        _root.Write(".dsh/skills/x/SKILL.md", "---\nname: x\ndescription: high\n---\n");
        _root.Write(".agents/skills/x/SKILL.md", "---\nname: x\ndescription: low\n---\n");

        var skill = Assert.Single(ProjectContext.Load(_root.Path, _isolated).Skills);
        Assert.Equal("high", skill.Description);
    }

    [Fact]
    public void FrontmatterParsing()
    {
        var parsed = ProjectContext.Frontmatter("---\nname: a\ndescription: \"quoted: value\"\n---\nbody");
        Assert.Equal("a", parsed["name"]);
        Assert.Equal("quoted: value", parsed["description"]);
        Assert.Empty(ProjectContext.Frontmatter("no frontmatter here"));
    }

    [Fact]
    public void SetUpMemoryCreatesScaffoldIdempotently()
    {
        Assert.Equal(2, ProjectContext.SetUpMemory(_root.Path).Count);
        Assert.Empty(ProjectContext.SetUpMemory(_root.Path)); // existing files must not be overwritten
        Assert.True(File.Exists(_root["MEMORY.md"]));
    }

    [Fact]
    public void CreateSkillWritesFrontmatter()
    {
        var path = ProjectContext.CreateSkill(_root.Path, "Run Tests", "Use before opening a PR.");
        Assert.EndsWith(Path.Combine(".agents", "skills", "run-tests", "SKILL.md"), path);
        Assert.Equal("Use before opening a PR.", ProjectContext.Frontmatter(File.ReadAllText(path))["description"]);
    }

    [Fact]
    public void EnvironmentBlockStatesTheGroundTruth()
    {
        var block = ProjectContext.EnvironmentBlock(_root.Path, "qwen3", PermissionPreset.WorkspaceWrite);
        Assert.Contains(_root.Path, block);
        Assert.Contains("qwen3", block);
        Assert.Contains("workspaceWrite", block);
    }

    [Fact]
    public void GitBranchReadsHead()
    {
        _root.Write(".git/HEAD", "ref: refs/heads/feature/x\n");
        Assert.Equal("feature/x", ProjectContext.GitBranch(_root.Path));
    }

    [Fact]
    public void GitBranchIsNullOutsideARepository()
    {
        Assert.Null(ProjectContext.GitBranch(_root.Path));
    }
}

/// <summary>New: worktrees, detached HEADs, the daily log, and case-insensitive file names.</summary>
public sealed class ProjectContextLoadingTests : IDisposable
{
    private readonly TempDirectory _root = new("dsh-ctx2");
    private readonly SkillLocations _isolated;

    public ProjectContextLoadingTests() => _isolated = new SkillLocations(_root["fake-home"], _root["fake-support"]);

    public void Dispose() => _root.Dispose();

    [Fact]
    public void GitBranchFollowsAWorktreePointerAndShortensADetachedHead()
    {
        var project = _root["worktree"];
        _root.Write("main/.git/worktrees/wt/HEAD", "ref: refs/heads/topic\n");
        _root.Write("worktree/.git", "gitdir: ../main/.git/worktrees/wt\n");
        Assert.Equal("topic", ProjectContext.GitBranch(project));

        _root.Write("main/.git/worktrees/wt/HEAD", "0123456789abcdef0123456789abcdef01234567\n");
        Assert.Equal("01234567", ProjectContext.GitBranch(project));
    }

    [Fact]
    public void EnvironmentBlockNamesTheBranchShellAndExtras()
    {
        _root.Write(".git/HEAD", "ref: refs/heads/main\n");
        var shell = new AgentShell(ShellKind.Cmd, "cmd.exe", "Command Prompt (cmd.exe)");
        var block = ProjectContext.EnvironmentBlock(_root.Path, "m", PermissionPreset.Plan, shell, ["Extra: yes"]);
        var lines = block.Split('\n');
        Assert.Equal("--- Environment ---", lines[0]);
        Assert.Contains("Git branch: main", lines);
        Assert.Contains("Shell for run_shell_command: Command Prompt (cmd.exe)", lines);
        Assert.Contains("Permission preset: plan", lines);
        Assert.Equal("Extra: yes", lines[^1]);
    }

    [Fact]
    public void TodaysDailyLogIsLoadedAfterTheInstructionFiles()
    {
        var today = ProjectContext.DayStamp();
        _root.Write("CLAUDE.md", "claude rules");
        _root.Write($"memory/{today}.md", "- worked on the parser");
        _root.Write("memory/1999-01-01.md", "- ancient history");

        var context = ProjectContext.Load(_root.Path, _isolated);
        Assert.Equal(["CLAUDE.md", $"memory/{today}.md"], context.Instructions.Select(i => i.Label));
        Assert.DoesNotContain("ancient history", context.PromptSupplement(""));
    }

    [Fact]
    public void DayStampIsIsoDate()
    {
        Assert.Equal("2026-01-05", ProjectContext.DayStamp(new DateTime(2026, 1, 5, 23, 59, 0)));
    }

    [Fact]
    public void PromptSupplementCanLeaveSkillsOut()
    {
        _root.Write(".dsh/skills/lint/SKILL.md", "---\nname: lint\ndescription: Use when linting.\n---\n");
        var context = ProjectContext.Load(_root.Path, _isolated);
        Assert.Contains("Use when linting.", context.PromptSupplement("env"));
        Assert.DoesNotContain("Use when linting.", context.PromptSupplement("env", includeSkills: false));
    }

    [Fact]
    public void InstructionFileLineCount()
    {
        Assert.Equal(3, new InstructionFile("p", "a\nb\nc", "AGENTS.md").LineCount);
        Assert.Equal(0, new InstructionFile("p", "", "AGENTS.md").LineCount);
    }

    /// <summary>On a case-insensitive disk CLAUDE.md and claude.md are one file, loaded once.</summary>
    [WindowsFact]
    public void CaseVariantsOfOneFileLoadOnceOnWindows()
    {
        _root.Write("claude.md", "rules");
        var context = ProjectContext.Load(_root.Path, _isolated);
        Assert.Equal(["CLAUDE.md"], context.Instructions.Select(i => i.Label));
    }
}
