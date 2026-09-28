using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>A throwaway project + fake home + fake app-support, so nothing reads the developer's real
/// ~/.claude, ~/.cursor or skills.</summary>
internal sealed class SkillFixture : IDisposable
{
    private readonly TempDirectory _temp = new("dsh-skills");

    public SkillFixture()
    {
        Project = System.IO.Path.Combine(Base, "project");
        Home = System.IO.Path.Combine(Base, "home");
        Support = System.IO.Path.Combine(Base, "support");
        foreach (var dir in new[] { Project, Home, Support }) Directory.CreateDirectory(dir);
    }

    public string Base => _temp.Path;
    public string Project { get; }
    public string Home { get; }
    public string Support { get; }
    public SkillLocations Locations => new(Home, Support);

    public void Dispose() => _temp.Dispose();

    /// <summary>Write <paramref name="text"/> at <paramref name="relative"/> ("/"-separated) under
    /// <paramref name="root"/> (the project by default).</summary>
    public string Write(string relative, string text, string? root = null)
    {
        var path = System.IO.Path.Combine([root ?? Project, .. relative.Split('/')]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string Skill(string relative, string name, string description, string body = "Do the thing.", string? root = null,
                        string extra = "") =>
        Write($"{relative}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n{extra}---\n\n{body}\n", root);

    /// <summary>A context for executing tools against the fixture's project.</summary>
    public ToolContext Context(PermissionPreset preset = PermissionPreset.FullAccess) => new()
    {
        Workspace = Project,
        Policy = new PermissionPolicy(preset, Project),
        Client = new FakeSkillClient(),
        Registry = new ToolRegistry([]),
    };

    /// <summary>"/" separators, so path assertions read the same on Windows.</summary>
    public static string Slashes(string path) => path.Replace('\\', '/');
}

/// <summary>An LLM client that plays back canned replies (streamed in small chunks) and records every
/// request.</summary>
internal sealed class FakeSkillClient(params string[] replies) : ILlmClient
{
    private readonly Queue<string> _replies = new(replies);
    public List<LlmRequest> Requests { get; } = [];

    public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(LlmRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var text = _replies.Count > 0 ? _replies.Dequeue() : "";
        await Task.Yield();
        for (var i = 0; i < text.Length; i += 16)
            yield return new LlmStreamEvent.Text(text.Substring(i, Math.Min(16, text.Length - i)));
        yield return new LlmStreamEvent.Done([], "stop", null);
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(["fake"]);
}

// MARK: - Frontmatter

public sealed class SkillDocumentTests
{
    [Fact]
    public void FoldedAndLiteralBlockScalars()
    {
        var doc = SkillDocument.Parse("""
            ---
            name: folded
            description: >
              Use when the build
              fails on CI.

              Second paragraph.
            notes: |
              line one
              line two
            ---
            # Body
            """);
        Assert.Equal("folded", doc["name"]);
        Assert.Equal("Use when the build fails on CI.\nSecond paragraph.", doc["description"]);
        Assert.Equal("line one\nline two", doc["notes"]);
        Assert.Equal("# Body", doc.Body);
    }

    [Fact]
    public void ListsInEveryShapeCursorAndClaudeWrite()
    {
        var doc = SkillDocument.Parse("""
            ---
            description: Canon bible
            globs:
              - "docs/lore/**"
              - "server/data/content/dialogue/**"
            allowed-tools: Read, Grep, Bash(git *)
            paths: [src/**/*.ts, "test/**"]
            also: "*.{ts,tsx},*.md"
            alwaysApply: false
            environments:
              - local
            ---
            """);
        Assert.Equal(new[] { "docs/lore/**", "server/data/content/dialogue/**" }, doc.List("globs"));
        Assert.Equal(new[] { "Read", "Grep", "Bash(git *)" }, doc.List("allowed-tools"));
        Assert.Equal(new[] { "src/**/*.ts", "test/**" }, doc.List("paths"));
        Assert.Equal(new[] { "*.{ts,tsx}", "*.md" }, doc.List("also")); // braces keep their commas
        Assert.False(doc.Bool("alwaysApply"));
        Assert.Equal(new[] { "local" }, doc.List("environments"));
    }

    [Fact]
    public void QuotingCommentsMultilinePlainAndOddInput()
    {
        var doc = SkillDocument.Parse("\uFEFF---\r\nname: x\r\ndescription: \"has: colon and \\\"quotes\\\"\"\r\nplain: hello # a comment\r\nwrapped: first\r\n  second\r\n---\r\nbody\r\n");
        Assert.Equal("has: colon and \"quotes\"", doc["description"]);
        Assert.Equal("hello", doc["plain"]);
        Assert.Equal("first second", doc["wrapped"]);
        Assert.Equal("body\n", doc.Body);
        Assert.False(SkillDocument.Parse("no frontmatter").HadFrontmatter);
        Assert.False(SkillDocument.Parse("---\nname: x\nnever closed").HadFrontmatter); // unterminated is not frontmatter
        Assert.Empty(SkillDocument.Parse("---\n---\nbody").Fields);
    }

    [Fact]
    public void RenderRoundTripsAndKeepsOrder()
    {
        var doc = new SkillDocument("Body text.\n", hadFrontmatter: true);
        doc["name"] = "my-skill";
        doc["description"] = "Use when: colons, \"quotes\" and # hashes appear";
        doc.SetList("globs", ["a/**", "*.{ts,tsx}"]);
        doc["alwaysApply"] = "true";
        var text = doc.Render();
        Assert.StartsWith("---\nname: my-skill\ndescription:", text);
        var back = SkillDocument.Parse(text);
        Assert.Equal(doc["description"], back["description"]);
        Assert.Equal(new[] { "a/**", "*.{ts,tsx}" }, back.List("globs"));
        Assert.True(back.Bool("alwaysApply"));
        Assert.Equal(new[] { "name", "description", "globs", "alwaysApply" }, back.Order);
        Assert.Equal("Body text.\n", back.Body);
    }

    [Fact]
    public void RealWorldClaudeSkillWithLongDescription()
    {
        var longText = string.Concat(Enumerable.Repeat("Use when JUDGING a Meshy asset — a body concept, a raw mesh. ", 5));
        var doc = SkillDocument.Parse($"---\nname: meshy-critique\ndescription: {longText}\n---\n\n# Critiquing\n");
        Assert.Equal(longText.Trim(' '), doc["description"]);
    }
}

// MARK: - Naming / arguments

public sealed class SkillNamingTests
{
    [Fact]
    public void SlugAndValidity()
    {
        Assert.Equal("run-tests", SkillNaming.Slug("Run  Tests!"));
        Assert.Equal("godot-debugging", SkillNaming.Slug("  --Godot_Debugging--  "));
        Assert.Equal("frontend-component", SkillNaming.Slug("frontend:component"));
        Assert.Equal("", SkillNaming.Slug("日本語"));
        Assert.Equal(64, SkillNaming.Slug(new string('a', 90)).Length);
        Assert.True(SkillNaming.IsValidName("godot-debugging"));
        Assert.False(SkillNaming.IsValidName("Godot Debugging"));
        Assert.False(SkillNaming.IsValidName("-x"));
        Assert.Equal("x-3", SkillNaming.Unique("x", n => n is "x" or "x-2"));
    }

    [Fact]
    public void ArgumentExpansion()
    {
        Assert.Equal(new[] { "one", "two words", "three four", "five" }, SkillArguments.Split("one \"two words\" 'three four' five"));
        Assert.Equal("Fix the bug now", SkillArguments.Expand("Fix $ARGUMENTS now", "the bug"));
        Assert.Equal("a=x b=y c= d=$10", SkillArguments.Expand("a=$1 b=$2 c=$3 d=$10", "x y")); // only $1-$9 are positional
        Assert.Equal("No placeholders.\n\nARGUMENTS: extra", SkillArguments.Expand("No placeholders.", "extra"));
        Assert.Equal("No placeholders.", SkillArguments.Expand("No placeholders.", ""));
    }
}

// MARK: - Discovery

public sealed class SkillCatalogTests : IDisposable
{
    private readonly SkillFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    private void Populate()
    {
        _fx.Skill(".dsh/skills/mine", "mine", "Use when DSH-owned.");
        _fx.Skill(".agents/skills/shared", "shared", "Use when agents-standard.");
        _fx.Skill(".claude/skills/claudey", "claudey", "Use when Claude.", extra: "allowed-tools: Read, Grep\nargument-hint: <file>\n");
        _fx.Skill(".claude/skills/private-flow", "private-flow", "User-only flow.", extra: "disable-model-invocation: true\n");
        _fx.Write(".claude/commands/deploy.md", "---\ndescription: Ship it\nargument-hint: <env>\n---\nDeploy to $ARGUMENTS");
        _fx.Write(".claude/commands/frontend/component.md", "Make a component named $1");
        _fx.Write(".claude/rules/style.md", "Use tabs.");
        _fx.Write(".claude/rules/tests.md", "---\npaths:\n  - \"**/*.test.ts\"\n---\nWrite tests first.");
        _fx.Write(".cursor/rules/lore.mdc", "---\ndescription: Canon bible for narrative work\nglobs:\n  - \"docs/lore/**\"\nalwaysApply: false\n---\nCanon rules.");
        _fx.Write(".cursor/rules/always.mdc", "---\ndescription: House rules\nalwaysApply: true\n---\nBe kind.");
        _fx.Write(".cursor/rules/manual.mdc", "---\nalwaysApply: false\n---\nOnly when asked.");
        _fx.Skill(".cursor/skills/cursory", "cursory", "Use when Cursor.");
        _fx.Skill(".claude/skills/dupe", "dupe", "claude copy");
        _fx.Skill(".dsh/skills/dupe", "dupe", "dsh copy");
        _fx.Skill(".claude/skills", "x", "y", root: _fx.Home); // ~/.claude/skills/SKILL.md is not a skill dir
        _fx.Skill(".claude/skills/homey", "homey", "Use when at home.", root: _fx.Home);
        _fx.Skill(".cursor/skills/homecursor", "homecursor", "Cursor home.", root: _fx.Home);
        _fx.Skill("skills/usery", "usery", "Use when user-wide.", root: _fx.Support);
        _fx.Skill("skills-builtin/godot-debugging", "godot-debugging", "Use when Godot.", root: _fx.Support);
        _fx.Skill(".dsh/skills/_drafts/nope", "nope", "hidden");
    }

    [Fact]
    public void DiscoversEveryLayout()
    {
        Populate();
        var all = SkillCatalog.LoadAll(_fx.Project, _fx.Locations);
        var byName = all.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var name in new[] { "mine", "shared", "claudey", "private-flow", "deploy", "frontend:component", "style", "tests",
                                     "lore", "always", "manual", "cursory", "homey", "homecursor", "usery", "godot-debugging" })
        {
            Assert.Contains(name, byName.Keys);
        }
        Assert.DoesNotContain("nope", byName.Keys); // underscore folders are not skills

        Skill S(string n) => byName[n][0];
        Assert.Equal(SkillOrigin.Dsh, S("mine").Origin);
        Assert.Equal(SkillScope.Project, S("mine").Scope);
        Assert.Equal(SkillOrigin.Claude, S("claudey").Origin);
        Assert.Equal(new[] { "Read", "Grep" }, S("claudey").AllowedTools);
        Assert.Equal("<file>", S("claudey").ArgumentHint);
        Assert.False(S("private-flow").ModelInvocable);
        Assert.Equal(SkillKind.Command, S("deploy").Kind);
        Assert.False(S("deploy").ModelInvocable);
        Assert.Equal("<env>", S("deploy").ArgumentHint);
        Assert.Equal(SkillKind.Command, S("frontend:component").Kind);
        Assert.True(S("style").AlwaysApply, "a Claude rule without paths always loads");
        Assert.False(S("tests").AlwaysApply);
        Assert.Equal(new[] { "**/*.test.ts" }, S("tests").Globs);
        Assert.Equal(new[] { "docs/lore/**" }, S("lore").Globs);
        Assert.Equal(SkillOrigin.Cursor, S("lore").Origin);
        Assert.Equal(SkillKind.Rule, S("lore").Kind);
        Assert.True(S("always").AlwaysApply);
        Assert.False(S("manual").ModelInvocable, "a description-less, glob-less Cursor rule is manual");
        Assert.Equal(SkillScope.User, S("homey").Scope);
        Assert.Equal(SkillOrigin.Claude, S("homey").Origin);
        Assert.Equal(SkillOrigin.Dsh, S("usery").Origin);
        Assert.Equal(SkillScope.User, S("usery").Scope);
        Assert.Equal(SkillOrigin.Builtin, S("godot-debugging").Origin);
    }

    [Fact]
    public void PrecedenceShadowingAndLoad()
    {
        Populate();
        var all = SkillCatalog.LoadAll(_fx.Project, _fx.Locations);
        var dupes = all.Where(s => s.Name == "dupe").ToList();
        Assert.Equal(2, dupes.Count);
        Assert.Equal("dsh copy", dupes.First(s => !s.Shadowed).Description); // .dsh beats .claude
        Assert.Equal(SkillOrigin.Claude, dupes.First(s => s.Shadowed).Origin);
        Assert.Single(SkillCatalog.Load(_fx.Project, _fx.Locations), s => s.Name == "dupe");
    }

    [Fact]
    public void SourcesCanBeSwitchedOff()
    {
        Populate();
        var names = SkillCatalog.Load(_fx.Project, _fx.Locations, SkillSources.Agents).Select(s => s.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "mine", "shared", "usery", "godot-debugging" }, names);
        Assert.Empty(names.Intersect(["claudey", "deploy", "style", "lore", "cursory", "homey"]));
    }

    [Fact]
    public void NoProjectStillFindsUserAndBuiltinSkills()
    {
        Populate();
        var names = SkillCatalog.Load(null, _fx.Locations).Select(s => s.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { "usery", "homey", "godot-debugging" }, names);
        Assert.DoesNotContain("mine", names);
    }
}

// MARK: - Prompt + use_skill

public sealed class SkillPromptTests : IDisposable
{
    private readonly SkillFixture _fx = new();
    private readonly IReadOnlyList<Skill> _skills;

