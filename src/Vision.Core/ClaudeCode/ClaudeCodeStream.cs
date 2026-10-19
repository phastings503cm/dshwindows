using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

// MARK: - Claude Code's tools, as Vision shows them

public static class ClaudeCodeTools
{
    /// <summary>The Vision tool a Claude Code tool matches, so its card gets the same icon and label (unknown ones — MCP
    /// tools, WebSearch — keep their own name).</summary>
    public static string VisionName(string tool) => tool switch
    {
        "Read" => "read_file",
        "Write" => "write_file",
        "Edit" or "MultiEdit" or "NotebookEdit" => "edit",
        "Bash" or "PowerShell" => "run_shell_command",
        "Glob" => "glob",
        "Grep" => "grep",
        "LS" => "list_directory",
        "WebFetch" => "web_fetch",
        "TodoWrite" or "TaskCreate" or "TaskUpdate" => "todo_write",
        "Task" or "Agent" => "agent",
        "ExitPlanMode" => "exit_plan_mode",
        "Skill" => "use_skill",
        _ => tool,
    };

    /// <summary>A person's word for a tool ("Run", "Edit"), for permission questions.</summary>
    public static string Label(string tool) => tool switch
    {
        "Read" => "Read",
        "Write" => "Write",
        "Edit" or "MultiEdit" => "Edit",
        "NotebookEdit" => "Edit notebook",
        "Bash" or "PowerShell" => "Run",
        "Glob" => "Find files",
        "Grep" => "Search",
        "WebFetch" => "Fetch",
        "WebSearch" => "Web search",
        "Task" or "Agent" => "Subagent",
        "ExitPlanMode" => "Present plan",
        "AskUserQuestion" => "Ask you",
        _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "MCP " + string.Join(" · ", tool.Split("__").Skip(1)),
        _ => tool,
    };

