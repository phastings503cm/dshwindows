using System.IO;
using System.Net.Http;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - Turn phases, the Prompts timeline, chat tasks, background activity
//
// What the person asked to see and steer (10-18, items 6, 11 and 2), the same way Vision for macOS does:
//  * the turn's phases — Planning → Execution → Validation → Results — with the estimate, the stamped steps, the
//    validation and the evidence (the green "Completed with results" card)
//  * the Prompts timeline — every prompt a chat sent and how it ended, kept on disk per chat
//  * the chat's tasks — what it is doing now (the running prompt's or /goal's plan, always the first task) and the
//    prompts queued after it with "Add to tasks", which start by themselves, in order, as the chat comes free
//  * background activity — everything working right now, on which model and server, assigned to what
// UI thread only, like the rest of AgentHost.

/// <summary>What a chat is doing now — its first task.</summary>
public sealed record CurrentWork(string Title, string Kind, DateTimeOffset StartedAt, int? Round, TurnPhaseState? Phase,
                                 IReadOnlyList<TodoItem> Steps, string? TaskId);

public sealed partial class AgentHost
{
    private TurnPhaseRunner? _phaseRunner;
    private PromptTimelineStore? _promptStore;

    /// <summary>The data folder the conversations live in (the timeline and the ETA cache sit beside them).</summary>
    private string DataFolder => Path.GetDirectoryName(Path.GetFullPath(Log.StorageDirectory).TrimEnd('\\', '/')) ?? AppPaths.Root;

    /// <summary>The learned-ETA cache (eta-cache.json in the data folder): every chat's finished turns feed it.</summary>
    public TurnPhaseRunner PhaseRunner => _phaseRunner ??= new TurnPhaseRunner(Path.Combine(DataFolder, TurnPhaseRunner.FileName));

    /// <summary>Each chat's Prompts timeline (prompt-timeline\&lt;chat&gt;.json in the data folder).</summary>
    public PromptTimelineStore PromptStore => _promptStore ??= new PromptTimelineStore(Path.Combine(DataFolder, PromptTimelineStore.FolderName));

    /// <summary>The cap on validation loop-backs for a /goal or a task; null = unlimited (the default). The no-progress check
    /// stops a loop-back that keeps failing the same way either way.</summary>
    public int? ValidationRetryCap => Config.ValidationRetryCap is > 0 and var cap ? cap : null;

    /// <summary>Raised when a chat's prompts or tasks change (the Prompts panel and the activity menu listen).</summary>
    public event Action? RunsChanged;
    private void RaiseRunsChanged() => RunsChanged?.Invoke();

    // MARK: - Driving the phases

    private readonly Dictionary<string, int> _projectFileCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A turn began: open planning. <paramref name="prompt"/> is what the model is asked, so the estimator
    /// classifies what the model sees.</summary>
    private void PhasesBegin(SessionVM vm, string prompt)
    {
        if (!vm.PhasesEnabled) return;
        // The first turn of a launch fetches what the org's harnesses learned (with a route through Guardian), so the
        // estimates that follow start smart.
        if (!_etaSynced) _ = SyncGuardianEtaAsync();
        var state = PhaseRunner.Begin(prompt, DateTimeOffset.Now, ProjectFiles(vm.WorkspacePath));
        state.LoopbackCap = ValidationRetryCap;
        vm.TurnFiles.Clear();
        vm.TurnPhase = state;
        vm.NotifyPhaseChanged();
    }

