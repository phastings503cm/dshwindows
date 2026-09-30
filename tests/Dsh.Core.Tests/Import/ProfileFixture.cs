using System.Text.Json;

namespace Dsh.Core.Tests;

/// <summary>A pretend PC: a home folder laid out the way Claude Code and Cursor lay theirs out (the
/// plugin cache with its version folder, the encoded per-project folders, installed_plugins.json),
/// beside a scratch DSH data folder and a "work" folder for projects. Nothing here touches the real
/// ~/.claude, ~/.cursor or %APPDATA%.</summary>
internal sealed class ProfileFixture : IDisposable
{
    private readonly TempDirectory _temp = new("dsh-profile");

    public ProfileFixture()
    {
        Home = Path.Combine(_temp.Path, "home");
        Support = Path.Combine(_temp.Path, "support");
        Work = Path.Combine(_temp.Path, "work");
        foreach (var dir in new[] { Home, Support, Work }) Directory.CreateDirectory(dir);
    }

    public string Home { get; }
    public string Support { get; }
    public string Work { get; }
    public ExternalLocations External => new(Home);
    public SkillLocations Dsh => new(Home, Support);

    public void Dispose() => _temp.Dispose();

    /// <summary>Write <paramref name="text"/> at <paramref name="relative"/> ("/"-separated) under the
    /// home folder (or <paramref name="root"/>).</summary>
    public string Write(string relative, string text, string? root = null)
    {
        var path = Path.Combine([root ?? Home, .. relative.Split('/')]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public string Skill(string relative, string name, string description, string body = "Do the thing.") =>
        Write($"{relative}/SKILL.md", $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");

    public string Project(string name) => Directory.CreateDirectory(Path.Combine(Work, name)).FullName;

    public ExternalInventory Scan() => ExternalScanner.Scan(External, Dsh);

    public ExternalImportResult ImportSuggested(ExternalInventory inventory, bool asDrafts = false) =>
        ExternalImporter.Import(inventory, inventory.Items.Where(i => i.SelectedByDefault).Select(i => i.Id), Dsh, asDrafts);

    public static string Json(string value) => JsonSerializer.Serialize(value);

    public static string Slashes(string path) => path.Replace('\\', '/');

    public static ExternalItem Item(ExternalInventory inventory, string name, ExternalKind? kind = null, ExternalTool? tool = null) =>
        inventory.Items.Single(i => i.Name == name && (kind is null || i.Kind == kind) && (tool is null || i.Tool == tool));

    /// <summary>What a long-time user of both tools has: sixteen items worth bringing in, one that
    /// isn't (Cursor's built-in skill), notes for a project that's gone, and a plugin that isn't
    /// installed.</summary>
    public string Populate()
    {
        // MARK: Claude Code
        Write(".claude/CLAUDE.md", "# About me\nI prefer tabs.\n");
        Skill(".claude/skills/pdf-tools", "pdf-tools", "Use when working with PDFs.");
        Write(".claude/skills/pdf-tools/scripts/extract.py", "print('hi')");
        Write(".claude/skills/pdf-tools/reference.md", "Reference");
        Skill(".claude/skills/review", "review", "Use when reviewing code.", "Review the diff.");
        Write(".claude/commands/release.md", "---\ndescription: Cut a release\nargument-hint: <version>\n---\nRelease $ARGUMENTS");
        Write(".claude/commands/git/commit.md", "Write a commit message.");
        Write(".claude/rules/style.md", "---\npaths:\n  - \"src/**\"\n---\nUse tabs in src.");
        Write(".claude/rules/always.md", "Always be kind.");
        Write(".claude/agents/code-reviewer.md",
            "---\nname: code-reviewer\ndescription: Expert reviewer. Use proactively after code changes.\ntools: Read, Grep, Glob\nmodel: sonnet\n---\nYou review code for bugs.\nBe brief.");

        // An installed plugin (version 2 of installed_plugins.json), and a marketplace clone holding a
        // plugin that is NOT installed.
        const string plugin = ".claude/plugins/cache/acme/toolbox/1.2.0";
        Write($"{plugin}/.claude-plugin/plugin.json", """{"name":"toolbox","description":"Handy things","version":"1.2.0"}""");
        Skill($"{plugin}/skills/lint", "lint", "Use when linting.", "Run ${CLAUDE_PLUGIN_ROOT}/bin/lint.sh");
        Write($"{plugin}/commands/fmt.md", "Format everything.");
        Write($"{plugin}/agents/helper.md", "---\nname: helper\ndescription: Helps out.\n---\nHelp.");
        Write($"{plugin}/hooks/hooks.json", "{}");
        Write($"{plugin}/.mcp.json", "{}");
        Write(".claude/plugins/installed_plugins.json",
            "{\"version\":2,\"plugins\":{\"toolbox@acme\":[{\"scope\":\"user\",\"installPath\":" + Json(Path.Combine(Home, ".claude", "plugins", "cache", "acme", "toolbox", "1.2.0")) + ",\"version\":\"1.2.0\"}]}}");
        Skill(".claude/plugins/marketplaces/acme/plugins/unused/skills/nope", "nope", "Use never.");

        Write(".claude/settings.json",
            """{"enabledPlugins":{"toolbox@acme":true},"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[{"type":"command","command":"echo"}]}]},"permissions":{"allow":["Bash(ls:*)"]}}""");
        Write(".claude.json",
            """{"mcpServers":{"github":{"command":"npx","env":{"GITHUB_TOKEN":"ghp_abcdefghijklmnopqrstuvwxyz0123456789"}},"postgres":{"command":"psql"}}}""");

        // Saved notes: one project that exists here, one that doesn't, one with nothing in it.
        var app = Project("my-app");
        var key = ClaudeProjectNames.Encode(app);
        Write($".claude/projects/{key}/memory/MEMORY.md", "- [Role](user_role.md) — who the user is\n- [Testing](feedback/testing.md) — how to test\n");
        Write($".claude/projects/{key}/memory/user_role.md", "---\nname: role\ndescription: who the user is\ntype: user\n---\nA data scientist.");
        Write($".claude/projects/{key}/memory/feedback/testing.md", "Integration tests must hit a real database.");
        Write($".claude/projects/{key}/session-1.jsonl", "{}\n");
        Write(".claude/projects/-Users-gone-old-app/memory/note.md", "About a project that is no longer here.");
        Directory.CreateDirectory(Path.Combine(Home, ".claude", "projects", "-Users-empty", "memory"));

        // MARK: Cursor
        Skill(".cursor/skills/api-design", "api-design", "Use when designing an API.");
        Skill(".cursor/skills-cursor/create-rule", "create-rule", "Use when creating a Cursor rule.");
        Write(".cursor/commands/ship.md", "Ship the current branch.");
        Write(".cursor/agents/qa.md", "---\nname: qa\ndescription: Checks the work. Use after big changes.\nreadonly: true\n---\nVerify the change.");
        Write(".cursor/mcp.json", """{"mcpServers":{"figma":{"url":"https://example.invalid/mcp"}}}""");
        const string cursorPlugin = ".cursor/plugins/cache/cursor-public/deploy-on-aws/7a17df718d26f07414b876e77a7480fa25089b08";
        Write($"{cursorPlugin}/.claude-plugin/plugin.json", """{"name":"deploy-on-aws","description":"Deploy to AWS"}""");
        Skill($"{cursorPlugin}/skills/deploy", "deploy", "Use when deploying to AWS.", "Validate with ${CLAUDE_PLUGIN_ROOT}/scripts/validate.sh first.");
        Write($"{cursorPlugin}/skills/deploy/references/guide.md", "Guide");
        Write($"{cursorPlugin}/hooks/hooks.json", "{}");
        Write($"{cursorPlugin}/.mcp.json", "{}");
        Write($"{cursorPlugin}/scripts/validate.sh", "#!/bin/sh\n");
        return app;
    }
}