    /// <summary>One line saying what a call does: the file, the command, the pattern, the URL.</summary>
    public static string Preview(string tool, JsonNode? input, string? workingDirectory = null)
    {
        string? Str(string key) => ClaudeCodePermissionRequest.Str(input?[key]);
        string PathOf(string? raw)
        {
            if (raw is null) return "";
            if (workingDirectory is { Length: > 0 } cwd)
            {
                try
                {
                    var relative = System.IO.Path.GetRelativePath(cwd, raw);
                    if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative)) return relative;
                }
                catch (ArgumentException)
                {
                    // Not a path after all.
                }
            }
            return raw;
        }
        var text = tool switch
        {
            "Read" or "Write" or "Edit" or "MultiEdit" => PathOf(Str("file_path")),
            "NotebookEdit" => PathOf(Str("notebook_path")),
            "Bash" or "PowerShell" => Str("command") ?? "",
            "Glob" => Str("pattern") ?? "",
            "Grep" => (Str("pattern") ?? "") + (Str("path") is { } p ? $"  in {PathOf(p)}" : ""),
            "LS" => PathOf(Str("path")),
            "WebFetch" => Str("url") ?? "",
            "WebSearch" => Str("query") ?? "",
            "Task" or "Agent" => Str("description") ?? Str("prompt") ?? "",
            "Skill" => Str("skill") ?? Str("name") ?? "",
            "TodoWrite" => input?["todos"] is JsonArray todos ? $"{todos.Count} step{(todos.Count == 1 ? "" : "s")}" : "",
            "TaskCreate" => Str("subject") ?? "",
            "TaskUpdate" => (Str("subject") ?? $"task {Str("taskId")}") + (Str("status") is { } s ? $" → {s.Replace('_', ' ')}" : ""),
            "ExitPlanMode" => "the plan",
            _ => input is null ? "" : input.ToJsonString(),
        };
        return TextUtil.Prefix(text.Replace('\n', ' ').Trim(), 240);
    }

    /// <summary>The text a tool_result carries (a string, or text blocks).</summary>
    public static string ContentText(JsonNode? content)
    {
        if (content is JsonValue v && v.TryGetValue<string>(out var s)) return s;
        if (content is not JsonArray blocks) return "";
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (ClaudeCodePermissionRequest.Str(block?["type"]) == "text" && ClaudeCodePermissionRequest.Str(block?["text"]) is { } t)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(t);
            }
        }
        return sb.ToString();
    }

    /// <summary>The tool card's one-line headline once the call is done.</summary>
    public static string Summary(string tool, JsonNode? input, JsonNode? result, string content, bool isError, string? workingDirectory = null)
    {
        string FirstLine(string text) => TextUtil.Prefix(text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "", 160);
        if (isError)
        {
            var line = FirstLine(content.Replace("<tool_use_error>", "").Replace("</tool_use_error>", ""));
            return line.Length == 0 ? "Failed" : line;
        }
        var name = System.IO.Path.GetFileName(ClaudeCodePermissionRequest.Str(input?["file_path"]) ?? ClaudeCodePermissionRequest.Str(input?["notebook_path"]) ?? "");
        int? Num(JsonNode? n) => n is JsonValue value && value.TryGetValue<int>(out var i) ? i : null;
        switch (tool)
        {
            case "Read":
                return Num(result?["file"]?["numLines"]) is { } lines ? $"Read {lines} line{(lines == 1 ? "" : "s")} of {name}" : $"Read {name}";
            case "Write":
                return ClaudeCodePermissionRequest.Str(result?["type"]) == "update" ? $"Updated {name}" : $"Created {name}";
            case "Edit" or "MultiEdit":
            {
                var (added, removed) = PatchCounts(result?["structuredPatch"]);
                return added + removed > 0 ? $"Edited {name} (+{added} −{removed})" : $"Edited {name}";
            }
            case "NotebookEdit":
                return $"Edited {name}";
            case "Bash" or "PowerShell":
            {
                var output = ClaudeCodePermissionRequest.Str(result?["stdout"]) ?? content;
                if (result?["interrupted"] is JsonValue iv && iv.TryGetValue<bool>(out var interrupted) && interrupted) return "Interrupted";
                var first = FirstLine(output);
                return first.Length == 0 || first == "(Bash completed with no output)" ? "Done (no output)" : first;
            }
            case "Glob":
                return Num(result?["numFiles"]) is { } files ? $"{files} file{(files == 1 ? "" : "s")}" : FirstLine(content);
            case "Grep":
                return Num(result?["numMatches"]) is { } m ? $"{m} match{(m == 1 ? "" : "es")}"
                    : Num(result?["numFiles"]) is { } f ? $"{f} file{(f == 1 ? "" : "s")}" : FirstLine(content);
            case "TodoWrite" or "TaskCreate" or "TaskUpdate":
                return "Plan updated";
            case "ExitPlanMode":
                return "Plan presented";
            default:
                var line = FirstLine(content);
                return line.Length == 0 ? "Done" : line;
        }
    }

    private static (int Added, int Removed) PatchCounts(JsonNode? patch)
    {
        int added = 0, removed = 0;
        if (patch is not JsonArray hunks) return (0, 0);
        foreach (var hunk in hunks)
        {
            if (hunk?["lines"] is not JsonArray lines) continue;
            foreach (var line in lines)
            {
                var text = ClaudeCodePermissionRequest.Str(line) ?? "";
                if (text.StartsWith('+')) added++;
                else if (text.StartsWith('-')) removed++;
            }
        }
        return (added, removed);
    }

    /// <summary>The files a finished call created or changed (the editor reloads them; the turn's "files changed" list).</summary>
    public static IReadOnlyList<FileChange> Changes(string tool, JsonNode? input, JsonNode? result, Func<string, string> mapPath)
    {
        string? path = tool switch
        {
            "Write" or "Edit" or "MultiEdit" => ClaudeCodePermissionRequest.Str(result?["filePath"]) ?? ClaudeCodePermissionRequest.Str(input?["file_path"]),
            "NotebookEdit" => ClaudeCodePermissionRequest.Str(result?["notebook_path"]) ?? ClaudeCodePermissionRequest.Str(input?["notebook_path"]),
            _ => null,
        };
        if (path is null) return [];
        var kind = tool == "Write" && ClaudeCodePermissionRequest.Str(result?["type"]) == "create" ? FileChangeKind.Created : FileChangeKind.Modified;
        return [new FileChange(mapPath(path), kind)];
    }

    /// <summary>TodoWrite's list as Vision's plan steps.</summary>
    public static IReadOnlyList<TodoItem>? Todos(JsonNode? input)
    {
        if (input?["todos"] is not JsonArray todos) return null;
        var items = new List<TodoItem>();
        var n = 0;
        foreach (var todo in todos)
        {
            n++;
            var content = ClaudeCodePermissionRequest.Str(todo?["content"]) ?? ClaudeCodePermissionRequest.Str(todo?["subject"]) ?? "";
            if (content.Length == 0) continue;
            items.Add(new TodoItem(ClaudeCodePermissionRequest.Str(todo?["id"]) ?? n.ToString(CultureInfo.InvariantCulture), content,
                Status(ClaudeCodePermissionRequest.Str(todo?["status"]))));
        }
        return items;
    }

    internal static TodoStatus Status(string? raw) => raw switch
    {
        "in_progress" => TodoStatus.InProgress,
        "completed" => TodoStatus.Completed,
        _ => TodoStatus.Pending,
    };
}

