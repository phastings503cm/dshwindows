namespace Dsh.Core.Tests;

public sealed class ExternalImportTests : IDisposable
{
    private readonly ProfileFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private static string[] Names(ExternalInventory inventory, ExternalKind kind) =>
        inventory.Items.Where(i => i.Kind == kind).Select(i => i.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    // MARK: - Finding things

    [Fact]
    public void ScanFindsWhatEachToolHasInstalled()
    {
        _fx.Populate();
        var inventory = _fx.Scan();

        Assert.All(inventory.Tools, t => Assert.True(t.Found));
        Assert.Equal(["api-design", "create-rule", "deploy", "lint", "pdf-tools", "review"], Names(inventory, ExternalKind.Skill));
        Assert.Equal(["fmt", "git:commit", "release", "ship"], Names(inventory, ExternalKind.Command));
        Assert.Equal(["always", "style"], Names(inventory, ExternalKind.Rule));
        Assert.Equal(["code-reviewer", "helper", "qa"], Names(inventory, ExternalKind.Subagent));
        Assert.Single(Names(inventory, ExternalKind.Instructions));
        Assert.Equal(2, inventory.Items.Count(i => i.Kind == ExternalKind.ProjectNotes)); // the empty one isn't a note set
        Assert.DoesNotContain(inventory.Items, i => i.Name == "nope"); // a marketplace's plugin that was never installed
    }

    [Fact]
    public void ItemsSayWhereTheyCameFrom()
    {
        _fx.Populate();
        var inventory = _fx.Scan();

        Assert.Equal("Your skills", ProfileFixture.Item(inventory, "pdf-tools").Group);
        Assert.Equal("Plugin: toolbox", ProfileFixture.Item(inventory, "lint").Group);
        Assert.Equal("Plugin: toolbox", ProfileFixture.Item(inventory, "fmt").Group);
        Assert.Equal("Plugin: deploy-on-aws", ProfileFixture.Item(inventory, "deploy").Group);
        Assert.Equal("Cursor's built-in skills", ProfileFixture.Item(inventory, "create-rule").Group);
        Assert.Equal(ExternalTool.Cursor, ProfileFixture.Item(inventory, "api-design").Tool);
        Assert.Equal(ExternalTool.ClaudeCode, ProfileFixture.Item(inventory, "review").Tool);
    }

    [Fact]
    public void SkillDetailsAreCounted()
    {
        _fx.Populate();
        var pdf = ProfileFixture.Item(_fx.Scan(), "pdf-tools");
        Assert.Equal(3, pdf.FileCount);
        Assert.True(pdf.HasScripts);
        Assert.False(ProfileFixture.Item(_fx.Scan(), "review").HasScripts);
    }

    [Fact]
    public void NewItemsAreTickedAndTheRestSayWhy()
    {
        _fx.Populate();
        var inventory = _fx.Scan();

        Assert.True(ProfileFixture.Item(inventory, "review").SelectedByDefault);
        var builtIn = ProfileFixture.Item(inventory, "create-rule");
        Assert.False(builtIn.SelectedByDefault);
        Assert.Contains("Cursor", builtIn.Reason);

        var notes = inventory.Items.Where(i => i.Kind == ExternalKind.ProjectNotes).ToList();
        var mine = notes.Single(i => i.ProjectPath is not null);
        Assert.Equal("my-app", mine.Name);
        Assert.Equal(3, mine.FileCount);
        Assert.True(mine.SelectedByDefault);
        var gone = notes.Single(i => i.ProjectPath is null);
        Assert.Equal("old-app", gone.Name);
        Assert.False(gone.SelectedByDefault);
        Assert.Contains("isn't on this PC", gone.Reason);

        Assert.Equal(inventory.Items.Count - 2, inventory.Suggested); // everything but the built-in skill and the gone project
    }

    [Fact]
    public void ThingsDSHCantUseAreListedNotDropped()
    {
        _fx.Populate();
        var left = _fx.Scan().Left;
        string Text(ExternalTool tool, string contains) => left.Single(n => n.Tool == tool && n.Text.Contains(contains, StringComparison.Ordinal)).Text;

        var mcp = Text(ExternalTool.ClaudeCode, "MCP servers (");
        Assert.Contains("github", mcp);
        Assert.Contains("postgres", mcp);
        Assert.DoesNotContain("ghp_", string.Join("\n", left.Select(n => n.Text))); // names only — never the settings, which hold tokens
        Assert.Contains("figma", Text(ExternalTool.Cursor, "MCP server ("));
        Assert.Contains("Claude Code hooks", Text(ExternalTool.ClaudeCode, "Claude Code hooks"));
        Assert.Contains("allow/deny", Text(ExternalTool.ClaudeCode, "allow/deny"));
        Assert.Contains("1 Claude Code conversation —", Text(ExternalTool.ClaudeCode, "conversation"));
        Assert.Equal("1 plugin also ships hooks or MCP servers — DSH can't use those.", Text(ExternalTool.ClaudeCode, "1 plugin"));
        Assert.Equal("1 plugin also ships hooks or MCP servers — DSH can't use those.", Text(ExternalTool.Cursor, "1 plugin"));
        Assert.Contains("User Rules", Text(ExternalTool.Cursor, "User Rules"));
    }

    [Fact]
    public void ANewMachineHasNothing()
    {
        var inventory = _fx.Scan();
        Assert.False(inventory.AnyToolFound);
        Assert.Empty(inventory.Items);
        Assert.Empty(inventory.Left);
        Assert.Equal(2, inventory.Tools.Count);
    }

    [Fact]
    public void ClaudeCodeCanLiveSomewhereElse()
    {
        var moved = Path.Combine(_fx.Home, "elsewhere");
        _fx.Write("CLAUDE.md", "Moved instructions.", moved);
        _fx.Skill("skills/relocated", "relocated", "Use when moved.");
        File.Move(Path.Combine(_fx.Home, "skills", "relocated", "SKILL.md"), Path.Combine(Directory.CreateDirectory(Path.Combine(moved, "skills", "relocated")).FullName, "SKILL.md"));
        _fx.Write(".claude.json", """{"mcpServers":{"moved":{}}}""", moved);

        var inventory = ExternalScanner.Scan(new ExternalLocations(_fx.Home, moved), _fx.Dsh);
        Assert.Contains(inventory.Items, i => i.Name == "relocated");
        Assert.Single(inventory.Items, i => i.Kind == ExternalKind.Instructions);
        Assert.Contains(inventory.Left, n => n.Text.Contains("moved", StringComparison.Ordinal));
        Assert.Equal(moved, inventory.Tools.Single(t => t.Tool == ExternalTool.ClaudeCode).Folder);
    }

    [Fact]
    public void APluginTurnedOffInClaudeCodeIsNotTicked()
    {
        _fx.Populate();
        _fx.Write(".claude/settings.json", """{"enabledPlugins":{"toolbox@acme":false}}""");
        var lint = ProfileFixture.Item(_fx.Scan(), "lint");
        Assert.False(lint.SelectedByDefault);
        Assert.Contains("turned off", lint.Reason);
    }

    [Fact]
    public void WithoutAPluginListTheCacheIsUsed()
    {
        _fx.Populate();
        _fx.Write(".claude/plugins/installed_plugins.json", "{ this is not json");
        _fx.Write(".claude/settings.json", "also broken");
        var inventory = _fx.Scan();
        Assert.Contains(inventory.Items, i => i.Name == "lint" && i.Group == "Plugin: toolbox");
        Assert.DoesNotContain(inventory.Items, i => i.Name == "nope");
    }

    [Fact]
    public void AVersionOneInstallListIsRead()
    {
        _fx.Populate();
        var install = Path.Combine(_fx.Home, ".claude", "plugins", "cache", "acme", "toolbox", "1.2.0");
        _fx.Write(".claude/plugins/installed_plugins.json",
            "{\"version\":1,\"plugins\":{\"toolbox@acme\":{\"version\":\"1.2.0\",\"installPath\":" + ProfileFixture.Json(install) + "}}}");
        Assert.Contains(_fx.Scan().Items, i => i.Name == "lint");
    }

    [Fact]
    public void AManifestCannotPointOutsideItsPlugin()
    {
        _fx.Populate();
        _fx.Skill("outside/skills/stolen", "stolen", "Use when stealing.");
        _fx.Write(".claude/plugins/cache/acme/toolbox/1.2.0/.claude-plugin/plugin.json",
            """{"name":"toolbox","skills":["../../../../../outside/skills","/etc","skills"]}""");
        Assert.DoesNotContain(_fx.Scan().Items, i => i.Name == "stolen");
        Assert.Contains(_fx.Scan().Items, i => i.Name == "lint");
    }

    [Fact]
    public void ABrokenManifestPathDoesNotStopTheScan()
    {
        _fx.Populate();
        // A NUL can't be in any path; the plugin's other content and every other plugin must still show.
        _fx.Write(".claude/plugins/cache/acme/toolbox/1.2.0/.claude-plugin/plugin.json",
            "{\"name\":\"toolbox\",\"skills\":[\"bad\\u0000path\",\"skills\"],\"commands\":\"???\"}");
        var inventory = _fx.Scan();
        Assert.Contains(inventory.Items, i => i.Name == "lint");
        Assert.Contains(inventory.Items, i => i.Name == "deploy");
    }

    [Fact]
    public void AClaudeFolderThatIsReallyAFileIsJustNotFound()
    {
        _fx.Populate();
        var inventory = ExternalScanner.Scan(new ExternalLocations(_fx.Home, Path.Combine(_fx.Home, ".claude", "settings.json")), _fx.Dsh);
        Assert.False(inventory.Tools.Single(t => t.Tool == ExternalTool.ClaudeCode).Found);
        Assert.Empty(inventory.Items.Where(i => i.Tool == ExternalTool.ClaudeCode));
        Assert.Contains(inventory.Items, i => i.Tool == ExternalTool.Cursor); // the other tool is unaffected
    }

    [Fact]
    public void AManifestCanNameItsOwnFolders()
    {
        _fx.Populate();
        _fx.Skill(".claude/plugins/cache/acme/toolbox/1.2.0/extras/deep", "deep", "Use when deep.");
        _fx.Write(".claude/plugins/cache/acme/toolbox/1.2.0/.claude-plugin/plugin.json", """{"name":"toolbox","skills":"./extras"}""");
        Assert.Contains(_fx.Scan().Items, i => i.Name == "deep" && i.Group == "Plugin: toolbox");
    }

    // MARK: - Bringing them in

    [Fact]
    public void ImportingTheSuggestionsCopiesEachThingToWhereDSHUsesIt()
    {
        var app = _fx.Populate();
        var inventory = _fx.Scan();
        var result = _fx.ImportSuggested(inventory);

        Assert.Empty(result.Skipped);
        Assert.Empty(result.Drafts);
        Assert.Equal(inventory.Suggested, result.Imported.Count);
        Assert.Equal(16, result.Imported.Count);

        var skills = _fx.Dsh.UserSkills;
        Assert.True(File.Exists(Path.Combine(skills, "pdf-tools", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(skills, "pdf-tools", "scripts", "extract.py")), "bundled files come along");
        Assert.True(File.Exists(Path.Combine(skills, "pdf-tools", "reference.md")));
        Assert.True(File.Exists(Path.Combine(skills, "deploy", "references", "guide.md")));
        Assert.False(Directory.Exists(Path.Combine(skills, "create-rule")), "Cursor's own skills stay behind unless asked for");
        Assert.False(File.Exists(Path.Combine(skills, "deploy", "scripts", "validate.sh")), "a plugin's scripts are not part of the skill");

        var live = SkillCatalog.Load(null, _fx.Dsh, SkillSources.None).ToDictionary(s => s.Name);
        Assert.False(live["release"].ModelInvocable, "a command is something you run yourself");
        Assert.Equal("<version>", live["release"].ArgumentHint);
        Assert.Equal(["src/**"], live["style"].Globs);
        Assert.True(live["always"].AlwaysApply);
        Assert.All(live.Values, s => Assert.Equal(SkillOrigin.Dsh, s.Origin));
        Assert.Contains("git-commit", live.Keys);

        Assert.Equal("# About me\nI prefer tabs.\n", File.ReadAllText(Path.Combine(_fx.Dsh.UserInstructions, "claude-code.md")).Replace("\r\n", "\n"));

        var notes = ProjectContext.ProjectNotesFolder(app, _fx.Dsh);
        Assert.True(File.Exists(Path.Combine(notes, "MEMORY.md")));
        Assert.True(File.Exists(Path.Combine(notes, "user_role.md")));
        Assert.True(File.Exists(Path.Combine(notes, "feedback", "testing.md")));
        Assert.False(File.Exists(Path.Combine(notes, "session-1.jsonl")), "conversations are not notes");
    }

    [Fact]
    public void ASubagentBecomesASkillThatHandsTheWorkToTheAgentTool()
    {
        _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());

        var skill = SkillCatalog.Load(null, _fx.Dsh, SkillSources.None).Single(s => s.Name == "code-reviewer");
        Assert.True(skill.ModelInvocable, "its description says when to hand work over");
        Assert.Equal("Expert reviewer. Use proactively after code changes.", skill.Description);
        var text = File.ReadAllText(skill.Path);
        Assert.Contains("`agent` tool", text);
        Assert.Contains("You review code for bugs.", text);
        Assert.Contains("Read, Grep, Glob", text);

        var readOnly = File.ReadAllText(Path.Combine(_fx.Dsh.UserSkills, "qa", "SKILL.md"));
        Assert.Contains("read-only in Cursor", readOnly);
    }

    [Fact]
    public void ImportingIsRepeatableAndChangesNothingTheSecondTime()
    {
        _fx.Populate();
        var first = _fx.ImportSuggested(_fx.Scan());
        Assert.Equal(16, first.Imported.Count);

        var again = _fx.Scan();
        Assert.All(again.Items.Where(i => i.Name is not ("create-rule" or "old-app")), i => Assert.Equal(ExternalStatus.Imported, i.Status));
        Assert.Equal(0, again.Suggested);

        var second = ExternalImporter.Import(again, again.Items.Select(i => i.Id), _fx.Dsh);
        Assert.Equal(["create-rule", "old-app"], second.Imported.Select(i => i.Item.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(16, second.Skipped.Count);
        Assert.All(second.Skipped, s => Assert.Equal("Already in DSH.", s.Reason));
        Assert.False(Directory.Exists(Path.Combine(_fx.Dsh.UserSkills, "review-2")), "no duplicate copies");
    }

    [Fact]
    public void ASkillThatChangedIsOfferedAsAnExtraCopyNotAnOverwrite()
    {
        _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        _fx.Skill(".claude/skills/review", "review", "Use when reviewing code.", "Review the diff. Then summarise.");

        var changed = ProfileFixture.Item(_fx.Scan(), "review");
        Assert.Equal(ExternalStatus.Different, changed.Status);
        Assert.False(changed.SelectedByDefault);
        Assert.Contains("keeps both", changed.Reason);

        var result = ExternalImporter.Import(_fx.Scan(), [changed.Id], _fx.Dsh);
        var imported = Assert.Single(result.Imported);
        Assert.True(imported.Renamed);
        Assert.EndsWith("review-2", ProfileFixture.Slashes(imported.Destination), StringComparison.Ordinal);
        Assert.Contains("Review the diff.\n", File.ReadAllText(Path.Combine(_fx.Dsh.UserSkills, "review", "SKILL.md")).Replace("\r\n", "\n"));
        Assert.Contains("Then summarise.", File.ReadAllText(Path.Combine(_fx.Dsh.UserSkills, "review-2", "SKILL.md")));

        // Now both are there, and the source matches one of them.
        Assert.Equal(ExternalStatus.Imported, ProfileFixture.Item(_fx.Scan(), "review").Status);
    }

    [Fact]
    public void ChangedInstructionsReplaceTheCopyAndKeepABackup()
    {
        _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        _fx.Write(".claude/CLAUDE.md", "# About me\nI now prefer spaces.\n");

        var item = _fx.Scan().Items.Single(i => i.Kind == ExternalKind.Instructions);
        Assert.Equal(ExternalStatus.Different, item.Status);
        Assert.False(item.SelectedByDefault); // it may be the copy in DSH that was edited

        ExternalImporter.Import(_fx.Scan(), [item.Id], _fx.Dsh);
        var folder = _fx.Dsh.UserInstructions;
        Assert.Contains("spaces", File.ReadAllText(Path.Combine(folder, "claude-code.md")));
        Assert.Contains("tabs", File.ReadAllText(Assert.Single(Directory.GetFiles(folder, "claude-code.md*.bak"))));
        Assert.Equal(["claude-code.md"], ProjectContext.UserInstructionFiles(_fx.Dsh).Select(f => f.Label)); // the backup is never loaded
    }

    [Fact]
    public void NotesThatGrewAreOfferedAsAnUpdateAndKeepWhatWasThere()
    {
        var app = _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        var key = ClaudeProjectNames.Encode(app);
        _fx.Write($".claude/projects/{key}/memory/new_note.md", "Deploys happen on Fridays.");

        var item = _fx.Scan().Items.Single(i => i.Kind == ExternalKind.ProjectNotes && i.ProjectPath is not null);
        Assert.Equal(ExternalStatus.Different, item.Status);
        Assert.False(item.SelectedByDefault); // an update is the user's call: it could replace a note edited in DSH

        ExternalImporter.Import(_fx.Scan(), [item.Id], _fx.Dsh);
        var notes = ProjectContext.ProjectNotesFolder(app, _fx.Dsh);
        Assert.True(File.Exists(Path.Combine(notes, "new_note.md")));
        Assert.True(File.Exists(Path.Combine(notes, "user_role.md")));
        Assert.Equal(ExternalStatus.Imported, _fx.Scan().Items.Single(i => i.Kind == ExternalKind.ProjectNotes && i.ProjectPath is not null).Status);
    }

    [Fact]
    public void NotesWithoutAnIndexGetOne()
    {
        var app = _fx.Project("bare");
        var key = ClaudeProjectNames.Encode(app);
        _fx.Write($".claude/projects/{key}/memory/tabs.md", "---\nname: Tabs\ndescription: Indent with tabs\n---\nTabs.");
        _fx.Write($".claude/projects/{key}/memory/plain.md", "Just a plain note about deploys.");

        var inventory = _fx.Scan();
        ExternalImporter.Import(inventory, inventory.Items.Select(i => i.Id), _fx.Dsh);
        var index = File.ReadAllText(Path.Combine(ProjectContext.ProjectNotesFolder(app, _fx.Dsh), "MEMORY.md"));
        Assert.Contains("[Tabs](tabs.md) — Indent with tabs", index);
        Assert.Contains("[plain](plain.md) — Just a plain note about deploys.", index);
    }

    [Fact]
    public void ReviewFirstHoldsSkillsBackAsDraftsButNotYourInstructionsOrNotes()
    {
        var app = _fx.Populate();
        var result = _fx.ImportSuggested(_fx.Scan(), asDrafts: true);

        Assert.Equal(14, result.Drafts.Count); // 5 skills + 4 commands + 2 rules + 3 subagents
        Assert.Equal(2, result.Imported.Count(i => !i.IsDraft)); // your instructions and the notes are written straight away
        Assert.Equal(14, result.Imported.Count(i => i.IsDraft));
        Assert.All(result.Imported.Where(i => !i.IsDraft), i => Assert.True(i.Item.Kind is ExternalKind.Instructions or ExternalKind.ProjectNotes));
        Assert.Equal(SkillDrafts.List(_fx.Dsh).Count, result.Drafts.Count);
        Assert.False(Directory.Exists(_fx.Dsh.UserSkills), "nothing is active until approved");
        Assert.True(File.Exists(Path.Combine(_fx.Dsh.UserInstructions, "claude-code.md")));
        Assert.True(Directory.Exists(ProjectContext.ProjectNotesFolder(app, _fx.Dsh)));
        Assert.All(result.Drafts, d => Assert.Equal("import", d.Source));
        Assert.Contains(result.Drafts, d => d.Note!.Contains("Claude Code", StringComparison.Ordinal));
    }

    [Fact]
    public void ASkillCopiedIntoBothToolsIsOneImport()
    {
        _fx.Skill(".claude/skills/shared", "shared", "Use when sharing.");
        _fx.Skill(".cursor/skills/shared", "shared", "Use when sharing.");

        var inventory = _fx.Scan();
        var copies = inventory.Items.Where(i => i.Name == "shared").ToList();
        Assert.Equal(2, copies.Count);
        var (claude, cursor) = (copies.Single(i => i.Tool == ExternalTool.ClaudeCode), copies.Single(i => i.Tool == ExternalTool.Cursor));
        Assert.True(claude.SelectedByDefault);
        Assert.False(cursor.SelectedByDefault);
        Assert.Contains("Same as the copy from Claude Code", cursor.Reason);

        var result = ExternalImporter.Import(inventory, [claude.Id, cursor.Id], _fx.Dsh);
        Assert.Single(result.Imported);
        Assert.Equal("Already in DSH.", Assert.Single(result.Skipped).Reason);
        Assert.False(Directory.Exists(Path.Combine(_fx.Dsh.UserSkills, "shared-2")));
    }

    [Fact]
    public void ImportedSkillsShadowTheOnesDSHWasReadingInPlace()
    {
        _fx.Populate();
        _fx.ImportSuggested(_fx.Scan());
        var all = SkillCatalog.LoadAll(null, _fx.Dsh);
        var review = all.Where(s => s.Name == "review").ToList();
        Assert.Equal(2, review.Count);
        Assert.Equal(SkillOrigin.Dsh, review[0].Origin);
        Assert.False(review[0].Shadowed);
        Assert.True(review[1].Shadowed, "the original is still read in place, but yields to your copy");
    }

    // MARK: - Safety

    [Fact]
    public void SecretsAreFlaggedAndNotTickedForYou()
    {
        _fx.Write(".claude/CLAUDE.md", "Use this key: sk-abcdefghijklmnopqrstuvwxyz0123456789\n");
        _fx.Skill(".claude/skills/clean", "clean", "Use when clean.");
        var app = _fx.Project("leaky");
        var key = ClaudeProjectNames.Encode(app);
        _fx.Write($".claude/projects/{key}/memory/api.md", "token ghp_abcdefghijklmnopqrstuvwxyz0123456789 works");

        var inventory = _fx.Scan();
        var instructions = inventory.Items.Single(i => i.Kind == ExternalKind.Instructions);
        Assert.Contains(instructions.Warnings, w => w.Contains("key or token", StringComparison.Ordinal));
        Assert.False(instructions.SelectedByDefault);
        Assert.Contains("key or token", instructions.Reason);
        var notes = inventory.Items.Single(i => i.Kind == ExternalKind.ProjectNotes);
        Assert.False(notes.SelectedByDefault);
        Assert.True(ProfileFixture.Item(inventory, "clean").SelectedByDefault);

        // Asking for it explicitly still works: it is the user's own text.
        var result = ExternalImporter.Import(inventory, [instructions.Id], _fx.Dsh);
        Assert.Single(result.Imported);
    }

    [Fact]
    public void LinksInsideAPluginAreNeverReadAndAreReported()
    {
        _fx.Populate();
        var secret = _fx.Write("secret.md", "---\nname: stolen\ndescription: Use when stealing.\n---\nTOP SECRET");
        var plugin = Path.Combine(_fx.Home, ".claude", "plugins", "cache", "acme", "toolbox", "1.2.0");
        Directory.CreateDirectory(Path.Combine(plugin, "skills", "evil"));
        try
        {
            File.CreateSymbolicLink(Path.Combine(plugin, "skills", "evil", "SKILL.md"), secret);
            File.CreateSymbolicLink(Path.Combine(plugin, "commands", "leak.md"), secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // no symlink rights (Windows without developer mode)
        }

        var inventory = _fx.Scan();
        Assert.DoesNotContain(inventory.Items, i => i.Name is "stolen" or "leak" or "evil");
        Assert.Contains(inventory.Left, n => n.Text.Contains("to somewhere else", StringComparison.Ordinal));
    }

    [Fact]
    public void LinksInsideASkillAreNotCopied()
    {
        var secret = _fx.Write("secret.txt", "TOP SECRET");
        _fx.Skill(".claude/skills/tidy", "tidy", "Use when tidy.");
        try
        {
            File.CreateSymbolicLink(Path.Combine(_fx.Home, ".claude", "skills", "tidy", "leak.txt"), secret);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }
        _fx.ImportSuggested(_fx.Scan());
        Assert.True(File.Exists(Path.Combine(_fx.Dsh.UserSkills, "tidy", "SKILL.md")));
        Assert.False(File.Exists(Path.Combine(_fx.Dsh.UserSkills, "tidy", "leak.txt")));
    }

    [Fact]
    public void OneOversizedSkillIsSkippedAndTheRestStillImport()
    {
        _fx.Skill(".claude/skills/huge", "huge", "Use when huge.");
        File.WriteAllBytes(Path.Combine(_fx.Home, ".claude", "skills", "huge", "blob.bin"), new byte[SkillFiles.MaxFileBytes + 1]);
        _fx.Skill(".claude/skills/small", "small", "Use when small.");

        var result = _fx.ImportSuggested(_fx.Scan());
        Assert.Equal(["small"], result.Imported.Select(i => i.Item.Name).ToArray());
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("huge", skipped.Item.Name);
        Assert.Contains("MB", skipped.Reason);
        Assert.False(Path.Exists(Path.Combine(_fx.Dsh.UserSkills, "huge")), "nothing half-copied");
    }

    [Fact]
    public void CorruptConfigFilesDoNotStopTheScan()
    {
        _fx.Populate();
        _fx.Write(".claude.json", "not json at all");
        _fx.Write(".cursor/mcp.json", "{");
        var inventory = _fx.Scan();
        Assert.NotEmpty(inventory.Items);
        Assert.DoesNotContain(inventory.Left, n => n.Text.Contains("MCP server", StringComparison.Ordinal) && !n.Text.Contains("hooks", StringComparison.Ordinal));
    }

    [Fact]
    public void SelectingNothingWritesNothing()
    {
        _fx.Populate();
        var result = ExternalImporter.Import(_fx.Scan(), [], _fx.Dsh);
        Assert.Empty(result.Imported);
        Assert.Empty(result.Skipped);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_fx.Support));
    }

    [Fact]
    public void TheOtherToolsFilesAreLeftExactlyAsTheyWere()
    {
        _fx.Populate();
        string Snapshot() => string.Join("\n", Directory.EnumerateFiles(_fx.Home, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal).Select(f => $"{f}:{new FileInfo(f).Length}:{File.GetLastWriteTimeUtc(f):O}"));
        var before = Snapshot();
        var inventory = _fx.Scan();
        _fx.ImportSuggested(inventory);
        ExternalImporter.Import(inventory, inventory.Items.Select(i => i.Id), _fx.Dsh);
        Assert.Equal(before, Snapshot());
    }

    // MARK: - Saying it in words

    [Fact]
    public void ItemsAreSummarisedInPlainWords()
    {
        _fx.Populate();
        var inventory = _fx.Scan();
        Assert.Equal("6 skills, 4 commands, 2 rules, your instructions, 3 subagents and notes for 2 projects",
            ExternalLabels.Summarize(inventory.Items));
        Assert.Equal("5 skills, 4 commands, 2 rules, your instructions, 3 subagents and notes for 1 project",
            ExternalLabels.Summarize(inventory.Items.Where(i => i.SelectedByDefault)));
        Assert.Equal("1 skill", ExternalLabels.Summarize(inventory.Items.Where(i => i.Name == "review")));
        Assert.Equal("your instructions and notes for 1 project",
            ExternalLabels.Summarize(inventory.Items.Where(i => i.Kind is ExternalKind.Instructions).Concat(inventory.Items.Where(i => i.ProjectPath is not null))));
        Assert.Equal("nothing", ExternalLabels.Summarize([]));
    }

    // MARK: - The converter behind the preview

    [Fact]
    public void ThePreviewTextIsWhatTheImportWrites()
    {
        _fx.Populate();
        foreach (var skill in SkillCatalog.LoadAll(null, _fx.Dsh).Where(s => !s.Shadowed))
        {
            var slug = SkillNaming.Slug(skill.Name);
            var staged = Path.Combine(_fx.Work, "staged-" + slug);
            SkillConverter.WriteSkillFolder(skill, staged, slug);
            Assert.Equal(SkillConverter.SkillFileText(skill, slug), File.ReadAllText(Path.Combine(staged, "SKILL.md")));
        }
    }
}
