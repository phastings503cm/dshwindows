using System.Net.Sockets;

namespace Vision.Core;

// MARK: - The fleet
//
// Every chat's own model is the *primary* for its subagents: the main agent talks to it. The routes the person enlisted
// for subagents are *workers*, shared by every chat: subagents run on them, so a fan-out of tasks spreads across machines
// instead of queueing on one. The fleet hands out leases on servers — the one the primary picked when it named one (waited
// for up to 2 minutes), else the least loaded worker, with the chat's own model only as overflow — and steers away from a
// server that has just failed. Which workers a chat may use is its pool (SubagentPool); where one task goes is one pure
// decision (SubagentPlacement). A task marked sensitive goes only to a private server (the user's own hardware, Guardian,
// Bedrock in their account), never to an online service.

/// <summary>Where one subagent runs: the server's client and model, its context window, and — when they differ
/// from the parent's — the settings of that server's route (null = inherit the parent's).</summary>
public sealed record FleetTarget(ILlmClient Client, string Model, int? ContextWindow = null)
{
    public bool? Vision { get; init; }
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
    /// <summary>The target is another route's server, with settings of its own: its temperature, output limit and thinking
    /// level are that route's (null = that provider's default), never the parent chat's. A Kimi or GLM worker must not be
    /// sent the temperature, max_tokens or reasoning effort the main model's route was set up with — a server that rejects
    /// them answers 400 and the subagent quietly ends up somewhere else. False (the primary): the parent's, as before.</summary>
    public bool OwnSettings { get; init; }
    /// <summary>The thinking level for this target when it has <see cref="OwnSettings"/> (null = the route's own default).</summary>
    public ThinkingLevel? Thinking { get; init; }
    /// <summary>An engine of its own works the task instead of Vision's (Claude Code: one `claude -p` job in the chat's
    /// folder); null = Vision's engine on <see cref="Client"/>.</summary>
    public IExternalSubagent? External { get; init; }
}

/// <summary>One model server as the fleet sees it.</summary>
public sealed class FleetServer
{
    /// <summary>Stable identity (the route id).</summary>
    public required string Id { get; init; }
    /// <summary>What the user calls it ("spark-2"); shown next to a subagent that ran on it.</summary>
    public required string Label { get; init; }
    /// <summary>The server the main agent talks to.</summary>
    public bool IsPrimary { get; init; }
    /// <summary>May see sensitive data unmasked (see <see cref="ProviderProfile.IsPrivateRoute"/>): a sensitive task runs
    /// only on such a server.</summary>
    public bool IsPrivate { get; init; }
    /// <summary>Its plan's terms allow only interactive use (see <see cref="ProviderProfile.IsInteractiveOnly"/>): unattended
    /// work — a chat's task list, a /goal carrying on by itself — never runs on it.</summary>
    public bool InteractiveOnly { get; init; }
    /// <summary>How many subagents may work on it at once.</summary>
    public int MaxParallel { get; init; } = 2;
    /// <summary>Probe the server and return its client. Throws if it can't be reached.</summary>
    public required Func<CancellationToken, Task<FleetTarget>> Resolve { get; init; }
    /// <summary>What the main agent calls it in the <c>server</c> argument ("dgx-spark"; "primary" for a chat's own model).</summary>
    public string? Handle { get; init; }
    /// <summary>What counts as its machine (see <see cref="SubagentPool.Machine(SubagentRouteFacts)"/>).</summary>
    public string? Machine { get; init; }
    /// <summary>Its route kind, in the pool's words ("openai-compatible", "claude-code", …).</summary>
    public string? Kind { get; init; }
    /// <summary>A chat's own model, standing in as the primary for that chat's subagents.</summary>
    public bool IsHome { get; init; }
}

public sealed record FleetStatus(string Id, string Label, bool IsPrimary, int InFlight, int MaxParallel,
                                 bool Healthy, DateTimeOffset? UnhealthyUntil, int Completed, int Failed)
{
    /// <summary>"primary" (a configured primary), "worker" (a subagent server) or "home" (a chat's own model, listed while
    /// its subagents run on it).</summary>
    public string Role { get; init; } = IsPrimary ? "primary" : "worker";
    /// <summary>What the main agent calls it ("dgx-spark", "primary").</summary>
    public string? Handle { get; init; }
    public bool IsHome => Role == "home";
}