// MARK: - The turn's result

/// <summary>What a turn's `result` line said. <see cref="TotalCostUsd"/> is the CLI's estimate for the whole conversation so
/// far (it adds up across --resume); <see cref="Usage"/> is this turn's tokens.</summary>
public sealed record ClaudeCodeResult(string Subtype, bool IsError, string Text)
{
    public double? TotalCostUsd { get; init; }
    public LlmUsage? Usage { get; init; }
    public int? NumTurns { get; init; }
    public long? DurationMs { get; init; }
    public int Denials { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string? TerminalReason { get; init; }
    public int? ContextWindow { get; init; }
    /// <summary>A command the CLI ran itself (/compact) instead of a model turn.</summary>
    public string? LocalCommand { get; init; }

    /// <summary>The turn ended because it was interrupted (Stop).</summary>
    public bool Interrupted => TerminalReason is { } reason && reason.StartsWith("aborted", StringComparison.Ordinal);
}

/// <summary>A subscription limit Claude Code reported.</summary>
public sealed record ClaudeCodeRateLimit(string Status, string? Type, double? Utilization, DateTimeOffset? ResetsAt)
{
    public string TypeWords => Type switch
    {
        "five_hour" => "five-hour",
        "seven_day" => "weekly",
        "seven_day_opus" => "weekly Opus",
        "seven_day_sonnet" => "weekly Sonnet",
        "overage" => "extra-usage",
        _ => "usage",
    };

    public string ResetWords => ResetsAt is { } at
        ? at.ToLocalTime().Date == DateTimeOffset.Now.Date ? $" — it resets at {at.ToLocalTime():HH:mm}" : $" — it resets {at.ToLocalTime():ddd d MMM, HH:mm}"
        : "";

    public int? Percent => Utilization is { } u ? (int)Math.Round(u <= 1 ? u * 100 : u) : null;
}

// MARK: - Reading the stream

/// <summary>Turns Claude Code's stream-json lines into Vision's engine events, and keeps what the turn said about itself
/// (its session id, model, last reply, usage, result). Only the main conversation becomes rows; a subagent's steps show
/// as the activity line. One instance per turn; feed it every stdout line in order.</summary>
public sealed class ClaudeCodeStream
{
    private readonly Action<EngineEvent> _sink;
    private readonly Action<string?>? _activity;
    private readonly Action<string>? _notice;
    private readonly Func<string, string> _mapPath;
    private readonly string? _workingDirectory;

    private sealed record PendingTool(string Name, JsonNode? Input);
    private readonly Dictionary<string, PendingTool> _tools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly List<TodoItem> _tasks = [];
    private readonly Dictionary<string, string> _taskCreates = new(StringComparer.Ordinal); // tool_use_id → subject
    private readonly HashSet<string> _limitWarnings = new(StringComparer.Ordinal);
    private bool _streamedThinking;
    private bool _planProposed;
    private int _retries;