    public SkillPromptTests()
    {
        _fx.Skill(".dsh/skills/alpha", "alpha", "Use when doing alpha work.", body: "ALPHA STEPS");
        _fx.Skill(".dsh/skills/beta", "beta", "Use when doing beta work.", body: "BETA $ARGUMENTS");
        _fx.Write(".dsh/skills/beta/scripts/run.sh", "echo hi");
        _fx.Write(".claude/commands/ship.md", "SHIP $1");
        _fx.Write(".cursor/rules/always.mdc", "---\ndescription: House\nalwaysApply: true\n---\nALWAYS BE TESTING");
        _fx.Write(".cursor/rules/glob.mdc", "---\ndescription: Lore\nglobs: [\"docs/**\"]\n---\nLORE BODY");
        _skills = SkillCatalog.Load(_fx.Project, _fx.Locations);
    }

    public void Dispose() => _fx.Dispose();

    private string Id(string name) => _skills.First(s => s.Name == name).Id;

    [Fact]
    public void AlwaysRulesAndCatalogAreSeparatedFromPinned()
    {
        var r = SkillPrompt.Build(_skills, new SkillSelection());
        Assert.Contains("Rules that always apply", r.Text);
        Assert.Contains("ALWAYS BE TESTING", r.Text);
        Assert.Contains("- alpha: Use when doing alpha work.", r.Text);
        Assert.Contains("- glob: Lore (files: docs/**)", r.Text);
        Assert.DoesNotContain("ALPHA STEPS", r.Text); // the catalog lists descriptions, not bodies
        Assert.DoesNotContain("- ship:", r.Text); // commands are user-invoked, not offered to the model
        Assert.DoesNotContain("- always:", r.Text); // always-on rules are injected, not listed
        Assert.Contains("use_skill", r.Text);
    }

