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
        "Spawn a subagent to work a self-contained task (e.g. 'find every usage of X and report the call sites'). It gets the same tools (except agent), works autonomously in a bounded loop, and returns a final report. Prefer this over doing long exploration yourself when the task is well-scoped. Set run_in_background to true to launch it in the background and keep working: several can run in parallel; check them with agent_status (you're also told automatically when one finishes) and stop one with agent_stop.",
        """{"type":"object","properties":{"description":{"type":"string","description":"What to name this subagent"},"prompt":{"type":"string","description":"The complete task for the subagent. It cannot ask you questions — everything it needs goes here."},"run_in_background":{"type":"boolean","description":"Start it in the background and return at once (default false: wait for its report)"}},"required":["description","prompt"]}""");

    /// <summary>Subagents get fewer iterations than the top-level session.</summary>
    private const int SubagentMaxIterations = 20;

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var description = JsonArgs.String(args, "description") ?? "subagent";
        var prompt = JsonArgs.String(args, "prompt") ?? "";
        if (prompt.Length == 0) return "Error: prompt is required.";
        if (context.Depth >= 1) return "Error: nested subagents are not allowed (depth limit).";

        if (JsonArgs.Bool(args, "run_in_background", false))
        {
            if (context.BackgroundAgents is not { } pool)
                return "Error: background subagents aren't available here; call agent without run_in_background.";
            // The background run belongs to the pool (Stop / agent_stop cancel it), not to this call.
            var (job, error) = pool.Launch(description, ct => RunAsync(prompt, context, ct));
            if (job is null) return "Error: " + (error ?? "can't start another background agent.");
            return $"Started background agent {job.Id} “{description}”. It works while you continue — you'll be told automatically when it finishes; agent_status {{\"id\":\"{job.Id}\",\"wait_seconds\":120}} waits for it, agent_stop stops it.";
        }

        var (ok, report) = await RunAsync(prompt, context, cancellationToken).ConfigureAwait(false);
        return ok ? $"Subagent '{description}' finished.\n\n{report}" : $"Subagent '{description}' failed: {report}";
    }

    /// <summary>Run a subagent to completion; its report (or the failure).</summary>
    public static async Task<(bool Ok, string Report)> RunAsync(string prompt, ToolContext context, CancellationToken cancellationToken)
    {
        // Same capabilities as the parent, minus agent itself: start from the parent's registry and drop
        // agent. That way a process the parent launched is readable here, and a "debug this game
        // window" task can screenshot and drive without the parent relaying every observation.
        var subRegistry = context.Registry.Removing(ToolName, AgentStatusTool.ToolName, AgentStopTool.ToolName, QueueAddTool.ToolName);
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
            Vault = context.Vault,
            VaultGrants = context.VaultGrants,
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
            return result.FinalText.Length > 0
                ? (true, result.FinalText)
                : (true, "Subagent completed without a final report. Its tool work (if any) has already been applied in the workspace.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
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
