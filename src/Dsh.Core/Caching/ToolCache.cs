using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Core;

// MARK: - Tool result cache
//
// A long session asks the same questions again and again: the model re-reads a file it read a few steps
// ago, re-greps for a symbol, re-fetches a page, or a fan-out sends three subagents to look at the same
// thing. Doing that work twice costs time, and sending the same 2,000 lines back into the context twice
// costs tokens that push the conversation toward compaction sooner.
//
// The cache is deliberately conservative — a stale answer is worse than a slow one:
//  * A file read is answered with a short "unchanged" note only while the earlier copy is still recent in
//    the conversation, the file's timestamp and size are exactly as they were, and nothing has been
//    compacted away since. Ask again straight after and you get the full text.
//  * Searches (glob, grep, list_directory) are reused only until anything that could change the
//    workspace runs (a write, a shell command, a subagent...), for a minute at most, and only once in a
//    row: asked a third time, the search runs again.
//  * A web page is reused for ten minutes (never one on this PC or the local network: a dev server's page
//    changes with the code); an identical read-only subagent task for fifteen, and only while the
//    workspace hasn't changed. "Read-only" is judged from what the agent type may actually do.
//  * Anything that may have changed the files behind the engine's back — a new user turn, a background
//    agent's report, a running process being read — counts as a change.
//  * Errors are never cached, and neither is anything that mentions a vault placeholder.

/// <summary>What the cache has saved so far.</summary>
public sealed record ToolCacheStats(int Hits, int Misses, int Reads, long CharsSaved)
{
    /// <summary>"12 repeats answered from cache, about 9K tokens saved" — empty when it hasn't helped yet.</summary>
    public string Describe() => Hits + Reads == 0
        ? ""
        : $"{Hits + Reads} repeated lookup{(Hits + Reads == 1 ? "" : "s")} answered from cache (about {Fmt.N(CharsSaved / 4)} tokens saved)";
}

