using System.Text;

namespace Dsh.Core;

// MARK: - Engine events (observed by the UI)

public abstract record EngineEvent
{
    /// <summary>A chunk of assistant text arrived.</summary>
    public sealed record TextDelta(string Text) : EngineEvent;
    /// <summary>A chunk of the model's reasoning arrived (shown live, not kept).</summary>
    public sealed record ReasoningDelta(string Text) : EngineEvent;
    /// <summary>The assistant's message for this turn is complete (text + any tool calls).</summary>
    public sealed record AssistantMessage(string Id, string Text, IReadOnlyList<ToolCall> Calls) : EngineEvent;
    /// <summary>A tool call is shown as started (it may still be refused or left unrun).</summary>
    public sealed record ToolStarted(string Id, string Name, string Preview) : EngineEvent;
    /// <summary>A tool call has passed its permission and credential checks and is being executed now — a call that is
    /// denied, refused or closed unrun never gets one.</summary>
    public sealed record ToolRunning(string Id, string Name) : EngineEvent;
    /// <summary>A tool call finished. Summary is a one-line headline; Output is the full (already
    /// length-capped) result the UI reveals on demand.</summary>
    public sealed record ToolFinished(string Id, string Name, bool Ok, string Summary, string Output) : EngineEvent;
    /// <summary>Images a tool produced; the UI shows them on the tool card.</summary>
    public sealed record ToolImages(string Id, IReadOnlyList<MessageAttachment> Images) : EngineEvent;
    /// <summary>Files a tool created, modified, or deleted — the editor reloads on these.</summary>
    public sealed record FilesChanged(IReadOnlyList<FileChange> Changes) : EngineEvent;
    /// <summary>The whole run finished cleanly.</summary>
    public sealed record Finished(LlmUsage? Usage) : EngineEvent;
    /// <summary>The model asked for something the policy flagged.</summary>
    public sealed record PermissionQuestion(string Id, string Name, string Detail) : EngineEvent;
    /// <summary>A tool pushed a todo list for the UI.</summary>
    public sealed record Todos(IReadOnlyList<TodoItem> Items) : EngineEvent;
    /// <summary>A planning run proposed a plan (exit_plan_mode); the plan panel shows it.</summary>
    public sealed record PlanProposed(string Text) : EngineEvent;
    /// <summary>The run failed (network error, provider error, ...).</summary>
    public sealed record Failed(string Message) : EngineEvent;
    /// <summary>A model call failed for a reason that can fix itself (timeout, server
    /// down/restarting/swapping, overloaded); the engine waits <paramref name="Delay"/> and tries again.
    /// Any text streamed by the failed attempt is void — the retry streams the reply from the start.</summary>
    public sealed record Retrying(int Attempt, TimeSpan Delay, string Reason) : EngineEvent;
    /// <summary>A model call succeeded after <paramref name="Attempts"/> failed tries.</summary>
    public sealed record Recovered(int Attempts) : EngineEvent;
}

/// <summary>Everything a run returns to its caller.</summary>
public sealed record RunResult(
    /// <summary>Full message list, ready to feed back next run.</summary>
    IReadOnlyList<LlmMessage> Messages,
    LlmUsage? Usage,
    int DeniedCount,
    /// <summary>The assistant text of the final turn (empty if the run was cut off mid-tools).</summary>
    string FinalText,
    /// <summary>Prompt tokens on the final model call — a good measure of how full the context is now.</summary>
    int? LastPromptTokens = null,
    /// <summary>True when the run stopped because it used its whole iteration budget while the model
    /// still wanted to call tools.</summary>
    bool HitIterationLimit = false,
    /// <summary>The last non-empty assistant text of the run. Usually FinalText, but when the model
    /// wrote its conclusion alongside a last tool call and then ended with an empty message, this still
    /// carries the conclusion. Null = same as FinalText.</summary>
    string? LastReply = null,
    /// <summary>The verdict the model gave a /goal loop through goal_complete / goal_blocked (null = none).</summary>
    GoalStatus? Goal = null,
    /// <summary>True when the run ended because the model made the same call with the same result over
    /// and over (see <see cref="StallTracker"/>) — the harness stops rather than spin.</summary>
    bool Stalled = false)
{
    public string LastReplyText => LastReply ?? FinalText;
}

/// <summary>A live copy of the transcript while a run is in flight. When a run throws or is cancelled
/// part-way, the caller keeps the work that already happened (tool calls that ran and their results)
/// instead of rolling the model's memory back to before the run while the files on disk moved on.</summary>
public sealed class RunProgress
{
    private readonly Lock _lock = new();
    private IReadOnlyList<LlmMessage>? _latest;
    /// <summary>Something beyond the run's own user message happened (a reply, a tool result, a
    /// compaction) — tracked as a flag, not by length, because an in-run compaction shrinks the list
    /// below where it started.</summary>
    private bool _progressed;
    private IReadOnlyList<BackgroundAgentJob> _reports = [];

    internal void Begin()
    {
        lock (_lock)
        {
            _latest = null;
            _progressed = false;
            _reports = [];
        }
    }

    internal void NoteReports(IReadOnlyList<BackgroundAgentJob> reports)
    {
        lock (_lock) _reports = reports;
    }

    /// <summary>The finished background agents whose reports this run took from the pool to hand the model. If the run ends
    /// before the model has replied (see <see cref="Salvaged"/>), they did not reach it and belong back in the pool.</summary>
    public IReadOnlyList<BackgroundAgentJob> TakenReports
    {
        get
        {
            lock (_lock) return _reports;
        }
    }

    internal void Update(List<LlmMessage> messages, bool progressed = true)
    {
        lock (_lock)
        {
            _latest = messages.ToList();
            if (progressed) _progressed = true;
        }
    }

