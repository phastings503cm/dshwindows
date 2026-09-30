using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Skills
//
// A skill is packaged instructions the agent can load when a task matches: a folder with a SKILL.md
// (frontmatter name + description, then Markdown) and optional bundled files. The format is shared
// by Claude Code, Cursor and the open Agent Skills convention, so this app reads all of their
// layouts in place and can import/export between them:
//
//   DSH        .dsh\skills\<n>\SKILL.md        %APPDATA%\DSH\skills
//   Agents     .agents\skills\<n>\SKILL.md     ~\.agents\skills
//   Qwen Code  .qwen\skills\<n>\SKILL.md
//   Claude     .claude\skills\<n>\SKILL.md     ~\.claude\skills
//              .claude\commands\<n>.md         ~\.claude\commands      (slash commands)
//              .claude\rules\*.md              ~\.claude\rules         (always-on / path-scoped)
//   Cursor     .cursor\skills\<n>\SKILL.md     ~\.cursor\skills
//              .cursor\rules\*.mdc             (alwaysApply / globs / description)
//   Built-in   shipped with the app, installed under %APPDATA%\DSH\skills-builtin

public enum SkillOrigin { Dsh, Agents, Qwen, Claude, Cursor, Builtin }

public enum SkillScope { Project, User }

public enum SkillKind
{
    /// <summary>A SKILL.md folder.</summary>
    Skill,
    /// <summary>A Cursor .mdc / Claude .claude\rules file.</summary>
    Rule,
    /// <summary>A Claude .claude\commands\*.md slash command.</summary>
    Command,
}

public static class SkillEnums
{
    public static string Label(this SkillOrigin origin) => origin switch
    {
        SkillOrigin.Dsh => "DSH",
        SkillOrigin.Agents => "Agents",
        SkillOrigin.Qwen => "Qwen",
        SkillOrigin.Claude => "Claude",
        SkillOrigin.Cursor => "Cursor",
        _ => "Built-in",
    };

    /// <summary>Whether the app owns (and may freely edit/delete) skills from here.</summary>
    public static bool IsOwned(this SkillOrigin origin) => origin == SkillOrigin.Dsh;

    public static string Label(this SkillScope scope) => scope == SkillScope.Project ? "Project" : "User";

    public static string Label(this SkillKind kind) => kind switch
    {
        SkillKind.Skill => "Skill",
        SkillKind.Rule => "Rule",
        _ => "Command",
    };

    public static string RawValue(this SkillScope scope) => scope == SkillScope.Project ? "project" : "user";
}

/// <summary>Which foreign layouts to read in place. DSH's own and the built-ins are always read.</summary>
[Flags]
public enum SkillSources
{
    None = 0,
    Agents = 1 << 0,
    Qwen = 1 << 1,
    Claude = 1 << 2,
    Cursor = 1 << 3,
    All = Agents | Qwen | Claude | Cursor,
}

/// <summary>A discovered skill, rule or command.</summary>
public sealed record Skill
{
    /// <summary>The file that holds it: SKILL.md, an .mdc rule, or a command .md.</summary>
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>Lower sorts first; matches the precedence of the root it came from.</summary>
    public int Rank { get; init; }
    public SkillOrigin Origin { get; init; } = SkillOrigin.Dsh;
    public SkillScope Scope { get; init; } = SkillScope.Project;
    public SkillKind Kind { get; init; } = SkillKind.Skill;
    /// <summary>File globs the skill applies to (Cursor globs, Claude rule paths).</summary>
    public IReadOnlyList<string> Globs { get; init; } = [];
    /// <summary>Always part of the prompt (Cursor alwaysApply, a Claude rule without paths).</summary>
    public bool AlwaysApply { get; init; }
    public string? ArgumentHint { get; init; }
    public IReadOnlyList<string> AllowedTools { get; init; } = [];
    /// <summary>False when the author disabled automatic use (disable-model-invocation) or it's a
    /// command / manual-only rule: it then runs only when the user picks it or types /name.</summary>
    public bool ModelInvocable { get; init; } = true;
    /// <summary>False for user-invocable: false (background knowledge only).</summary>
    public bool UserInvocable { get; init; } = true;
    /// <summary>A skill with the same name from a higher-precedence root hides this one.</summary>
    public bool Shadowed { get; init; }

