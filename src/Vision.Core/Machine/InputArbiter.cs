namespace Vision.Core;

// MARK: - One real mouse, many chats
//
// Background input (UI Automation patterns, window messages) never touches the person's mouse or
// keyboard, so any number of chats can do it at once. The real mouse and keyboard (SendInput) are
// one device: when a chat has to use them, it asks the arbiter, which hands them to one chat at a
// time. The holder keeps them for a short linger after each action, so a click-type-click sequence
// isn't interleaved with another chat's clicks; with others waiting, a holder that has had them for
// its fair share gives them up at its next release. Nobody gets them while the person is using the
// mouse or keyboard: they wait until the person has been still for a moment.

/// <summary>How long the arbiter's holds and waits last. Tests shorten them.</summary>
public sealed record ArbiterTimings
{
    /// <summary>After an action, the holder keeps the mouse this long for its next one.</summary>
    public TimeSpan Linger { get; init; } = TimeSpan.FromSeconds(2.5);
    /// <summary>With other chats waiting, a holder gives the mouse up at its next release once it has
    /// had it this long.</summary>
    public TimeSpan FairShare { get; init; } = TimeSpan.FromSeconds(20);
    /// <summary>The person counts as using the mouse or keyboard until they have been still this long.</summary>
    public TimeSpan PersonQuiet { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>How often a waiting request looks again.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>The longest a request waits for other chats before giving up.</summary>
    public TimeSpan MaxWaitForChats { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>The longest a request waits for the person to stop before giving up.</summary>
    public TimeSpan MaxWaitForPerson { get; init; } = TimeSpan.FromMinutes(2);

    public static ArbiterTimings Standard { get; } = new();
}

/// <summary>Why a request for the real mouse has to wait.</summary>
public enum WaitReason
{
    None,
    /// <summary>The person is using the mouse or keyboard.</summary>
    Person,
    /// <summary>Another chat has the mouse, or is ahead in the line.</summary>
    OtherChat,
    /// <summary>Another action of this same chat (one of its subagents) has the mouse right now.</summary>
    OwnAction,
}

/// <summary>A chat in the line for the real mouse.</summary>
public sealed record WaitingChat(string ChatId, string Title);

/// <summary>Who has the real mouse and who waits for it, for the glow and the chats.</summary>
public sealed record ArbiterSnapshot(string? Holder, string? HolderTitle, bool HolderBusy, IReadOnlyList<WaitingChat> Waiting,
                                     bool PersonActive)
{
    public static ArbiterSnapshot Idle { get; } = new(null, null, false, [], false);
}

/// <summary>A request gave up waiting for the real mouse; the message says why, for the model.</summary>
public sealed class InputWaitException(string message) : Exception(message);

/// <summary>Hands the real mouse and keyboard to one chat at a time (see the file comment). Thread-safe.</summary>
public sealed class InputArbiter
{
    private readonly Lock _lock = new();
    private readonly TimeProvider _time;
    private readonly Func<DateTimeOffset?> _lastPersonInput;
    private readonly List<Request> _queue = [];
    private long _nextRequest;
    private long _lease;

    private string? _holder;
    private string? _holderTitle;
    private DateTimeOffset _holderSince;
    private bool _busy;
    private DateTimeOffset _releasedAt;

    private sealed record Request(long Id, string ChatId, string Title);

    /// <param name="lastPersonInput">When the person last moved the mouse or pressed a key (not counting
    /// injected input); null when unknown or never.</param>
    public InputArbiter(ArbiterTimings? timings = null, TimeProvider? time = null, Func<DateTimeOffset?>? lastPersonInput = null)
    {
        Timings = timings ?? ArbiterTimings.Standard;
        _time = time ?? TimeProvider.System;
        _lastPersonInput = lastPersonInput ?? (static () => null);
    }

    public ArbiterTimings Timings { get; }

    /// <summary>The person moved the mouse or typed within <see cref="ArbiterTimings.PersonQuiet"/>.</summary>
    public bool PersonActive => PersonActiveAt(_time.GetUtcNow());

    private bool PersonActiveAt(DateTimeOffset now) =>
        _lastPersonInput() is { } last && now - last < Timings.PersonQuiet;

    // MARK: Decisions (each one is a single look at the state, so tests can step through them)

    /// <summary>Join the line for the real mouse; returns the request's id.</summary>
    public long Enqueue(string chatId, string title)
    {
        lock (_lock)
        {
            var request = new Request(++_nextRequest, chatId, title);
            _queue.Add(request);
            return request.Id;
        }
    }

    /// <summary>Whether request <paramref name="requestId"/> may have the mouse now. On a grant the
    /// request leaves the line and <paramref name="lease"/> is the token to release; otherwise the
    /// reason and the chat in the way.</summary>
    public bool TryGrant(long requestId, out long lease, out WaitReason reason, out string? blocker)
    {
        lease = 0;
        blocker = null;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            ExpireLocked(now);
            var index = _queue.FindIndex(r => r.Id == requestId);
            if (index < 0) throw new InvalidOperationException("That request is not waiting for the mouse.");
            var request = _queue[index];
            if (PersonActiveAt(now))
            {
                reason = WaitReason.Person;
                return false;
            }
            if (_holder == request.ChatId)
            {
                if (_busy)
                {
                    reason = WaitReason.OwnAction;
                    blocker = _holderTitle;
                    return false;
                }
                // Its fair share is up and others wait: give the mouse up and join the back of the line.
                if (now - _holderSince >= Timings.FairShare && _queue.Any(r => r.ChatId != request.ChatId))
                {
                    _holder = null;
                    _holderTitle = null;
                    _queue.RemoveAt(index);
                    _queue.Add(request);
                }
                else
                {
                    _queue.RemoveAt(index);
                    _holderTitle = request.Title;
                    _busy = true;
                    lease = ++_lease;
                    reason = WaitReason.None;
                    return true;
                }
            }
            if (_holder is not null)
            {
                reason = WaitReason.OtherChat;
                blocker = _holderTitle;
                return false;
            }
            // Nobody has it: first come, first served.
            var first = _queue[0];
            if (first.Id != requestId)
            {
                reason = first.ChatId == request.ChatId ? WaitReason.OwnAction : WaitReason.OtherChat;
                blocker = first.Title;
                return false;
            }
            _queue.RemoveAt(0);
            _holder = request.ChatId;
            _holderTitle = request.Title;
            _holderSince = now;
            _busy = true;
            lease = ++_lease;
            reason = WaitReason.None;
            return true;
        }
    }

    /// <summary>The action holding <paramref name="lease"/> is done. The chat keeps the mouse for the
    /// linger, unless its fair share is up and others wait.</summary>
    public void Release(long lease)
    {
        lock (_lock)
        {
            if (lease != _lease || !_busy) return; // released already, or a stale token
            var now = _time.GetUtcNow();
            _busy = false;
            _releasedAt = now;
            if (_holder is { } holder && now - _holderSince >= Timings.FairShare && _queue.Any(r => r.ChatId != holder))
            {
                _holder = null;
                _holderTitle = null;
            }
        }
    }

    /// <summary>Leave the line without the mouse (the wait was cancelled or gave up).</summary>
    public void Cancel(long requestId)
    {
        lock (_lock) _queue.RemoveAll(r => r.Id == requestId);
    }

    /// <summary>Who has the mouse and who waits, as of now.</summary>
    public ArbiterSnapshot Snapshot()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            ExpireLocked(now);
            var waiting = new List<WaitingChat>();
            foreach (var request in _queue)
            {
                if (request.ChatId != _holder && waiting.All(w => w.ChatId != request.ChatId)) waiting.Add(new WaitingChat(request.ChatId, request.Title));
            }
            return new ArbiterSnapshot(_holder, _holderTitle, _busy, waiting, PersonActiveAt(now));
        }
    }

    /// <summary>A holder that has been idle past the linger no longer has the mouse.</summary>
    private void ExpireLocked(DateTimeOffset now)
    {
        if (_holder is not null && !_busy && now - _releasedAt >= Timings.Linger)
        {
            _holder = null;
            _holderTitle = null;
        }
    }

    // MARK: Waiting

    /// <summary>Wait for the real mouse and keyboard. <paramref name="waiting"/> hears each change of
    /// reason (and <see cref="WaitReason.None"/> once granted after a wait). Throws
    /// <see cref="InputWaitException"/> when the wait runs past its limit.</summary>
    public async Task<InputLease> AcquireAsync(string chatId, string title, Action<WaitReason, string?>? waiting,
                                               CancellationToken cancellationToken)
    {
        var id = Enqueue(chatId, title);
        var granted = false;
        var reported = WaitReason.None;
        string? reportedBlocker = null;
        var started = _time.GetUtcNow();
        DateTimeOffset? personSince = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryGrant(id, out var lease, out var reason, out var blocker))
                {
                    granted = true;
                    if (reported != WaitReason.None) waiting?.Invoke(WaitReason.None, null);
                    return new InputLease(this, lease);
                }
                if (reason != reported || blocker != reportedBlocker)
                {
                    reported = reason;
                    reportedBlocker = blocker;
                    waiting?.Invoke(reason, blocker);
                }
                var now = _time.GetUtcNow();
                if (reason == WaitReason.Person)
                {
                    personSince ??= now;
                    if (now - personSince.Value >= Timings.MaxWaitForPerson)
                        throw new InputWaitException(MachineText.PersonTimeout(Timings.MaxWaitForPerson));
                }
                else
                {
                    personSince = null;
                    if (now - started >= Timings.MaxWaitForChats)
                        throw new InputWaitException(MachineText.ChatTimeout(Timings.MaxWaitForChats, blocker));
                }
                await Task.Delay(Timings.Poll, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!granted)
            {
                Cancel(id);
                if (reported != WaitReason.None) waiting?.Invoke(WaitReason.None, null);
            }
        }
    }

    /// <summary>Mid-action: wait until the person has stopped using the mouse and keyboard. False when
    /// they kept at it past <see cref="ArbiterTimings.MaxWaitForPerson"/>.</summary>
    public async Task<bool> WaitForPersonAsync(Action<WaitReason, string?>? waiting, CancellationToken cancellationToken)
    {
        if (!PersonActive) return true;
        waiting?.Invoke(WaitReason.Person, null);
        var started = _time.GetUtcNow();
        try
        {
            while (PersonActive)
            {
                if (_time.GetUtcNow() - started >= Timings.MaxWaitForPerson) return false;
                await Task.Delay(Timings.Poll, _time, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            waiting?.Invoke(WaitReason.None, null);
        }
    }
}

/// <summary>The real mouse and keyboard, held by one action; dispose to release (the chat keeps them
/// for the linger).</summary>
public sealed class InputLease : IDisposable
{
    private readonly InputArbiter _arbiter;
    private readonly long _token;
    private int _released;

    internal InputLease(InputArbiter arbiter, long token)
    {
        _arbiter = arbiter;
        _token = token;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _arbiter.Release(_token);
    }
}

/// <summary>When the hooks that watch for the person are installed fresh (each machine session), they have seen nothing
/// yet: the person may be mid-typing. Windows' own last-input time fills that gap, when it is recent enough to matter.
/// (Vision's own SendInput only happens once the hooks run, and a session's hooks come down 2.5 s after its run ends —
/// longer than the person-quiet time — so a recent tick is the person, or another program.)</summary>
public static class PersonSeed
{
    /// <summary>How long ago the last input was, from GetLastInputInfo's tick and GetTickCount's now (both 32-bit
    /// milliseconds that wrap every 49.7 days); null when it is older than <paramref name="window"/>.</summary>
    public static TimeSpan? Age(uint lastInputTick, uint nowTick, TimeSpan window)
    {
        var age = TimeSpan.FromMilliseconds(unchecked(nowTick - lastInputTick));
        return age < window ? age : null;
    }

    /// <summary>The person's last input as the arbiter reads it, or null when nothing recent happened.</summary>
    public static DateTimeOffset? LastInput(uint lastInputTick, uint nowTick, DateTimeOffset now, TimeSpan window) =>
        Age(lastInputTick, nowTick, window) is { } age ? now - age : null;
}