/// <summary>One task asking the fleet for a server.</summary>
public sealed record FleetRequest
{
    /// <summary>The server the main agent picked with <c>server</c> (its id — the home's for "primary"); waited for up to
    /// <see cref="AgentFleet.PinWait"/> when busy, then only a preference.</summary>
    public string? Pinned { get; init; }
    /// <summary>An agent file's soft preference (matched against names, ids and handles).</summary>
    public string? Preferred { get; init; }
    /// <summary>It needs sensitive data: private servers only.</summary>
    public bool Sensitive { get; init; }
    /// <summary>A queue task or a /goal: no server whose plan allows interactive use only.</summary>
    public bool Unattended { get; init; }
    /// <summary>The asking chat's own server, which stands in for the primary.</summary>
    public FleetServer? Home { get; init; }
    /// <summary>The ids of the configured servers this chat may use besides its home (null = every one).</summary>
    public IReadOnlySet<string>? Pool { get; init; }
    /// <summary>The asking chat is Sensitive: a server that only sees it masked gets no pictures (the shield can't check them).</summary>
    public bool SensitiveChat { get; init; }
}

/// <summary>A subagent server that can't take work until a known time (a Claude plan's usage limit): a server fault, and the
/// fleet keeps work off it until <see cref="Until"/> (else <see cref="AgentFleet.UsageLimitPause"/>).</summary>
public sealed class SubagentServerPausedException(string message, DateTimeOffset? until) : Exception(message)
{
    public DateTimeOffset? Until { get; } = until;
}

/// <summary>No configured server answered.</summary>
public class FleetUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Unattended work, and every server that could take it is for interactive use only.</summary>
public sealed class NoUnattendedServerException() : FleetUnavailableException(Text)
{
    /// <summary>What the model is told.</summary>
    public const string Text = "The model servers that could take this task are for interactive use only under their plans' terms, so unattended work can't run there. Do it yourself.";
}

/// <summary>A sensitive task, and no private server to run it on.</summary>
public sealed class NoPrivateServerException(Exception? inner = null) : FleetUnavailableException(Text, inner)
{
    /// <summary>What the model is told.</summary>
    public const string Text = "No private server is free for a sensitive task. Do it yourself, or rewrite it without the sensitive details and try again.";
}

/// <summary>A slot on one server. Dispose it when the work is over; call <see cref="Fail"/> first if the
/// server itself was the problem, so the next task goes elsewhere.</summary>
public sealed class FleetLease : IDisposable
{
    private readonly AgentFleet _fleet;
    private int _released;
    private bool _failed;
    private bool _abandoned;

    internal FleetLease(AgentFleet fleet, FleetServer server, FleetTarget target, IReadOnlyList<string>? skipped = null,
                        PlacementReason? reason = null, string? note = null)
    {
        _fleet = fleet;
        Server = server;
        Target = target;
        Skipped = skipped ?? [];
        Reason = reason;
        Note = note;
    }

    public FleetServer Server { get; }
    public FleetTarget Target { get; }
    public string Label => Server.Label;
    /// <summary>What the main agent calls the server ("dgx-spark", "primary").</summary>
    public string Handle => Server.Handle ?? Server.Label;
    /// <summary>Why it runs here (see <see cref="SubagentPlacement"/>).</summary>
    public PlacementReason? Reason { get; }
    /// <summary>What a fallback is about ("spark-2 stayed busy for 2 minutes").</summary>
    public string? Note { get; }
    /// <summary>Servers tried first that couldn't take the work, and why ("DGX Spark: didn't answer") — so the person can
    /// see why a subagent runs where it does.</summary>
    public IReadOnlyList<string> Skipped { get; }

    /// <summary>The server misbehaved (unreachable, overloaded, erroring): keep new work away from it for a while.</summary>
    public void Fail(Exception error)
    {
        _failed = true;
        _fleet.MarkFailed(Server.Id, error);
    }

    /// <summary>The work was stopped rather than finished or failed: free the slot without counting a success (which
    /// would also clear a blacklisting the server earned).</summary>
    public void Abandon() => _abandoned = true;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _fleet.Release(Server.Id, _failed, counted: !_abandoned);
    }
}

