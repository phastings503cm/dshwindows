using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - Chats on Claude Code (your Claude subscription)
//
// A chat whose route is a Claude Code route (claude-code://…) hands each turn to the official claude CLI in headless
// mode instead of Vision's own engine: one `claude -p` per turn (per /goal round, per task), stream-json in and out, in the
// chat's folder, carrying on the same Claude Code session with --resume. Its events become the chat's rows through the
// same Apply as every engine event, so run phases, the ETA, the Prompts timeline, /goal and the task list all work on it
// unchanged; Claude Code's permission questions are answered with the chat's preset (asking the person on the same card
// Vision's own engine uses); Stop interrupts the turn. The CLI does all the talking with the person's own sign-in —
// Vision never reads it. Vision for macOS does the same (ClaudeCodeEngine.swift).

public sealed partial class AgentHost
{
    /// <summary>Tests: start this scripted process instead of the claude CLI.</summary>
    public ClaudeCodeProcessStarter? ClaudeCodeStarterForTesting { get; set; }

    /// <summary>Tests: the CLI to "start" (skips the search).</summary>
    public ClaudeCodeLaunch? ClaudeCodeLaunchForTesting { get; set; }

    /// <summary>The CLI found for each Claude Code address (searched once, again after Settings changes it).</summary>
    private readonly Dictionary<string, ClaudeCodeLaunch> _claudeLaunches = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional flags each CLI said it doesn't know (an older version): left out from then on.</summary>
    private readonly Dictionary<string, HashSet<string>> _claudeUnsupported = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Chats whose last turn ran on Claude Code (a later turn on another model says it changed).</summary>
    private readonly HashSet<string> _claudeChats = new(StringComparer.Ordinal);

    /// <summary>Chats already told the CLI bills an API key instead of the subscription (once per launch of Vision).</summary>
    private readonly HashSet<string> _claudeKeyNoted = new(StringComparer.Ordinal);

    /// <summary>Forget the CLI found for each route (Settings › Claude Code changed its path, or it was installed).</summary>
    public void ResetClaudeCode()
    {
        _claudeLaunches.Clear();
        _claudeUnsupported.Clear();
        ClaudeCodeClient.ResetCache();
    }

    /// <summary>How the top bar and the composer name a chat's model: "Claude Code · Sonnet" for Claude Code (its alias alone
    /// would read like any route's), else the model the route serves.</summary>
    public string? ModelLabelFor(SessionVM? vm) =>
        ChatRoute(vm) is { } route && ClaudeCodeRoutes.IsClaudeCode(route)
            ? $"{RouteLabel(route)} · {ClaudeCodeRoutes.ModelTitle(route.Model)}"
            : ModelIdFor(vm);

    /// <summary>The chat runs on Claude Code.</summary>
    public bool IsClaudeCodeChat(SessionVM? vm) => ClaudeCodeRoutes.IsClaudeCode(ChatRoute(vm));

    /// <summary>The shield is switched on (Settings › AI Models › Sensitive data).</summary>
    public bool ShieldOn => Config.Privacy.Mask;

    /// <summary>Why a Sensitive chat may not use <paramref name="route"/> (null when it may, or the chat isn't Sensitive).</summary>
    public string? SensitivityRefusal(SessionVM vm, ProviderProfile? route) =>
        vm.Sensitive && route is not null ? SensitivityPolicy.RefusalMessage(route, ShieldOn) : null;

    /// <summary>The Vision note every Claude Code session gets once (its system prompt is recorded on the first turn and
    /// reused, so it says only what holds for every turn).</summary>
    public const string ClaudeCodeSystemNote =
        "You are running inside Vision, a desktop agent harness, for one of the person's chats: they see your replies, " +
        "tool calls, plans and todo lists there, and answer your permission requests in Vision. When a message asks you to " +
        "end with GOAL_COMPLETE or GOAL_BLOCKED, write that marker alone on the last line of your reply — Vision's " +
        "goal_complete and goal_blocked tools don't exist here.";

