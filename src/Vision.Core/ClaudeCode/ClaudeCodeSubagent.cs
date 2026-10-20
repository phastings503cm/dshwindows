namespace Vision.Core;

// MARK: - Claude Code as a subagent server
//
// A route on Claude Code that is ticked "Subagent" works a chat's side tasks the way Claude Code works: each task is one
// `claude -p` in the chat's folder (its scratch folder without a project), with no session kept, under the chat's
// permissions (Plan only for a read-only agent type) — its permission questions answered as a chat turn's are, the person
// asked on the chat's own card (which says no by itself for unattended work). Its last message is the report, followed by
// the list of tool calls it made, and goes back to the main agent like any subagent's. Stopping the task stops the CLI (an
// interrupt, then the process tree is killed: no claude process outlives the task). Claude Code is never private: it never
// takes a sensitive task and is never in a Sensitive chat's pool (see SubagentPool). The same words and report as Vision for
// macOS (ClaudeCodeSubagent.swift).

public sealed class ClaudeCodeSubagent(ClaudeCodeLaunch launch, string model, ThinkingLevel? thinking = null) : IExternalSubagent
{
    /// <summary>What every job is told first (the same words as the Mac).</summary>
    public const string Note =
        "You are running inside Vision as a subagent of another model's chat. Work the task in the message on your own: don't ask questions; " +
        "make reasonable assumptions and state them. Your final message is your report — it goes back to the main agent, not to a person: " +
        "keep it concise and factual, and list each file you changed.";

    /// <summary>The report when Claude Code wrote nothing.</summary>
    public const string NoReply = "(Claude Code worked on this without a written reply.)";

    /// <summary>In front of the report when it ran out of steps.</summary>
    public const string StepLimitPrefix = "It reached its step limit before finishing. ";

    /// <summary>Vision's names for the tools that change the workspace (Claude Code's Edit, MultiEdit, NotebookEdit, Write, Bash).</summary>
    public static readonly IReadOnlySet<string> MutatingTools = new HashSet<string>(StringComparer.Ordinal) { "edit", "write_file", "run_shell_command" };

    /// <summary>How many tool calls the report lists.</summary>
    internal const int ListedCalls = 40;

    public ClaudeCodeLaunch Launch { get; } = launch;
    /// <summary>The route's Claude Code alias ("sonnet").</summary>
    public string Model { get; } = model;
    /// <summary>--effort from the route's own thinking level (null = Claude Code's default).</summary>
    public ThinkingLevel? Thinking { get; } = thinking;
    /// <summary>Tests: start this scripted process instead of the CLI.</summary>
    public ClaudeCodeProcessStarter? Starter { get; init; }
    /// <summary>Whether the person trusted a folder in Claude Code (then its own settings load).</summary>
    public Func<string, bool> IsTrusted { get; init; } = static _ => false;
    /// <summary>Paths as Claude Code reports them → as Windows sees them (the CLI inside WSL); null = as they are.</summary>
    public Func<string, string>? MapPath { get; init; }
    /// <summary>Optional flags this CLI said it doesn't know: left out (filled in as it says so).</summary>
    public ISet<string> Unsupported { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The command line of one job.</summary>
    public ClaudeCodeTurnOptions Options(ExternalSubagentJob job, bool projectSettings)
    {
        HashSet<string> unsupported;
        lock (Unsupported) unsupported = new HashSet<string>(Unsupported, StringComparer.Ordinal);
        return new ClaudeCodeTurnOptions
        {
            Model = Model,
            Preset = job.Preset,
            Thinking = Thinking,
            AppendSystemPrompt = job.SystemNote,
            PartialMessages = false,
            Unsupported = unsupported,
            ProjectSettings = projectSettings,
            MaxTurns = job.MaxSteps,
            NoSessionPersistence = true,
            // Without --no-session-persistence a fresh id at least keeps it apart from every chat's session.
            NewSession = unsupported.Contains("--no-session-persistence") ? Guid.NewGuid().ToString() : null,
        };
    }

    public async Task<ExternalSubagentResult> RunAsync(ExternalSubagentJob job, CancellationToken cancellationToken)
    {
        bool CanSkip()
        {
            lock (Unsupported) return !Unsupported.Contains("--setting-sources");
        }
        // A folder whose own Claude Code settings can't be left out runs nothing (headless, its hooks would run unasked).
        ClaudeCodeFolderSettings Folder()
        {
            var folder = ClaudeCodeTrust.Settings(job.Workspace, IsTrusted(job.Workspace), CanSkip());
            if (folder.Trust == ClaudeCodeFolderTrust.CantSkip)
                throw new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.UntrustedFolder, ClaudeCodeTrust.CantSkipMessage(null, folder.Skipped)));
            return folder;
        }
        var folder = Folder();
        var policy = new PermissionPolicy(job.Preset, job.Workspace);
        async Task<ClaudeCodePermissionDecision> Decide(ClaudeCodePermissionRequest request, CancellationToken token)
        {
            var decision = ClaudeCodePermissions.Decide(request, policy, null, MapPath);
            if (decision is not ClaudeCodePermissionDecision.Ask ask) return decision;
            var allowed = await job.RequestPermission(request.ToolUseId ?? request.RequestId, ask.Name, ask.Detail).WaitAsync(token).ConfigureAwait(false);
            return allowed ? new ClaudeCodePermissionDecision.Allow() : new ClaudeCodePermissionDecision.Deny(ClaudeCodePermissions.Declined);
        }

