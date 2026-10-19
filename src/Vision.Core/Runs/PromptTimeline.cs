using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - The Prompts timeline
//
// Every prompt a chat has sent, with how it ended: Completed with results, failed, stopped, still running, queued. One
// record per prompt the person sent (typed, a /goal, a skill, a task from the chat's task list) — never the harness's own
// rows ("↻ round 3: keep going", "🤖 Background agents finished"). Kept per chat in prompt-timeline\<chatId>.json next to
// the conversations, so the history survives a relaunch; a chat from before the timeline existed is backfilled once from
// its transcript.
//
// The same records, words and file as Vision for macOS (PromptTimeline.swift): {"schema":"vision.prompts/1","chatID",
// "prompts":[…]}, ISO-8601 dates with milliseconds, keys as the Mac writes them (chatID, turnID).

/// <summary>Where a prompt came from. Stored as typed, goal, task, queue, skill.</summary>
public enum PromptSource
{
    /// <summary>Typed and sent in the chat.</summary>
    Typed,
    /// <summary><c>/goal &lt;objective&gt;</c>.</summary>
    Goal,
    /// <summary>A task of the chat's task list (on Windows every task list is a chat's own).</summary>
    Task,
    /// <summary>A task of the Mac's unattended queue (Windows reads it, never writes it).</summary>
    Queue,
    /// <summary><c>/name args</c>: a skill run by name.</summary>
    Skill,
}

/// <summary>How a prompt ended, or where it is now. Stored as queued, running, completed, issues, needsYou, failed, stopped,
/// unknown.</summary>
public enum PromptOutcome
{
    Queued,
    Running,
    Completed,
    /// <summary>Finished, but validation still found something wrong.</summary>
    Issues,
    /// <summary>A goal or task paused because the agent needs the person.</summary>
    NeedsYou,
    Failed,
    Stopped,
    /// <summary>A prompt from before the timeline existed whose ending the transcript doesn't say.</summary>
    Unknown,
}

public static class PromptKinds
{
    public static string RawValue(this PromptSource source) => source switch
    {
        PromptSource.Goal => "goal",
        PromptSource.Task => "task",
        PromptSource.Queue => "queue",
        PromptSource.Skill => "skill",
        _ => "typed",
    };

    public static PromptSource SourceFromRaw(string? raw) => raw switch
    {
        "goal" => PromptSource.Goal,
        "task" => PromptSource.Task,
        "queue" => PromptSource.Queue,
        "skill" => PromptSource.Skill,
        _ => PromptSource.Typed,
    };

    public static string Label(this PromptSource source) => source switch
    {
        PromptSource.Goal => "Goal",
        PromptSource.Task => "Task",
        PromptSource.Queue => "Queue task",
        PromptSource.Skill => "Skill",
        _ => "Prompt",
    };

    public static string RawValue(this PromptOutcome outcome) => outcome switch
    {
        PromptOutcome.Queued => "queued",
        PromptOutcome.Running => "running",
        PromptOutcome.Completed => "completed",
        PromptOutcome.Issues => "issues",
        PromptOutcome.NeedsYou => "needsYou",
        PromptOutcome.Failed => "failed",
        PromptOutcome.Stopped => "stopped",
        _ => "unknown",
    };

    public static PromptOutcome OutcomeFromRaw(string? raw) => raw switch
    {
        "queued" => PromptOutcome.Queued,
        "running" => PromptOutcome.Running,
        "completed" => PromptOutcome.Completed,
        "issues" => PromptOutcome.Issues,
        "needsYou" => PromptOutcome.NeedsYou,
        "failed" => PromptOutcome.Failed,
        "stopped" => PromptOutcome.Stopped,
        _ => PromptOutcome.Unknown,
    };

    /// <summary>Still going (or waiting to): no finish time, no duration yet.</summary>
    public static bool IsOpen(this PromptOutcome outcome) => outcome is PromptOutcome.Queued or PromptOutcome.Running;
}

