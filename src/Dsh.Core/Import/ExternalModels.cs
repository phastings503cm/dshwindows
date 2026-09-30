namespace Dsh.Core;

// MARK: - Bringing in what other tools left on this PC
//
// Claude Code and Cursor keep skills, commands, rules, subagents, plugins and saved notes under the
// user's profile. DSH already reads their skill folders in place; this layer finds *everything* they
// have installed, shows it, and copies the chosen items into DSH's own data folder — converted where
// the shapes differ — so DSH keeps working without them and the copies can be edited. Nothing in the
// other tool's folders is ever changed, and nothing is run.
//
//   Claude Code   ~\.claude\CLAUDE.md                  → your instructions (every project)
//                 ~\.claude\skills\<n>\SKILL.md        → skill
//                 ~\.claude\commands\**\*.md           → skill you run with /name
//                 ~\.claude\rules\**\*.md              → rule
//                 ~\.claude\agents\*.md                → skill that delegates with the agent tool
//                 ~\.claude\plugins (installed ones)   → the same four, from each plugin
//                 ~\.claude\projects\<p>\memory\*.md   → notes for that project
//   Cursor        ~\.cursor\skills, commands, rules, agents, plugins — the same shapes

public enum ExternalTool { ClaudeCode, Cursor }

public enum ExternalKind
{
    /// <summary>A SKILL.md folder.</summary>
    Skill,
    /// <summary>A slash command (name.md): becomes a skill only you run, as /name.</summary>
    Command,
    /// <summary>A rule: always on, or attached to files by glob.</summary>
    Rule,
    /// <summary>The global instruction file (CLAUDE.md): becomes one of your instruction files.</summary>
    Instructions,
    /// <summary>A subagent definition: becomes a skill that hands the work to the agent tool.</summary>
    Subagent,
    /// <summary>The notes Claude Code saved for one project.</summary>
    ProjectNotes,
}

/// <summary>How an item compares with what DSH already has.</summary>
public enum ExternalStatus
{
    /// <summary>Not in DSH yet.</summary>
    New,
    /// <summary>Already in DSH, identical.</summary>
    Imported,
    /// <summary>DSH has something of that name that isn't the same.</summary>
    Different,
    /// <summary>Already in the approval queue in Settings › Skills, waiting to be switched on.</summary>
    Waiting,
}

public static class ExternalLabels
{
    public static string Label(this ExternalTool tool) => tool == ExternalTool.ClaudeCode ? "Claude Code" : "Cursor";

    /// <summary>The heading for a kind's section: plural, as a noun.</summary>
    public static string Plural(this ExternalKind kind) => kind switch
    {
        ExternalKind.Skill => "Skills",
        ExternalKind.Command => "Commands",
        ExternalKind.Rule => "Rules",
        ExternalKind.Instructions => "Your instructions",
        ExternalKind.Subagent => "Subagents",
        _ => "Project memory",
    };

    /// <summary>One line on what importing does with the kind.</summary>
    public static string Effect(this ExternalKind kind) => kind switch
    {
        ExternalKind.Skill => "Copied to your DSH skills; the agent loads one when a task matches.",
        ExternalKind.Command => "Become skills you run yourself by typing /name in a chat.",
        ExternalKind.Rule => "Become DSH rules — always on, or applied to the files they name.",
        ExternalKind.Instructions => "Loaded into every prompt, in every project. Edit them in Memory & Skills.",
        ExternalKind.Subagent => "Become skills that tell the agent to hand the work to a subagent with these instructions.",
        _ => "Saved notes, shown to the agent only in the project they belong to.",
    };

    public static string Label(this ExternalKind kind) => kind switch
    {
        ExternalKind.Skill => "Skill",
        ExternalKind.Command => "Command",
        ExternalKind.Rule => "Rule",
        ExternalKind.Instructions => "Instructions",
        ExternalKind.Subagent => "Subagent",
        _ => "Project notes",
    };

