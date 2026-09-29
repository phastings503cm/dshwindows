using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsh.Core;

// MARK: - Task queue file format
//
// {"tasks": [{"id", "title", "details", "status", "enteredAt", "startedAt", "finishedAt", "rounds",
// "promptTokens", "completionTokens", "sessionId", "cwd", "log": [{"id", "at", "kind", "text"}]}]}
//
// Camel-case keys, statuses and log kinds as their lower-case names, ISO-8601 dates. Decoding is
// tolerant because a failed decode would cost the user their whole queue: a missing field (a file
// from an older or newer build) takes its default; a status this build doesn't know reads as blocked
// (it needs a person to look and must never be re-run unattended); an unknown log kind reads as a
// note; a log that can't be read is dropped rather than the task. What still fails is a file that
// isn't JSON of this shape at all, or a task without an id — TaskQueue sets such a file aside.

internal static class TaskQueueFile
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Encode(IEnumerable<QueueTask> tasks) => JsonSerializer.Serialize(new StoredQueue
    {
        Tasks = tasks.Select(t => new StoredTask
        {
            Id = t.Id,
            Title = t.Title,
            Details = t.Details,
            Status = t.Status.RawValue(),
            EnteredAt = t.EnteredAt,
            StartedAt = t.StartedAt,
            FinishedAt = t.FinishedAt,
            Rounds = t.Rounds,
            PromptTokens = t.PromptTokens,
            CompletionTokens = t.CompletionTokens,
            SessionId = t.SessionId,
            Cwd = t.Cwd,
            Log = JsonSerializer.SerializeToElement(
                t.Log.Select(l => new StoredLine { Id = l.Id, At = l.At, Kind = l.Kind.RawValue(), Text = l.Text }).ToList(), Json),
        }).ToList<StoredTask?>(),
    }, Json);

    /// <summary>The tasks in <paramref name="json"/>; <paramref name="now"/> stands in for a missing
    /// entry date. Throws <see cref="JsonException"/> when the file can't be used.</summary>
    public static List<QueueTask> Decode(string json, DateTimeOffset now)
    {
        var stored = JsonSerializer.Deserialize<StoredQueue>(json, Json);
        if (stored?.Tasks is not { } tasks) throw new JsonException("Not a task queue file.");
        return tasks.Select(t => t is null ? throw new JsonException("A task is null.") : new QueueTask
        {
            Id = t.Id ?? throw new JsonException("A task has no id."),
            Title = t.Title ?? "Untitled task",
            Details = t.Details ?? "",
            Status = t.Status is null ? QueueTaskStatus.Queued : QueueTaskStatuses.FromRaw(t.Status) ?? QueueTaskStatus.Blocked,
            EnteredAt = t.EnteredAt ?? now,
            StartedAt = t.StartedAt,
            FinishedAt = t.FinishedAt,
            Rounds = t.Rounds ?? 0,
            PromptTokens = t.PromptTokens ?? 0,
            CompletionTokens = t.CompletionTokens ?? 0,
            SessionId = t.SessionId,
            Cwd = t.Cwd,
            Log = DecodeLog(t.Log),
        }).ToList();
    }

    /// <summary>A task's log, or none when any line of it can't be read.</summary>
    private static IReadOnlyList<QueueLogLine> DecodeLog(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Array } array) return [];
        try
        {
            var lines = new List<QueueLogLine>();
            foreach (var line in array.Deserialize<List<StoredLine?>>(Json) ?? [])
            {
                if (line is not { Id: { } id, At: { } at, Kind: { } kind, Text: { } text }) return [];
                lines.Add(new QueueLogLine(at, QueueLogKinds.FromRaw(kind) ?? QueueLogKind.Note, text) { Id = id });
            }
            return lines;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private sealed class StoredQueue
    {
        public List<StoredTask?>? Tasks { get; set; }
    }

    private sealed class StoredTask
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Details { get; set; }
        public string? Status { get; set; }
        public DateTimeOffset? EnteredAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public int? Rounds { get; set; }
        public long? PromptTokens { get; set; }
        public long? CompletionTokens { get; set; }
        public string? SessionId { get; set; }
        public string? Cwd { get; set; }
        public JsonElement? Log { get; set; }
    }

    private sealed class StoredLine
    {
        public Guid? Id { get; set; }
        public DateTimeOffset? At { get; set; }
        public string? Kind { get; set; }
        public string? Text { get; set; }
    }
}