/// <summary>One prompt the person sent, and how it went. Mutable: the host updates it as the prompt runs, on the UI
/// thread, and saves the chat's records after each change.</summary>
public sealed class PromptRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ChatId { get; set; } = "";
    /// <summary>What the person asked (a goal's objective, a task's prompt — without the harness's display prefixes).</summary>
    public string Text { get; set; } = "";
    public PromptSource Source { get; set; } = PromptSource.Typed;
    /// <summary>When it was sent (for a queued task: when it was added).</summary>
    public DateTimeOffset At { get; set; } = DateTimeOffset.Now;
    public PromptOutcome Outcome { get; set; } = PromptOutcome.Running;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>The estimate the planning phase stated, seconds.</summary>
    public double? EtaSeconds { get; set; }
    /// <summary>Validation attempts: 1 = it passed (or ended) the first time; each loop-back adds one.</summary>
    public int Attempts { get; set; } = 1;
    /// <summary>Goal rounds, for a goal or a task.</summary>
    public int? Rounds { get; set; }
    /// <summary>The estimator's task kind (the <see cref="TaskKind"/> raw value).</summary>
    public string? TaskKind { get; set; }
    /// <summary>The model id and the route (server) name the main agent ran it on.</summary>
    public string? Model { get; set; }
    public string? Route { get; set; }
    public long PromptTokens { get; set; }
    public long CompletionTokens { get; set; }
    public List<Evidence> Evidence { get; set; } = [];
    /// <summary>The results line ("Validated against the plan: 6 of 6 steps done.") and the model's closing summary.</summary>
    public string? CompletionNote { get; set; }
    /// <summary>Why it failed, stopped or needs the person.</summary>
    public string? Reason { get; set; }
    public string? TurnId { get; set; }
    /// <summary>The transcript row it produced, exactly as displayed ("🎯 /goal …"), and when — how "Jump" finds it again.</summary>
    public string? EntryText { get; set; }
    public DateTimeOffset? EntryAt { get; set; }
    /// <summary>Reconstructed from a transcript written before the timeline existed.</summary>
    public bool Backfilled { get; set; }

    public PromptRecord Clone()
    {
        var copy = (PromptRecord)MemberwiseClone();
        copy.Evidence = [.. Evidence];
        return copy;
    }

    /// <summary>Wall clock from sending to the end (null while it is still going).</summary>
    public double? DurationSeconds => FinishedAt is { } end ? Math.Max(0, (end - At).TotalSeconds) : null;

    /// <summary>The outcome as the timeline says it — the same words on every harness.</summary>
    public string Headline => Outcome switch
    {
        PromptOutcome.Queued => "Queued",
        PromptOutcome.Running => "Running",
        PromptOutcome.Completed => Evidence.Count == 0 ? "Completed" : "Completed with results",
        PromptOutcome.Issues => "Finished with issues",
        PromptOutcome.NeedsYou => "Needs you",
        PromptOutcome.Failed => "Failed",
        PromptOutcome.Stopped => "Stopped",
        _ => "Outcome not recorded",
    };

    /// <summary>The first non-blank line, for a one-line row.</summary>
    public string FirstLine
    {
        get
        {
            foreach (var line in Text.Split('\n'))
                if (line.Trim().Length > 0) return line.Trim();
            return Text.Trim();
        }
    }
}

// MARK: - Rules

public static class PromptTimeline
{
    /// <summary>The most records a chat keeps (the oldest go first).</summary>
    public const int MaxRecordsPerChat = 500;

    // MARK: Timing

    /// <summary>"5m 12s · est. about 4 min · 2 attempts" — the duration against the estimate; "running 1m 40s · est. …" while
    /// it runs.</summary>
    public static string Timing(PromptRecord record, DateTimeOffset now)
    {
        var parts = new List<string>();
        if (record.DurationSeconds is { } duration) parts.Add(duration.FormattedDuration());
        else if (record.Outcome == PromptOutcome.Running) parts.Add("running " + Math.Max(0, (now - record.At).TotalSeconds).FormattedDuration());
        if (record.EtaSeconds is { } eta) parts.Add("est. " + TurnEstimator.Phrase(eta));
        if (record.Attempts > 1) parts.Add($"{record.Attempts} attempts");
        if (record.Rounds is > 1 and var rounds) parts.Add($"{rounds} rounds");
        if (record.Evidence.Count > 0) parts.Add($"{record.Evidence.Count} evidence item{(record.Evidence.Count == 1 ? "" : "s")}");
        return string.Join(" · ", parts);
    }

    /// <summary>How the duration compares with the estimate ("on time", "2m over the estimate").</summary>
    public static string? VersusEstimate(PromptRecord record) => PhaseText.VersusEstimate(record.DurationSeconds, record.EtaSeconds);