    [Fact]
    public void PinnedSkillIsInjectedInFullWithItsDirectory()
    {
        var r = SkillPrompt.Build(_skills, new SkillSelection { Pinned = new HashSet<string> { Id("alpha"), Id("ship") } });
        Assert.Contains("Skills the user selected for this chat", r.Text);
        Assert.Contains("ALPHA STEPS", r.Text);
        Assert.Contains("SHIP", r.Text); // a user can pin a command
        Assert.Contains(_skills.First(s => s.Name == "alpha").Directory, r.Text);
        Assert.DoesNotContain("- alpha:", r.Text); // pinned skills leave the catalog
        Assert.Equal(new[] { "alpha", "always", "ship" }, r.Injected.Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ManualModeHidesTheCatalogButKeepsPinned()
    {
        var r = SkillPrompt.Build(_skills, new SkillSelection { Pinned = new HashSet<string> { Id("beta") }, Auto = false });
        Assert.DoesNotContain("Available skills", r.Text);
        Assert.Contains("BETA", r.Text);
        Assert.Contains("ALWAYS BE TESTING", r.Text); // always-on rules are not optional
    }

    [Fact]
    public void DisabledSkillsVanishEverywhere()
    {
        var r = SkillPrompt.Build(_skills, new SkillSelection
        {
            Pinned = new HashSet<string> { Id("alpha") },
            Disabled = new HashSet<string> { Id("alpha"), Id("always") },
        });
        Assert.DoesNotContain("alpha", r.Text);
        Assert.DoesNotContain("ALWAYS BE TESTING", r.Text);
    }

    [Fact]
    public void OversizedSkillIsCappedNotDropped()
    {
        _fx.Skill(".dsh/skills/huge", "huge", "Use when huge.", body: new string('x', 100_000));
        var all = SkillCatalog.Load(_fx.Project, _fx.Locations);
        var huge = all.First(s => s.Name == "huge");
        var r = SkillPrompt.Build(all, new SkillSelection { Pinned = new HashSet<string> { huge.Id } });
        Assert.Contains("[… truncated", r.Text);
        Assert.True(r.Text.Length < SkillPrompt.TotalCap + 20_000);
    }

    [Fact]
    public async Task UseSkillLoadsBodyArgumentsAndBundledFiles()
    {
        var tool = new UseSkillTool(_skills);
        Assert.Equal("use_skill", tool.Name);
        Assert.Equal("use_skill", tool.Spec.Name);
        var ctx = _fx.Context();
        var ok = await tool.ExecuteAsync("""{"name":"beta","arguments":"the widget"}""", ctx, CancellationToken.None);
        Assert.Contains("# Skill: beta (DSH skill, project)", ok.Output);
        Assert.Contains("BETA the widget", ok.Output);
        Assert.Contains("- scripts/run.sh", ok.Output);
        Assert.Contains("Base directory:", ok.Output);

        var slashForm = await tool.ExecuteAsync("""{"name":"/Alpha"}""", ctx, CancellationToken.None);
        Assert.Contains("ALPHA STEPS", slashForm.Output); // case and leading slash are forgiven

        var unknown = await tool.ExecuteAsync("""{"name":"nope"}""", ctx, CancellationToken.None);
        Assert.StartsWith("Error:", unknown.Output);
        Assert.Contains("alpha, beta", unknown.Output);

        var command = await tool.ExecuteAsync("""{"name":"ship"}""", ctx, CancellationToken.None);
        Assert.Contains("user runs themselves", command.Output);
    }
}

// MARK: - Drafts, approval, management

public sealed class SkillDraftTests : IDisposable
{
    private readonly SkillFixture _fx = new();

    public void Dispose() => _fx.Dispose();

    [Fact]
    public async Task ProposeCreatesAnInactiveDraftThatApprovalActivates()
    {
        var tool = new ProposeSkillTool(_fx.Project, _fx.Locations);
        Assert.Equal("propose_skill", tool.Spec.Name);
        var result = await tool.ExecuteAsync(
            """{"name":"Godot Export Android","description":"Use when exporting a Godot project to Android.","instructions":"1. Install the export templates.\n2. Run the export command.\n3. Verify the APK installs."}""",
            _fx.Context(PermissionPreset.WorkspaceWrite), CancellationToken.None);
        Assert.Contains("NOT active", result.Output);
        Assert.Contains("Settings › Skills", result.Output);

        Assert.Empty(SkillCatalog.Load(_fx.Project, _fx.Locations)); // a draft is not a skill
        var drafts = SkillDrafts.List(_fx.Locations);
        var draft = Assert.Single(drafts);
        Assert.Equal("godot-export-android", draft.Name);
        Assert.Equal("agent", draft.Source);
        Assert.Equal(SkillScope.Project, draft.Scope);
        Assert.Equal("Proposed by the agent", draft.SourceLabel);

        var landed = SkillDrafts.Approve(draft, _fx.Project, locations: _fx.Locations);
        Assert.EndsWith(".dsh/skills/godot-export-android/SKILL.md", SkillFixture.Slashes(landed));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(landed)!, "draft.json")));
        Assert.Empty(SkillDrafts.List(_fx.Locations));
        var live = SkillCatalog.Load(_fx.Project, _fx.Locations);
        Assert.Equal(new[] { "godot-export-android" }, live.Select(s => s.Name));
        Assert.Equal(SkillOrigin.Dsh, live[0].Origin);
    }

