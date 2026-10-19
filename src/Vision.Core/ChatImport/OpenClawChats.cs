using System.Text.Json;

namespace Vision.Core;

/// <summary>OpenClaw's sessions. Since 2026.8 an agent keeps them in SQLite — <c>agents\&lt;agent&gt;\agent\openclaw-agent.sqlite</c>,
/// table <c>session_windows</c> (one row per conversation: key, display name, times, model) and <c>transcript_events</c> (its
/// events in order, each the JSON line older versions wrote to <c>agents\&lt;agent&gt;\sessions\&lt;id&gt;.jsonl</c>, indexed by
/// <c>sessions.json</c>). Both are read. The events are pi-agent session lines: <c>session</c> (id, cwd), <c>message</c> (user,
/// assistant with text and tool calls, toolResult), <c>compaction</c>, <c>custom_message</c>, <c>model_change</c>, linked by
/// <c>parentId</c> — the conversation is the chain that ends at the last message. Subagent sessions are left out (their
/// results are in the parent's chat); scheduled runs are listed but not ticked. Deleted and reset sessions aren't offered.</summary>
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

    /// <summary>A place sessions are kept: an agent's SQLite store, or its sessions folder.</summary>
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
                var sessions = System.IO.Path.Combine(folder, "sessions");
                if (Directory.Exists(sessions)) stores.Add(new Store(agent, sessions, false));
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
    public sealed record SqliteSession(string Id, string Key, string? DisplayName, DateTimeOffset? Created, DateTimeOffset? Updated, string? Model);

    public static IReadOnlyList<SqliteSession> ListSqlite(string database)
    {
        var sessions = new List<SqliteSession>();
        using var db = SqliteReader.OpenReadOnly(database);
        if (!db.HasTable("session_windows") || !db.HasTable("transcript_events")) return sessions;
        db.Query("SELECT session_id, session_key, display_name, created_at, updated_at, model FROM session_windows ORDER BY updated_at DESC", row =>
        {
            if (row.Text(0) is not { } id) return;
            sessions.Add(new SqliteSession(id, row.Text(1) ?? "", row.Text(2), Millis(row.Int(3)), Millis(row.Int(4)), row.Text(5)));
        });
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
        return Convert(Each, session.Id, session.DisplayName, session.Key, session.Model, session.Updated ?? session.Created ?? DateTimeOffset.UtcNow,
            database, summaryOnly);
    }

    private static DateTimeOffset? Millis(long? value) => value is > 0 ? ChatRowBuilder.Epoch(value.Value) : null;

    /// <summary>Not a conversation of its own: a subagent's.</summary>
    public static bool IsSubagent(string key) => key.Contains(":subagent:", StringComparison.Ordinal);

    /// <summary>A scheduled run (cron) or an automation's.</summary>
    public static bool IsAutomated(string key, string? displayName) =>
        key.Contains(":cron:", StringComparison.Ordinal) || displayName is { } name && (name.StartsWith("Cron:", StringComparison.Ordinal) || name.StartsWith("Automation:", StringComparison.Ordinal));

    // MARK: - JSON Lines (before 2026.8)

    /// <summary>A sessions folder's live transcripts (<c>&lt;id&gt;.jsonl</c> — not the .reset/.deleted/.bak copies), with the
    /// key and label <c>sessions.json</c> gives each.</summary>
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
            foreach (var file in Directory.EnumerateFiles(sessionsFolder, "*.jsonl"))
            {
                var id = Path.GetFileNameWithoutExtension(file);
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

    public static ImportedChat? ReadJsonl(string path, string key = "", string? label = null, bool summaryOnly = false, CancellationToken cancellationToken = default)
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
        return Convert(visit => JsonLines.Read(path, visit, cancellationToken), Path.GetFileNameWithoutExtension(path), label, key, null, modified, path, summaryOnly);
    }

    // MARK: - Events

    private static ImportedChat? Convert(Action<Action<JsonElement, int>> each, string fallbackId, string? title, string key, string? model,
                                         DateTimeOffset fallbackTime, string from, bool summaryOnly)
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
        id ??= fallbackId;
        var notes = new List<string>();
        if (images > 0) notes.Add(images == 1 ? "1 image not imported" : $"{images} images not imported");
        var automated = IsAutomated(key, title);
        if (automated) notes.Insert(0, "Scheduled run");
        return new ImportedChat
        {
            Source = ChatSource.OpenClaw,
            SourceId = id,
            Title = !string.IsNullOrWhiteSpace(title) ? ChatRowBuilder.OneLine(title, 120) : ChatRowBuilder.TitleFrom(rows.FirstPrompt, "OpenClaw session " + ClaudeCodeChats.Short(id)),
            Rows = rows.Rows,
            Project = IsOpenClawFolder(cwd) ? null : cwd,
            Model = model,
            StartedAt = started ?? rows.First,
            UpdatedAt = rows.Last ?? fallbackTime,
            From = from,
            Notes = notes,
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
