namespace Dsh.Core;

// MARK: - agent (subagents)
//
// Spawns a child Engine that works a self-contained task in a bounded loop, then returns a final
// report. The child inherits the parent's model, permission gate, shell, and workspace/policy; it
// does NOT inherit the agent tool itself (no nested subagents).

public sealed class AgentTool : IToolExecutor
{
    public const string ToolName = "agent";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Spawn a subagent to work a self-contained task (e.g. 'find every usage of X and report the call sites'). It gets the same tools (except agent), works autonomously in a bounded loop, and returns a final report. Prefer this over doing long exploration yourself when the task is well-scoped.",
        """{"type":"object","properties":{"description":{"type":"string","description":"What to name this subagent"},"prompt":{"type":"string","description":"The complete task for the subagent. It cannot ask you questions — everything it needs goes here."}},"required":["description","prompt"]}""");

    /// <summary>Subagents get fewer iterations than the top-level session.</summary>
    private const int SubagentMaxIterations = 20;

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var description = JsonArgs.String(args, "description") ?? "subagent";
        var prompt = JsonArgs.String(args, "prompt") ?? "";
        if (prompt.Length == 0) return "Error: prompt is required.";
        if (context.Depth >= 1) return "Error: nested subagents are not allowed (depth limit).";

        // Same built-in capabilities as the parent, minus the agent tool itself.
        var subRegistry = ToolRegistry.Standard(context.Depth + 1, context.Shell);
        var config = new EngineConfig(context.Model)
        {
            MaxIterations = SubagentMaxIterations,
            ToolTimeout = TimeSpan.FromSeconds(300),
            ContextWindow = context.ContextWindow,
            Thinking = context.Thinking,
            Shell = context.Shell,
            Depth = context.Depth + 1,
        };
        var client = context.Client;
        var engine = new Engine(client, subRegistry, SubagentPrompt(context.Workspace, context.Policy.Preset, context.Shell),
            config, context.Workspace, context.Policy, context.RequestPermission)
        {
            // Same window the parent resolved — without this, a long subagent task runs uncompacted
            // until it hits the server's hard limit. A subagent's run is one user message followed
            // by many tool round-trips, so it opts into the assistant-boundary rule (which still
            // never orphans a call from its own result).
            Compactor = async (used, messages, ct) =>
            {
                if (context.ContextWindow is not { } limit
                    || Compaction.MakePlan(used, limit, messages, allowAssistantBoundary: true) is not { } plan)
                    return messages;
                var summary = await Dsh.Core.Compaction.SummarizeAsync(client, plan, model: context.Model,
                    cancellationToken: ct).ConfigureAwait(false);
                if (summary is null) return messages;
                return [LlmMessage.SystemText(Dsh.Core.Compaction.SummaryHeader + summary), .. plan.ToKeep];
            },
        };

        try
        {
            var result = await engine.RunAsync([], prompt, sink: static _ => { }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var report = result.FinalText.Length > 0
                ? result.FinalText
                : "Subagent completed without a final report. Its tool work (if any) has already been applied in the workspace.";
            return $"Subagent '{description}' finished.\n\n{report}";
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Subagent '{description}' failed: {ex.Message}";
        }
    }

    public static string SubagentPrompt(string workspace, PermissionPreset preset, AgentShell shell) =>
        "You are a focused subagent. Work autonomously: read and run tools as needed to answer the task in the user message. " +
        "Do not ask questions — make reasonable assumptions and state them in your final answer. Keep your final answer " +
        "concise and factual; it is returned to the main agent, not to a human.\n" +
        $"Workspace: {workspace}\n" +
        $"Platform: {PlatformInfo.Description}; run_shell_command uses {shell.DisplayName}.\n" +
        $"Permission preset: {preset.RawValue()}. If a tool is denied, do not retry the same action; note the limitation in your report instead.";
}
