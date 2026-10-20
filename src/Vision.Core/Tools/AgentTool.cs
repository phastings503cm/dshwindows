using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - agent (subagents)
//
// Spawns a child Engine that works a self-contained task in a bounded loop, then returns a final
// report. The child inherits the parent's permission gate, shell, workspace and policy; it does NOT
// inherit the agent tools themselves (no nested subagents). What it *is* depends on its agent type
// (see AgentDefinition): a read-only explorer, a planner, a reviewer, an implementer, or one the user
// defined. Where it *runs* is the chat's pool (SubagentPool): the main agent may name a server with
// `server` (waited for up to 2 minutes when busy), else the least busy one runs it, the chat's own model
// as overflow; a server that fails hands the task to another one. A server that runs its own agent
// (Claude Code) works the task as one job in the chat's folder. A task marked sensitive runs only on a
// private server.

/// <summary>The words the main agent is told about subagent servers, in the agent and delegate tools (the same as the Mac).</summary>
public static class SubagentServers
{
    /// <summary>What the model is told about servers that aren't private.</summary>
    public const string Guidance =
        "Servers that aren't private must not receive sensitive data. Describe their tasks in general terms — no names, credentials, " +
        "personal or client details; the app also masks what it detects.";

    /// <summary>The description of the <c>sensitive</c> parameter.</summary>
    public const string SensitiveParameter =
        "Set true when the task needs sensitive data (personal data, credentials, client details). It then runs only on a private server.";

    /// <summary>The description of the <c>server</c> parameter.</summary>
    public const string ServerParameter =
        "Where this task runs: an id from the subagent servers listed in the agent tool, or \"primary\" for this chat's own model. " +
        "Leave it out to let Vision pick the least busy. A chosen server that is busy is waited for up to 2 minutes; after that, " +
        "or if it can't take the task, another server runs it and the report says so.";
}

public sealed class AgentTool : IToolExecutor
{
    public const string ToolName = "agent";
    public string Name => ToolName;
    public ToolSpec Spec { get; }

    /// <summary>A subagent's default step budget; agent types may set their own.</summary>
    internal const int DefaultMaxSteps = 30;
    /// <summary>How many times a task moves to another server after its server fails.</summary>
    private const int MaxFailovers = 2;

    /// <param name="pool">The chat's subagent servers, listed in the description (null = not said).</param>
    public AgentTool(AgentCatalog? catalog = null, SubagentPool? pool = null) =>
        Spec = SpecFor(catalog ?? AgentCatalog.Default, pool);

    /// <summary>The description: what a subagent is, the agent types, and — with servers besides the chat's own model — the
    /// stable catalogue of them (<see cref="SubagentPool.Describe"/>; how busy they are comes back in results instead).</summary>
    public static ToolSpec SpecFor(AgentCatalog catalog, SubagentPool? pool = null) => new(ToolName,
        "Spawn a subagent to work a self-contained task (e.g. 'find every usage of X and report the call sites'). It works autonomously in a bounded loop and returns a final report. Prefer this over doing long exploration yourself when the task is well-scoped. " +
        "Pick agent_type for a specialist — " + catalog.Describe() + ". Omit it for the general worker. " +
        "Several agent calls in one turn run at the same time. Set run_in_background to true to launch it in the background and keep working: check on it with agent_status (you're also told automatically when one finishes) and stop one with agent_stop. To fan out many independent tasks in one go, use `delegate`." +
        (SubagentPool.Describe(pool) is { } catalogue ? "\n\n" + catalogue : ""),
        Parameters);

    /// <summary>The parameters (the same as the Mac's; compared structurally by the shared fixture).</summary>
    public static readonly string Parameters =
        """{"type":"object","properties":{"description":{"type":"string","description":"What to name this subagent"},"prompt":{"type":"string","description":"The complete task for the subagent. It cannot ask you questions and cannot see this conversation — everything it needs goes here."},"agent_type":{"type":"string","description":"The kind of subagent (default: general)"},"server":{"type":"string","description":""" + JsonArgs.Quote(SubagentServers.ServerParameter) + """},"run_in_background":{"type":"boolean","description":"Start it in the background and return at once (default false: wait for its report)"},"step":{"type":"string","description":""" + JsonArgs.Quote(StepParameter) + """},"sensitive":{"type":"boolean","description":""" + JsonArgs.Quote(SubagentServers.SensitiveParameter) + """}},"required":["description","prompt"]}""";