        for (var attempt = 1; ; attempt++)
        {
            var log = new ToolLog();
            void Sink(EngineEvent ev)
            {
                switch (ev)
                {
                    case EngineEvent.ToolStarted started when log.Start(started.Id, started.Name, started.Preview):
                        job.Activity($"{started.Name}: {TextUtil.Prefix(started.Preview, 60)}", true);
                        if (MutatingTools.Contains(started.Name)) job.NoteMutation();
                        break;
                    case EngineEvent.ToolFinished { Ok: false } finished:
                        log.Fail(finished.Id);
                        break;
                }
            }
            var stream = new ClaudeCodeStream(Sink, job.Workspace, text =>
            {
                if (text is { Length: > 0 }) job.Activity(text, false);
            }, mapPath: MapPath);
            var options = Options(job, folder.LoadsProjectSettings);
            var outcome = await ClaudeCodeRunner.RunAsync(Launch, options, job.Workspace, ClaudeCodeCommand.UserMessage(job.Prompt), stream, Decide,
                cancellationToken, Starter).ConfigureAwait(false);
            // An older CLI that doesn't know an optional flag: left out from now on, and the job starts again (it did nothing yet).
            if (outcome.Failure is { Kind: ClaudeCodeFailureKind.UnknownOption, Detail: { } flag } && !stream.Progressed && attempt < 4
                && (ClaudeCodeCommand.OptionalFlags.Contains(flag) || flag == "--setting-sources") && AddUnsupported(flag))
            {
                if (flag == "--setting-sources") folder = Folder();
                continue;
            }
            var hitTurnLimit = outcome.Result?.Subtype == "error_max_turns";
            if (outcome.Failure is { } failure && !hitTurnLimit)
            {
                // The plan's limit: keep subagent work off Claude Code until it resets.
                if (failure.Kind == ClaudeCodeFailureKind.RateLimited) throw new SubagentServerPausedException(failure.Message, stream.RateLimit?.ResetsAt);
                throw new ClaudeCodeException(failure);
            }
            var usage = outcome.Usage ?? outcome.Result?.Usage;
            return new ExternalSubagentResult(true, Report(stream.LastReply, log.Entries, hitTurnLimit))
            {
                Mutated = log.Mutated,
                PromptTokens = usage?.PromptTokens ?? 0,
                CompletionTokens = usage?.CompletionTokens ?? 0,
                CostUsd = outcome.Result?.TotalCostUsd,
            };
        }
    }

    private bool AddUnsupported(string flag)
    {
        lock (Unsupported) return Unsupported.Add(flag);
    }

    /// <summary>The report: Claude Code's last message (or a line saying it wrote none), then the tool calls it made — so the main
    /// agent sees what was done even when the reply doesn't say. (The same text as the Mac.)</summary>
    public static string Report(string reply, IReadOnlyList<string> toolLog, bool hitTurnLimit)
    {
        var text = reply.Trim();
        if (text.Length == 0) text = NoReply;
        if (toolLog.Count > 0)
        {
            var shown = toolLog.Take(ListedCalls).ToList();
            var more = toolLog.Count - shown.Count;
            text += "\n\nClaude Code's tool calls: " + string.Join("; ", shown) + (more > 0 ? $"; … and {more} more" : "") + ".";
        }
        return hitTurnLimit ? StepLimitPrefix + text : text;
    }

    /// <summary>The calls a job made, in order: "read_file hello.txt", "run_shell_command dotnet test (failed)".</summary>
    internal sealed class ToolLog
    {
        private readonly Lock _lock = new();
        private readonly List<string> _order = [];
        private readonly Dictionary<string, (string Name, string Preview, bool Failed)> _calls = new(StringComparer.Ordinal);

        /// <summary>Whether this is a call it hadn't seen.</summary>
        public bool Start(string id, string name, string preview)
        {
            lock (_lock)
            {
                if (_calls.ContainsKey(id)) return false;
                _order.Add(id);
                _calls[id] = (name, preview, false);
                return true;
            }
        }

        public void Fail(string id)
        {
            lock (_lock)
            {
                if (_calls.TryGetValue(id, out var call)) _calls[id] = call with { Failed = true };
            }
        }

        public IReadOnlyList<string> Entries
        {
            get
            {
                lock (_lock)
                {
                    return _order.Select(id =>
                    {
                        var call = _calls[id];
                        var preview = call.Preview.Trim();
                        return (preview.Length == 0 ? call.Name : $"{call.Name} {preview}") + (call.Failed ? " (failed)" : "");
                    }).ToList();
                }
            }
        }

        public bool Mutated
        {
            get
            {
                lock (_lock) return _calls.Values.Any(c => MutatingTools.Contains(c.Name));
            }
        }
    }
}
