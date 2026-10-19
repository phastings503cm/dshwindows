using System.IO;
using System.Windows.Threading;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - What the agent itself needs (10-19, item 20)
//
//  * Checkpoints and Rewind: every turn's file changes are recorded (the files as they were, in a content-addressed store),
//    the turn ends with a "Files changed (N)" row, and "Rewind this turn…" (or /rewind) puts them back — asking first about
//    any file that changed since. A rewind is recorded as a turn of its own, so it can be rewound in turn. The model hears at
//    its next message which files went back.
//  * MCP servers: the servers in Settings › MCP Servers (added there, or brought in from Claude Code, Claude Desktop and
//    Cursor) are connected at launch, and their tools are offered to every chat they apply to.
//  * Sensitive chats: a chat marked Sensitive (new chats of a project start so when the project is set to) uses only private
//    routes, or others through the shield; engines that read the files themselves are refused (see SensitivityPolicy).
// The behaviour and the words are Vision for macOS's (AppTransport+Checkpoints/+MCP/+Sensitivity.swift). UI thread only.

public sealed partial class AgentHost
{
    // MARK: Checkpoints

    private CheckpointStore? _checkpointStore;
    private readonly Dictionary<string, CheckpointSlot> _checkpointSlots = new();
    /// <summary>Chat → what its next message tells the model (after a rewind: which files went back).</summary>
    private readonly Dictionary<string, List<string>> _modelNotes = new();
    private bool _checkpointsPruned;

    /// <summary>checkpoints\ in the data folder.</summary>
    public CheckpointStore Checkpoints => _checkpointStore ??= new CheckpointStore(Path.Combine(DataFolder, CheckpointStore.FolderName));

    /// <summary>Raised when a chat's checkpoints change (one was added or rewound) — the Rewind window listens.</summary>
    public event Action<string>? CheckpointsChanged;

    /// <summary>/rewind: the UI opens the Rewind window for the chat — at a turn to confirm (null = the list).</summary>
    public Action<SessionVM, string?>? OnRewindCommand { get; set; }

    private CheckpointSlot CheckpointSlotFor(string sessionId)
    {
        if (!_checkpointSlots.TryGetValue(sessionId, out var slot)) _checkpointSlots[sessionId] = slot = new CheckpointSlot();
        return slot;
    }

    /// <summary>A turn begins: what its tools change is recorded under its prompt's id ("auto-…" for the harness's own turn).</summary>
    private void BeginCheckpoint(SessionVM vm, string prompt)
    {
        var slot = CheckpointSlotFor(vm.Id);
        if (slot.Current is not null) FinishCheckpoint(vm); // a turn that never closed its own
        slot.Current = new TurnRecorder(Checkpoints, vm.Id, vm.CurrentPromptId ?? "auto-" + Guid.NewGuid(), vm.WorkingFolder,
            TurnCheckpoint.TitleOf(prompt));
    }

    /// <summary>The turn is over: its checkpoint is saved and, when it changed files, "Files changed (N)" goes under it.</summary>
    private void FinishCheckpoint(SessionVM vm)
    {
        if (!_checkpointSlots.TryGetValue(vm.Id, out var slot) || slot.Current is not { } recorder) return;
        slot.Current = null;
        TurnCheckpoint? turn;
        try
        {
            turn = recorder.Finish();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            turn = null;
        }
        PruneCheckpointsOnce();
        if (turn is not null) AddCheckpointRow(vm, turn);
    }

    private void AddCheckpointRow(SessionVM vm, TurnCheckpoint turn)
    {
        vm.EndStreaming();
        vm.Entries.Add(new CheckpointEntryVM(turn.Id, turn.Heading) { Turn = turn });
        vm.NotifyContentChanged();
        Log.RecordItem(vm.Id, "checkpoint", turn.Heading, "checkpoint", turn.Id);
        CheckpointsChanged?.Invoke(vm.Id);
    }

    /// <summary>Once per launch, off the UI thread: records older than 30 days go, then the oldest past 1 GB.</summary>
    private void PruneCheckpointsOnce()
    {
        if (_checkpointsPruned) return;
        _checkpointsPruned = true;
        var store = Checkpoints;
        _ = Task.Run(() =>
        {
            try
            {
                store.Prune();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Next launch.
            }
        });
    }

    /// <summary>The chat's recent turns that changed files, newest first.</summary>
    public IReadOnlyList<TurnCheckpoint> CheckpointsOf(SessionVM vm, int limit = 30) => Checkpoints.List(vm.Id).Take(limit).ToList();

