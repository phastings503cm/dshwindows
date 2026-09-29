using System.Net.Http;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Dsh.App.Infrastructure;
using Dsh.Core;

namespace Dsh.App.Model;

/// <summary>Drives agent sessions in-process: one Engine per conversation, its event stream folded
/// into the session's timeline, items mirrored to the ConversationLog. This app IS the harness — no
/// external process. Everything here runs on the UI thread; engines run on the thread pool and their
/// events are marshalled back.</summary>
public sealed partial class AgentHost : ObservableObject
{
    public AppConfig Config { get; }
    public ConversationLog Log { get; }
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<SessionVM> Sessions { get; } = [];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Selected))] private string? _selectedId;
    [ObservableProperty] private string? _banner;
    /// <summary>While the banner says an AWS sign-in ran out: the AWS CLI profile to sign in again.</summary>
    [ObservableProperty] private string? _awsSignInProfile;

    partial void OnBannerChanged(string? value) => AwsSignInProfile = null;
    /// <summary>Plugin manifests loaded for the current project, and any that failed.</summary>
    [ObservableProperty] private IReadOnlyList<PluginManifest> _plugins = [];
    [ObservableProperty] private IReadOnlyList<string> _pluginErrors = [];
    /// <summary>Instruction files and skills the active project contributes.</summary>
    [ObservableProperty] private ProjectContext? _projectContext;
    /// <summary>AI-generated, agent-proposed and imported skills waiting for approval.</summary>
    public ObservableCollection<SkillDraft> PendingDrafts { get; } = [];
    /// <summary>Bumped whenever skills change on disk through the app, so open views reload.</summary>
    [ObservableProperty] private int _skillsRevision;
    public SkillLocations SkillLocations { get; }

    private readonly Dictionary<string, Engine> _engines = new();
    /// <summary>What each session's engine was built with; a mismatch rebuilds it.</summary>
    private readonly Dictionary<string, EngineKey> _engineKeys = new();
    private readonly Dictionary<string, List<LlmMessage>> _transcripts = new();
    private readonly Dictionary<string, (TaskCompletionSource<bool> Tcs, string SessionId)> _gates = new();
    private readonly Dictionary<string, CancellationTokenSource> _runs = new();
    private readonly Dictionary<string, ComputerGrants> _grants = new();
    private readonly string _basePrompt;
    /// <summary>Route id → context window learned from a server probe.</summary>
    private readonly Dictionary<string, int> _probedContext = new();
    /// <summary>Route id → what the server said it serves, and when we last asked.</summary>
    private readonly Dictionary<string, RouteInfo> _routeInfo = new();
    private readonly HashSet<string> _probingContext = new();
    /// <summary>Session id → the fully-built system prompt, cached so the context gauge reads a
    /// string instead of re-loading project files on every keystroke.</summary>
    private readonly Dictionary<string, string> _systemPrompts = new();
    /// <summary>Routes whose server rejected an image (learned at runtime).</summary>
    private readonly HashSet<string> _noVisionRoutes = new();

    /// <summary>Called when tools touch files, so code mode's editor can reload.</summary>
    public Action<IReadOnlyList<FileChange>>? OnFilesChanged { get; set; }
    /// <summary>True while the model server is switching models (Spark swapper).</summary>
    public Func<bool>? IsServerSwitching { get; set; }
    /// <summary>Handles /swap [model].</summary>
    public Action<string?, SessionVM>? OnSwapCommand { get; set; }
    /// <summary>The context window or served model changed (a probe landed, the route changed).</summary>
    public event Action? ContextInfoChanged;

    private sealed record RouteInfo(string ServedModel, IReadOnlyList<string> ServedModels, int? Context, DateTimeOffset At);

    private sealed record EngineKey(string Profile, int Window, ThinkingLevel? Thinking, PermissionPreset Preset,
                                    int Skills, bool ComputerTools, bool Vision, string Shell, long Vault);

    /// <summary>Everything about skills one turn needs: what exists, what's on, and the prompt text.</summary>
    private sealed record SkillState(IReadOnlyList<Skill> All, IReadOnlyList<Skill> Active, SkillPromptResult Result, int Signature);

    /// <param name="queueFile">Where the task queue is saved (default %APPDATA%\DSH\task-queue.json).</param>
    /// <param name="skillLocations">Where skills live (tests point it at a temp folder).</param>
    /// <param name="vault">The credentials vault (default: DPAPI-encrypted, under the data folder).</param>
    public AgentHost(AppConfig config, ConversationLog log, Dispatcher dispatcher, string? systemPrompt = null,
                     string? queueFile = null, SkillLocations? skillLocations = null, CredentialVault? vault = null)
    {
        Config = config;
        Log = log;
        _dispatcher = dispatcher;
        _basePrompt = systemPrompt ?? DefaultSystemPrompt;
        SkillLocations = skillLocations ?? SkillLocations.Standard;
        Vault = vault ?? new CredentialVault();
        _queueFile = queueFile ?? TaskQueue.DefaultFilePath;
        Sessions.CollectionChanged += (_, e) =>
        {
            foreach (SessionVM vm in e.NewItems ?? Array.Empty<SessionVM>()) vm.PropertyChanged += OnSessionChanged;
            foreach (SessionVM vm in e.OldItems ?? Array.Empty<SessionVM>()) vm.PropertyChanged -= OnSessionChanged;
            OnPropertyChanged(nameof(AnyRunning));
            OnPropertyChanged(nameof(RunningCount));
        };
        Reload();
        // Skills that ship with the app (godot-debugging) are kept current on every launch.
        try
        {
            BuiltinSkills.Install(SkillLocations.BuiltinSkills);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Not fatal: the app works without them.
        }
        RefreshDrafts();
        InitQueue();
        Vault.Changed += (_, _) => _dispatcher.BeginInvoke(() => VaultRevision = Vault.Revision);
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SessionVM.Running))
        {
            OnPropertyChanged(nameof(AnyRunning));
            OnPropertyChanged(nameof(RunningCount));
        }
    }

    public SessionVM? Selected => SelectedId is null ? null : Sessions.FirstOrDefault(s => s.Id == SelectedId);
    public SessionVM? Session(string id) => Sessions.FirstOrDefault(s => s.Id == id);
    public IReadOnlyList<SessionVM> RunningSessions => Sessions.Where(s => s.Running).ToList();
    public bool AnyRunning => Sessions.Any(s => s.Running);
    public int RunningCount => Sessions.Count(s => s.Running);
    /// <summary>Anything working — a turn, a queue task, or a background agent.</summary>
    public bool AnythingRunning => AnyRunning || Sessions.Any(s => s.RunningBackgroundJobs.Count > 0);

    public const string DefaultSystemPrompt =
        "You are a capable coding agent running inside a native Windows app, working in the user's project folder.\n\n" +
        "Use the tools to read, write, and search files and to run shell commands. Prefer small, verifiable steps:\n" +
        "read before you edit, and check your work after you change something. When a task needs more than a\n" +
        "couple of steps, track it with `todo_write` so the user can see the plan.\n\n" +
        "Parallel and background work: for a well-scoped side task, launch a subagent with `agent` — with\n" +
        "run_in_background: true it works while you continue (several can run at once); you're told when each\n" +
        "finishes, and agent_status / agent_stop manage them. Long-running programs (dev servers, game engines,\n" +
        "REPLs) go in `process_start`. Follow-up work that can happen later, unattended, goes on the task queue\n" +
        "with `queue_task`.\n\n" +
        "Rules that matter:\n" +
        "- Never claim a command succeeded unless you ran it and saw the output.\n" +
        "- Prefer `edit` over `write_file` for changes to an existing file; rewriting a whole file loses work.\n" +
        "- Paths are resolved against the project folder. Stay inside it unless the user asks otherwise.\n" +
        "- This is Windows: `run_shell_command` runs in the shell named in the Environment section. Write commands\n" +
        "  for that shell (PowerShell unless it says otherwise), not for bash, and use Windows paths.\n" +
        "- When you finish, summarize what changed in a few lines. Reference files as `path:line`.";

    /// <summary>Post a notice into the chat the user is looking at.</summary>
    public void Broadcast(string text, bool error = false)
    {
        if (Selected is not { } vm)
        {
            Banner = text;
            return;
        }
        vm.Note(text, error ? MessageRole.Error : MessageRole.Notice);
        Log.RecordItem(vm.Id, error ? "error" : "notice", text, isError: error);
    }

    // MARK: - Project

    /// <summary>Adopt a project folder: reload its plugins, instructions, and skills.</summary>
    public void AdoptProject(string? path)
    {
        if (path is null)
        {
            ProjectContext = null;
            Plugins = [];
            PluginErrors = [];
            return;
        }
        ProjectContext = Dsh.Core.ProjectContext.Load(path, SkillLocations, Config.SkillSources);
        var loaded = PluginLoader.Load(path);
        Plugins = loaded.Plugins;
        PluginErrors = loaded.Errors;
        // Sessions pick the new context up on their next turn.
        _engines.Clear();
        _systemPrompts.Clear();
    }

    /// <summary>Re-read instructions, skills, and plugins from disk.</summary>
    public void RefreshProjectContext()
    {
        if (ProjectContext?.Root is { } root) AdoptProject(root);
        else
        {
            var loaded = PluginLoader.Load(null);
            Plugins = loaded.Plugins;
            PluginErrors = loaded.Errors;
            _engines.Clear();
        }
    }

    // MARK: - Sessions

    public SessionVM NewSession(string? cwd, PermissionPreset? preset = null, bool select = true)
    {
        var id = Guid.NewGuid().ToString();
        var resolved = preset ?? Config.AsPreset;
        var vm = new SessionVM(id, "New chat", cwd, resolved);
        Sessions.Insert(0, vm);
        Log.Upsert(id, cwd, "New chat", resolved.RawValue());
        if (select) SelectedId = id;
        return vm;
    }

    public void DeleteSession(string id)
    {
        ResolveAllGates(id, false);
        if (_runs.Remove(id, out var cts)) cts.Cancel();
        _engines.Remove(id);
        _engineKeys.Remove(id);
        _transcripts.Remove(id);
        _systemPrompts.Remove(id);
        _grants.Remove(id);
        _vaultGrants.Remove(id);
        _autoContinuations.Remove(id);
        _agentQueuedCount.Remove(id);
        if (_backgroundPools.Remove(id, out var pool)) pool.StopAll();
        Queue.DetachSession(id);
        Config.SetSkillsFor(id, null);
        Log.Delete(id);
        if (Session(id) is { } vm) Sessions.Remove(vm);
        if (SelectedId == id) SelectedId = Sessions.FirstOrDefault()?.Id;
    }

    public void RenameSession(string id, string title)
    {
        if (Session(id) is not { } vm) return;
        vm.Title = title;
        Log.Touch(id, title);
    }

    public void Reload()
    {
        foreach (var row in Log.List())
        {
            if (Sessions.Any(s => s.Id == row.Id)) continue;
            var cwd = string.IsNullOrEmpty(row.Cwd) ? null : row.Cwd;
            var vm = new SessionVM(row.Id, row.Title, cwd, PermissionPresets.FromRaw(row.Preset) ?? PermissionPreset.WorkspaceWrite)
            {
                UpdatedAt = row.UpdatedAt,
            };
            Sessions.Add(vm);
        }
        SortSessions();
        SelectedId ??= Sessions.FirstOrDefault()?.Id;
        if (Selected is { } selected) Hydrate(selected);
    }

    /// <summary>Newest first (the sidebar additionally floats running chats to the top).</summary>
    private void SortSessions()
    {
        var ordered = Sessions.OrderByDescending(s => s.UpdatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = Sessions.IndexOf(ordered[i]);
            if (current != i) Sessions.Move(current, i);
        }
    }

    /// <summary>Replay a stored transcript into a session's timeline. Called lazily, so a long
    /// session list stays cheap to open.</summary>
    public void Hydrate(SessionVM vm)
    {
        if (vm.Entries.Count > 0) return;
        foreach (var row in Log.LoadItems(vm.Id))
        {
            switch (row.Kind)
            {
                case "user": vm.AppendMessage(MessageRole.User, row.Text ?? "", row.At); break;
                case "assistant": vm.AppendMessage(MessageRole.Assistant, row.Text ?? "", row.At); break;
                case "notice": vm.AppendMessage(MessageRole.Notice, row.Text ?? "", row.At); break;
                case "error": vm.AppendMessage(MessageRole.Error, row.Text ?? "", row.At); break;
                case "tool":
                    vm.AddFinishedTool($"log-{row.Seq}", row.ToolName ?? "tool", row.ArgSummary ?? "", row.Text, row.Output, !row.IsError, row.At);
                    break;
                case "compaction":
                    vm.Entries.Add(new CompactionEntryVM(int.TryParse(row.ArgSummary, out var removed) ? removed : 0, row.Text ?? "", row.At));
                    break;
            }
        }
        // A hydrated session has no live engine; rebuild the model transcript so a follow-up turn
        // keeps the conversation rather than starting over.
        if (!_transcripts.ContainsKey(vm.Id)) _transcripts[vm.Id] = ReplayMessages(vm.Entries);
    }

    /// <summary>Reconstruct a model-facing transcript from a rendered one. Tool calls are folded into
    /// the assistant text: replaying their exact call ids is not worth persisting, and the model only
    /// needs to know what happened.</summary>
    public static List<LlmMessage> ReplayMessages(IEnumerable<ChatEntryVM> entries)
    {
        var output = new List<LlmMessage>();
        var pendingTools = new List<string>();

        void FlushTools()
        {
            if (pendingTools.Count == 0) return;
            output.Add(LlmMessage.Assistant("[earlier tool activity]\n" + string.Join("\n", pendingTools)));
            pendingTools.Clear();
        }

        foreach (var entry in entries)
        {
            switch (entry)
            {
                case MessageEntryVM { Role: MessageRole.User } m:
                    FlushTools();
                    output.Add(LlmMessage.User(m.Text));
                    break;
                case MessageEntryVM { Role: MessageRole.Assistant } m:
                    FlushTools();
                    output.Add(LlmMessage.Assistant(m.Text));
                    break;
                case ToolEntryVM tool:
                    pendingTools.Add($"- {tool.Name}({tool.Preview}) → {tool.Summary ?? (tool.IsOk == false ? "failed" : "ok")}");
                    break;
                case CompactionEntryVM note:
                    // A compacted transcript starts from its summary: everything above the divider
                    // is already folded into it.
                    pendingTools.Clear();
                    output.Clear();
                    output.Add(LlmMessage.SystemText(Compaction.SummaryHeader + note.Summary));
                    break;
            }
        }
        FlushTools();
        return output;
    }

    // MARK: - Engine

    private Engine BuildEngine(SessionVM vm, IProviderClient client, int window, ThinkingLevel? thinking, SkillState skillState)
    {
        var sessionId = vm.Id;
        var workspace = vm.WorkspacePath ?? PermissionPolicy.HomeDirectory;
        var policy = new PermissionPolicy(vm.Preset, workspace);
        var profile = client.Profile;
        var shell = Config.ResolvedAgentShell;

        // Project instructions + skills + plugin tools are what make this a harness rather than a
        // chat window.
        var context = ProjectContext is { } pc && SamePath(pc.Root, workspace)
            ? pc
            : (vm.WorkspacePath is null ? null : Dsh.Core.ProjectContext.Load(workspace, SkillLocations, Config.SkillSources));
        var environment = Dsh.Core.ProjectContext.EnvironmentBlock(workspace, profile.Model, vm.Preset, shell);
        var prompt = _basePrompt + "\n\n" + (context?.PromptSupplement(environment, includeSkills: false) ?? environment);
        // Skills: always-on rules, the ones the user selected for this chat, and a catalog the
        // model can load from with use_skill.
        if (skillState.Result.Text.Length > 0) prompt += "\n\n" + skillState.Result.Text;
        // The credentials the agent may use, by name — never a value.
        if (VaultPrompt.Section(Vault.All, shell.Kind) is { Length: > 0 } vaultSection) prompt += "\n\n" + vaultSection;
        if (vm.Preset == PermissionPreset.Plan)
            prompt += "\n\n--- Plan mode ---\nDo not modify anything. Research and produce a plan, then call `exit_plan_mode` with it and stop.";
        _systemPrompts[sessionId] = prompt;

        // Background processes: the model's own long-running programs (game engines, dev servers,
        // REPLs) that keep running between tool calls. Seeing and steering the machine — screenshots,
        // windows, UI trees, clicks, keystrokes — is off with the computer-tools switch; each first use
        // in a chat still asks (the engine's gate). Both count as built-ins, so a plugin can't take
        // over their names.
        var builtins = ToolRegistry.Standard(0, shell).Adding(ExtraTools.Processes());
        if (Config.ComputerToolsEnabled) builtins = builtins.Adding(ExtraTools.Machine());
        var extra = new List<IToolExecutor>(PluginLoader.Tools(Plugins, builtins.Names));
        if (skillState.Active.Count > 0) extra.Add(new UseSkillTool(skillState.Active));
        extra.Add(new VaultSearchTool(Vault));
        extra.AddRange(ToolRegistry.BackgroundAgentTools());
        if (vm.Preset != PermissionPreset.Plan)
        {
            extra.Add(new QueueAddTool((title, details, front, start) =>
                _dispatcher.InvokeAsync(() => AgentQueueTask(title, details, front, start, sessionId)).Task));
            extra.Add(new ProposeSkillTool(vm.WorkspacePath, SkillLocations));
        }
        var registry = builtins.Adding(extra);

        if (!_grants.TryGetValue(sessionId, out var grants)) _grants[sessionId] = grants = new ComputerGrants();
        var engine = new Engine(client, registry, prompt,
            new EngineConfig(profile.Model)
            {
                Temperature = profile.Temperature,
                MaxOutputTokens = profile.MaxOutputTokens,
                ContextWindow = window,
                Thinking = thinking,
                VisionEnabled = VisionOn(profile),
                Shell = shell,
                Retry = RetryPolicy,
            },
            workspace, policy,
            (id, name, detail) => _dispatcher.InvokeAsync(() => AskGateAsync(sessionId, id, name, detail)).Task.Unwrap())
        {
            OnTodos = todos => _dispatcher.BeginInvoke(() => Session(sessionId)?.SetTodos(todos)),
            Compactor = (used, messages, ct) =>
                _dispatcher.InvokeAsync(() => CompactTranscriptAsync(sessionId, used, messages, ct)).Task.Unwrap(),
            ComputerGrants = grants,
            Reroute = ct => _dispatcher.InvokeAsync(() => RerouteForRetryAsync(sessionId)).Task.Unwrap(),
            Vault = Vault,
            VaultGrants = VaultGrantsFor(sessionId),
            BackgroundAgents = BackgroundPool(sessionId),
        };
        _engines[sessionId] = engine;
        return engine;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Bridge the engine's permission gate to the UI: publish the gate on the session and
    /// await the user's answer.
    ///
    /// Queue tasks run unattended — a gate nobody answers would stall the whole queue. So for queue
    /// sessions the decision times out (auto-deny) after a few minutes, and the stall is surfaced in
    /// the chat.</summary>
    private async Task<bool> AskGateAsync(string sessionId, string callId, string name, string detail)
    {
        if (Session(sessionId) is not { } vm) return false;
        // Tool-call ids repeat ("call-0" every turn, in every chat): key the question by a token of its
        // own, so two chats can't overwrite each other's answer and an old timer can't answer a newer
        // question.
        var gateId = $"{callId}#{Guid.NewGuid():N}";
        vm.PendingGates.Add(new GateVM(gateId, name, detail));
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _gates[gateId] = (tcs, sessionId);
        vm.NotifyContentChanged();
        if (_queueSessions.ContainsKey(sessionId)) _ = AutoDenyAsync();
        var decision = await tcs.Task;
        _gates.Remove(gateId);
        foreach (var answered in vm.PendingGates.Where(g => g.Id == gateId).ToList()) vm.PendingGates.Remove(answered);
        return decision;

        async Task AutoDenyAsync()
        {
            await Task.Delay(QueueGateTimeout);
            if (!_gates.Remove(gateId, out var entry)) return; // answered or cancelled
            foreach (var stale in vm.PendingGates.Where(g => g.Id == gateId).ToList()) vm.PendingGates.Remove(stale);
            var text = $"Permission for {Icons.ToolLabel(name)} auto-denied after {(int)QueueGateTimeout.TotalMinutes} minutes — queue tasks run unattended. " +
                       "To allow it, change the permission preset in Settings, then Resume the task.";
            vm.Note(text, MessageRole.Error);
            Log.RecordItem(sessionId, "error", text, isError: true);
            entry.Tcs.TrySetResult(false);
        }
    }

    /// <summary>How long a queue task's permission question waits before it is answered "no".</summary>
    public static TimeSpan QueueGateTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Answer a pending gate from the UI.</summary>
    public void AnswerGate(string sessionId, string gateId, bool allow)
    {
        if (!_gates.Remove(gateId, out var entry)) return;
        entry.Tcs.TrySetResult(allow);
        if (Session(sessionId) is { } vm)
            foreach (var answered in vm.PendingGates.Where(g => g.Id == gateId).ToList()) vm.PendingGates.Remove(answered);
    }

    private void ResolveAllGates(string sessionId, bool allow)
    {
        foreach (var (gateId, entry) in _gates.Where(g => g.Value.SessionId == sessionId).ToList())
        {
            _gates.Remove(gateId);
            entry.Tcs.TrySetResult(allow);
        }
    }

    // MARK: - Sending

    public void Send(string text, string sessionId, IReadOnlyList<MessageAttachment>? attachments = null)
    {
        attachments ??= [];
        if (Session(sessionId) is not { } vm) return;
        if (vm.Running || _runs.ContainsKey(sessionId))
        {
            vm.Note("The agent is still working; send again when it is done.");
            return;
        }
        _autoContinuations[sessionId] = 0;
        if (attachments.Count == 0 && SlashCommand.Parse(text) is { } command)
        {
            RunCommand(command, vm);
            return;
        }
        // "/deploy staging": a skill or command invoked by name.
        if (attachments.Count == 0 && MatchSkillCommand(text, vm) is var (skill, args) && InvocationText(skill, args) is { } expanded)
        {
            if (IsServerSwitching?.Invoke() == true)
            {
                vm.Note("The Spark is switching models right now — send again once it says it's ready.");
                return;
            }
            var display = "/" + skill.Slug + (args.Length == 0 ? "" : " " + args);
            StartRun(vm, ct => RunTurnAsync(vm, display, [], goal: null, modelText: expanded, ct));
            return;
        }
        if (IsServerSwitching?.Invoke() == true)
        {
            vm.Note("The Spark is switching models right now — send again once it says it's ready (usually a few minutes).");
            return;
        }
        StartRun(vm, ct => RunTurnAsync(vm, text, attachments, goal: null, modelText: null, ct));
    }

    /// <summary>Start background work for a session, cancellable with Stop.</summary>
    private void StartRun(SessionVM vm, Func<CancellationToken, Task> work)
    {
        if (_runs.Remove(vm.Id, out var previous)) previous.Cancel();
        var cts = new CancellationTokenSource();
        _runs[vm.Id] = cts;
        _ = RunGuardedAsync(vm.Id, cts, work);
    }

    private async Task RunGuardedAsync(string sessionId, CancellationTokenSource cts, Func<CancellationToken, Task> work)
    {
        try
        {
            await work(cts.Token);
        }
        finally
        {
            if (_runs.TryGetValue(sessionId, out var current) && ReferenceEquals(current, cts)) _runs.Remove(sessionId);
            cts.Dispose();
        }
    }

    // MARK: - Slash commands

    private void RunCommand(SlashCommand command, SessionVM vm)
    {
        switch (command)
        {
            case SlashCommand.Help:
                vm.Note("Commands:\n" + string.Join("\n", SlashCommand.Catalog.Select(c => $"`{c.Usage}` — {c.Summary}")));
                break;

            case SlashCommand.Think think:
            {
                var fallback = Config.ActiveProvider?.Thinking;
                if (think.Argument is null)
                {
                    var current = vm.Thinking ?? fallback;
                    vm.Note($"Thinking: **{current?.Label() ?? "server default"}**"
                            + (vm.Thinking is null ? " (from the provider setting)" : " (this chat)")
                            + ". Change it with `/think off|low|medium|high|max|default`.");
                    return;
                }
                var arg = think.Argument.ToLowerInvariant();
                if (arg is "default" or "reset" or "auto")
                {
                    vm.Thinking = null;
                    vm.Note($"Thinking back to the provider default ({fallback?.Label() ?? "server default"}).");
                }
                else if (ThinkingLevels.ParseUserInput(think.Argument) is { } level)
                {
                    vm.Thinking = level;
                    vm.Note($"Thinking set to **{level.Label()}** for this chat — {level.Blurb().ToLowerInvariant()}.");
                }
                else
                {
                    vm.Note($"Unknown level “{think.Argument}”. Use off, low, medium, high, max, or default.", MessageRole.Error);
                }
                break;
            }

            case SlashCommand.Context:
                StartRun(vm, async _ =>
                {
                    await ResolveRouteAsync(force: true);
                    var limit = ContextLimit();
                    var used = ContextUsed(vm.Id, "");
                    var model = Config.ActiveProvider is { } p ? Effective(p).Model : "?";
                    vm.Note($"Context: {Fmt.N(used)} of {Fmt.N(limit)} tokens used ({(int)(used / (double)Math.Max(limit, 1) * 100)}%). " +
                            $"Model `{model}`; window {ContextSource()}. Auto-compaction starts at {(int)(Compaction.TriggerFraction * 100)}%.");
                });
                break;

            case SlashCommand.Swap swap:
                if (OnSwapCommand is not null) OnSwapCommand(swap.Argument, vm);
                else vm.Note("Model switching isn't available.", MessageRole.Error);
                break;

            case SlashCommand.Skills:
                vm.Note(SkillsSummary(vm));
                break;

            case SlashCommand.Skill skill:
                HandleSkillCommand(skill.Argument, vm);
                break;

            case SlashCommand.Compact compact:
                StartRun(vm, ct => CompactNowAsync(vm, compact.Focus, ct));
                break;

            case SlashCommand.Goal goal:
                if (IsServerSwitching?.Invoke() == true)
                {
                    vm.Note("The Spark is switching models right now — send again once it says it's ready.");
                    return;
                }
                if (goal.Text.Length == 0)
                {
                    // A bare /goal picks the chat's unfinished goal back up (after a Stop, a block the
                    // user has since answered, or a failure).
                    if ((vm.LastGoal ?? LastGoalIn(vm.Entries)) is not { } previous)
                    {
                        vm.Note("Usage: `/goal <what you want done>` — the agent keeps working, round after round, until it " +
                                "declares the goal complete (or needs you). Stop it any time with Ctrl+.");
                        return;
                    }
                    StartRun(vm, ct => RunTurnAsync(vm, previous, [], goal: previous, modelText: null, ct, resumingGoal: true));
                    return;
                }
                StartRun(vm, ct => RunTurnAsync(vm, goal.Text, [], goal: goal.Text, modelText: null, ct));
                break;

            case SlashCommand.Queue:
            {
                var stats = Queue.Stats();
                if (QueueRunning)
                    vm.Note($"Queue is running — {stats.Queued} waiting, {stats.Completed} done so far. Ctrl+Shift+Q shows the panel; Stop there (or Ctrl+.) halts it.");
                else if (stats.Queued > 0)
                {
                    vm.Note($"Starting the queue: {stats.Queued} task{(stats.Queued == 1 ? "" : "s")} to go, one at a time, unattended.");
                    StartQueue();
                }
                else
                    vm.Note("The queue is empty. Add tasks from the Task Queue panel (Ctrl+Shift+Q), then press Start — or /queue again.");
                break;
            }
        }
    }

    /// <summary>/compact: fold the conversation into a summary now, whatever its size.</summary>
    private async Task CompactNowAsync(SessionVM vm, string? focus, CancellationToken ct)
    {
        vm.Running = true;
        try
        {
            await CompactSessionAsync(vm, focus, ct);
        }
        catch (OperationCanceledException)
        {
            vm.Note("Stopped.");
        }
        finally
        {
            vm.Running = false;
            vm.Stopping = false;
            vm.Activity = null;
        }
    }

    /// <summary>Fold the conversation into a summary now (the /compact work, and a goal round that
    /// overflowed). Leaves the session's running state alone.</summary>
    private async Task CompactSessionAsync(SessionVM vm, string? focus, CancellationToken ct)
    {
        var sessionId = vm.Id;
        vm.Activity = "Compacting conversation…";
        try
        {
            if (!_transcripts.ContainsKey(sessionId)) Hydrate(vm);
            var messages = _transcripts.GetValueOrDefault(sessionId) ?? [];
            if (!messages.Any(m => m.Role == Dsh.Core.MessageRole.User))
            {
                vm.Note("Nothing to compact yet.");
                return;
            }
            if (await ResolveRouteAsync() is not { } profile)
            {
                vm.Note(LlmException.NoModel().Message, MessageRole.Error);
                return;
            }
            var prompt = _systemPrompts.GetValueOrDefault(sessionId) ?? _basePrompt;
            var before = TokenEstimate.Request(prompt, messages);
            if (Compaction.MakePlan(before, ContextLimit(), messages, force: true) is not { } plan)
            {
                vm.Note($"The conversation is already as compact as it gets (~{Fmt.N(before)} tokens).");
                return;
            }
            var summary = await Compaction.SummarizeAsync(MakeClient(profile), plan, model: profile.Model, focus: focus,
                cancellationToken: ct);
            if (summary is null)
            {
                vm.Note("Compaction failed: the model did not return a summary. Nothing was changed.", MessageRole.Error);
                return;
            }
            if (ct.IsCancellationRequested) return;
            var newMessages = new List<LlmMessage> { LlmMessage.SystemText(Compaction.SummaryHeader + summary) };
            newMessages.AddRange(plan.ToKeep);
            ApplyCompaction(sessionId, plan, summary, newMessages);
            var after = TokenEstimate.Request(prompt, newMessages);
            var text = $"Compacted {plan.ToSummarize.Count} messages: ~{Fmt.N(before)} → ~{Fmt.N(after)} tokens.";
            vm.Note(text);
            Log.RecordItem(sessionId, "notice", text);
        }
        finally
        {
            vm.Activity = null;
        }
    }

    // MARK: - Turns

    private async Task RunTurnAsync(SessionVM vm, string text, IReadOnlyList<MessageAttachment> attachments,
                                    string? goal, string? modelText, CancellationToken ct, bool resumingGoal = false)
    {
        var sessionId = vm.Id;
        vm.Running = true;
        vm.Stopping = false;
        try
        {
            if (goal is not null) await RunGoalAsync(vm, goal, resumingGoal, ct);
            else await TurnAsync(vm, modelText ?? text, text, attachments, ct);
        }
        catch (OperationCanceledException)
        {
            vm.EndStreaming();
            var note = goal is not null ? "Stopped. The goal was not finished — send `/goal` to pick it back up." : "Stopped.";
            vm.Note(note);
            Log.RecordItem(sessionId, "notice", note);
        }
        catch (LlmException error)
        {
            // A server overflow that still surfaced tells us the real window is smaller than we
            // budgeted — learn it so the gauge and future compaction use the true number.
            if (error.Kind == LlmErrorKind.Overflow && error.Limit > 0 && Config.ActiveProvider is { } provider)
            {
                _probedContext[provider.RouteId] = Math.Min(error.Limit, _probedContext.GetValueOrDefault(provider.RouteId, error.Limit));
                ContextInfoChanged?.Invoke();
            }
            ReportFailure(vm, Describe(error));
            if (error.InnerException is AwsSignInRequiredException expired) AwsSignInProfile = expired.Profile;
        }
        catch (Exception error)
        {
            ReportFailure(vm, Describe(error));
        }
        finally
        {
            vm.Running = false;
            vm.Stopping = false;
            vm.Goal = null;
            vm.Activity = null;
            vm.Retry = null;
            vm.RunningTool = null;
            vm.ClearReasoning();
            vm.EndStreaming();
            Log.Touch(sessionId);
            vm.UpdatedAt = DateTimeOffset.Now;
            SortSessions();
        }
    }

    private void ReportFailure(SessionVM vm, string message)
    {
        vm.EndStreaming();
        vm.Note(message, MessageRole.Error);
        Log.RecordItem(vm.Id, "error", message, isError: true);
        Banner = message;
    }

    /// <summary>One user message → one engine run (which may take many tool steps).</summary>
    private async Task<RunResult> TurnAsync(SessionVM vm, string modelText, string displayText,
                                            IReadOnlyList<MessageAttachment> attachments, CancellationToken ct)
    {
        var sessionId = vm.Id;
        // A chat whose timeline was released (an archived queue chat) or never loaded: bring it back
        // first, so the model keeps its history.
        Hydrate(vm);
        // Prime the model transcript from the timeline when it's missing (an archived queue chat whose
        // transcript was evicted), before this turn's entry is added so its text isn't sent twice.
        if (!_transcripts.ContainsKey(sessionId)) _transcripts[sessionId] = ReplayMessages(vm.Entries);
        var userEntryId = vm.AppendMessage(MessageRole.User, displayText);
        Log.RecordItem(sessionId, "user", displayText);
        if (vm.Title == "New chat")
        {
            var first = displayText.Split('\n').FirstOrDefault() ?? displayText;
            var title = TextUtil.Prefix(first.Trim(), 48);
            if (title.Length > 0) RenameSession(sessionId, title);
        }

        // Re-read what the server serves right now: the Spark can swap models between turns, and the
        // window/model id must follow.
        var profile = await ResolveRouteAsync() ?? throw LlmException.NoModel();
        ct.ThrowIfCancellationRequested();
        var window = ContextLimit();
        var thinking = vm.Thinking;
        var skillState = BuildSkillState(vm);
        var shell = Config.ResolvedAgentShell;
        var key = new EngineKey(ProfileKey(profile), window, thinking, vm.Preset, skillState.Signature,
                                Config.ComputerToolsEnabled, VisionOn(profile), shell.Executable, Vault.Revision);
        if (_engineKeys.TryGetValue(sessionId, out var previous) && !string.Equals(ModelOf(previous.Profile), profile.Model, StringComparison.Ordinal))
            vm.Note($"The server is now serving `{profile.Model}` (was `{ModelOf(previous.Profile)}`) — switched to it, {Fmt.N(window)}-token window.");
        if (!_engines.TryGetValue(sessionId, out var engine) || _engineKeys.GetValueOrDefault(sessionId) != key)
        {
            engine = BuildEngine(vm, MakeClient(profile), window, thinking, skillState);
            _engineKeys[sessionId] = key;
        }
        _retryRoute.Remove(sessionId);
        var input = _transcripts[sessionId].ToList();

        // Engine events arrive on a pool thread; hop to the UI thread so the timeline is only ever
        // mutated from one place.
        void Sink(EngineEvent evt) => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => Apply(evt, sessionId));
        var progress = new RunProgress();
        RunResult result;
        try
        {
            result = await Task.Run(() => engine.RunAsync(input, modelText, attachments, Sink, ct, progress), CancellationToken.None);
        }
        catch (Exception)
        {
            // Keep what the run already did — tool calls that ran and their results — so the model's
            // memory matches the files on disk and the timeline, instead of rolling back to before
            // the run.
            if (progress.Salvaged is { } salvaged) _transcripts[sessionId] = salvaged.ToList();
            else MarkUndelivered(vm, userEntryId); // the model never saw this message
            vm.Retry = null;
            throw;
        }
        vm.Retry = null;
        _transcripts[sessionId] = result.Messages.ToList();
        vm.LastUsage = result.Usage;
        // How full the context is now: server-reported prompt tokens when available, otherwise a
        // character-based estimate of the whole request.
        vm.ContextUsed = result.LastPromptTokens ?? TokenEstimate.Request(engine.SystemPrompt, result.Messages);
        if (result.DeniedCount > 0) vm.Note($"{result.DeniedCount} tool call(s) were denied.");
        if (result.HitIterationLimit && vm.Goal is null)
            vm.Note($"Paused after {engine.Config.MaxIterations} steps. Say “continue” to keep going, or use `/goal` for long tasks.");
        return result;
    }

    /// <summary>Every model client the host makes goes through here, so tests can swap the network.</summary>
    private IProviderClient MakeClient(ProviderProfile profile) => ProviderClients.Create(profile, HttpHandlerForTesting);

    /// <summary>Tests: answer model requests from this handler instead of the network.</summary>
    public HttpMessageHandler? HttpHandlerForTesting { get; set; }

    /// <summary>How engine runs retry a model that doesn't answer (tests shorten the waits).</summary>
    public RetryPolicy RetryPolicy { get; set; } = RetryPolicy.Standard;

    private static string ProfileKey(ProviderProfile p) =>
        string.Join("|", p.Kind, p.Name, p.BaseUrl, p.Model, p.Temperature, p.MaxOutputTokens, p.ContextWindow,
            p.ReasoningEffort, p.Vision, p.ApiKey?.GetHashCode(),
            p.CustomHeaders is null ? "" : string.Join(",", p.CustomHeaders.OrderBy(h => h.Key).Select(h => $"{h.Key}={h.Value}")));

    private static string ModelOf(string profileKey) => profileKey.Split('|') is { Length: > 3 } parts ? parts[3] : "";

    private void Apply(EngineEvent evt, string sessionId)
    {
        if (Session(sessionId) is not { } vm) return;
        switch (evt)
        {
            case EngineEvent.TextDelta d:
                vm.Retry = null;
                vm.ClearReasoning();
                vm.AppendDelta(d.Text);
                break;

            case EngineEvent.ReasoningDelta r:
                vm.Retry = null;
                vm.AppendReasoning(r.Text);
                break;

            case EngineEvent.Retrying retrying:
            {
                // The failed attempt's partial reply is void — the retry streams the whole reply
                // again — so drop its bubble.
                vm.DropStreaming();
                vm.ClearReasoning();
                vm.Retry = new RetryState(retrying.Attempt, retrying.Reason, DateTimeOffset.Now + retrying.Delay);
                if (retrying.Attempt == 1)
                {
                    var text = $"⚠️ The model didn't answer ({retrying.Reason}). Retrying automatically until it's available — press Stop to give up.";
                    vm.Note(text);
                    Log.RecordItem(sessionId, "notice", text);
                    if (_queueSessions.TryGetValue(sessionId, out var taskId))
                        Queue.Note(taskId, $"Model unavailable ({retrying.Reason}) — retrying until it answers.");
                }
                break;
            }

            case EngineEvent.Recovered recovered:
            {
                vm.Retry = null;
                var text = $"✓ The model is answering again (after {recovered.Attempts} retr{(recovered.Attempts == 1 ? "y" : "ies")}).";
                vm.Note(text);
                Log.RecordItem(sessionId, "notice", text);
                if (_queueSessions.TryGetValue(sessionId, out var taskId)) Queue.Note(taskId, text);
                break;
            }

            case EngineEvent.AssistantMessage m:
                // Fold a turn's complete text if deltas never arrived, then close the bubble so any
                // tool calls render after it.
                if (vm.StreamingId is null && m.Text.Length > 0) vm.AppendMessage(MessageRole.Assistant, m.Text);
                if (m.Text.Length > 0) Log.RecordItem(sessionId, "assistant", m.Text);
                vm.EndStreaming();
                break;

            case EngineEvent.ToolStarted t:
                vm.ClearReasoning();
                vm.StartTool(t.Id, t.Name, t.Preview);
                break;

            case EngineEvent.ToolFinished f:
                vm.FinishTool(f.Id, f.Ok, f.Summary, f.Output);
                if (f.Name == "propose_skill" && f.Ok && f.Output.Contains("Saved draft", StringComparison.Ordinal))
                {
                    RefreshDrafts();
                    vm.Note("📝 The agent drafted a skill. It is not active until you approve it — open **Skills** under the composer (or Settings › Skills) to review it.");
                }
                var preview = vm.Entries.LastOrDefault(e => e.Id == f.Id) is ToolEntryVM card ? card.Preview : null;
                Log.RecordItem(sessionId, "tool", f.Summary, f.Name, preview, f.Output, !f.Ok);
                break;

            case EngineEvent.ToolImages images:
                vm.AttachImages(images.Id, images.Images.Select(i => ImageTools.Thumbnail(i.Data)).OfType<System.Windows.Media.Imaging.BitmapSource>());
                break;

            case EngineEvent.FilesChanged changes:
                vm.RecordFileChanges(changes.Changes);
                OnFilesChanged?.Invoke(changes.Changes);
                break;

            case EngineEvent.Finished finished:
                vm.LastUsage = finished.Usage;
                vm.EndStreaming();
                break;

            case EngineEvent.PermissionQuestion q:
                if (!vm.PendingGates.Any(g => g.Id == q.Id)) vm.PendingGates.Add(new GateVM(q.Id, q.Name, q.Detail));
                break;

            case EngineEvent.Todos todos:
                vm.SetTodos(todos.Items);
                break;

            case EngineEvent.Failed failed:
                vm.Note(failed.Message, MessageRole.Error);
                Banner = failed.Message;
                break;
        }
    }

    private bool VisionOn(ProviderProfile? profile)
    {
        if (profile is null) return true;
        if (_noVisionRoutes.Contains(profile.RouteId)) return false;
        return profile.Vision ?? true;
    }

    // MARK: - Skills

    /// <summary>Reload the approval queue (and tell open views to reload).</summary>
    public void RefreshDrafts()
    {
        PendingDrafts.Clear();
        foreach (var draft in SkillDrafts.List(SkillLocations)) PendingDrafts.Add(draft);
        SkillsRevision++;
    }

    /// <summary>Every skill visible from a chat's project (shadowed ones included).</summary>
    public IReadOnlyList<Skill> Skills(SessionVM? vm) =>
        SkillCatalog.LoadAll(vm?.WorkspacePath ?? ProjectContext?.Root, SkillLocations, Config.SkillSources);

    public SkillSelection Selection(string sessionId)
    {
        var chosen = Config.SkillsFor(sessionId);
        return new SkillSelection
        {
            Pinned = new HashSet<string>(chosen.Pinned, StringComparer.OrdinalIgnoreCase),
            Auto = chosen.Auto,
            Disabled = new HashSet<string>(Config.DisabledSkills, StringComparer.OrdinalIgnoreCase),
        };
    }

    private SkillState BuildSkillState(SessionVM vm)
    {
        var all = Skills(vm);
        var selection = Selection(vm.Id);
        var result = SkillPrompt.Build(all, selection);
        var active = SkillPrompt.Active(all, selection);
        var hash = new HashCode();
        hash.Add(result.Text);
        foreach (var s in active) hash.Add(s.Id);
        return new SkillState(all, active, result, hash.ToHashCode());
    }

    public bool IsPinned(Skill skill, SessionVM vm) =>
        Config.SkillsFor(vm.Id).Pinned.Contains(skill.Id, StringComparer.OrdinalIgnoreCase);

    public void SetPinned(Skill skill, bool on, SessionVM vm)
    {
        var chosen = Config.SkillsFor(vm.Id);
        var pinned = chosen.Pinned.Where(p => !string.Equals(p, skill.Id, StringComparison.OrdinalIgnoreCase)).ToList();
        if (on) pinned.Add(skill.Id);
        Config.SetSkillsFor(vm.Id, chosen with { Pinned = pinned });
        SkillsRevision++;
    }

    public void SetAutoSkills(bool on, SessionVM vm)
    {
        Config.SetSkillsFor(vm.Id, Config.SkillsFor(vm.Id) with { Auto = on });
        SkillsRevision++;
    }

    public void ClearPinnedSkills(SessionVM vm)
    {
        Config.SetSkillsFor(vm.Id, Config.SkillsFor(vm.Id) with { Pinned = [] });
        SkillsRevision++;
    }

    /// <summary>/skills: what exists and what this chat uses.</summary>
    private string SkillsSummary(SessionVM vm)
    {
        var all = Skills(vm).Where(s => !s.Shadowed).ToList();
        if (all.Count == 0)
            return "No skills yet. Create one in Settings › Skills, import from Claude/Cursor, or try `/skill new <what it should do>`.";
        var selection = Selection(vm.Id);
        var lines = all.Take(40).Select(s =>
        {
            var mark = selection.Pinned.Contains(s.Id) ? "📌" : selection.Disabled.Contains(s.Id) ? "○ off" : "•";
            var tags = string.Join(", ", new[] { s.Origin.Label(), s.Kind == SkillKind.Skill ? null : s.Kind.Label().ToLowerInvariant(), s.AlwaysApply ? "always" : null }
                .Where(t => t is not null));
            return $"{mark} **{s.Name}** ({tags}) — {TextUtil.Prefix(s.Description, 90)}";
        });
        var text = $"Skills ({all.Count}):\n" + string.Join("\n", lines);
        if (all.Count > 40) text += $"\n… and {all.Count - 40} more (see the Skills button).";
        text += $"\n\nAuto-pick by the model: **{(selection.Auto ? "on" : "off")}**. `/skill <name>` selects one for this chat, `/<name>` runs it, `/skill new <what>` writes a new one.";
        if (PendingDrafts.Count > 0)
            text += $"\n📝 {PendingDrafts.Count} draft{(PendingDrafts.Count == 1 ? "" : "s")} awaiting your approval.";
        return text;
    }

    private void HandleSkillCommand(string? arg, SessionVM vm)
    {
        if (string.IsNullOrEmpty(arg))
        {
            vm.Note(SkillsSummary(vm));
            return;
        }
        var lower = arg.ToLowerInvariant();
        foreach (var verb in new[] { "new ", "generate ", "create " })
        {
            if (!lower.StartsWith(verb, StringComparison.Ordinal)) continue;
            var goal = arg[verb.Length..].Trim();
            if (goal.Length == 0) break;
            StartRun(vm, ct => GenerateSkillFromChatAsync(vm, goal, ct));
            return;
        }
        if (lower is "off" or "clear" or "none")
        {
            ClearPinnedSkills(vm);
            vm.Note("Cleared the skills selected for this chat.");
            return;
        }
        if (lower is "auto on" or "auto off")
        {
            SetAutoSkills(lower == "auto on", vm);
            vm.Note($"The model {(lower == "auto on" ? "can" : "can no longer")} choose skills on its own in this chat.");
            return;
        }
        var live = SkillPrompt.Active(Skills(vm), Selection(vm.Id));
        var key = SkillNaming.Slug(arg);
        var skill = live.FirstOrDefault(s => s.Slug == key) ?? live.FirstOrDefault(s => key.Length > 0 && s.Slug.Contains(key));
        if (skill is null)
        {
            vm.Note($"No active skill matches “{arg}”. `/skills` lists them.", MessageRole.Error);
            return;
        }
        var now = !IsPinned(skill, vm);
        SetPinned(skill, now, vm);
        vm.Note(now
            ? $"📌 **{skill.Name}** is selected for this chat — its instructions are included from the next message."
            : $"**{skill.Name}** is no longer selected for this chat.");
    }

    /// <summary>/name args: the skill or command being run, if the text names one.</summary>
    private (Skill Skill, string Args)? MatchSkillCommand(string text, SessionVM vm)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('/')) return null;
        var headLength = 0;
        while (headLength < trimmed.Length && !char.IsWhiteSpace(trimmed[headLength])) headLength++;
        var word = trimmed[1..headLength];
        if (word.Length == 0 || word.Contains('/') || word.Contains('\\')) return null;
        var args = trimmed[headLength..].Trim();
        var key = SkillNaming.Slug(word);
        var live = SkillPrompt.Active(Skills(vm), Selection(vm.Id));
        var skill = live.FirstOrDefault(s => s.UserInvocable && (s.Slug == key || string.Equals(s.Name, word, StringComparison.OrdinalIgnoreCase)));
        return skill is null ? null : (skill, args);
    }

    /// <summary>The message the model sees when the user runs a skill.</summary>
    private static string? InvocationText(Skill skill, string arguments)
    {
        if (skill.Document() is not { } doc) return null;
        var body = SkillArguments.Expand(doc.Body, arguments).Trim();
        if (body.Length > SkillPrompt.PerSkillCap) body = TextUtil.Prefix(body, SkillPrompt.PerSkillCap) + "\n[… truncated]";
        var withArgs = arguments.Length == 0 ? "" : $" with: {arguments}";
        return $"The user ran the skill “{skill.Name}”{withArgs}. Follow it now.\n\n" +
               $"<skill name=\"{skill.Name}\" directory=\"{skill.Directory}\">\n{body}\n</skill>";
    }

    /// <summary>Ask the model to write a skill (optionally from a chat, or improving one).</summary>
    public async Task<GeneratedSkill> GenerateSkillAsync(string goal, SessionVM? vm, string? improving = null,
                                                         CancellationToken ct = default)
    {
        var profile = await ResolveRouteAsync() ?? throw LlmException.NoModel();
        if (vm is not null && !_transcripts.ContainsKey(vm.Id)) Hydrate(vm);
        var conversation = vm is not null ? _transcripts.GetValueOrDefault(vm.Id) ?? [] : [];
        var request = new SkillGenerationRequest(goal, conversation, Skills(vm).Select(s => s.Name).ToList(), improving);
        return await SkillGenerator.GenerateAsync(MakeClient(profile), profile.Model, request, ct: ct);
    }

    /// <summary>/skill new …: write a skill from this chat and queue it for approval.</summary>
    private async Task GenerateSkillFromChatAsync(SessionVM vm, string goal, CancellationToken ct)
    {
        vm.Running = true;
        vm.Activity = "Writing a skill…";
        try
        {
            var generated = await GenerateSkillAsync(goal, vm, ct: ct);
            if (ct.IsCancellationRequested) return;
            var scope = vm.WorkspacePath is not null ? SkillScope.Project : SkillScope.User;
            var draft = SkillDrafts.Create(generated.Text, scope: scope, projectRoot: vm.WorkspacePath, source: "chat",
                locations: SkillLocations);
            RefreshDrafts();
            var warn = generated.Issues.Where(i => i.Severity >= SkillIssueSeverity.Warning).Select(i => i.Message).ToList();
            vm.Note($"📝 Drafted skill **{draft.Name}** — {draft.Description}\nIt is not active yet: open **Skills** under the composer to review, edit and approve it."
                    + (warn.Count == 0 ? "" : "\nNotes: " + string.Join(" ", warn)));
        }
        catch (OperationCanceledException)
        {
            vm.Note("Stopped.");
        }
        catch (Exception error)
        {
            vm.Note($"Couldn't write the skill: {Describe(error)}", MessageRole.Error);
        }
        finally
        {
            vm.Running = false;
            vm.Stopping = false;
            vm.Activity = null;
        }
    }

    // MARK: - Stopping

    public void StopSession(string id)
    {
        if (Session(id) is { } vm && vm.Running) vm.Stopping = true;
        ResolveAllGates(id, false);
        if (_runs.TryGetValue(id, out var cts)) cts.Cancel();
        // Stop in a chat stops what it launched in the background too.
        if (_backgroundPools.TryGetValue(id, out var pool)) pool.StopAll();
    }

    public void StopAll()
    {
        StopQueue();
        foreach (var id in _runs.Keys.Concat(_backgroundPools.Keys).Distinct().ToList()) StopSession(id);
    }

    /// <summary>The app is closing: cancel every run and background agent, but keep the queue's
    /// "resume on launch" so a queue that was running picks back up next time.</summary>
    public void Shutdown()
    {
        _shuttingDown = true;
        foreach (var id in _runs.Keys.Concat(_backgroundPools.Keys).Distinct().ToList()) StopSession(id);
    }

    private bool _shuttingDown;

    // MARK: - Context window

    /// <summary>The active model's context budget, in tokens: a user override in the provider config
    /// wins, then a value learned by probing the server, then the well-known model tables, then a
    /// conservative default. Read-only — the probe lives in <see cref="EnsureContextProbe"/>.</summary>
    public int ContextLimit()
    {
        if (Config.ActiveProvider is not { } provider) return FallbackContextWindow.DefaultLimit;
        if (provider.ContextWindow is > 0 and var overridden) return overridden;
        if (_probedContext.TryGetValue(provider.RouteId, out var probed)) return probed;
        return FallbackContextWindow.Limit(Effective(provider).Model) ?? FallbackContextWindow.DefaultLimit;
    }

    /// <summary>Where the current window figure came from, for /context and the gauge tooltip.</summary>
    public string ContextSource()
    {
        if (Config.ActiveProvider is not { } provider) return "default";
        if (provider.ContextWindow is > 0) return "set manually in Settings";
        if (_probedContext.ContainsKey(provider.RouteId)) return "detected from the server";
        if (FallbackContextWindow.Limit(Effective(provider).Model) is not null) return "from the built-in model table (server did not report one)";
        return "a conservative default (server did not report one — set it in Settings)";
    }

    /// <summary>The model id actually in use for the active route (follows a swap).</summary>
    public string? ActiveModelId => Config.ActiveProvider is { } p ? Effective(p).Model : null;

    /// <summary>The profile with the model the server actually serves.</summary>
    public ProviderProfile Effective(ProviderProfile provider)
    {
        if (_routeInfo.TryGetValue(provider.RouteId, out var info) && info.ServedModel.Length > 0)
            return provider.DeepCopy() with { Model = info.ServedModel, ApiKey = provider.ApiKey };
        return provider;
    }

    /// <summary>Ask the server what it serves and how big its window is, then return the profile to
    /// use. Cached for a few seconds so /goal rounds don't re-ask; a failed probe keeps what we knew.</summary>
    public async Task<ProviderProfile?> ResolveRouteAsync(bool force = false)
    {
        if (Config.ActiveProvider is not { } provider) return null;
        var routeId = provider.RouteId;
        var fresh = _routeInfo.TryGetValue(routeId, out var known) && DateTimeOffset.Now - known.At < TimeSpan.FromSeconds(15);
        if (force || !fresh)
        {
            var info = await MakeClient(provider).ModelInfoAsync();
            if (info.Served.Count > 0)
            {
                _routeInfo[routeId] = new RouteInfo(info.Id, info.Served, info.ContextWindow, DateTimeOffset.Now);
                if (info.ContextWindow is > 0 and var limit && _probedContext.GetValueOrDefault(routeId) != limit)
                    _probedContext[routeId] = limit;
                ContextInfoChanged?.Invoke();
            }
        }
        return Config.ActiveProvider is { } current ? Effective(current) : null;
    }

    /// <summary>Fire the server probe for the active route so ContextLimit fills in a learned value.
    /// Safe to call repeatedly: one in flight at a time, and a recent answer is reused.</summary>
    public void EnsureContextProbe()
    {
        if (Config.ActiveProvider is not { } provider) return;
        var routeId = provider.RouteId;
        if (_probingContext.Contains(routeId)) return;
        if (_routeInfo.TryGetValue(routeId, out var info) && DateTimeOffset.Now - info.At < TimeSpan.FromSeconds(60)) return;
        _probingContext.Add(routeId);
        _ = ProbeAsync();

        async Task ProbeAsync()
        {
            try
            {
                await ResolveRouteAsync(force: true);
            }
            catch (Exception)
            {
                // The gauge keeps its fallback figure.
            }
            finally
            {
                _probingContext.Remove(routeId);
            }
        }
    }

    /// <summary>Forget cached probes (Settings changed the route, or the user asked).</summary>
    public void ResetRouteCache()
    {
        _routeInfo.Clear();
        _probedContext.Clear();
        _engines.Clear();
        _engineKeys.Clear();
        ContextInfoChanged?.Invoke();
        EnsureContextProbe();
    }

    /// <summary>How much of the context a session is using right now, in tokens: the figure recorded
    /// at the end of the last turn (server-reported prompt tokens, or an estimate), plus the draft.</summary>
    public int ContextUsed(string sessionId, string draft)
    {
        int used;
        if (Session(sessionId)?.ContextUsed is { } recorded)
        {
            used = recorded;
        }
        else
        {
            var prompt = _systemPrompts.GetValueOrDefault(sessionId) ?? _basePrompt;
            used = TokenEstimate.Request(prompt, _transcripts.GetValueOrDefault(sessionId) ?? []);
        }
        var trimmed = draft.Trim();
        if (trimmed.Length > 0) used += Math.Max(1, trimmed.Length / 4);
        return used;
    }

    // MARK: - Compaction
    //
    // When the model transcript grows toward the context window, the older messages are replaced by
    // a summary the model writes, and the recent tail is kept verbatim. The engine calls this on
    // every model call (in-loop), so it also fires mid-run when tool output accumulates, and again
    // on a server overflow as a second line of defence.

    public async Task<IReadOnlyList<LlmMessage>> CompactTranscriptAsync(string sessionId, int used,
                                                                        IReadOnlyList<LlmMessage> messages,
                                                                        CancellationToken ct)
    {
        var limit = _engineKeys.TryGetValue(sessionId, out var key) ? key.Window : ContextLimit();
        if (Compaction.MakePlan(used, limit, messages) is not { } plan) return messages;
        var vm = Session(sessionId);
        if (vm is not null) vm.Activity = "Compacting conversation to fit the context window…";
        try
        {
            await ResolveRouteAsync();
            var profile = Config.ActiveProvider is { } p ? Effective(p) : null;
            if (profile is null) return messages;
            var summary = await Compaction.SummarizeAsync(MakeClient(profile), plan, model: profile.Model, cancellationToken: ct);
            // No summary (no client, or it failed): keep the transcript as-is; the server may still
            // accept it, and an overflow is surfaced rather than silently dropped.
            if (summary is null) return messages;
            var newMessages = new List<LlmMessage> { LlmMessage.SystemText(Compaction.SummaryHeader + summary) };
            newMessages.AddRange(plan.ToKeep);
            ApplyCompaction(sessionId, plan, summary, newMessages);
            return newMessages;
        }
        finally
        {
            if (vm is not null) vm.Activity = null;
        }
    }

    /// <summary>Rewrite the session's display + persisted log so the summarized part is marked by a
    /// compaction divider, and point the model transcript at the new (shorter) list.</summary>
    private void ApplyCompaction(string sessionId, CompactionPlan plan, string summary, List<LlmMessage> newMessages)
    {
        if (Session(sessionId) is not { } vm) return;
        _transcripts[sessionId] = newMessages;
        // Reset the gauge: the model now carries a small context.
        var prompt = _systemPrompts.GetValueOrDefault(sessionId) ?? _basePrompt;
        vm.ContextUsed = TokenEstimate.Request(prompt, newMessages);

        // The transcript's user-message count maps 1:1 onto the display's user entries, so the cut is
        // "the Nth+1 user entry", where N is how many user messages were summarized. Counting (instead
        // of matching text) keeps duplicates like repeated "hi" from cutting at the wrong spot.
        var summarizedUsers = plan.ToSummarize.Count(m => m.Role == Dsh.Core.MessageRole.User);
        var seenUsers = 0;
        int? cut = null;
        for (var i = 0; i < vm.Entries.Count; i++)
        {
            if (vm.Entries[i] is not MessageEntryVM { Role: MessageRole.User }) continue;
            if (seenUsers == summarizedUsers)
            {
                cut = i;
                break;
            }
            seenUsers++;
        }
        // Earlier messages stay visible above the divider (replay starts from the divider, so they
        // are not sent to the model again). A cut inside one long turn goes at the end.
        vm.Entries.Insert(cut ?? vm.Entries.Count, new CompactionEntryVM(plan.ToSummarize.Count, summary));
        // Re-sync the persisted log so a restart reloads the compacted conversation.
        Log.Resync(sessionId, LogRows(vm.Entries, sessionId));
        vm.NotifyContentChanged();
    }

    /// <summary>Persisted log rows for the current display entries — the single place that turns a
    /// (possibly compacted) session into storage.</summary>
    public static List<LogItemRow> LogRows(IEnumerable<ChatEntryVM> entries, string sessionId)
    {
        var output = new List<LogItemRow>();
        var seq = 0;
        foreach (var entry in entries)
        {
            var row = entry switch
            {
                MessageEntryVM m => new LogItemRow(sessionId, seq, m.Role switch
                {
                    MessageRole.User => "user",
                    MessageRole.Assistant => "assistant",
                    MessageRole.Notice => "notice",
                    _ => "error",
                }, m.Text, null, null, null, m.Role == MessageRole.Error, entry.At),
                ToolEntryVM t => new LogItemRow(sessionId, seq, "tool", t.Summary, t.Name, t.Preview, t.Output, t.IsOk == false, entry.At),
                // The removed count rides in ArgSummary so a reloaded divider still says how much
                // was folded away.
                CompactionEntryVM c => new LogItemRow(sessionId, seq, "compaction", c.Summary, "compaction", c.Removed.ToString(), null, false, entry.At),
                _ => new LogItemRow(sessionId, seq, "todos", null, null, null, null, false, entry.At),
            };
            output.Add(row);
            seq++;
        }
        return output;
    }

    public static string Describe(Exception error) => error switch
    {
        LlmException llm => llm.Message,
        HttpRequestException http => $"Network error: {http.Message}",
        _ => error.Message,
    };
}
