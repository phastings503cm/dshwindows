using System.Text.Json;

namespace Vision.Core;

/// <summary>Claude Code's sessions: one JSON Lines file per session under <c>~\.claude\projects\&lt;project&gt;\</c>. Each line
/// is an entry (user, assistant, attachment, system, title…) linked to the one before by <c>parentUuid</c>; a rewound
/// session keeps its abandoned branch in the file, so the conversation is the chain that ends at the last message.
/// Sidechain entries and the subagent transcripts beside the file (<c>&lt;session&gt;\subagents\</c>) are left out — what a
/// subagent found came back to the main chat as its tool result.</summary>
public static class ClaudeCodeChats
{
    /// <summary>The session files under <paramref name="projects"/> (one level of project folders; subagent files are deeper
    /// and not listed).</summary>
    public static IReadOnlyList<string> SessionFiles(string projects)
    {
        var files = new List<string>();
        if (!Directory.Exists(projects)) return files;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(projects))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*.jsonl"));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // An unreadable project folder: the others still count.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // No access to the projects folder at all.
        }
        return files;
    }

    /// <summary>Read one session file. Null when it holds no conversation (a session opened and closed, a title-only
    /// file).</summary>
    public static ImportedChat? Read(string path, bool summaryOnly = false, CancellationToken cancellationToken = default)
    {
        var sessionId = Path.GetFileNameWithoutExtension(path);
        DateTimeOffset modified;
        try
        {
            modified = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            modified = DateTimeOffset.UtcNow;
        }

        // Pass 1: the tree, and the session's titles.
        var nodes = new Dictionary<string, (string? Parent, int Line)>(StringComparer.Ordinal);
        // Parallel tool calls: their results are siblings, and the chain runs through only one of them.
        var calls = new Dictionary<int, List<string>>();
        var results = new Dictionary<int, List<string>>();
        string? customTitle = null, aiTitle = null, agentName = null, summaryTitle = null;
        string? leaf = null;
        var leafLine = -1;
        JsonLines.Read(path, (e, line) =>
        {
            var type = e.Str("type");
            switch (type)
            {
                case "custom-title":
                    customTitle = e.Str("customTitle") ?? customTitle;
                    return;
                case "ai-title":
                    aiTitle = e.Str("aiTitle") ?? aiTitle;
                    return;
                case "agent-name":
                    agentName = e.Str("agentName") ?? agentName;
                    return;
                case "summary":
                    summaryTitle = e.Str("summary") ?? summaryTitle;
                    return;
            }
            if (e.Str("uuid") is not { } uuid) return;
            if (e.Flag("isSidechain")) return;
            var parent = e.Str("parentUuid") ?? e.Str("logicalParentUuid");
            nodes[uuid] = (parent, line);
            var content = e.Get("message").Get("content");
            if (type == "assistant")
            {
                var ids = content.Items().Where(b => b.Str("type") is "tool_use" or "server_tool_use").Select(b => b.Str("id")).OfType<string>().ToList();
                if (ids.Count > 0) calls[line] = ids;
            }
            else if (type == "user" && content.ValueKind == JsonValueKind.Array && content.GetArrayLength() > 0
                     && content.EnumerateArray().All(b => b.Str("type") == "tool_result"))
            {
                results[line] = content.EnumerateArray().Select(b => b.Str("tool_use_id")).OfType<string>().ToList();
            }
            if (type is "user" or "assistant" && line > leafLine)
            {
                leaf = uuid;
                leafLine = line;
            }
        }, cancellationToken);
        if (leaf is null) return null;

        // The conversation: the chain that ends at the last message.
        var chain = new HashSet<int>();
        var guard = 0;
        string? at = leaf;
        for (; at is not null && nodes.TryGetValue(at, out var node) && guard++ < 2_000_000; at = node.Parent)
            if (!chain.Add(node.Line)) break;
        // A chain that breaks off at an entry the file doesn't have (an older Claude Code, a damaged file) would lose the
        // start of the conversation: then every main-line entry counts, in file order.
        if (at is not null && !nodes.ContainsKey(at)) chain = nodes.Values.Select(n => n.Line).ToHashSet();
        // A result off the chain that answers only calls on it belongs to the conversation (the sibling results of
        // parallel calls) — without it those calls would read as never answered.
        var called = calls.Where(c => chain.Contains(c.Key)).SelectMany(c => c.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var (line, ids) in results)
            if (!chain.Contains(line) && ids.Count > 0 && ids.All(called.Contains)) chain.Add(line);

        // Pass 2: the rows, in file order.
        var rows = new ChatRowBuilder(modified, summaryOnly);
        string? cwd = null, model = null, version = null, entrypoint = null;
        DateTimeOffset? started = null;
        var images = 0;
        JsonLines.Read(path, (e, line) =>
        {
            if (!chain.Contains(line)) return;
            var at = ChatRowBuilder.Time(e.Get("timestamp"));
            started ??= at;
            if (e.Str("cwd") is { Length: > 0 } where) cwd = where;
            version ??= e.Str("version");
            entrypoint ??= e.Str("entrypoint");
            switch (e.Str("type"))
            {
                case "user":
                    User(e, at, rows, ref images);
                    break;
                case "attachment":
                    // A prompt typed while a turn ran, queued until it finished: said nowhere else.
                    var attachment = e.Get("attachment");
                    if (attachment.Str("type") == "queued_command" && !e.Flag("isMeta") && !attachment.Flag("isMeta")
                        && attachment.Str("prompt") is { } queued && !queued.TrimStart().StartsWith('<'))
                        rows.User(queued, at);
                    break;
                case "assistant":
                    var message = e.Get("message");
                    if (message.Str("model") is { } m && m != "<synthetic>") model = m;
                    Assistant(message, at, rows);
                    break;
            }
        }, cancellationToken);
        if (rows.Messages == 0) return null;

        var notes = new List<string>();
        // A headless run (claude -p, the Agent SDK's CLI) is work nobody typed.
        var scripted = entrypoint?.StartsWith("sdk", StringComparison.Ordinal) == true;
        if (scripted) notes.Add("Scripted run");
        var subagents = SubagentCount(path);
        if (subagents > 0) notes.Add(subagents == 1 ? "1 subagent transcript left out (its result is in the chat)" : $"{subagents} subagent transcripts left out (their results are in the chat)");
        if (images > 0) notes.Add(images == 1 ? "1 image not imported" : $"{images} images not imported");
        var title = FirstText(customTitle, aiTitle, agentName, summaryTitle) is { } named
            ? ChatRowBuilder.OneLine(named, 120)
            : ChatRowBuilder.TitleFrom(rows.FirstPrompt, "Claude Code session " + Short(sessionId));
        return new ImportedChat
        {
            Source = ChatSource.ClaudeCode,
            SourceId = sessionId,
            Title = title,
            Rows = rows.Rows,
            Project = cwd,
            Model = model,
            StartedAt = started ?? rows.First,
            UpdatedAt = rows.Last ?? modified,
            From = path,
            Notes = notes,
            Subagents = subagents,
            Automated = scripted,
        };
    }

    private static string? FirstText(params string?[] candidates) => candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

    internal static string Short(string id) => id.Length > 8 ? id[..8] : id;

    /// <summary>How many subagent transcripts sit beside the session (<c>&lt;session&gt;\subagents\*.jsonl</c>).</summary>
    private static int SubagentCount(string path)
    {
        var folder = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path), "subagents");
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.jsonl").Count() : 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    // MARK: - Entries

    private static void User(JsonElement e, DateTimeOffset? at, ChatRowBuilder rows, ref int images)
    {
        if (e.Flag("isMeta")) return;
        var content = e.Get("message").Get("content");
        if (e.Flag("isCompactSummary"))
        {
            rows.Compaction(JsonRead.Text(content), at);
            return;
        }
        if (content.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var block in content.EnumerateArray())
            {
                switch (block.Str("type"))
                {
                    case "tool_result":
                        rows.ToolResult(block.Str("tool_use_id"), null, ChatRowBuilder.WithoutReminders(ResultText(block.Get("content"), ref images)), block.Flag("is_error"), at);
                        break;
                    case "text":
                        if (block.Str("text") is { } text) texts.Add(text);
                        break;
                    case "image":
                        images++;
                        texts.Add("[image]");
                        break;
                    case "document":
                        texts.Add("[document]");
                        break;
                }
            }
            if (texts.Count > 0) Prompt(string.Join("\n\n", texts), e, at, rows);
            return;
        }
        if (content.ValueKind == JsonValueKind.String) Prompt(content.GetString() ?? "", e, at, rows);
    }

    /// <summary>A user entry's text: a prompt, a slash command, its output, or something the harness itself put there (a
    /// background task's notice, a message from another session) — those become notices, not prompts.</summary>
    private static void Prompt(string text, JsonElement e, DateTimeOffset? at, ChatRowBuilder rows)
    {
        text = ChatRowBuilder.WithoutReminders(text).Trim();
        if (text.Length == 0) return;
        if (text.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
        {
            rows.Notice("Interrupted by the user.", at);
            return;
        }
        var origin = e.Get("origin").Str("kind");
        if (origin is "task-notification" or "peer" || text.StartsWith("<task-notification>", StringComparison.Ordinal))
        {
            rows.Notice(Notification(text, origin), at);
            return;
        }
        if (Tag(text, "local-command-stdout") is { } stdout)
        {
            rows.Notice(stdout, at);
            return;
        }
        if (Tag(text, "command-name") is { } command)
        {
            // A slash command was never said to a model: a notice, not a prompt to replay.
            var args = Tag(text, "command-args");
            rows.Notice($"Ran {(string.IsNullOrWhiteSpace(args) ? command : command + " " + args)} in Claude Code.", at);
            return;
        }
        if (Tag(text, "bash-input") is { } bash)
        {
            rows.User("! " + bash, at);
            return;
        }
        if (Tag(text, "bash-stdout") is { } bashOut)
        {
            var error = Tag(text, "bash-stderr");
            rows.Notice(string.IsNullOrWhiteSpace(error) ? bashOut : bashOut + "\n" + error, at);
            return;
        }
        rows.User(text, at);
    }

    /// <summary>"Background task finished: <summary>" from a task notification; a peer's message as it was.</summary>
    private static string Notification(string text, string? origin)
    {
        if (Tag(text, "summary") is { Length: > 0 } summary)
        {
            var status = Tag(text, "status");
            return string.IsNullOrWhiteSpace(status) ? "Background task: " + summary : $"Background task {status}: {summary}";
        }
        return origin == "peer" ? "Message from another session: " + text : text;
    }

    private static void Assistant(JsonElement message, DateTimeOffset? at, ChatRowBuilder rows)
    {
        var id = message.Str("id");
        var synthetic = message.Str("model") == "<synthetic>";
        var content = message.Get("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            if (synthetic) rows.Error(content.GetString(), at);
            else rows.Assistant(content.GetString(), at, id);
            return;
        }
        foreach (var block in content.Items())
        {
            switch (block.Str("type"))
            {
                case "text":
                    if (synthetic) rows.Error(block.Str("text"), at);
                    else rows.Assistant(block.Str("text"), at, id);
                    break;
                case "tool_use":
                case "server_tool_use":
                    rows.ToolCall(block.Str("id"), block.Str("name") ?? "tool", ChatRowBuilder.Arguments(block.Get("input")), at);
                    break;
                case "web_search_tool_result":
                    rows.ToolResult(block.Str("tool_use_id"), "web_search", "Search results", false, at);
                    break;
            }
        }
    }

    /// <summary>A tool result's text (string or blocks); images in it are counted and noted.</summary>
    private static string ResultText(JsonElement content, ref int images)
    {
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        var parts = new List<string>();
        foreach (var block in content.Items())
        {
            if (block.Str("type") == "image")
            {
                images++;
                parts.Add("[image]");
            }
            else if (block.Str("text") is { } text) parts.Add(text);
        }
        return string.Join("\n", parts);
    }

    /// <summary>The text inside the first &lt;tag&gt;…&lt;/tag&gt;, trimmed; null when there is none.</summary>
    private static string? Tag(string text, string tag)
    {
        var open = text.IndexOf("<" + tag + ">", StringComparison.Ordinal);
        if (open < 0) return null;
        var start = open + tag.Length + 2;
        var close = text.IndexOf("</" + tag + ">", start, StringComparison.Ordinal);
        return (close < 0 ? text[start..] : text[start..close]).Trim();
    }
}