    public TurnCheckpoint? CheckpointOf(SessionVM vm, string turnId) => Checkpoints.Load(vm.Id, turnId);

    /// <summary>What rewinding a turn would do now (null when its record is gone).</summary>
    public RewindPlan? PlanRewind(SessionVM vm, string turnId) =>
        CheckpointOf(vm, turnId) is { } turn ? CheckpointRewind.Plan(Checkpoints, turn) : null;

    /// <summary>Why a chat's turns can't be rewound right now (null = they can).</summary>
    public static string? RewindBlocked(SessionVM vm) => vm.Running ? "Wait for the turn to finish (or stop it) to rewind it." : null;

    /// <summary>Rewind a turn the person confirmed: put its files back (and, with <paramref name="includeConflicts"/>, also the
    /// ones changed since). The rewind is recorded as a turn of its own (it gets its own "Files changed" row); the chat says
    /// what happened, and the model hears it with the next message.</summary>
    public RewindResult Rewind(SessionVM vm, RewindPlan plan, bool includeConflicts)
    {
        if (RewindBlocked(vm) is { } why) throw new InvalidOperationException(why);
        var recorder = new TurnRecorder(Checkpoints, vm.Id, "rewind-" + Guid.NewGuid(), vm.WorkingFolder, $"Rewind of “{plan.Turn.Title}”");
        var result = CheckpointRewind.Apply(Checkpoints, plan, includeConflicts, recorder);
        var root = vm.WorkspacePath ?? plan.Turn.Workspace;
        var text = CheckpointRewind.Summary(result, plan.Turn.Title, root);
        var failed = result.Failed.Count > 0;
        vm.Note(text, failed ? MessageRole.Error : MessageRole.Notice);
        Log.RecordItem(vm.Id, failed ? "error" : "notice", text, isError: failed);
        if (result.ChangedCount > 0)
        {
            ModelNotesFor(vm.Id).Add(CheckpointRewind.ModelNote(result, plan.Turn.Title, root));
            // The editor reloads what went back.
            var changes = result.Restored.Select(p => new FileChange(p, FileChangeKind.Modified))
                .Concat(result.Removed.Select(p => new FileChange(p, FileChangeKind.Deleted))).ToList();
            vm.RecordFileChanges(changes);
            OnFilesChanged?.Invoke(changes);
        }
        // The turn's rows show it rewound; the rewind gets its own row.
        var updated = CheckpointOf(vm, plan.Turn.Id);
        foreach (var entry in vm.Entries.OfType<CheckpointEntryVM>().Where(e => e.TurnId == plan.Turn.Id)) entry.Turn = updated;
        if (recorder.Finish() is { } rewind) AddCheckpointRow(vm, rewind);
        else CheckpointsChanged?.Invoke(vm.Id);
        return result;
    }

    private List<string> ModelNotesFor(string sessionId)
    {
        if (!_modelNotes.TryGetValue(sessionId, out var notes)) _modelNotes[sessionId] = notes = [];
        return notes;
    }

    /// <summary>The words for the model that go in front of the next message (taken: they go once).</summary>
    private string? TakeModelNotes(string sessionId) =>
        _modelNotes.Remove(sessionId, out var notes) && notes.Count > 0 ? string.Join("\n\n", notes) : null;

    /// <summary>/rewind: the recent turns that changed files (and the Rewind window); /rewind n asks to rewind turn n.</summary>
    private void RewindCommand(string? argument, SessionVM vm)
    {
        var records = CheckpointsOf(vm);
        if (records.Count == 0)
        {
            vm.Note(NothingToRewind);
            return;
        }
        var root = vm.WorkspacePath;
        if (argument is { } arg && int.TryParse(arg.Trim(), out var n))
        {
            if (n < 1 || n > records.Count)
            {
                vm.Note($"There is no turn {n} — `/rewind` lists {records.Count}.", MessageRole.Error);
                return;
            }
            OnRewindCommand?.Invoke(vm, records[n - 1].Id);
            return;
        }
        var lines = records.Take(10).Select((record, index) =>
        {
            var files = record.Changed;
            var names = string.Join(", ", files.Take(3).Select(f => Path.GetFileName(CheckpointRewind.Display(f.Path, root))))
                        + (files.Count > 3 ? $" +{files.Count - 3}" : "");
            return $"{index + 1}. {record.StartedAt.ToLocalTime():HH:mm} “{record.Title}” — {files.Count} file{(files.Count == 1 ? "" : "s")} ({names})"
                   + (record.IsRewound ? " · rewound" : "");
        });
        vm.Note("Recent turns that changed files (newest first):\n" + string.Join("\n", lines) +
                "\n\n`/rewind <n>` undoes turn n's file changes (you confirm first).");
        OnRewindCommand?.Invoke(vm, null);
    }

