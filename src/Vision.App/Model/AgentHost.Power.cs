using System.IO;
using System.Windows.Threading;
using Vision.Core;

namespace Vision.App.Model;

// MARK: - What the agent itself needs (10-19, item 20)
//
//  * Checkpoints and Rewind: every turn's file changes are recorded (the files as they were, in a content-addressed store),
//    the turn ends with a "Files changed (N)" row, and "Rewind this turn" (or /rewind) puts them back — asking first about
//    any file that changed since. The model is told at its next turn which files went back.
//  * MCP servers: the servers in Settings › MCP Servers (added there, or brought in from Claude Code, Claude Desktop and
//    Cursor) are connected at launch, and their tools are offered to every chat they apply to.
//  * The sensitivity policy: a Sensitive chat (by its own toggle, or its project's default) uses only private routes, or
//    online ones through the shield (forced on for it); engines that read the files themselves are refused.
// UI thread only, like the rest of AgentHost.

public sealed partial class AgentHost
{
    // MARK: Checkpoints

    private CheckpointStore? _checkpointStore;
    private readonly Dictionary<string, CheckpointSlot> _checkpointSlots = new();
    /// <summary>Chat → what its next turn tells the model about a rewind.</summary>
    private readonly Dictionary<string, string> _rewindNotes = new();
    private bool _checkpointsPruned;

    /// <summary>checkpoints\ in the data folder.</summary>
    public CheckpointStore Checkpoints => _checkpointStore ??= new CheckpointStore(Path.Combine(DataFolder, CheckpointStore.FolderName));

    /// <summary>Raised when a chat's checkpoints change (one was added, rewound or un-rewound) — the /rewind window listens.</summary>
    public event Action<string>? CheckpointsChanged;

    /// <summary>/rewind: the UI opens the list of this chat's recent turns. Null = the host lists them as a note.</summary>
    public Action<SessionVM>? OnRewindCommand { get; set; }

    private CheckpointSlot CheckpointSlotFor(string sessionId)
    {
        if (!_checkpointSlots.TryGetValue(sessionId, out var slot)) _checkpointSlots[sessionId] = slot = new CheckpointSlot();
        return slot;
    }

    /// <summary>A turn begins: what its tools change is recorded under its prompt's id.</summary>
    private void BeginCheckpoint(SessionVM vm, string prompt)
    {
        var slot = CheckpointSlotFor(vm.Id);
        if (slot.Current is not null) FinishCheckpoint(vm); // a turn that never closed its own
        slot.Current = new TurnRecorder(Checkpoints, vm.Id, vm.CurrentPromptId ?? Guid.NewGuid().ToString(), vm.WorkingFolder, prompt);
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
        if (turn is null) return;
        vm.EndStreaming();
        vm.Entries.Add(new CheckpointEntryVM(turn.Id, turn.Title) { Turn = turn });
        vm.NotifyContentChanged();
        Log.RecordItem(vm.Id, "checkpoint", turn.Title, "checkpoint", turn.Id);
        CheckpointsChanged?.Invoke(vm.Id);
    }

    /// <summary>Once per launch, off the UI thread: checkpoints older than 30 days go, then the oldest past 1 GB.</summary>
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

    /// <summary>A chat's checkpoints, newest first.</summary>
    public IReadOnlyList<TurnCheckpoint> CheckpointsOf(SessionVM vm) => Checkpoints.List(vm.Id);

    public TurnCheckpoint? CheckpointOf(SessionVM vm, string turnId) => Checkpoints.Load(vm.Id, turnId);

    /// <summary>What rewinding a turn would do now (null when its checkpoint is gone).</summary>
    public RewindPlan? PlanRewind(SessionVM vm, string turnId) =>
        CheckpointOf(vm, turnId) is { } turn ? CheckpointRewind.Plan(Checkpoints, turn) : null;

    /// <summary>Why a chat's turns can't be rewound right now (null = they can).</summary>
    public static string? RewindBlocked(SessionVM vm) =>
        vm.Running ? "The agent is working in this chat — stop it (Ctrl+.) or wait for it to finish, then rewind." : null;

    /// <summary>Carry out a rewind the person confirmed: the files go back, the chat says so, and the model hears of it at its
    /// next turn.</summary>
    public RewindResult Rewind(SessionVM vm, RewindPlan plan, bool overwriteConflicts)
    {
        if (RewindBlocked(vm) is { } why) throw new InvalidOperationException(why);
        var result = CheckpointRewind.Apply(Checkpoints, plan, overwriteConflicts);
        var moved = plan.Items
            .Where(i => i.Action is RewindAction.Restore or RewindAction.Delete && (!i.Conflict || overwriteConflicts) && !result.Failed.Contains(i.File.Path))
            .ToList();
        AfterRewind(vm, plan.Turn, result, moved.Select(i => new FileChange(i.File.Path,
            i.Action == RewindAction.Delete ? FileChangeKind.Deleted : i.Current is null ? FileChangeKind.Created : FileChangeKind.Modified)).ToList());
        if (moved.Count > 0)
            _rewindNotes[vm.Id] = RewindNote(plan.Turn, moved.Select(i => i.File.Path), undone: false);
        return result;
    }

