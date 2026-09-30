using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - memory_save

public sealed class MemorySaveTool(MemoryStore store) : IToolExecutor
{
    public const string ToolName = "memory_save";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Save a note to long-term memory so it is remembered in future chats. Use it for durable, non-obvious facts: the user's preferences, a decision and its reason, a project convention, a fix that keeps being needed. Keep it short and specific, one fact per note. Never save secrets — put credentials in the vault and note only where to find them. A note that repeats an existing one updates it. Preferences are remembered everywhere; other kinds belong to the current project unless scope is 'global'.",
        """{"type":"object","properties":{"title":{"type":"string","description":"A short label (a few words)"},"content":{"type":"string","description":"The fact itself, in a sentence or two"},"kind":{"type":"string","enum":["preference","fact","decision","procedure","reference","note"]},"scope":{"type":"string","enum":["project","global"],"description":"project = only in this project folder; global = in every chat"},"tags":{"type":"array","items":{"type":"string"},"description":"A few keywords to find it by"}},"required":["title","content"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var title = (JsonArgs.String(args, "title") ?? "").Trim();
        var content = (JsonArgs.String(args, "content") ?? "").Trim();
        if (content.Length == 0) return Task.FromResult<ToolResult>("Error: content is required.");
        var tags = args["tags"] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "").Where(s => s.Length > 0).ToList()
            : [];
        if (SecretGuard.LooksLikeSecret(title + ": " + content + " " + string.Join(' ', tags)))
            return Task.FromResult<ToolResult>("Error: that looks like a credential, and memory is stored as plain text. Add it to the Credentials Vault instead (the user does that, Ctrl+Shift+K) and save only a note about what it is for and where it lives.");

        var kind = MemoryKinds.Normalize(JsonArgs.String(args, "kind"));
        var scope = (JsonArgs.String(args, "scope") ?? "").Trim().ToLowerInvariant();
        var global = scope == "global" || (scope != "project" && MemoryKinds.GlobalByDefault(kind)) || IsHome(context.Workspace);
        // Pinning puts a note in every future system prompt: that is the user's call (the notes window), never the agent's.
        var result = store.Save(new MemoryDraft
        {
            Title = title,
            Body = content,
            Tags = tags,
            Kind = kind,
            Project = global ? null : context.Workspace,
            Source = "agent",
        });
        var verb = result.Kind == MemorySaveKind.Created ? "Saved" : "Updated";
        var where = result.Item.Project is null ? "everywhere" : "this project";
        return Task.FromResult<ToolResult>($"{verb} note [{result.Item.Id}] “{result.Item.Title}” ({result.Item.Kind}, {where}).");
    }

    private static bool IsHome(string workspace)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 && string.Equals(MemoryStore.NormalizeProject(workspace), MemoryStore.NormalizeProject(home), StringComparison.OrdinalIgnoreCase);
    }
}

// MARK: - memory_search

public sealed class MemorySearchTool(MemoryStore store) : IToolExecutor
{
    public const string ToolName = "memory_search";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Search long-term memory for notes about something (a preference, a past decision, how a task was done before). Relevant notes are already attached to each message automatically; use this when you want to look something up on purpose.",
        """{"type":"object","properties":{"query":{"type":"string","description":"What to look for"},"limit":{"type":"integer","description":"How many notes at most (default 5)"}},"required":["query"]}""");

    public Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var args = JsonArgs.Object(arguments);
        var query = (JsonArgs.String(args, "query") ?? "").Trim();
        if (query.Length == 0) return Task.FromResult<ToolResult>("Error: query is required.");
        var limit = Math.Clamp(JsonArgs.Int(args, "limit", 5), 1, 15);
        var hits = store.Search(query, new MemoryQueryOptions { Project = context.Workspace, Limit = limit, IncludePinned = true, MinRelevance = 0.08 });
        if (hits.Count == 0) return Task.FromResult<ToolResult>($"No saved notes match “{query}”.");
        store.NoteUsed(hits.Select(h => h.Item.Id));
        var text = new StringBuilder($"{hits.Count} note{(hits.Count == 1 ? "" : "s")}:\n");
        foreach (var hit in hits)
        {
            var item = hit.Item;
            var scope = item.Project is null ? "global" : "this project";
            text.Append($"- [{item.Id}] ({item.Kind}, {scope}, saved {item.UpdatedAt:yyyy-MM-dd}) {MemoryRecall.Neutralize(item.Title)}\n");
            if (item.Body.Length > 0) text.Append("  ").Append(MemoryRecall.Neutralize(MemoryText.Clip(item.Body, 700))).Append('\n');
        }
        return Task.FromResult<ToolResult>(text.ToString().TrimEnd());
    }
}

// MARK: - memory_forget

public sealed class MemoryForgetTool(MemoryStore store) : IToolExecutor
{
    public const string ToolName = "memory_forget";
    public string Name => ToolName;
    public ToolSpec Spec { get; } = new(ToolName,
        "Delete a note from long-term memory, by the id shown in brackets when it was recalled or searched (e.g. a1b2c3d4). Use it when a note turns out to be wrong or out of date.",
        """{"type":"object","properties":{"id":{"type":"string","description":"The note id, e.g. a1b2c3d4"}},"required":["id"]}""");

    public async Task<ToolResult> ExecuteAsync(string arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var id = (JsonArgs.String(arguments, "id") ?? "").Trim();
        if (id.Length == 0) return "Error: id is required.";
        var item = store.Get(id);
        if (item is null) return $"Error: there is no note {id}. memory_search lists ids.";
        // A note the agent wrote it may take back. One the user wrote, pinned, or that came from a file or an import, is theirs to
        // delete — and text the agent read on the web must not be able to talk it into wiping them.
        if ((item.Source != "agent" || item.Pinned)
            && !await context.RequestPermission($"forget-{item.Id}", ToolName, $"Forget the note “{TextUtil.Prefix(item.Title, 80)}”?").ConfigureAwait(false))
            return $"Not forgotten: [{item.Id}] “{item.Title}” was not written by the agent and the user did not agree.";
        // (An "Allow" that arrives after the call was given up on — the tool timeout — must not delete anything now.)
        cancellationToken.ThrowIfCancellationRequested();
        store.Delete(item.Id);
        return $"Forgot [{item.Id}] “{item.Title}”.";
    }
}

/// <summary>The three memory tools over one store.</summary>
public static class MemoryTools
{
    public static IReadOnlyList<IToolExecutor> All(MemoryStore store) =>
        [new MemorySaveTool(store), new MemorySearchTool(store), new MemoryForgetTool(store)];
}
