using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Core;

/// <summary>One stored transcript row. Kind is user | assistant | tool | notice | error | compaction.</summary>
public sealed record LogItemRow(string SessionId, int Seq, string Kind, string? Text, string? ToolName,
                                string? ArgSummary, string? Output, bool IsError, DateTimeOffset At)
{
    public string Id => $"{SessionId}:{Seq}";
}

/// <summary>Stored session metadata.</summary>
public sealed record LogSession
{
    public required string Id { get; init; }
    public string Title { get; set; } = "New chat";
    public string? Cwd { get; set; }
    public string? Preset { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Per-session conversation store: one JSON file per session under
/// %APPDATA%\DSH\conversations. Plain files, so a search tool (or grep) works on them directly.
/// Not thread-safe: use it from the UI thread.</summary>
public sealed class ConversationLog
{
    private readonly string _dir;
    private readonly Dictionary<string, LogSession> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<LogItemRow>> _items = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public ConversationLog(string? directory = null)
    {
        _dir = directory ?? AppPaths.Conversations;
        try
        {
            Directory.CreateDirectory(_dir);
            foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<SessionFile>(File.ReadAllText(file), Json);
                    if (stored?.Session is null) continue;
                    _rows[stored.Session.Id] = stored.Session;
                    _items[stored.Session.Id] = (stored.Items ?? [])
                        .Select(i => new LogItemRow(stored.Session.Id, i.Seq, i.Kind, i.Text, i.ToolName, i.ArgSummary,
                            i.Output, i.IsError, i.At))
                        .ToList();
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // A damaged file is skipped, not fatal.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No store: the app still runs, it just can't remember.
        }
    }

    public string StorageDirectory => _dir;

    private string FilePath(string id) => Path.Combine(_dir, $"{id}.json");

    public void Upsert(string id, string? cwd, string title, string? preset)
    {
        if (!_rows.TryGetValue(id, out var row))
        {
            row = new LogSession { Id = id, Title = title, Cwd = cwd, Preset = preset, UpdatedAt = DateTimeOffset.Now };
            _rows[id] = row;
        }
        row.Title = title;
        row.UpdatedAt = DateTimeOffset.Now;
        if (!string.IsNullOrEmpty(cwd)) row.Cwd = cwd;
        if (preset is not null) row.Preset = preset;
        Persist(id);
    }

    public IReadOnlyList<LogSession> List() => _rows.Values.OrderByDescending(r => r.UpdatedAt).ToList();

    public IReadOnlyList<LogItemRow> LoadItems(string id) => _items.TryGetValue(id, out var list) ? list : [];

    public void RecordItem(string id, string kind, string? text, string? toolName = null, string? argSummary = null,
                           string? output = null, bool isError = false)
    {
        if (!_items.TryGetValue(id, out var list)) _items[id] = list = [];
        list.Add(new LogItemRow(id, list.Count, kind, text, toolName, argSummary, output, isError, DateTimeOffset.Now));
        Persist(id);
    }

    public void Delete(string id)
    {
        _rows.Remove(id);
        _items.Remove(id);
        try { File.Delete(FilePath(id)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Replace a session's stored items wholesale (after compaction, so the persisted log
    /// tracks the compacted transcript exactly).</summary>
    public void Resync(string id, IReadOnlyList<LogItemRow> rows)
    {
        _items[id] = rows.ToList();
        Persist(id);
    }

    public void Touch(string id, string? title = null)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        if (title is not null) row.Title = title;
        row.UpdatedAt = DateTimeOffset.Now;
        Persist(id);
    }

    private void Persist(string id)
    {
        if (!_rows.TryGetValue(id, out var row)) return;
        var file = new SessionFile
        {
            Session = row,
            Items = (_items.GetValueOrDefault(id) ?? []).Select(i => new StoredItem
            {
                Seq = i.Seq,
                Kind = i.Kind,
                Text = i.Text,
                ToolName = i.ToolName,
                ArgSummary = i.ArgSummary,
                Output = i.Output,
                IsError = i.IsError,
                At = i.At,
            }).ToList(),
        };
        try
        {
            var path = FilePath(id);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, Json), TextUtil.Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a full disk must not take the chat down.
        }
    }

    private sealed class SessionFile
    {
        public LogSession? Session { get; set; }
        public List<StoredItem>? Items { get; set; }
    }

    private sealed class StoredItem
    {
        public int Seq { get; set; }
        public string Kind { get; set; } = "";
        public string? Text { get; set; }
        public string? ToolName { get; set; }
        public string? ArgSummary { get; set; }
        public string? Output { get; set; }
        public bool IsError { get; set; }
        public DateTimeOffset At { get; set; }
    }
}