    /// <summary>Put back what a rewind replaced.</summary>
    public RewindResult UndoRewind(SessionVM vm, string turnId)
    {
        if (RewindBlocked(vm) is { } why) throw new InvalidOperationException(why);
        if (CheckpointOf(vm, turnId) is not { RewoundFrom: { Count: > 0 } from } turn) return new RewindResult(0, 0, 0, []);
        var paths = from.Keys.ToList();
        var result = CheckpointRewind.Undo(Checkpoints, turn);
        var undone = result.Changed == 0 ? "Nothing to undo." : $"Undid the rewind: {result.Changed} {(result.Changed == 1 ? "file is" : "files are")} back as they were before it.";
        AfterRewind(vm, turn, result, paths.Select(p => new FileChange(p, File.Exists(p) ? FileChangeKind.Modified : FileChangeKind.Deleted)).ToList(), undone);
        if (result.Changed > 0) _rewindNotes[vm.Id] = RewindNote(turn, paths, undone: true);
        return result;
    }

    private void AfterRewind(SessionVM vm, TurnCheckpoint turn, RewindResult result, IReadOnlyList<FileChange> changes, string? summary = null)
    {
        if (changes.Count > 0)
        {
            vm.RecordFileChanges(changes);
            OnFilesChanged?.Invoke(changes);
        }
        // Every row of this turn shows its new state.
        var fresh = CheckpointOf(vm, turn.Id);
        foreach (var entry in vm.Entries.OfType<CheckpointEntryVM>().Where(e => e.TurnId == turn.Id)) entry.Turn = fresh;
        var text = summary ?? result.Summary;
        vm.Note(text);
        Log.RecordItem(vm.Id, "notice", text);
        CheckpointsChanged?.Invoke(vm.Id);
    }

    /// <summary>What the model is told at its next turn: which files went back (so it reads them again rather than trusting
    /// what it remembers).</summary>
    public static string RewindNote(TurnCheckpoint turn, IEnumerable<string> paths, bool undone)
    {
        var names = paths.Select(p => turn.Workspace is { } root ? CheckpointRewind.Relative(root, p) : Path.GetFileName(p)).ToList();
        var shown = string.Join(", ", names.Take(12)) + (names.Count > 12 ? $" and {names.Count - 12} more" : "");
        var prompt = turn.Prompt.Length > 0 ? $" (“{TextUtil.Prefix(turn.Prompt, 80)}”)" : "";
        return undone
            ? $"[Automatic message: the person undid their rewind of an earlier turn{prompt}. These files have that turn's changes again: {shown}. Read them again before relying on what you remember of them.]"
            : $"[Automatic message: the person rewound the file changes of an earlier turn{prompt}. These files are back to how they were before it: {shown}. Read them again before relying on what you remember of them.]";
    }

    /// <summary>/rewind without a window: the chat's recent turns that changed files.</summary>
    private void RewindCommand(SessionVM vm)
    {
        if (OnRewindCommand is not null)
        {
            OnRewindCommand(vm);
            return;
        }
        var turns = CheckpointsOf(vm).Take(10).ToList();
        if (turns.Count == 0)
        {
            vm.Note(NothingToRewind);
            return;
        }
        vm.Note("Recent turns that changed files (newest first):\n" + string.Join("\n", turns.Select(t =>
            $"• {t.At.ToLocalTime():MMM d, HH:mm} — “{TextUtil.Prefix(t.Prompt, 60)}” — {t.Title}{(t.IsRewound ? " · rewound" : "")}")));
    }

    public const string NothingToRewind = "Nothing to rewind yet: no turn in this chat has changed files.";

    // MARK: MCP servers

    private McpServerStore? _mcpStore;
    private McpManager? _mcp;
    private readonly Dictionary<string, McpGrants> _mcpGrants = new();

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
            McpServers.Changed += () => StartMcp();
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

    private McpGrants McpGrantsFor(string sessionId)
    {
        if (!_mcpGrants.TryGetValue(sessionId, out var grants)) _mcpGrants[sessionId] = grants = new McpGrants();
        return grants;
    }