    [Fact]
    public async Task ProposeValidatesAndRejectsJunk()
    {
        var tool = new ProposeSkillTool(_fx.Project, _fx.Locations);
        foreach (var bad in new[]
                 {
                     """{"name":"","description":"d","instructions":"long enough instructions here"}""",
                     """{"name":"x","description":"","instructions":"long enough instructions here"}""",
                     """{"name":"x","description":"d","instructions":"short"}""",
                 })
        {
            var r = await tool.ExecuteAsync(bad, _fx.Context(PermissionPreset.WorkspaceWrite), CancellationToken.None);
            Assert.StartsWith("Error:", r.Output);
        }
        Assert.Empty(SkillDrafts.List(_fx.Locations));
    }

    [Fact]
    public void UserScopeApprovalAndConflictPolicies()
    {
        const string text = "---\nname: shared-tool\ndescription: Use when sharing.\n---\n\nSteps.";
        var first = SkillDrafts.Create(text, SkillScope.User, null, "ai", locations: _fx.Locations);
        var landed = SkillDrafts.Approve(first, null, locations: _fx.Locations);
        Assert.Contains("support/skills/shared-tool", SkillFixture.Slashes(landed));

        var second = SkillDrafts.Create(text, SkillScope.User, null, "ai", locations: _fx.Locations);
        Assert.Equal("shared-tool", second.Id); // drafts are separate from active skills
        var error = Assert.Throws<SkillException>(() => SkillDrafts.Approve(second, null, ConflictPolicy.Fail, _fx.Locations));
        Assert.Contains("already exists", error.Message);
        Assert.Equal(SkillErrorKind.Exists, error.Kind);
        Assert.Single(SkillDrafts.List(_fx.Locations)); // a failed approval keeps the draft
        var renamed = SkillDrafts.Approve(second, null, ConflictPolicy.Rename, _fx.Locations);
        Assert.Contains("shared-tool-2", renamed);
    }