    /// <summary>Stable identity: the file path.</summary>
    public string Id => Path;
    public string Slug => SkillNaming.Slug(Name);
    /// <summary>Folder holding bundled files (scripts, references).</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? Path;
    /// <summary>Whether removing/editing it touches only files this app created.</summary>
    public bool IsOwned => Origin.IsOwned();

    /// <summary>The parsed file.</summary>
    public SkillDocument? Document()
    {
        try
        {
            return File.Exists(Path) ? SkillDocument.Parse(File.ReadAllText(Path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Files bundled next to a SKILL.md, relative to its folder with "/" separators (skills only).</summary>
    public IReadOnlyList<string> Resources(int limit = 60)
    {
        if (Kind != SkillKind.Skill || !System.IO.Directory.Exists(Directory)) return [];
        var output = new List<string>();
        void Visit(string dir, string prefix)
        {
            foreach (var entry in FileWalk.Entries(dir).OrderBy(e => e.Name, StringComparer.Ordinal))
            {
                if (output.Count >= limit) return;
                var rel = prefix.Length == 0 ? entry.Name : prefix + "/" + entry.Name;
                if (entry.IsDirectory)
                {
                    if (!entry.IsLink) Visit(entry.FullPath, rel);
                }
                else if (!rel.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add(rel);
                }
            }
        }
        Visit(Directory, "");
        output.Sort(StringComparer.Ordinal);
        return output;
    }
}

// MARK: - Naming

public static partial class SkillNaming
{
    /// <summary>Lowercase ASCII words joined by hyphens, at most 64 characters — the shape every
    /// tool's skill name must take.</summary>
    public static string Slug(string s)
    {
        var output = new System.Text.StringBuilder();
        var lastDash = false;
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsAscii(ch) && char.IsLetterOrDigit(ch))
            {
                output.Append(ch);
                lastDash = false;
            }
            else if (!lastDash && output.Length > 0)
            {
                output.Append('-');
                lastDash = true;
            }
        }
        var trimmed = output.Length > 64 ? output.ToString(0, 64) : output.ToString();
        return trimmed.TrimEnd('-');
    }

    public static bool IsValidName(string s) => s.Length is > 0 and <= 64 && ValidName().IsMatch(s);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex ValidName();

    /// <summary>base, or base-2, base-3 … until <paramref name="taken"/> no longer contains it.</summary>
    public static string Unique(string baseName, Func<string, bool> taken)
    {
        var root = baseName.Length == 0 ? "skill" : baseName;
        if (!taken(root)) return root;
        var n = 2;
        while (taken($"{root}-{n}")) n++;
        return $"{root}-{n}";
    }

    public static string FirstParagraph(string text, int limit = 300)
    {
        foreach (var line in text.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith('#') && t != "---" && !t.StartsWith("```", StringComparison.Ordinal))
                return TextUtil.Prefix(t, limit);
        }
        return "";
    }
}

// MARK: - Where skills live

public sealed record SkillLocations(string Home, string AppSupport)
{
    public static SkillLocations Standard =>
        new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppPaths.Root);

    /// <summary>Skills you own, available in every project.</summary>
    public string UserSkills => Path.Combine(AppSupport, "skills");
    /// <summary>Skills shipped with the app, re-installed on launch.</summary>
    public string BuiltinSkills => Path.Combine(AppSupport, "skills-builtin");
    /// <summary>AI-generated and imported skills waiting for approval.</summary>
    public string Drafts => Path.Combine(AppSupport, "skill-drafts");
    /// <summary>Your own instruction files (*.md): loaded into every prompt, in every project. Imported
    /// from Claude Code's ~/.claude/CLAUDE.md, or written by hand.</summary>
    public string UserInstructions => Path.Combine(AppSupport, "instructions");
    /// <summary>Notes saved for one project, kept outside the project folder so they never land in a
    /// repository: one folder per project, named the way Claude Code names it
    /// (<see cref="ClaudeProjectNames.Encode"/>), each with a MEMORY.md index and the notes it lists.</summary>
    public string ProjectNotes => Path.Combine(AppSupport, "project-notes");