    /// <summary>The <c>step</c> argument of agent and delegate's tasks: which plan step the subagent works, so the plan, the
    /// Now card and the activity menu show it there (the same words on the Mac).</summary>
    public const string StepParameter =
        "Which step of your plan (todo_write) this subagent works: its number in the plan (1 = the first) or its text. Shown on that step.";

    /// <summary>The step argument as text (models send a number or a string).</summary>
    internal static string? StepArgument(JsonObject args) => args["step"] switch
    {
        JsonValue v when v.TryGetValue<string>(out var text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        JsonValue v when v.TryGetValue<double>(out var number) && double.IsFinite(number) =>
            ((long)Math.Round(number)).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null,
    };

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
        // Where the main agent wants it: checked before anything starts.
        var choice = ServerChoice(JsonArgs.String(args, "server"), context);
        if (choice?.Error is { } wrongServer) return wrongServer;
        var pin = choice?.Entry;
        var sensitive = JsonArgs.Bool(args, "sensitive", false);
        var step = StepArgument(args);

        if (JsonArgs.Bool(args, "run_in_background", false))
        {
            if (context.BackgroundAgents is not { } pool)
                return "Error: background subagents aren't available here; call agent without run_in_background.";
            // The background run belongs to the pool (Stop / agent_stop cancel it), not to this call.
            var (job, error) = pool.Launch(description, async ct =>
            {
                var outcome = await RunCoreAsync(prompt, description, definition, background: true, context, ct, sensitive, step, pin).ConfigureAwait(false);
                // A task that ran elsewhere than the main agent picked says so in front of its report.
                return (outcome.Ok, outcome.FallbackLine() is { } moved ? moved + "\n\n" + outcome.Report : outcome.Report);
            });
            if (job is null) return "Error: " + (error ?? "can't start another background agent.");
            return WithLoad($"Started background agent {job.Id} “{description}”. It works while you continue — you'll be told automatically when it finishes; agent_status {{\"id\":\"{job.Id}\",\"wait_seconds\":120}} waits for it, agent_stop stops it.", context);
        }

        var result = await RunCoreAsync(prompt, description, definition, background: false, context, cancellationToken, sensitive, step, pin).ConfigureAwait(false);
        return WithLoad(Present(description, result), context);
    }

