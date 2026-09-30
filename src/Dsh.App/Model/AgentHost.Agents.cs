using System.IO;
using Dsh.Core;

namespace Dsh.App.Model;

// MARK: - Subagents across servers, memory, caching
//
// Everything the host adds around a chat's engine beyond the model loop itself:
//  * the fleet — with more than one model server (say two DGX Sparks), the active route is the primary
//    (the main agent talks to it) and routes marked "subagent server" in Settings › Models are workers;
//    subagents run on the workers, spreading side tasks across machines
//  * a roster per chat, so the plan panel can show who is working on what, and where
//  * long-term memory — notes searched for each message, never loaded wholesale
//  * a result cache per chat, so repeated reads and searches don't cost time or context
// Runs on the UI thread like the rest of AgentHost; engines call back through the dispatcher.

public sealed partial class AgentHost
{
    /// <summary>Long-term memory: notes searched for each message (%APPDATA%\DSH\memory).</summary>
    public MemoryStore Memory { get; private set; } = null!;

    /// <summary>The model servers subagents run on. The active route is the primary; routes flagged
    /// "subagent server" are workers. Reconfigured whenever the routes change.</summary>
    public AgentFleet Fleet { get; } = new();

    private readonly Dictionary<string, AgentRoster> _rosters = new();
    private readonly Dictionary<string, ToolCache> _caches = new();
    private readonly Dictionary<string, MemoryRecallSession> _recalls = new();
    private readonly Dictionary<string, (ModelInfo Info, DateTimeOffset At)> _workerProbe = new();
    private readonly Dictionary<string, (AgentCatalog Catalog, string Stamp)> _catalogs = new(StringComparer.OrdinalIgnoreCase);

