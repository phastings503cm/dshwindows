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
    bool HitIterationLimit = false);

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
}

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

    /// <summary>Drive one user turn to completion, executing tool calls along the way.</summary>
    public async Task<RunResult> RunAsync(IReadOnlyList<LlmMessage> input, string userText,
                                          IReadOnlyList<MessageAttachment>? userAttachments = null,
                                          Action<EngineEvent>? sink = null,
                                          CancellationToken cancellationToken = default)
    {
        sink ??= static _ => { };
        var messages = input.ToList();
        if (userText.Length > 0 || userAttachments is { Count: > 0 })
            messages.Add(LlmMessage.User(userText, userAttachments));
        LlmUsage? usage = null;
        var denied = 0;
        var finalText = "";
        int? lastPromptTokens = null;
        var compacted = 0;

        for (var iteration = 0; iteration < Config.MaxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // -- Auto-compaction: keep the request inside the context window. --
            if (Config.ContextWindow is > 0 and var window && Compactor is not null && compacted < Config.MaxCompactions)
            {
                var used = TokenEstimate.Request(SystemPrompt, messages);
                if (used >= (int)(window * Compaction.TriggerFraction))
                {
                    try
                    {
                        var shrunk = await Compactor(used, messages.ToList(), cancellationToken).ConfigureAwait(false);
                        if (shrunk.Count < messages.Count)
                        {
                            messages = shrunk.ToList();
                            compacted++;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception)
                    {
                        // Compaction failed: proceed as-is; a server overflow is the second line of
                        // defence below.
                    }
                }
            }

            PruneToolImages(messages, Config.MaxToolImageMessages);

            var request = new LlmRequest(SystemPrompt, messages.ToList(), Registry.Specs, Config.Model,
                Config.Temperature, Config.MaxOutputTokens, Config.Thinking);

            // -- Model turn --
            var text = new StringBuilder();
            IReadOnlyList<ToolCall> calls = [];
            LlmUsage? turnUsage = null;
            try
            {
                await foreach (var ev in Client.StreamAsync(request, cancellationToken).ConfigureAwait(false))
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
            }
            catch (LlmException ex) when (ex.Kind == LlmErrorKind.Overflow && Compactor is not null
                                          && compacted < Config.MaxCompactions)
            {
                // The server said the request overflowed its real window. If we can still compact,
                // do it once and retry this iteration.
                IReadOnlyList<LlmMessage>? shrunk = null;
                try
                {
                    var used = TokenEstimate.Request(SystemPrompt, messages);
                    shrunk = await Compactor(used, messages.ToList(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception)
                {
                    // Fall through to rethrow the overflow.
                }
                if (shrunk is not null && shrunk.Count < messages.Count)
                {
                    messages = shrunk.ToList();
                    compacted++;
                    continue;
                }
                throw;
            }

            if (turnUsage is not null)
            {
                usage = usage is null
                    ? turnUsage
                    : new LlmUsage(usage.PromptTokens + turnUsage.PromptTokens, usage.CompletionTokens + turnUsage.CompletionTokens);
                lastPromptTokens = turnUsage.PromptTokens;
            }
            finalText = text.ToString();

            var assistantId = $"m{iteration}";
            messages.Add(LlmMessage.Assistant(finalText, calls));
            sink(new EngineEvent.AssistantMessage(assistantId, finalText, calls));

            if (calls.Count == 0)
            {
                sink(new EngineEvent.Finished(usage));
                return new RunResult(messages, usage, denied, finalText, lastPromptTokens);
            }

            // -- Tool turns --
            var toolImages = new List<(string Tool, IReadOnlyList<MessageAttachment> Images)>();
            foreach (var call in calls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sink(new EngineEvent.ToolStarted(call.Id, call.Name, Preview(call)));

                var (ok, reason) = await CheckPermissionAsync(call).ConfigureAwait(false);
                if (!ok)
                {
                    denied++;
                    var message = $"Permission denied: {reason}. Do not retry the same action; tell the user what you wanted to do and why, and stop.";
                    messages.Add(LlmMessage.ToolOutput(call.Id, call.Name, message));
                    sink(new EngineEvent.ToolFinished(call.Id, call.Name, false, "Denied by user", message));
                    continue;
                }

                var context = new ToolContext
                {
                    Workspace = Workspace,
                    Policy = Policy,
                    Client = Client,
                    Registry = Registry,
                    Depth = Config.Depth,
                    Model = Config.Model,
                    ContextWindow = Config.ContextWindow,
                    Thinking = Config.Thinking,
                    Shell = Config.Shell,
                    RequestPermission = PermissionGate,
                };
                var executor = Registry.Tool(call.Name);
                var result = executor is not null
                    ? await ExecuteWithTimeoutAsync(executor, call.Arguments, context, cancellationToken).ConfigureAwait(false)
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
                sink(new EngineEvent.ToolFinished(call.Id, call.Name,
                    !result.Output.StartsWith("Error:", StringComparison.Ordinal), Summary(result.Output), truncated));
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
            }
        }

        // Iteration budget exhausted: stop rather than loop forever.
        sink(new EngineEvent.Finished(usage));
        return new RunResult(messages, usage, denied, finalText, lastPromptTokens, HitIterationLimit: true);
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

    private async Task<ToolResult> ExecuteWithTimeoutAsync(IToolExecutor executor, string arguments, ToolContext context,
                                                           CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Run on the pool so a tool doing synchronous work never blocks the loop's timer.
        var work = Task.Run(() => executor.ExecuteAsync(arguments, context, cts.Token), CancellationToken.None);
        var watchdog = Task.Delay(Config.ToolTimeout, cts.Token);
        var first = await Task.WhenAny(work, watchdog).ConfigureAwait(false);
        if (first != work)
        {
            cts.Cancel();
            return cancellationToken.IsCancellationRequested
                ? new ToolResult("Error: cancelled by user.")
                : new ToolResult($"Error: tool timed out after {(int)Config.ToolTimeout.TotalSeconds}s.");
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
