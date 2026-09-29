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
    /// <summary>A tool call is about to execute.</summary>
    public sealed record ToolStarted(string Id, string Name, string Preview) : EngineEvent;
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
    string? LastReply = null)
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

    internal void Begin()
    {
        lock (_lock)
        {
            _latest = null;
            _progressed = false;
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
        if (userText.Length > 0 || userAttachments is { Count: > 0 })
            messages.Add(LlmMessage.User(userText, userAttachments));
        // Background agents that finished since the last turn report in.
        AppendBackgroundResults(messages, BackgroundAgents);
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

        for (var iteration = 0; iteration < Config.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // -- Model turn (retried while the server is unavailable) --
            var text = new StringBuilder();
            IReadOnlyList<ToolCall> calls = [];
            LlmUsage? turnUsage = null;
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
                if (Config.ContextWindow is > 0 and var window && Compactor is not null && compacted < Config.MaxCompactions)
                {
                    var used = TokenEstimate.Request(SystemPrompt, messages);
                    if (used >= (int)(window * Compaction.TriggerFraction)
                        && await TryCompactAsync(messages, used, cancellationToken).ConfigureAwait(false) is { } shrunk)
                    {
                        messages = shrunk;
                        compacted++;
                        progress?.Update(messages);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();

                PruneToolImages(messages, Config.MaxToolImageMessages);
                var request = new LlmRequest(SystemPrompt, messages.ToList(), Registry.Specs, model,
                    Config.Temperature, Config.MaxOutputTokens, Config.Thinking);

                text.Clear();
                calls = [];
                turnUsage = null;
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
                            progress?.Update(messages);
                            continue;
                        }
                    }
                    // Timeouts, a server that is down/restarting/swapping/busy: wait and try again until
                    // it answers or the user stops.
                    failures++;
                    if (RequestRetry.Disposition(error) is RetryDisposition.Limited) limitedFailures++;
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

            if (turnUsage is not null)
            {
                usage = usage is null
                    ? turnUsage
                    : new LlmUsage(usage.PromptTokens + turnUsage.PromptTokens, usage.CompletionTokens + turnUsage.CompletionTokens);
                lastPromptTokens = turnUsage.PromptTokens;
            }
            finalText = text.ToString();
            if (finalText.Trim().Length > 0) lastReplyText = finalText;

            var assistantId = $"m{iteration}";
            messages.Add(LlmMessage.Assistant(finalText, calls));
            progress?.Update(messages);
            sink(new EngineEvent.AssistantMessage(assistantId, finalText, calls));

            if (calls.Count == 0)
            {
                sink(new EngineEvent.Finished(usage));
                return new RunResult(messages, usage, denied, finalText, lastPromptTokens, LastReply: lastReplyText);
            }

            // -- Tool turns --
            var toolImages = new List<(string Tool, IReadOnlyList<MessageAttachment> Images)>();
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink(new EngineEvent.ToolStarted(call.Id, call.Name, Preview(call)));

                var (ok, reason) = await CheckPermissionAsync(call).ConfigureAwait(false);
                // Stop resolves a pending question as "no": that is not the user declining, so don't
                // record it as a refusal.
                cancellationToken.ThrowIfCancellationRequested();
                if (!ok)
                {
                    denied++;
                    var message = $"Permission denied: {reason}. Do not retry the same action; tell the user what you wanted to do and why, and stop.";
                    messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, message));
                    progress?.Update(messages);
                    sink(new EngineEvent.ToolFinished(call.Id, call.Name, false, "Denied by user", message));
                    continue;
                }

                var context = new ToolContext
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
                };
                var executor = Registry.Tool(call.Name);
                // A (foreground) subagent is a whole task, not a quick tool call.
                var timeout = call.Name == AgentTool.ToolName && Config.ToolTimeout < SubagentTimeout ? SubagentTimeout : Config.ToolTimeout;
                var result = executor is not null
                    ? await ExecuteWithTimeoutAsync(executor, call.Arguments, context, timeout, cancellationToken).ConfigureAwait(false)
                    : new ToolResult($"Error: unknown tool '{call.Name}'.");

                if (result.Todos is not null) OnTodos(result.Todos);
                var resultOutput = result.Output;
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
                var truncated = resultOutput.Length > 40_000
                    ? TextUtil.Suffix(resultOutput, 40_000) + "\n[result truncated]"
                    : resultOutput;
                messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, truncated));
                progress?.Update(messages);
                sink(new EngineEvent.ToolFinished(call.Id, call.Name,
                    !resultOutput.StartsWith("Error:", StringComparison.Ordinal), Summary(resultOutput), truncated));
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
            if (AppendBackgroundResults(messages, BackgroundAgents)) progress?.Update(messages);
        }

        // Iteration budget exhausted: stop rather than loop forever.
        sink(new EngineEvent.Finished(usage));
        return new RunResult(messages, usage, denied, finalText, lastPromptTokens, HitIterationLimit: true,
                             LastReply: lastReplyText);
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
    public static bool AppendBackgroundResults(List<LlmMessage> messages, BackgroundAgents? pool)
    {
        if (pool is null) return false;
        var finished = pool.TakeUnreported();
        if (finished.Count == 0) return false;
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
        return true;
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