    // MARK: Grouping and search

    public sealed record DayGroup(string Title, IReadOnlyList<PromptRecord> Records);

    /// <summary>"Today", "Yesterday", a weekday within the last week, else "Oct 12" (with the year when it isn't this
    /// year) — in <paramref name="zone"/>.</summary>
    public static string DayTitle(DateTimeOffset date, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var day = TimeZoneInfo.ConvertTime(date, zone).Date;
        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var days = (today - day).Days;
        if (days <= 0) return "Today";
        if (days == 1) return "Yesterday";
        var culture = CultureInfo.GetCultureInfo("en-US");
        if (days < 7) return day.ToString("dddd", culture);
        return day.ToString(day.Year == today.Year ? "MMM d" : "MMM d, yyyy", culture);
    }

    /// <summary>Newest first, under day headings.</summary>
    public static IReadOnlyList<DayGroup> Grouped(IEnumerable<PromptRecord> records, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var groups = new List<(string Title, List<PromptRecord> Records)>();
        foreach (var record in records.OrderByDescending(r => r.At).ThenBy(r => r.Id, StringComparer.Ordinal))
        {
            var title = DayTitle(record.At, now, zone);
            if (groups.Count > 0 && groups[^1].Title == title) groups[^1].Records.Add(record);
            else groups.Add((title, [record]));
        }
        return groups.Select(g => new DayGroup(g.Title, g.Records)).ToList();
    }

    /// <summary>The records whose text, notes, outcome, source, model or chat title contain every word of
    /// <paramref name="query"/> (any case, any order).</summary>
    public static IReadOnlyList<PromptRecord> Filter(IEnumerable<PromptRecord> records, string query, IReadOnlyDictionary<string, string>? chatTitles = null)
    {
        var words = query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return records.ToList();
        return records.Where(record =>
        {
            var haystack = string.Join("\n", record.Text, record.CompletionNote ?? "", record.Reason ?? "", record.Headline,
                record.Source.Label(), chatTitles?.GetValueOrDefault(record.ChatId) ?? "", record.Model ?? "").ToLowerInvariant();
            return words.All(w => haystack.Contains(w, StringComparison.Ordinal));
        }).ToList();
    }

    // MARK: Talking about an earlier prompt