    [Fact]
    public void ProjectDraftNeedsAProjectAndRetargetWorks()
    {
        var d = SkillDrafts.Create("---\nname: p\ndescription: Use when p.\n---\nBody text here.", SkillScope.Project, null, "ai",
                                   locations: _fx.Locations);
        Assert.Throws<SkillException>(() => SkillDrafts.Approve(d, null, locations: _fx.Locations));
        SkillDrafts.Retarget(d, SkillScope.User, null);
        var moved = SkillDrafts.List(_fx.Locations)[0];
        Assert.Equal(SkillScope.User, moved.Scope);
        SkillDrafts.Approve(moved, null, locations: _fx.Locations);
    }

    [Fact]
    public void EditingAndRejectingDrafts()
    {
        var d = SkillDrafts.Create("---\nname: e\ndescription: Use when e.\n---\nBody text here.", SkillScope.User, null, "ai",
                                   locations: _fx.Locations);
        SkillDrafts.Update(d, "---\nname: Edited Name\ndescription: Use when edited.\n---\nNew body.");
        Assert.Equal("edited-name", SkillDrafts.List(_fx.Locations)[0].Name);
        Assert.Throws<SkillException>(() => SkillDrafts.Update(d, "no frontmatter and no name"));
        SkillDrafts.Reject(SkillDrafts.List(_fx.Locations)[0]);
        Assert.Empty(SkillDrafts.List(_fx.Locations));
    }

