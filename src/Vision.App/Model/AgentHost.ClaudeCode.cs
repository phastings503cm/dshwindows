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
// Vision never reads it. A folder's own Claude Code settings (hooks, MCP servers, CLAUDE.md) load only when the person
// trusted it in Claude Code (ClaudeCodeTrust), and what Vision hands over — the recap, the message — goes through the
// sensitive-data shield (ClaudeCodeShield). Vision for macOS does the same (AppTransport+ClaudeCode.swift).

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

    /// <summary>Chats already told their folder's own Claude Code settings are left out (once per launch).</summary>
    private readonly HashSet<string> _claudeTrustNoted = new(StringComparer.Ordinal);

    /// <summary>Chats already told what the shield covers on Claude Code (once per launch).</summary>
    private readonly HashSet<string> _claudeShieldNoted = new(StringComparer.Ordinal);

    /// <summary>Tests: Claude Code's own config file (.claude.json) to read the folder's trust from, instead of the person's.</summary>
    public string? ClaudeCodeConfigForTesting { get; set; }

    /// <summary>What Claude Code's config says about each folder (none for a route inside WSL: its config is in the Linux home).</summary>
    private IReadOnlyDictionary<string, ClaudeCodeProjectRecord> ClaudeCodeProjects(ProviderProfile route) =>
        ClaudeCodeRoutes.UsesWsl(route) ? new Dictionary<string, ClaudeCodeProjectRecord>() : ClaudeCodeTrust.ReadProjects(ClaudeCodeConfigForTesting);

    /// <summary>What a turn in <paramref name="workspace"/> loads of the folder's own settings — or the failure that stops it
    /// (a CLI that can't leave them out). Says once per chat per launch when they are left out.</summary>
    private ClaudeCodeFolderSettings ClaudeCodeFolder(SessionVM vm, string workspace, IReadOnlyDictionary<string, ClaudeCodeProjectRecord> projects,
                                                      bool canSkip)
    {
        var folder = ClaudeCodeTrust.Settings(workspace, ClaudeCodeTrust.IsTrusted(workspace, projects), canSkip);
        if (folder.Trust == ClaudeCodeFolderTrust.CantSkip)
            throw new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.UntrustedFolder, ClaudeCodeTrust.CantSkipMessage(null, folder.Skipped)));
        if (folder.Trust == ClaudeCodeFolderTrust.UserOnly && folder.Skipped.Count > 0 && _claudeTrustNoted.Add(vm.Id))
        {
            var text = ClaudeCodeTrust.SkippedNotice(folder.Skipped);
            vm.Note(text);
            Log.RecordItem(vm.Id, "notice", text);
        }
        return folder;
    }

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

    /// <summary>How Claude Code takes attachments: pictures (PNG, JPEG, GIF, WebP) and PDFs as themselves within the Messages
    /// API's limits, the rest as text (<see cref="ClaudeCodeCommand.Attachments"/>).</summary>
    private static AttachmentRoute ClaudeCodeAttachments => ClaudeCodeCommand.Attachments;

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
            _ => throw new ClaudeCodeException(new ClaudeCodeFailure(ClaudeCodeFailureKind.NotInstalled,
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
        var wsl = ClaudeCodeRoutes.UsesWsl(profile);
        string ForCli(string path) => wsl ? ClaudeCodeCli.ToWslPath(path) : path;
        var unsupported = _claudeUnsupported.TryGetValue(launch.Display, out var known) ? known : _claudeUnsupported[launch.Display] = new HashSet<string>();

        // The folder's own settings only when the person trusted it in Claude Code: headless, the CLI would run its hooks and
        // MCP servers without asking.
        var projects = ClaudeCodeProjects(profile);
        ClaudeCodeFolderSettings folder;
        try
        {
            folder = ClaudeCodeFolder(vm, workspace, projects, canSkip: !unsupported.Contains("--setting-sources"));
        }
        catch (ClaudeCodeException)
        {
            MarkUndelivered(vm, userEntryId);
            throw;
        }

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
                notes.Add($"(The full text of {attachment.Name} is saved at {ForCli(Path.Combine(store.Directory, entry.File))} — read the rest of it there with your Read tool; there is no read_attachment tool.)");
                if (!addDirs.Contains(store.Directory)) addDirs.Add(store.Directory);
            }
        }
        var rawParts = attachments.Count == 0 ? [] : ClaudeCodeCommand.AttachmentParts(attachments);
        var parts = rawParts;

        // The Claude Code session this chat carries on — or a new one, which first hears what the chat said before it.
        var row = Log.Row(sessionId);
        var session = row?.ClaudeSession;
        var lastCost = row?.ClaudeCost;
        var body = modelText;
        if (vm.Goal is not null) body += "\n\n" + ClaudeCodeGoalNote;
        if (notes.Count > 0) body += "\n\n" + string.Join("\n", notes);
        var shield = ShieldFor(sessionId);
        // The chat before the new message — all of it, or only what came after `since` — as a recap in front of the message,
        // behind the shield (ClaudeCodeShield): what it masked goes on the turn's answer like any route's.
        string Seeded(DateTimeOffset? since)
        {
            var before = vm.Entries.TakeWhile(e => e.Id != userEntryId).Where(e => since is not { } after || e.At > after).ToList();
            var masked = ClaudeCodeShield.Mask(ClaudeCodeRecap.Build(ReplayMessages(before)), body, rawParts, shield);
            parts = masked.Parts;
            if (shield is not null)
            {
                shield.Report?.Invoke(new ShieldReport(ShieldedClient.RouteName(profile), masked.Counts, masked.Unchecked));
                if (masked.Recap is not null && ShieldOn && _claudeShieldNoted.Add(sessionId))
                {
                    vm.Note(ClaudeCodeShield.Notice);
                    Log.RecordItem(sessionId, "notice", ClaudeCodeShield.Notice);
                }
            }
            return masked.Recap is { } recap ? recap + "\n\n" + masked.Message : masked.Message;
        }
        var resume = session;
        string? fresh = null;
        string prompt;
        if (resume is null)
        {
            fresh = Guid.NewGuid().ToString();
            prompt = Seeded(since: null);
        }
        else
        {
            // Turns other models answered since Claude Code last spoke go along first (nothing when none happened).
            prompt = Seeded(since: row?.ClaudeSyncedAt ?? DateTimeOffset.MinValue);
        }
        // The CLI's cost adds up across --resume only when it restores the session's running total — which it does when the
        // folder's last Claude Code session was this one (another chat in the same folder, or the person's terminal, breaks
        // that). Unknown (no record): the stored total.
        double? PreviousTotal() =>
            resume is null ? null
            : ClaudeCodeTrust.Record(workspace, projects) is { } record ? record.LastSessionId == resume ? lastCost : null
            : lastCost;
        var previousTotal = PreviousTotal();
        var policy = new PermissionPolicy(vm.Preset, workspace);
        Func<string, string>? mapPath = wsl ? ClaudeCodeCli.FromWslPath : null;

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
            var decision = ClaudeCodePermissions.Decide(request, policy, addDirs, mapPath);
            if (decision is not ClaudeCodePermissionDecision.Ask ask) return decision;
            // The card comes down (answered no) once the answer can't reach Claude Code any more.
            var allowed = await _dispatcher.InvokeAsync(() => AskGateAsync(sessionId, request.ToolUseId ?? request.RequestId, ask.Name, ask.Detail, token))
                .Task.Unwrap().ConfigureAwait(false);
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
                AddDirs = addDirs.Select(ForCli).ToList(),
                Unsupported = unsupported,
                ProjectSettings = folder.LoadsProjectSettings,
            };
            var stream = new ClaudeCodeStream(Sink, workspace, Activity, Notice, mapPath);
            var message = ClaudeCodeCommand.UserMessage(prompt, parts);
            var starter = ClaudeCodeStarterForTesting;
            try
            {
                outcome = await Task.Run(() => ClaudeCodeRunner.RunAsync(launch, options, workspace, message, stream, Decide, ct, starter, Activity),
                    CancellationToken.None);
            }
            catch (OperationCanceledException) when (!stream.Progressed)
            {
                // Stopped before Claude Code got anywhere: the message is kept, marked as not delivered (as on the Mac).
                MarkUndelivered(vm, userEntryId);
                throw;
            }
            catch (ClaudeCodeException error) when (error.Failure.Kind == ClaudeCodeFailureKind.NotInstalled)
            {
                // The CLI found earlier doesn't start any more: look for it again next time.
                _claudeLaunches.Remove(profile.BaseUrl);
                MarkUndelivered(vm, userEntryId);
                throw;
            }
            finally
            {
                // Whatever happened, the session it opened is the one to carry on (a lost one is replaced on the next turn).
                if (stream.SessionId is { } opened)
                    Log.SetClaudeSession(sessionId, opened, stream.Result?.TotalCostUsd ?? (opened == session ? lastCost : null),
                        stream.Progressed ? DateTimeOffset.Now : null);
                // Switching this chat to another model later: it rebuilds its transcript from the rows, Claude Code's turns included.
                _transcripts.Remove(sessionId);
            }
            if (outcome.Failure is not { } failure || outcome.Stream.Progressed || attempts >= 4) break;
            switch (failure.Kind)
            {
                // A CLI too old to leave the folder's own settings out: fine where the folder has none, refused where it has.
                case ClaudeCodeFailureKind.UnknownOption when failure.Detail == "--setting-sources" && !options.ProjectSettings && unsupported.Add("--setting-sources"):
                    try
                    {
                        folder = ClaudeCodeFolder(vm, workspace, projects, canSkip: false);
                    }
                    catch (ClaudeCodeException)
                    {
                        MarkUndelivered(vm, userEntryId);
                        throw;
                    }
                    continue;
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
                    previousTotal = null;
                    fresh = Guid.NewGuid().ToString();
                    prompt = Seeded(since: null);
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
        if (result is not null && result.LocalCommand is null && UsageLine(result, usage, previousTotal, stream2.ApiKeySource) is { } line)
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
        // A Sensitive chat doesn't hand its conversation to Claude Code to summarise either.
        if (SensitivityRefusal(vm, profile) is { } refusal)
        {
            vm.Note(refusal, MessageRole.Error);
            return;
        }
        ClaudeCodeLaunch launch;
        var workspace = vm.WorkspacePath is null ? PlainChat.Ensure(vm.WorkingFolder) : vm.WorkingFolder;
        var unsupported = new HashSet<string>();
        ClaudeCodeFolderSettings folder;
        try
        {
            launch = await ClaudeCodeLaunchFor(profile);
            if (_claudeUnsupported.TryGetValue(launch.Display, out var known)) unsupported = known;
            folder = ClaudeCodeFolder(vm, workspace, ClaudeCodeProjects(profile), canSkip: !unsupported.Contains("--setting-sources"));
        }
        catch (ClaudeCodeException error)
        {
            vm.Note(error.Message, MessageRole.Error);
            return;
        }
        var options = new ClaudeCodeTurnOptions
        {
            Model = profile.Model,
            Preset = vm.Preset,
            ResumeSession = session,
            PartialMessages = false,
            Unsupported = unsupported,
            ProjectSettings = folder.LoadsProjectSettings,
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