    /// <param name="sink">Where the events go (the host marshals them to the UI).</param>
    /// <param name="workingDirectory">The chat's folder, so previews show project-relative paths.</param>
    /// <param name="activity">The working line ("Claude Code is compacting the conversation…"; null clears it).</param>
    /// <param name="notice">One-line notes for the chat (a compaction, a subscription limit getting close).</param>
    /// <param name="mapPath">Paths as Claude Code reports them → as Windows sees them (WSL's /mnt/c/…).</param>
    public ClaudeCodeStream(Action<EngineEvent> sink, string? workingDirectory = null, Action<string?>? activity = null,
                            Action<string>? notice = null, Func<string, string>? mapPath = null)
    {
        _sink = sink;
        _workingDirectory = workingDirectory;
        _activity = activity;
        _notice = notice;
        _mapPath = mapPath ?? (p => p);
    }

    public string? SessionId { get; private set; }
    /// <summary>The model the alias resolved to ("claude-sonnet-5-5").</summary>
    public string? Model { get; private set; }
    public string? Version { get; private set; }
    /// <summary>Where the CLI's credential came from: "none" for a claude.ai sign-in, "ANTHROPIC_API_KEY" for a key in the
    /// environment (billed to that key, not the subscription).</summary>
    public string? ApiKeySource { get; private set; }
    public string? PermissionMode { get; private set; }
    /// <summary>The last thing the main conversation said (what /goal reads its end marker from).</summary>
    public string LastReply { get; private set; } = "";
    /// <summary>How full the context was on the last model call (its input, cached and new).</summary>
    public int? LastPromptTokens { get; private set; }
    public ClaudeCodeResult? Result { get; private set; }
    /// <summary>The API error the CLI reported on an assistant message ("authentication_failed", "rate_limit"…), and its text.</summary>
    public string? ApiError { get; private set; }
    public string? ApiErrorText { get; private set; }
    public ClaudeCodeRateLimit? RateLimit { get; private set; }
    /// <summary>Something happened beyond the prompt: a reply, a tool call (the session holds this turn now).</summary>
    public bool Progressed { get; private set; }
    /// <summary>Lines that weren't JSON (a crash's stack trace on stdout, a shell's banner) — kept for the error message.</summary>
    public StringBuilder Unparsed { get; } = new();

    /// <summary>Read one stdout line. A permission question comes back to the caller (who answers it on stdin); every
    /// other control request is the caller's to refuse (<see cref="ClaudeCodeCommand.Unhandled"/>), reported here as
    /// <paramref name="unhandled"/>.</summary>
    public ClaudeCodePermissionRequest? Feed(string line, out (string RequestId, string Subtype)? unhandled)
    {
        unhandled = null;
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            if (Unparsed.Length < 4000) Unparsed.AppendLine(line);
            return null;
        }
        if (node is not JsonObject o) return null;
        var parent = Str(o["parent_tool_use_id"]);
        switch (Str(o["type"]))
        {
            case "system":
                OnSystem(o);
                break;
            case "stream_event" when parent is null:
                OnStreamEvent(o["event"]);
                break;
            case "assistant":
                if (parent is null) OnAssistant(o);
                else OnSubagent(o);
                break;
            case "user" when parent is null:
                OnUser(o);
                break;
            case "rate_limit_event":
                OnRateLimit(o["rate_limit_info"]);
                break;
            case "result":
                OnResult(o);
                break;
            case "control_request":
                if (ClaudeCodePermissionRequest.From(o) is { } permission) return permission;
                unhandled = (Str(o["request_id"]) ?? "", Str(o["request"]?["subtype"]) ?? "unknown");
                break;
        }
        return null;
    }

    public ClaudeCodePermissionRequest? Feed(string line) => Feed(line, out _);