    /// <summary>How many files the project has (a scope signal) — counted once per folder, off the UI thread; 0 until known.</summary>
    private int ProjectFiles(string? root)
    {
        if (root is null) return 0;
        if (_projectFileCounts.TryGetValue(root, out var known)) return known;
        _projectFileCounts[root] = 0;
        _ = Task.Run(() => CountProjectFiles(root)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully) _dispatcher.BeginInvoke(() => _projectFileCounts[root] = t.Result);
        }, TaskScheduler.Default);
        return 0;
    }

    private static readonly string[] SkippedFolders = ["node_modules", ".git", "bin", "obj", ".build", "dist", ".venv", "__pycache__", ".vs"];

    /// <summary>Files under <paramref name="root"/>, skipping build output and dependencies, up to 2,000.</summary>
    public static int CountProjectFiles(string root)
    {
        var count = 0;
        var pending = new Stack<string>([root]);
        while (pending.Count > 0 && count <= 2_000)
        {
            var folder = pending.Pop();
            try
            {
                count += Directory.EnumerateFiles(folder).Count();
                foreach (var child in Directory.EnumerateDirectories(folder))
                    if (!SkippedFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase)) pending.Push(child);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A folder that can't be read doesn't count.
            }
        }
        return Math.Min(count, 2_000);
    }

    /// <summary>The plan settled: its first todo list, its proposed plan, or the first line of its answer. Estimate from the
    /// scope so far and what similar turns took, and move to execution.</summary>
    private void PhasesPlanSettled(SessionVM vm)
    {
        if (vm.TurnPhase is not { Phase: TurnPhase.Planning } state) return;
        string plan;
        if (vm.Todos.Count > 0) plan = string.Join("\n", vm.Todos.Select(t => "• " + t.Content));
        else if (vm.ProposedPlan is { Length: > 0 } proposed) plan = proposed;
        else plan = ClosingReply(vm, state).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        PhaseRunner.PlanSettled(state, plan, Math.Max(vm.Todos.Count, 1), DateTimeOffset.Now);
        PromptPlanned(vm, state);
        vm.NotifyPhaseChanged();
    }

    /// <summary>A question-only turn settles its plan on the prose it wrote (no todo list this turn).</summary>
    private void PhasesPlanFromProse(SessionVM vm)
    {
        if (vm.TurnPhase is { Phase: TurnPhase.Planning } && vm.Todos.Count == 0) PhasesPlanSettled(vm);
    }

    /// <summary>A step landed (a tool finished): stamped into the working badge's log.</summary>
    private void PhasesStep(SessionVM vm, string text)
    {
        if (vm.TurnPhase is not { Phase: TurnPhase.Execution or TurnPhase.Planning } state) return;
        state.AddStep(text, DateTimeOffset.Now);
        vm.NotifyPhaseChanged();
    }

    /// <summary>The live draft: the first text of a turn is its plan being written.</summary>
    private static void PhasesDraft(SessionVM vm, string chunk)
    {
        if (vm.TurnPhase is not { Phase: TurnPhase.Planning } state) return;
        state.Draft += chunk;
        if (state.Draft.Length > 8_000) state.Draft = TextUtil.Suffix(state.Draft, 6_000);
    }

    private static void PhasesFilesChanged(SessionVM vm, IEnumerable<FileChange> changes)
    {
        foreach (var change in changes) vm.TurnFiles.Add(change.Path);
        if (vm.TurnPhase is { } state && vm.TurnFiles.Count > state.FilesChanged) state.FilesChanged = vm.TurnFiles.Count;
    }

    /// <summary>The plan grew mid-execution: nudge the estimate up, gently.</summary>
    private void PhasesRefineIfGrew(SessionVM vm)
    {
        if (vm.TurnPhase is not { Phase: TurnPhase.Execution } state || vm.Todos.Count == 0) return;
        PhaseRunner.Refine(state, vm.Todos.Count);
        PromptPlanned(vm, state);
        vm.NotifyPhaseChanged();
    }

    /// <summary>The todo list changed: it settles the plan, or refines the estimate.</summary>
    private void PhasesTodos(SessionVM vm)
    {
        if (vm.TurnPhase?.Phase == TurnPhase.Planning) PhasesPlanSettled(vm);
        else PhasesRefineIfGrew(vm);
    }

    /// <summary>A plain turn's engine run ended: check it against the plan it set out with, gather the evidence it produced,
    /// and show results — "Finished with issues" when validation found something (nothing re-runs a plain turn by itself:
    /// the person decides what's next).</summary>
    private void PhasesValidate(SessionVM vm)
    {
        if (vm.TurnPhase is not { } state || state.IsOver) return;
        if (state.Phase == TurnPhase.Planning) PhasesPlanFromProse(vm);
        if (state.Phase != TurnPhase.Execution) return;
        state.Phase = TurnPhase.Validation;
        var reply = ClosingReply(vm, state);
        var failure = TurnValidation.Failures(state, reply).FirstOrDefault();
        GatherEvidence(vm, state, reply);
        if (failure is not null)
        {
            state.LastValidationFailure = failure;
            state.AddEvidence(new Evidence(EvidenceKind.Note, "Validation found an issue", failure, DateTimeOffset.Now));
            state.BeginResults("Finished with issues: " + failure, DateTimeOffset.Now);
        }
        else
        {
            state.BeginResults(ValidatedLine(vm), DateTimeOffset.Now);
        }
        PhasesFinish(vm);
    }

    private static string ValidatedLine(SessionVM vm) => vm.Todos.Count == 0
        ? "Validated: the reply matches what was asked."
        : $"Validated against the plan: {vm.Todos.Count(t => t.Status == TodoStatus.Completed)} of {vm.Todos.Count} steps done.";

    /// <summary>A /goal or task the model declared complete: validate before accepting it. Null when it passes (the turn moves
    /// to results); otherwise the failure, and the turn is back in execution for another round — or, at the cap or after the
    /// no-progress check, finished with issues (<paramref name="accept"/> true).</summary>
    private string? PhasesValidateGoal(SessionVM vm, string reply, ref (string? Failure, int Done, int Streak) last, out bool accept)
    {
        accept = true;
        if (vm.TurnPhase is not { } state || state.IsOver) return null;
        if (state.Phase == TurnPhase.Planning) PhasesPlanSettled(vm);
        var now = DateTimeOffset.Now;
        var done = vm.Todos.Count(t => t.Status == TodoStatus.Completed);
        // Only what the harness can see: an error the closing words report (an empty "goal_complete" is not a failure — the
        // tool's summary is the closing words).
        var failure = TurnValidation.ReportedError(reply) is { } marker ? $"the reply reports an error ({marker.Trim()})" : null;
        if (failure is null)
        {
            last = (null, done, 0);
            // An earlier attempt's failure is fixed now: the card and the record say it passed.
            state.LastValidationFailure = null;
            state.Phase = TurnPhase.Validation;
            GatherEvidence(vm, state, reply);
            state.BeginResults(ValidatedLine(vm), now);
            vm.NotifyPhaseChanged();
            return null;
        }
        var streak = TurnValidation.MadeNoProgress(last.Failure, failure, last.Done, done) ? last.Streak + 1 : 0;
        last = (failure, done, streak);
        if (streak < TurnValidation.NoProgressLimit && state.LoopBack(failure, now))
        {
            accept = false;
            vm.NotifyPhaseChanged();
            return failure;
        }
        // Capped, or going nowhere: finish with what was achieved, and say what didn't pass.
        state.LastValidationFailure = failure;
        state.Phase = TurnPhase.Validation;
        GatherEvidence(vm, state, reply);
        state.AddEvidence(new Evidence(EvidenceKind.Note,
            streak >= TurnValidation.NoProgressLimit ? "Validation stopped: no progress in two attempts" : "Validation hit the retry cap",
            "Last failure: " + failure, now));
        state.BeginResults("Finished with issues: " + failure, now);
        vm.NotifyPhaseChanged();
        return failure;
    }

    /// <summary>A turn ended without reaching validation (stopped, an error, a goal that needs the person): close it out
    /// honestly.</summary>
    private void PhasesAbandon(SessionVM vm, string reason, bool completed)
    {
        if (vm.TurnPhase is not { } state || state.IsOver) return;
        if (completed)
        {
            GatherEvidence(vm, state, ClosingReply(vm, state));
            state.BeginResults(reason, DateTimeOffset.Now);
        }
        else
        {
            state.End(reason, DateTimeOffset.Now);
        }
        PhasesFinish(vm);
    }

    /// <summary>The turn is over: its duration goes into the ETA cache (and Guardian's, when a Guardian route is set up), and
    /// the card stays until the next turn.</summary>
    private void PhasesFinish(SessionVM vm)
    {
        if (vm.TurnPhase is not { } state) return;
        var now = DateTimeOffset.Now;
        PhaseRunner.RecordCompletion(state, now);
        if (state.TaskKind is { } kind && state.StartedAt is { } start && (state.Phase == TurnPhase.Results || state.LastValidationFailure is not null))
            PushEta(kind, state.ScopeScale, ((state.FinishedAt ?? now) - start).TotalSeconds);
        vm.NotifyPhaseChanged();
    }

    /// <summary>The turn's closing words: its newest answer since the turn began.</summary>
    private static string ClosingReply(SessionVM vm, TurnPhaseState state)
    {
        var since = (state.StartedAt ?? DateTimeOffset.MinValue).AddSeconds(-1);
        for (var i = vm.Entries.Count - 1; i >= 0; i--)
        {
            if (vm.Entries[i].At < since) break;
            if (vm.Entries[i] is MessageEntryVM { Role: MessageRole.Assistant } answer && answer.Text.Trim().Length > 0) return answer.Text;
        }
        return "";
    }

    private static readonly string[] CheckWords = ["test", "build", "check", "lint", "verify", "pytest", "jest", "cargo", "make", "run"];

    /// <summary>What the turn's own timeline already proves, so the results card has something to show even when the model
    /// wrote no summary: the screenshot it took, the files it changed, the last check it ran, its closing words.</summary>
    private static void GatherEvidence(SessionVM vm, TurnPhaseState state, string reply)
    {
        var since = (state.StartedAt ?? DateTimeOffset.MinValue).AddSeconds(-1);
        var turn = vm.Entries.Where(e => e.At >= since).ToList();
        var now = DateTimeOffset.Now;
        if (turn.OfType<ToolEntryVM>().LastOrDefault(t => t is { Name: "screenshot", IsOk: true } && t.Images.Count > 0) is { } shot)
            state.AddEvidence(new Evidence(EvidenceKind.Screenshot, "Screenshot of the result", "captured " + PhaseText.Stamp(shot.At) + " — see the screenshot card above", now));
        if (vm.TurnFiles.Count > 0)
        {
            var names = string.Join(", ", vm.TurnFiles.Take(5).Select(Path.GetFileName));
            state.AddEvidence(new Evidence(EvidenceKind.File, $"{vm.TurnFiles.Count} file{(vm.TurnFiles.Count == 1 ? "" : "s")} changed",
                names + (vm.TurnFiles.Count > 5 ? $" and {vm.TurnFiles.Count - 5} more" : ""), now));
        }
        if (turn.OfType<ToolEntryVM>().LastOrDefault(t => t is { Name: "run_shell_command", IsOk: true }
                && CheckWords.Any(w => t.Preview.Contains(w, StringComparison.OrdinalIgnoreCase))) is { } check)
        {
            var output = check.Output is { Length: > 0 } text ? "\n" + TextUtil.Suffix(text.Trim(), 400) : "";
            state.AddEvidence(new Evidence(EvidenceKind.RunOutput, "Ran " + TextUtil.Prefix(check.Preview.Replace('\n', ' '), 80),
                (check.Summary ?? "ok") + output, now));
        }
        if (reply.Trim() is { Length: > 0 } closing)
            state.AddEvidence(new Evidence(EvidenceKind.Check, "The model's closing summary", TextUtil.Prefix(closing, 600), now));
    }

    // MARK: - Guardian's shared ETA cache

    private bool _etaSynced;

    /// <summary>The signed-in Guardian account (Settings › Guardian), when there is one. Set by the app's Guardian service.</summary>
    public Func<GuardianEndpoint?>? GuardianAccount { get; set; }

    /// <summary>Where prompt history and shared ETAs go: the signed-in account, else the Guardian a route through Guardian's
    /// model proxy points at, with its key — null when neither.</summary>
    private GuardianEndpoint? GuardianEndpointNow()
    {
        if (GuardianAccount?.Invoke() is { } account) return account;
        foreach (var route in Config.Providers.Where(p => p.IsEnabled))
        {
            if (!route.BaseUrl.Contains(GuardianEndpoint.HarnessPath, StringComparison.OrdinalIgnoreCase)) continue;
            if (GuardianEndpoint.FromRoute(route, Config.ApiKey(route)) is { } endpoint) return endpoint;
        }
        return null;
    }

    private GuardianEtaClient? GuardianEta() => GuardianEndpointNow() is { } endpoint ? new GuardianEtaClient(endpoint, HttpHandlerForTesting) : null;

    /// <summary>Once per launch, with a Guardian route set up: what the org's harnesses learned blends into this PC's cache,
    /// so its first estimates start smart. Silent on failure — the local cache works on its own.</summary>
    public async Task SyncGuardianEtaAsync()
    {
        if (_etaSynced || GuardianEta() is not { } client) return;
        _etaSynced = true;
        try
        {
            await client.SyncIntoAsync(PhaseRunner);
        }
        catch (Exception error) when (error is GuardianException or HttpRequestException or TaskCanceledException)
        {
            _etaSynced = false; // try again next time
        }
    }

    /// <summary>A finished turn's duration goes up to Guardian's shared cache (silent on failure).</summary>
    private void PushEta(TaskKind kind, double scope, double seconds)
    {
        if (GuardianEta() is not { } client) return;
        _ = PushAsync();

        async Task PushAsync()
        {
            try
            {
                await client.PushAsync([(kind, scope, seconds)]);
                await SyncGuardianEtaAsync();
            }
            catch (Exception error) when (error is GuardianException or HttpRequestException or TaskCanceledException)
            {
                // The local cache already has it.
            }
        }
    }

    // MARK: - The Prompts timeline

    /// <summary>A chat task starting: the source of the next record the chat opens, and the task it belongs to.</summary>
    private readonly Dictionary<string, (PromptSource Source, string TaskId)> _pendingPromptSource = new();
    /// <summary>Task id → the prompt record its turn opened.</summary>
    private readonly Dictionary<string, string> _chatTaskRecord = new();

    /// <summary>Read a chat's timeline from disk once — or backfill it from the transcript for a chat from before the
    /// timeline. A prompt a previous launch left running didn't finish.</summary>
    private void LoadPromptHistory(SessionVM vm)
    {
        if (vm.PromptHistoryLoaded) return;
        vm.PromptHistoryLoaded = true;
        var begunEarlier = vm.PromptHistory.ToList();
        var records = PromptStore.Load(vm.Id);
        if (records is null)
        {
            records = PromptTimeline.Backfill(vm.Id, TimelineRows(vm.Entries));
            if (records.Count > 0) PromptStore.Save(vm.Id, records);
        }
        var repaired = false;
        foreach (var record in records.Where(r => r.Outcome == PromptOutcome.Running && r.Id != vm.CurrentPromptId))
        {
            record.Outcome = PromptOutcome.Stopped;
            record.Reason = "Vision quit before it finished.";
            repaired = true;
        }
        foreach (var record in begunEarlier.Where(r => records.All(s => s.Id != r.Id))) records.Add(record);
        vm.PromptHistory.Clear();
        vm.PromptHistory.AddRange(records.OrderBy(r => r.At));
        if (repaired) SavePrompts(vm);
        vm.NotifyPromptsChanged();
    }

    /// <summary>The chat's timeline, loaded (its transcript first, so a backfill has something to read).</summary>
    public IReadOnlyList<PromptRecord> PromptsOf(SessionVM vm)
    {
        if (!vm.PromptHistoryLoaded)
        {
            Hydrate(vm);
            LoadPromptHistory(vm);
        }
        return vm.PromptHistory;
    }

    private static IEnumerable<PromptTimeline.Row> TimelineRows(IEnumerable<ChatEntryVM> entries)
    {
        foreach (var entry in entries)
        {
            switch (entry)
            {
                case MessageEntryVM m:
                    yield return new PromptTimeline.Row(m.Role switch
                    {
                        MessageRole.User => "user",
                        MessageRole.Assistant => "assistant",
                        MessageRole.Notice => "notice",
                        _ => "error",
                    }, m.Text, m.At);
                    break;
                case ToolEntryVM t:
                    yield return new PromptTimeline.Row("tool", t.Summary ?? t.Name, t.At);
                    break;
                case CompactionEntryVM c:
                    yield return new PromptTimeline.Row("compaction", c.Summary, c.At);
                    break;
            }
        }
    }

    private void SavePrompts(SessionVM vm)
    {
        PromptStore.Save(vm.Id, vm.PromptHistory);
        vm.NotifyPromptsChanged();
        RaiseRunsChanged();
    }

    /// <summary>A prompt started working: its row goes in the timeline as Running.</summary>
    private PromptRecord BeginPrompt(SessionVM vm, string text, PromptSource source)
    {
        PromptsOf(vm);
        string? taskId = null;
        if (_pendingPromptSource.Remove(vm.Id, out var pending))
        {
            taskId = pending.TaskId;
            if (source != PromptSource.Goal) source = pending.Source;
        }
        // A prompt still open can't stay Running behind a new one.
        if (vm.CurrentPrompt is { Outcome: PromptOutcome.Running }) FinishPrompt(vm, PromptOutcome.Unknown, null);
        vm.TurnPromptTokens = 0;
        vm.TurnCompletionTokens = 0;
        var record = new PromptRecord
        {
            ChatId = vm.Id, Text = text, Source = source, At = DateTimeOffset.Now, Outcome = PromptOutcome.Running,
            TaskKind = vm.TurnPhase?.TaskKind?.RawValue(), TurnId = vm.TurnPhase?.TurnId,
        };
        // The chat's own model (its route, else the primary) until the turn resolves the route it really runs on.
        if (ChatRoute(vm) is { } route)
        {
            record.Route = RouteLabel(route);
            record.Model = Effective(route).Model;
        }
        vm.PromptHistory.Add(record);
        vm.CurrentPromptId = record.Id;
        if (taskId is not null) _chatTaskRecord[taskId] = record.Id;
        SavePrompts(vm);
        PushPrompt(vm, record);
        return record;
    }

    /// <summary>The prompt's row landed in the transcript: noted, so Jump finds it after a relaunch too.</summary>
    private void PromptEntryLanded(SessionVM vm, string entryId, string displayText)
    {
        if (vm.CurrentPrompt is not { EntryText: null } record) return;
        record.EntryText = displayText;
        record.EntryAt = vm.Entries.LastOrDefault(e => e.Id == entryId)?.At ?? DateTimeOffset.Now;
        SavePrompts(vm);
    }

    private void PromptPlanned(SessionVM vm, TurnPhaseState state)
    {
        if (vm.CurrentPrompt is not { } record) return;
        record.EtaSeconds = state.EtaSeconds;
        record.TaskKind = state.TaskKind?.RawValue();
        record.TurnId ??= state.TurnId;
        vm.NotifyPromptsChanged();
    }

    /// <summary>A goal moved on to its next round.</summary>
    private void PromptRound(SessionVM vm, int round)
    {
        if (vm.CurrentPrompt is not { } record) return;
        record.Rounds = round;
        vm.NotifyPromptsChanged();
    }

    /// <summary>The prompt is over: how it ended, how long it took against the estimate, the attempts, the evidence and the
    /// results note, what it ran on and what it used. Idempotent.</summary>
    private void FinishPrompt(SessionVM vm, PromptOutcome outcome, string? reason)
    {
        if (vm.CurrentPromptId is not { } id) return;
        vm.CurrentPromptId = null;
        if (vm.PromptHistory.LastOrDefault(r => r.Id == id) is not { Outcome: PromptOutcome.Running } record) return;
        record.Outcome = outcome;
        record.FinishedAt = DateTimeOffset.Now;
        if (reason is not null && outcome != PromptOutcome.Completed) record.Reason = TextUtil.Prefix(reason, 600);
        var notes = new List<string>();
        if (vm.TurnPhase is { } state && (record.TurnId is null || record.TurnId == state.TurnId))
        {
            record.TurnId = state.TurnId;
            record.EtaSeconds = state.EtaSeconds ?? record.EtaSeconds;
            record.TaskKind = state.TaskKind?.RawValue() ?? record.TaskKind;
            record.Attempts = state.Loopbacks + 1;
            // The record keeps the newest eight, each trimmed, so a long chat's timeline file stays small (as on the Mac).
            record.Evidence = state.Evidence.TakeLast(MaxRecordEvidence)
                .Select(e => e with { Detail = TextUtil.Prefix(e.Detail, MaxEvidenceDetail) }).ToList();
            if (state.Phase == TurnPhase.Results && state.Steps.LastOrDefault()?.Text is { } line) notes.Add(line);
        }
        var start = (record.EntryAt ?? record.At).AddSeconds(-1);
        if (vm.Entries.OfType<MessageEntryVM>().LastOrDefault(m => m.Role == MessageRole.Assistant && m.At >= start)?.Text.Trim() is { Length: > 0 } closing)
            notes.Add(TextUtil.Prefix(closing, 600));
        if (notes.Count > 0) record.CompletionNote = string.Join("\n\n", notes);
        record.PromptTokens = vm.TurnPromptTokens;
        record.CompletionTokens = vm.TurnCompletionTokens;
        if (vm.TurnModel is { } model) record.Model = model;
        if (vm.TurnRoute is { } routeName) record.Route = routeName;
        SavePrompts(vm);
        PushPrompt(vm, record);
    }

    private const int MaxRecordEvidence = 8;
    private const int MaxEvidenceDetail = 400;

    /// <summary>Signed in to Guardian, or with a route through it: the prompt goes to the account's prompt history (Guardian's
    /// Prompt Notebook) — when it starts and again when it ends, with the same text and time, which Guardian keeps as one
    /// row. Silent on failure: the timeline file has it.</summary>
    private void PushPrompt(SessionVM vm, PromptRecord record)
    {
        if (GuardianEta() is not { } client) return;
        var snapshot = record.Clone();
        var title = vm.Title;
        var project = vm.ProjectName;
        _ = PushAsync();

        async Task PushAsync()
        {
            try
            {
                await client.PushPromptsAsync([(snapshot, title, project)]);
            }
            catch (Exception error) when (error is GuardianException or HttpRequestException or TaskCanceledException)
            {
                // The timeline file has it; Guardian's copy is a convenience.
            }
        }
    }

    /// <summary>The transcript row a record points at, in a loaded chat.</summary>
    public string? EntryIdFor(PromptRecord record, SessionVM vm)
    {
        Hydrate(vm);
        // A prompt the model never received became a notice ("Not delivered to the model: …"): still its row.
        var candidates = vm.Entries.OfType<MessageEntryVM>()
            .Where(m => m.Role == MessageRole.User || (m.Role == MessageRole.Notice && m.Text.StartsWith(UndeliveredPrefix, StringComparison.Ordinal)))
            .Select(m => new PromptTimeline.EntryRef(m.Id, m.Text, m.At));
        return PromptTimeline.MatchEntry(record, candidates);
    }

    /// <summary>Every chat's timeline on this PC, for All Prompts: the loaded ones as they are, the others read from disk —
    /// and a chat that never had a timeline file is backfilled from its stored transcript (once).</summary>
    public IReadOnlyList<PromptRecord> AllPromptRecords()
    {
        var output = new List<PromptRecord>();
        foreach (var vm in Sessions)
        {
            if (vm.PromptHistoryLoaded)
            {
                output.AddRange(vm.PromptHistory);
                continue;
            }
            if (PromptStore.Load(vm.Id) is { } stored)
            {
                output.AddRange(stored);
                continue;
            }
            var rows = Log.LoadItems(vm.Id).Select(r => new PromptTimeline.Row(r.Kind, r.Text ?? "", r.At));
            var records = PromptTimeline.Backfill(vm.Id, rows);
            if (records.Count > 0) PromptStore.Save(vm.Id, records);
            output.AddRange(records);
        }
        return output;
    }

    // MARK: - Chat tasks ("Add to tasks")

    /// <summary>Chats whose prompt tasks are paused, with the banner's reason. A paused list keeps its tasks; Start (or
    /// Resume) carries on.</summary>
    private readonly Dictionary<string, string> _chatTasksPaused = new();

    /// <summary>Why <paramref name="chatId"/>'s prompt tasks are holding, if they are.</summary>
    public string? ChatTasksPaused(string chatId) => _chatTasksPaused.GetValueOrDefault(chatId);

    /// <summary>Queue <paramref name="text"/> as this chat's next task: it runs here, as the chat's next prompt, exactly as
    /// written, once everything before it is done — at once when the chat is free and its list isn't paused.</summary>
    /// <param name="attachments">The composer's chips (files, pictures, a large paste): kept with the task in the chat's
    /// attachments folder and sent with the prompt when its turn comes.</param>
    public QueueTask? AddToTasks(string chatId, string text, bool atFront = false, IReadOnlyList<MessageAttachment>? attachments = null)
    {
        var trimmed = text.Trim();
        attachments ??= [];
        if ((trimmed.Length == 0 && attachments.Count == 0) || Session(chatId) is not { } vm) return null;
        IReadOnlyList<QueuedAttachment> kept;
        try
        {
            kept = AttachmentStoreFor(chatId).SaveQueued(attachments);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            vm.Note($"Couldn't keep the attachments for the task list: {error.Message}", MessageRole.Error);
            return null;
        }
        var title = ChatTaskRules.TitleFor(trimmed, attachments.Select(a => a.Digest?.Format == DigestFormat.Pasted ? "Pasted text" : a.Name).ToList());
        var task = Queue.Add(title, trimmed, atFront, vm.WorkspacePath, chatId, asPrompt: true, attachments: kept);
        var position = Queue.Position(task.Id) ?? 1;
        var next = Queue.NextTaskFor(chatId);
        var carries = kept.Count == 0 ? "" : " " + ChatTaskRules.CarriesNotice(kept.Count);
        if (ChatTasksPaused(chatId) is not null)
            vm.Note($"📋 Added to this chat's tasks (#{position}) — the list is paused; Resume (Ctrl+Shift+Q) carries on.{carries}");
        else if (!IsQueueRunning(chatId) && next is { AsPrompt: false })
            vm.Note($"📋 Added to this chat's tasks (#{position}) — after “{next.Title}”, which waits for Start (Ctrl+Shift+Q).{carries}");
        else if (vm.Running || _runs.ContainsKey(chatId) || IsQueueRunning(chatId) || next?.Id != task.Id)
            vm.Note(ChatTaskRules.AddedNotice(position, startsNow: false) + carries);
        PumpChatTasks(chatId);
        RaiseRunsChanged();
        return Queue.Find(task.Id) ?? task;
    }

    /// <summary>Carry on with a paused list.</summary>
    public void ResumeChatTasks(string chatId)
    {
        _chatTasksPaused.Remove(chatId);
        PumpChatTasks(chatId);
        QueueStateChanged();
    }

    /// <summary>Hold the list: the current work finishes, nothing new starts until Resume.</summary>
    public void PauseChatTasks(string chatId)
    {
        _chatTasksPaused[chatId] = "Paused — nothing new starts until you resume.";
        if (IsQueueRunning(chatId) && QueueActiveTask(chatId) is null) StopQueue(chatId);
        QueueStateChanged();
    }

    /// <summary>Start the chat's list for its prompt tasks: when the next waiting task is one (an ordinary task waits for
    /// Start), nothing is paused and the list isn't already going. It starts once the chat is free.</summary>
    private void PumpChatTasks(string chatId)
    {
        if (IsQueueRunning(chatId) || _chatTasksPaused.ContainsKey(chatId) || !Config.IsConfigured) return;
        if (Queue.NextTaskFor(chatId) is not { AsPrompt: true }) return;
        StartQueue(chatId, resumeOnLaunch: false);
    }

    /// <summary>Some work in a chat ended (a turn, a goal): what the chat has queued may start, or — after a Stop, a failure,
    /// a goal that needs the person — wait.</summary>
    private void AfterWork(SessionVM vm, PromptOutcome outcome)
    {
        if (IsQueueRunning(vm.Id) || Queue.NextTaskFor(vm.Id) is not { AsPrompt: true }) return;
        if (!ChatTaskRules.ContinuesAfter(outcome))
        {
            _chatTasksPaused.TryAdd(vm.Id, ChatTaskRules.PauseReason(outcome));
            QueueStateChanged();
            return;
        }
        _dispatcher.BeginInvoke(() => PumpChatTasks(vm.Id));
    }

    /// <summary>At launch: prompt tasks never start by themselves when Vision opens.</summary>
    private void PausePromptTasksAtLaunch()
    {
        foreach (var chatId in Queue.Tasks.Where(t => t is { AsPrompt: true, Status: QueueTaskStatus.Queued } && t.SessionId is not null)
                     .Select(t => t.SessionId!).Distinct())
        {
            if (!Config.QueueResumeChats.Contains(chatId)) _chatTasksPaused[chatId] = ChatTaskRules.PausedAtLaunch;
        }
    }

    /// <summary>Run one prompt task: sent exactly as if the person sent it now (a /goal, a skill or a plain prompt), with its
    /// own row in the Prompts timeline. Its outcome decides the task's status and whether the list moves on.</summary>
    private async Task<(WorkOutcome Outcome, string? Pause)> WorkPromptAsync(SessionVM vm, QueueTask task, ChatQueueRun run)
    {
        // What it was queued with goes with it, read again as when it was attached.
        IReadOnlyList<MessageAttachment> attachments = [];
        if (task.Attachments.Count > 0)
        {
            if (AttachmentStoreFor(vm.Id).LoadQueued(task.Attachments, out var missing) is not { } loaded)
            {
                Queue.Start(task.Id, vm.Id);
                Queue.Finish(task.Id, QueueTaskStatus.Failed, $"its attachment “{missing}” is no longer on this PC", vm.Id);
                QueueStateChanged();
                return (WorkOutcome.Blocked, ChatTaskRules.PauseReason(PromptOutcome.Failed));
            }
            attachments = loaded;
        }
        Queue.Start(task.Id, vm.Id);
        run.ActiveTaskId = task.Id;
        QueueStateChanged();
        _pendingPromptSource[vm.Id] = (PromptSource.Task, task.Id);
        _autoContinuations[vm.Id] = 0;
        _stoppedChats.Remove(vm.Id);
        PromptOutcome outcome;
        string? reason = null;
        var refused = false;
        try
        {
            _refusedWhileSwitching.Remove(vm.Id);
            _dispatchingTask.Add(vm.Id);
            Task? work;
            try
            {
                work = Dispatch(vm, task.PromptText, attachments);
            }
            finally
            {
                _dispatchingTask.Remove(vm.Id);
            }
            // Refused (the chat's model server started switching models just now): it never ran.
            refused = _refusedWhileSwitching.Remove(vm.Id);
            if (work is not null) await work;
            var record = _chatTaskRecord.Remove(task.Id, out var recordId) ? vm.PromptHistory.LastOrDefault(r => r.Id == recordId) : null;
            // A command ("/help", "/think high") has no prompt row: it simply ran.
            outcome = record?.Outcome ?? PromptOutcome.Completed;
            reason = record?.Reason;
        }
        finally
        {
            _pendingPromptSource.Remove(vm.Id);
            if (run.ActiveTaskId == task.Id) run.ActiveTaskId = null;
        }
        if (refused)
        {
            // Back in line, in its place: the list waits for the server (WorkPromptTaskAsync) and then runs it.
            Queue.MarkStopped(task.Id, "Waiting — the model server was switching models; it runs once it's ready.");
            QueueStateChanged();
            return (WorkOutcome.Finished, null);
        }
        if (Queue.Find(task.Id)?.Status == QueueTaskStatus.Running)
            Queue.Finish(task.Id, ChatTaskRules.Status(outcome), outcome == PromptOutcome.Stopped ? "Stopped." : reason, vm.Id);
        QueueStateChanged();
        if (ChatTaskRules.ContinuesAfter(outcome)) return (WorkOutcome.Finished, null);
        return (outcome == PromptOutcome.Stopped ? WorkOutcome.Stopped : WorkOutcome.Blocked, ChatTaskRules.PauseReason(outcome));
    }

    // MARK: - What the chat is doing now (its first task)

    /// <summary>The chat's current work, shown as the first task: the running goal, task or prompt, with its plan steps.
    /// Null when the chat is idle.</summary>
    public CurrentWork? CurrentWorkFor(SessionVM vm)
    {
        if (!vm.Running && !_runs.ContainsKey(vm.Id)) return null;
        var record = vm.CurrentPrompt;
        string title, kind;
        string? taskId = null;
        if (_queueSessions.TryGetValue(vm.Id, out var queued) && Queue.Find(queued) is { } goalTask)
        {
            (title, kind, taskId) = (goalTask.Title, "Task", goalTask.Id);
        }
        else if (QueueActiveTask(vm.Id) is { } activeId && Queue.Find(activeId) is { AsPrompt: true } promptTask)
        {
            (title, kind, taskId) = (vm.Goal?.Text ?? promptTask.PromptText, vm.Goal is null ? "Task" : "Task · goal", promptTask.Id);
        }
        else if (vm.Goal is { } goal)
        {
            (title, kind) = (goal.Text, "Goal");
        }
        else if (record is not null)
        {
            (title, kind) = (record.Text, record.Source.Label());
        }
        else
        {
            (title, kind) = (vm.Activity ?? "Working…", "Working");
        }
        return new CurrentWork(title, kind, record?.At ?? vm.Goal?.Started ?? vm.TurnPhase?.StartedAt ?? DateTimeOffset.Now,
            vm.Goal?.Round, vm.TurnPhase, vm.Todos, taskId);
    }

    // MARK: - Background activity

    /// <summary>Everything working right now, across chats, each with the model and server it runs on, what it was given, how
    /// long it has run, and what it used.</summary>
    public IReadOnlyList<ActivityItem> ActivityItems()
    {
        var chats = new List<ChatActivity>();
        foreach (var vm in Sessions)
        {
            var listRunning = IsQueueRunning(vm.Id);
            var listStopping = IsQueueStopping(vm.Id);
            var waiting = Queue.QueuedCountFor(vm.Id);
            var anything = vm.Running || vm.Agents.Any(a => a.IsRunning) || vm.RunningBackgroundJobs.Count > 0 || listRunning || listStopping || waiting > 0;
            if (!anything) continue;
            var work = CurrentWorkFor(vm);
            var active = QueueActiveTask(vm.Id) is { } id ? Queue.Find(id) : Queue.RunningTaskFor(vm.Id);
            chats.Add(new ChatActivity(vm.Id, vm.Title)
            {
                Running = vm.Running,
                Stopping = vm.Stopping,
                Phase = vm.TurnPhase,
                GoalRound = vm.Goal?.Round,
                WorkKind = work?.Kind,
                WorkTitle = work is null ? null : TextUtil.Prefix(work.Title.Split('\n')[0], 120),
                WorkStartedAt = work?.StartedAt,
                Model = vm.TurnModel ?? (ChatRoute(vm) is { } p ? Effective(p).Model : null),
                Route = vm.TurnRoute ?? (ChatRoute(vm) is { } r ? RouteLabel(r) : null),
                RunningTool = vm.RunningTool is { } tool ? $"{tool.Label.ToLowerInvariant()} {TextUtil.Prefix(tool.Preview.Replace('\n', ' '), 80)}" : null,
                ActivityLine = vm.Retry is { } retry ? $"waiting for the model ({retry.Reason})" : vm.Activity,
                Tokens = vm.TurnPromptTokens + vm.TurnCompletionTokens,
                Agents = vm.Agents.ToList(),
                RunningJobs = vm.RunningBackgroundJobs,
                ListRunning = listRunning,
                ListStopping = listStopping,
                ListActiveTask = active,
                QueuedTasks = waiting,
                NextTaskTitle = Queue.NextTaskFor(vm.Id)?.Title,
                PausedReason = ChatTasksPaused(vm.Id),
            });
        }
        return RunActivity.Build(chats);
    }

    /// <summary>How many things are working now (the toolbar's count).</summary>
    public int ActivityCount() => RunActivity.Count(ActivityItems());

    /// <summary>Stop one item of the activity list.</summary>
    public void StopActivity(ActivityItem item)
    {
        if (item.ChatId is not { } chatId) return;
        switch (item.Kind)
        {
            case ActivityKind.MainAgent:
            case ActivityKind.Subagent: // a foreground subagent belongs to its parent's tool call: stopping it stops the turn
                StopSession(chatId);
                break;
            case ActivityKind.BackgroundAgent:
                if (item.BackgroundJobId is { } jobId) StopBackgroundAgent(chatId, jobId);
                else if (Session(chatId) is { } vm && vm.Agents.FirstOrDefault(a => a.Id == item.AgentRunId) is { } run
                         && vm.RunningBackgroundJobs.FirstOrDefault(j => j.Description == run.Description) is { } job)
                    StopBackgroundAgent(chatId, job.Id);
                break;
            case ActivityKind.QueueRunner:
                StopQueue(chatId);
                break;
            case ActivityKind.ChatTasks:
                PauseChatTasks(chatId);
                break;
        }
    }

    /// <summary>The stamped log behind one activity line, as text: a subagent's own log, the main agent's turn steps.</summary>
    public string ActivityLog(ActivityItem item)
    {
        var vm = item.ChatId is { } id ? Session(id) : null;
        var lines = new List<string> { $"{RunActivity.KindLabel(item.Kind)} · {item.ChatTitle}", item.Title };
        if (item.Placement is { } placement) lines.Add("Runs on: " + placement);
        if (item.Assignment is { } assignment) lines.Add("Assigned: " + assignment);
        lines.Add("Status: " + item.Status);
        lines.Add("");
        if (vm is not null && item.AgentRunId is { } runId && vm.Agents.FirstOrDefault(a => a.Id == runId) is { } run)
        {
            if (run.Prompt is { Length: > 0 } prompt) lines.AddRange(["Task given:", prompt, ""]);
            if (run.TotalTokens > 0) lines.Add($"Tokens: {run.PromptTokens:N0} in · {run.CompletionTokens:N0} out");
            lines.AddRange(run.Log.Select(l => $"{PhaseText.Stamp(l.At)}  {l.Text}"));
            if (run.Report is { Length: > 0 } report) lines.AddRange(["", "Report:", report]);
        }
        else if (vm?.TurnPhase is { } phase && item.Kind == ActivityKind.MainAgent)
        {
            lines.Add(PhaseText.RuntimeLine(phase, DateTimeOffset.Now) + (phase.EtaBasis is { } basis ? $" ({basis})" : ""));
            lines.AddRange(phase.Steps.Select(s => $"{PhaseText.Stamp(s.At)}  {s.Text}"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}
