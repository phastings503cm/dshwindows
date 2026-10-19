using System.Text.Json;

namespace Vision.Core;

/// <summary>OpenClaw's sessions. Since 2026.8 an agent keeps them in SQLite — <c>agents\&lt;agent&gt;\agent\openclaw-agent.sqlite</c>,
/// table <c>session_windows</c> (one row per conversation: key, display name, times, model) and <c>transcript_events</c> (its
/// events in order, each the JSON line older versions wrote to <c>agents\&lt;agent&gt;\sessions\&lt;id&gt;.jsonl</c>, indexed by
/// <c>sessions.json</c>). Both are read. The events are pi-agent session lines: <c>session</c> (id, cwd), <c>message</c> (user,
/// assistant with text and tool calls, toolResult), <c>compaction</c>, <c>custom_message</c>, <c>model_change</c>, linked by
/// <c>parentId</c> — the conversation is the chain that ends at the last leaf (or message). Reset generations
/// (<c>&lt;id&gt;.jsonl.reset.*</c>) and the pre-SQLite archive (<c>session-sqlite-import-archive</c>) are read too; deleted
/// (zstd) sessions aren't. Cron runs, subagents and other runs are listed but not ticked, behind "Show scheduled and
/// scripted runs". The rules and words are Vision for macOS's.</summary>
public static class OpenClawChats
{
    /// <summary>The OpenClaw state folders in a home: OPENCLAW_STATE_DIR, ~/.openclaw, profiles (~/.openclaw-work) and the
    /// older names.</summary>
    public static IReadOnlyList<string> StateDirs(string home, string? stateDirOverride = null)
    {
        var found = new List<string>();
        void Add(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            var full = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (!found.Contains(full, StringComparer.OrdinalIgnoreCase)) found.Add(full);
        }
        Add(stateDirOverride);
        foreach (var name in new[] { ".openclaw", ".clawdbot", ".moltbot" }) Add(Path.Combine(home, name));
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(home, ".*"))
            {
                var name = Path.GetFileName(folder).ToLowerInvariant();
                if (name.StartsWith(".openclaw-", StringComparison.Ordinal) || name.StartsWith(".clawdbot-", StringComparison.Ordinal)) Add(folder);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The home can't be listed: the usual names were still tried.
        }
        return found;
    }

    /// <summary>A place sessions are kept: an agent's SQLite store, or a folder of its transcript files.</summary>
    public sealed record Store(string Agent, string Path, bool IsSqlite);

    public static IReadOnlyList<Store> Stores(string stateDir)
    {
        var stores = new List<Store>();
        var agents = System.IO.Path.Combine(stateDir, "agents");
        if (!Directory.Exists(agents)) return stores;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(agents))
            {
                var agent = System.IO.Path.GetFileName(folder);
                var sqlite = System.IO.Path.Combine(folder, "agent", "openclaw-agent.sqlite");
                if (File.Exists(sqlite)) stores.Add(new Store(agent, sqlite, true));
                // Older transcripts, reset generations and the pre-SQLite archive are files.
                foreach (var name in new[] { "sessions", "session-sqlite-import-archive" })
                    if (System.IO.Path.Combine(folder, name) is var files && Directory.Exists(files)) stores.Add(new Store(agent, files, false));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unreadable agents folder.
        }
        return stores;
    }

    // MARK: - SQLite (2026.8 and later)

    /// <summary>A conversation listed in an agent's SQLite store.</summary>
    /// <param name="Via">session_nodes.created_via: operator, cron, spawn, run, …</param>
    public sealed record SqliteSession(string Id, string Key, string? DisplayName, DateTimeOffset? Created, DateTimeOffset? Updated, string? Model,
                                       string? Via = null, string Agent = "main");

    public static IReadOnlyList<SqliteSession> ListSqlite(string database, string agent = "main")
    {
        var sessions = new List<SqliteSession>();
        using var db = SqliteReader.OpenReadOnly(database);
        if (!db.HasTable("session_windows") || !db.HasTable("transcript_events")) return sessions;
        void Add(SqliteReader.Row row)
        {
            if (row.Text(0) is not { } id) return;
            sessions.Add(new SqliteSession(id, row.Text(1) ?? "", row.Text(2), Millis(row.Int(3)), Millis(row.Int(4)), row.Text(5), row.Text(6), agent));
        }
        // The session's node names it best (its label) and says how it was made; an older store has no nodes table.
        try
        {
            if (db.HasTable("session_nodes"))
            {
                db.Query("""
                    SELECT w.session_id, w.session_key, COALESCE(NULLIF(n.label, ''), NULLIF(n.display_name, ''), NULLIF(w.display_name, '')),
                           w.created_at, w.updated_at, w.model, n.created_via
                    FROM session_windows w LEFT JOIN session_nodes n ON n.session_key = w.session_key ORDER BY w.updated_at DESC
                    """, Add);
                return sessions;
            }
        }
        catch (IOException)
        {
            sessions.Clear(); // a node table without those columns
        }
        db.Query("SELECT session_id, session_key, display_name, created_at, updated_at, model, NULL FROM session_windows ORDER BY updated_at DESC", Add);
        return sessions;
    }

    public static ImportedChat? ReadSqlite(string database, SqliteSession session, bool summaryOnly = false, CancellationToken cancellationToken = default)
    {
        using var db = SqliteReader.OpenReadOnly(database);
        var events = new List<string>();
        db.Query("SELECT event_json FROM transcript_events WHERE session_id = ? ORDER BY seq", row =>
        {
            if (row.Text(0) is { } json) events.Add(json);
        }, session.Id);
        cancellationToken.ThrowIfCancellationRequested();
        void Each(Action<JsonElement, int> visit)
        {
            for (var i = 0; i < events.Count; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(events[i]);
                }
                catch (JsonException)
                {
                    continue;
                }
                using (doc) visit(doc.RootElement, i);
            }
        }
        return Convert(Each, new Kind(session.Id, session.DisplayName, session.Key, session.Via, session.Agent, false), session.Model,
            session.Updated ?? session.Created ?? DateTimeOffset.UtcNow, database, summaryOnly);
    }

    private static DateTimeOffset? Millis(long? value) => value is > 0 ? ChatRowBuilder.Epoch(value.Value) : null;

    /// <summary>Work nobody typed, from its key or how it was made: a cron run, a subagent (spawned by another session), a
    /// run — listed behind "Show scheduled and scripted runs" (Vision for macOS's rule).</summary>
    public static bool IsAutomated(string? key, string? via = null) =>
        key is { } k && (k.Contains(":cron:", StringComparison.Ordinal) || k.Contains(":subagent:", StringComparison.Ordinal)
                         || k.Contains(":spawn:", StringComparison.Ordinal) || k.Contains(":run:", StringComparison.Ordinal))
        || via is "cron" or "spawn" or "run";

    /// <summary>"[cron:&lt;id&gt; &lt;name&gt;] …" (how a cron run's prompt starts) → the job's name.</summary>
    internal static string? CronName(string? prompt)
    {
        if (prompt is null || !prompt.StartsWith("[cron:", StringComparison.Ordinal)) return null;
        var close = prompt.IndexOf(']');
        if (close < 0) return null;
        var parts = prompt[6..close].Split(' ', 2);
        return parts.Length == 2 && parts[1].Trim().Length > 0 ? parts[1].Trim() : null;
    }

    /// <summary>The preview's remark for a session: "Before a reset", "Cron run", "Subagent", "Chat" or "Session" (and
    /// " · agent &lt;name&gt;" for an agent other than main).</summary>
    internal static string Remark(string? key, string? via, string agent, bool reset, bool cron)
    {
        var kind = reset ? "Before a reset"
            : key?.Contains(":cron:", StringComparison.Ordinal) == true || cron ? "Cron run"
            : key?.Contains(":subagent:", StringComparison.Ordinal) == true || via == "spawn" ? "Subagent"
            : key?.Contains(":dashboard:", StringComparison.Ordinal) == true ? "Chat"
            : "Session";
        return agent == "main" ? kind : $"{kind} · agent {agent}";
    }

    /// <summary>What a session is, as far as its store or file says.</summary>
    private sealed record Kind(string FallbackId, string? Label, string? Key, string? Via, string Agent, bool Reset);

    // MARK: - JSON Lines (before 2026.8)

    /// <summary>A transcript file: a session (<c>&lt;id&gt;.jsonl</c>), a reset generation (<c>&lt;id&gt;.jsonl.reset.&lt;time&gt;</c>) or the
    /// pre-SQLite archive's (<c>….jsonl.imported-&lt;ms&gt;</c>) — not trajectories, checkpoints, deleted (zstd) or .bak copies.</summary>
    internal static bool IsTranscriptFile(string name)
    {
        if (!name.Contains(".jsonl", StringComparison.Ordinal)) return false;
        foreach (var skip in new[] { ".trajectory.", ".checkpoint.", ".deleted.", ".zst", ".bak", "trajectory-path" })
            if (name.Contains(skip, StringComparison.Ordinal)) return false;
        return name.EndsWith(".jsonl", StringComparison.Ordinal) || name.Contains(".jsonl.reset.", StringComparison.Ordinal)
               || name.Contains(".jsonl.imported-", StringComparison.Ordinal);
    }

    /// <summary>"archive-tier.&lt;id&gt;.jsonl.imported-…" or "&lt;id&gt;.jsonl.reset.…" → the id in the name.</summary>
    internal static string IdFromFileName(string name)
    {
        var stem = name.StartsWith("archive-tier.", StringComparison.Ordinal) ? name["archive-tier.".Length..] : name;
        var at = stem.IndexOf(".jsonl", StringComparison.Ordinal);
        return at < 0 ? stem : stem[..at];
    }

    /// <summary>A folder's transcript files, with the key and label <c>sessions.json</c> gives each (when it has one).</summary>
    public static IReadOnlyList<(string Path, string Key, string? Label)> ListJsonl(string sessionsFolder)
    {
        var index = new Dictionary<string, (string Key, string? Label)>(StringComparer.Ordinal);
        var indexPath = Path.Combine(sessionsFolder, "sessions.json");
        if (File.Exists(indexPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var entry in doc.RootElement.EnumerateObject())
                        if (entry.Value.Str("sessionId") is { } id)
                            index[id] = (entry.Name, entry.Value.Str("label") ?? entry.Value.Str("displayName") ?? entry.Value.Str("subject"));
            }
            catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
            {
                // No index: the files still read, with titles from their first prompts.
            }
        }
        var files = new List<(string, string, string?)>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(sessionsFolder))
            {
                if (!IsTranscriptFile(Path.GetFileName(file))) continue;
                var id = IdFromFileName(Path.GetFileName(file));
                var (key, label) = index.TryGetValue(id, out var known) ? known : ("", null);
                files.Add((file, key, label));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Unreadable folder.
        }
        return files;
    }

    public static ImportedChat? ReadJsonl(string path, string key = "", string? label = null, bool summaryOnly = false, CancellationToken cancellationToken = default,
                                          string agent = "main")
    {
        DateTimeOffset modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            modified = DateTimeOffset.UtcNow;
        }
        var name = Path.GetFileName(path);
        var kind = new Kind(IdFromFileName(name), label, key.Length == 0 ? null : key, null, agent, name.Contains(".jsonl.reset.", StringComparison.Ordinal));
        return Convert(visit => JsonLines.Read(path, visit, cancellationToken), kind, null, modified, path, summaryOnly);
    }

    // MARK: - Events

    private static ImportedChat? Convert(Action<Action<JsonElement, int>> each, Kind kind, string? model, DateTimeOffset fallbackTime, string from,
                                         bool summaryOnly)
    {
        // Pass 1: the tree.
        var parents = new Dictionary<string, (string? Parent, int Index)>(StringComparer.Ordinal);
        string? lastMessage = null, leafTarget = null;
        each((e, i) =>
        {
            var type = e.Str("type");
            if (e.Str("id") is not { } id) return;
            parents[id] = (e.Str("parentId"), i);
            if (type == "message") lastMessage = id;
            else if (type == "leaf" && e.Str("targetId") is { } target) leafTarget = target;
        });
        var end = leafTarget is not null && parents.ContainsKey(leafTarget) ? leafTarget : lastMessage;
        if (end is null) return null;
        var chain = new HashSet<int>();
        string? at = end;
        var guard = 0;
        for (; at is not null && parents.TryGetValue(at, out var node) && guard++ < 2_000_000; at = node.Parent)
            if (!chain.Add(node.Index)) break;
        if (at is not null && !parents.ContainsKey(at)) chain = parents.Values.Select(p => p.Index).ToHashSet();

        // Pass 2: the rows.
        var rows = new ChatRowBuilder(fallbackTime, summaryOnly);
        string? id = null, cwd = null;
        DateTimeOffset? started = null;
        var images = 0;
        each((e, i) =>
        {
            var type = e.Str("type");
            var when = ChatRowBuilder.Time(e.Get("timestamp"));
            if (type == "session")
            {
                id ??= e.Str("id");
                cwd ??= e.Str("cwd");
                started ??= when;
                if (when is { } begun) rows.Clock(begun);
                return;
            }
            if (!chain.Contains(i)) return;
            switch (type)
            {
                case "message":
                    var message = e.Get("message");
                    when = ChatRowBuilder.Time(message.Get("timestamp")) ?? when;
                    Message(message, when, rows, ref images, ref model, e.Str("id"));
                    break;
                case "compaction":
                    rows.Compaction(e.Str("summary"), when);
                    break;
                case "branch_summary":
                    rows.Notice("Summary of an earlier branch:\n" + e.Str("summary"), when);
                    break;
                case "custom_message" when e.Flag("display"):
                    rows.Notice(JsonRead.Text(e.Get("content")), when);
                    break;
                case "model_change":
                    model = e.Str("modelId") ?? e.Str("model") ?? model;
                    break;
            }
        });
        if (rows.Messages == 0) return null;
        id ??= kind.FallbackId;
        var cron = CronName(rows.FirstPrompt);
        var notes = new List<string> { Remark(kind.Key, kind.Via, kind.Agent, kind.Reset, cron is not null) };
        if (images > 0) notes.Add(images == 1 ? "1 image not imported" : $"{images} images not imported");
        var automated = IsAutomated(kind.Key, kind.Via) || cron is not null;
        var title = !string.IsNullOrWhiteSpace(kind.Label) ? kind.Label
            : cron is not null ? "Cron: " + cron
            : null;
        return new ImportedChat
        {
            Source = ChatSource.OpenClaw,
            SourceId = id,
            Title = title is not null ? ChatRowBuilder.OneLine(title, 120) : ChatRowBuilder.TitleFrom(rows.FirstPrompt, "OpenClaw session"),
            Rows = rows.Rows,
            Project = IsOpenClawFolder(cwd) ? null : cwd,
            Model = model,
            StartedAt = started ?? rows.First,
            UpdatedAt = rows.Last ?? fallbackTime,
            From = from,
            Notes = notes,
            Automated = automated,
        };
    }

    /// <summary>OpenClaw's own workspace isn't a project of the person's: a chat there imports as a chat without one.</summary>
    private static bool IsOpenClawFolder(string? cwd) =>
        cwd is null || cwd.Replace('\\', '/').Contains("/.openclaw", StringComparison.OrdinalIgnoreCase);

    private static void Message(JsonElement message, DateTimeOffset? at, ChatRowBuilder rows, ref int images, ref string? model, string? key)
    {
        var content = message.Get("content");
        switch (message.Str("role"))
        {
            case "user":
                var parts = new List<string>();
                if (content.ValueKind == JsonValueKind.String) parts.Add(content.GetString() ?? "");
                foreach (var block in content.Items())
                {
                    if (block.Str("type") == "image")
                    {
                        images++;
                        parts.Add("[image]");
                    }
                    else if (block.Str("text") is { } text) parts.Add(text);
                }
                rows.User(string.Join("\n\n", parts), at);
                break;
            case "assistant":
                model = message.Str("model") ?? model;
                if (content.ValueKind == JsonValueKind.String) rows.Assistant(content.GetString(), at, key);
                foreach (var block in content.Items())
                {
                    switch (block.Str("type"))
                    {
                        case "text":
                            rows.Assistant(block.Str("text"), at, key);
                            break;
                        case "toolCall":
                            rows.ToolCall(block.Str("id"), block.Str("name") ?? "tool", ChatRowBuilder.Arguments(block.Get("arguments")), at);
                            break;
                    }
                }
                if (message.Str("stopReason") == "error" && message.Str("errorMessage") is { Length: > 0 } error) rows.Error(error, at);
                break;
            case "toolResult":
                var output = new List<string>();
                if (content.ValueKind == JsonValueKind.String) output.Add(content.GetString() ?? "");
                foreach (var block in content.Items())
                {
                    if (block.Str("type") == "image")
                    {
                        images++;
                        output.Add("[image]");
                    }
                    else if (block.Str("text") is { } text) output.Add(text);
                }
                rows.ToolResult(message.Str("toolCallId"), message.Str("toolName"), string.Join("\n", output), message.Flag("isError"), at);
                break;
            case "bashExecution":
                rows.ToolCall(null, "bash", message.Str("command"), at);
                rows.ToolResult(null, "bash", message.Str("output"), message.Get("exitCode") is { ValueKind: JsonValueKind.Number } code && code.TryGetInt32(out var exit) && exit != 0, at);
                break;
        }
    }
}