    [Fact]
    public void NormalizeRepairsAndRefuses()
    {
        var (slug, text) = SkillDrafts.Normalize("Just instructions, no header.", fallbackName: "Quick Fix", fallbackDescription: "Use when quick.");
        Assert.Equal("quick-fix", slug);
        Assert.StartsWith("---\nname: quick-fix\ndescription: Use when quick.\n---", text);
        Assert.Throws<SkillException>(() => SkillDrafts.Normalize("---\ndescription: d\n---\nbody")); // no name
        Assert.Throws<SkillException>(() => SkillDrafts.Normalize("---\nname: n\ndescription: d\n---\n   \n")); // no instructions
        // No description: the body's first line stands in.
        Assert.Contains("description: First line of the body.", SkillDrafts.Normalize("---\nname: n\n---\nFirst line of the body.").Text);
    }

    [Fact]
    public void DraftMetadataIsCamelCaseIso8601AndToleratesDamage()
    {
        var d = SkillDrafts.Create("---\nname: meta\ndescription: Use when checking metadata.\n---\nBody text here.", SkillScope.Project,
                                   _fx.Project, "chat", note: "from a test", locations: _fx.Locations);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(d.Directory, "draft.json")))!.AsObject();
        Assert.Equal("project", (string?)json["scope"]);
        Assert.Equal(_fx.Project, (string?)json["projectPath"]);
        Assert.Equal("chat", (string?)json["source"]);
        Assert.Equal("from a test", (string?)json["note"]);
        Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$", (string?)json["createdAt"]);
        Assert.True(DateTimeOffset.UtcNow - d.CreatedAt < TimeSpan.FromMinutes(5));
        Assert.Equal("Made from a chat", d.SourceLabel);

        // A user draft has no project and no note: those keys are left out.
        var u = SkillDrafts.Create("---\nname: meta-user\ndescription: Use when checking metadata.\n---\nBody text here.", SkillScope.User,
                                   null, "ai", locations: _fx.Locations);
        var userJson = JsonNode.Parse(File.ReadAllText(Path.Combine(u.Directory, "draft.json")))!.AsObject();
        Assert.Equal("user", (string?)userJson["scope"]);
        Assert.False(userJson.ContainsKey("projectPath"));
        Assert.False(userJson.ContainsKey("note"));

        // Unreadable metadata falls back to an imported user draft, as on the Mac.
        File.WriteAllText(Path.Combine(d.Directory, "draft.json"), """{"scope":"galaxy","source":"ai"}""");
        var damaged = SkillDrafts.List(_fx.Locations).Single(x => x.Id == d.Id);
        Assert.Equal(SkillScope.User, damaged.Scope);
        Assert.Equal("import", damaged.Source);
        Assert.Equal(DateTimeOffset.MinValue, damaged.CreatedAt);
        Assert.Equal("Imported", damaged.SourceLabel);
        Assert.Equal("Custom", (damaged with { Source = "custom" }).SourceLabel);
    }

    [Fact]
    public void ManagerCreateAdoptAndReadOnlyForForeignFiles()
    {
        // Adopt a Cursor rule: it becomes a DSH skill and keeps its globs / alwaysApply.
        _fx.Write(".cursor/rules/style.mdc", "---\ndescription: Style guide\nglobs: [\"src/**\"]\nalwaysApply: true\n---\nUse tabs.");
        _fx.Write(".claude/commands/ship.md", "---\ndescription: Ship\nargument-hint: <env>\n---\nShip $ARGUMENTS");
        var all = SkillCatalog.Load(_fx.Project, _fx.Locations);
        var rule = all.First(s => s.Name == "style");
        Assert.Throws<SkillException>(() => SkillManager.Write(rule, "x")); // foreign files are edited only after adopting
        var adopted = SkillManager.Adopt(rule, SkillScope.Project, _fx.Project, locations: _fx.Locations);
        var copy = SkillDocument.Parse(File.ReadAllText(adopted));
        Assert.Equal("style", copy["name"]);
        Assert.Equal(new[] { "src/**" }, copy.List("globs"));
        Assert.True(copy.Bool("alwaysApply"));
        Assert.Equal("Use tabs.", copy.Body.Trim());

        var cmd = SkillManager.Adopt(all.First(s => s.Name == "ship"), SkillScope.User, null, locations: _fx.Locations);
        var cmdDoc = SkillDocument.Parse(File.ReadAllText(cmd));
        Assert.True(cmdDoc.Bool("disable-model-invocation")); // a command stays user-only
        Assert.Equal("<env>", cmdDoc["argument-hint"]);

        // The adopted DSH copy now shadows the Cursor original and is editable.
        var after = SkillCatalog.LoadAll(_fx.Project, _fx.Locations).Where(s => s.Name == "style").ToList();
        var active = after.First(s => !s.Shadowed);
        Assert.Equal(SkillOrigin.Dsh, active.Origin);
        SkillManager.Write(active, "---\nname: style\ndescription: Style guide v2\n---\nx");
        Assert.Contains("Style guide v2", SkillManager.Read(active));

        // Creating a skill by hand.
        var made = SkillManager.Create(SkillManager.Scaffold("My Workflow", "Use when testing."), SkillScope.User, null,
                                       locations: _fx.Locations);
        Assert.EndsWith("skills/my-workflow/SKILL.md", SkillFixture.Slashes(made));
        Assert.Throws<SkillException>(() => SkillManager.Create(SkillManager.Scaffold("My Workflow", "Use when testing."),
                                                                SkillScope.User, null, locations: _fx.Locations));
        var noProject = Assert.Throws<SkillException>(() => SkillManager.Base(SkillScope.Project, null, _fx.Locations));
        Assert.Equal(SkillErrorKind.Invalid, noProject.Kind);
    }

    [Fact]
    public void TrashUsesTheRecycleBinWhenSetAndDeletesOtherwise()
    {
        _fx.Skill(".dsh/skills/keep", "keep", "Use when trashing.");
        _fx.Skill(".dsh/skills/gone", "gone", "Use when trashing.");
        var skills = SkillCatalog.Load(_fx.Project, _fx.Locations);
        var recycled = new List<string>();
        SkillManager.RecycleBin = path =>
        {
            recycled.Add(path);
            return true;
        };
        try
        {
            SkillManager.Trash(skills.First(s => s.Name == "keep"));
            Assert.Equal(new[] { Path.Combine(_fx.Project, ".dsh", "skills", "keep") }, recycled);
            SkillManager.RecycleBin = _ => false;
            Assert.Throws<SkillException>(() => SkillManager.Trash(skills.First(s => s.Name == "gone")));
        }
        finally
        {
            SkillManager.RecycleBin = null;
        }
        Assert.True(Directory.Exists(Path.Combine(_fx.Project, ".dsh", "skills", "gone")));
        SkillManager.Trash(skills.First(s => s.Name == "gone"));
        Assert.False(Directory.Exists(Path.Combine(_fx.Project, ".dsh", "skills", "gone")));
        var missing = Assert.Throws<SkillException>(() => SkillManager.Trash(skills.First(s => s.Name == "gone")));
        Assert.Equal(SkillErrorKind.NotFound, missing.Kind);
    }
}

