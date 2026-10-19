using System.Text.Json;

namespace Vision.Core;

/// <summary>Codex CLI's sessions ("rollouts"): <c>~\.codex\sessions\YYYY\MM\DD\rollout-…-&lt;id&gt;.jsonl</c> (and
/// <c>archived_sessions</c>), one <c>{timestamp, type, payload}</c> per line — <c>session_meta</c> (id, cwd), the person's
/// <c>user_message</c> events, the model's <c>response_item</c>s (messages, function calls and their outputs) and
/// <c>compacted</c> summaries. Titles come from <c>session_index.jsonl</c> (Codex Desktop's thread names). Older rollouts
/// without the payload wrapper are read too.</summary>
public static class CodexChats
{
    /// <summary>Every rollout under the Codex home (sessions and archived_sessions), newest folders first.</summary>
    public static IReadOnlyList<string> SessionFiles(string codexHome)
    {
        var files = new List<string>();
        foreach (var folder in new[] { Path.Combine(codexHome, "sessions"), Path.Combine(codexHome, "archived_sessions") })
        {
            if (!Directory.Exists(folder)) continue;
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*.jsonl", new EnumerationOptions
                {
                    RecurseSubdirectories = true, MaxRecursionDepth = 4, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true,
                }));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Unreadable: nothing from there.
            }
        }
        return files;
    }

    /// <summary>Thread names by session id, from <c>session_index.jsonl</c> (the last name given wins).</summary>
    public static IReadOnlyDictionary<string, string> ThreadNames(string codexHome)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = Path.Combine(codexHome, "session_index.jsonl");
        if (!File.Exists(path)) return names;
        try
        {
            JsonLines.Read(path, (e, _) =>
            {
                if (e.Str("id") is { } id && e.Str("thread_name") is { Length: > 0 } name) names[id] = name;
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // No titles: the first prompts name the chats.
        }
        return names;
    }

    public static ImportedChat? Read(string path, IReadOnlyDictionary<string, string>? threadNames = null, bool summaryOnly = false,
                                     CancellationToken cancellationToken = default)
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

        // The person's prompts come as user_message events; a rollout without them (an old one) has only the model-facing
        // user items, which also carry the harness's own context — those are told apart below.
        var hasUserEvents = false;
        JsonLines.Read(path, (e, _) =>
        {
            if (!hasUserEvents && e.Str("type") == "event_msg" && e.Get("payload").Str("type") == "user_message") hasUserEvents = true;
        }, cancellationToken);

        var rows = new ChatRowBuilder(modified, summaryOnly);
        string? id = null, cwd = null, model = null;
        var scripted = false;
        DateTimeOffset? started = null;
        JsonLines.Read(path, (e, line) =>
        {
            var at = ChatRowBuilder.Time(e.Get("timestamp"));
            var type = e.Str("type");
            var payload = e.Get("payload");
            if (payload.ValueKind != JsonValueKind.Object)
            {
                // The old shape: the first line is the session ({id, timestamp, instructions}), the rest are items.
                if (line == 0 && type is null && e.Str("id") is { } oldId)
                {
                    id = oldId;
                    started = at;
                    if (at is { } begun) rows.Clock(begun);
                    return;
                }
                if (type is "message" or "function_call" or "function_call_output" or "local_shell_call" or "custom_tool_call" or "custom_tool_call_output")
                    Item(e, at, rows, hasUserEvents);
                return;
            }
            switch (type)
            {
                case "session_meta":
                    // codex exec: a scripted run nobody typed into.
                    if (payload.Str("originator")?.Contains("exec", StringComparison.Ordinal) == true || payload.Str("source") == "exec") scripted = true;
                    id ??= payload.Str("id");
                    cwd ??= payload.Str("cwd");
                    started ??= ChatRowBuilder.Time(payload.Get("timestamp")) ?? at;
                    if (started is { } begun) rows.Clock(begun);
                    break;
                case "turn_context":
                    model = payload.Str("model") ?? model;
                    cwd ??= payload.Str("cwd");
                    break;
                case "event_msg":
                    Event(payload, at, rows);
                    break;
                case "response_item":
                    Item(payload, at, rows, hasUserEvents);
                    break;
                case "compacted":
                    rows.Compaction(payload.Str("message"), at);
                    break;
            }
        }, cancellationToken);
        if (rows.Messages == 0) return null;

        id ??= IdFromName(path);
        var title = threadNames is not null && threadNames.TryGetValue(id, out var named) && named.Trim().Length > 0
            ? ChatRowBuilder.OneLine(named, 120)
            : ChatRowBuilder.TitleFrom(rows.FirstPrompt, "Codex session " + ClaudeCodeChats.Short(id));
        return new ImportedChat
        {
            Source = ChatSource.Codex,
            SourceId = id,
            Title = title,
            Rows = rows.Rows,
            Project = cwd,
            Model = model,
            StartedAt = started ?? rows.First,
            UpdatedAt = rows.Last ?? modified,
            From = path,
            Notes = scripted ? ["Scripted run"] : [],
            Automated = scripted,
        };
    }

    /// <summary>"rollout-2026-03-26T20-24-56-019d2d52-d341-7c81-a9c4-936aefd65446" → the trailing uuid.</summary>
    internal static string IdFromName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Length > 36 && Guid.TryParse(name[^36..], out _) ? name[^36..] : name;
    }

    private static void Event(JsonElement payload, DateTimeOffset? at, ChatRowBuilder rows)
    {
        switch (payload.Str("type"))
        {
            case "user_message":
                var text = payload.Str("message") ?? "";
                var images = payload.Get("images").Items().Count() + payload.Get("local_images").Items().Count();
                if (images > 0) text += images == 1 ? "\n\n[image]" : $"\n\n[{images} images]";
                rows.User(text, at);
                break;
            case "turn_aborted":
                rows.Notice("The turn was interrupted in Codex.", at);
                break;
        }
    }

    private static void Item(JsonElement item, DateTimeOffset? at, ChatRowBuilder rows, bool hasUserEvents)
    {
        switch (item.Str("type"))
        {
            case "message":
                var role = item.Str("role");
                var text = JsonRead.Text(item.Get("content"));
                if (role == "assistant") rows.Assistant(text, at);
                else if (role == "user" && !hasUserEvents && !IsHarnessContext(text)) rows.User(text, at);
                break;
            case "function_call":
                rows.ToolCall(item.Str("call_id"), item.Str("name") ?? "tool", ChatRowBuilder.Arguments(item.Get("arguments")), at);
                break;
            case "custom_tool_call":
                var tool = item.Str("name") ?? "tool";
                var input = item.Get("input");
                rows.ToolCall(item.Str("call_id"), tool,
                    tool == "apply_patch" && input.ValueKind == JsonValueKind.String && PatchFiles(input.GetString()!) is { Length: > 0 } files
                        ? files
                        : ChatRowBuilder.Arguments(input), at);
                break;
            case "local_shell_call":
                rows.ToolCall(item.Str("call_id"), "shell", ChatRowBuilder.Arguments(item.Get("action")), at);
                break;
            case "web_search_call":
                rows.ToolCall(null, "web_search", ChatRowBuilder.Arguments(item.Get("action")), at);
                rows.ToolResult(null, "web_search", "", false, at);
                break;
            case "function_call_output":
            case "custom_tool_call_output":
            case "local_shell_call_output":
                var output = item.Get("output");
                var (result, failed) = output.ValueKind switch
                {
                    JsonValueKind.String => (output.GetString() ?? "", false),
                    JsonValueKind.Object => (output.Str("content") ?? JsonRead.Text(output.Get("content")), output.Get("success").ValueKind == JsonValueKind.False),
                    _ => ("", false),
                };
                var (body, exitFailed) = ExecOutput(result);
                rows.ToolResult(item.Str("call_id"), null, body, failed || exitFailed, at);
                break;
        }
    }

    /// <summary>A command's output without Codex's header ("Command: … Chunk ID: … Process exited with code N … Output:"), or
    /// the output inside an older rollout's <c>{"output", "metadata":{"exit_code"}}</c>; and whether the command failed.</summary>
    internal static (string Body, bool Failed) ExecOutput(string output)
    {
        var trimmed = output.TrimStart();
        if (trimmed.StartsWith('{') && trimmed.Contains("\"output\"", StringComparison.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.Str("output") is { } inner)
                {
                    var code = doc.RootElement.Get("metadata").Get("exit_code");
                    return (inner, code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var exit) && exit != 0);
                }
            }
            catch (JsonException)
            {
                // Not that shape: the text as it is.
            }
        }
        var failed = ExitedWithError(output);
        if (trimmed.StartsWith("Command:", StringComparison.Ordinal) || trimmed.StartsWith("Chunk ID:", StringComparison.Ordinal))
        {
            var marker = output.IndexOf("\nOutput:\n", StringComparison.Ordinal);
            if (marker >= 0) return (output[(marker + "\nOutput:\n".Length)..], failed);
        }
        return (output, failed);
    }

    /// <summary>The files a patch touches: "Add File: notes.md, Update File: src/app.py" (Vision for macOS's line).</summary>
    internal static string PatchFiles(string patch)
    {
        var files = new List<string>();
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var verb in new[] { "Add File: ", "Update File: ", "Delete File: " })
                if (line.StartsWith("*** " + verb, StringComparison.Ordinal)) files.Add(verb + line[(4 + verb.Length)..].Trim());
        }
        return ChatRowBuilder.OneLine(string.Join(", ", files), ChatRowBuilder.MaxArgSummary);
    }

    /// <summary>Codex's exec output says "Process exited with code N" (or "Exit code: N"); anything but 0 is a failure.</summary>
    private static bool ExitedWithError(string output)
    {
        foreach (var marker in new[] { "Process exited with code ", "Exit code: " })
        {
            var at = output.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0 || at > 400) continue;
            var start = at + marker.Length;
            var end = start;
            while (end < output.Length && char.IsAsciiDigit(output[end])) end++;
            return end > start && output[start..end] != "0";
        }
        return false;
    }

    /// <summary>What Codex itself put in front of the model as a "user" item: the environment, the project's AGENTS.md, the
    /// person's standing instructions.</summary>
    private static bool IsHarnessContext(string text)
    {
        var start = text.TrimStart();
        return start.StartsWith("<environment_context>", StringComparison.Ordinal) || start.StartsWith("<user_instructions>", StringComparison.Ordinal)
               || start.StartsWith("# AGENTS.md instructions", StringComparison.Ordinal) || start.StartsWith("<permissions instructions>", StringComparison.Ordinal);
    }
}
