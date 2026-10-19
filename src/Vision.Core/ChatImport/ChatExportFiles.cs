using System.IO.Compression;
using System.Text.Json;

namespace Vision.Core;

/// <summary>Chats in a file someone chose: a Claude.ai or ChatGPT data export (the .zip, or its conversations.json), a
/// Vision chat exported as JSON, or one session file of Claude Code, Codex or OpenClaw (.jsonl).</summary>
public static class ChatExportFiles
{
    /// <summary>A conversations.json bigger than this isn't read (an export is mostly text; this is generous).</summary>
    public const long MaxJsonBytes = 1_500_000_000;

    /// <summary>The chats in <paramref name="path"/>, or a sentence saying why there are none.</summary>
    public static (IReadOnlyList<ImportedChat> Chats, string? Problem) Read(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".zip") return ReadZip(path, cancellationToken);
            if (extension == ".jsonl") return ReadSessionFile(path, cancellationToken);
            using var stream = File.OpenRead(path);
            return ReadJson(stream, path, cancellationToken);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return ([], $"Couldn't read {Path.GetFileName(path)}: {error.Message}");
        }
    }

    private static (IReadOnlyList<ImportedChat>, string?) ReadZip(string path, CancellationToken cancellationToken)
    {
        using var zip = ZipFile.OpenRead(path);
        var entries = zip.Entries
            .Where(e => Path.GetFileName(e.FullName) is var name && name.StartsWith("conversations", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();
        if (entries.Count == 0)
            return ([], $"{Path.GetFileName(path)} has no conversations.json — choose the .zip Claude.ai or ChatGPT emailed you (Settings › Data controls › Export).");
        var chats = new List<ImportedChat>();
        string? problem = null;
        foreach (var entry in entries)
        {
            if (entry.Length > MaxJsonBytes)
            {
                problem = $"{entry.FullName} is too big to read.";
                continue;
            }
            using var stream = entry.Open();
            var (found, why) = ReadJson(stream, path, cancellationToken);
            chats.AddRange(found);
            problem ??= found.Count == 0 ? why : null;
        }
        return (chats, chats.Count == 0 ? problem ?? "That export has no conversations." : null);
    }

    private static (IReadOnlyList<ImportedChat>, string?) ReadSessionFile(string path, CancellationToken cancellationToken)
    {
        // Which tool wrote it shows in its first lines.
        string? kind = null;
        try
        {
            using var reader = new StreamReader(path);
            for (var i = 0; i < 20 && kind is null && reader.ReadLine() is { } line; i++)
            {
                if (line.Contains("\"session_meta\"", StringComparison.Ordinal) || line.Contains("\"response_item\"", StringComparison.Ordinal)) kind = "codex";
                else if (line.Contains("\"parentUuid\"", StringComparison.Ordinal) || line.Contains("\"sessionId\"", StringComparison.Ordinal)) kind = "claude";
                else if (line.Contains("\"type\":\"session\"", StringComparison.Ordinal) || line.Contains("\"parentId\"", StringComparison.Ordinal)) kind = "openclaw";
            }
        }
        catch (IOException error)
        {
            return ([], error.Message);
        }
        var chat = kind switch
        {
            "codex" => CodexChats.Read(path, cancellationToken: cancellationToken),
            "claude" => ClaudeCodeChats.Read(path, cancellationToken: cancellationToken),
            "openclaw" => OpenClawChats.ReadJsonl(path, cancellationToken: cancellationToken),
            _ => null,
        };
        return chat is null ? ([], $"{Path.GetFileName(path)} isn't a Claude Code, Codex or OpenClaw session with messages in it.") : ([chat], null);
    }

    /// <summary>A JSON document: a Claude.ai or ChatGPT conversations list, or Vision chats.</summary>
    internal static (IReadOnlyList<ImportedChat>, string?) ReadJson(Stream stream, string from, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 512 });
        var root = doc.RootElement;
        var chats = new List<ImportedChat>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var conversation in root.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chat = conversation.Get("chat_messages").ValueKind == JsonValueKind.Array ? ClaudeAi(conversation, from)
                    : conversation.Get("mapping").ValueKind == JsonValueKind.Object ? ChatGpt(conversation, from)
                    : conversation.Get("rows").ValueKind == JsonValueKind.Array ? VisionChat(conversation, from)
                    : null;
                if (chat is not null) chats.Add(chat);
            }
        }
        else if (root.Get("rows").ValueKind == JsonValueKind.Array)
        {
            if (VisionChat(root, from) is { } chat) chats.Add(chat);
        }
        else if (root.Get("chats").ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.Get("chats").EnumerateArray())
                if (VisionChat(item, from) is { } chat) chats.Add(chat);
        }
        return (chats, chats.Count == 0 ? "No conversations in that file (it isn't a Claude.ai, ChatGPT or Vision export)." : null);
    }

    // MARK: - Claude.ai

    /// <summary>One conversation of a Claude.ai export: {uuid, name, created_at, updated_at, chat_messages:[{uuid, sender:
    /// human|assistant, text, content:[text|tool_use|tool_result|thinking], attachments:[{file_name, extracted_content}],
    /// files:[{file_name}], created_at}]}.</summary>
    internal static ImportedChat? ClaudeAi(JsonElement conversation, string from)
    {
        var id = conversation.Str("uuid");
        if (string.IsNullOrEmpty(id)) return null;
        var created = ChatRowBuilder.Time(conversation.Get("created_at"));
        var updated = ChatRowBuilder.Time(conversation.Get("updated_at"));
        var rows = new ChatRowBuilder(created ?? updated ?? DateTimeOffset.UtcNow);
        foreach (var message in conversation.Get("chat_messages").Items())
        {
            var at = ChatRowBuilder.Time(message.Get("created_at"));
            var content = message.Get("content");
            if (message.Str("sender") == "human")
            {
                var parts = new List<string> { content.ValueKind == JsonValueKind.Array && JsonRead.Text(content) is { Length: > 0 } t ? t : message.Str("text") ?? "" };
                foreach (var attachment in message.Get("attachments").Items())
                {
                    var fileName = attachment.Str("file_name") ?? "attachment";
                    var extracted = attachment.Str("extracted_content");
                    parts.Add(string.IsNullOrEmpty(extracted) ? $"[Attached: {fileName}]" : $"[Attached: {fileName}]\n{ChatRowBuilder.Cap(extracted, 50_000)}");
                }
                foreach (var file in message.Get("files").Items()) parts.Add($"[File: {file.Str("file_name") ?? "file"}]");
                rows.User(string.Join("\n\n", parts.Where(p => p.Length > 0)), at);
                continue;
            }
            var key = message.Str("uuid");
            var any = false;
            foreach (var block in content.Items())
            {
                switch (block.Str("type"))
                {
                    case "text":
                        rows.Assistant(block.Str("text"), at, key);
                        any = true;
                        break;
                    case "tool_use":
                        var tool = block.Str("name") ?? "tool";
                        var input = block.Get("input");
                        if (tool == "artifacts")
                        {
                            // An artifact's content is what the reply made: keep it as the call's output.
                            rows.ToolCall(block.Str("id"), tool, input.Str("title") ?? input.Str("id") ?? input.Str("command"), at);
                            rows.ToolResult(block.Str("id"), tool, input.Str("content") ?? "", false, at);
                        }
                        else rows.ToolCall(block.Str("id"), tool, ChatRowBuilder.Arguments(input), at);
                        any = true;
                        break;
                    case "tool_result" when block.Str("name") != "artifacts": // an artifact's "OK"; its content is already the output
                        rows.ToolResult(block.Str("tool_use_id"), block.Str("name"), JsonRead.Text(block.Get("content")), block.Flag("is_error"), at);
                        break;
                }
            }
            if (!any) rows.Assistant(message.Str("text"), at, key);
        }
        if (rows.Messages == 0) return null;
        return new ImportedChat
        {
            Source = ChatSource.ClaudeAi,
            SourceId = id,
            Title = conversation.Str("name") is { Length: > 0 } name ? ChatRowBuilder.OneLine(name, 120) : ChatRowBuilder.TitleFrom(rows.FirstPrompt, "Claude.ai chat"),
            Rows = rows.Rows,
            StartedAt = created ?? rows.First,
            UpdatedAt = rows.Last ?? updated ?? DateTimeOffset.UtcNow,
            From = from,
        };
    }

    // MARK: - ChatGPT

    /// <summary>One conversation of a ChatGPT export: {title, create_time, update_time, conversation_id|id, current_node,
    /// mapping:{id:{message, parent, children}}} — the conversation is the path from the current node back to the root
    /// (edited and regenerated branches are left out).</summary>
    internal static ImportedChat? ChatGpt(JsonElement conversation, string from)
    {
        var id = conversation.Str("conversation_id") ?? conversation.Str("id");
        var mapping = conversation.Get("mapping");
        if (string.IsNullOrEmpty(id) || mapping.ValueKind != JsonValueKind.Object) return null;
        var created = ChatRowBuilder.Time(conversation.Get("create_time"));
        var updated = ChatRowBuilder.Time(conversation.Get("update_time"));

        // The path: from the current node (else the last leaf) to the root.
        var current = conversation.Str("current_node");
        if (current is null || !mapping.TryGetProperty(current, out _))
            current = mapping.EnumerateObject().Where(n => !n.Value.Get("children").Items().Any()).Select(n => n.Name).LastOrDefault();
        var path = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var node = current; node is not null && seen.Add(node) && mapping.TryGetProperty(node, out var entry); node = entry.Str("parent"))
            path.Add(entry);
        path.Reverse();

        var rows = new ChatRowBuilder(created ?? updated ?? DateTimeOffset.UtcNow);
        string? model = conversation.Str("default_model_slug");
        foreach (var node in path)
        {
            var message = node.Get("message");
            if (message.ValueKind != JsonValueKind.Object) continue;
            if (message.Get("metadata").Flag("is_visually_hidden_from_conversation")) continue;
            var at = ChatRowBuilder.Time(message.Get("create_time"));
            var content = message.Get("content");
            var type = content.Str("content_type");
            if (type is "user_editable_context" or "thoughts" or "reasoning_recap" or "model_editable_context") continue;
            var text = ChatGptText(content);
            switch (message.Get("author").Str("role"))
            {
                case "user":
                    rows.User(text, at);
                    break;
                case "assistant":
                    model = message.Get("metadata").Str("model_slug") ?? model;
                    var recipient = message.Str("recipient");
                    if (recipient is { Length: > 0 } and not "all") rows.ToolCall(null, recipient, ChatRowBuilder.OneLine(text, ChatRowBuilder.MaxArgSummary), at);
                    else rows.Assistant(text, at, message.Str("id"));
                    break;
                case "tool":
                    rows.ToolResult(null, message.Get("author").Str("name") ?? "tool", text, false, at);
                    break;
            }
        }
        if (rows.Messages == 0) return null;
        return new ImportedChat
        {
            Source = ChatSource.ChatGpt,
            SourceId = id,
            Title = conversation.Str("title") is { Length: > 0 } title ? ChatRowBuilder.OneLine(title, 120) : ChatRowBuilder.TitleFrom(rows.FirstPrompt, "ChatGPT chat"),
            Rows = rows.Rows,
            Model = model,
            StartedAt = created ?? rows.First,
            UpdatedAt = rows.Last ?? updated ?? DateTimeOffset.UtcNow,
            From = from,
        };
    }

    /// <summary>A ChatGPT message's text: its parts (strings; an image or audio part as a placeholder), or the text a code,
    /// execution or quote message carries.</summary>
    private static string ChatGptText(JsonElement content)
    {
        var parts = new List<string>();
        foreach (var part in content.Get("parts").Items())
        {
            if (part.ValueKind == JsonValueKind.String) parts.Add(part.GetString() ?? "");
            else if (part.ValueKind == JsonValueKind.Object)
            {
                var kind = part.Str("content_type") ?? "";
                if (kind.Contains("image", StringComparison.Ordinal)) parts.Add("[image]");
                else if (kind.Contains("audio", StringComparison.Ordinal)) parts.Add(part.Str("text") is { Length: > 0 } spoken ? spoken : "[audio]");
                else if (part.Str("text") is { } text) parts.Add(text);
            }
        }
        if (parts.Count > 0) return string.Join("\n\n", parts.Where(p => p.Length > 0));
        return content.Str("text") ?? content.Str("result") ?? content.Str("title") ?? "";
    }

    // MARK: - Vision

    /// <summary>A chat in the shape both Vision harnesses export and keep in Guardian ({id, title, cwd, rows}, times as ISO text
    /// or Apple reference-date seconds). It keeps its id; one Vision imported keeps where it really came from
    /// (<c>source</c> + <c>sourceId</c>), so the same Claude Code session imported on two computers is one chat.</summary>
    internal static ImportedChat? VisionChat(JsonElement chat, string from) => VisionChat(chat.GetRawText(), from);

    internal static ImportedChat? VisionChat(string json, string from, string? fallbackId = null, DateTimeOffset? updated = null)
    {
        if (GuardianChats.FromPayload(json, fallbackId) is not { } transcript || transcript.Rows.Count == 0) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var origin = ChatSources.FromRaw(root.Str("source"));
        var originId = root.Str("sourceId");
        var source = origin is { } known && known != ChatSource.Vision && !string.IsNullOrEmpty(originId) ? known : ChatSource.Vision;
        var rows = transcript.Rows.Select((r, i) => r with { SessionId = "", Seq = i }).ToList();
        return new ImportedChat
        {
            Source = source,
            SourceId = source == ChatSource.Vision ? transcript.Id : originId!,
            KeepId = transcript.Id,
            Title = transcript.Title,
            Rows = rows,
            Project = transcript.Cwd,
            StartedAt = ChatRowBuilder.Time(root.Get("createdAt")) ?? rows[0].At,
            UpdatedAt = ChatRowBuilder.Time(root.Get("updatedAt")) ?? updated ?? rows.Max(r => r.At),
            From = from,
            Notes = source != ChatSource.Vision ? [$"Originally from {source.Label()}"] : [],
        };
    }
}