// MARK: - Lint

public sealed class SkillLintTests
{
    [Fact]
    public void FlagsMissingPiecesAndSecrets()
    {
        Assert.Contains(SkillLint.Check("just text"), i => i.Severity == SkillIssueSeverity.Error);
        Assert.Contains(SkillLint.Check("---\nname: x\n---\nbody body body"),
                        i => i.Message.Contains("description", StringComparison.Ordinal) && i.Severity == SkillIssueSeverity.Error);
        Assert.Contains(SkillLint.Check("---\nname: Bad Name\ndescription: Use when testing lint rules.\n---\nbody"),
                        i => i.Message.Contains("lowercase-hyphen", StringComparison.Ordinal));
        var secret = SkillLint.Check("---\nname: s\ndescription: Use when deploying to production.\n---\nexport KEY=sk-abcdefghijklmnopqrstuvwxyz123456");
        Assert.Contains(secret, i => i.Message.Contains("secret", StringComparison.Ordinal));
        var good = SkillLint.Check("---\nname: good-skill\ndescription: Use when you need to lint a Swift package before a release.\n---\n\n# Steps\n1. Run it.\n");
        Assert.DoesNotContain(good, i => i.Severity >= SkillIssueSeverity.Warning);
        var vague = SkillLint.Check("---\nname: v\ndescription: Deployment things and stuff here\n---\nbody");
        Assert.Contains(vague, i => i.Severity == SkillIssueSeverity.Info);
    }

