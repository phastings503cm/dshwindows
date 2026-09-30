using System.Net.Sockets;

namespace Dsh.Core;

// MARK: - The fleet
//
// With more than one model server (say two DGX Sparks), one is the *primary*: the main agent talks to
// it. The others are *workers*: subagents run on them, so a fan-out of tasks spreads across machines
// instead of queueing on one. The fleet hands out leases on servers — the least loaded worker first, the
// primary only as overflow — and steers away from a server that has just failed. With a single server
// the fleet is just a concurrency limit on it.

/// <summary>The live client, model and window for a server, resolved when a lease is granted (a Spark may
/// have swapped models since it was configured).</summary>
/// <summary>Where one subagent runs: the server's client and model, its context window, and — when they differ
/// from the parent's — the settings of that server's route (null = inherit the parent's).</summary>
public sealed record FleetTarget(ILlmClient Client, string Model, int? ContextWindow = null)
{
    public bool? Vision { get; init; }
    public double? Temperature { get; init; }
    public int? MaxOutputTokens { get; init; }
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
    /// <summary>How many subagents may work on it at once.</summary>
    public int MaxParallel { get; init; } = 2;
    /// <summary>Probe the server and return its client. Throws if it can't be reached.</summary>
    public required Func<CancellationToken, Task<FleetTarget>> Resolve { get; init; }
}

public sealed record FleetStatus(string Id, string Label, bool IsPrimary, int InFlight, int MaxParallel,
                                 bool Healthy, DateTimeOffset? UnhealthyUntil, int Completed, int Failed);

/// <summary>No configured server answered.</summary>
public sealed class FleetUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A slot on one server. Dispose it when the work is over; call <see cref="Fail"/> first if the
/// server itself was the problem, so the next task goes elsewhere.</summary>
public sealed class FleetLease : IDisposable
{
    private readonly AgentFleet _fleet;
    private int _released;
    private bool _failed;
    private bool _abandoned;

    internal FleetLease(AgentFleet fleet, FleetServer server, FleetTarget target)
    {
        _fleet = fleet;
        Server = server;
        Target = target;
    }