    public static string ProjectSkills(string project) => Path.Combine(project, ".dsh", "skills");
}

// MARK: - Discovery

public enum SkillRootLayout { SkillDirs, RuleFiles, CommandFiles }

public sealed record SkillRoot(string Path, int Rank, SkillOrigin Origin, SkillScope Scope, SkillRootLayout Layout,
                               bool ClaudeRuleStyle = false);

public static class SkillCatalog
{
    private static string Join(string a, string relative) =>
        Path.Combine([a, .. relative.Split('/')]);

    /// <summary>Every root to scan, in precedence order.</summary>
    public static IReadOnlyList<SkillRoot> Roots(string? project, SkillLocations? locations = null,
                                                 SkillSources sources = SkillSources.All)
    {
        locations ??= SkillLocations.Standard;
        var output = new List<SkillRoot>();
        void Add(string path, int rank, SkillOrigin origin, SkillScope scope, SkillRootLayout layout, bool claude = false) =>
            output.Add(new SkillRoot(path, rank, origin, scope, layout, claude));
        if (project is not null)
        {
            Add(Join(project, ".dsh/skills"), 100, SkillOrigin.Dsh, SkillScope.Project, SkillRootLayout.SkillDirs);
            if (sources.HasFlag(SkillSources.Agents)) Add(Join(project, ".agents/skills"), 200, SkillOrigin.Agents, SkillScope.Project, SkillRootLayout.SkillDirs);
            if (sources.HasFlag(SkillSources.Qwen)) Add(Join(project, ".qwen/skills"), 300, SkillOrigin.Qwen, SkillScope.Project, SkillRootLayout.SkillDirs);
            if (sources.HasFlag(SkillSources.Claude))
            {
                Add(Join(project, ".claude/skills"), 350, SkillOrigin.Claude, SkillScope.Project, SkillRootLayout.SkillDirs);
                Add(Join(project, ".claude/commands"), 355, SkillOrigin.Claude, SkillScope.Project, SkillRootLayout.CommandFiles);
                Add(Join(project, ".claude/rules"), 358, SkillOrigin.Claude, SkillScope.Project, SkillRootLayout.RuleFiles, claude: true);
            }
            if (sources.HasFlag(SkillSources.Cursor))
            {
                Add(Join(project, ".cursor/skills"), 360, SkillOrigin.Cursor, SkillScope.Project, SkillRootLayout.SkillDirs);
                Add(Join(project, ".cursor/rules"), 365, SkillOrigin.Cursor, SkillScope.Project, SkillRootLayout.RuleFiles);
            }
        }
        Add(locations.UserSkills, 400, SkillOrigin.Dsh, SkillScope.User, SkillRootLayout.SkillDirs);
        var home = locations.Home;
        if (sources.HasFlag(SkillSources.Agents)) Add(Join(home, ".agents/skills"), 450, SkillOrigin.Agents, SkillScope.User, SkillRootLayout.SkillDirs);
        if (sources.HasFlag(SkillSources.Claude))
        {
            Add(Join(home, ".claude/skills"), 500, SkillOrigin.Claude, SkillScope.User, SkillRootLayout.SkillDirs);
            Add(Join(home, ".claude/commands"), 505, SkillOrigin.Claude, SkillScope.User, SkillRootLayout.CommandFiles);
            Add(Join(home, ".claude/rules"), 508, SkillOrigin.Claude, SkillScope.User, SkillRootLayout.RuleFiles, claude: true);
        }
        if (sources.HasFlag(SkillSources.Cursor)) Add(Join(home, ".cursor/skills"), 520, SkillOrigin.Cursor, SkillScope.User, SkillRootLayout.SkillDirs);
        Add(locations.BuiltinSkills, 900, SkillOrigin.Builtin, SkillScope.User, SkillRootLayout.SkillDirs);
        return output;
    }

