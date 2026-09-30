using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - agent (subagents)
//
// Spawns a child Engine that works a self-contained task in a bounded loop, then returns a final
// report. The child inherits the parent's permission gate, shell, workspace and policy; it does NOT
// inherit the agent tools themselves (no nested subagents). What it *is* depends on its agent type
// (see AgentDefinition): a read-only explorer, a planner, a reviewer, an implementer, or one the user
// defined. Where it *runs* depends on the fleet: with several model servers, subagents go to the
// workers (least loaded first) and fall back to the primary, and a server that fails hands the task to
// another one.

public sealed class AgentTool : IToolExecutor
{
    public const string ToolName = "agent";
    public string Name => ToolName;
    public ToolSpec Spec { get; }

    /// <summary>A subagent's default step budget; agent types may set their own.</summary>
    private const int DefaultMaxSteps = 30;
    /// <summary>How many times a task moves to another server after its server fails.</summary>
    private const int MaxFailovers = 2;

    public AgentTool(AgentCatalog? catalog = null) => Spec = SpecFor(catalog ?? AgentCatalog.Default);

    public static ToolSpec SpecFor(AgentCatalog catalog) => new(ToolName,
        "Spawn a subagent to work a self-contained task (e.g. 'find every usage of X and report the call sites'). It works autonomously in a bounded loop and returns a final report. Prefer this over doing long exploration yourself when the task is well-scoped. " +
        "Pick agent_type for a specialist — " + catalog.Describe() + ". Omit it for the general worker. " +
        "Several agent calls in one turn run at the same time. Set run_in_background to true to launch it in the background and keep working: check on it with agent_status (you're also told automatically when one finishes) and stop one with agent_stop. To fan out many independent tasks in one go, use `delegate`.",
        """{"type":"object","properties":{"description":{"type":"string","description":"What to name this subagent"},"prompt":{"type":"string","description":"The complete task for the subagent. It cannot ask you questions and cannot see this conversation — everything it needs goes here."},"agent_type":{"type":"string","description":"The kind of subagent (default: general)"},"run_in_background":{"type":"boolean","description":"Start it in the background and return at once (default false: wait for its report)"}},"required":["description","prompt"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var description = JsonArgs.String(args, "description") ?? "subagent";
        var prompt = JsonArgs.String(args, "prompt") ?? "";
        if (prompt.Length == 0) return "Error: prompt is required.";
        if (context.Depth >= 1) return "Error: nested subagents are not allowed (depth limit).";
        var catalog = context.AgentTypes ?? AgentCatalog.Default;
        var typeName = JsonArgs.String(args, "agent_type");
        if (catalog.Find(typeName) is not { } definition)
            return $"Error: unknown agent_type '{typeName}'. Available: {catalog.Describe()}.";

        if (JsonArgs.Bool(args, "run_in_background", false))
        {
            if (context.BackgroundAgents is not { } pool)
                return "Error: background subagents aren't available here; call agent without run_in_background.";
            // The background run belongs to the pool (Stop / agent_stop cancel it), not to this call.
            var (job, error) = pool.Launch(description, async ct =>
            {
                var outcome = await RunCoreAsync(prompt, description, definition, background: true, context, ct).ConfigureAwait(false);
                return (outcome.Ok, outcome.Report);
            });
            if (job is null) return "Error: " + (error ?? "can't start another background agent.");
            return $"Started background agent {job.Id} “{description}”. It works while you continue — you'll be told automatically when it finishes; agent_status {{\"id\":\"{job.Id}\",\"wait_seconds\":120}} waits for it, agent_stop stops it.";
        }

        var result = await RunCoreAsync(prompt, description, definition, background: false, context, cancellationToken).ConfigureAwait(false);
        return Present(description, result);
    }

    /// <summary>The report as the parent sees it. The first line keeps a fixed shape; a specialist type or a
    /// named server is added on a line of its own.</summary>
    internal static string Present(string description, SubagentOutcome outcome)
    {
        var head = outcome.Ok ? $"Subagent '{description}' finished." : $"Subagent '{description}' failed: {outcome.Report}";
        var details = outcome.Details();
        var text = new StringBuilder(head);
        if (details.Length > 0) text.Append('\n').Append(details);
        if (outcome.Ok) text.Append("\n\n").Append(outcome.Report);
        return text.ToString();
    }

    /// <summary>Run a subagent to completion; its report (or the failure).</summary>
    public static async Task<(bool Ok, string Report)> RunAsync(string prompt, ToolContext context, CancellationToken cancellationToken)
    {
        var outcome = await RunCoreAsync(prompt, "subagent", AgentDefinition.BuiltIn[0], background: false, context, cancellationToken)
            .ConfigureAwait(false);
        return (outcome.Ok, outcome.Report);
    }

    /// <summary>The whole life of one subagent: wait for a server, run, fail over if the server dies,
    /// and keep the roster current.</summary>
    internal static async Task<SubagentOutcome> RunCoreAsync(string prompt, string description, AgentDefinition definition,
                                                             bool background, ToolContext context, CancellationToken cancellationToken)
    {
        var roster = context.Roster;
        var runId = roster?.Begin(description, definition.Name, background);
        var clock = Stopwatch.StartNew();
        var failovers = 0;
        string? firstServer = null;
        // The report names the server only when there was a choice of servers; a lone one is just "the model".
        var choice = context.Fleet is { } known && known.Servers.Count > 1;
        string? Named(string? label) => choice ? label : null;
        var task = prompt;
        var trace = new AttemptTrace();
        try
        {
            while (true)
            {
                FleetLease? lease = null;
                FleetTarget target;
                if (context.Fleet is { } fleet)
                {
                    if (runId is not null) roster!.Note(runId, "waiting for a free server");
                    try
                    {
                        lease = await fleet.AcquireAsync(definition.Server, cancellationToken).ConfigureAwait(false);
                    }
                    catch (FleetUnavailableException unavailable)
                    {
                        if (runId is not null) roster!.Finish(runId, AgentRunStatus.Failed, unavailable.Message);
                        return new SubagentOutcome(false, unavailable.Message, definition.Name, Named(firstServer), clock.Elapsed) { Failovers = failovers };
                    }
                    target = lease.Target;
                    firstServer ??= lease.Label;
                    if (runId is not null && choice) roster!.SetServer(runId, lease.Label);
                }
                else
                {
                    target = new FleetTarget(context.Client, context.Model, context.ContextWindow);
                }

                try
                {
                    // With one server there is nowhere to fail over to: wait it out as long as the main agent would.
                    var (ok, report) = await RunEngineAsync(prompt, definition, target, context, runId, boundedRetries: choice, trace,
                        cancellationToken).ConfigureAwait(false);
                    lease?.Dispose();
                    if (runId is not null) roster!.Finish(runId, ok ? AgentRunStatus.Done : AgentRunStatus.Failed, report);
                    return new SubagentOutcome(ok, report, definition.Name, Named(lease?.Label ?? firstServer), clock.Elapsed) { Failovers = failovers };
                }
                catch (OperationCanceledException)
                {
                    lease?.Abandon();
                    lease?.Dispose();
                    throw;
                }
                catch (Exception error) when (choice && lease is not null && AgentFleet.IsServerFault(error) && failovers < MaxFailovers)
                {
                    // The server, not the task, was the problem: another one may not have it.
                    lease.Fail(error);
                    lease.Dispose();
                    failovers++;
                    // The task starts again from the top on the next server — after work that already changed things, it
                    // has to look before it repeats a step (a migration, a commit, a deploy).
                    if (trace.Mutated) prompt = InterruptedNote + task;
                    if (runId is not null) roster!.Note(runId, $"{lease.Label} failed — trying another server");
                }
                catch (Exception error)
                {
                    lease?.Fail(error);
                    lease?.Dispose();
                    if (runId is not null) roster!.Finish(runId, AgentRunStatus.Failed, error.Message);
                    return new SubagentOutcome(false, error.Message, definition.Name, Named(lease?.Label ?? firstServer), clock.Elapsed) { Failovers = failovers };
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (runId is not null) roster!.Finish(runId, AgentRunStatus.Stopped, "Stopped before it finished.");
            throw;
        }
    }

    private const string InterruptedNote =
        "[Note: an earlier attempt at this task was cut short when its model server failed, after it had already changed things. " +
        "Look at the current state of the workspace first and do not repeat steps that are already done.]\n\n";

    /// <summary>What an attempt did that a retry must know about.</summary>
    private sealed class AttemptTrace
    {
        public volatile bool Mutated;
    }

    private static async Task<(bool Ok, string Report)> RunEngineAsync(string prompt, AgentDefinition definition, FleetTarget target,
                                                                        ToolContext context, string? runId, bool boundedRetries,
                                                                        AttemptTrace trace, CancellationToken cancellationToken)
    {
        // Same capabilities as the parent, minus the agent tools themselves: start from the parent's registry
        // and drop them. That way a process the parent launched is readable here, and a "debug this game
        // window" task can screenshot and drive without the parent relaying every observation.
        var registry = context.Registry.Removing(ToolName, DelegateTool.ToolName, AgentStatusTool.ToolName, AgentStopTool.ToolName,
            QueueAddTool.ToolName, GoalCompleteTool.ToolName, GoalBlockedTool.ToolName);
        if (definition.Tools is { Count: > 0 } allowed) registry = registry.Only(allowed);
        if (definition.DisallowedTools is { Count: > 0 } denied) registry = registry.Removing([.. denied]);

        var window = target.ContextWindow ?? context.ContextWindow;
        var config = new EngineConfig(target.Model)
        {
            MaxIterations = definition.MaxSteps ?? DefaultMaxSteps,
            ToolTimeout = TimeSpan.FromSeconds(300),
            ContextWindow = window,
            Thinking = context.Thinking,
            Shell = context.Shell,
            Depth = context.Depth + 1,
            // The leased server's own settings when it has them (a worker without vision must not be sent screenshots).
            VisionEnabled = target.Vision ?? context.VisionEnabled,
            Temperature = target.Temperature ?? context.Temperature,
            MaxOutputTokens = target.MaxOutputTokens ?? context.MaxOutputTokens,
            // Out of steps, a subagent still owes its caller a report.
            WrapUpAtLimit = true,
            // On a fleet, a server that keeps failing should hand the task on, not be waited on for ever:
            // two quick retries, then the task moves to another server.
            Retry = boundedRetries
                ? (context.Retry ?? RetryPolicy.Standard with { Delay = n => TimeSpan.FromSeconds(Math.Min(n, 2)) }) with { MaxFailures = 2 }
                : context.Retry ?? RetryPolicy.Standard,
        };
        var client = target.Client;
        var engine = new Engine(client, registry, SubagentPrompt(context.Workspace, context.Policy.Preset, context.Shell, definition),
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
                if (window is not { } limit
                    || Compaction.MakePlan(used, limit, messages, allowAssistantBoundary: true) is not { } plan)
                    return messages;
                var summary = await Compaction.SummarizeAsync(client, plan, model: target.Model,
                    cancellationToken: ct).ConfigureAwait(false);
                if (summary is null) return messages;
                return [LlmMessage.SystemText(Compaction.SummaryHeader + summary), .. plan.ToKeep];
            },
        };

        void Sink(EngineEvent ev)
        {
            // Only a call that really ran can have changed anything (not one that was denied, refused or left unrun).
            if (ev is EngineEvent.ToolRunning { Name: var tool } && !ToolCache.IsReadOnlyTool(tool)) trace.Mutated = true;
            if (runId is null || context.Roster is not { } roster) return;
            switch (ev)
            {
                case EngineEvent.ToolStarted started:
                    roster.Note(runId, $"{started.Name}: {TextUtil.Prefix(started.Preview, 60)}", step: true);
                    break;
                case EngineEvent.Retrying retrying:
                    roster.Note(runId, $"server not answering ({retrying.Reason}) — retrying");
                    break;
                case EngineEvent.AssistantMessage { Calls.Count: 0 }:
                    roster.Note(runId, "writing the report");
                    break;
            }
        }

        var result = await engine.RunAsync([], prompt, sink: Sink, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Stalled)
            return (false, result.LastReplyText);
        var text = result.FinalText.Trim().Length > 0 ? result.FinalText : result.LastReplyText;
        return text.Trim().Length > 0
            ? (true, text)
            : (true, "Subagent completed without a final report. Its tool work (if any) has already been applied in the workspace.");
    }

    public static string SubagentPrompt(string workspace, PermissionPreset preset, AgentShell shell, AgentDefinition? definition = null)
    {
        var text = "You are a focused subagent. Work autonomously: read and run tools as needed to answer the task in the user message. " +
                   "Do not ask questions — make reasonable assumptions and state them in your final answer. Keep your final answer " +
                   "concise and factual; it is returned to the main agent, not to a human.\n" +
                   $"Workspace: {workspace}\n" +
                   $"Platform: {PlatformInfo.Description}; run_shell_command uses {shell.DisplayName}.\n" +
                   $"Permission preset: {preset.RawValue()}. If a tool is denied, do not retry the same action; note the limitation in your report instead.";
        if (definition is { Prompt.Length: > 0 }) text += "\n\n" + definition.Prompt;
        return text;
    }
}

/// <summary>How a subagent's run ended.</summary>
internal sealed record SubagentOutcome(bool Ok, string Report, string AgentType, string? Server, TimeSpan Elapsed)
{
    public int Failovers { get; init; }

    /// <summary>"(explore · on spark-2 · 34s)" — empty for a plain general agent on the only server.</summary>
    public string Details()
    {
        var parts = new List<string>();
        if (!string.Equals(AgentType, AgentDefinition.General, StringComparison.OrdinalIgnoreCase)) parts.Add(AgentType + " agent");
        if (Server is not null) parts.Add("on " + Server);
        if (Failovers > 0) parts.Add($"after {Failovers} server failure{(Failovers == 1 ? "" : "s")}");
        if (parts.Count > 0) parts.Add(Elapsed.FormattedDuration());
        return parts.Count == 0 ? "" : "(" + string.Join(" · ", parts) + ")";
    }
}

// MARK: - delegate (parallel fan-out)

/// <summary>Runs several independent subtasks at once, each in its own subagent, and returns every report
/// together. With more than one model server the tasks spread across them.</summary>
public sealed class DelegateTool : IToolExecutor
{
    public const string ToolName = "delegate";
    public string Name => ToolName;
    public ToolSpec Spec { get; }

    private const int MaxTasks = 12;
    private const int DefaultParallel = 6;
    private const int ReportChars = 8_000;

    public DelegateTool(AgentCatalog? catalog = null)
    {
        var types = (catalog ?? AgentCatalog.Default).Describe();
        Spec = new ToolSpec(ToolName,
            "Run several independent subtasks at the same time, each by its own subagent, and get all their reports back together. Use it to fan work out — investigate three modules, review four files, implement three unrelated changes — instead of doing them one after another. Subtasks cannot talk to each other or see this conversation, so each prompt must be complete; make sure they don't edit the same files. " +
            "Give each an agent_type: " + types + ". When several model servers are configured the tasks are spread across them.",
            """{"type":"object","properties":{"tasks":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"object","properties":{"description":{"type":"string","description":"A short name for the subtask"},"prompt":{"type":"string","description":"The complete task"},"agent_type":{"type":"string","description":"The kind of subagent (default: general)"}},"required":["description","prompt"]},"description":"The subtasks to run in parallel"},"max_parallel":{"type":"integer","description":"At most this many at once (default 6)"}},"required":["tasks"]}""");
    }

    private sealed record Task1(string Description, string Prompt, AgentDefinition Definition);

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Depth >= 1) return "Error: nested subagents are not allowed (depth limit).";
        var args = JsonArgs.Object(arguments);
        JsonArray? array = args["tasks"] as JsonArray;
        // Some models send the list as a JSON string.
        if (array is null && JsonArgs.String(args, "tasks") is { } encoded)
        {
            try { array = JsonNode.Parse(encoded) as JsonArray; } catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException) { }
        }
        if (array is null || array.Count == 0) return "Error: tasks must be a non-empty array of {description, prompt} objects.";
        if (array.Count > MaxTasks) return $"Error: at most {MaxTasks} tasks at once; split the rest into a second delegate call.";

        var catalog = context.AgentTypes ?? AgentCatalog.Default;
        var tasks = new List<Task1>();
        var index = 0;
        foreach (var node in array)
        {
            index++;
            if (node is not JsonObject item) return $"Error: task {index} is not an object.";
            var prompt = (JsonArgs.String(item, "prompt") ?? "").Trim();
            if (prompt.Length == 0) return $"Error: task {index} has no prompt.";
            var typeName = JsonArgs.String(item, "agent_type");
            if (catalog.Find(typeName) is not { } definition)
                return $"Error: task {index} has an unknown agent_type '{typeName}'. Available: {catalog.Describe()}.";
            var description = (JsonArgs.String(item, "description") ?? "").Trim();
            tasks.Add(new Task1(description.Length > 0 ? description : $"task {index}", prompt, definition));
        }

        var parallel = Math.Clamp(JsonArgs.Int(args, "max_parallel", DefaultParallel), 1, MaxTasks);
        using var gate = new SemaphoreSlim(parallel);
        var runs = tasks.Select(async task =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await AgentTool.RunCoreAsync(task.Prompt, task.Description, task.Definition, background: false, context,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        var outcomes = await Task.WhenAll(runs).ConfigureAwait(false);

        var done = outcomes.Count(o => o.Ok);
        var text = new StringBuilder($"Delegated {tasks.Count} task{(tasks.Count == 1 ? "" : "s")}: {done} finished, {tasks.Count - done} failed.\n");
        for (var i = 0; i < tasks.Count; i++)
        {
            var outcome = outcomes[i];
            var details = outcome.Details();
            text.Append($"\n## {i + 1}. {tasks[i].Description} — {(outcome.Ok ? "finished" : "failed")}{(details.Length > 0 ? " " + details : "")}\n");
            var report = outcome.Report;
            text.Append(report.Length > ReportChars ? report[..ReportChars] + "\n[… report truncated]" : report).Append('\n');
        }
        return text.ToString().TrimEnd();
    }
}