    /// <summary>The transcript to keep after an interrupted run, or null when the run got no further
    /// than its own user message (nothing worth keeping — and two user messages in a row upset strict
    /// chat templates). Tool calls left without a result are closed with a stub so the transcript
    /// stays a valid request.</summary>
    public IReadOnlyList<LlmMessage>? Salvaged
    {
        get
        {
            lock (_lock)
            {
                if (_latest is null || !_progressed) return null;
                return Engine.ClosingDanglingToolCalls(_latest);
            }
        }
    }
}

public sealed record EngineConfig(string Model)
{
    public int MaxIterations { get; init; } = 30;
    public TimeSpan ToolTimeout { get; init; } = TimeSpan.FromSeconds(300);
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    /// <summary>The model's context window in tokens. When set, the run auto-compacts the transcript as
    /// it approaches the limit. Null disables in-run compaction.</summary>
    public int? ContextWindow { get; init; }
    /// <summary>How many times one run may compact, as a runaway guard (0 = never).</summary>
    public int MaxCompactions { get; init; } = 4;
    /// <summary>Thinking level sent with every model call; null = the provider default.</summary>
    public ThinkingLevel? Thinking { get; init; }
    /// <summary>How many tool-image messages stay in the request; older ones are replaced by a note.</summary>
    public int MaxToolImageMessages { get; init; } = 3;
    /// <summary>False when the model can't take images: tool images are dropped and the result says so.</summary>
    public bool VisionEnabled { get; init; } = true;
    /// <summary>The shell run_shell_command uses.</summary>
    public AgentShell Shell { get; init; } = AgentShell.Default;
    /// <summary>0 for a chat's own engine, 1 for a subagent.</summary>
    public int Depth { get; init; }
    /// <summary>How model calls that fail transiently (timeouts, server down, overloaded) are retried.
    /// Standard: indefinitely, with backoff, until cancelled.</summary>
    public RetryPolicy Retry { get; init; } = RetryPolicy.Standard;
    /// <summary>When true, reaching <see cref="MaxIterations"/> is a checkpoint, not the end: the step
    /// counter (and the per-segment compaction allowance) starts over and the model keeps working until it
    /// replies without tool calls, the user stops it, or it stalls. Chat turns and /goal rounds use this so
    /// nobody has to type "continue"; a subagent keeps its hard budget.</summary>
    public bool ContinueAfterLimit { get; init; }
    /// <summary>With <see cref="ContinueAfterLimit"/>: how many checkpoints one run may pass before it stops anyway
    /// (0 = no limit). The backstop for a model that explores for ever with ever-varying calls, which no
    /// repeat detector can catch — an unattended run must not be able to spend without end.</summary>
    public int MaxCheckpoints { get; init; }
    /// <summary>At the step limit (when not continuing), ask the model for a final report with no further
    /// work, so a subagent that ran out of steps still hands back what it found.</summary>
    public bool WrapUpAtLimit { get; init; }
    /// <summary>Identical (call, result) repeats before the model is told it is going in circles (0 = never).</summary>
    public int StallNudgeAt { get; init; } = 4;
    /// <summary>Identical repeats before the run ends (0 = never).</summary>
    public int StallStopAt { get; init; } = 8;
    /// <summary>Replies in a row cut off by the output-token limit that are continued automatically.</summary>
    public int MaxTruncationContinuations { get; init; } = 6;
}

/// <summary>Called before each retry of a failed model call: the host re-resolves the route (the
/// server may have swapped models, or the user fixed the provider in Settings) and returns the client
/// and model id to use from now on, or null to keep the current ones.</summary>
public delegate Task<(ILlmClient Client, string Model)?> RerouteHook(CancellationToken cancellationToken);

/// <summary>Auto-compaction hook: given the current request size and the transcript, return a smaller
/// transcript (older messages replaced by a summary) when it is over budget. Returning the input
/// unchanged (or throwing) means "proceed as-is".</summary>
public delegate Task<IReadOnlyList<LlmMessage>> CompactionHook(int used, IReadOnlyList<LlmMessage> transcript,
                                                                CancellationToken cancellationToken);

// MARK: - The engine

/// <summary>Agent engine: one run takes the current transcript and returns the extended one,
/// emitting events along the way. The transcript stays with the caller, which keeps persistence
/// trivial.</summary>
public sealed class Engine
{
    public ILlmClient Client { get; }
    public ToolRegistry Registry { get; }
    public string SystemPrompt { get; }
    public EngineConfig Config { get; }
    /// <summary>Workspace + policy for the run.</summary>
    public string Workspace { get; }
    public PermissionPolicy Policy { get; }
    /// <summary>Ask the user whether a flagged tool call may proceed.</summary>
    public PermissionGate PermissionGate { get; }
    /// <summary>Where structured todos are pushed for UI rendering.</summary>
    public Action<IReadOnlyList<TodoItem>> OnTodos { get; init; } = static _ => { };
    public CompactionHook? Compactor { get; init; }
    /// <summary>Screen/computer approvals for this chat (persist across engine rebuilds).</summary>
    public ComputerGrants ComputerGrants { get; init; } = new();
    /// <summary>Re-resolves the route before a retry (see <see cref="RerouteHook"/>).</summary>
    public RerouteHook? Reroute { get; init; }
    /// <summary>Background subagents launched from this chat; their results are handed to the model
    /// automatically when they finish.</summary>
    public BackgroundAgents? BackgroundAgents { get; init; }
    /// <summary>Credentials the agent can use as {{vault:NAME}} (substituted when a tool runs, scrubbed
    /// from every tool result), and this chat's "ask first" approvals.</summary>
    public CredentialVault? Vault { get; init; }
    public VaultGrants VaultGrants { get; init; } = new();
    /// <summary>The model servers subagents run on (see <see cref="AgentFleet"/>); null = this engine's client.</summary>
    public AgentFleet? Fleet { get; init; }
    /// <summary>Where this chat's subagent runs are recorded for the UI.</summary>
    public AgentRoster? Roster { get; init; }
    /// <summary>The kinds of subagent this chat offers.</summary>
    public AgentCatalog? AgentTypes { get; init; }
    /// <summary>Answers repeated read-only calls without redoing (or re-sending) the work; null = no caching.</summary>
    public ToolCache? Cache { get; init; }