    private void OnSystem(JsonObject o)
    {
        switch (Str(o["subtype"]))
        {
            case "init":
                SessionId = Str(o["session_id"]) ?? SessionId;
                Model = Str(o["model"]) ?? Model;
                Version = Str(o["claude_code_version"]) ?? Version;
                ApiKeySource = Str(o["apiKeySource"]) ?? ApiKeySource;
                PermissionMode = Str(o["permissionMode"]) ?? PermissionMode;
                break;
            case "status":
                _activity?.Invoke(Str(o["status"]) switch
                {
                    "compacting" => "Claude Code is compacting the conversation…",
                    "requesting" => "Waiting for Claude…",
                    _ => null,
                });
                break;
            case "api_retry":
            {
                _retries++;
                var attempt = Int(o["attempt"]) ?? _retries;
                var delay = TimeSpan.FromMilliseconds(Math.Max(0, Int(o["retry_delay_ms"]) ?? 0));
                var status = Int(o["error_status"]);
                var reason = (Str(o["error"]) ?? "error").Replace('_', ' ') + (status is { } s ? $", HTTP {s}" : "");
                _sink(new EngineEvent.Retrying(attempt, delay, $"Claude Code: {reason}"));
                break;
            }
            case "compact_boundary":
            {
                var meta = o["compact_metadata"];
                var before = Int(meta?["pre_tokens"]);
                var after = Int(meta?["post_tokens"]);
                var how = Str(meta?["trigger"]) == "auto" ? "Claude Code compacted the conversation to make room" : "Claude Code compacted the conversation";
                _notice?.Invoke(before is { } b && after is { } a ? $"{how}: ~{b:N0} → ~{a:N0} tokens." : how + ".");
                break;
            }
            case "informational":
                if (Str(o["level"]) is "warning" or "error" && Str(o["content"]) is { } content)
                    _notice?.Invoke("Claude Code: " + TextUtil.Prefix(content, 400));
                break;
        }
    }

    private void Recovered()
    {
        if (_retries == 0) return;
        _sink(new EngineEvent.Recovered(_retries));
        _retries = 0;
    }

    private void OnStreamEvent(JsonNode? e)
    {
        if (e is null) return;
        var type = Str(e["type"]);
        if (type == "message_start")
        {
            Recovered();
            return;
        }
        if (type != "content_block_delta") return;
        var delta = e["delta"];
        switch (Str(delta?["type"]))
        {
            case "text_delta" when Str(delta?["text"]) is { } text:
                _sink(new EngineEvent.TextDelta(text));
                break;
            case "thinking_delta" when Str(delta?["thinking"]) is { } thinking:
                _streamedThinking = true;
                _sink(new EngineEvent.ReasoningDelta(thinking));
                break;
        }
    }

    private void OnAssistant(JsonObject o)
    {
        Recovered();
        var message = o["message"];
        if (Str(o["error"]) is { } error && (o["is_api_error_message"] is JsonValue flag && flag.TryGetValue<bool>(out var isApi) && isApi
                                             || Str(message?["model"]) == "<synthetic>"))
        {
            // Not the model speaking: the CLI telling us its call failed ("Not logged in · Please run /login").
            ApiError = error;
            ApiErrorText = ClaudeCodeTools.ContentText(message?["content"]);
            return;
        }
        if (message?["usage"] is { } usage && Int(usage["input_tokens"]) is { } input)
            LastPromptTokens = input + (Int(usage["cache_read_input_tokens"]) ?? 0) + (Int(usage["cache_creation_input_tokens"]) ?? 0);
        if (message?["content"] is not JsonArray blocks) return;
        var id = Str(o["uuid"]) ?? Guid.NewGuid().ToString("N");
        foreach (var block in blocks)
        {
            switch (Str(block?["type"]))
            {
                case "text":
                {
                    var text = Str(block?["text"]) ?? "";
                    if (text.Length == 0) break;
                    Progressed = true;
                    LastReply = text;
                    _sink(new EngineEvent.AssistantMessage(id, text, []));
                    break;
                }
                case "thinking":
                    if (!_streamedThinking && Str(block?["thinking"]) is { Length: > 0 } thinking) _sink(new EngineEvent.ReasoningDelta(thinking));
                    _streamedThinking = false;
                    break;
                case "tool_use":
                    OnToolUse(Str(block?["id"]) ?? Guid.NewGuid().ToString("N"), Str(block?["name"]) ?? "tool", block?["input"]);
                    break;
            }
        }
    }

