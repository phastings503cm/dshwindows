namespace Vision.Core;

// MARK: - The processes each chat started
//
// Every process a chat starts — a run_shell_command, a process_start, and everything those start in
// turn — descends from one the chat launched itself. The ledger records those roots per chat; with
// "keep each chat to the apps it started" on, a chat's clicks and keystrokes may only go to windows
// of processes that descend from its roots. The chat is the run's async flow (an AsyncLocal the host
// sets around each engine run), so subagents and background agents count as their chat, and the
// shell and process tools need no idea of chats at all.

/// <summary>A process a chat started, and when.</summary>
public sealed record SpawnedProcess(int ProcessId, DateTimeOffset Started);

/// <summary>One row of a process snapshot: who started it, and when (null when it can't be read).</summary>
public sealed record ProcessEntry(int ProcessId, int ParentId, DateTimeOffset? Started);

public static class ChatProcesses
{
    /// <summary>Most roots kept per chat; the oldest go first.</summary>
    public const int Limit = 512;

    private static readonly AsyncLocal<string?> CurrentChat = new();
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, List<SpawnedProcess>> Roots = new(StringComparer.Ordinal);

    /// <summary>The chat whose run this code is part of (null outside a run). Set it at the start of a
    /// run's async flow; it flows into every task the run starts.</summary>
    public static string? Current
    {
        get => CurrentChat.Value;
        set => CurrentChat.Value = value;
    }

    /// <summary>A process was just started on behalf of the current chat (no-op outside a run).</summary>
    public static void Record(int processId)
    {
        if (Current is { Length: > 0 } chat && processId > 0) Record(chat, processId, DateTimeOffset.UtcNow);
    }

    public static void Record(string chatId, int processId, DateTimeOffset started)
    {
        lock (Gate)
        {
            if (!Roots.TryGetValue(chatId, out var list)) Roots[chatId] = list = [];
            list.RemoveAll(p => p.ProcessId == processId);
            list.Add(new SpawnedProcess(processId, started));
            if (list.Count > Limit) list.RemoveRange(0, list.Count - Limit);
        }
    }

    /// <summary>The processes <paramref name="chatId"/> started, oldest first.</summary>
    public static IReadOnlyList<SpawnedProcess> For(string chatId)
    {
        lock (Gate) return Roots.TryGetValue(chatId, out var list) ? [.. list] : [];
    }

    /// <summary>The chat was deleted.</summary>
    public static void Forget(string chatId)
    {
        lock (Gate) Roots.Remove(chatId);
    }
}

/// <summary>Whether a process descends from one a chat started.</summary>
public static class ProcessTree
{
    /// <summary>A process id can be reused once its process is gone; a parent must have started before
    /// its child (allowing for clock reads taken a moment apart).</summary>
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    /// <summary>True when <paramref name="processId"/> is one of <paramref name="roots"/> or descends from
    /// one, walking parent links through <paramref name="lookup"/> (null = that process is gone).</summary>
    public static bool Descends(int processId, IReadOnlyCollection<SpawnedProcess> roots, Func<int, ProcessEntry?> lookup)
    {
        if (roots.Count == 0 || processId <= 0) return false;
        var byId = new Dictionary<int, SpawnedProcess>();
        foreach (var root in roots) byId[root.ProcessId] = root;
        var current = lookup(processId);
        if (current is null) return false;
        var seen = new HashSet<int>();
        for (var depth = 0; depth < 64 && current is not null && seen.Add(current.ProcessId); depth++)
        {
            if (byId.TryGetValue(current.ProcessId, out var root) && SameProcess(root, current.Started)) return true;
            var parentId = current.ParentId;
            if (parentId <= 0 || parentId == current.ProcessId) return false;
            var parent = lookup(parentId);
            if (parent is null)
            {
                // The parent is gone (a shell that launched a GUI app and exited): it still counts when
                // it was a root that started before this process did.
                return byId.TryGetValue(parentId, out var gone) && StartedBefore(gone.Started, current.Started);
            }
            // A "parent" that started after its child is a reused id, not the real parent.
            if (parent.Started is { } ps && current.Started is { } cs && ps > cs + Slack) return false;
            current = parent;
        }
        return false;
    }

    private static bool SameProcess(SpawnedProcess root, DateTimeOffset? started) =>
        started is not { } at || Math.Abs((at - root.Started).TotalSeconds) <= Slack.TotalSeconds;

    private static bool StartedBefore(DateTimeOffset root, DateTimeOffset? child) => child is not { } at || root <= at + Slack;
}

/// <summary>The agent inside a chat whose run this code is part of: a subagent's or background agent's own id, null for the
/// chat's main agent. Like <see cref="ChatProcesses.Current"/>, an AsyncLocal set where each agent's run starts (in
/// AgentTool), so parallel subagents of one chat each keep their own machine state (the field they clicked, the window they
/// look at) and never type into the field another one picked.</summary>
public static class AgentScope
{
    private static readonly AsyncLocal<string?> CurrentAgent = new();

    public static string? Current
    {
        get => CurrentAgent.Value;
        set => CurrentAgent.Value = value;
    }
}
