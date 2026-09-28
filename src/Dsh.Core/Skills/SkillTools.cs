using System.Text;

namespace Dsh.Core;

// MARK: - use_skill

/// <summary>Loads a skill's full instructions. The system prompt lists skills by name and description
/// only; this is how the model reads the one that applies.</summary>
public sealed class UseSkillTool(IReadOnlyList<Skill> skills) : IToolExecutor
{
    public const string ToolName = "use_skill";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Load a skill's full instructions (and its bundled files) before doing a task the skill covers. Skills are listed in the system prompt with when to use each. Pass the exact skill name; pass an unknown name to list every skill.",
        """{"type":"object","properties":{"name":{"type":"string","description":"Skill name, exactly as listed"},"arguments":{"type":"string","description":"Optional arguments for the skill ($ARGUMENTS)"}},"required":["name"]}""");

    /// <summary>Skills in play for this chat (already filtered for enabled/shadowed).</summary>
    public IReadOnlyList<Skill> Skills { get; } = skills;

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ToolResult>(Execute(arguments));

    private string Execute(string arguments)
    {
        var args = JsonArgs.Object(arguments);
        var requested = (JsonArgs.String(args, "name") ?? "").Trim();
        var skillArguments = JsonArgs.String(args, "arguments") ?? "";
        var key = SkillNaming.Slug(requested.StartsWith('/') ? requested[1..] : requested);
        var skill = requested.Length == 0
            ? null
            : Skills.FirstOrDefault(s => s.Slug == key || s.Name.ToLowerInvariant() == requested.ToLowerInvariant());
        if (skill is null)
        {
            var names = Skills.Where(s => s.ModelInvocable).Select(s => s.Name).ToList();
            return $"Error: no skill named “{requested}”. Available: {(names.Count == 0 ? "(none)" : string.Join(", ", names))}.";
        }
        if (!skill.ModelInvocable && !skill.AlwaysApply)
            return $"Error: “{skill.Name}” is a command the user runs themselves ({skill.Kind.Label().ToLowerInvariant()}); it is not available for automatic use.";
        if (skill.Document() is not { } doc) return $"Error: can't read {skill.Path}.";

        var body = SkillArguments.Expand(doc.Body, skillArguments).Trim();
        var truncated = false;
        if (body.Length > SkillPrompt.PerSkillCap)
        {
            body = TextUtil.Prefix(body, SkillPrompt.PerSkillCap);
            truncated = true;
        }
        var output = new StringBuilder();
        output.Append($"# Skill: {skill.Name} ({skill.Origin.Label()} {skill.Kind.Label().ToLowerInvariant()}, {skill.Scope.Label().ToLowerInvariant()})\n");
        if (skill.Kind == SkillKind.Skill) output.Append($"Base directory: {skill.Directory}\n");
        if (skill.AllowedTools.Count > 0) output.Append($"Suggested tools: {string.Join(", ", skill.AllowedTools)}\n");
        output.Append('\n').Append(body);
        if (truncated) output.Append($"\n\n[… truncated; read {skill.Path} for the rest]");
        var files = skill.Resources();
        if (files.Count > 0)
        {
            output.Append("\n\nBundled files (relative to the base directory; read them with read_file when the instructions say so):\n");
            output.Append(string.Join("\n", files.Select(f => $"- {f}")));
        }
        return output.ToString();
    }
}

// MARK: - propose_skill

/// <summary>Lets the agent write down a reusable procedure as a DRAFT. Nothing the agent proposes is
/// active until the user approves it in Skills.</summary>
public sealed class ProposeSkillTool(string? projectRoot, SkillLocations? locations = null) : IToolExecutor
{
    public const string ToolName = "propose_skill";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Propose a reusable skill (a saved procedure) for the user to review. Use when the user asks you to make a skill, or after finishing a multi-step workflow worth repeating. It is saved as a DRAFT the user must approve — it is not active in this conversation. Write the description as WHEN to use it.",
        """{"type":"object","properties":{"name":{"type":"string","description":"lowercase-hyphen name, e.g. godot-export-android"},"description":{"type":"string","description":"One or two sentences: when to use this skill, with trigger words"},"instructions":{"type":"string","description":"The skill body in Markdown: goal, numbered steps, exact commands, gotchas"},"scope":{"type":"string","enum":["project","user"],"description":"project = this project only (default); user = every project"}},"required":["name","description","instructions"]}""");

    public string? ProjectRoot { get; } = projectRoot;
    public SkillLocations Locations { get; } = locations ?? SkillLocations.Standard;

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult<ToolResult>(Execute(arguments));

    private string Execute(string arguments)
    {
        var args = JsonArgs.Object(arguments);
        var name = JsonArgs.String(args, "name") ?? "";
        var description = (JsonArgs.String(args, "description") ?? "").Trim();
        var instructions = (JsonArgs.String(args, "instructions") ?? "").Trim();
        var scope = JsonArgs.String(args, "scope") == "user" ? SkillScope.User : SkillScope.Project;
        var slug = SkillNaming.Slug(name);
        if (slug.Length == 0) return "Error: give the skill a lowercase-hyphen name.";
        if (description.Length == 0) return "Error: the description must say when to use the skill.";
        if (instructions.Length < 20) return "Error: the instructions are empty or too short to be useful.";
        if (instructions.Length > 60_000)
            return "Error: the instructions are too long (max 60,000 characters). Split it, or keep reference material in a file.";
        var text = $"---\nname: {slug}\ndescription: {SkillDocument.Scalar(description)}\n---\n\n{instructions}\n";
        try
        {
            var draft = SkillDrafts.Create(text, scope, ProjectRoot, "agent", locations: Locations);
            var issues = SkillLint.Check(text).Where(i => i.Severity >= SkillIssueSeverity.Warning).ToList();
            var output = $"Saved draft “{draft.Name}” for the user's review. It is NOT active yet — the user has to approve it in Skills (Settings › Skills, or the Skills button in the chat). Don't rely on it in this conversation; carry on with the task.";
            if (issues.Count > 0) output += "\nNotes: " + string.Join(" ", issues.Select(i => i.Message));
            return output;
        }
        catch (Exception ex) when (ex is SkillException or IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return $"Error: {ex.Message}";
        }
    }
}