    /// <summary>Every skill found, best-precedence first; a later duplicate name is marked Shadowed
    /// rather than dropped so the manager can show it.</summary>
    public static IReadOnlyList<Skill> LoadAll(string? project, SkillLocations? locations = null,
                                               SkillSources sources = SkillSources.All)
    {
        var found = new List<Skill>();
        foreach (var root in Roots(project, locations, sources))
        {
            found.AddRange(root.Layout switch
            {
                SkillRootLayout.SkillDirs => ScanSkillDirs(root),
                SkillRootLayout.RuleFiles => ScanRules(root, root.ClaudeRuleStyle),
                _ => ScanCommands(root),
            });
        }
        found = found.OrderBy(s => s.Rank).ThenBy(s => s.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < found.Count; i++)
        {
            var key = found[i].Slug.Length == 0 ? found[i].Name.ToLowerInvariant() : found[i].Slug;
            if (!seen.Add(key)) found[i] = found[i] with { Shadowed = true };
        }
        return found;
    }

    /// <summary>The skills that apply (not shadowed).</summary>
    public static IReadOnlyList<Skill> Load(string? project, SkillLocations? locations = null,
                                            SkillSources sources = SkillSources.All) =>
        LoadAll(project, locations, sources).Where(s => !s.Shadowed).ToList();

    // MARK: Scanners