    private void OnToolUse(string id, string name, JsonNode? input)
    {
        Progressed = true;
        _tools[id] = new PendingTool(name, input?.DeepClone());
        _sink(new EngineEvent.ToolStarted(id, ClaudeCodeTools.VisionName(name), ClaudeCodeTools.Preview(name, input, _workingDirectory)));
        switch (name)
        {
            case "TodoWrite" when ClaudeCodeTools.Todos(input) is { } todos:
                _sink(new EngineEvent.Todos(todos));
                break;
            case "TaskCreate":
                _taskCreates[id] = Str(input?["subject"]) ?? Str(input?["description"]) ?? "Task";
                break;
            case "TaskUpdate" when Str(input?["taskId"]) is { } taskId:
            {
                var index = _tasks.FindIndex(t => t.Id == taskId);
                var status = Str(input?["status"]);
                if (status == "deleted")
                {
                    if (index >= 0) _tasks.RemoveAt(index);
                }
                else if (index >= 0)
                {
                    _tasks[index] = _tasks[index] with
                    {
                        Content = Str(input?["subject"]) ?? _tasks[index].Content,
                        Status = status is null ? _tasks[index].Status : ClaudeCodeTools.Status(status),
                    };
                }
                else
                {
                    _tasks.Add(new TodoItem(taskId, Str(input?["subject"]) ?? $"Task {taskId}", ClaudeCodeTools.Status(status)));
                }
                _sink(new EngineEvent.Todos(_tasks.ToList()));
                break;
            }
            case "ExitPlanMode" when Str(input?["plan"]) is { Length: > 0 } plan:
                _planProposed = true;
                _sink(new EngineEvent.PlanProposed(plan));
                break;
        }
    }

    private void OnSubagent(JsonObject o)
    {
        if (o["message"]?["content"] is not JsonArray blocks) return;
        foreach (var block in blocks)
        {
            if (Str(block?["type"]) != "tool_use") continue;
            var name = Str(block?["name"]) ?? "tool";
            _activity?.Invoke($"Subagent: {ClaudeCodeTools.Label(name)} {ClaudeCodeTools.Preview(name, block?["input"], _workingDirectory)}".TrimEnd());
        }
    }

    private void OnUser(JsonObject o)
    {
        if (o["message"]?["content"] is not JsonArray blocks) return;
        var structured = o["tool_use_result"];
        foreach (var block in blocks)
        {
            if (Str(block?["type"]) != "tool_result") continue;
            var id = Str(block?["tool_use_id"]) ?? "";
            var tool = _tools.GetValueOrDefault(id) ?? new PendingTool("tool", null);
            var isError = block?["is_error"] is JsonValue ev && ev.TryGetValue<bool>(out var err) && err;
            var content = ClaudeCodeTools.ContentText(block?["content"]);
            var result = structured is JsonObject ? structured : null;
            if (!isError && tool.Name == "TaskCreate" && Str(result?["task"]?["id"]) is { } taskId)
            {
                _tasks.Add(new TodoItem(taskId, Str(result?["task"]?["subject"]) ?? _taskCreates.GetValueOrDefault(id) ?? "Task"));
                _sink(new EngineEvent.Todos(_tasks.ToList()));
            }
            if (!isError && tool.Name == "ExitPlanMode" && !_planProposed && Str(result?["plan"]) is { Length: > 0 } plan)
            {
                _planProposed = true;
                _sink(new EngineEvent.PlanProposed(plan));
            }
            if (_finished.Add(id))
            {
                _sink(new EngineEvent.ToolFinished(id, ClaudeCodeTools.VisionName(tool.Name), !isError,
                    ClaudeCodeTools.Summary(tool.Name, tool.Input, result, content, isError, _workingDirectory), TextUtil.Prefix(content, 20_000)));
                if (Images(block?["content"]) is { Count: > 0 } images) _sink(new EngineEvent.ToolImages(id, images));
                if (!isError && ClaudeCodeTools.Changes(tool.Name, tool.Input, result, _mapPath) is { Count: > 0 } changes)
                    _sink(new EngineEvent.FilesChanged(changes));
            }
        }
    }