    /// <summary>What a /goal round adds to its message on Claude Code (the kickoff names Vision's goal tools).</summary>
    public const string ClaudeCodeGoalNote =
        "(Vision note: you are Claude Code here — the goal_complete and goal_blocked tools aren't available. When the goal is " +
        "fully done and verified, end your reply with a line containing exactly GOAL_COMPLETE; if only the person can unblock " +
        "you, end with a line GOAL_BLOCKED: <what you need>.)";

    /// <summary>How Claude Code takes attachments: pictures (PNG, JPEG, GIF, WebP) and PDFs as themselves, the rest as text.</summary>
    private static readonly AttachmentRoute ClaudeCodeAttachments = new()
    {
        Vision = true,
        Documents = true,
        ImageTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image/png", "image/jpeg", "image/gif", "image/webp" },
        MaxImageBytes = 5 * 1024 * 1024,
        MaxDocumentBytes = 32L * 1024 * 1024,
    };

    /// <summary>The CLI a Claude Code route runs: found once (Settings' path, PATH, the installers' folders — or WSL).</summary>
    private async Task<ClaudeCodeLaunch> ClaudeCodeLaunchFor(ProviderProfile route)
    {
        if (ClaudeCodeLaunchForTesting is { } testing) return testing;
        if (_claudeLaunches.TryGetValue(route.BaseUrl, out var known)) return known;
        var status = await Task.Run(() => ClaudeCodeClient.CheckAsync(route)).ConfigureAwait(true);
        var launch = status switch
        {
            ClaudeCodeStatus.Ready ready => ready.Launch,
            // Signed out: run it anyway — the turn says so in Claude Code's own words, and works the moment they sign in.
            ClaudeCodeStatus.SignedOut signedOut => signedOut.Launch,
            _ => throw new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.Failed,
                status.Summary + " Settings › Claude Code shows how to install it and sign in.")),
        };
        _claudeLaunches[route.BaseUrl] = launch;
        return launch;
    }

    /// <summary>One turn on Claude Code. The user's row is already in the chat; this runs `claude -p` and returns what the
    /// goal loop and the queue read (the reply, the usage).</summary>
    private async Task<RunResult> ClaudeCodeTurnAsync(SessionVM vm, string modelText, IReadOnlyList<MessageAttachment> attachments,
                                                      ProviderProfile profile, string userEntryId, CancellationToken ct)
    {
        var sessionId = vm.Id;
        var plain = vm.WorkspacePath is null;
        var workspace = plain ? PlainChat.Ensure(vm.WorkingFolder) : vm.WorkingFolder;
        if (_claudeChats.Add(sessionId) && _engineKeys.Remove(sessionId))
        {
            var text = $"This chat now uses {RouteLabel(profile)} — {ClaudeCodeRoutes.ModelTitle(profile.Model)}, on your Claude plan through the claude app on this computer.";
            vm.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        }
        _engines.Remove(sessionId);
        var launch = await ClaudeCodeLaunchFor(profile);
        ct.ThrowIfCancellationRequested();

        // Attachments: pictures and PDFs go as themselves; a long file goes in parts, and Claude Code reads the rest of it
        // from where Vision saved it (it has no read_attachment tool).
        var addDirs = new List<string>();
        var notes = new List<string>();
        if (attachments.Count > 0)
        {
            attachments = await PrepareAttachmentsAsync(attachments, vm, userEntryId, profile, ContextLimit(profile));
            var store = AttachmentStoreFor(sessionId);
            foreach (var attachment in attachments.Where(a => a.Parts is > 1))
            {
                if (store.Entries().LastOrDefault(e => e.Name == attachment.Name) is not { } entry) continue;
                notes.Add($"(The full text of {attachment.Name} is saved at {Path.Combine(store.Directory, entry.File)} — read the rest of it there with your Read tool; there is no read_attachment tool.)");
                if (!addDirs.Contains(store.Directory)) addDirs.Add(store.Directory);
            }
        }
        var parts = attachments.Count == 0 ? [] : AttachmentWire.Parts(attachments, ClaudeCodeAttachments);

        // The Claude Code session this chat carries on — or a new one, which first hears what the chat said before it.
        var row = Log.Row(sessionId);
        var session = row?.ClaudeSession;
        var lastCost = row?.ClaudeCost;
        string Seeded(string text)
        {
            var before = vm.Entries.TakeWhile(e => e.Id != userEntryId).ToList();
            return ClaudeCodeRecap.Build(ReplayMessages(before)) is { } recap ? recap + "\n\n" + text : text;
        }
        var prompt = modelText;
        if (vm.Goal is not null) prompt += "\n\n" + ClaudeCodeGoalNote;
        if (notes.Count > 0) prompt += "\n\n" + string.Join("\n", notes);
        var resume = session;
        string? fresh = null;
        if (resume is null)
        {
            fresh = Guid.NewGuid().ToString();
            prompt = Seeded(prompt);
        }
        var unsupported = _claudeUnsupported.TryGetValue(launch.Display, out var known) ? known : _claudeUnsupported[launch.Display] = new HashSet<string>();
        var policy = new PermissionPolicy(vm.Preset, workspace);

        void Sink(EngineEvent evt) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Apply(evt, sessionId));
        void Activity(string? text) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (Session(sessionId) is { Running: true } live) live.Activity = text;
        });
        void Notice(string text) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (Session(sessionId) is not { } live) return;
            live.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        });
        async Task<ClaudeCodePermissionDecision> Decide(ClaudeCodePermissionRequest request, CancellationToken token)
        {
            var decision = ClaudeCodePermissions.Decide(request, policy);
            if (decision is not ClaudeCodePermissionDecision.Ask ask) return decision;
            var allowed = await _dispatcher.InvokeAsync(() => AskGateAsync(sessionId, request.ToolUseId ?? request.RequestId, ask.Name, ask.Detail)).Task.Unwrap()
                .ConfigureAwait(false);
            return allowed ? new ClaudeCodePermissionDecision.Allow() : new ClaudeCodePermissionDecision.Deny(ClaudeCodePermissions.Declined);
        }

        ClaudeCodeTurnResult outcome;
        var attempts = 0;
        while (true)
        {
            attempts++;
            var options = new ClaudeCodeTurnOptions
            {
                Model = profile.Model,
                Preset = vm.Preset,
                ResumeSession = resume,
                NewSession = fresh,
                Thinking = vm.Thinking ?? profile.Thinking,
                AppendSystemPrompt = ClaudeCodeSystemNote,
                AddDirs = addDirs,
                Unsupported = unsupported,
            };
            var stream = new ClaudeCodeStream(Sink, workspace, Activity, Notice,
                ClaudeCodeRoutes.UsesWsl(profile) ? ClaudeCodeCli.FromWslPath : null);
            var message = ClaudeCodeCommand.UserMessage(prompt, parts);
            var starter = ClaudeCodeStarterForTesting;
            try
            {
                outcome = await Task.Run(() => ClaudeCodeRunner.RunAsync(launch, options, workspace, message, stream, Decide, ct, starter, Activity),
                    CancellationToken.None);
            }
            finally
            {
                // Whatever happened, the session it opened is the one to carry on (a lost one is replaced on the next turn).
                if (stream.SessionId is { } opened) Log.SetClaudeSession(sessionId, opened, stream.Result?.TotalCostUsd ?? (opened == session ? lastCost : null));
                // Switching this chat to another model later: it rebuilds its transcript from the rows, Claude Code's turns included.
                _transcripts.Remove(sessionId);
            }
            if (outcome.Failure is not { } failure || outcome.Stream.Progressed || attempts >= 4) break;
            switch (failure.Kind)
            {
                case ClaudeCodeFailureKind.UnknownOption when failure.Detail is { } flag && ClaudeCodeCommand.OptionalFlags.Contains(flag) && unsupported.Add(flag):
                    continue;
                case ClaudeCodeFailureKind.ResumeMissing or ClaudeCodeFailureKind.SessionInUse:
                {
                    var text = "Claude Code no longer had this chat's session, so a new one starts here, with a recap of the chat so far.";
                    vm.Note(text);
                    Log.RecordItem(sessionId, "notice", text);
                    session = null;
                    lastCost = null;
                    resume = null;
                    fresh = Guid.NewGuid().ToString();
                    prompt = Seeded(modelText + (vm.Goal is not null ? "\n\n" + ClaudeCodeGoalNote : "") + (notes.Count > 0 ? "\n\n" + string.Join("\n", notes) : ""));
                    continue;
                }
            }
            break;
        }

        var stream2 = outcome.Stream;
        var result = outcome.Result;
        if (stream2.Model is { } resolved) vm.TurnModel = resolved;
        if (stream2.ApiKeySource is { } source && source != "none" && _claudeKeyNoted.Add(sessionId))
        {
            var text = source == "ANTHROPIC_API_KEY"
                ? "Claude Code is using the ANTHROPIC_API_KEY set on this computer — what it does is billed to that key, not your Claude plan."
                : $"Claude Code is signed in with an API key ({source}) — billed to that key, not your Claude plan.";
            vm.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        }
        if (result?.ContextWindow is { } window)
        {
            ClaudeCodeRoutes.LearnWindow(profile.Model, window);
            if (_probedContext.GetValueOrDefault(profile.RouteId) != window)
            {
                _probedContext[profile.RouteId] = window;
                ContextInfoChanged?.Invoke();
            }
        }
        if (outcome.Failure is { } failed)
        {
            if (!stream2.Progressed) MarkUndelivered(vm, userEntryId);
            throw new ClaudeCodeException(failed);
        }

        var usage = outcome.Usage ?? result?.Usage;
        vm.LastUsage = usage;
        if (usage is { } used)
        {
            vm.TurnPromptTokens += used.PromptTokens;
            vm.TurnCompletionTokens += used.CompletionTokens;
        }
        if (stream2.LastPromptTokens is { } filled) vm.ContextUsed = filled;
        var denied = result?.Denials ?? 0;
        if (denied > 0) vm.Note($"{denied} tool call(s) were denied.");
        if (result is not null && result.LocalCommand is null && UsageLine(result, usage, lastCost, stream2.ApiKeySource) is { } line)
        {
            vm.Note(line);
            Log.RecordItem(sessionId, "notice", line);
        }
        return new RunResult([], usage, denied, stream2.LastReply, stream2.LastPromptTokens);
    }

    /// <summary>"Claude Code: 3 steps · 12,400 tokens in (8,000 cached) · 84 out · about $0.01 at API prices — covered by your
    /// Claude plan." The CLI's cost adds up over the session, so a turn's share is the difference from the last one.</summary>
    public static string? UsageLine(ClaudeCodeResult result, LlmUsage? usage, double? previousTotal, string? apiKeySource)
    {
        var parts = new List<string>();
        if (result.NumTurns is > 0 and var steps) parts.Add($"{steps} step{(steps == 1 ? "" : "s")}");
        if (usage is { } u)
        {
            parts.Add($"{Fmt.N(u.PromptTokens)} tokens in" + (u.CachedTokens is > 0 and var cached ? $" ({Fmt.N(cached)} cached)" : ""));
            parts.Add($"{Fmt.N(u.CompletionTokens)} out");
        }
        if (result.TotalCostUsd is { } total)
        {
            var turn = previousTotal is { } before && total >= before ? total - before : total;
            var amount = turn < 0.01 ? "under $0.01" : "about $" + turn.ToString(turn < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture);
            var who = apiKeySource is { } source && source != "none" ? "billed to the API key" : "covered by your Claude plan";
            parts.Add($"{amount} at API prices — {who}");
        }
        return parts.Count == 0 ? null : "Claude Code: " + string.Join(" · ", parts) + ".";
    }

    /// <summary>/compact on a Claude Code chat: Claude Code compacts its own session.</summary>
    private async Task ClaudeCodeCompactAsync(SessionVM vm, ProviderProfile profile, string? focus, CancellationToken ct)
    {
        var sessionId = vm.Id;
        if (Log.Row(sessionId)?.ClaudeSession is not { } session)
        {
            vm.Note("Nothing to compact yet — this chat hasn't talked to Claude Code.");
            return;
        }
        ClaudeCodeLaunch launch;
        try
        {
            launch = await ClaudeCodeLaunchFor(profile);
        }
        catch (ClaudeCodeException error)
        {
            vm.Note(error.Message, MessageRole.Error);
            return;
        }
        var workspace = vm.WorkspacePath is null ? PlainChat.Ensure(vm.WorkingFolder) : vm.WorkingFolder;
        var options = new ClaudeCodeTurnOptions
        {
            Model = profile.Model,
            Preset = vm.Preset,
            ResumeSession = session,
            PartialMessages = false,
            Unsupported = _claudeUnsupported.TryGetValue(launch.Display, out var known) ? known : new HashSet<string>(),
        };
        void Notice(string text) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (Session(sessionId) is not { } live) return;
            live.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        });
        var stream = new ClaudeCodeStream(_ => { }, workspace, null, Notice);
        var command = "/compact" + (string.IsNullOrWhiteSpace(focus) ? "" : " " + focus.Trim());
        var outcome = await Task.Run(() => ClaudeCodeRunner.RunAsync(launch, options, workspace, ClaudeCodeCommand.UserMessage(command), stream,
            (_, _) => Task.FromResult<ClaudeCodePermissionDecision>(new ClaudeCodePermissionDecision.Deny(ClaudeCodePermissions.Declined)), ct,
            ClaudeCodeStarterForTesting), CancellationToken.None);
        if (outcome.Failure is { } failure)
        {
            vm.Note(failure.Message, MessageRole.Error);
            return;
        }
        if (stream.SessionId is { } id) Log.SetClaudeSession(sessionId, id, outcome.Result?.TotalCostUsd ?? Log.Row(sessionId)?.ClaudeCost);
    }

    /// <summary>/sensitive [on|off]: show or set whether the chat is Sensitive.</summary>
    private void SensitiveCommand(string? argument, SessionVM vm)
    {
        var route = ChatRoute(vm);
        switch (argument?.Trim().ToLowerInvariant())
        {
            case null or "":
                vm.Note((vm.Sensitive ? SensitivityPolicy.HeaderLine(route, ShieldOn) : "This chat isn't Sensitive: every model may be used.") +
                        "\n" + SensitivityPolicy.Explanation + " Change it with `/sensitive on` or `/sensitive off`.");
                return;
            case "on" or "yes" or "true":
                SetSensitive(vm, true);
                var line = SensitivityPolicy.HeaderLine(route, ShieldOn);
                if (route is not null && SensitivityPolicy.Admission(route, ShieldOn).Refusal is { } refusal)
                    vm.Note($"This chat is Sensitive now. {refusal}", MessageRole.Error);
                else vm.Note($"This chat is Sensitive now. {line}");
                return;
            case "off" or "no" or "false":
                SetSensitive(vm, false);
                vm.Note("This chat isn't Sensitive any more: every model may be used.");
                return;
            default:
                vm.Note($"Unknown setting “{argument}”. Use `/sensitive on` or `/sensitive off`.", MessageRole.Error);
                return;
        }
    }

    /// <summary>Mark a chat Sensitive (or not), and store it with the conversation.</summary>
    public void SetSensitive(SessionVM vm, bool sensitive)
    {
        vm.Sensitive = sensitive;
        Log.SetSensitive(vm.Id, sensitive);
    }
}