    /// <summary>What "Add to composer" drops in: the prompt as a quote, with when it ran and how it ended, so the model can be
    /// asked about it with its results in view.</summary>
    public static string Quote(PromptRecord record, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var sent = TimeZoneInfo.ConvertTime(record.At, zone).ToString("MMM d 'at' HH:mm", CultureInfo.GetCultureInfo("en-US"));
        var head = $"About my earlier {(record.Source == PromptSource.Typed ? "prompt" : record.Source.Label().ToLowerInvariant())} (sent {sent} — {record.Headline}";
        if (record.DurationSeconds is { } duration) head += ", " + duration.FormattedDuration();
        head += "):";
        const int maxLines = 12, maxChars = 1_200;
        var lines = record.Text.Replace("\r\n", "\n").Split('\n').ToList();
        var truncated = false;
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            truncated = true;
        }
        var body = string.Join("\n", lines);
        if (body.Length > maxChars)
        {
            body = body[..maxChars];
            truncated = true;
        }
        var quoted = body.Split('\n').Select(l => "> " + l).ToList();
        if (truncated) quoted.Add("> …");
        var output = new StringBuilder(head).Append('\n').Append(string.Join("\n", quoted));
        var outcome = new List<string>();
        if (record.Evidence.Count > 0) outcome.Add(string.Join("; ", record.Evidence.Take(4).Select(e => e.Title)));
        if (record.CompletionNote?.Trim() is { Length: > 0 } note) outcome.Add(TextUtil.Prefix(note.Split('\n')[0], 240));
        if (record.Reason is { Length: > 0 } reason && record.Outcome != PromptOutcome.Completed) outcome.Add(reason);
        if (outcome.Count > 0) output.Append("\nOutcome: ").Append(string.Join(" · ", outcome));
        return output.Append("\n\n").ToString();
    }

    // MARK: Finding its row in the transcript

    /// <summary>A transcript row a record may point at.</summary>
    public sealed record EntryRef(string Id, string Text, DateTimeOffset At);

    /// <summary>The transcript row this prompt produced: the user row with the same displayed text closest in time, else the
    /// closest user row containing the prompt's first line. Null when the chat no longer has it.</summary>
    public static string? MatchEntry(PromptRecord record, IEnumerable<EntryRef> candidates)
    {
        var anchor = record.EntryAt ?? record.At;
        var list = candidates.ToList();
        var wanted = record.EntryText ?? record.Text;
        if (list.Where(c => c.Text == wanted).MinBy(c => Math.Abs((c.At - anchor).TotalSeconds)) is { } exact) return exact.Id;
        var first = record.FirstLine;
        if (first.Length == 0) return null;
        return list.Where(c => c.Text.Contains(first, StringComparison.Ordinal)).MinBy(c => Math.Abs((c.At - anchor).TotalSeconds))?.Id;
    }

    // MARK: Backfill from a transcript

    /// <summary>One stored transcript row, as the backfill reads it (kind: user, assistant, notice, error, tool, compaction).</summary>
    public sealed record Row(string Kind, string Text, DateTimeOffset At);

    /// <summary>The harness's own user rows — continuations that belong to the prompt before them.</summary>
    private static readonly string[] Continuations =
    [
        "↻ round", "↻ validation", "🤖 Background agents finished", "🎯 /goal (resuming)", "📋 Task (resuming)", "🚀 queue (resuming)",
    ];

    /// <summary>What a user row is: a prompt the person sent (its source and text), or null for one of the harness's own
    /// rows. The display strings are the harness's own — matched, never changed (resume reads them).</summary>
    public static (PromptSource Source, string Text)? Classify(string userRow)
    {
        if (Continuations.Any(c => userRow.StartsWith(c, StringComparison.Ordinal))) return null;
        if (userRow.StartsWith("📋 Task: ", StringComparison.Ordinal)) return (PromptSource.Task, userRow["📋 Task: ".Length..]);
        if (userRow.StartsWith("🚀 queue: ", StringComparison.Ordinal)) return (PromptSource.Queue, userRow["🚀 queue: ".Length..]);
        if (userRow.StartsWith("🎯 /goal ", StringComparison.Ordinal)) return (PromptSource.Goal, userRow["🎯 /goal ".Length..]);
        if (userRow.StartsWith('/')) return (PromptSource.Skill, userRow);
        return (PromptSource.Typed, userRow);
    }

    /// <summary>Records for a chat whose prompts were sent before the timeline existed: one per prompt row, its ending read
    /// from what followed it (a Stop notice, an error, a goal's ✅/⏸ line, an answer).</summary>
    public static List<PromptRecord> Backfill(string chatId, IEnumerable<Row> rows)
    {
        var records = new List<PromptRecord>();
        var spans = new List<List<Row>>();
        foreach (var row in rows)
        {
            if (row.Kind == "user" && Classify(row.Text) is var (source, text))
            {
                records.Add(new PromptRecord
                {
                    ChatId = chatId, Text = text, Source = source, At = row.At, Outcome = PromptOutcome.Unknown,
                    EntryText = row.Text, EntryAt = row.At, Backfilled = true,
                });
                spans.Add([]);
            }
            else if (spans.Count > 0)
            {
                spans[^1].Add(row);
            }
        }
        for (var i = 0; i < records.Count; i++)
        {
            var span = spans[i];
            var record = records[i];
            if (span.Count > 0) record.FinishedAt = span[^1].At > record.At ? span[^1].At : record.At;
            var notices = span.Where(r => r.Kind == "notice").Select(r => r.Text).ToList();
            var lastAnswer = span.LastOrDefault(r => r.Kind == "assistant")?.Text;
            if (notices.Any(n => n.StartsWith("Stopped", StringComparison.Ordinal)))
                record.Outcome = PromptOutcome.Stopped;
            else if (notices.Any(n => n.StartsWith("✅ Goal complete", StringComparison.Ordinal) || n.StartsWith("✅ Task complete", StringComparison.Ordinal)))
                record.Outcome = PromptOutcome.Completed;
            else if (span.Where(r => r.Kind is "notice" or "error").Select(r => r.Text)
                         .FirstOrDefault(n => n.StartsWith("⏸ Goal paused", StringComparison.Ordinal) || n.StartsWith("⏸ Task blocked", StringComparison.Ordinal)) is { } paused)
            {
                record.Outcome = PromptOutcome.NeedsYou;
                record.Reason = TextUtil.Prefix(paused, 300);
            }
            else if (span.LastOrDefault(r => r.Kind != "notice") is { Kind: "error" } failure)
            {
                record.Outcome = PromptOutcome.Failed;
                record.Reason = TextUtil.Prefix(failure.Text, 300);
            }
            else if (lastAnswer is not null)
            {
                record.Outcome = PromptOutcome.Completed;
            }
            if (lastAnswer is { Length: > 0 }) record.CompletionNote = TextUtil.Prefix(lastAnswer, 600);
            var rounds = span.Count(r => r.Kind == "user" && r.Text.StartsWith("↻ round", StringComparison.Ordinal)) + 1;
            record.Rounds = rounds == 1 ? null : rounds;
        }
        return records;
    }

    /// <summary>Keep the newest <see cref="MaxRecordsPerChat"/>, oldest first.</summary>
    public static List<PromptRecord> Capped(IEnumerable<PromptRecord> records)
    {
        var sorted = records.OrderBy(r => r.At).ToList();
        return sorted.Count > MaxRecordsPerChat ? sorted.Skip(sorted.Count - MaxRecordsPerChat).ToList() : sorted;
    }
}

