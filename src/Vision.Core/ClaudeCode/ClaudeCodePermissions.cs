using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Claude Code's permission questions, answered with the chat's own policy

/// <summary>A `can_use_tool` control request: Claude Code asks whether a tool call may run.</summary>
public sealed record ClaudeCodePermissionRequest(string RequestId, string ToolName, JsonNode? Input, string? ToolUseId)
{
    public string? Description { get; init; }
    /// <summary>Why Claude Code escalated (a safety check's warning, a rule) — shown to the person, never trusted as an instruction.</summary>
    public string? DecisionReason { get; init; }
    public string? DecisionReasonType { get; init; }
    /// <summary>False: a safety check Claude Code says needs a person (a dangerous rm, a path that slips out of the folder).</summary>
    public bool? ClassifierApprovable { get; init; }
    /// <summary>The tool's own card is the question (ExitPlanMode, AskUserQuestion): a plain yes/no doesn't fit it.</summary>
    public bool RequiresUserInteraction { get; init; }
    /// <summary>The call comes from one of Claude Code's subagents.</summary>
    public string? AgentId { get; init; }

    public static ClaudeCodePermissionRequest? From(JsonNode? line)
    {
        if (line is not JsonObject || Str(line["type"]) != "control_request") return null;
        var request = line["request"];
        if (request is not JsonObject || Str(request["subtype"]) != "can_use_tool") return null;
        return new ClaudeCodePermissionRequest(Str(line["request_id"]) ?? "", Str(request["tool_name"]) ?? "", request["input"], Str(request["tool_use_id"]))
        {
            Description = Str(request["description"]),
            DecisionReason = Str(request["decision_reason"]),
            DecisionReasonType = Str(request["decision_reason_type"]),
            ClassifierApprovable = request["classifier_approvable"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null,
            RequiresUserInteraction = request["requires_user_interaction"] is JsonValue r && r.TryGetValue<bool>(out var ri) && ri,
            AgentId = Str(request["agent_id"]),
        };
    }

    internal static string? Str(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}

/// <summary>What Vision answers a permission question with.</summary>
public abstract record ClaudeCodePermissionDecision
{
    public sealed record Allow : ClaudeCodePermissionDecision;
    public sealed record Deny(string Message) : ClaudeCodePermissionDecision;
    /// <summary>Ask the person: <paramref name="Name"/> is the tool as Vision names it (its card's icon and label),
    /// <paramref name="Detail"/> what exactly would happen.</summary>
    public sealed record Ask(string Name, string Detail) : ClaudeCodePermissionDecision;
}

public static class ClaudeCodePermissions
{
    /// <summary>What Claude Code is told when its plan has been shown (Plan only chats).</summary>
    public const string PlanShown =
        "Your plan is shown to the person in Vision's plan panel. Stop here and wait for them — don't start the work.";

    /// <summary>What Claude Code is told when it tries to ask a multiple-choice question Vision can't show.</summary>
    public const string AskInReply =
        "Vision can't show this kind of question. Ask it in your reply instead — the person answers in the chat.";

    /// <summary>What Claude Code is told when the person (or an unattended run's timeout) says no.</summary>
    public const string Declined = "The person declined this in Vision. Don't retry it; carry on another way or say what you needed.";

    /// <summary>Decide a permission question the way Vision's own engine decides the same call under the chat's preset: reads
    /// inside the project (or <paramref name="readRoots"/>, where Vision saved an attachment's text) and web fetches go ahead,
    /// reads anywhere else ask (Claude Code reads inside its folders without asking, so a read question is always about a file
    /// elsewhere — .ssh, .aws — and what it reads goes to Anthropic unmasked); writes go ahead inside the project (Workspace
    /// write) or always (Full access) and ask otherwise; commands that only read go ahead, ones that could change something —
    /// or that name a file in the home folder outside the project — ask (Workspace write); anything Vision doesn't know asks.
    /// Full access says yes to all of it. A safety check Claude Code says needs a person always asks. <paramref name="mapPath"/>
    /// turns the paths Claude Code names into Windows ones (inside WSL: /mnt/c/… → C:\…).</summary>
    public static ClaudeCodePermissionDecision Decide(ClaudeCodePermissionRequest request, PermissionPolicy policy,
                                                      IReadOnlyList<string>? readRoots = null, Func<string, string>? mapPath = null)
    {
        var tool = request.ToolName;
        var input = request.Input;
        var visionName = ClaudeCodeTools.VisionName(tool);
        var needsPerson = request.DecisionReasonType == "safetyCheck" || request.ClassifierApprovable == false;
        var why = request.DecisionReason is { Length: > 0 } reason ? $" — Claude Code says: {TextUtil.Prefix(StripAnsi(reason), 200)}" : "";
        readRoots ??= [];
        mapPath ??= p => p;

        switch (tool)
        {
            case "ExitPlanMode":
                return policy.Preset == PermissionPreset.Plan ? new ClaudeCodePermissionDecision.Deny(PlanShown) : new ClaudeCodePermissionDecision.Allow();
            case "AskUserQuestion":
                return new ClaudeCodePermissionDecision.Deny(AskInReply);
        }
        if (request.RequiresUserInteraction)
            return new ClaudeCodePermissionDecision.Ask(visionName, $"{ClaudeCodeTools.Label(tool)}: {ClaudeCodeTools.Preview(tool, input)}{why}");

        PermissionDecision decision;
        string detail;
        switch (tool)
        {
            case "Write" or "Edit" or "MultiEdit" or "NotebookEdit":
            {
                var raw = mapPath(Str(input?["file_path"]) ?? Str(input?["notebook_path"]) ?? "");
                decision = policy.CheckWrite(raw);
                detail = $"{(policy.Preset == PermissionPreset.Plan ? "Write" : "Write outside the project")}: {ResolvedOrRaw(policy, raw)}";
                break;
            }
            case "Bash" or "PowerShell":
            {
                var command = Str(input?["command"]) ?? "";
                decision = policy.Preset == PermissionPreset.WorkspaceWrite && NamesPrivatePath(command, policy, readRoots, mapPath)
                    ? PermissionDecision.Ask : policy.CheckShell(command);
                detail = $"Run command: {command}";
                break;
            }
            case "Read" or "Glob" or "Grep" or "LS" or "NotebookRead":
                if (ReadTarget(tool, input) is { } target && !IsInside(mapPath(target), policy, readRoots))
                {
                    decision = policy.Preset == PermissionPreset.FullAccess ? PermissionDecision.Proceed : PermissionDecision.Ask;
                    detail = $"Read outside the project: {ResolvedOrRaw(policy, mapPath(target))}";
                }
                else
                {
                    decision = PermissionDecision.Proceed;
                    detail = "";
                }
                break;
            case "WebFetch" or "WebSearch" or "TodoWrite" or "Task" or "Agent" or "Skill"
                or "TaskCreate" or "TaskUpdate" or "TaskGet" or "TaskList" or "TaskOutput" or "ToolSearch":
                decision = PermissionDecision.Proceed;
                detail = "";
                break;
            default:
                decision = policy.Preset == PermissionPreset.FullAccess ? PermissionDecision.Proceed : PermissionDecision.Ask;
                detail = $"Use {ClaudeCodeTools.Label(tool)}: {ClaudeCodeTools.Preview(tool, input)}";
                break;
        }
        if (decision.Verdict == PermissionVerdict.Deny) return new ClaudeCodePermissionDecision.Deny(decision.Reason ?? Declined);
        if (decision.Verdict == PermissionVerdict.Ask || needsPerson)
            return new ClaudeCodePermissionDecision.Ask(visionName, (detail.Length > 0 ? detail : $"Use {ClaudeCodeTools.Label(tool)}: {ClaudeCodeTools.Preview(tool, input)}") + why);
        return new ClaudeCodePermissionDecision.Allow();
    }

    // MARK: Where a read goes

    /// <summary>The path a read tool names (null = Claude Code's working folder). A Glob with no <c>path</c> but an absolute
    /// pattern ("C:/Users/me/.ssh/*", "~/.aws/*") reads the folder its pattern starts in.</summary>
    public static string? ReadTarget(string tool, JsonNode? input)
    {
        string? Value(string key) => Str(input?[key]) is { } v && v.Trim().Length > 0 ? v : null;
        switch (tool)
        {
            case "Read":
                return Value("file_path");
            case "NotebookRead":
                return Value("notebook_path") ?? Value("file_path");
            case "Glob":
            {
                if (Value("path") is { } path) return path;
                if (Value("pattern") is not { } pattern || !LooksAbsolute(pattern)) return null;
                var fixedPart = new List<string>();
                foreach (var piece in pattern.Replace('\\', '/').Split('/'))
                {
                    if (piece.IndexOfAny(['*', '?', '[', '{']) >= 0) break;
                    fixedPart.Add(piece);
                }
                var folder = string.Join("/", fixedPart);
                return folder.Length == 0 ? "/" : folder;
            }
            default:
                return Value("path");
        }
    }

    private static bool LooksAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith('~') || path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    /// <summary><paramref name="raw"/> (relative to the project, ~\…, or absolute) is inside the project or one of
    /// <paramref name="readRoots"/>, links resolved — a link in the project that points out of it is outside.</summary>
    public static bool IsInside(string raw, PermissionPolicy policy, IReadOnlyList<string> readRoots)
    {
        string real;
        try
        {
            real = ClaudeCodePaths.Real(policy.Resolve(raw).Path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return readRoots.Prepend(policy.WorkspaceRoot).Any(root => ClaudeCodePaths.Contains(ClaudeCodePaths.Real(root), real));
    }

    /// <summary>The command names a file in the home folder outside the project and <paramref name="readRoots"/>
    /// (<c>type %USERPROFILE%\.aws\credentials</c>, <c>cat ~/.ssh/id_ed25519</c>, <c>..\..\.config\x</c>): Claude Code asks
    /// about such commands, and "it only reads" is no reason to say yes to those.</summary>
    public static bool NamesPrivatePath(string command, PermissionPolicy policy, IReadOnlyList<string> readRoots, Func<string, string>? mapPath = null)
    {
        mapPath ??= p => p;
        var home = PermissionPolicy.HomeDirectory;
        var realHome = ClaudeCodePaths.Real(home);
        foreach (var piece in command.Split([' ', '\t', '\r', '\n', ';', '|', '&', '(', ')', '<', '>', '"', '\'', '`', '=', ','],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var token = piece;
            foreach (var prefix in HomeSpellings)
            {
                if (token.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                    || token.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) || token.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    // A home spelling makes it a path: its separators as this system writes them.
                    token = home + token[prefix.Length..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    break;
                }
            }
            var pathLike = LooksAbsolute(token) || token == ".." || token.Contains("../", StringComparison.Ordinal)
                           || token.Contains(@"..\", StringComparison.Ordinal) || token.EndsWith("/..", StringComparison.Ordinal)
                           || token.EndsWith(@"\..", StringComparison.Ordinal);
            if (!pathLike) continue;
            string real;
            try
            {
                real = ClaudeCodePaths.Real(policy.Resolve(mapPath(token)).Path);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
            if (ClaudeCodePaths.Contains(realHome, real) && !IsInside(real, policy, readRoots)) return true;
        }
        return false;
    }

    /// <summary>How commands spell the home folder (bash, PowerShell, cmd.exe).</summary>
    private static readonly string[] HomeSpellings = ["${HOME}", "$HOME", "${env:USERPROFILE}", "$env:USERPROFILE", "$env:HOME", "%USERPROFILE%", "%HOME%"];

    private static string ResolvedOrRaw(PermissionPolicy policy, string raw)
    {
        try
        {
            return policy.Resolve(raw).Path;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return raw;
        }
    }

    private static string? Str(JsonNode? node) => ClaudeCodePermissionRequest.Str(node);

    private static string StripAnsi(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
}