    public FleetServer Server { get; }
    public FleetTarget Target { get; }
    public string Label => Server.Label;

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
    }

    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private List<FleetServer> _servers = [];
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly List<TaskCompletionSource> _waiters = [];

    public AgentFleet(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>A server that just failed is left alone this long (unless every server is down).</summary>
    public TimeSpan UnhealthyFor { get; set; } = TimeSpan.FromSeconds(60);

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
                if (_states[gone].InFlight == 0) _states.Remove(gone);
            }
            wake = TakeWaitersLocked();
        }
        Wake(wake);
        RaiseChanged();
    }

    public IReadOnlyList<FleetStatus> Snapshot()
    {
        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            return _servers.Select(s =>
            {
                var state = _states.GetValueOrDefault(s.Id) ?? new State();
                return new FleetStatus(s.Id, s.Label, s.IsPrimary, state.InFlight, s.MaxParallel, IsHealthyLocked(state, now),
                    state.UnhealthyUntil > now ? state.UnhealthyUntil : null, state.Completed, state.Failed);
            }).ToList();
        }
    }

    /// <summary>Wait for a free slot on a suitable server and lease it. <paramref name="preferred"/> names a
    /// server the caller would like (matched against the label and id); it is a preference, not a demand.
    /// Throws <see cref="FleetUnavailableException"/> when no server can be reached.</summary>
    public async Task<FleetLease> AcquireAsync(string? preferred, CancellationToken cancellationToken)
    {
        var tried = new HashSet<string>(StringComparer.Ordinal);
        Exception? lastError = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FleetServer? chosen;
            TaskCompletionSource? waiter = null;
            TimeSpan? recovery = null;
            lock (_lock)
            {
                if (_servers.Count == 0) throw new FleetUnavailableException("No model server is configured.");
                if (_servers.All(s => tried.Contains(s.Id)))
                    throw new FleetUnavailableException("No model server answered" + (lastError is null ? "." : $": {lastError.Message}"), lastError);
                chosen = ChooseLocked(preferred, tried);
                if (chosen is not null)
                {
                    _states[chosen.Id].InFlight++;
                }
                else
                {
                    waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters.Add(waiter);
                    recovery = NextRecoveryLocked(tried);
                }
            }
            if (chosen is null)
            {
                try
                {
                    // A blacklisted server comes back by itself when its time is up, with nothing else to wake us.
                    if (recovery is { } after) await waiter!.Task.WaitAsync(after, cancellationToken).ConfigureAwait(false);
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
                return new FleetLease(this, chosen, target);
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
                tried.Add(chosen.Id);
                MarkFailed(chosen.Id, error);
                Release(chosen.Id, failed: true);
            }
        }
    }

    /// <summary>Whether <paramref name="error"/> is the server's fault rather than the request's — the kind
    /// of failure another server might not have.</summary>
    public static bool IsServerFault(Exception error)
    {
        if (error is OperationCanceledException) return false;
        // Credentials that one server rejects may be fine on another (a worker with a stale key, a Bedrock sign-in that expired).
        if (error is LlmException { Kind: LlmErrorKind.Http, StatusCode: 401 or 403 }) return true;
        // Whatever the retry policy would wait out — unreachable, overloaded, restarting, swapping models, "still loading" — is
        // that server's problem, and another server may not have it. What it would give up on at once is the request's.
        return RequestRetry.Disposition(error) is not RetryDisposition.Fail;
    }

    internal void MarkFailed(string id, Exception error)
    {
        if (!IsServerFault(error) && error is not FleetUnavailableException) return;
        lock (_lock)
        {
            if (_states.TryGetValue(id, out var state)) state.UnhealthyUntil = _clock.GetUtcNow() + UnhealthyFor;
        }
        RaiseChanged();
    }

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
                }
                if (state.InFlight == 0 && _servers.All(s => s.Id != id)) _states.Remove(id);
            }
            wake = TakeWaitersLocked();
        }
        Wake(wake);
        RaiseChanged();
    }

    // MARK: Choosing

    private FleetServer? ChooseLocked(string? preferred, HashSet<string> tried)
    {
        var now = _clock.GetUtcNow();
        var candidates = _servers.Where(s => !tried.Contains(s.Id)).ToList();
        if (candidates.Count == 0) return null;
        // Skip servers that just failed — unless that would leave none.
        var healthy = candidates.Where(s => IsHealthyLocked(_states[s.Id], now)).ToList();
        if (healthy.Count == 0) healthy = candidates;

        FleetServer? PickFrom(IEnumerable<FleetServer> pool) =>
            pool.Where(HasCapacityLocked)
                .OrderBy(s => _states[s.Id].InFlight / (double)Math.Max(1, s.MaxParallel))
                .ThenBy(s => _servers.IndexOf(s))
                .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var wanted = preferred.Trim();
            var matches = healthy.Where(s => s.Label.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                                             || s.Id.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (PickFrom(matches) is { } favourite) return favourite;
        }
        var workers = healthy.Where(s => !s.IsPrimary).ToList();
        if (PickFrom(workers) is { } worker) return worker;
        var primaries = healthy.Where(s => s.IsPrimary).ToList();
        // No workers at all, or the primary may take overflow: use it. Otherwise wait for a worker.
        if (workers.Count == 0 || UsePrimaryWhenWorkersBusy) return PickFrom(primaries);
        return null;
    }

    /// <summary>How long until the first blacklisted server (one not yet tried) is trusted again; null if none is waiting out a ban.</summary>
    private TimeSpan? NextRecoveryLocked(HashSet<string> tried)
    {
        var now = _clock.GetUtcNow();
        TimeSpan? soonest = null;
        foreach (var server in _servers)
        {
            if (tried.Contains(server.Id) || !_states.TryGetValue(server.Id, out var state)) continue;
            if (state.UnhealthyUntil is not { } until || until <= now) continue;
            var left = until - now + TimeSpan.FromMilliseconds(50);
            if (soonest is null || left < soonest) soonest = left;
        }
        return soonest;
    }

    private bool HasCapacityLocked(FleetServer server) => _states[server.Id].InFlight < Math.Max(1, server.MaxParallel);

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
