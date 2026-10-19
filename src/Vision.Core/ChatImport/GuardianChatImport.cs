using System.Text.Json;
using System.Text.Json.Nodes;

namespace Vision.Core;

/// <summary>The person's chats in Guardian, as Vision chats: read a page at a time from the newest back
/// (GET /api/chats/:id?messages=latest, then GET /api/chats/:id/messages?before=…), each message turned into rows — a USER
/// message is a prompt (with its attachments named), an ASSISTANT message is its tool calls (<c>toolCalls[]</c>: name, args,
/// output, status, startedAt) followed by its reply. And the Vision chats saved in Guardian (GET /api/harness/chats), which
/// keep their id.</summary>
public static class GuardianChatImport
{
    /// <summary>Messages asked for per page (Guardian's maximum).</summary>
    public const int PageSize = 200;
    /// <summary>A transcript is read up to this many pages (100,000 messages).</summary>
    public const int MaxPages = 500;

    public static async Task<ImportedChat?> ReadAsync(GuardianClient client, GuardianWebChat chat, CancellationToken cancellationToken = default)
    {
        var pages = new List<IReadOnlyList<JsonObject>>();
        string? before = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var answer = await client.WebChatPageAsync(chat.Id, before, PageSize, cancellationToken).ConfigureAwait(false);
            pages.Add(answer.Messages);
            if (!answer.HasMore || answer.Before is null || answer.Before == before || answer.Messages.Count == 0) break;
            before = answer.Before;
        }
        pages.Reverse();
        return Convert(chat, pages.SelectMany(p => p).ToList(), client.Address);
    }

    /// <summary>How many messages a chat has, from one tiny page (the newest message and the count of older ones).</summary>
    public static async Task<int?> CountAsync(GuardianClient client, string chatId, CancellationToken cancellationToken = default)
    {
        var page = await client.WebChatPageAsync(chatId, null, 1, cancellationToken).ConfigureAwait(false);
        // A Guardian without paging sends every message: then the page is the count.
        return page.OlderCount is { } older ? older + page.Messages.Count : page.HasMore ? null : page.Messages.Count;
    }

    /// <summary>A transcript (oldest message first) as an imported chat.</summary>
    public static ImportedChat? Convert(GuardianWebChat chat, IReadOnlyList<JsonObject> messages, string from)
    {
        var rows = new ChatRowBuilder(chat.CreatedAt ?? chat.UpdatedAt);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? model = chat.ModelId;
        foreach (var message in messages)
        {
            // Pages meet at their cursor; a message read twice is used once.
            if (GuardianJson.Text(message["id"]) is { } id && !seen.Add(id)) continue;
            var at = GuardianJson.Date(message["createdAt"]);
            var content = GuardianJson.Text(message["content"]) ?? "";
            switch (GuardianJson.Text(message["role"])?.ToUpperInvariant())
            {
                case "USER":
                    var names = (message["attachments"] as JsonArray ?? []).OfType<JsonObject>()
                        .Select(a => GuardianJson.Text(a["attachment"]?["name"]) ?? GuardianJson.Text(a["name"]))
                        .OfType<string>()
                        .Select(name => $"[Attached: {name}]");
                    rows.User(string.Join("\n\n", new[] { content }.Concat(names).Where(p => p.Length > 0)), at);
                    break;
                case "ASSISTANT":
                    model = GuardianJson.Text(message["modelId"]) ?? model;
                    foreach (var call in (message["toolCalls"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        var callId = GuardianJson.Text(call["id"]);
                        var name = GuardianJson.Text(call["name"]) ?? "tool";
                        rows.ToolCall(callId, name, Arguments(call["args"] ?? call["input"]), GuardianJson.Date(call["startedAt"]) ?? at);
                        if (call["output"] is not null || GuardianJson.Text(call["status"]) is "done" or "error")
                            rows.ToolResult(callId, name, GuardianJson.Text(call["output"]) ?? "", GuardianJson.Text(call["status"]) == "error",
                                GuardianJson.Date(call["completedAt"]) ?? at);
                    }
                    rows.Assistant(content, at);
                    break;
                case "TOOL":
                    rows.ToolResult(null, "tool", content, false, at);
                    break;
            }
        }
        if (rows.Messages == 0) return null;
        return new ImportedChat
        {
            Source = ChatSource.Guardian,
            SourceId = chat.Id,
            Title = ChatRowBuilder.OneLine(chat.Title, 120),
            Rows = rows.Rows,
            Model = model,
            StartedAt = chat.CreatedAt ?? rows.First,
            UpdatedAt = rows.Last ?? chat.UpdatedAt,
            From = from,
            Notes = chat.Remark is { } remark ? [remark] : [],
        };
    }

    private static string Arguments(JsonNode? args)
    {
        if (args is null) return "";
        using var doc = JsonDocument.Parse(args.ToJsonString());
        return ChatRowBuilder.Arguments(doc.RootElement);
    }

    /// <summary>A Vision chat saved in Guardian (POST /api/harness/chats/:id by either harness), keeping its id (and, for one
    /// Vision had imported, where it really came from).</summary>
    public static async Task<ImportedChat?> ReadSavedAsync(GuardianClient client, GuardianChatSummary summary, CancellationToken cancellationToken = default)
    {
        var payload = await client.ChatAsync(summary.Id, cancellationToken).ConfigureAwait(false);
        return ChatExportFiles.VisionChat(payload, client.Address, summary.Id, summary.UpdatedAt > DateTimeOffset.MinValue ? summary.UpdatedAt : null);
    }
}