    [Fact]
    public void IssuesAreSortedMostSevereFirst()
    {
        var issues = SkillLint.Check("---\nname: Bad Name\ndescription: tiny\n---\n");
        Assert.True(issues.Count >= 3);
        Assert.Equal(issues.OrderByDescending(i => i.Severity).Select(i => i.Id), issues.Select(i => i.Id));
        Assert.Equal(SkillIssueSeverity.Error, issues[0].Severity);
        Assert.Equal($"2:{issues[0].Message}", issues[0].Id);
    }
}

// MARK: - Generator

public sealed class SkillGeneratorTests
{
    [Fact]
    public void ExtractHandlesFencesThinkingAndChatter()
    {
        const string file = "---\nname: a\ndescription: Use when a.\n---\nBody";
        Assert.Equal(file, SkillGenerator.Extract(file));
        Assert.Equal(file, SkillGenerator.Extract($"```markdown\n{file}\n```"));
        Assert.Equal(file, SkillGenerator.Extract($"let me think</think>\n{file}"));
        Assert.Equal(file, SkillGenerator.Extract($"Sure! Here's the skill:\n{file}"));
    }

    [Fact]
    public async Task GenerateNormalizesAndRetriesOnInvalidOutput()
    {
        const string bad = "I can't do that, sorry.";
        const string good = "```markdown\n---\nname: Release Notes\ndescription: Use when writing release notes from git history.\n---\n\n# Release notes\n1. Run git log.\n2. Group by type.\n```";
        var client = new FakeSkillClient(bad, good);
        var output = await SkillGenerator.GenerateAsync(client, "m",
            new SkillGenerationRequest("write release notes", existingNames: ["deploy"]));
        Assert.Equal("release-notes", output.Name);
        Assert.Equal("Use when writing release notes from git history.", output.Description);
        Assert.StartsWith("---\nname: release-notes\n", output.Text);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("rejected", client.Requests[1].Messages[0].Content);
        Assert.Equal(ThinkingLevel.Low, client.Requests[0].Thinking); // generation must not inherit a slow max-effort default
        Assert.Contains("deploy", client.Requests[0].Messages[0].Content); // existing names are passed along
        Assert.Equal(SkillGenerator.SystemPrompt, client.Requests[0].SystemPrompt);
        Assert.Equal(6_000, client.Requests[0].MaxTokens);
        Assert.Equal("m", client.Requests[0].Model);
        Assert.Empty(client.Requests[0].Tools);
    }

    [Fact]
    public async Task GenerateGivesUpAfterTwoBadReplies()
    {
        var client = new FakeSkillClient("nope", "still nope");
        await Assert.ThrowsAsync<SkillException>(() =>
            SkillGenerator.GenerateAsync(client, "m", new SkillGenerationRequest("x")));
        Assert.Equal(2, client.Requests.Count);
    }

    [Fact]
    public void DigestKeepsTheRecentPartAndSkipsImages()
    {
        var messages = new List<LlmMessage>();
        for (var i = 0; i < 400; i++)
        {
            messages.Add(LlmMessage.User($"question {i} " + new string('x', 200)));
            messages.Add(LlmMessage.Assistant($"answer {i}"));
        }
        messages.Add(new LlmMessage(MessageRole.User, "[image]") { ImageSource = "screenshot" });
        var d = SkillGenerator.Digest(messages, limit: 3_000);
        Assert.Contains("answer 399", d);
        Assert.DoesNotContain("answer 0", d);
        Assert.DoesNotContain("[image]", d);
        Assert.True(d.Length < 4_500);
        Assert.Contains("<conversation>", SkillGenerator.UserPrompt(new SkillGenerationRequest("g", conversation: [LlmMessage.User("hi")])));
    }

    [Fact]
    public void DigestShowsToolCallsAndResults()
    {
        var messages = new List<LlmMessage>
        {
            LlmMessage.SystemText("system prompt"),
            LlmMessage.User("build it"),
            LlmMessage.Assistant("Running the build.", [new ToolCall("1", "run_shell_command", """{"command":"make"}""")]),
            LlmMessage.ToolOutput("1", "run_shell_command", "ok"),
        };
        var d = SkillGenerator.Digest(messages);
        Assert.Equal("User: build it\nAssistant: Running the build.\n[ran run_shell_command {\"command\":\"make\"}]\n  → ok", d);
        Assert.DoesNotContain("system prompt", d);
        Assert.Equal("abc …", SkillGenerator.Clip("  abcdef  ", 3));
    }
}