    private void InitAgents(MemoryStore? memory)
    {
        Memory = memory ?? new MemoryStore();
        Config.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppConfig.ActiveRoute) or nameof(AppConfig.SubagentsUsePrimary)) RefreshFleet();
        };
        Config.Providers.CollectionChanged += (_, _) => RefreshFleet();
        RefreshFleet();
    }

    // MARK: Fleet

    /// <summary>Rebuild the fleet from the configured routes. Call it after changing which routes are
    /// subagent servers (the route list itself is watched).</summary>
    public void RefreshFleet()
    {
        var servers = new List<FleetServer>();
        var active = Config.Providers.FirstOrDefault(p => p.RouteId == Config.ActiveRoute);
        if (active is not null)
        {
            servers.Add(new FleetServer
            {
                Id = active.RouteId,
                Label = RouteLabel(active),
                IsPrimary = true,
                MaxParallel = Math.Clamp(active.SubagentParallel ?? 4, 1, 16),
                // The main route is probed the way the chat probes it (and shares its cache).
                Resolve = _ => _dispatcher.InvokeAsync(ResolvePrimaryTargetAsync).Task.Unwrap(),
            });
        }
        foreach (var route in WorkerRoutes)
        {
            var captured = route;
            servers.Add(new FleetServer
            {
                Id = captured.RouteId,
                Label = RouteLabel(captured),
                IsPrimary = false,
                MaxParallel = Math.Clamp(captured.SubagentParallel ?? 2, 1, 16),
                Resolve = ct => ResolveWorkerTargetAsync(captured, ct),
            });
        }
        Fleet.UsePrimaryWhenWorkersBusy = Config.SubagentsUsePrimary;
        Fleet.Configure(servers);
    }

    /// <summary>How a route is named next to a subagent: the route's own name (e.g. "Spark 2").</summary>
    public static string RouteLabel(ProviderProfile route) => route.Name.Length > 0 ? route.Name : route.Model;

    /// <summary>The routes subagents currently run on (workers), for the settings summary. One self-hosted machine is one
    /// worker however many models it is listed with (a Spark serves one at a time, so a second route there would only queue
    /// behind the first), and the main route's own machine is never its own worker. A hosted service (Bedrock, OpenAI...)
    /// serves every model at once: those are told apart by route, so a cheaper model in the same Region can take the side
    /// tasks while a stronger one is the main agent.</summary>
    public IReadOnlyList<ProviderProfile> WorkerRoutes
    {
        get
        {
            var primary = Config.Providers.FirstOrDefault(p => p.RouteId == Config.ActiveRoute);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (primary is not null) seen.Add(MachineOf(primary));
            return Config.Providers.Where(p => p.SubagentWorker && p.RouteId != Config.ActiveRoute && seen.Add(MachineOf(p))).ToList();
        }
    }

    private static string MachineOf(ProviderProfile route) =>
        route.Kind != ProviderKind.Bedrock && route.IsSelfHosted ? route.BaseUrl.Trim().TrimEnd('/') : "route:" + route.RouteId;

    private async Task<FleetTarget> ResolvePrimaryTargetAsync()
    {
        var profile = await ResolveRouteAsync().ConfigureAwait(true) ?? throw LlmException.NoModel();
        return new FleetTarget(MakeClient(profile), profile.Model, ContextLimit())
        {
            Vision = VisionOn(profile), Temperature = profile.Temperature, MaxOutputTokens = profile.MaxOutputTokens,
        };
    }

    private async Task<FleetTarget> ResolveWorkerTargetAsync(ProviderProfile route, CancellationToken cancellationToken)
    {
        // The route, its key and the vision setting belong to the UI thread (Settings edits them; keys are cached there): take
        // a copy there, and do the slow part — asking the server what it serves — here on the caller's thread.
        var (profile, vision) = await _dispatcher.InvokeAsync(() =>
        {
            var copy = route.DeepCopy();
            var key = Config.ApiKey(route);
            copy.ApiKey = key.Length == 0 ? null : key;
            return (copy, VisionOn(copy));
        }).Task.ConfigureAwait(false);
        var client = MakeClient(profile);
        var model = profile.Model;
        var window = profile.ContextWindow;
        if (profile.Kind != ProviderKind.Bedrock)
        {
            // Ask the server what it serves right now (a Spark may have swapped models); the answer is
            // reused for a minute. A server that names no model at all isn't answering.
            ModelInfo? info = null;
            lock (_workerProbe)
            {
                if (_workerProbe.TryGetValue(route.RouteId, out var known) && DateTimeOffset.Now - known.At < TimeSpan.FromSeconds(60)) info = known.Info;
            }
            if (info is null)
            {
                info = await client.ModelInfoAsync(null, cancellationToken).ConfigureAwait(false);
                if (info.Served.Count == 0) throw LlmException.Connection($"{route.BaseUrl} didn't answer");
                lock (_workerProbe) _workerProbe[route.RouteId] = (info, DateTimeOffset.Now);
            }
            if (!string.IsNullOrEmpty(info.Id)) model = info.Id;
            window ??= info.ContextWindow;
        }
        return new FleetTarget(client, model, window ?? FallbackContextWindow.Limit(model) ?? FallbackContextWindow.DefaultLimit)
        {
            Vision = vision, Temperature = profile.Temperature, MaxOutputTokens = profile.MaxOutputTokens,
        };
    }

    /// <summary>Forget what was learned about the subagent servers (Settings changed, or the user asked).</summary>
    private void ResetWorkerProbes()
    {
        lock (_workerProbe) _workerProbe.Clear();
    }

    // MARK: Per-chat helpers

    /// <summary>This chat's subagent roster (created on first use); its changes are mirrored into the
    /// session so the plan panel can show them.</summary>
    private AgentRoster RosterFor(string sessionId)
    {
        if (_rosters.TryGetValue(sessionId, out var existing)) return existing;
        var roster = new AgentRoster();
        roster.Changed += run =>
        {
            if (run.Id.Length == 0) return;
            _dispatcher.BeginInvoke(() => Session(sessionId)?.UpsertAgent(run));
        };
        _rosters[sessionId] = roster;
        return roster;
    }

    private ToolCache CacheFor(string sessionId)
    {
        if (_caches.TryGetValue(sessionId, out var existing)) return existing;
        return _caches[sessionId] = new ToolCache();
    }

    private MemoryRecallSession RecallFor(string sessionId)
    {
        if (_recalls.TryGetValue(sessionId, out var existing)) return existing;
        return _recalls[sessionId] = new MemoryRecallSession();
    }

    /// <summary>What the cache has saved in a chat so far ("12 repeated lookups answered from cache…"); empty if nothing yet.</summary>
    public string CacheSummary(string sessionId) => _caches.TryGetValue(sessionId, out var cache) ? cache.Stats.Describe() : "";

    /// <summary>The subagent types this chat offers: built-ins plus agent files from the user's folders and the project's.</summary>
    private AgentCatalog CatalogFor(SessionVM vm)
    {
        var root = vm.WorkspacePath ?? "";
        // A file added or edited while the app is running is picked up at the next turn (the stamp is a few directory listings).
        var stamp = AgentCatalog.Stamp(vm.WorkspacePath);
        if (_catalogs.TryGetValue(root, out var known) && known.Stamp == stamp) return known.Catalog;
        var catalog = AgentCatalog.Load(vm.WorkspacePath);
        _catalogs[root] = (catalog, stamp);
        return catalog;
    }

    /// <summary>Changes whenever an agent file is added, removed or edited in this chat's folders — so the engine (which
    /// carries the agent types, their tools and the prompt that lists them) is rebuilt for the next turn.</summary>
    private string AgentsStampFor(SessionVM vm)
    {
        CatalogFor(vm);
        return _catalogs.TryGetValue(vm.WorkspacePath ?? "", out var known) ? known.Stamp : "";
    }

    /// <summary>Re-read agent files (the user edited one, or opened another project).</summary>
    public void RefreshAgentTypes()
    {
        _catalogs.Clear();
        _engines.Clear();
    }

    /// <summary>Raised the first time a chat makes a plan (a todo list, or a proposed plan), so the window can
    /// show the plan panel by itself. Once per chat: closing the panel is not undone by every later update.</summary>
    public event Action<SessionVM>? PlanAppeared;
    private readonly HashSet<string> _plansAnnounced = new();

    private void AutoOpenPlan(SessionVM vm)
    {
        // A plan made in a chat that isn't open is not announced yet: the window would decline it, and the one chance would
        // be gone by the time the user gets there. It is announced with the first update they are there for.
        if (!Config.PlanPanelAutoOpen || vm.Id != SelectedId) return;
        if (_plansAnnounced.Add(vm.Id)) PlanAppeared?.Invoke(vm);
    }

    private void ForgetChatState(string sessionId)
    {
        _plansAnnounced.Remove(sessionId);
        _rosters.Remove(sessionId);
        _caches.Remove(sessionId);
        _recalls.Remove(sessionId);
        _stoppedChats.Remove(sessionId);
        _failedRuns.Remove(sessionId);
    }

    // MARK: Memory in the prompt

    /// <summary>The memory section of the system prompt (how to use the tools, the few pinned notes), or empty
    /// when memory is off.</summary>
    private string MemoryPromptFor(string? workspace, bool canSave = true) =>
        Config.MemoryEnabled ? MemoryPrompt.Section(Memory, workspace, canSave) : "";

    /// <summary>The notes that bear on <paramref name="message"/>, as text to put in front of it; null when
    /// none do (the usual case). Shows a one-line notice of what was recalled.</summary>
    private string? RecallBlockFor(SessionVM vm, string message)
    {
        if (!Config.MemoryEnabled) return null;
        MemoryRecallResult? recall;
        try
        {
            recall = MemoryRecall.Build(Memory, message, vm.WorkspacePath, RecallFor(vm.Id));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null; // memory is a help, never a reason for a turn to fail
        }
        if (recall is null) return null;
        var titles = string.Join(" · ", recall.Items.Select(i => TextUtil.Prefix(i.Title, 40)));
        vm.Note($"🧠 Remembered {recall.Items.Count} note{(recall.Items.Count == 1 ? "" : "s")}: {titles}");
        return recall.Block;
    }

    // MARK: /remember, /memory, /agents

    private void RememberCommand(string text, SessionVM vm)
    {
        var note = text.Trim();
        // "global: …" makes a note for every chat; otherwise it belongs to the folder this chat works in.
        var global = false;
        foreach (var prefix in new[] { "global:", "everywhere:" })
        {
            if (!note.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            global = true;
            note = note[prefix.Length..].Trim();
            break;
        }
        if (note.Length == 0)
        {
            vm.Note("Usage: `/remember <what to remember>` — for example `/remember the staging server is called orion`. " +
                    "It belongs to this chat's project; start with `global:` for a note that applies everywhere. " +
                    "`/memory` shows everything that's remembered.");
            return;
        }
        if (SecretGuard.LooksLikeSecret(note))
        {
            vm.Note("That looks like a key or password, and memory is stored as plain text. Put it in the Credentials Vault (Ctrl+Shift+K) instead.", MessageRole.Error);
            return;
        }
        var project = global ? null : vm.WorkspacePath;
        var saved = Memory.Save(new MemoryDraft { Body = note, Source = "user", Kind = MemoryKinds.Note, Project = project });
        var where = project is null ? "in every chat" : "in this project";
        var text2 = saved.Kind == MemorySaveKind.Created
            ? $"🧠 Remembered {where}: “{TextUtil.Prefix(saved.Item.Title, 80)}”. " +
              (Config.MemoryEnabled ? "It will come up when it's relevant; `/memory` lets you edit or remove it."
                                    : "Memory is switched off right now, so it isn't used until you turn it back on (Session › Remembered Notes).")
            : $"🧠 Updated the note “{TextUtil.Prefix(saved.Item.Title, 80)}”.";
        if (Memory.StorageProblem is { } problem) text2 += "\n⚠️ " + problem + " The note is kept for this session.";
        vm.Note(text2);
    }

    private void MemoryCommand(string? query, SessionVM vm)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            var stats = Memory.Stats();
            vm.Note($"Memory: {stats.Count} note{(stats.Count == 1 ? "" : "s")} ({stats.Pinned} pinned). Opening the memory manager.");
            OpenMemoryManagerRequested?.Invoke();
            return;
        }
        var hits = Memory.Search(query, new MemoryQueryOptions { Project = vm.WorkspacePath, Limit = 8, IncludePinned = true, Browse = true });
        if (hits.Count == 0)
        {
            vm.Note($"Nothing remembered matches “{query}”.");
            return;
        }
        vm.Note($"Remembered notes matching “{query}”:\n" + string.Join("\n", hits.Select(h =>
            $"- **{h.Item.Title}** [{h.Item.Id}] — {TextUtil.Prefix(h.Item.Body.Replace('\n', ' '), 140)}")));
    }

    /// <summary>Raised when a slash command wants the memory manager window.</summary>
    public event Action? OpenMemoryManagerRequested;

    private void AgentsCommand(SessionVM vm)
    {
        var lines = new List<string> { "**Subagent types** (the agent tool's `agent_type`):" };
        foreach (var type in CatalogFor(vm).All)
            lines.Add($"- `{type.Name}` — {TextUtil.Prefix(type.Description, 110)}" + (type.Origin == "built-in" ? "" : $" *({type.Origin})*"));
        lines.Add("");
        var fleet = Fleet.Snapshot();
        if (fleet.Count == 0)
        {
            lines.Add("**Model servers:** none configured.");
        }
        else
        {
            lines.Add(fleet.Count == 1
                ? "**Model server:** subagents share the one server. Mark more routes “Use for subagents” in Settings › Models to spread them across machines."
                : "**Model servers** (the main agent uses the primary; subagents go to workers first):");
            foreach (var server in fleet)
            {
                var health = server.Healthy ? "" : " — not answering, skipped for a minute";
                lines.Add($"- {(server.IsPrimary ? "🖥 primary" : "⚙ worker")} **{server.Label}** — {server.InFlight}/{server.MaxParallel} busy, {server.Completed} done{(server.Failed > 0 ? $", {server.Failed} failed" : "")}{health}");
            }
        }
        var running = vm.Agents.Where(a => a.IsRunning).ToList();
        lines.Add("");
        lines.Add(running.Count == 0
            ? "No subagents running in this chat."
            : "**Running now:**\n" + string.Join("\n", running.Select(a => $"- {a.Description} ({a.AgentType}{(a.Server is null ? "" : " · " + a.Server)}) — {a.Activity}")));
        vm.Note(string.Join("\n", lines));
    }
}