public sealed class ToolCache
{
    /// <summary>Tools that never change the workspace, so running one doesn't make cached lookups stale. Reading a
    /// process or a background agent is not among them: what they are doing to the files goes on unseen, so each
    /// look counts as a possible change.</summary>
    private static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "read_file", "read_many_files", "glob", "grep", "list_directory", "web_fetch", "todo_write", "exit_plan_mode",
        "use_skill", "vault_search", "memory_search", "memory_save", "memory_forget", "goal_complete",
        "goal_blocked", "queue_task", "propose_skill",
    };

    /// <summary>Whether a tool never changes the workspace.</summary>
    internal static bool IsReadOnlyTool(string tool) => ReadOnly.Contains(tool);

    /// <summary>Whether a subagent type can only read, as this chat defines the type (a project may replace "explore"
    /// with a file that has every tool). Null = the built-in definitions.</summary>
    public Func<string?, bool>? AgentIsReadOnly { get; set; }

    private sealed class Entry(string tool, ToolResult result, long epoch, DateTimeOffset at)
    {
        public string Tool { get; } = tool;
        public ToolResult Result { get; } = result;
        public long Epoch { get; } = epoch;
        public DateTimeOffset At { get; } = at;
        public DateTimeOffset LastUsed { get; set; } = at;
        /// <summary>The last time this was asked, the cache answered: asking a third time gets a fresh run.</summary>
        public bool ServedLast { get; set; }
        public int Size => Result.Output.Length;
    }

    private sealed class ReadEntry(long length, DateTime modifiedUtc, int messageIndex, int chars)
    {
        public long Length { get; } = length;
        public DateTime ModifiedUtc { get; } = modifiedUtc;
        public int MessageIndex { get; set; } = messageIndex;
        public int Chars { get; } = chars;
        public bool StubbedLast { get; set; }
    }

    private readonly Lock _lock = new();
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReadEntry> _reads = new(StringComparer.Ordinal);
    private long _epoch;
    private long _totalChars;
    private int _hits;
    private int _misses;
    private int _reads_;
    private long _saved;

    public ToolCache(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    /// <summary>How many messages back an earlier read still counts as "in the conversation".</summary>
    public int RecentWindow { get; init; } = 40;
    public TimeSpan SearchTtl { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan WebTtl { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan AgentTtl { get; init; } = TimeSpan.FromMinutes(15);
    public int MaxEntries { get; init; } = 300;
    public long MaxChars { get; init; } = 6_000_000;

    public ToolCacheStats Stats
    {
        get
        {
            lock (_lock) return new ToolCacheStats(_hits, _misses, _reads_, _saved);
        }
    }

    /// <summary>Whether a read_file answer is the short "unchanged" note rather than the file's text.</summary>
    public static bool IsUnchangedNote(string output) => output.StartsWith("[Unchanged] ", StringComparison.Ordinal);

    /// <summary>Whether the cache has an opinion about <paramref name="tool"/> at all.</summary>
    public static bool Handles(string tool) => tool is "read_file" or "glob" or "grep" or "list_directory" or "web_fetch" or "agent";

    /// <summary>An answer for this call without running it, or null to run it.</summary>
    /// <param name="resolve">Turns a path the model wrote into the absolute path it means.</param>
    /// <param name="messageIndex">Where the transcript stands now (its message count).</param>
    public ToolResult? TryGet(string tool, string arguments, string workspace, Func<string, string> resolve, int messageIndex)
    {
        if (!Handles(tool) || VaultPlaceholders.Names(arguments).Count > 0 || Uncacheable(tool, arguments)) return null;
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (tool == "read_file") return TryReadLocked(arguments, resolve, messageIndex);

            var key = KeyOf(tool, arguments, workspace);
            if (!_entries.TryGetValue(key, out var entry)) return Missed();
            var age = now - entry.At;
            var fresh = tool switch
            {
                "web_fetch" => age <= WebTtl,
                "agent" => entry.Epoch == _epoch && age <= AgentTtl,
                _ => entry.Epoch == _epoch && age <= SearchTtl,
            };
            if (!fresh)
            {
                RemoveLocked(key, entry);
                return Missed();
            }
            // A search asked a third time in a row is run again: what a background process or another program is doing to the
            // files goes on unseen, and a model that keeps asking probably expects something to have changed. (Staleness is
            // bounded to one repeat.)
            if (entry.ServedLast && tool is "glob" or "grep" or "list_directory")
            {
                RemoveLocked(key, entry);
                return Missed();
            }
            entry.ServedLast = true;
            entry.LastUsed = now;
            _hits++;
            _saved += entry.Size;
            return tool == "agent"
                ? new ToolResult($"(Reused the report of an identical subagent run from {Ago(age)} — nothing in the workspace has changed since.)\n{entry.Result.Output}")
                : entry.Result;
        }
    }

    /// <summary>Note a call that ran. Mutating tools make cached lookups stale; results worth keeping are kept.</summary>
    public void Observe(string tool, string arguments, string workspace, Func<string, string> resolve, ToolResult result, int messageIndex)
    {
        var now = _clock.GetUtcNow();
        lock (_lock)
        {
            if (!IsReadOnly(tool, arguments) || result.Files.Count > 0) _epoch++;
            if (!Handles(tool) || result.Output.StartsWith("Error:", StringComparison.Ordinal)
                || VaultPlaceholders.Names(arguments).Count > 0 || Uncacheable(tool, arguments))
                return;
            if (tool == "read_file")
            {
                RecordReadLocked(arguments, resolve, result, messageIndex);
                return;
            }
            // Only a finished report of an agent that could not have changed anything is worth reusing: a worker's
            // "3 tests failed" is stale the moment the user fixes something, and an empty or out-of-steps report says nothing.
            if (tool == "agent" && !(IsReadOnly(tool, arguments)
                                     && result.Output.StartsWith("Subagent '", StringComparison.Ordinal)
                                     && !result.Output.Contains("' failed:", StringComparison.Ordinal)
                                     && !result.Output.Contains("completed without a final report", StringComparison.Ordinal)))
                return;
            if (result.Output.Length > 400_000) return;
            var key = KeyOf(tool, arguments, workspace);
            if (_entries.TryGetValue(key, out var old)) RemoveLocked(key, old);
            // Stamped with the epoch as it stands after this call, so a read-only run stays valid.
            _entries[key] = new Entry(tool, result, _epoch, now);
            _totalChars += result.Output.Length;
            EvictLocked();
        }
    }

    /// <summary>Something outside this engine may have changed the files (the user, a background agent, another
    /// program): searches and subagent reports cached so far are no longer trusted.</summary>
    public void NoteExternalChange()
    {
        lock (_lock) _epoch++;
    }

    /// <summary>The conversation was compacted: earlier reads may be summarised away, so none may be
    /// answered with "it's above".</summary>
    public void NoteCompaction()
    {
        lock (_lock) _reads.Clear();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _reads.Clear();
            _totalChars = 0;
            _epoch++;
        }
    }

    // MARK: File reads

    private ToolResult? TryReadLocked(string arguments, Func<string, string> resolve, int messageIndex)
    {
        if (ReadKey(arguments, resolve) is not var (key, path)) return Missed();
        if (!_reads.TryGetValue(key, out var entry)) return Missed();
        if (Stamp(path) is not var (length, modified) || length != entry.Length || modified != entry.ModifiedUtc)
        {
            _reads.Remove(key);
            return Missed();
        }
        var behind = messageIndex - entry.MessageIndex;
        if (behind > RecentWindow || behind < 0)
        {
            _reads.Remove(key);
            return Missed();
        }
        if (entry.StubbedLast)
        {
            // Asked again right after being told it's above: it wants the text, so give it (the read refreshes the entry).
            entry.StubbedLast = false;
            return Missed();
        }
        entry.StubbedLast = true;
        _reads_++;
        _saved += entry.Chars;
        return new ToolResult($"[Unchanged] {Path.GetFileName(path)} is exactly as it was when you read it {behind} message{(behind == 1 ? "" : "s")} ago (same lines) — that text is still above in this conversation. If you need it again, ask once more and the full text will be returned.");
    }

    private void RecordReadLocked(string arguments, Func<string, string> resolve, ToolResult result, int messageIndex)
    {
        if (ReadKey(arguments, resolve) is not var (key, path) || Stamp(path) is not var (length, modified)) return;
        _reads[key] = new ReadEntry(length, modified, messageIndex, result.Output.Length);
        if (_reads.Count > MaxEntries)
        {
            var oldest = _reads.OrderBy(r => r.Value.MessageIndex).First().Key;
            _reads.Remove(oldest);
        }
    }

    private static (string Key, string Path)? ReadKey(string arguments, Func<string, string> resolve)
    {
        var args = JsonArgs.Object(arguments);
        var raw = JsonArgs.String(args, "file_path");
        if (string.IsNullOrEmpty(raw)) return null;
        string path;
        try
        {
            path = resolve(raw);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return null;
        }
        return ($"{path}|{JsonArgs.Int(args, "start_line", 1)}|{JsonArgs.Int(args, "end_line", 0)}", path);
    }

    private static (long Length, DateTime ModifiedUtc)? Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // MARK: Bookkeeping

    private bool IsReadOnly(string tool, string arguments)
    {
        if (ReadOnly.Contains(tool)) return true;
        // A subagent of a read-only type can't change anything.
        return tool == "agent" && (AgentIsReadOnly ?? BuiltInAgentIsReadOnly)(JsonArgs.String(arguments, "agent_type"));
    }

    private static bool BuiltInAgentIsReadOnly(string? type) => AgentCatalog.Default.Find(type)?.IsReadOnly == true;

    /// <summary>Pages that change without a workspace write — a dev server on this PC, a router, an intranet
    /// site — must not be served from memory.</summary>
    private static bool Uncacheable(string tool, string arguments)
    {
        if (tool != "web_fetch") return false;
        if (JsonArgs.String(arguments, "url") is not { } url || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.'); // ("spark." is the same machine as "spark")
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".corp", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localdomain", StringComparison.OrdinalIgnoreCase))
            return true;
        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4(); // ::ffff:192.168.1.5 is 192.168.1.5
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
            var b = ip.GetAddressBytes();
            // 100.64.0.0/10 is the shared range Tailscale and carrier-grade NAT hand out: a machine on your own network.
            if (b.Length == 4) return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127);
            return b.Length == 16 && (b[0] & 0xFE) == 0xFC; // fc00::/7
        }
        return !host.Contains('.'); // a single-label name is an intranet host
    }

    private ToolResult? Missed()
    {
        _misses++;
        return null;
    }

    private static string KeyOf(string tool, string arguments, string workspace)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(workspace + "\u0001" + Canonical(tool, arguments)));
        return tool + ":" + Convert.ToHexString(digest, 0, 16);
    }

    /// <summary>The arguments re-serialised, so spacing between tokens doesn't matter — but spacing inside a string
    /// does ("new Foo" and "newFoo" are different searches).</summary>
    private static string Canonical(string tool, string arguments)
    {
        try
        {
            var node = JsonNode.Parse(arguments);
            // The "description" a call carries says why it is made, not what it asks: the same read with a new one is the same
            // read. (A subagent's own label is part of its report, so an agent call keeps it.)
            if (node is JsonObject obj && tool != "agent") obj.Remove("description");
            return node?.ToJsonString() ?? arguments;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return arguments;
        }
    }

    private void RemoveLocked(string key, Entry entry)
    {
        if (_entries.Remove(key)) _totalChars -= entry.Size;
    }

    private void EvictLocked()
    {
        while (_entries.Count > MaxEntries || _totalChars > MaxChars)
        {
            var oldest = _entries.OrderBy(e => e.Value.LastUsed).First();
            RemoveLocked(oldest.Key, oldest.Value);
        }
    }

    private static string Ago(TimeSpan age) => age.TotalMinutes >= 1 ? $"{(int)age.TotalMinutes} min ago" : "moments ago";
}