// MARK: - Storage

/// <summary>The timeline files: <c>&lt;directory&gt;\&lt;chatId&gt;.json</c> = <c>{schema, chatID, prompts}</c>. Written
/// atomically; a file that can't be read is set aside (renamed <c>.unreadable-&lt;time&gt;.json</c>), never overwritten.</summary>
public sealed class PromptTimelineStore
{
    public const string Schema = "vision.prompts/1";
    public const string FolderName = "prompt-timeline";

    private readonly Lock _lock = new();

    public PromptTimelineStore(string directory) => Folder = directory;

    public string Folder { get; }

    public string FilePath(string chatId) => Path.Combine(Folder, SafeName(chatId) + ".json");

    /// <summary>A chat id as a file name: letters, digits, dash and underscore only.</summary>
    public static string SafeName(string chatId)
    {
        var cleaned = new string(chatId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return cleaned.Length == 0 ? "_" : cleaned;
    }

    /// <summary>The chat's records, oldest first; null when it has no timeline file yet (backfill it).</summary>
    public List<PromptRecord>? Load(string chatId)
    {
        lock (_lock)
        {
            var path = FilePath(chatId);
            string text;
            try
            {
                if (!File.Exists(path)) return null;
                text = File.ReadAllText(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (Decode(text) is { } records) return records.OrderBy(r => r.At).ToList();
            try
            {
                var aside = Path.Combine(Folder, $"{SafeName(chatId)}.unreadable-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.json");
                File.Move(path, aside);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Left where it is; the next save would replace it, so keep that from happening by returning an empty list
                // the caller backfills — the transcript still holds every prompt.
            }
            return null;
        }
    }

    public void Save(string chatId, IEnumerable<PromptRecord> records)
    {
        lock (_lock)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Folder);
                var path = FilePath(chatId);
                var temp = path + ".tmp";
                File.WriteAllText(temp, Encode(chatId, PromptTimeline.Capped(records)));
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The records stay in memory; the next change tries again.
            }
        }
    }