    /// <summary>/mcp: the servers, their state and their tools, as a note.</summary>
    private void McpCommand(SessionVM vm)
    {
        var statuses = Mcp.Statuses;
        if (statuses.Count == 0)
        {
            vm.Note("No MCP servers yet. Add one, or bring in the ones Claude Code, Claude Desktop and Cursor already have, in Settings › MCP Servers.");
            return;
        }
        vm.Note("MCP servers:\n" + string.Join("\n", statuses.Select(s =>
        {
            var scope = s.Config.Project is { } project ? $" (only in {Path.GetFileName(project.TrimEnd('\\', '/'))})" : "";
            var applies = s.Config.AppliesTo(vm.WorkspacePath) ? "" : " — not used in this chat";
            var tools = s.State == McpState.Connected && s.Tools.Count > 0
                ? ": " + string.Join(", ", s.Tools.Take(12).Select(t => $"`{McpTool.NameFor(s.Config.Id, t.Name)}`")) + (s.Tools.Count > 12 ? $" and {s.Tools.Count - 12} more" : "")
                : "";
            return $"• **{s.Config.Name}**{scope} — {s.Headline}{applies}{tools}";
        })) + "\nManage them in Settings › MCP Servers.");
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

    // MARK: Sensitivity

    /// <summary>Whether a chat is Sensitive: its own choice, else its project's default.</summary>
    public bool IsSensitive(SessionVM vm) => vm.Sensitive ?? Config.IsSensitiveProject(vm.WorkspacePath);

    /// <summary>Raised when a chat's sensitivity (or a project's default) changes — the header listens.</summary>
    public event Action? SensitivityChanged;

    /// <summary>Mark a chat Sensitive or Standard (null = follow its project's default). Takes effect at its next turn.</summary>
    public void SetChatSensitive(SessionVM vm, bool? sensitive)
    {
        var before = IsSensitive(vm);
        vm.Sensitive = sensitive;
        Log.SetSensitive(vm.Id, sensitive);
        var after = IsSensitive(vm);
        if (before != after)
        {
            var text = after
                ? "This chat is now Sensitive: it uses only private models (your own servers, Bedrock in your AWS account, Guardian)" +
                  (Config.SensitiveAllowsShielded ? " or online models with every sensitive detail masked" : "") +
                  ", never an engine that reads your files itself (Claude Code)."
                : "This chat is now Standard: any model, with the shield's usual masking for models that aren't private.";
            vm.Note(text);
            Log.RecordItem(vm.Id, "notice", text);
        }
        SensitivityChanged?.Invoke();
    }

    /// <summary>Make <paramref name="project"/>'s chats Sensitive (or Standard) by default — every chat there that hasn't chosen.</summary>
    public void SetProjectSensitive(string? project, bool sensitive)
    {
        Config.SetSensitiveProject(project, sensitive);
        SensitivityChanged?.Invoke();
    }

    /// <summary>Whether <paramref name="route"/> (default: the chat's own) may serve the chat, and how.</summary>
    public Admission? AdmissionFor(SessionVM vm, ProviderProfile? route = null)
    {
        route ??= ChatRoute(vm);
        return route is null ? null : RouteAdmission.Admit(route, IsSensitive(vm), Config.SensitiveAllowsShielded);
    }

    /// <summary>Why the chat can't send on its model right now (a Sensitive chat on a route it may not use); null = it can.</summary>
    public string? SensitiveRefusal(SessionVM vm) => AdmissionFor(vm) is { Allowed: false, Refusal: { } why } ? why : null;

    /// <summary>The header's policy text for a chat.</summary>
    public string SensitivityDescription(SessionVM vm) => RouteAdmission.Describe(IsSensitive(vm), AdmissionFor(vm), Config.SensitiveAllowsShielded);

    /// <summary>/sensitive [on|off|default].</summary>
    private void SensitiveCommand(string? argument, SessionVM vm)
    {
        switch (argument?.Trim().ToLowerInvariant())
        {
            case null or "":
                var project = Config.IsSensitiveProject(vm.WorkspacePath) ? "Sensitive" : "Standard";
                vm.Note($"This chat is **{RouteAdmission.Label(IsSensitive(vm))}**{(vm.Sensitive is null ? $" (its project's default)" : $" (its own choice; the project's default is {project})")}.\n" +
                        SensitivityDescription(vm) + "\nChange it with `/sensitive on`, `/sensitive off` or `/sensitive default`, or the Sensitive button at the top.");
                break;
            case "on" or "yes" or "true" or "sensitive":
                SetChatSensitive(vm, true);
                break;
            case "off" or "no" or "false" or "standard":
                SetChatSensitive(vm, false);
                break;
            case "default" or "project" or "reset" or "auto":
                SetChatSensitive(vm, null);
                vm.Note($"This chat follows its project's default now: {RouteAdmission.Label(IsSensitive(vm))}.");
                break;
            default:
                vm.Note($"Unknown setting “{argument}”. Use `/sensitive on`, `/sensitive off` or `/sensitive default`.", MessageRole.Error);
                break;
        }
    }

    /// <summary>A chat's turn refused because of its sensitivity.</summary>
    public sealed class SensitiveRouteException(string message) : Exception(message);

    private void ForgetPowerState(string id)
    {
        _checkpointSlots.Remove(id);
        _rewindNotes.Remove(id);
        _mcpGrants.Remove(id);
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