    public static IReadOnlyList<Skill> ScanSkillDirs(SkillRoot root)
    {
        if (!Directory.Exists(root.Path)) return [];
        var output = new List<Skill>();
        foreach (var entry in FileWalk.Entries(root.Path).Where(e => e.IsDirectory).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry.Name.StartsWith('_')) continue; // _drafts and friends
            var manifest = Path.Combine(entry.FullPath, "SKILL.md");
            if (!File.Exists(manifest))
            {
                manifest = Path.Combine(entry.FullPath, "skill.md");
                if (!File.Exists(manifest)) continue;
            }
            if (ReadText(manifest) is not { } text) continue;
            output.Add(Build(SkillDocument.Parse(text), manifest, entry.Name, root, SkillKind.Skill));
        }
        return output;
    }

    public static IReadOnlyList<Skill> ScanRules(SkillRoot root, bool claudeStyle)
    {
        if (!Directory.Exists(root.Path)) return [];
        var output = new List<Skill>();
        foreach (var file in FilesUnder(root.Path))
        {
            var ext = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            if (ext is not ("mdc" or "md")) continue;
            var baseName = Path.GetFileNameWithoutExtension(file);
            if (baseName.Equals("readme", StringComparison.OrdinalIgnoreCase)) continue;
            // Folder-style rule: <name>/RULE.md(c) is named after the folder.
            var fallback = baseName.Equals("RULE", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileName(Path.GetDirectoryName(file)) ?? baseName
                : baseName;
            if (ReadText(file) is not { } text) continue;
            output.Add(Build(SkillDocument.Parse(text), file, fallback, root, SkillKind.Rule, claudeStyle));
        }
        return output.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<Skill> ScanCommands(SkillRoot root)
    {
        if (!Directory.Exists(root.Path)) return [];
        var output = new List<Skill>();
        var basePath = Path.GetFullPath(root.Path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in FilesUnder(root.Path))
        {
            if (!Path.GetExtension(file).Equals(".md", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.GetFullPath(file);
            var rel = full.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? full[basePath.Length..] : Path.GetFileName(file);
            rel = rel[..^3]; // ".md"
            var name = rel.Replace(Path.DirectorySeparatorChar, ':').Replace('/', ':');
            if (ReadText(file) is not { } text) continue;
            output.Add(Build(SkillDocument.Parse(text), file, name, root, SkillKind.Command));
        }
        return output.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>Regular files under <paramref name="root"/>, recursively, skipping hidden entries and
    /// never following directory links.</summary>
    internal static IEnumerable<string> FilesUnder(string root)
    {
        foreach (var entry in FileWalk.Entries(root))
        {
            if (entry.IsDirectory)
            {
                if (entry.IsLink) continue;
                foreach (var nested in FilesUnder(entry.FullPath)) yield return nested;
            }
            else
            {
                yield return entry.FullPath;
            }
        }
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>One parsed file → a <see cref="Skill"/>.</summary>
    public static Skill Build(SkillDocument doc, string path, string fallbackName, SkillRoot root, SkillKind kind,
                              bool claudeRuleStyle = false)
    {
        var name = doc["name"]?.Trim() is { Length: > 0 } n ? n : fallbackName;
        var globs = doc.List("globs");
        if (globs.Count == 0) globs = doc.List("paths");
        var description = doc["description"]?.Trim() ?? "";
        if (description.Length == 0) description = doc["when_to_use"] ?? "";
        bool always;
        if (doc.Bool("alwaysApply") is { } explicitAlways) always = explicitAlways;
        else if (kind == SkillKind.Rule && claudeRuleStyle) always = globs.Count == 0; // a Claude rule without paths always loads
        else always = false;
        if (description.Length == 0)
        {
            description = globs.Count == 0
                ? SkillNaming.FirstParagraph(doc.Body)
                : $"Applies to files matching {string.Join(", ", globs)}";
        }
        var invocable = !(doc.Bool("disable-model-invocation") ?? false);
        if (kind == SkillKind.Command) invocable = false;
        // A Cursor rule with no description, no globs and not always-on is "manual": only attached
        // when the user asks for it.
        if (kind == SkillKind.Rule && !always && globs.Count == 0 && string.IsNullOrEmpty(doc["description"])) invocable = false;
        return new Skill
        {
            Path = path,
            Name = name,
            Description = description,
            Rank = root.Rank,
            Origin = root.Origin,
            Scope = root.Scope,
            Kind = kind,
            Globs = globs,
            AlwaysApply = always,
            ArgumentHint = doc["argument-hint"],
            AllowedTools = doc.List("allowed-tools"),
            ModelInvocable = invocable,
            UserInvocable = doc.Bool("user-invocable") ?? true,
        };
    }
}

// MARK: - Arguments

public static partial class SkillArguments
{
    /// <summary>Shell-like split: whitespace separates, quotes group.</summary>
    public static IReadOnlyList<string> Split(string s)
    {
        var output = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var has = false;
        foreach (var ch in s)
        {
            if (quote is { } q)
            {
                if (ch == q) quote = null;
                else current.Append(ch);
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                has = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (has || current.Length > 0)
                {
                    output.Add(current.ToString());
                    current.Clear();
                    has = false;
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (has || current.Length > 0) output.Add(current.ToString());
        return output;
    }

    /// <summary>Fill $ARGUMENTS and $1…$9. A skill that never mentions $ARGUMENTS still gets what the
    /// user typed, appended.</summary>
    public static string Expand(string body, string arguments)
    {
        var trimmed = arguments.Trim();
        var parts = Split(trimmed);
        var usesAll = body.Contains("$ARGUMENTS", StringComparison.Ordinal);
        var output = body.Replace("$ARGUMENTS", trimmed, StringComparison.Ordinal);
        var usedPositional = false;
        output = Positional().Replace(output, m =>
        {
            usedPositional = true;
            var index = m.Groups[1].Value[0] - '0';
            return index - 1 < parts.Count ? parts[index - 1] : "";
        });
        if (trimmed.Length > 0 && !usesAll && !usedPositional) output += $"\n\nARGUMENTS: {trimmed}";
        return output;
    }

    [GeneratedRegex(@"\$([1-9])(?![0-9])")]
    private static partial Regex Positional();
}

// MARK: - What goes into the prompt

/// <summary>What the user has chosen for one chat.</summary>
public sealed record SkillSelection
{
    /// <summary>Skill ids selected by hand — their full instructions are always in the prompt.</summary>
    public IReadOnlySet<string> Pinned { get; init; } = new HashSet<string>();
    /// <summary>Whether the model may discover skills itself (the catalog + use_skill).</summary>
    public bool Auto { get; init; } = true;
    /// <summary>Skill ids switched off everywhere.</summary>
    public IReadOnlySet<string> Disabled { get; init; } = new HashSet<string>();
}

public sealed record SkillPromptResult(
    string Text,
    /// <summary>Skills whose full text is in the prompt (always-on rules + pinned).</summary>
    IReadOnlyList<Skill> Injected,
    /// <summary>Skills offered in the catalog for use_skill.</summary>
    IReadOnlyList<Skill> Catalog,
    /// <summary>Names left out because of size caps.</summary>
    IReadOnlyList<string> Skipped);

public static class SkillPrompt
{
    public const int PerSkillCap = 60_000;
    public const int TotalCap = 200_000;
    public const int CatalogLimit = 60;

    /// <summary>The skills in play: not shadowed, not switched off.</summary>
    public static IReadOnlyList<Skill> Active(IEnumerable<Skill> skills, SkillSelection selection) =>
        skills.Where(s => !s.Shadowed && !selection.Disabled.Contains(s.Id)).ToList();

    public static SkillPromptResult Build(IEnumerable<Skill> skills, SkillSelection selection)
    {
        var live = Active(skills, selection);
        var parts = new List<string>();
        var injected = new List<Skill>();
        var skipped = new List<string>();
        var used = 0;

        string? BodyOf(Skill skill)
        {
            if (skill.Document() is not { } doc) return null;
            var text = SkillArguments.Expand(doc.Body, "").Trim();
            if (text.Length > PerSkillCap) text = TextUtil.Prefix(text, PerSkillCap) + "\n[… truncated; read the file for the rest]";
            return text;
        }

        var alwaysBlocks = new List<string>();
        foreach (var skill in live.Where(s => s.AlwaysApply && !selection.Pinned.Contains(s.Id)))
        {
            if (BodyOf(skill) is not { } text) continue;
            if (used + text.Length > TotalCap)
            {
                skipped.Add(skill.Name);
                continue;
            }
            used += text.Length;
            injected.Add(skill);
            alwaysBlocks.Add($"### {skill.Name} ({skill.Origin.Label()} {skill.Kind.Label().ToLowerInvariant()})\n{text}");
        }
        if (alwaysBlocks.Count > 0) parts.Add("--- Rules that always apply ---\n" + string.Join("\n\n", alwaysBlocks));

        var pinnedBlocks = new List<string>();
        foreach (var skill in live.Where(s => selection.Pinned.Contains(s.Id)))
        {
            if (BodyOf(skill) is not { } text) continue;
            if (used + text.Length > TotalCap)
            {
                skipped.Add(skill.Name);
                continue;
            }
            used += text.Length;
            injected.Add(skill);
            var dir = skill.Kind == SkillKind.Skill ? $"\nBase directory for bundled files: {skill.Directory}" : "";
            pinnedBlocks.Add($"### {skill.Name}{dir}\n{text}");
        }
        if (pinnedBlocks.Count > 0)
            parts.Add("--- Skills the user selected for this chat (follow them) ---\n" + string.Join("\n\n", pinnedBlocks));

        var catalog = new List<Skill>();
        if (selection.Auto)
        {
            catalog = live.Where(s => s.ModelInvocable && !s.AlwaysApply && !selection.Pinned.Contains(s.Id)).ToList();
            if (catalog.Count > 0)
            {
                var shown = catalog.Take(CatalogLimit).ToList();
                var lines = shown.Select(s =>
                {
                    var line = $"- {s.Name}: {TextUtil.Prefix(s.Description, 300)}";
                    if (s.Globs.Count > 0) line += $" (files: {string.Join(", ", s.Globs.Take(4))})";
                    return line;
                });
                var block = "--- Available skills ---\n" +
                            "Skills are packaged instructions. When the task matches one below, call `use_skill` with its name " +
                            "BEFORE you start, then follow it. Do not load skills that don't apply.\n" +
                            string.Join("\n", lines);
                if (catalog.Count > shown.Count)
                    block += $"\n({catalog.Count - shown.Count} more skills exist; call `use_skill` with an unknown name to list them all.)";
                parts.Add(block);
            }
        }
        return new SkillPromptResult(string.Join("\n\n", parts), injected, catalog, skipped);
    }
}
