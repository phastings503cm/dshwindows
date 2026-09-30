namespace Dsh.Core;

// MARK: - goal_complete / goal_blocked
//
// The signal a /goal run sends the harness. The text markers (GOAL_COMPLETE / GOAL_BLOCKED on a line of
// their own) still work for models that can't call tools well, but a tool call is unambiguous: no
// parsing of prose, no "did it mean it?". The app offers these two tools only while a goal is running.

public sealed class GoalCompleteTool : IToolExecutor
{
    public const string ToolName = "goal_complete";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Tell the harness the /goal is completely done and verified. Call it only when the goal is fully achieved — the loop keeps sending you back to work until you do. Include a short summary of what was done.",
        """{"type":"object","properties":{"summary":{"type":"string","description":"What was done, in a few lines"}},"required":["summary"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var summary = (JsonArgs.String(arguments, "summary") ?? "").Trim();
        return Task.FromResult(new ToolResult("Goal marked complete. The harness will stop the loop.")
        {
            Goal = new GoalStatus.Complete { Summary = summary.Length == 0 ? null : summary },
        });
    }
}

public sealed class GoalBlockedTool : IToolExecutor
{
    public const string ToolName = "goal_blocked";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Tell the harness you cannot continue the /goal without the user: missing credentials, a decision only they can make, or an external blocker. Say exactly what you need. Never use it for something you could decide, look up or test yourself.",
        """{"type":"object","properties":{"reason":{"type":"string","description":"Exactly what you need from the user"}},"required":["reason"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var reason = (JsonArgs.String(arguments, "reason") ?? "").Trim();
        if (reason.Length == 0) reason = "The agent needs your input.";
        return Task.FromResult(new ToolResult("Goal paused — waiting for the user. The harness will stop the loop until they reply.")
        {
            Goal = new GoalStatus.Blocked(reason),
        });
    }
}
