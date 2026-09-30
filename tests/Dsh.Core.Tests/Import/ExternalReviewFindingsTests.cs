namespace Dsh.Core.Tests;

/// <summary>Cases found by review of the first version: each starts as a failing test, then the fix.</summary>
public sealed class ExternalReviewFindingsTests : IDisposable
{
    private readonly ProfileFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    // MARK: - "Keeping both" must leave two skills that are both on

    [Fact]
    public void AKeptCopyIsActiveNotShadowedByTheOriginal()
    {
        _fx.Skill(".claude/skills/review", "review", "Use when reviewing code.", "Review the diff.");
        _fx.ImportSuggested(_fx.Scan());
        _fx.Skill(".claude/skills/review", "review", "Use when reviewing code.", "Review the diff. Then summarise.");

        var changed = ProfileFixture.Item(_fx.Scan(), "review");
        ExternalImporter.Import(_fx.Scan(), [changed.Id], _fx.Dsh);

        var live = SkillCatalog.Load(null, _fx.Dsh, SkillSources.None);
        Assert.Equal(["review", "review-2"], live.Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(SkillCatalog.LoadAll(null, _fx.Dsh, SkillSources.None), s => s.Shadowed);
    }

    [Fact]
    public void AKeptCopyIsRecognisedAsImportedAfterwards()
    {
        _fx.Skill(".claude/skills/review", "review", "Use when reviewing code.", "One.");
        _fx.ImportSuggested(_fx.Scan());
        _fx.Skill(".claude/skills/review", "review", "Use when reviewing code.", "Two.");
        ExternalImporter.Import(_fx.Scan(), [ProfileFixture.Item(_fx.Scan(), "review").Id], _fx.Dsh);

        var again = _fx.Scan();
        Assert.Equal(ExternalStatus.Imported, ProfileFixture.Item(again, "review").Status);
        var repeat = ExternalImporter.Import(again, [ProfileFixture.Item(again, "review").Id], _fx.Dsh);
        Assert.Empty(repeat.Imported);
        Assert.False(Directory.Exists(Path.Combine(_fx.Dsh.UserSkills, "review-3")));
    }

    [Fact]
    public void TwoToolsWithTheSameNameAndDifferentTextBothStayOn()
    {
        _fx.Skill(".claude/skills/deploy", "deploy", "Use when deploying from Claude.", "Claude way.");
        _fx.Skill(".cursor/skills/deploy", "deploy", "Use when deploying from Cursor.", "Cursor way.");
        var inventory = _fx.Scan();
        ExternalImporter.Import(inventory, inventory.Items.Select(i => i.Id), _fx.Dsh);
        Assert.Equal(["deploy", "deploy-2"], SkillCatalog.Load(null, _fx.Dsh, SkillSources.None).Select(s => s.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    // MARK: - Approval drafts are not forgotten

    [Fact]
    public void SkillsWaitingForApprovalAreNotOfferedAgain()
    {
        _fx.Populate();
        var first = _fx.ImportSuggested(_fx.Scan(), asDrafts: true);
        Assert.Equal(14, first.Drafts.Count);

        var again = _fx.Scan();
        Assert.All(again.Items.Where(i => i.Kind is ExternalKind.Skill or ExternalKind.Command or ExternalKind.Rule or ExternalKind.Subagent && i.Name != "create-rule"),
            i => Assert.Equal(ExternalStatus.Waiting, i.Status));
        Assert.Equal(0, again.Suggested);

        // Ask for everything already queued, again (Cursor's built-in skill was never queued, so it is left out of this).
        var second = ExternalImporter.Import(again, again.Items.Where(i => i.Name != "create-rule").Select(i => i.Id), _fx.Dsh, skillsAsDrafts: true);
        Assert.Equal(14, SkillDrafts.List(_fx.Dsh).Count); // no duplicate drafts
        Assert.Empty(second.Drafts);
        Assert.Contains(second.Skipped, s => s.Reason == "Already waiting for your approval.");
        Assert.DoesNotContain(second.Drafts, d => d.Name is "pdf-tools" or "review");
    }

    // MARK: - Plugins that are not installed stay out

    [Fact]
    public void AnInstallListThatNamesNothingMeansNothingIsInstalled()
    {
        _fx.Populate();
        _fx.Write(".claude/plugins/installed_plugins.json", "{\"version\":2,\"plugins\":{}}");
        var inventory = _fx.Scan();
        Assert.DoesNotContain(inventory.Items, i => i.Group == "Plugin: toolbox"); // the cache still holds it, but it was uninstalled
    }

    [Fact]
    public void APluginInstalledForOneProjectIsNotTickedForEverywhere()
    {
        _fx.Populate();
        var install = Path.Combine(_fx.Home, ".claude", "plugins", "cache", "acme", "toolbox", "1.2.0");
        _fx.Write(".claude/plugins/installed_plugins.json",
            "{\"version\":2,\"plugins\":{\"toolbox@acme\":[{\"scope\":\"project\",\"projectPath\":" + ProfileFixture.Json(_fx.Project("only-here")) + ",\"installPath\":" + ProfileFixture.Json(install) + "}]}}");
        var lint = ProfileFixture.Item(_fx.Scan(), "lint");
        Assert.False(lint.SelectedByDefault);
        Assert.Contains("one project", lint.Reason);
    }

    [Fact]
    public void ACursorPluginsMcpFileIsAlsoNoted()
    {
        _fx.Populate();
        File.Delete(Path.Combine(_fx.Home, ".cursor", "plugins", "cache", "cursor-public", "deploy-on-aws", "7a17df718d26f07414b876e77a7480fa25089b08", ".mcp.json"));
        File.Delete(Path.Combine(_fx.Home, ".cursor", "plugins", "cache", "cursor-public", "deploy-on-aws", "7a17df718d26f07414b876e77a7480fa25089b08", "hooks", "hooks.json"));
        _fx.Write(".cursor/plugins/cache/cursor-public/deploy-on-aws/7a17df718d26f07414b876e77a7480fa25089b08/mcp.json", "{}");
        Assert.Contains(_fx.Scan().Left, n => n.Tool == ExternalTool.Cursor && n.Text.Contains("hooks or MCP servers", StringComparison.Ordinal));
    }

    // MARK: - A linked folder inside a plugin is not followed

    [Fact]
    public void ALinkedContentFolderInsideAPluginIsNeverRead()
    {
        _fx.Populate();
        _fx.Write("private-notes/diary.md", "Dear diary, my password is hunter2.");
        var plugin = Path.Combine(_fx.Home, ".claude", "plugins", "cache", "acme", "toolbox", "1.2.0");
        Directory.Delete(Path.Combine(plugin, "commands"), recursive: true);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(plugin, "commands"), Path.Combine(_fx.Home, "private-notes"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // no symlink rights (Windows without developer mode)
        }
        var inventory = _fx.Scan();
        Assert.DoesNotContain(inventory.Items, i => i.Name == "diary");
        Assert.Contains(inventory.Left, n => n.Text.Contains("to somewhere else", StringComparison.Ordinal));
    }

    // MARK: - Notes: an update never silently replaces an edit

    [Fact]
    public void ChangedNotesAreOfferedButNotTickedAndAnEditIsBackedUp()
    {
        var app = _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        var notes = ProjectContext.ProjectNotesFolder(app, _fx.Dsh);
        File.WriteAllText(Path.Combine(notes, "user_role.md"), "I edited this in DSH.");
        var key = ClaudeProjectNames.Encode(app);
        _fx.Write($".claude/projects/{key}/memory/user_role.md", "---\nname: role\ndescription: who the user is\ntype: user\n---\nA data scientist and a manager.");

        var item = _fx.Scan().Items.Single(i => i.Kind == ExternalKind.ProjectNotes && i.ProjectPath is not null);
        Assert.Equal(ExternalStatus.Different, item.Status);
        Assert.False(item.SelectedByDefault, "an update is the user's call");
        Assert.Equal(0, _fx.Scan().Suggested);

        ExternalImporter.Import(_fx.Scan(), [item.Id], _fx.Dsh);
        Assert.Contains("manager", File.ReadAllText(Path.Combine(notes, "user_role.md")));
        var backups = Directory.GetFiles(notes, "user_role.md*.bak");
        Assert.Single(backups);
        Assert.Equal("I edited this in DSH.", File.ReadAllText(backups[0]));
    }

    [Fact]
    public void BackupsOfInstructionsAreKeptSeparately()
    {
        _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        for (var round = 1; round <= 3; round++)
        {
            _fx.Write(".claude/CLAUDE.md", $"# About me\nVersion {round}.\n");
            ExternalImporter.Import(_fx.Scan(), [_fx.Scan().Items.Single(i => i.Kind == ExternalKind.Instructions).Id], _fx.Dsh);
        }
        Assert.Equal(3, Directory.GetFiles(_fx.Dsh.UserInstructions, "claude-code.md*.bak").Length); // the original and two later versions
        Assert.Equal(["claude-code.md"], ProjectContext.UserInstructionFiles(_fx.Dsh).Select(f => f.Label));
    }

    // MARK: - Secrets: the formats people actually have

    [Theory]
    [InlineData("key sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("key sk-proj-abcdefghijklmnopqrstuvwxyz_0123456789-abc")]
    [InlineData("OPENAI=sk-abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("stripe sk_" + "live_abcdefghijklmnopqrstuvwx")]
    [InlineData("google AIzaSyA-abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("hf hf_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("gitlab glpat-abcdefghijklmnopqrstuv")]
    [InlineData("aws ASIAABCDEFGHIJKLMNOP")]
    [InlineData("gh github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz0123456789abcdefghijk")]
    public void KeysInTheFormatsPeopleActuallyHaveAreRecognised(string text) => Assert.True(SkillLint.ContainsSecret(text), text);

    [Theory]
    [InlineData("Use the task-runner-configuration-service for this.")]
    [InlineData("disk-usage-analyzer-and-cleanup-tool")]
    [InlineData("Set sk to the size of the block.")]
    [InlineData("A plain sentence about keys and tokens without any.")]
    public void OrdinaryTextIsNotTakenForAKey(string text) => Assert.False(SkillLint.ContainsSecret(text), text);

    [Fact]
    public void ASecretInABundledFileIsFlaggedToo()
    {
        _fx.Skill(".claude/skills/deploy-app", "deploy-app", "Use when deploying the app.");
        _fx.Write(".claude/skills/deploy-app/config/.env", "API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz0123456789\n");
        _fx.Skill(".claude/skills/fine", "fine", "Use when fine.");
        _fx.Write(".claude/skills/fine/notes.md", "Nothing secret here.");
        _fx.Skill(".claude/skills/keyfile", "keyfile", "Use when signing.");
        _fx.Write(".claude/skills/keyfile/id_rsa", "not really a key, but named like one");

        var inventory = _fx.Scan();
        var deploy = ProfileFixture.Item(inventory, "deploy-app");
        Assert.False(deploy.SelectedByDefault);
        Assert.Contains(deploy.Warnings, w => w.Contains(".env", StringComparison.Ordinal));
        Assert.True(ProfileFixture.Item(inventory, "fine").SelectedByDefault);
        var keyfile = ProfileFixture.Item(inventory, "keyfile");
        Assert.False(keyfile.SelectedByDefault);
        Assert.Contains(keyfile.Warnings, w => w.Contains("id_rsa", StringComparison.Ordinal));
    }
}
