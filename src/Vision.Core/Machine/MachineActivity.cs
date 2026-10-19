namespace Vision.Core;

// MARK: - Which chats are using the machine
//
// A chat's machine session starts with its first machine tool (a screenshot, a click, a keystroke)
// and lasts the whole run: between two clicks the model is thinking, and a glow that blinks off and
// on with every tool call is worse than one that stays. The session ends when the run ends (after a
// short linger, as on the Mac), or once no machine tool has run for a while — a run that took one
// screenshot and then spends half an hour editing code isn't using the machine.

/// <summary>One chat's machine session, as the glow shows it.</summary>
public sealed record MachineSessionInfo(string ChatId, string Title, DateTimeOffset Started, DateTimeOffset LastUsed, bool ToolRunning);

/// <summary>The chats using the machine right now. Thread-safe; the clock decides when a session lapses,
/// so the app looks again on a timer (<see cref="Sessions"/>) while any is active.</summary>
public sealed class MachineActivity(TimeProvider? time = null)
{
    /// <summary>A session ends this long after its run does (the Mac's glow lingers the same 2.5 s).</summary>
    public static readonly TimeSpan Linger = TimeSpan.FromSeconds(2.5);
    /// <summary>A session ends after this long without any machine tool, even while the run goes on.</summary>
    public static readonly TimeSpan IdleEnd = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    private sealed class Session(string chatId, string title, DateTimeOffset started)
    {
        public string ChatId { get; } = chatId;
        public string Title { get; set; } = title;
        public DateTimeOffset Started { get; set; } = started;
        public DateTimeOffset LastUsed { get; set; } = started;
        public int Running { get; set; }
        public DateTimeOffset? Ended { get; set; }
    }

    /// <summary>The first session began while none was active: the app shows the glow.</summary>
    public event Action? Started;

    /// <summary>A machine tool of <paramref name="chatId"/> starts; dispose when it finishes.</summary>
    public IDisposable Begin(string chatId, string title)
    {
        bool first;
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            PruneLocked(now);
            first = _sessions.Count == 0;
            if (!_sessions.TryGetValue(chatId, out var session)) _sessions[chatId] = session = new Session(chatId, title, now);
            session.Title = title;
            session.Ended = null;
            session.Running++;
            session.LastUsed = now;
        }
        if (first) Started?.Invoke();
        return new Use(this, chatId);
    }

    private void Finish(string chatId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(chatId, out var session)) return;
            session.Running = Math.Max(0, session.Running - 1);
            session.LastUsed = _time.GetUtcNow();
        }
    }

    /// <summary>The chat's run is over (finished, failed or stopped): its session ends after the linger.</summary>
    public void EndRun(string chatId)
    {
        lock (_lock)
        {
            if (_sessions.TryGetValue(chatId, out var session)) session.Ended = _time.GetUtcNow();
        }
    }

    /// <summary>End every session at once (the stop key): nothing lingers.</summary>
    public void Clear()
    {
        lock (_lock) _sessions.Clear();
    }

    /// <summary>The active sessions, oldest first. Lapsed ones are dropped as a side effect.</summary>
    public IReadOnlyList<MachineSessionInfo> Sessions()
    {
        lock (_lock)
        {
            PruneLocked(_time.GetUtcNow());
            return _sessions.Values.OrderBy(s => s.Started).ThenBy(s => s.ChatId, StringComparer.Ordinal)
                .Select(s => new MachineSessionInfo(s.ChatId, s.Title, s.Started, s.LastUsed, s.Running > 0)).ToList();
        }
    }

    /// <summary>Whether <paramref name="chatId"/> is using the machine now.</summary>
    public bool IsActive(string chatId) => Sessions().Any(s => s.ChatId == chatId);

    private void PruneLocked(DateTimeOffset now)
    {
        foreach (var session in _sessions.Values.ToList())
        {
            if (session.Running > 0) continue;
            var ended = session.Ended is { } at && now - at >= Linger;
            if (ended || now - session.LastUsed >= IdleEnd) _sessions.Remove(session.ChatId);
        }
    }

    private sealed class Use(MachineActivity owner, string chatId) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) owner.Finish(chatId);
        }
    }
}