    /// <summary>What a <c>server</c> argument names in this chat (null when it isn't given).</summary>
    internal static SubagentPool.Choice? ServerChoice(string? raw, ToolContext context)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var pool = context.FleetPool ?? SubagentPool.From(context.Fleet?.Servers ?? []);
        return SubagentPool.Match(raw, pool);
    }

    /// <summary><paramref name="text"/>, then — after a blank line — how busy each of the chat's subagent servers is now (when it
    /// has any besides its own model). Results only: descriptions stay stable for the prompt cache.</summary>
    internal static string WithLoad(string text, ToolContext context) => LoadLine(context) is { } line ? text + "\n\n" + line : text;

    /// <summary>"Subagent servers now: dgx-spark 1/2 busy · primary 0/4 busy." (null without servers besides the chat's own).</summary>
    internal static string? LoadLine(ToolContext context) =>
        context.Fleet is { } fleet && context.FleetPool is { HasWorkers: true } pool
            ? SubagentPool.LoadLine(pool, fleet.Loads(pool, context.FleetHome))
            : null;

    /// <summary>The report as the parent sees it. The first line keeps a fixed shape; a specialist type or a named server is
    /// added on a line of its own, and a task that ran elsewhere than the main agent picked says so.</summary>
    internal static string Present(string description, SubagentOutcome outcome)
    {
        var head = outcome.Ok ? $"Subagent '{description}' finished." : $"Subagent '{description}' failed: {outcome.Report}";
        var details = outcome.Details();
        var text = new StringBuilder(head);
        if (details.Length > 0) text.Append('\n').Append(details);
        if (outcome.FallbackLine() is { } moved) text.Append('\n').Append(moved);
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
    /// and keep the roster current. A <paramref name="sensitive"/> task runs only on a private server; <paramref name="pin"/>
    /// is the server the main agent picked (<c>server</c>).</summary>
    internal static async Task<SubagentOutcome> RunCoreAsync(string prompt, string description, AgentDefinition definition,
                                                             bool background, ToolContext context, CancellationToken cancellationToken,
                                                             bool sensitive = false, string? step = null, SubagentPool.Entry? pin = null)
    {
        var roster = context.Roster;
        var runId = roster?.Begin(description, definition.Name, background, prompt, step);
        // This subagent's own machine state (see AgentScope); the caller's comes back when this method returns.
        AgentScope.Current = runId ?? Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        var failovers = 0;
        string? firstServer = null;
        // The report names the server only when there was a choice of servers; a lone one is just "the model".
        var poolIds = context.FleetPool?.WorkerRouteIds;
        var choice = context.Fleet is { } known && known.ChoiceCount(context.FleetHome, poolIds) > 1;
        string? Named(string? handle) => choice ? handle : null;
        // The pinned server's fleet id: the chat's own server for "primary".
        var pinnedId = pin is null ? null
            : pin.IsPrimary ? context.FleetHome?.Id ?? context.Fleet?.Servers.FirstOrDefault(s => s.IsPrimary)?.Id ?? pin.RouteId
            : pin.RouteId;
        var task = prompt;
        string? movedNote = null;
        (PlacementReason? Reason, string? Note) placement = (null, null);
        var trace = new AttemptTrace();
        SubagentOutcome Outcome(bool ok, string report, string? server) =>
            new(ok, report, definition.Name, Named(server), clock.Elapsed)
            {
                Failovers = failovers, Reason = placement.Reason, Note = placement.Note,
                Pinned = placement.Reason == PlacementReason.Fallback ? pin?.Id : null,
            };
        try
        {
            while (true)
            {
                FleetLease? lease = null;
                FleetTarget target;
                if (context.Fleet is { } fleet)
                {
                    if (runId is not null)
                        roster!.Note(runId, pin is not null ? $"waiting for {pin.Id}, which the primary picked (up to {SubagentPlacement.WaitWords(fleet.PinWait)})" : "waiting for a free server");
                    try
                    {
                        lease = await fleet.AcquireAsync(new FleetRequest
                        {
                            Pinned = pinnedId, Preferred = definition.Server, Sensitive = sensitive, Unattended = context.Unattended,
                            Home = context.FleetHome, Pool = poolIds, SensitiveChat = context.SensitiveChat,
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (FleetUnavailableException unavailable)
                    {
                        if (runId is not null) roster!.Finish(runId, AgentRunStatus.Failed, unavailable.Message);
                        return Outcome(false, unavailable.Message, firstServer);
                    }
                    target = lease.Target;
                    firstServer ??= lease.Handle;
                    if (runId is not null && choice) roster!.SetServer(runId, lease.Label);
                    placement = Placement(lease, movedNote, choice || pin is not null);
                    if (runId is not null)
                        roster!.SetPlacement(runId, lease.Label, target.Model, reason: placement.Reason?.Code(),
                            note: PlacementNote(definition, lease, fleet, placement.Note));
                }
                else
                {
                    // No fleet: it would run on the chat's own model — for a sensitive task, only if that is private (as a
                    // Sensitive chat counts it: own hardware, Guardian, Bedrock in your account).
                    if (sensitive && !(context.Client is IProviderClient provider && SensitivityPolicy.IsPrivate(provider.Profile)))
                    {
                        if (runId is not null) roster!.Finish(runId, AgentRunStatus.Failed, NoPrivateServerException.Text);
                        return Outcome(false, NoPrivateServerException.Text, null);
                    }
                    target = new FleetTarget(context.Client, context.Model, context.ContextWindow);
                    if (runId is not null) roster!.SetPlacement(runId, ThisChatsModel, context.Model);
                }

                try
                {
                    var (ok, report) = target.External is { } external
                        // An engine of its own (Claude Code): one job in the chat's folder, its report back like any.
                        ? await RunExternalAsync(external, prompt, definition, target, context, runId, trace, cancellationToken).ConfigureAwait(false)
                        // With one server there is nowhere to fail over to: wait it out as long as the main agent would.
                        : await RunEngineAsync(prompt, definition, target, context, runId, boundedRetries: choice, trace, cancellationToken).ConfigureAwait(false);
                    lease?.Dispose();
                    if (runId is not null) roster!.Finish(runId, ok ? AgentRunStatus.Done : AgentRunStatus.Failed, report);
                    return Outcome(ok, report, lease?.Handle ?? firstServer);
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
                    movedNote = SubagentPlacement.MovedNote(lease.Handle, error.Message);
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
                    return Outcome(false, error.Message, lease?.Handle ?? firstServer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (runId is not null) roster!.Finish(runId, AgentRunStatus.Stopped, "Stopped before it finished.");
            throw;
        }
    }

    /// <summary>Where a subagent runs when there is no fleet: the chat's own client.</summary>
    public const string ThisChatsModel = "this chat's model";

    /// <summary>How the lease's server was chosen, as the roster and the report say it: after a failover, a fallback "moved
    /// here"; with no choice of servers (and no pick), nothing.</summary>
    internal static (PlacementReason? Reason, string? Note) Placement(FleetLease lease, string? movedNote, bool choice)
    {
        if (movedNote is not null) return (PlacementReason.Fallback, movedNote);
        if (!choice) return (null, null);
        return (lease.Reason, lease.Reason == PlacementReason.Fallback ? lease.Note : null);
    }

    /// <summary>Why a subagent runs where it does when that isn't the obvious place: the main agent's pick or its agent file
    /// asked for a server it didn't get, or servers tried first couldn't take it. Null when it runs where it was meant to.</summary>
    internal static string? PlacementNote(AgentDefinition definition, FleetLease lease, AgentFleet fleet, string? note = null)
    {
        var notes = new List<string>();
        if (note is not null)
        {
            notes.Add(note);
        }
        else if (definition.Server is { Length: > 0 } wanted && !AgentFleet.Matches(lease.Server, wanted))
        {
            notes.Add(fleet.Servers.Any(s => AgentFleet.Matches(s, wanted)) ? SubagentPlacement.AgentFileBusy(wanted) : SubagentPlacement.AgentFileMissing(wanted));
        }
        if (lease.Skipped.Count > 0) notes.Add("tried first: " + string.Join("; ", lease.Skipped));
        return notes.Count == 0 ? null : string.Join(" · ", notes);
    }

    /// <summary>A task on a server that runs its own agent (Claude Code): one job in the chat's folder, under the chat's
    /// permissions (Plan only for a read-only type), its steps on the roster, its report back like any subagent's.</summary>
    private static async Task<(bool Ok, string Report)> RunExternalAsync(IExternalSubagent external, string prompt, AgentDefinition definition,
                                                                          FleetTarget target, ToolContext context, string? runId,
                                                                          AttemptTrace trace, CancellationToken cancellationToken)
    {
        var note = ClaudeCodeSubagent.Note;
        if (definition.Prompt is { Length: > 0 } typePrompt) note += "\n\n" + typePrompt;
        var roster = context.Roster;
        var job = new ExternalSubagentJob
        {
            Prompt = prompt,
            SystemNote = note,
            Workspace = context.Workspace,
            Preset = definition.IsReadOnly ? PermissionPreset.Plan : context.Policy.Preset,
            ReadOnly = definition.IsReadOnly,
            MaxSteps = definition.MaxSteps ?? DefaultMaxSteps,
            Activity = (text, step) =>
            {
                if (runId is not null) roster?.Note(runId, text, step);
            },
            NoteMutation = () => trace.Mutated = true,
            RequestPermission = context.RequestPermission,
        };
        var result = await external.RunAsync(job, cancellationToken).ConfigureAwait(false);
        if (result.Mutated) trace.Mutated = true;
        if (runId is not null && roster is not null)
        {
            roster.AddTokens(runId, result.PromptTokens, result.CompletionTokens);
            if (result.CostUsd is { } cost)
                roster.Note(runId, "≈ " + (cost < 0.01 ? "under $0.01" : "$" + cost.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))
                                   + " at API prices (paid by your Claude plan)");
        }
        return (result.Ok, result.Report);
    }

    /// <summary>The model settings a subagent sends: a target on another route uses that route's own (null = its provider's
    /// default) — never the parent chat's temperature, output limit or thinking level; the primary (or no fleet) shares the
    /// parent's.</summary>
    internal static (double? Temperature, int? MaxOutputTokens, ThinkingLevel? Thinking) Settings(FleetTarget target, ToolContext context) =>
        target.OwnSettings
            ? (target.Temperature, target.MaxOutputTokens, target.Thinking)
            : (target.Temperature ?? context.Temperature, target.MaxOutputTokens ?? context.MaxOutputTokens, context.Thinking);

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
        var settings = Settings(target, context);
        var config = new EngineConfig(target.Model)
        {
            MaxIterations = definition.MaxSteps ?? DefaultMaxSteps,
            ToolTimeout = TimeSpan.FromSeconds(300),
            ContextWindow = window,
            Thinking = settings.Thinking,
            Shell = context.Shell,
            Depth = context.Depth + 1,
            // The leased server's own settings when it has them (a worker without vision must not be sent screenshots, a Kimi
            // worker not the main route's temperature).
            VisionEnabled = target.Vision ?? context.VisionEnabled,
            Temperature = settings.Temperature,
            MaxOutputTokens = settings.MaxOutputTokens,
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
        if (runId is not null && result.Usage is { } usage) context.Roster?.AddTokens(runId, usage.PromptTokens, usage.CompletionTokens);
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
    /// <summary>How its server was chosen (null when there was no choice).</summary>
    public PlacementReason? Reason { get; init; }
    /// <summary>Why it ran where it did, for a fallback.</summary>
    public string? Note { get; init; }
    /// <summary>The server the main agent picked, when it ran somewhere else.</summary>
    public string? Pinned { get; init; }

    /// <summary>"Ran on bedrock instead of spark-2: spark-2 stayed busy for 2 minutes." — when it didn't run where the main
    /// agent picked.</summary>
    public string? FallbackLine() =>
        Reason == PlacementReason.Fallback && Pinned is { } pinned && Server is { } server && server != pinned && Note is { } note
            ? $"Ran on {server} instead of {pinned}: {note}."
            : null;

    /// <summary>"(explore agent · on spark-2 · 34s)" — the server as the main agent names it; empty for a plain general agent on
    /// the only server.</summary>
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
/// together. With more than one model server the tasks spread across them; a task (or all of them) may name its server.</summary>
public sealed class DelegateTool : IToolExecutor
{
    public const string ToolName = "delegate";
    public string Name => ToolName;
    public ToolSpec Spec { get; }

    private const int MaxTasks = 12;
    private const int DefaultParallel = 6;
    private const int ReportChars = 8_000;

    /// <summary>The description names the agent types; the servers are listed once, in the agent tool.</summary>
    public DelegateTool(AgentCatalog? catalog = null) =>
        Spec = new ToolSpec(ToolName,
            "Run several independent subtasks at the same time, each by its own subagent, and get all their reports back together. Use it to fan work out — investigate three modules, review four files, implement three unrelated changes — instead of doing them one after another. Subtasks cannot talk to each other or see this conversation, so each prompt must be complete; make sure they don't edit the same files. " +
            "Give each an agent_type: " + (catalog ?? AgentCatalog.Default).Describe() + ". Without a server, tasks spread across the subagent servers by load; give a task `server` (an id listed in the agent tool) to choose where it runs.",
            Parameters);

    /// <summary>The parameters (the same as the Mac's; compared structurally by the shared fixture).</summary>
    public static readonly string Parameters =
        """{"type":"object","properties":{"tasks":{"type":"array","minItems":1,"maxItems":12,"items":{"type":"object","properties":{"description":{"type":"string","description":"A short name for the subtask"},"prompt":{"type":"string","description":"The complete task"},"agent_type":{"type":"string","description":"The kind of subagent (default: general)"},"server":{"type":"string","description":""" + JsonArgs.Quote(SubagentServers.ServerParameter + " (This task only.)") + """},"step":{"type":"string","description":""" + JsonArgs.Quote(AgentTool.StepParameter) + """},"sensitive":{"type":"boolean","description":""" + JsonArgs.Quote(SubagentServers.SensitiveParameter + " (This task only.)") + """}},"required":["description","prompt"]},"description":"The subtasks to run in parallel"},"max_parallel":{"type":"integer","description":"At most this many at once (default 6)"},"server":{"type":"string","description":""" + JsonArgs.Quote(SubagentServers.ServerParameter + " (Every task without its own.)") + """},"sensitive":{"type":"boolean","description":""" + JsonArgs.Quote(SubagentServers.SensitiveParameter + " (Every task.)") + """}},"required":["tasks"]}""";

    private sealed record Task1(string Description, string Prompt, AgentDefinition Definition, bool Sensitive, string? Step, SubagentPool.Entry? Pin);

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
        var allSensitive = JsonArgs.Bool(args, "sensitive", false);
        // Where the main agent wants them: every server named is checked before any task starts.
        var allChoice = AgentTool.ServerChoice(JsonArgs.String(args, "server"), context);
        if (allChoice?.Error is { } wrongServer) return wrongServer;
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
            var pin = allChoice?.Entry;
            if (AgentTool.ServerChoice(JsonArgs.String(item, "server"), context) is { } own)
            {
                if (own.Error is { } error) return $"Error: task {index}: " + (error.StartsWith("Error: ", StringComparison.Ordinal) ? error["Error: ".Length..] : error);
                pin = own.Entry;
            }
            var description = (JsonArgs.String(item, "description") ?? "").Trim();
            tasks.Add(new Task1(description.Length > 0 ? description : $"task {index}", prompt, definition,
                JsonArgs.Bool(item, "sensitive", allSensitive), AgentTool.StepArgument(item), pin));
        }

        var parallel = Math.Clamp(JsonArgs.Int(args, "max_parallel", DefaultParallel), 1, MaxTasks);
        using var gate = new SemaphoreSlim(parallel);
        var runs = tasks.Select(async task =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await AgentTool.RunCoreAsync(task.Prompt, task.Description, task.Definition, background: false, context,
                    cancellationToken, task.Sensitive, task.Step, task.Pin).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        var outcomes = await Task.WhenAll(runs).ConfigureAwait(false);

        var done = outcomes.Count(o => o.Ok);
        var text = new StringBuilder($"Delegated {tasks.Count} task{(tasks.Count == 1 ? "" : "s")}: {done} finished, {tasks.Count - done} failed.\n");
        // How busy each server is now, after the header (results only: the descriptions stay stable).
        if (AgentTool.LoadLine(context) is { } load) text.Append('\n').Append(load).Append('\n');
        for (var i = 0; i < tasks.Count; i++)
        {
            var outcome = outcomes[i];
            var details = outcome.Details();
            text.Append($"\n## {i + 1}. {tasks[i].Description} — {(outcome.Ok ? "finished" : "failed")}{(details.Length > 0 ? " " + details : "")}\n");
            if (outcome.FallbackLine() is { } moved) text.Append(moved).Append('\n');
            var report = outcome.Report;
            text.Append(report.Length > ReportChars ? report[..ReportChars] + "\n[… report truncated]" : report).Append('\n');
        }
        return text.ToString().TrimEnd();
    }
}