    public void Delete(string chatId)
    {
        lock (_lock)
        {
            try
            {
                File.Delete(FilePath(chatId));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Every chat that has a timeline file.</summary>
    public IReadOnlyList<string> ChatIds()
    {
        lock (_lock)
        {
            if (!System.IO.Directory.Exists(Folder)) return [];
            return System.IO.Directory.EnumerateFiles(Folder, "*.json")
                .Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.Contains(".unreadable-", StringComparison.Ordinal))
                .Select(n => n[..^5]).ToList();
        }
    }

    // MARK: Wire format (as the Mac writes it)

    private static string Iso(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Encode(string chatId, IEnumerable<PromptRecord> records)
    {
        var prompts = new JsonArray();
        foreach (var r in records) prompts.Add(EncodeRecord(r));
        var root = new JsonObject { ["chatID"] = chatId, ["prompts"] = prompts, ["schema"] = Schema };
        return root.ToJsonString();
    }

    public static JsonObject EncodeRecord(PromptRecord r)
    {
        // Sorted keys, nils omitted: what the Mac's encoder writes.
        var o = new SortedDictionary<string, JsonNode?>(StringComparer.Ordinal)
        {
            ["at"] = Iso(r.At),
            ["attempts"] = r.Attempts,
            ["backfilled"] = r.Backfilled,
            ["chatID"] = r.ChatId,
            ["completionTokens"] = r.CompletionTokens,
            ["evidence"] = new JsonArray(r.Evidence.Select(e => (JsonNode)new JsonObject
            {
                ["at"] = Iso(e.At), ["detail"] = e.Detail, ["id"] = e.Id, ["kind"] = e.Kind.RawValue(), ["title"] = e.Title,
            }).ToArray()),
            ["id"] = r.Id,
            ["outcome"] = r.Outcome.RawValue(),
            ["promptTokens"] = r.PromptTokens,
            ["source"] = r.Source.RawValue(),
            ["text"] = r.Text,
        };
        void Put(string key, JsonNode? value)
        {
            if (value is not null) o[key] = value;
        }
        Put("finishedAt", r.FinishedAt is { } f ? Iso(f) : null);
        Put("etaSeconds", r.EtaSeconds is { } eta ? eta : null);
        Put("rounds", r.Rounds is { } rounds ? rounds : null);
        Put("taskKind", r.TaskKind);
        Put("model", r.Model);
        Put("route", r.Route);
        Put("completionNote", r.CompletionNote);
        Put("reason", r.Reason);
        Put("turnID", r.TurnId);
        Put("entryText", r.EntryText);
        Put("entryAt", r.EntryAt is { } e2 ? Iso(e2) : null);
        var json = new JsonObject();
        foreach (var (key, value) in o) json[key] = value;
        return json;
    }

    /// <summary>The records in a timeline file, or null when it isn't one. Tolerant per field: a value of the wrong shape
    /// takes its default rather than losing the file.</summary>
    public static List<PromptRecord>? Decode(string text)
    {
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (root?["prompts"] is not JsonArray prompts) return null;
        var output = new List<PromptRecord>();
        foreach (var node in prompts)
        {
            if (node is not JsonObject o) continue;
            var record = new PromptRecord
            {
                Id = Str(o, "id") ?? Guid.NewGuid().ToString(),
                ChatId = Str(o, "chatID") ?? Str(o, "chatId") ?? "",
                Text = Str(o, "text") ?? "",
                Source = PromptKinds.SourceFromRaw(Str(o, "source")),
                At = Date(o, "at") ?? DateTimeOffset.Now,
                Outcome = PromptKinds.OutcomeFromRaw(Str(o, "outcome")),
                FinishedAt = Date(o, "finishedAt"),
                EtaSeconds = Num(o, "etaSeconds"),
                Attempts = (int?)Num(o, "attempts") ?? 1,
                Rounds = (int?)Num(o, "rounds"),
                TaskKind = Str(o, "taskKind"),
                Model = Str(o, "model"),
                Route = Str(o, "route"),
                PromptTokens = (long?)Num(o, "promptTokens") ?? 0,
                CompletionTokens = (long?)Num(o, "completionTokens") ?? 0,
                CompletionNote = Str(o, "completionNote"),
                Reason = Str(o, "reason"),
                TurnId = Str(o, "turnID") ?? Str(o, "turnId"),
                EntryText = Str(o, "entryText"),
                EntryAt = Date(o, "entryAt"),
                Backfilled = o["backfilled"] is JsonValue b && b.TryGetValue<bool>(out var flag) && flag,
            };
            if (o["evidence"] is JsonArray evidence)
            {
                foreach (var item in evidence.OfType<JsonObject>())
                {
                    record.Evidence.Add(new Evidence(EvidenceKinds.FromRaw(Str(item, "kind")), Str(item, "title") ?? "", Str(item, "detail") ?? "",
                        Date(item, "at") ?? record.At) { Id = Str(item, "id") ?? Guid.NewGuid().ToString() });
                }
            }
            output.Add(record);
        }
        return output;
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    private static DateTimeOffset? Date(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<string>(out var s))
            return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
        // Seconds since 1970, as another harness might write.
        if (v.TryGetValue<double>(out var seconds) && double.IsFinite(seconds)) return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        return null;
    }
}