// MARK: - Wording

/// <summary>The glow's label: a headline, a line naming which chat does what, and the stop key.</summary>
public sealed record GlowText(string Headline, string Detail, string Hint)
{
    /// <summary>All three on one line ("A · B · C"), for logs and tooltips.</summary>
    public override string ToString() => $"{Headline} · {Detail} · {Hint}";
}

/// <summary>Every sentence the machine-in-use glow and the input arbiter show, in one place so the Mac
/// and Windows apps can say the same thing.</summary>
public static class MachineText
{
    public const string Headline = "Machine in use by Vision AI";
    public const string StopHint = "Ctrl+Alt+. stops";
    /// <summary>The hint when another app holds Ctrl+Alt+. (the stop key then only works inside Vision).</summary>
    public const string StopHintInApp = "Ctrl+. in Vision stops";
    /// <summary>Longest chat title the glow shows before an ellipsis.</summary>
    public const int TitleLimit = 40;

    /// <summary>The glow's label for these sessions and this state of the real mouse; null when no chat
    /// is using the machine (no glow).</summary>
    public static GlowText? Glow(IReadOnlyList<MachineSessionInfo> sessions, ArbiterSnapshot input)
    {
        if (sessions.Count == 0 && input.Holder is null && input.Waiting.Count == 0) return null;
        string detail;
        if (input.PersonActive && (input.Holder is not null || input.Waiting.Count > 0))
        {
            detail = "Paused while you use the mouse or keyboard — Vision carries on a moment after you stop";
        }
        else if (input.Holder is not null)
        {
            detail = $"{Quote(input.HolderTitle)} has the mouse";
            if (input.Waiting.Count > 0) detail += $" · {input.Waiting.Count} waiting";
        }
        else if (input.Waiting.Count > 0)
        {
            detail = $"{Quote(input.Waiting[0].Title)} is waiting for the mouse";
        }
        else if (sessions.Count == 1)
        {
            detail = $"{Quote(sessions[0].Title)} is working in the background — your mouse is free";
        }
        else
        {
            var more = sessions.Count - 1;
            detail = $"{Quote(sessions[0].Title)} and {more} more chat{(more == 1 ? "" : "s")} are working in the background — your mouse is free";
        }
        return new GlowText(Headline, detail, StopHint);
    }

    /// <summary>What a chat shows while it waits for the real mouse; null once it has it.</summary>
    public static string? Waiting(WaitReason reason, string? blocker) => reason switch
    {
        WaitReason.Person => "Waiting for you to finish with the mouse and keyboard…",
        WaitReason.OtherChat => $"Waiting for the mouse — {Quote(blocker)} is using it…",
        WaitReason.OwnAction => "Waiting for the mouse — another step of this chat is using it…",
        _ => null,
    };

    /// <summary>The tool error after waiting for the person too long.</summary>
    public static string PersonTimeout(TimeSpan waited) =>
        $"Error: waited {Duration(waited)} for the person to stop using the mouse and keyboard, and they are still at it. " +
        "Try the action in the background (mode: \"background\"), or ask them.";

    /// <summary>The tool error after waiting for other chats too long.</summary>
    public static string ChatTimeout(TimeSpan waited, string? blocker) =>
        $"Error: waited {Duration(waited)} for the mouse, and {Quote(blocker)} is still using it. " +
        "Try the action in the background (mode: \"background\"), or try again later.";

    /// <summary>A chat title the way the glow quotes it: curly quotes, clipped.</summary>
    public static string Quote(string? title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "a chat" : string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length > TitleLimit) text = text[..(TitleLimit - 1)].TrimEnd() + "…";
        return $"“{text}”";
    }

    private static string Duration(TimeSpan span) =>
        span.TotalMinutes >= 1 && span.TotalSeconds % 60 == 0
            ? $"{(int)span.TotalMinutes} minute{((int)span.TotalMinutes == 1 ? "" : "s")}"
            : $"{(int)Math.Round(span.TotalSeconds)} s";
}