    public Engine(ILlmClient client, ToolRegistry registry, string systemPrompt, EngineConfig config,
                  string workspace, PermissionPolicy policy, PermissionGate permissionGate)
    {
        Client = client;
        Registry = registry;
        SystemPrompt = systemPrompt;
        Config = config;
        Workspace = workspace;
        Policy = policy;
        PermissionGate = permissionGate;
    }

    /// <summary>Convenience for tests / subagents with auto-approval.</summary>
    public static Engine AutoApproving(ILlmClient client, ToolRegistry registry, string systemPrompt,
                                       EngineConfig config, string workspace, PermissionPolicy policy) =>
        new(client, registry, systemPrompt, config, workspace, policy, static (_, _, _) => Task.FromResult(true));

    // MARK: - Run

    /// <summary>Tools with no permission prompt and no vault substitution that may run side by side when
    /// the model asks for several in one turn (read-only lookups and subagents).</summary>
    private static readonly IReadOnlySet<string> ParallelTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "read_many_files", "glob", "grep", "list_directory", "agent", "memory_search", "vault_search",
        "todo_write", "use_skill",
    };

    private const string TruncationNotice =
        "[Automatic message: your last reply was cut off by the output-token limit. Continue exactly where you stopped — do not repeat what you already wrote.]";

    private const string CutOffNotice =
        "Not run: your reply was cut off by the output-token limit in the middle of this call, so its arguments were incomplete. " +
        "Send it again in smaller pieces (for example, write a large file in several parts).";

    private const string SiblingCutOffNotice =
        "Not run: another call in the same reply was cut off by the output-token limit, so none of this batch was run. " +
        "Send this call again (and the cut-off one in smaller pieces).";

    private const string GoalNotAlone =
        "Not recorded: goal_complete counts only when it is the only call in its turn, after you have seen the results of your other calls. " +
        "Check those results, then call goal_complete again on its own.";

    private const string RunEndedNotice = "Not run: the run was stopped before this call.";

    private const string WrapUpNotice =
        "[Automatic message: you have used your whole step budget. Reply now with your final report — what you did, what you found or changed, and what is still unfinished. Do not call any tools.]";

    /// <summary>A reply the server ended because it hit the output-token limit (OpenAI "length", Bedrock "max_tokens").</summary>
    private static bool IsTruncated(string? finish) => finish is "length" or "max_tokens" or "max_output_tokens";

    /// <summary>Whether a call's arguments are a JSON object (an empty string counts: some models send nothing for a
    /// tool that takes nothing). Cut-off or malformed JSON is not.</summary>
    internal static bool ValidArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return true;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(arguments) is System.Text.Json.Nodes.JsonObject;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Drive one user turn to completion, executing tool calls along the way.
    /// <paramref name="progress"/>, when given, tracks the transcript as it grows so a caller whose run
    /// throws can keep the work already done (see <see cref="RunProgress.Salvaged"/>).</summary>
    public async Task<RunResult> RunAsync(IReadOnlyList<LlmMessage> input, string userText,
                                          IReadOnlyList<MessageAttachment>? userAttachments = null,
                                          Action<EngineEvent>? sink = null,
                                          CancellationToken cancellationToken = default,
                                          RunProgress? progress = null)
    {
        sink ??= static _ => { };
        var messages = input.ToList();
        progress?.Begin();
        // A new turn: the user, a background agent or another program may have changed files since the last one, so
        // nothing searched before it is trusted. (And "read-only" for a subagent type is judged from this chat's own definitions.)
        Cache?.NoteExternalChange();
        if (Cache is not null && AgentTypes is not null) Cache.AgentIsReadOnly = type => AgentTypes.Find(type)?.IsReadOnly == true;
        if (userText.Length > 0 || userAttachments is { Count: > 0 })
            messages.Add(LlmMessage.User(userText, userAttachments));
        // Background agents that finished since the last turn report in.
        var reports = TakeBackgroundResults(messages, BackgroundAgents);
        progress?.NoteReports(reports);
        progress?.Update(messages, progressed: false);
        LlmUsage? usage = null;
        var denied = 0;
        var finalText = "";
        var lastReplyText = "";
        int? lastPromptTokens = null;
        var compacted = 0;
        // The route can change under a long run: a retry after the Spark swapped models continues on
        // whatever it serves now.
        var client = Client;
        var model = Config.Model;
        var step = 0;          // model calls so far (message ids)
        var segmentSteps = 0;  // model calls since the last checkpoint
        var truncations = 0;   // replies in a row cut off by the output-token limit
        var stall = new StallTracker(Config.StallNudgeAt, Config.StallStopAt, Config.Shell.Kind);
        GoalStatus? goal = null;
        var stalled = false;
        var checkpoints = 0;       // step-limit checkpoints passed
        var compactFailures = 0;   // summaries that failed in this segment (each costs a model call)
        var batchSize = 0;         // tool calls in the model turn being recorded
        var lastRead = new Dictionary<string, string>(StringComparer.Ordinal); // read_file arguments → result key of the last full read
        var toolImages = new List<(string Tool, IReadOnlyList<MessageAttachment> Images)>();

        ToolContext MakeContext() => new()
        {
            Workspace = Workspace,
            Policy = Policy,
            Client = client,
            Registry = Registry,
            Depth = Config.Depth,
            Model = model,
            ContextWindow = Config.ContextWindow,
            Thinking = Config.Thinking,
            Shell = Config.Shell,
            RequestPermission = PermissionGate,
            BackgroundAgents = BackgroundAgents,
            Vault = Vault,
            VaultGrants = VaultGrants,
            Fleet = Fleet,
            Roster = Roster,
            AgentTypes = AgentTypes,
            Retry = Config.Retry,
            VisionEnabled = Config.VisionEnabled,
            Temperature = Config.Temperature,
            MaxOutputTokens = Config.MaxOutputTokens,
        };

        // Run one call's executor (after permission and vault checks) with the timeout that fits it.
        async Task<ToolResult> RunToolAsync(ToolCall call, string arguments)
        {
            var executor = Registry.Tool(call.Name);
            if (executor is null) return new ToolResult($"Error: unknown tool '{call.Name}'.");
            // The cache keys on what the model wrote (before any vault value went in).
            string ResolvePath(string raw) => Policy.Resolve(raw).Path;
            if (Cache?.TryGet(call.Name, call.Arguments, Workspace, ResolvePath, messages.Count) is { } cached) return cached;
            // A (foreground) subagent is a whole task, not a quick tool call.
            var timeout = call.Name is AgentTool.ToolName or DelegateTool.ToolName && Config.ToolTimeout < SubagentTimeout ? SubagentTimeout : Config.ToolTimeout;
            var fresh = await ExecuteWithTimeoutAsync(executor, arguments, MakeContext(), timeout, cancellationToken).ConfigureAwait(false);
            Cache?.Observe(call.Name, call.Arguments, Workspace, ResolvePath, fresh, messages.Count);
            return fresh;
        }

        // Calls that change things without a list of files: a worker subagent edits the project, a process is a build or a
        // server. What a check said before them is not what it will say after.
        bool MayChangeThings(ToolCall call) => call.Name switch
        {
            AgentTool.ToolName => !(AgentTypes ?? AgentCatalog.Default).ReadOnlyType(JsonArgs.String(call.Arguments, "agent_type")),
            DelegateTool.ToolName or "process_start" or "process_write" => true,
            _ => false,
        };

        // The same call giving the same answer again is not progress: warn the model, then end the run.
        string Observe(ToolCall call, string output, bool changedFiles = false)
        {
            var key = StallTracker.ResultKey(call.Name, output);
            if (call.Name == ReadFileTool.ToolName)
            {
                // A cached "unchanged" note stands for the text it points back to, so a model re-reading one file
                // over and over still looks like what it is: the same call with the same result.
                var arguments = StallTracker.Normalize(call.Arguments);
                if (ToolCache.IsUnchangedNote(output) && lastRead.TryGetValue(arguments, out var known)) key = known;
                else lastRead[arguments] = key;
            }
            switch (stall.Observe(call.Name, call.Arguments, key, StallTracker.Failed(call.Name, output), changedFiles,
                        StallTracker.BareFailure(call.Name, output)))
            {
                case StallLevel.Nudge:
                    return output + StallTracker.NudgeText(call.Name, stall.Repeats, stall.Kind, stall.Span);
                case StallLevel.Stop:
                    stalled = true;
                    lastReplyText = StallTracker.StopText(call.Name, stall.Repeats, stall.Kind, stall.Span);
                    break;
            }
            return output;
        }

        // Everything that happens once a call has a result: todos, redaction, images, file changes, the
        // transcript entry, the goal verdict, and the repeat check.
        void Record(ToolCall call, ToolResult result)
        {
            if (result.Todos is not null) OnTodos(result.Todos);
            // No vault value ever reaches the model, the timeline or the logs.
            var secrets = Vault?.ValuesForRedaction() ?? [];
            var resultOutput = secrets.Count == 0 ? result.Output : VaultPlaceholders.Redact(result.Output, secrets);
            if (result.Images.Count > 0)
            {
                if (Config.VisionEnabled)
                {
                    toolImages.Add((call.Name, result.Images));
                    sink(new EngineEvent.ToolImages(call.Id, result.Images));
                }
                else
                {
                    resultOutput += $"\n({result.Images.Count} image(s) not shown: the selected model can't take images. Use text tools — ui_tree, process_read, logs — instead.)";
                }
            }
            if (result.Files.Count > 0) sink(new EngineEvent.FilesChanged(result.Files));
            if (result.Plan is { Length: > 0 } plan) sink(new EngineEvent.PlanProposed(plan));
            if (result.Goal is { } verdict)
            {
                // "Done" counts only once the model has seen the results of everything else it asked for this turn.
                if (verdict is GoalStatus.Complete && batchSize > 1) resultOutput = GoalNotAlone;
                else goal = verdict;
            }
            resultOutput = Observe(call, resultOutput, result.Files.Count > 0 || MayChangeThings(call));
            var truncated = resultOutput.Length > 40_000
                ? TextUtil.Suffix(resultOutput, 40_000) + "\n[result truncated]"
                : resultOutput;
            messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, truncated));
            progress?.Update(messages);
            sink(new EngineEvent.ToolFinished(call.Id, call.Name,
                !resultOutput.StartsWith("Error:", StringComparison.Ordinal), Summary(resultOutput), truncated));
        }

        while (true)
        {
            if (segmentSteps >= Config.MaxIterations)
            {
                if (!Config.ContinueAfterLimit) break;
                if (Config.MaxCheckpoints > 0 && ++checkpoints >= Config.MaxCheckpoints) break;
                // A checkpoint, not a stop: keep working with a fresh step and compaction allowance.
                segmentSteps = 0;
                compacted = 0;
                compactFailures = 0;
            }
            segmentSteps++;
            cancellationToken.ThrowIfCancellationRequested();

            // -- Model turn (retried while the server is unavailable) --
            var text = new StringBuilder();
            IReadOnlyList<ToolCall> calls = [];
            LlmUsage? turnUsage = null;
            string? providerState = null;
            string? finish = null;
            var failures = 0;
            // HTTP 500s get a bounded number of tries of their own — counted apart from outage
            // retries, so a 500 after a long outage still gets its full allowance.
            var limitedFailures = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Auto-compaction: keep the request inside the context window. Re-checked on every
                // attempt — a retry after an outage may still need it, and a failed summarizer gets
                // another chance.
                if (Config.ContextWindow is > 0 and var window && Compactor is not null && compacted < Config.MaxCompactions && compactFailures < 2)
                {
                    var used = TokenEstimate.Request(SystemPrompt, messages);
                    if (used >= (int)(window * Compaction.TriggerFraction))
                    {
                        if (await TryCompactAsync(messages, used, cancellationToken).ConfigureAwait(false) is { } shrunk)
                        {
                            messages = shrunk;
                            compacted++;
                            Cache?.NoteCompaction();
                            progress?.Update(messages);
                        }
                        else
                        {
                            // A summariser that keeps failing must not cost a wasted call on every step of a long run.
                            compactFailures++;
                        }
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();

                PruneToolImages(messages, Config.MaxToolImageMessages);
                var request = new LlmRequest(SystemPrompt, messages.ToList(), Registry.Specs, model,
                    Config.Temperature, Config.MaxOutputTokens, Config.Thinking);

                text.Clear();
                calls = [];
                turnUsage = null;
                providerState = null;
                finish = null;
                try
                {
                    await foreach (var ev in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        switch (ev)
                        {
                            case LlmStreamEvent.Text t:
                                text.Append(t.Delta);
                                sink(new EngineEvent.TextDelta(t.Delta));
                                break;
                            case LlmStreamEvent.Reasoning r:
                                sink(new EngineEvent.ReasoningDelta(r.Delta));
                                break;
                            case LlmStreamEvent.Done d:
                                calls = d.Calls;
                                turnUsage = d.Usage;
                                providerState = d.ProviderState;
                                finish = d.Finish;
                                break;
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    // Only a reply that arrived whole counts as recovery — a stream that starts and is
                    // cut off again is another failure.
                    if (failures > 0) sink(new EngineEvent.Recovered(failures));
                    break;
                }
                catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    // The server said the request overflowed its real window. If we can still compact,
                    // do it and retry this call. The server's word beats our estimate (which undercounts
                    // code and JSON): report at least a full window so the hook compacts even when the
                    // estimate looks fine.
                    if (error is LlmException { Kind: LlmErrorKind.Overflow } && Compactor is not null
                        && compacted < Config.MaxCompactions)
                    {
                        var used = Math.Max(TokenEstimate.Request(SystemPrompt, messages), Config.ContextWindow ?? 0);
                        if (await TryCompactAsync(messages, used, cancellationToken).ConfigureAwait(false) is { } shrunk)
                        {
                            messages = shrunk;
                            compacted++;
                            Cache?.NoteCompaction();
                            progress?.Update(messages);
                            continue;
                        }
                    }
                    // Timeouts, a server that is down/restarting/swapping/busy: wait and try again until
                    // it answers or the user stops.
                    failures++;
                    if (RequestRetry.Disposition(error) is RetryDisposition.Limited) limitedFailures++;
                    if (Config.Retry.MaxFailures is { } cap && failures > cap) throw;
                    if (!Config.Retry.ShouldRetry(error, Math.Max(1, limitedFailures))) throw;
                    var delay = Config.Retry.Delay(failures);
                    if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                    sink(new EngineEvent.Retrying(failures, delay, RequestRetry.Reason(error)));
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Reroute is not null && await Reroute(cancellationToken).ConfigureAwait(false) is { } route)
                    {
                        client = route.Client;
                        if (route.Model.Length > 0) model = route.Model;
                    }
                }
            }

            usage = LlmUsage.Sum(usage, turnUsage);
            if (turnUsage is not null) lastPromptTokens = turnUsage.PromptTokens;
            finalText = text.ToString();
            if (finalText.Trim().Length > 0) lastReplyText = finalText;

            var assistantId = $"m{step++}";
            // The provider's opaque state (signed reasoning) rides on the message so the next request
            // can send it back.
            messages.Add(LlmMessage.Assistant(finalText, calls) with { ProviderState = providerState });
            progress?.Update(messages);
            sink(new EngineEvent.AssistantMessage(assistantId, finalText, calls));

            if (calls.Count == 0)
            {
                // Cut off by the output-token limit rather than finished: pick up where the reply
                // stopped instead of leaving the user to type "continue".
                if (IsTruncated(finish) && truncations < Config.MaxTruncationContinuations)
                {
                    truncations++;
                    messages.Add(new LlmMessage(MessageRole.User, TruncationNotice) { ImageSource = "output limit" });
                    progress?.Update(messages);
                    continue;
                }
                sink(new EngineEvent.Finished(usage));
                return new RunResult(messages, usage, denied, finalText, lastPromptTokens, LastReply: lastReplyText);
            }
            // The reply ran into the output-token limit in the middle of a tool call: its arguments are cut off. Running
            // it would act on half a file, and sending broken JSON back would poison every later request, so each
            // call is closed unrun and the model is asked to send it again in smaller pieces.
            if (IsTruncated(finish) && calls.Any(c => !ValidArguments(c.Arguments)))
            {
                messages[^1] = messages[^1] with { ToolCalls = calls.Select(c => ValidArguments(c.Arguments) ? c : c with { Arguments = "{}" }).ToList() };
                foreach (var call in calls)
                {
                    var notice = ValidArguments(call.Arguments) ? SiblingCutOffNotice : CutOffNotice;
                    sink(new EngineEvent.ToolStarted(call.Id, call.Name, Preview(call)));
                    messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, notice));
                    sink(new EngineEvent.ToolFinished(call.Id, call.Name, false, "Not run", notice));
                }
                progress?.Update(messages);
                if (++truncations > Config.MaxTruncationContinuations)
                {
                    lastReplyText = $"Stopped: the model's reply was cut off by its output-token limit {truncations} times in a row while it was writing a tool call. " +
                                    "Raise the output limit for this model in Settings, or ask for a smaller change.";
                    sink(new EngineEvent.Finished(usage));
                    return new RunResult(messages, usage, denied, finalText, lastPromptTokens, LastReply: lastReplyText, Stalled: true);
                }
                continue;
            }
            truncations = 0;
            batchSize = calls.Count;

            // -- Tool turns --
            toolImages.Clear();
            // (Calls with a vault placeholder go one at a time: the parallel path does no substitution.)
            if (calls.Count > 1 && calls.All(c => ParallelTools.Contains(c.Name) && VaultPlaceholders.Names(c.Arguments).Count == 0))
            {
                // Independent lookups and subagents run side by side; results are recorded in call order.
                foreach (var call in calls)
                {
                    sink(new EngineEvent.ToolStarted(call.Id, call.Name, Preview(call)));
                    sink(new EngineEvent.ToolRunning(call.Id, call.Name));
                }
                var running = calls.Select(call => RunToolAsync(call, call.Arguments)).ToArray();
                var results = await Task.WhenAll(running).ConfigureAwait(false);
                for (var i = 0; i < calls.Count; i++) Record(calls[i], results[i]);
            }
            else
            {
                foreach (var call in calls)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The run has already ended (repeating itself, or waiting for the user): close the rest unrun.
                    if (stalled || goal is not null)
                    {
                        messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, RunEndedNotice));
                        progress?.Update(messages);
                        sink(new EngineEvent.ToolStarted(call.Id, call.Name, Preview(call)));
                        sink(new EngineEvent.ToolFinished(call.Id, call.Name, false, "Not run", RunEndedNotice));
                        continue;
                    }
                    sink(new EngineEvent.ToolStarted(call.Id, call.Name, Preview(call)));

                    var (ok, reason) = await CheckPermissionAsync(call).ConfigureAwait(false);
                    // Stop resolves a pending question as "no": that is not the user declining, so don't
                    // record it as a refusal.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ok)
                    {
                        denied++;
                        var message = Observe(call, $"Permission denied: {reason}. Do not retry the same action; tell the user what you wanted to do and why, and stop.");
                        messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, message));
                        progress?.Update(messages);
                        sink(new EngineEvent.ToolFinished(call.Id, call.Name, false, "Denied by user", message));
                        continue;
                    }

                    // Credentials: {{vault:NAME}} becomes the real value only now, after the permission
                    // check saw the placeholder.
                    var arguments = call.Arguments;
                    switch (await ResolveVaultAsync(call).ConfigureAwait(false))
                    {
                        case VaultResolution.Substituted substituted:
                            arguments = substituted.Arguments;
                            break;
                        case VaultResolution.Refused refused:
                            cancellationToken.ThrowIfCancellationRequested();
                            var refusal = Observe(call, refused.Message);
                            messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, refusal));
                            progress?.Update(messages);
                            sink(new EngineEvent.ToolFinished(call.Id, call.Name, false, Summary(refused.Message), refusal));
                            continue;
                    }
                    cancellationToken.ThrowIfCancellationRequested();

                    sink(new EngineEvent.ToolRunning(call.Id, call.Name));
                    Record(call, await RunToolAsync(call, arguments).ConfigureAwait(false));
                }
            }

            // Chat-completion tool messages are text-only, so what the tools captured goes back on one
            // attached user message, after every tool result of this turn (tool answers must stay
            // contiguous).
            if (toolImages.Count > 0)
            {
                var all = toolImages.SelectMany(t => t.Images).ToList();
                var names = string.Join(", ", toolImages.Select(t => t.Tool).Distinct().OrderBy(n => n, StringComparer.Ordinal));
                messages.Add(new LlmMessage(MessageRole.User,
                    $"[Automatic message: {all.Count} image(s) captured by {names}. Not from the user — this is what the tool saw.]")
                {
                    Attachments = all,
                    ImageSource = names,
                });
                progress?.Update(messages);
            }

            // The model told the harness it is finished (or stuck), or kept repeating itself: the run ends
            // here, with the transcript still a valid request (every call has its result).
            if (goal is not null || stalled)
            {
                sink(new EngineEvent.Finished(usage));
                return new RunResult(messages, usage, denied, finalText, lastPromptTokens, LastReply: lastReplyText,
                                     Goal: goal, Stalled: stalled);
            }
            if (AppendBackgroundResults(messages, BackgroundAgents))
            {
                // A background agent may have been editing files while this run searched them.
                Cache?.NoteExternalChange();
                progress?.Update(messages);
            }
        }

        // Iteration budget exhausted: stop rather than loop forever — but a subagent still owes its caller
        // a report, so ask for one.
        if (Config.WrapUpAtLimit && await WrapUpAsync(messages, client, model, sink, progress, cancellationToken).ConfigureAwait(false) is { } report)
        {
            finalText = report;
            lastReplyText = report;
        }
        sink(new EngineEvent.Finished(usage));
        return new RunResult(messages, usage, denied, finalText, lastPromptTokens, HitIterationLimit: true,
                             LastReply: lastReplyText);
    }

    /// <summary>One last model call with no more work asked of it: the report of a run that hit its step
    /// budget. Null when the model has nothing to say or the call fails (the caller keeps what it has).</summary>
    private async Task<string?> WrapUpAsync(List<LlmMessage> messages, ILlmClient client, string model,
                                            Action<EngineEvent> sink, RunProgress? progress, CancellationToken cancellationToken)
    {
        // Close any call the budget cut off, then ask. The tools stay in the request so the transcript
        // (which contains tool calls) remains a valid one for servers that insist on it.
        var closed = ClosingDanglingToolCalls(messages);
        if (!ReferenceEquals(closed, messages))
        {
            messages.Clear();
            messages.AddRange(closed);
        }
        messages.Add(new LlmMessage(MessageRole.User, WrapUpNotice) { ImageSource = "step limit" });
        var request = new LlmRequest(SystemPrompt, messages.ToList(), Registry.Specs, model,
            Config.Temperature, Config.MaxOutputTokens, Config.Thinking);
        var text = new StringBuilder();
        try
        {
            await foreach (var ev in client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (ev is LlmStreamEvent.Text t)
                {
                    text.Append(t.Delta);
                    sink(new EngineEvent.TextDelta(t.Delta));
                }
            }
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var report = text.ToString();
        if (report.Trim().Length == 0) return null;
        messages.Add(LlmMessage.Assistant(report));
        progress?.Update(messages);
        sink(new EngineEvent.AssistantMessage("wrap-up", report, []));
        return report;
    }

    /// <summary>How long a foreground subagent may work before its call times out.</summary>
    public static readonly TimeSpan SubagentTimeout = TimeSpan.FromHours(1);

    /// <summary>Run the compaction hook; the smaller transcript, or null when it didn't shrink (or
    /// failed — a server overflow is the second line of defence).</summary>
    private async Task<List<LlmMessage>?> TryCompactAsync(List<LlmMessage> messages, int used, CancellationToken cancellationToken)
    {
        try
        {
            var shrunk = await Compactor!(used, messages.ToList(), cancellationToken).ConfigureAwait(false);
            return shrunk.Count < messages.Count ? shrunk.ToList() : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Close tool calls that never got a result (the run was interrupted between the model
    /// asking and the tool answering) with a stub, so the transcript is a valid request again —
    /// servers reject an assistant tool call with no matching tool message.</summary>
    public static IReadOnlyList<LlmMessage> ClosingDanglingToolCalls(IReadOnlyList<LlmMessage> messages)
    {
        var last = -1;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role == MessageRole.Assistant)
            {
                last = i;
                break;
            }
        }
        if (last < 0 || messages[last].ToolCalls is not { Count: > 0 } calls) return messages;
        var after = messages.Skip(last + 1).ToList();
        // Anything but tool results after it means the block was closed already.
        if (!after.All(m => m.Role == MessageRole.Tool)) return messages;
        var answered = after.Select(m => m.ToolCallId).ToHashSet();
        var output = messages.ToList();
        foreach (var call in calls.Where(c => !answered.Contains(c.Id)))
            output.Add(LlmMessage.ToolOutput(call.Id, call.Name, "Not run — the turn was interrupted before this call executed."));
        return output;
    }

    /// <summary>Hand the model the reports of background agents that finished, as an automatic message
    /// (folded into a trailing user message so user turns never stack). Returns true when something
    /// was added.</summary>
    public static bool AppendBackgroundResults(List<LlmMessage> messages, BackgroundAgents? pool) =>
        TakeBackgroundResults(messages, pool).Count > 0;

    /// <summary>The same, and says which agents' reports were handed over (none when nothing was added).</summary>
    public static IReadOnlyList<BackgroundAgentJob> TakeBackgroundResults(List<LlmMessage> messages, BackgroundAgents? pool)
    {
        if (pool is null) return [];
        var finished = pool.TakeUnreported();
        if (finished.Count == 0) return [];
        var notice = BackgroundAgents.Notice(finished);
        if (messages.Count > 0 && messages[^1].Role == MessageRole.User)
        {
            messages[^1] = messages[^1] with { Content = (messages[^1].Content ?? "") + "\n\n" + notice };
        }
        else
        {
            // Tagged like the tool-image message: an automatic user message with no display entry.
            messages.Add(new LlmMessage(MessageRole.User, notice) { ImageSource = "background agents" });
        }
        return finished;
    }

    // MARK: - Vault

    private abstract record VaultResolution
    {
        public sealed record None : VaultResolution;
        public sealed record Substituted(string Arguments) : VaultResolution;
        public sealed record Refused(string Message) : VaultResolution;
    }

    /// <summary>Resolve the {{vault:NAME}} placeholders in a call: unknown names and credentials the
    /// user withheld refuse the call; "ask first" asks once per chat; the rest are substituted into
    /// the arguments. Only tools that execute their arguments get real values — a subagent prompt, a
    /// queued task, a skill draft or a todo would carry the secret to a model or to disk, so those keep
    /// the placeholder.</summary>
    private async Task<VaultResolution> ResolveVaultAsync(ToolCall call)
    {
        if (Vault is null || !VaultPlaceholders.SubstitutesInto(call.Name)) return new VaultResolution.None();
        var names = VaultPlaceholders.Names(call.Arguments);
        if (names.Count == 0) return new VaultResolution.None();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            switch (Vault.Lookup(name))
            {
                case VaultLookup.Value { Access: VaultAccess.Never }:
                    return new VaultResolution.Refused(
                        $"Error: the user has made the credential {name} unavailable to the agent. Tell them what you needed it for.");
                case VaultLookup.Value found:
                    if (found.Access == VaultAccess.Ask && !VaultGrants.Has(name))
                    {
                        var approved = await PermissionGate(call.Id, call.Name,
                            $"Use the credential {name} from the vault in {call.Name}").ConfigureAwait(false);
                        if (!approved)
                            return new VaultResolution.Refused(
                                $"Permission denied: the user did not allow {name} to be used. Don't retry with it; say what you needed it for.");
                        VaultGrants.Grant(name);
                    }
                    values[name] = found.Secret;
                    break;
                default:
                    return new VaultResolution.Refused(
                        $"Error: there is no credential named {name} in the vault. Use vault_search to see what's there; if it's missing, ask the user to add it to the Credentials Vault ({VaultPrompt.Shortcut}) — never to paste it into chat.");
            }
        }
        foreach (var name in names) Vault.NoteUse(name);
        return new VaultResolution.Substituted(VaultPlaceholders.Substitute(call.Arguments, values));
    }

    // MARK: - Permission

    private async Task<(bool Ok, string Reason)> CheckPermissionAsync(ToolCall call)
    {
        switch (call.Name)
        {
            case WriteFileTool.ToolName:
            case EditTool.ToolName:
            {
                var raw = JsonArgs.String(call.Arguments, "file_path") ?? "";
                var decision = Policy.CheckWrite(raw);
                if (decision.Verdict == PermissionVerdict.Proceed) return (true, "");
                if (decision.Verdict == PermissionVerdict.Deny) return (false, decision.Reason ?? "denied");
                var target = Policy.Preset == PermissionPreset.Plan ? "Write" : "Write outside the project";
                var approved = await PermissionGate(call.Id, call.Name, $"{target}: {Policy.Resolve(raw).Path}").ConfigureAwait(false);
                return (approved, approved ? "" : "user declined");
            }
            case RunShellCommandTool.ToolName:
                return await ShellGateAsync(call, JsonArgs.String(call.Arguments, "command") ?? "", "Run command").ConfigureAwait(false);
            case "process_start":
                // Starting a background process is running a shell command.
                return await ShellGateAsync(call, JsonArgs.String(call.Arguments, "command") ?? "", "Start background process").ConfigureAwait(false);
            case "process_write":
                // Input to a running process can be a command to its shell.
                return await ShellGateAsync(call, JsonArgs.String(call.Arguments, "input") ?? "", "Send to background process").ConfigureAwait(false);
            default:
                if (ComputerAccessInfo.ForTool(call.Name) is { } access)
                    return await ComputerGateAsync(access, call).ConfigureAwait(false);
                return (true, "");
        }
    }

    private async Task<(bool Ok, string Reason)> ShellGateAsync(ToolCall call, string command, string detailPrefix)
    {
        var decision = Policy.CheckShell(command);
        if (decision.Verdict == PermissionVerdict.Proceed) return (true, "");
        if (decision.Verdict == PermissionVerdict.Deny) return (false, decision.Reason ?? "denied");
        var approved = await PermissionGate(call.Id, call.Name, $"{detailPrefix}: {command}").ConfigureAwait(false);
        return (approved, approved ? "" : "user declined");
    }

    private async Task<(bool Ok, string Reason)> ComputerGateAsync(ComputerAccess access, ToolCall call)
    {
        var decision = Policy.CheckComputer(access);
        switch (decision.Verdict)
        {
            case PermissionVerdict.Proceed:
                return (true, "");
            case PermissionVerdict.Deny:
                return (false, decision.Reason ?? "denied");
            default:
                if (ComputerGrants.Has(access)) return (true, "");
                var approved = await PermissionGate(call.Id, call.Name, access.Prompt()).ConfigureAwait(false);
                if (approved) ComputerGrants.Grant(access);
                return (approved, approved ? "" : "user declined");
        }
    }

    // MARK: - Helpers

    /// <summary>Replace all but the newest <paramref name="keep"/> tool-image messages with a short
    /// note. Images the user attached themselves are never touched.</summary>
    public static void PruneToolImages(List<LlmMessage> messages, int keep)
    {
        var seen = 0;
        for (var index = messages.Count - 1; index >= 0; index--)
        {
            var m = messages[index];
            if (m.ImageSource is null || m.Attachments is not { Count: > 0 } attachments) continue;
            seen++;
            if (seen > Math.Max(0, keep))
            {
                messages[index] = m with
                {
                    Attachments = null,
                    Content = $"[{attachments.Count} earlier image(s) from {m.ImageSource} removed to save context — capture again if you need to see it.]",
                };
            }
        }
    }

    private static async Task<ToolResult> ExecuteWithTimeoutAsync(IToolExecutor executor, string arguments, ToolContext context,
                                                                  TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Run on the pool so a tool doing synchronous work never blocks the loop's timer.
        var work = Task.Run(() => executor.ExecuteAsync(arguments, context, cts.Token), CancellationToken.None);
        var watchdog = Task.Delay(timeout, cts.Token);
        var first = await Task.WhenAny(work, watchdog).ConfigureAwait(false);
        if (first != work)
        {
            cts.Cancel();
            return cancellationToken.IsCancellationRequested
                ? new ToolResult("Error: cancelled by user.")
                : new ToolResult($"Error: tool timed out after {(int)timeout.TotalSeconds}s.");
        }
        try
        {
            return await work.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ToolResult("Error: cancelled by user.");
        }
        catch (Exception ex)
        {
            return new ToolResult($"Error: tool failed: {ex.Message}");
        }
    }

    /// <summary>The interesting argument of a call — a path, a command, a pattern.</summary>
    public static string Preview(ToolCall call)
    {
        var args = JsonArgs.Object(call.Arguments);
        string? S(string key) => JsonArgs.String(args, key);
        switch (call.Name)
        {
            case "read_file": return S("file_path") ?? call.Name;
            case "write_file":
            case "edit": return S("file_path") ?? call.Name;
            case "run_shell_command": return S("command") ?? call.Name;
            case "web_fetch": return S("url") ?? call.Name;
            case "list_directory": return S("path") ?? call.Name;
            case "todo_write": return "update task list";
            case "delegate": return args["tasks"] is System.Text.Json.Nodes.JsonArray tasks ? $"{tasks.Count} parallel task{(tasks.Count == 1 ? "" : "s")}" : "parallel tasks";
            case "memory_save": return S("title") ?? "save a memory";
            case "memory_search": return S("query") ?? call.Name;
            case "memory_forget": return S("id") ?? call.Name;
            case "goal_complete": return "goal complete";
            case "goal_blocked": return S("reason") is { } why ? TextUtil.Prefix(why, 80) : "goal blocked";
            case "agent":
                return S("description") ?? (S("prompt") is { } p ? TextUtil.Prefix(p, 80) : null) ?? "subagent";
            case "glob": return S("pattern") ?? call.Name;
            case "grep": return S("pattern") ?? call.Name;
            case "read_many_files": return S("paths") ?? call.Name;
            default:
                // Plugin tools: the first string argument is nearly always the interesting one.
                foreach (var (_, node) in args.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    if (node is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
                        return TextUtil.Prefix(s, 80);
                }
                return call.Name;
        }
    }

    /// <summary>One-line headline of a tool result.</summary>
    public static string Summary(string output)
    {
        var first = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? output;
        return TextUtil.Prefix(first, 120);
    }
}