/// <summary>Thread-safe; one per app, shared by every chat so two chats can't both fill the same Spark.</summary>
public sealed class AgentFleet
{
    private sealed class State
    {
        public int InFlight;
        public int Completed;
        public int Failed;
        public DateTimeOffset? UnhealthyUntil;
        public string? LastError;
    }

    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private List<FleetServer> _servers = [];
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly List<TaskCompletionSource> _waiters = [];
    /// <summary>Chats' own servers with subagents on them right now (for the status list).</summary>
    private readonly Dictionary<string, FleetServer> _homes = new(StringComparer.Ordinal);

    public AgentFleet(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>A server that just failed is left alone this long (unless every server is down).</summary>
    public TimeSpan UnhealthyFor { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long a busy server the main agent picked is waited for (2 minutes; tests shorten it).</summary>
    public TimeSpan PinWait { get; set; } = SubagentPlacement.PinWait;

    /// <summary>A Claude plan's usage limit with no reset time keeps Claude Code off this long.</summary>
    public static readonly TimeSpan UsageLimitPause = TimeSpan.FromMinutes(15);

    /// <summary>When every worker is busy, let subagents run on the primary too (a vLLM server batches
    /// requests, so this is usually a gain). False keeps the primary free for the main agent.</summary>
    public bool UsePrimaryWhenWorkersBusy { get; set; } = true;

    /// <summary>Raised (on any thread) when a lease is granted or released, a server fails, or the set changes.</summary>
    public event Action? Changed;

    // (A listener's problem is not the fleet's: a slot must never be lost to one.)
    private void RaiseChanged() => Listeners.Raise(Changed);

    public IReadOnlyList<FleetServer> Servers
    {
        get
        {
            lock (_lock) return [.. _servers];
        }
    }

    public bool HasWorkers
    {
        get
        {
            lock (_lock) return _servers.Any(s => !s.IsPrimary);
        }
    }

    /// <summary>Replace the set of servers (Settings changed, the route was switched). Work already leased
    /// carries on; counters of servers that stay are kept.</summary>
    public void Configure(IEnumerable<FleetServer> servers)
    {
        List<TaskCompletionSource> wake;
        lock (_lock)
        {
            _servers = servers.ToList();
            foreach (var server in _servers) _states.TryAdd(server.Id, new State());
            foreach (var gone in _states.Keys.Where(id => _servers.All(s => s.Id != id)).ToList())
            {
                if (_states[gone].InFlight != 0) continue;
                _states.Remove(gone);
                _homes.Remove(gone);
            }
            wake = TakeWaitersLocked();
        }
        Wake(wake);
        RaiseChanged();
    }

    /// <summary>Every configured server, then each chat's own server that has subagents on it now (role "home").</summary>
    public IReadOnlyList<FleetStatus> Snapshot()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            FleetStatus Status(FleetServer s, string? role)
            {
                var state = _states.GetValueOrDefault(s.Id) ?? new State();
                return new FleetStatus(s.Id, s.Label, s.IsPrimary || role == "home", state.InFlight, s.MaxParallel, IsHealthyLocked(state, now),
                    state.UnhealthyUntil > now ? state.UnhealthyUntil : null, state.Completed, state.Failed)
                {
                    Role = role ?? (s.IsPrimary ? "primary" : "worker"),
                    Handle = s.Handle,
                };
            }
            var list = _servers.Select(s => Status(s, null)).ToList();
            foreach (var home in _homes.Values.OrderBy(h => h.Label, StringComparer.CurrentCultureIgnoreCase))
            {
                if (_servers.Any(s => s.Id == home.Id) || (_states.GetValueOrDefault(home.Id)?.InFlight ?? 0) == 0) continue;
                list.Add(Status(home, "home"));
            }
            return list;
        }
    }

