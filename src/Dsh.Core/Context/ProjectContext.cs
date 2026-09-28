using System.Globalization;

namespace Dsh.Core;

/// <summary>A project instruction file the agent loads on every turn.</summary>
public sealed record InstructionFile(string Path, string Text, string Label)
{
    public string Id => Path;
    public int LineCount => Text.Length == 0 ? 0 : Text.Split('\n').Length;
}

/// <summary>Everything the app knows about a project folder that shapes the system prompt:
/// instruction/memory files and the skill catalog. This app IS the harness, so the files are read and
/// injected directly — no mirroring into AGENTS.md, no managed marker blocks.</summary>
public sealed record ProjectContext(string Root, IReadOnlyList<InstructionFile> Instructions, IReadOnlyList<Skill> Skills)
{
    /// <summary>Instruction files are looked for in this order; every one that exists is loaded.
    /// AGENTS.md and QWEN.md keep us compatible with projects set up for other agents;
    /// .cursorrules, .claude/CLAUDE.md and CLAUDE.local.md are the same idea in Cursor's and Claude
    /// Code's layouts.</summary>
    public static readonly IReadOnlyList<string> InstructionNames =
        ["AGENTS.md", "QWEN.md", "CLAUDE.md", ".claude/CLAUDE.md", "CLAUDE.local.md", ".cursorrules", "DSH.md", "MEMORY.md"];

    public static string UserSkillsDirectory => SkillLocations.Standard.UserSkills;

    private static string Join(string root, string relative) => Path.Combine([root, .. relative.Split('/')]);

    /// <summary>Read the project's instruction files and skill catalog from disk.</summary>
    public static ProjectContext Load(string root, SkillLocations? locations = null, SkillSources sources = SkillSources.All)
    {
        var instructions = new List<InstructionFile>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in InstructionNames)
        {
            var path = Join(root, name);
            if (!seen.Add(Path.GetFullPath(path))) continue; // CLAUDE.md vs claude.md on a case-insensitive disk
            if (ReadNonEmpty(path) is { } text) instructions.Add(new InstructionFile(path, text, name));
        }
        // Today's daily memory log, if the project keeps one.
        var today = DayStamp();
        var daily = Join(root, $"memory/{today}.md");
        if (ReadNonEmpty(daily) is { } log) instructions.Add(new InstructionFile(daily, log, $"memory/{today}.md"));