    private static IReadOnlyList<MessageAttachment> Images(JsonNode? content)
    {
        if (content is not JsonArray blocks) return [];
        var images = new List<MessageAttachment>();
        foreach (var block in blocks)
        {
            if (Str(block?["type"]) != "image" || Str(block?["source"]?["data"]) is not { } data) continue;
            try
            {
                var mime = Str(block?["source"]?["media_type"]) ?? "image/png";
                images.Add(new MessageAttachment(AttachmentKind.Image, "image." + mime.Split('/').Last(), Convert.FromBase64String(data)));
            }
            catch (FormatException)
            {
                // Not an image after all.
            }
        }
        return images;
    }

    private void OnRateLimit(JsonNode? info)
    {
        var status = Str(info?["status"]);
        if (status is null or "allowed") return;
        double? utilization = info?["utilization"] is JsonValue u && u.TryGetValue<double>(out var d) ? d : null;
        DateTimeOffset? resets = info?["resetsAt"] is JsonValue r && r.TryGetValue<double>(out var epoch) && epoch > 0
            ? DateTimeOffset.FromUnixTimeSeconds((long)epoch) : null;
        var limit = new ClaudeCodeRateLimit(status, Str(info?["rateLimitType"]), utilization, resets);
        if (status == "rejected")
        {
            RateLimit = limit;
            return;
        }
        if (!_limitWarnings.Add(limit.TypeWords)) return;
        _notice?.Invoke($"Your Claude plan: {(limit.Percent is { } p ? $"{p}% of" : "close to")} your {limit.TypeWords} limit used{limit.ResetWords}.");
    }

    private void OnResult(JsonObject o)
    {
        SessionId = Str(o["session_id"]) ?? SessionId;
        LlmUsage? usage = null;
        if (o["usage"] is JsonObject u && Int(u["input_tokens"]) is { } input)
        {
            var cacheRead = Int(u["cache_read_input_tokens"]) ?? 0;
            var cacheWrite = Int(u["cache_creation_input_tokens"]) ?? 0;
            usage = new LlmUsage(input + cacheRead + cacheWrite, Int(u["output_tokens"]) ?? 0, cacheRead);
        }
        int? window = null;
        if (o["modelUsage"] is JsonObject models)
        {
            var mine = Model is not null && models[Model] is JsonObject own ? own : null;
            window = Int(mine?["contextWindow"]) ?? models.Select(m => Int(m.Value?["contextWindow"]) ?? 0).DefaultIfEmpty(0).Max();
            if (window <= 0) window = null;
        }
        var errors = o["errors"] is JsonArray list ? list.Select(e => Str(e) ?? "").Where(e => e.Length > 0).ToList() : [];
        var denials = o["permission_denials"] is JsonArray d ? d.Count : 0;
        Result = new ClaudeCodeResult(Str(o["subtype"]) ?? "", o["is_error"] is JsonValue ie && ie.TryGetValue<bool>(out var isError) && isError,
            Str(o["result"]) ?? "")
        {
            TotalCostUsd = o["total_cost_usd"] is JsonValue c && c.TryGetValue<double>(out var cost) ? cost : null,
            Usage = usage,
            NumTurns = Int(o["num_turns"]),
            DurationMs = o["duration_ms"] is JsonValue dm && dm.TryGetValue<long>(out var ms) ? ms : null,
            Denials = denials,
            Errors = errors,
            TerminalReason = Str(o["terminal_reason"]),
            ContextWindow = window,
            LocalCommand = Str(o["local_command"]),
        };
        if (!Result.IsError && Result.Text.Length > 0 && LastReply.Length == 0) LastReply = Result.Text;
        _activity?.Invoke(null);
        if (!Result.IsError) _sink(new EngineEvent.Finished(usage));
    }

    /// <summary>Tool calls that never got a result (the turn was stopped, or the CLI died): close their cards.</summary>
    public void CloseDangling(string why)
    {
        foreach (var (id, tool) in _tools)
        {
            if (!_finished.Add(id)) continue;
            _sink(new EngineEvent.ToolFinished(id, ClaudeCodeTools.VisionName(tool.Name), false, why, why));
        }
    }

    private static string? Str(JsonNode? node) => ClaudeCodePermissionRequest.Str(node);

    private static int? Int(JsonNode? node)
    {
        if (node is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (value.TryGetValue<double>(out var d)) return (int)d;
        return null;
    }
}