    /// <summary>How busy each of a chat's pool entries is now, keyed by entry id — for <see cref="SubagentPool.LoadLine"/>. The
    /// chat's own model is <paramref name="home"/> (its count is shared with the same route serving other chats as a worker).</summary>
    public IReadOnlyDictionary<string, SubagentPool.Load> Loads(SubagentPool pool, FleetServer? home)
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            var loads = new Dictionary<string, SubagentPool.Load>(StringComparer.Ordinal);
            foreach (var entry in pool.Entries)
            {
                var id = entry.IsPrimary ? home?.Id ?? entry.RouteId : entry.RouteId;
                var state = _states.GetValueOrDefault(id) ?? new State();
                var until = state.UnhealthyUntil is { } u && u > now ? u : (DateTimeOffset?)null;
                loads[entry.Id] = new SubagentPool.Load(state.InFlight, until is null, until is { } back ? (back - now).TotalSeconds : null);
            }
            return loads;
        }
    }

    /// <summary>How many servers one request may choose between (its home included).</summary>
    public int ChoiceCount(FleetServer? home, IReadOnlySet<string>? pool)
    {
        lock (_lock) return ServersLocked(home, pool).Count;
    }

    /// <summary>Wait for a free slot on a suitable server and lease it. <paramref name="preferred"/> names a
    /// server the caller would like (matched against the label, id and handle); it is a preference, not a demand.
    /// A <paramref name="sensitive"/> task goes only to a private server: a private worker, or the primary when it is
    /// private and may take subagents (always, when there are no workers at all).
    /// Throws <see cref="FleetUnavailableException"/> when no server can be reached, and
    /// <see cref="NoPrivateServerException"/> when a sensitive task has no private server to go to. <paramref name="unattended"/>
    /// work (a chat's task list, a /goal carrying on by itself) never goes to a server whose plan allows only interactive use.</summary>
    /// <param name="home">The asking chat's own server: it stands in for the configured primary, so a chat's subagents fall back
    /// to the model that chat uses — never to the one another chat picked (the workers are shared). Null = the configured
    /// primary.</param>
    public Task<FleetLease> AcquireAsync(string? preferred, CancellationToken cancellationToken, bool sensitive = false, bool unattended = false,
                                         FleetServer? home = null) =>
        AcquireAsync(new FleetRequest { Preferred = preferred, Sensitive = sensitive, Unattended = unattended, Home = home }, cancellationToken);

    /// <summary>Lease a server for one task (see <see cref="SubagentPlacement"/> for the order). A server the main agent picked
    /// (<see cref="FleetRequest.Pinned"/>) is waited for up to <see cref="PinWait"/> when busy; after that, or when it can't take
    /// the task, another server runs it and the lease's <see cref="FleetLease.Note"/> says why. Throws
    /// <see cref="FleetUnavailableException"/> when no server can take it, and <see cref="OperationCanceledException"/> when the
    /// task is cancelled while it waits (a lease already being resolved is given back).</summary>
    public async Task<FleetLease> AcquireAsync(FleetRequest? request, CancellationToken cancellationToken)
    {
        // (AcquireAsync(null, ct) reads as "no preference" whichever overload it binds to.)
        request ??= new FleetRequest();
        DateTimeOffset? deadline = request.Pinned is null ? null : _clock.GetUtcNow() + PinWait;
        var tried = new HashSet<string>(StringComparer.Ordinal);
        var skipped = new List<string>();
        Exception? lastError = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FleetServer? chosen = null;
            PlacementReason reason = PlacementReason.LeastBusy;
            string? note = null;
            TaskCompletionSource? waiter = null;
            TimeSpan? timeout = null;
            lock (_lock)
            {
                var servers = ServersLocked(request.Home, request.Pool);
                if (servers.Count == 0) throw new FleetUnavailableException(SubagentPlacement.NoServer);
                var now = _clock.GetUtcNow();
                var candidates = servers.Select(server =>
                {
                    var state = _states.GetValueOrDefault(server.Id);
                    return new SubagentPlacement.Candidate
                    {
                        Id = server.Id, Handle = server.Handle ?? server.Label, Label = server.Label, IsPrimary = server.IsPrimary,
                        IsPrivate = server.IsPrivate, InteractiveOnly = server.InteractiveOnly, InFlight = state?.InFlight ?? 0,
                        Parallel = server.MaxParallel, HealthyAt = state?.UnhealthyUntil, Error = state?.LastError,
                    };
                }).ToList();
                var decision = SubagentPlacement.Choose(new SubagentPlacement.Request
                {
                    Pinned = request.Pinned, AgentFile = request.Preferred, Sensitive = request.Sensitive, Unattended = request.Unattended,
                    PinDeadline = deadline, Tried = tried, Overflow = UsePrimaryWhenWorkersBusy, LastError = lastError?.Message, PinWait = PinWait,
                }, candidates, now);
                switch (decision)
                {
                    case SubagentPlacement.Decision.Run run:
                        chosen = servers.First(s => s.Id == run.Id);
                        StateLocked(chosen.Id).InFlight++;
                        if (chosen.IsHome || chosen.Id == request.Home?.Id) _homes[chosen.Id] = chosen;
                        (reason, note) = (run.Reason, run.Note);
                        break;
                    case SubagentPlacement.Decision.Wait wait:
                        waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        _waiters.Add(waiter);
                        if (wait.Until is { } until) timeout = until - now + TimeSpan.FromMilliseconds(50);
                        break;
                    case SubagentPlacement.Decision.Unavailable unavailable:
                        throw unavailable.Message switch
                        {
                            NoPrivateServerException.Text => new NoPrivateServerException(lastError),
                            NoUnattendedServerException.Text => new NoUnattendedServerException(),
                            _ => new FleetUnavailableException(unavailable.Message, lastError),
                        };
                }
            }
            if (chosen is null)
            {
                try
                {
                    // A blacklisted server comes back by itself when its time is up, and a pin's wait ends at its deadline —
                    // with nothing else to wake us.
                    if (timeout is { } after) await waiter!.Task.WaitAsync(after < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : after, cancellationToken).ConfigureAwait(false);
                    else await waiter!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    lock (_lock) _waiters.Remove(waiter!);
                }
                catch (OperationCanceledException)
                {
                    lock (_lock) _waiters.Remove(waiter!);
                    throw;
                }
                continue;
            }
            RaiseChanged();
            try
            {
                var target = await chosen.Resolve(cancellationToken).ConfigureAwait(false);
                // A Sensitive chat sends a server that only sees it masked no pictures: the shield can't check them.
                if (request.SensitiveChat && !chosen.IsPrivate) target = target with { Vision = false };
                return new FleetLease(this, chosen, target, skipped.ToList(), reason, note);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Release(chosen.Id, failed: false, counted: false);
                throw;
            }
            catch (Exception error)
            {
                // Couldn't reach it: note that, free the slot, and try the next server.
                lastError = error;
                skipped.Add($"{chosen.Label}: {TextUtil.Prefix(error.Message, 140)}");
                tried.Add(chosen.Id);
                MarkFailed(chosen.Id, error);
                Release(chosen.Id, failed: true);
            }
        }
    }

    /// <summary>Whether <paramref name="server"/> is the one a preference names (its label, route id or handle contains it, any
    /// case) — how an agent file's <c>server:</c> line picks a server.</summary>
    public static bool Matches(FleetServer server, string preferred)
    {
        var wanted = preferred.Trim();
        return wanted.Length > 0 && (server.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                     || server.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                     || server.Handle?.Contains(wanted, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>A worker counts as reachable only if it lists its models — true for a server you run yourself (a Spark that is
    /// down or swapping lists nothing). A hosted API (Kimi, Z.ai, OpenRouter…) is trusted with its configured model instead:
    /// some plans' keys can't list models, and treating that as "down" sent every subagent to the main model without a word.</summary>
    public static bool WorkerNeedsModelList(ProviderProfile route) => route.IsSelfHosted || string.IsNullOrWhiteSpace(route.Model);

    /// <summary>Whether <paramref name="error"/> is the server's fault rather than the request's — the kind
    /// of failure another server might not have.</summary>
    public static bool IsServerFault(Exception error)
    {
        if (error is OperationCanceledException) return false;
        // A worker that can't take work for now (a Claude plan's usage limit).
        if (error is SubagentServerPausedException) return true;
        // Claude Code that isn't there, isn't signed in, is out of its plan's usage, is too old for a flag or won't run in this
        // folder: another server can do the task. A task that failed on its own would fail anywhere.
        if (error is ClaudeCodeException claude)
            return claude.Failure.Kind is ClaudeCodeFailureKind.NotSignedIn or ClaudeCodeFailureKind.RateLimited or ClaudeCodeFailureKind.UnknownOption
                or ClaudeCodeFailureKind.NotInstalled or ClaudeCodeFailureKind.UntrustedFolder;
        // Credentials that one server rejects may be fine on another (a worker with a stale key, a Bedrock sign-in that expired).
        if (error is LlmException { Kind: LlmErrorKind.Http, StatusCode: 401 or 403 }) return true;
        // Whatever the retry policy would wait out — unreachable, overloaded, restarting, swapping models, "still loading" — is
        // that server's problem, and another server may not have it. What it would give up on at once is the request's.
        return RequestRetry.Disposition(error) is not RetryDisposition.Fail;
    }

    /// <summary>Keep new work off a server that failed: until <paramref name="until"/> when given, until a usage limit resets
    /// (else <see cref="UsageLimitPause"/>) for a paused one, else for <see cref="UnhealthyFor"/>.</summary>
    internal void MarkFailed(string id, Exception error, DateTimeOffset? until = null)
    {
        var fault = IsServerFault(error) || error is FleetUnavailableException;
        lock (_lock)
        {
            if (!_states.TryGetValue(id, out var state)) return;
            state.LastError = error.Message;
            if (!fault) return;
            var now = _clock.GetUtcNow();
            state.UnhealthyUntil = until ?? PauseFor(error, now) ?? now + UnhealthyFor;
        }
        RaiseChanged();
    }

    /// <summary>How long a server that hit a usage limit stays out (null: it didn't).</summary>
    internal static DateTimeOffset? PauseFor(Exception error, DateTimeOffset now) => error switch
    {
        SubagentServerPausedException paused => paused.Until is { } until && until > now ? until : now + UsageLimitPause,
        ClaudeCodeException { Failure.Kind: ClaudeCodeFailureKind.RateLimited } => now + UsageLimitPause,
        _ => null,
    };

    internal void Release(string id, bool failed, bool counted = true)
    {
        List<TaskCompletionSource> wake;
        lock (_lock)
        {
            if (_states.TryGetValue(id, out var state))
            {
                state.InFlight = Math.Max(0, state.InFlight - 1);
                if (failed) state.Failed++;
                else if (counted)
                {
                    state.Completed++;
                    state.UnhealthyUntil = null; // it just did real work
                    state.LastError = null;
                }
                if (state.InFlight == 0 && _servers.All(s => s.Id != id))
                {
                    _states.Remove(id);
                    _homes.Remove(id);
                }
            }
            wake = TakeWaitersLocked();
        }
        Wake(wake);
        RaiseChanged();
    }

    // MARK: Choosing

    /// <summary>The servers one request may use: the configured ones (only those in the chat's pool, when it has one), with the
    /// asking chat's own server as the primary.</summary>
    private List<FleetServer> ServersLocked(FleetServer? home, IReadOnlySet<string>? pool)
    {
        var allowed = pool is null ? _servers : _servers.Where(s => pool.Contains(s.Id)).ToList();
        if (home is null) return [.. allowed];
        var asPrimary = home.IsPrimary && home.IsHome ? home : new FleetServer
        {
            Id = home.Id, Label = home.Label, IsPrimary = true, IsPrivate = home.IsPrivate, InteractiveOnly = home.InteractiveOnly,
            MaxParallel = home.MaxParallel, Resolve = home.Resolve, Handle = home.Handle ?? SubagentPool.PrimaryId, Machine = home.Machine,
            Kind = home.Kind, IsHome = true,
        };
        return [.. allowed.Where(s => !s.IsPrimary && s.Id != asPrimary.Id), asPrimary];
    }

    private State StateLocked(string id)
    {
        if (!_states.TryGetValue(id, out var state)) _states[id] = state = new State();
        return state;
    }

    private static bool IsHealthyLocked(State state, DateTimeOffset now) => state.UnhealthyUntil is not { } until || until <= now;

    private List<TaskCompletionSource> TakeWaitersLocked()
    {
        var all = _waiters.ToList();
        _waiters.Clear();
        return all;
    }

    private static void Wake(List<TaskCompletionSource> waiters)
    {
        foreach (var waiter in waiters) waiter.TrySetResult();
    }
}