        var skills = SkillCatalog.Load(root, locations, sources);
        return new ProjectContext(root, instructions, skills);
    }

    private static string? ReadNonEmpty(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            return text.Trim().Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string DayStamp(DateTime? date = null) =>
        (date ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Frontmatter as flat key: value pairs (list values joined by ", ").</summary>
    public static IReadOnlyDictionary<string, string> Frontmatter(string text)
    {
        var doc = SkillDocument.Parse(text);
        var output = new Dictionary<string, string>(doc.Fields);
        foreach (var (key, list) in doc.Lists) output[key] = string.Join(", ", list);
        return output;
    }

    // MARK: - Prompt assembly

    /// <summary>The block appended to the system prompt for this project. Skills are advertised by
    /// name + description only; the model reads the SKILL.md with read_file when one applies.</summary>
    public string PromptSupplement(string environment, bool includeSkills = true)
    {
        var parts = new List<string> { environment };
        foreach (var file in Instructions)
            parts.Add($"--- {file.Label} (project instructions — follow these) ---\n{file.Text.Trim()}");

        if (includeSkills && Skills.Count > 0)
        {
            var catalog = string.Join("\n", Skills.Where(s => s.ModelInvocable && !s.AlwaysApply)
                .Select(s => $"- {s.Name}: {s.Description} [{s.Path}]"));
            parts.Add("--- Available skills ---\nWhen a task matches one of these, read its SKILL.md with `read_file` first and follow it.\n" + catalog);
        }
        return string.Join("\n\n", parts);
    }

    /// <summary>Machine facts the model would otherwise guess at.</summary>
    public static string EnvironmentBlock(string workspace, string model, PermissionPreset preset,
                                          AgentShell? shell = null, IEnumerable<string>? extraLines = null)
    {
        var lines = new List<string>
        {
            "--- Environment ---",
            $"Project folder: {workspace}",
            $"Platform: {PlatformInfo.Description}",
            $"Shell for run_shell_command: {(shell ?? AgentShell.Default).DisplayName}",
            $"Today's date: {DayStamp()}",
            $"Model: {model}",
            $"Permission preset: {preset.RawValue()}",
        };
        if (GitBranch(workspace) is { } branch) lines.Add($"Git branch: {branch}");
        if (extraLines is not null) lines.AddRange(extraLines);
        return string.Join("\n", lines);
    }

    /// <summary>Current branch, read straight from .git/HEAD — no subprocess, so this is safe to call
    /// while assembling a prompt. Follows a worktree's "gitdir:" pointer.</summary>
    public static string? GitBranch(string root)
    {
        try
        {
            var gitPath = Path.Combine(root, ".git");
            string headPath;
            if (Directory.Exists(gitPath))
            {
                headPath = Path.Combine(gitPath, "HEAD");
            }
            else if (File.Exists(gitPath))
            {
                var pointer = File.ReadAllText(gitPath).Trim();
                if (!pointer.StartsWith("gitdir:", StringComparison.Ordinal)) return null;
                var gitDir = pointer["gitdir:".Length..].Trim();
                if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(root, gitDir));
                headPath = Path.Combine(gitDir, "HEAD");
            }
            else
            {
                return null;
            }
            if (!File.Exists(headPath)) return null;
            var head = File.ReadAllText(headPath).Trim();
            const string prefix = "ref: refs/heads/";
            if (head.StartsWith(prefix, StringComparison.Ordinal)) return head[prefix.Length..];
            return TextUtil.Prefix(head, 8); // detached HEAD
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // MARK: - Authoring

    /// <summary>Create the memory scaffold a project needs: MEMORY.md plus today's daily log.
    /// Existing files are left alone. Returns the files created.</summary>
    public static IReadOnlyList<string> SetUpMemory(string root)
    {
        var created = new List<string>();
        var memory = Path.Combine(root, "MEMORY.md");
        if (!File.Exists(memory))
        {
            File.WriteAllText(memory,
                "# Project memory\n\n" +
                "Durable facts about this project. Loaded into every session, so keep it short —\n" +
                "detail belongs in a skill, and today's state belongs in the daily log.\n\n" +
                "-\n", TextUtil.Utf8NoBom);
            created.Add(memory);
        }
        var dailyDir = Directory.CreateDirectory(Path.Combine(root, "memory")).FullName;
        var daily = Path.Combine(dailyDir, $"{DayStamp()}.md");
        if (!File.Exists(daily))
        {
            File.WriteAllText(daily, $"# {DayStamp()}\n\n- \n", TextUtil.Utf8NoBom);
            created.Add(daily);
        }
        return created;
    }

    /// <summary>Scaffold a new skill under &lt;project&gt;/.agents/skills/&lt;slug&gt;/SKILL.md.</summary>
    public static string CreateSkill(string root, string name, string description)
    {
        var slug = new string(name.ToLowerInvariant().Replace(' ', '-')
            .Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());
        var dir = Directory.CreateDirectory(Path.Combine(root, ".agents", "skills", slug)).FullName;
        var path = Path.Combine(dir, "SKILL.md");
        if (File.Exists(path)) return path;
        File.WriteAllText(path,
            $"---\nname: {name}\ndescription: {description}\n---\n\n# {name}\n\n" +
            "State *when* this skill applies in the description above — that line is all the\n" +
            "model sees before deciding to load this file.\n\n## Steps\n\n1.\n", TextUtil.Utf8NoBom);
        return path;
    }
}