    /// <summary>The items in plain words: "8 skills, 2 commands, your instructions and notes for 3
    /// projects". "nothing" when there are none.</summary>
    public static string Summarize(IEnumerable<ExternalItem> items)
    {
        static string Count(int n, string singular) => n == 1 ? $"1 {singular}" : $"{n} {singular}s";
        var parts = new List<string>();
        foreach (var group in items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
        {
            var n = group.Count();
            parts.Add(group.Key switch
            {
                ExternalKind.Skill => Count(n, "skill"),
                ExternalKind.Command => Count(n, "command"),
                ExternalKind.Rule => Count(n, "rule"),
                ExternalKind.Subagent => Count(n, "subagent"),
                ExternalKind.Instructions => "your instructions",
                _ => $"notes for {Count(n, "project")}",
            });
        }
        return parts.Count switch
        {
            0 => "nothing",
            1 => parts[0],
            _ => string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1],
        };
    }
}

/// <summary>Something another tool has installed that DSH can use.</summary>
public sealed record ExternalItem
{
    /// <summary>Stable across scans: the tool, the kind and where it is read from.</summary>
    public required string Id { get; init; }
    public required ExternalTool Tool { get; init; }
    public required ExternalKind Kind { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    /// <summary>The file or folder it is read from.</summary>
    public required string SourcePath { get; init; }
    /// <summary>Where it sits within the tool: "Your skills", "Plugin deploy-on-aws", …</summary>
    public string Group { get; init; } = "";
    public int FileCount { get; init; } = 1;
    public long Bytes { get; init; }
    /// <summary>Bundled scripts — listed so the user knows; importing never runs them.</summary>
    public bool HasScripts { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    /// <summary>It, or a file bundled with it, looks like it holds a key or token: not ticked for you.</summary>
    public bool ContainsSecret { get; init; }
    public ExternalStatus Status { get; init; }
    /// <summary>Ticked when the import sheet opens.</summary>
    public bool SelectedByDefault { get; init; }
    /// <summary>Why it isn't ticked by default, when Status doesn't already say.</summary>
    public string? Reason { get; init; }
    /// <summary>Project notes: the project's folder, when it exists on this PC.</summary>
    public string? ProjectPath { get; init; }

    /// <summary>What it becomes in DSH: the folder name under the skills folder, or the instruction
    /// file's name without ".md", or the notes folder's name.</summary>
    public string Target { get; init; } = "";
    /// <summary>The parsed skill, command or rule (null for the other kinds).</summary>
    internal Skill? Skill { get; init; }
}

/// <summary>Something found but not importable, said plainly so nothing is silently dropped.</summary>
public sealed record ExternalNote(ExternalTool Tool, string Text);

public sealed record ExternalToolInfo(ExternalTool Tool, string Folder, bool Found);

/// <summary>Everything a scan found.</summary>
public sealed record ExternalInventory(IReadOnlyList<ExternalToolInfo> Tools, IReadOnlyList<ExternalItem> Items,
                                       IReadOnlyList<ExternalNote> Left)
{
    public bool AnyToolFound => Tools.Any(t => t.Found);

    /// <summary>Items the sheet would tick for you: new, and safe to bring in without a second look.</summary>
    public int Suggested => Items.Count(i => i.SelectedByDefault);

    public static ExternalInventory Empty { get; } = new([], [], []);
}

/// <param name="Destination">Where it was written (for a draft, the draft's folder).</param>
/// <param name="Renamed">It landed under a numbered name because DSH already had a different one.</param>
/// <param name="IsDraft">Held for approval in Settings › Skills instead of switched on.</param>
public sealed record ExternalImported(ExternalItem Item, string Destination, bool Renamed, bool IsDraft = false);

public sealed record ExternalSkipped(ExternalItem Item, string Reason);

public sealed record ExternalImportResult
{
    /// <summary>Everything written to DSH's data folder, drafts included (see <see cref="ExternalImported.IsDraft"/>).</summary>
    public IReadOnlyList<ExternalImported> Imported { get; init; } = [];
    /// <summary>The skills among them held back for approval (asked for with "review first").</summary>
    public IReadOnlyList<SkillDraft> Drafts { get; init; } = [];
    public IReadOnlyList<ExternalSkipped> Skipped { get; init; } = [];
}
