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

    /// <summary>Decide a permission question the way Vision's own engine decides the same call under the chat's preset:
    /// reads and web fetches go ahead; writes go ahead inside the project (Workspace write) or always (Full access) and ask
    /// otherwise; commands that only read go ahead, ones that could change something ask (Workspace write); anything Vision
    /// doesn't know asks — except under Full access. A safety check Claude Code says needs a person always asks.</summary>
    public static ClaudeCodePermissionDecision Decide(ClaudeCodePermissionRequest request, PermissionPolicy policy)
    {
        var tool = request.ToolName;
        var input = request.Input;
        var visionName = ClaudeCodeTools.VisionName(tool);
        var needsPerson = request.DecisionReasonType == "safetyCheck" || request.ClassifierApprovable == false;
        var why = request.DecisionReason is { Length: > 0 } reason ? $" — Claude Code says: {TextUtil.Prefix(StripAnsi(reason), 200)}" : "";

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
                var raw = Str(input?["file_path"]) ?? Str(input?["notebook_path"]) ?? "";
                decision = policy.CheckWrite(raw);
                detail = $"{(policy.Preset == PermissionPreset.Plan ? "Write" : "Write outside the project")}: {policy.Resolve(raw).Path}";
                break;
            }
            case "Bash" or "PowerShell":
            {
                var command = Str(input?["command"]) ?? "";
                decision = policy.CheckShell(command);
                detail = $"Run command: {command}";
                break;
            }
            case "Read" or "Glob" or "Grep" or "LS" or "WebFetch" or "WebSearch" or "TodoWrite" or "Task" or "Agent" or "Skill"
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

    private static string? Str(JsonNode? node) => ClaudeCodePermissionRequest.Str(node);

    private static string StripAnsi(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\x1B\[[0-9;?]*[ -/]*[@-~]", "");
}