    public const string NothingToRewind =
        "No turn in this chat has changed files yet — nothing to rewind. (Changes made by shell commands are tracked in git repositories only.)";

    // MARK: MCP servers

    private McpServerStore? _mcpStore;
    private McpManager? _mcp;

    /// <summary>Settings › MCP Servers' list (mcp-servers.json in the data folder).</summary>
    public McpServerStore McpServers => _mcpStore ??= new McpServerStore(Path.Combine(DataFolder, McpServerStore.FileName));

    /// <summary>The servers' connections.</summary>
    public McpManager Mcp
    {
        get
        {
            if (_mcp is not null) return _mcp;
            _mcp = new McpManager(McpServers);
            _mcp.Changed += () => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => McpChanged?.Invoke());
            McpServers.Changed += StartMcp;
            return _mcp;
        }
    }

    /// <summary>Raised on the UI thread when a server connects, fails or lists different tools.</summary>
    public event Action? McpChanged;

    /// <summary>Connect the servers that are on (at launch, and after the list changes). Off the UI thread: starting a program
    /// or reaching a URL can take a moment.</summary>
    public void StartMcp()
    {
        var manager = Mcp;
        _ = Task.Run(() => manager.SyncAsync());
    }

    /// <summary>/mcp: the servers, their state and their tools, as a note.</summary>
    private void McpCommand(SessionVM vm)
    {
        var statuses = Mcp.Statuses;
        if (statuses.Count == 0)
        {
            vm.Note("No MCP servers yet. Add one, or import the ones Claude Code, Claude Desktop or Cursor already have, in Settings › MCP Servers.");
            return;
        }
        vm.Note("MCP servers:\n" + string.Join("\n", statuses.Select(s =>
        {
            var scope = s.Config.Project is { } project ? $" (only in {Path.GetFileName(project.TrimEnd('\\', '/'))})" : "";
            var applies = s.Config.AppliesTo(vm.WorkspacePath) ? "" : " — not used in this chat";
            var offered = s.Tools.Where(t => !s.Config.DisabledTools.Contains(t.Name)).ToList();
            var tools = s.State == McpState.Connected && offered.Count > 0
                ? $" · {offered.Count} tool{(offered.Count == 1 ? "" : "s")}: " + string.Join(", ", offered.Take(8).Select(t => t.Name)) + (offered.Count > 8 ? " …" : "")
                : "";
            return $"• **{s.Config.Name}**{scope} — {s.Headline}{applies}{tools}";
        })) + "\n\nTheir tools are offered as `mcp__<server>__<tool>`. Settings › MCP Servers adds, imports and switches them; `/mcp reload` reconnects them.");
    }

    private void StopMcp()
    {
        if (_mcp is not { } manager) return;
        try
        {
            Task.Run(() => manager.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(4));
        }
        catch (Exception error) when (error is AggregateException or TimeoutException)
        {
            // The programs get end-of-file on their input when Vision exits.
        }
    }

    // MARK: Sensitive chats

    /// <summary>Whether the shield is switched on (Settings › AI Models › Sensitive data).</summary>
    public bool ShieldOn => Config.Privacy.Mask;

    public bool IsSensitive(SessionVM vm) => vm.Sensitive;

    /// <summary>Raised when a chat's sensitivity (or a project's default) changes — the header listens.</summary>
    public event Action? SensitivityChanged;

    /// <summary>A new chat starts Sensitive when its project (or, without one, chats without a project) is set to.</summary>
    private void AssignInitialSensitivity(SessionVM vm)
    {
        vm.Sensitive = Config.IsSensitiveProject(vm.WorkspacePath);
        Log.SetSensitive(vm.Id, vm.Sensitive);
    }

    /// <summary>Mark a chat Sensitive or not. Its engine is rebuilt at the next turn (the prompt and subagent rules change).</summary>
    public void SetChatSensitive(SessionVM vm, bool on)
    {
        if (vm.Sensitive == on) return;
        vm.Sensitive = on;
        Log.SetSensitive(vm.Id, on);
        var text = on
            ? "🔒 This chat is now Sensitive. " + SensitivityPolicy.Explanation + (SensitiveRefusal(vm) is { } refusal ? "\n\n" + refusal : "")
            : "This chat is no longer Sensitive: every model may be used, with the usual masking for models that aren't private.";
        vm.Note(text);
        Log.RecordItem(vm.Id, "notice", text);
        SensitivityChanged?.Invoke();
    }

    /// <summary>Whether new chats in <paramref name="project"/> (null = without a project) start Sensitive.</summary>
    public bool ProjectStartsSensitive(string? project) => Config.IsSensitiveProject(project);

    public void SetProjectStartsSensitive(string? project, bool on)
    {
        Config.SetSensitiveProject(project, on);
        SensitivityChanged?.Invoke();
    }

    /// <summary>How the chat's model (or <paramref name="route"/>) may be used in it — null when the chat isn't Sensitive (or
    /// has no model).</summary>
    public RouteAdmission? AdmissionFor(SessionVM vm, ProviderProfile? route = null)
    {
        if (!vm.Sensitive || (route ?? ChatRoute(vm)) is not { } chosen) return null;
        return SensitivityPolicy.Admission(chosen, ShieldOn);
    }

    /// <summary>Why the chat can't send right now (a Sensitive chat on a model it may not use), else null.</summary>
    public string? SensitiveRefusal(SessionVM vm) =>
        vm.Sensitive && ChatRoute(vm) is { } route ? SensitivityPolicy.RefusalMessage(route, ShieldOn) : null;

    /// <summary>Whether a Sensitive chat may switch to <paramref name="route"/> (true for every route when it isn't Sensitive).</summary>
    public bool SensitivityAllows(ProviderProfile route, SessionVM? vm) => vm is not { Sensitive: true } || SensitivityPolicy.Admission(route, ShieldOn).IsAllowed;

    /// <summary>The chat header's line for a Sensitive chat (null when it isn't).</summary>
    public string? SensitivityHeader(SessionVM vm) => vm.Sensitive ? SensitivityPolicy.HeaderLine(ChatRoute(vm), ShieldOn) : null;

    /// <summary>Subagents of a Sensitive chat stay on private servers while the shield is off.</summary>
    private bool PrivateSubagentsOnly(SessionVM vm) => vm.Sensitive && SensitivityPolicy.SubagentsPrivateOnly(ShieldOn);

    /// <summary>Pictures (attached, or captured by the screen tools) aren't sent from a Sensitive chat to a model that only gets
    /// it masked: the shield can't check what is in a picture (the model gets the text read out of it instead).</summary>
    private bool WithholdsPictures(SessionVM? vm, ProviderProfile? route) =>
        vm is { Sensitive: true } && route is not null && SensitivityPolicy.Admission(route, ShieldOn).IsMasked;

    /// <summary>/sensitive [on|off].</summary>
    private void SensitiveCommand(string? argument, SessionVM vm)
    {
        switch (argument?.Trim().ToLowerInvariant())
        {
            case "on" or "yes" or "true":
                if (vm.Sensitive) vm.Note("This chat is already Sensitive. " + (SensitivityHeader(vm) ?? ""));
                SetChatSensitive(vm, true);
                break;
            case "off" or "no" or "false":
                if (!vm.Sensitive) vm.Note("This chat isn't Sensitive.");
                SetChatSensitive(vm, false);
                break;
            case null or "":
                vm.Note(vm.Sensitive
                    ? (SensitivityHeader(vm) ?? "Sensitive.") + "\n\n" + SensitivityPolicy.Explanation + "\n\nTurn it off with `/sensitive off`."
                    : "This chat isn't Sensitive: every model may be used, with what the shield detects masked for models that aren't " +
                      "private. `/sensitive on` limits it to private models (or masked ones).");
                break;
            default:
                vm.Note("Use `/sensitive`, `/sensitive on` or `/sensitive off`.", MessageRole.Error);
                break;
        }
    }

    /// <summary>What the system prompt adds for a Sensitive chat (Vision for macOS's words).</summary>
    public const string SensitivePrompt =
        "--- Sensitive chat ---\nThe user marked this chat Sensitive: treat what you read here as confidential. Don't put it into web addresses " +
        "(web_fetch), queue tasks, skills or anything that leaves this chat unless the user asks; give subagents only what their task needs.";

    private void ForgetPowerState(string id)
    {
        _checkpointSlots.Remove(id);
        _modelNotes.Remove(id);
        try
        {
            Checkpoints.DeleteChat(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Pruned by age later.
        }
    }
}
